/*
DS4Windows
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Streams haptic and listening audio to a Bluetooth-connected DualSense.
    ///
    /// Haptics-only mode uses HID output report 0x32 carrying 3 kHz stereo PCM
    /// for the voice-coil actuators. When listening audio is enabled, the
    /// larger 0x39 container is used instead, carrying both the haptic PCM and
    /// Opus-encoded 48 kHz stereo audio (10 ms frames, 160 kbps CBR) routed to
    /// the controller's headphone jack or internal speaker.
    ///
    /// Protocol research credit: egormanga/SAxense (haptics stream) and
    /// awalol/DS5Dongle (audio container, Opus parameters, routing).
    /// </summary>
    public class DualSenseHapticsStreamer
    {
        // 0x32 haptics-only report
        private const int HAPTICS_REPORT_SIZE = 142;
        private const byte HAPTICS_REPORT_ID = 0x32;

        // 0x39 haptics + listening audio container
        private const int AUDIO_REPORT_SIZE = 547;
        private const byte AUDIO_REPORT_ID = 0x39;
        private const int OPUS_FRAME_BYTES = 200;      // CBR: 160 kbps * 10 ms / 8
        private const int OPUS_SAMPLES_PER_FRAME = 480; // one frame per report half, per channel
        private const int AUDIO_SAMPLE_RATE = 48000;   // Opus codec rate

        // The controller consumes one 480-sample Opus frame per ~10.667 ms haptic
        // slot (audio is slaved to the 3 kHz haptics clock), so audio must be
        // delivered at 480 / 10.667 ms = 45000 samples/s or the stream overruns
        // and drops frames audibly. Same reason DS5Dongle resamples 512->480.
        private const int AUDIO_DELIVERY_RATE = 45000;

        private const byte CONTROLLER_AUDIO_BUFFER = 120; // controller-side dejitter buffer [16,127]
        private const int OPUS_QUEUE_DEPTH = 10;          // ~213 ms of local frame backlog
        private const int OPUS_PREBUFFER_FRAMES = 4;      // frames banked before playback starts
        private const double MAX_CATCHUP_MS = 250.0;      // burst catch-up window before resync

        private const int SAMPLE_RATE = 3000;          // haptic PCM rate per channel
        private const int HAPTIC_CHUNK_BYTES = 64;     // 32 stereo frames
        private const double TICK_MS = 32 * 1000.0 / SAMPLE_RATE; // ~10.667 ms
        private const int RING_CAPACITY = 1920;
        private const int PREBUFFER_BYTES = 576;
        private const int MAX_CONSECUTIVE_WRITE_FAILURES = 50;

        private const double HEAVY_FREQ_HZ = 62.0;
        private const double LIGHT_FREQ_HZ = 170.0;
        private const double ENVELOPE_ATTACK = 0.35;
        private const double ENVELOPE_RELEASE = 0.015;

        private readonly DualSenseDevice device;
        private readonly HidDevice hidDevice;
        private readonly byte[] outputBTCrc32Head = new byte[] { 0xA2 };

        private readonly object stateLock = new object();
        private Thread streamThread;
        private volatile bool running;

        private DualSenseControllerOptions.HapticsMode mode =
            DualSenseControllerOptions.HapticsMode.Off;
        private double gain = 3.0;
        private int lowPassHz = 350;
        private string endpointId = string.Empty;
        private bool audioEnabled = false;
        private DualSenseControllerOptions.AudioOutputRoute audioRoute =
            DualSenseControllerOptions.AudioOutputRoute.Auto;
        private int audioVolume = 85;

        // Rumble-to-haptics synth state
        private double heavyEnv, lightEnv, heavyPhase, lightPhase;

        private byte seq;
        private byte packetCounter;

        public bool Active => running;

        public DualSenseHapticsStreamer(DualSenseDevice device, HidDevice hidDevice)
        {
            this.device = device;
            this.hidDevice = hidDevice;
        }

        public void Configure(DualSenseControllerOptions.HapticsMode newMode,
            double newGain, int newLowPassHz, string newEndpointId,
            bool newAudioEnabled, DualSenseControllerOptions.AudioOutputRoute newAudioRoute,
            int newAudioVolume)
        {
            lock (stateLock)
            {
                newGain = Math.Clamp(newGain, 0.1, 10.0);
                newLowPassHz = Math.Clamp(newLowPassHz, 40, 1000);
                newEndpointId ??= string.Empty;
                newAudioVolume = Math.Clamp(newAudioVolume, 0, 100);

                // Gain, volume, and routing apply live; anything that changes the
                // pipeline shape needs a restart.
                if (running && newMode == mode && newLowPassHz == lowPassHz &&
                    newEndpointId == endpointId && newAudioEnabled == audioEnabled)
                {
                    gain = newGain;
                    audioVolume = newAudioVolume;
                    audioRoute = newAudioRoute;
                    return;
                }

                bool wasRunning = running;
                StopLocked();

                mode = newMode;
                gain = newGain;
                lowPassHz = newLowPassHz;
                endpointId = newEndpointId;
                audioEnabled = newAudioEnabled;
                audioRoute = newAudioRoute;
                audioVolume = newAudioVolume;

                if (audioEnabled || mode != DualSenseControllerOptions.HapticsMode.Off)
                {
                    StartLocked();
                }
                else if (wasRunning)
                {
                    AppLogger.LogToGui($"{device.MacAddress}: BT haptics/audio streaming stopped", false);
                }
            }
        }

        public void Stop()
        {
            lock (stateLock)
            {
                StopLocked();
            }
        }

        private void StartLocked()
        {
            running = true;
            streamThread = new Thread(StreamLoop)
            {
                Priority = ThreadPriority.AboveNormal,
                IsBackground = true,
                Name = $"DualSense Haptics thread: {device.MacAddress}",
            };
            streamThread.Start();
            AppLogger.LogToGui($"{device.MacAddress}: BT streaming started " +
                $"(haptics: {mode}{(audioEnabled ? ", audio: on" : "")})", false);
        }

        private void StopLocked()
        {
            running = false;
            if (streamThread != null && streamThread.IsAlive &&
                streamThread != Thread.CurrentThread)
            {
                streamThread.Join(500);
            }

            streamThread = null;
        }

        private void StreamLoop()
        {
            bool captureForHaptics = mode == DualSenseControllerOptions.HapticsMode.SystemAudio ||
                                     mode == DualSenseControllerOptions.HapticsMode.Mix;
            bool useRumbleSynth = mode == DualSenseControllerOptions.HapticsMode.RumbleToHaptics ||
                                  mode == DualSenseControllerOptions.HapticsMode.Mix;
            bool needCapture = captureForHaptics || audioEnabled;

            SampleRing hapticsRing = captureForHaptics ? new SampleRing(RING_CAPACITY) : null;
            ShortRing audioRing = audioEnabled ? new ShortRing(AUDIO_SAMPLE_RATE * 2 / 5) : null; // 200 ms
            WasapiLoopbackCapture capture = null;
            IOpusEncoder opusEncoder = null;

            try
            {
                if (needCapture)
                {
                    capture = CreateCapture(hapticsRing, audioRing);
                    capture?.StartRecording();
                }

                byte[] silenceOpus = null;
                OpusFrameQueue opusQueue = null;
                short[] pcmFrame = null;
                if (audioEnabled)
                {
                    // The controller's audio amp defaults to muted volume; it only
                    // plays the stream after headphone/speaker volume is set.
                    SendAudioVolumeSetup();

                    opusEncoder = OpusCodecFactory.CreateEncoder(AUDIO_SAMPLE_RATE, 2,
                        OpusApplication.OPUS_APPLICATION_AUDIO);
                    opusEncoder.Bitrate = OPUS_FRAME_BYTES * 8 * 100;
                    opusEncoder.UseVBR = false;
                    opusEncoder.Complexity = 5;

                    pcmFrame = new short[OPUS_SAMPLES_PER_FRAME * 2];
                    silenceOpus = new byte[OPUS_FRAME_BYTES];
                    EncodeOpusFrame(opusEncoder, new short[OPUS_SAMPLES_PER_FRAME * 2], silenceOpus);
                    opusQueue = new OpusFrameQueue(OPUS_QUEUE_DEPTH, OPUS_FRAME_BYTES);
                }

                byte[] report = new byte[audioEnabled ? AUDIO_REPORT_SIZE : HAPTICS_REPORT_SIZE];
                byte[] chunkA = new byte[HAPTIC_CHUNK_BYTES];
                byte[] chunkB = new byte[HAPTIC_CHUNK_BYTES];
                byte[] opusA = new byte[OPUS_FRAME_BYTES];
                byte[] opusB = new byte[OPUS_FRAME_BYTES];
                bool primed = false;
                bool audioPrimed = false;
                long audioUnderruns = 0;
                long lastUnderrunLogTick = 0;
                int consecutiveFailures = 0;
                long tick = 0;

                Stopwatch clock = Stopwatch.StartNew();
                double nextDeadlineMs = 0.0;

                while (running)
                {
                    nextDeadlineMs += TICK_MS;
                    double wait = nextDeadlineMs - clock.Elapsed.TotalMilliseconds;
                    if (wait > 2.0)
                    {
                        Thread.Sleep((int)(wait - 1.5));
                    }

                    while (running && clock.Elapsed.TotalMilliseconds < nextDeadlineMs)
                    {
                        Thread.SpinWait(80);
                    }

                    if (!running)
                    {
                        break;
                    }

                    // If we fall behind (a stalled BT write, a scheduler hiccup),
                    // send back-to-back to catch up: the controller's dejitter
                    // buffer absorbs the burst. Only resync (accepting an audio
                    // gap) after a stall too large to catch up from.
                    if (clock.Elapsed.TotalMilliseconds - nextDeadlineMs > MAX_CATCHUP_MS)
                    {
                        nextDeadlineMs = clock.Elapsed.TotalMilliseconds;
                    }

                    byte[] chunk = (tick & 1) == 0 ? chunkA : chunkB;
                    FillHapticsChunk(chunk, hapticsRing, useRumbleSynth, ref primed);

                    bool sendNow;
                    if (audioEnabled)
                    {
                        // Encode any pending captured audio into Opus frames.
                        while (audioRing.ReadExact(pcmFrame))
                        {
                            byte[] frame = opusQueue.RentSlot();
                            EncodeOpusFrame(opusEncoder, pcmFrame, frame);
                            opusQueue.CommitSlot();
                        }

                        // One 0x39 container per two haptic chunks (~21.3 ms).
                        sendNow = (tick & 1) == 1;
                        if (sendNow)
                        {
                            // Bank a few frames before draining so transient
                            // capture gaps don't immediately become silence.
                            if (!audioPrimed && opusQueue.Count >= OPUS_PREBUFFER_FRAMES)
                            {
                                audioPrimed = true;
                            }

                            bool gotA = audioPrimed && opusQueue.TryDequeue(opusA);
                            bool gotB = audioPrimed && opusQueue.TryDequeue(opusB);
                            if (!gotA)
                            {
                                Buffer.BlockCopy(silenceOpus, 0, opusA, 0, OPUS_FRAME_BYTES);
                            }

                            if (!gotB)
                            {
                                Buffer.BlockCopy(silenceOpus, 0, opusB, 0, OPUS_FRAME_BYTES);
                            }

                            if (audioPrimed && !gotB)
                            {
                                audioPrimed = false; // ran dry: rebuffer before resuming
                                audioUnderruns++;
                            }

                            if (audioUnderruns > 0 && tick - lastUnderrunLogTick > 2800) // ~30 s
                            {
                                AppLogger.LogToGui($"{device.MacAddress}: BT audio underruns in last interval: {audioUnderruns}", false);
                                audioUnderruns = 0;
                                lastUnderrunLogTick = tick;
                            }

                            BuildAudioReport(report, chunkA, chunkB, opusA, opusB);
                        }
                    }
                    else
                    {
                        sendNow = true;
                        BuildHapticsReport(report, chunk);
                    }

                    if (!sendNow)
                    {
                        tick++;
                        continue;
                    }

                    if (hidDevice.WriteOutputReportViaInterrupt(report, 100))
                    {
                        consecutiveFailures = 0;
                    }
                    else if (++consecutiveFailures >= MAX_CONSECUTIVE_WRITE_FAILURES)
                    {
                        AppLogger.LogToGui($"{device.MacAddress}: BT haptics/audio stream aborted after repeated write failures", true);
                        running = false;
                    }

                    tick++;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui($"{device.MacAddress}: BT haptics/audio stream error: {ex.Message}", true);
                running = false;
            }
            finally
            {
                if (capture != null)
                {
                    try
                    {
                        capture.StopRecording();
                        capture.Dispose();
                    }
                    catch (Exception) { }
                }

                (opusEncoder as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// Fills one 64-byte haptic chunk (u8 offset-binary, silence = 0x80)
        /// from the capture ring and/or the rumble synth.
        /// </summary>
        private void FillHapticsChunk(byte[] chunk, SampleRing ring, bool useRumbleSynth, ref bool primed)
        {
            int captured = 0;
            if (ring != null)
            {
                if (!primed && ring.Count >= PREBUFFER_BYTES)
                {
                    primed = true;
                }

                captured = primed ? ring.ReadPartial(chunk) : 0;
                if (primed && captured == 0)
                {
                    primed = false;
                }
            }

            if (captured < chunk.Length)
            {
                Array.Fill(chunk, (byte)0x80, captured, chunk.Length - captured);
            }

            if (useRumbleSynth)
            {
                double heavyTarget = device.CurrentRumbleHeavy / 255.0;
                double lightTarget = device.CurrentRumbleLight / 255.0;
                double heavyInc = 2.0 * Math.PI * HEAVY_FREQ_HZ / SAMPLE_RATE;
                double lightInc = 2.0 * Math.PI * LIGHT_FREQ_HZ / SAMPLE_RATE;
                for (int i = 0; i < chunk.Length / 2; i++)
                {
                    heavyEnv += (heavyTarget - heavyEnv) *
                        (heavyTarget > heavyEnv ? ENVELOPE_ATTACK : ENVELOPE_RELEASE);
                    lightEnv += (lightTarget - lightEnv) *
                        (lightTarget > lightEnv ? ENVELOPE_ATTACK : ENVELOPE_RELEASE);

                    double left = (chunk[i * 2] - 128) / 127.0 + heavyEnv * Math.Sin(heavyPhase);
                    double right = (chunk[i * 2 + 1] - 128) / 127.0 + lightEnv * Math.Sin(lightPhase);
                    heavyPhase += heavyInc;
                    lightPhase += lightInc;

                    chunk[i * 2] = SoftClipToU8(left);
                    chunk[i * 2 + 1] = SoftClipToU8(right);
                }
            }
        }

        private void EncodeOpusFrame(IOpusEncoder encoder, short[] pcm, byte[] dest)
        {
            int written = encoder.Encode(pcm, OPUS_SAMPLES_PER_FRAME, dest, dest.Length);
            if (written < dest.Length && written > 0)
            {
                Array.Clear(dest, written, dest.Length - written);
            }
        }

        /// <summary>
        /// Report 0x32 layout (142 bytes): see Phase 1/2 research. Config packet
        /// 0x11 with the 0xFE haptics flags, one 64-byte audio packet 0x12.
        /// </summary>
        private void BuildHapticsReport(byte[] report, byte[] chunk)
        {
            Array.Clear(report, 0, HAPTICS_REPORT_SIZE);
            report[0] = HAPTICS_REPORT_ID;
            report[1] = (byte)((seq & 0x0F) << 4);
            seq = (byte)((seq + 1) & 0x0F);

            report[2] = 0x91; // config packet: PID 0x11 | sized
            report[3] = 0x07;
            report[4] = 0xFE;
            report[9] = 0xFF;
            report[10] = ++packetCounter;

            report[11] = 0x92; // haptic audio packet: PID 0x12 | sized
            report[12] = HAPTIC_CHUNK_BYTES;
            Buffer.BlockCopy(chunk, 0, report, 13, HAPTIC_CHUNK_BYTES);

            ApplyCrc(report, HAPTICS_REPORT_SIZE);
        }

        /// <summary>
        /// Report 0x39 layout (547 bytes), per DS5Dongle:
        ///   [0]=0x39, [1]=seq&lt;&lt;4
        ///   [2]=0x91, [3]=6, [4]=0x7E flags, [5..8]=audio buffer length (64),
        ///   [9]=frame counter (+2 per report)
        ///   [10]=0xD2, [11]=64, [12..139]=two 64-byte haptic chunks (signed PCM)
        ///   [140]=route PID (0x13 speaker / 0x16 headphone) | 0xC0, [141]=200,
        ///   [142..341]/[342..541]=two 200-byte Opus frames
        ///   [543..546]=CRC-32 over 0xA2 || first 543 bytes
        /// </summary>
        private void BuildAudioReport(byte[] report, byte[] chunkA, byte[] chunkB,
            byte[] opusA, byte[] opusB)
        {
            Array.Clear(report, 0, AUDIO_REPORT_SIZE);
            report[0] = AUDIO_REPORT_ID;
            report[1] = (byte)((seq & 0x0F) << 4);
            seq = (byte)((seq + 1) & 0x0F);

            report[2] = 0x91; // config packet: PID 0x11 | sized
            report[3] = 6;
            report[4] = 0x7E; // haptics + speaker session, no mic
            report[5] = CONTROLLER_AUDIO_BUFFER;
            report[6] = CONTROLLER_AUDIO_BUFFER;
            report[7] = CONTROLLER_AUDIO_BUFFER;
            report[8] = CONTROLLER_AUDIO_BUFFER; // controller-side audio buffer length
            packetCounter += 2;
            report[9] = packetCounter;

            report[10] = 0xD2; // haptic packet: PID 0x12 | 0xC0, two frames
            report[11] = HAPTIC_CHUNK_BYTES;
            for (int i = 0; i < HAPTIC_CHUNK_BYTES; i++)
            {
                // Ring stores u8 offset-binary; the 0x39 container carries signed PCM.
                report[12 + i] = (byte)(chunkA[i] ^ 0x80);
                report[12 + HAPTIC_CHUNK_BYTES + i] = (byte)(chunkB[i] ^ 0x80);
            }

            bool headphone;
            switch (audioRoute)
            {
                case DualSenseControllerOptions.AudioOutputRoute.Headphone:
                    headphone = true;
                    break;
                case DualSenseControllerOptions.AudioOutputRoute.Speaker:
                    headphone = false;
                    break;
                case DualSenseControllerOptions.AudioOutputRoute.Auto:
                default:
                    headphone = device.HeadsetPlugged;
                    break;
            }

            report[140] = (byte)((headphone ? 0x16 : 0x13) | 0xC0);
            report[141] = OPUS_FRAME_BYTES;
            Buffer.BlockCopy(opusA, 0, report, 142, OPUS_FRAME_BYTES);
            Buffer.BlockCopy(opusB, 0, report, 142 + OPUS_FRAME_BYTES, OPUS_FRAME_BYTES);

            ApplyCrc(report, AUDIO_REPORT_SIZE);
        }

        /// <summary>
        /// Sends a SetStateData container packet (PID 0x10 inside a 0x32 report)
        /// that unmutes the controller's audio amp: headphone/speaker volume 100
        /// with a mild speaker pre-gain boost. Mirrors what DS5Dongle emits when
        /// the USB host sets its volume; without this the Opus stream is silent.
        /// </summary>
        private void SendAudioVolumeSetup()
        {
            byte[] pkt = new byte[HAPTICS_REPORT_SIZE];
            pkt[0] = HAPTICS_REPORT_ID;
            pkt[1] = (byte)((seq & 0x0F) << 4);
            seq = (byte)((seq + 1) & 0x0F);

            pkt[2] = 0x90; // SetStateData packet: PID 0x10 | sized
            pkt[3] = 0x3F;

            // SetStateData payload starts at pkt[4] (offsets per Nielk1's layout)
            pkt[4] = 0xB0;      // AllowHeadphoneVolume | AllowSpeakerVolume | AllowAudioControl
            pkt[5] = 0x80;      // AllowAudioControl2
            pkt[4 + 4] = 0x64;  // VolumeHeadphones (max 0x7F)
            pkt[4 + 5] = 0x64;  // VolumeSpeaker (PS5 uses 0x3D..0x64)
            pkt[4 + 7] = 0x00;  // AudioControl: mic auto, default output path
            pkt[4 + 37] = 0x02; // AudioControl2: SpeakerCompPreGain = 2

            ApplyCrc(pkt, HAPTICS_REPORT_SIZE);
            hidDevice.WriteOutputReportViaInterrupt(pkt, 100);
        }

        private void ApplyCrc(byte[] report, int totalSize)
        {
            int crcOffset = totalSize - 4;
            uint calcCrc32 = ~Crc32Algorithm.Compute(outputBTCrc32Head);
            calcCrc32 = ~Crc32Algorithm.CalculateBasicHash(ref calcCrc32, ref report, 0, crcOffset);
            report[crcOffset] = (byte)calcCrc32;
            report[crcOffset + 1] = (byte)(calcCrc32 >> 8);
            report[crcOffset + 2] = (byte)(calcCrc32 >> 16);
            report[crcOffset + 3] = (byte)(calcCrc32 >> 24);
        }

        private WasapiLoopbackCapture CreateCapture(SampleRing hapticsRing, ShortRing audioRing)
        {
            WasapiLoopbackCapture capture = null;
            try
            {
                string endpointName = null;
                if (!string.IsNullOrEmpty(endpointId))
                {
                    using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                    try
                    {
                        MMDevice endpoint = enumerator.GetDevice(endpointId);
                        if (endpoint != null && endpoint.State == DeviceState.Active)
                        {
                            capture = new WasapiLoopbackCapture(endpoint);
                            endpointName = endpoint.FriendlyName;
                        }
                    }
                    catch (Exception)
                    {
                        AppLogger.LogToGui($"{device.MacAddress}: configured haptics audio device unavailable, using default output", true);
                    }
                }

                if (capture == null)
                {
                    capture = new WasapiLoopbackCapture();
                    try
                    {
                        using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                        endpointName = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).FriendlyName;
                    }
                    catch (Exception) { }
                }

                AppLogger.LogToGui($"{device.MacAddress}: capturing audio from \"{endpointName ?? "default output"}\"", false);

                int inRate = capture.WaveFormat.SampleRate;
                int inChannels = capture.WaveFormat.Channels;
                BiquadLowPass lpfL = hapticsRing != null ? new BiquadLowPass(lowPassHz, inRate) : null;
                BiquadLowPass lpfR = hapticsRing != null ? new BiquadLowPass(lowPassHz, inRate) : null;
                int decimPhase = 0;

                WdlResampler resampler = null;
                float[] resampleOut = null;
                if (audioRing != null)
                {
                    resampler = new WdlResampler();
                    resampler.SetMode(true, 2, false);
                    resampler.SetFilterParms();
                    resampler.SetFeedMode(true);
                    resampler.SetRates(inRate, AUDIO_DELIVERY_RATE);
                    resampleOut = new float[16384];
                }

                capture.DataAvailable += (sender, e) =>
                {
                    double captureGain = gain;
                    double volumeScale = audioVolume / 100.0;
                    ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(
                        e.Buffer.AsSpan(0, e.BytesRecorded));
                    int frames = samples.Length / inChannels;
                    if (frames <= 0)
                    {
                        return;
                    }

                    if (audioRing != null)
                    {
                        float[] inBuffer;
                        int inOffset;
                        int needed = resampler.ResamplePrepare(frames, 2, out inBuffer, out inOffset);
                        for (int i = 0; i < frames; i++)
                        {
                            inBuffer[inOffset + i * 2] = samples[i * inChannels];
                            inBuffer[inOffset + i * 2 + 1] =
                                inChannels > 1 ? samples[i * inChannels + 1] : samples[i * inChannels];
                        }

                        int outFrames = resampler.ResampleOut(resampleOut, 0, frames, resampleOut.Length / 2, 2);
                        audioRing.Write(resampleOut, outFrames * 2, volumeScale);
                    }

                    if (hapticsRing != null)
                    {
                        for (int i = 0; i < frames; i++)
                        {
                            double l = lpfL.Process(samples[i * inChannels]);
                            double r = lpfR.Process(inChannels > 1 ? samples[i * inChannels + 1] : samples[i * inChannels]);

                            decimPhase += SAMPLE_RATE;
                            if (decimPhase < inRate)
                            {
                                continue;
                            }

                            decimPhase -= inRate;
                            hapticsRing.Write(SoftClipToU8(l * captureGain), SoftClipToU8(r * captureGain));
                        }
                    }
                };

                return capture;
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui($"{device.MacAddress}: failed to open audio capture: {ex.Message}", true);
                capture?.Dispose();
                return null;
            }
        }

        private static byte SoftClipToU8(double x)
        {
            double y = x / (1.0 + Math.Abs(x));
            return (byte)Math.Clamp(128.0 + y * 127.0, 0, 255);
        }

        /// <summary>Byte ring for interleaved L/R u8 haptic samples with bounded latency.</summary>
        private sealed class SampleRing
        {
            private readonly byte[] buffer;
            private readonly object gate = new object();
            private int head;
            private int count;

            public int Count { get { lock (gate) return count; } }

            public SampleRing(int capacityBytes)
            {
                buffer = new byte[capacityBytes];
            }

            public void Write(byte left, byte right)
            {
                lock (gate)
                {
                    if (count > buffer.Length - 2)
                    {
                        head = (head + 2) % buffer.Length;
                        count -= 2;
                    }

                    int tail = (head + count) % buffer.Length;
                    buffer[tail] = left;
                    buffer[(tail + 1) % buffer.Length] = right;
                    count += 2;
                }
            }

            public int ReadPartial(byte[] dest)
            {
                lock (gate)
                {
                    int n = Math.Min(count, dest.Length) & ~1;
                    for (int i = 0; i < n; i++)
                    {
                        dest[i] = buffer[head];
                        head = (head + 1) % buffer.Length;
                    }

                    count -= n;
                    return n;
                }
            }
        }

        /// <summary>
        /// Ring of 16-bit interleaved stereo samples at 48 kHz feeding the Opus
        /// encoder. Drops oldest data when full to bound latency.
        /// </summary>
        private sealed class ShortRing
        {
            private readonly short[] buffer;
            private readonly object gate = new object();
            private int head;
            private int count;

            public ShortRing(int capacitySamples)
            {
                buffer = new short[capacitySamples];
            }

            public void Write(float[] samples, int sampleCount, double volumeScale)
            {
                lock (gate)
                {
                    for (int i = 0; i < sampleCount; i++)
                    {
                        if (count >= buffer.Length)
                        {
                            head = (head + 2) % buffer.Length;
                            count -= 2;
                        }

                        double v = samples[i] * volumeScale * 32767.0;
                        int tail = (head + count) % buffer.Length;
                        buffer[tail] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                        count++;
                    }
                }
            }

            /// <summary>Reads exactly dest.Length samples or returns false leaving state unchanged.</summary>
            public bool ReadExact(short[] dest)
            {
                lock (gate)
                {
                    if (count < dest.Length)
                    {
                        return false;
                    }

                    for (int i = 0; i < dest.Length; i++)
                    {
                        dest[i] = buffer[head];
                        head = (head + 1) % buffer.Length;
                    }

                    count -= dest.Length;
                    return true;
                }
            }
        }

        /// <summary>Small fixed-size queue of Opus frames with drop-oldest overflow.</summary>
        private sealed class OpusFrameQueue
        {
            private readonly byte[][] slots;
            private readonly object gate = new object();
            private int head;
            private int count;

            public int Count { get { lock (gate) return count; } }

            public OpusFrameQueue(int depth, int frameBytes)
            {
                slots = new byte[depth][];
                for (int i = 0; i < depth; i++)
                {
                    slots[i] = new byte[frameBytes];
                }
            }

            /// <summary>Returns the buffer to encode into; call CommitSlot afterwards.</summary>
            public byte[] RentSlot()
            {
                lock (gate)
                {
                    if (count >= slots.Length)
                    {
                        head = (head + 1) % slots.Length; // drop oldest
                        count--;
                    }

                    return slots[(head + count) % slots.Length];
                }
            }

            public void CommitSlot()
            {
                lock (gate)
                {
                    if (count < slots.Length)
                    {
                        count++;
                    }
                }
            }

            public bool TryDequeue(byte[] dest)
            {
                lock (gate)
                {
                    if (count == 0)
                    {
                        return false;
                    }

                    Buffer.BlockCopy(slots[head], 0, dest, 0, dest.Length);
                    head = (head + 1) % slots.Length;
                    count--;
                    return true;
                }
            }
        }

        /// <summary>2nd-order Butterworth low-pass (Q = 0.7071), direct form 1.</summary>
        private sealed class BiquadLowPass
        {
            private readonly double b0, b1, b2, a1, a2;
            private double x1, x2, y1, y2;

            public BiquadLowPass(double cutoffHz, double sampleRate)
            {
                double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
                double alpha = Math.Sin(w0) / (2.0 * 0.70710678);
                double cosW0 = Math.Cos(w0);
                double a0 = 1.0 + alpha;
                b0 = (1.0 - cosW0) / 2.0 / a0;
                b1 = (1.0 - cosW0) / a0;
                b2 = b0;
                a1 = -2.0 * cosW0 / a0;
                a2 = (1.0 - alpha) / a0;
            }

            public double Process(double x)
            {
                double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x;
                y2 = y1; y1 = y;
                return y;
            }
        }
    }
}
