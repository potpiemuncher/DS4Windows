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
    /// Haptics-only mode uses the self-contained HID output report 0x36 carrying
    /// controller state plus 3 kHz stereo PCM for the voice-coil actuators. When
    /// listening audio is enabled, the same container also carries one
    /// Opus-encoded 48 kHz stereo frame (10 ms, 160 kbps CBR) routed to the
    /// controller's headphone jack or internal speaker.
    ///
    /// Protocol research credit: egormanga/SAxense (haptics stream) and
    /// awalol/DS5Dongle (audio container, Opus parameters, routing).
    /// </summary>
    public class DualSenseHapticsStreamer
    {
        // 0x36 self-contained haptics report (state + signed 3 kHz PCM)
        private const int HAPTICS_REPORT_SIZE = 398;
        private const byte HAPTICS_REPORT_ID = 0x36;
        private const byte HAPTICS_CONTROLLER_BUFFER = 0x20;

        // Packetized SetStateData command used to initialize the listening-audio amp.
        private const int STATE_SETUP_REPORT_SIZE = 142;
        private const byte STATE_SETUP_REPORT_ID = 0x32;

        // 0x36 haptics + one listening-audio frame
        private const int AUDIO_REPORT_SIZE = 398;
        private const byte AUDIO_REPORT_ID = 0x36;
        private const int OPUS_FRAME_BYTES = 200;      // CBR: 160 kbps * 10 ms / 8
        private const int OPUS_SAMPLES_PER_FRAME = 480; // one frame per report, per channel
        private const int AUDIO_SAMPLE_RATE = 48000;   // Opus codec rate

        // The controller consumes one 480-sample Opus frame per ~10.667 ms haptic
        // slot (audio is slaved to the 3 kHz haptics clock), so audio must be
        // delivered at 480 / 10.667 ms = 45000 samples/s or the stream overruns
        // and drops frames audibly. Same reason DS5Dongle resamples 512->480.
        private const int AUDIO_DELIVERY_RATE = 45000;

        /// <summary>
        /// One coherent set of buffer sizes for the whole pipeline. Bigger
        /// buffers survive congested links (2.4 GHz Wi-Fi, wireless headset
        /// dongles); smaller buffers cut end-to-end delay for game audio.
        /// </summary>
        private readonly struct LatencyProfile
        {
            public readonly byte ControllerBuffer;   // controller-side dejitter buffer [16,127]
            public readonly int OpusQueueDepth;      // local frame backlog (frames of ~21.3 ms/2)
            public readonly int OpusPrebufferFrames; // frames banked before playback starts
            public readonly double MaxCatchupMs;     // burst catch-up window before resync
            public readonly int AudioRingSamples;    // capture ring feeding the encoder
            public readonly int HapticsRingBytes;    // capture ring feeding the actuators
            public readonly int HapticsPrebufferBytes;

            public LatencyProfile(byte controllerBuffer, int queueDepth, int prebufferFrames,
                double maxCatchupMs, int audioRingSamples, int hapticsRingBytes, int hapticsPrebufferBytes)
            {
                ControllerBuffer = controllerBuffer;
                OpusQueueDepth = queueDepth;
                OpusPrebufferFrames = prebufferFrames;
                MaxCatchupMs = maxCatchupMs;
                AudioRingSamples = audioRingSamples;
                HapticsRingBytes = hapticsRingBytes;
                HapticsPrebufferBytes = hapticsPrebufferBytes;
            }
        }

        private static LatencyProfile GetLatencyProfile(DualSenseControllerOptions.AudioLatencyMode latencyMode)
        {
            switch (latencyMode)
            {
                case DualSenseControllerOptions.AudioLatencyMode.LowLatency:
                    // ~80-120 ms end to end; needs a clean link
                    return new LatencyProfile(32, 4, 2, 100.0, AUDIO_DELIVERY_RATE * 2 / 10, 960, 192);
                case DualSenseControllerOptions.AudioLatencyMode.Balanced:
                    // ~150-250 ms
                    return new LatencyProfile(64, 6, 3, 150.0, AUDIO_DELIVERY_RATE * 2 * 3 / 20, 1440, 384);
                case DualSenseControllerOptions.AudioLatencyMode.Smooth:
                default:
                    // ~300-400 ms; proven on congested 2.4 GHz environments
                    return new LatencyProfile(120, 10, 4, 250.0, AUDIO_DELIVERY_RATE * 2 / 5, 1920, 576);
            }
        }

        private const int SAMPLE_RATE = 3000;          // haptic PCM rate per channel
        private const int HAPTIC_CHUNK_BYTES = 64;     // 32 stereo frames
        private const double TICK_MS = 32 * 1000.0 / SAMPLE_RATE; // ~10.667 ms
        private const int MAX_CONSECUTIVE_WRITE_FAILURES = 50;

        // Known-good advanced-haptics state used by DS5Dongle-AutoHaptics.
        // UseRumbleNotHaptics (state byte 0 bit 1) is clear.
        private static readonly byte[] HAPTICS_STATE = new byte[63]
        {
            0xFD, 0xF7, 0x00, 0x00,
            0x7F, 0x64, 0xFF, 0x09, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A,
            0x07, 0x00, 0x00, 0x02, 0x01, 0x00, 0xFF, 0xD7, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };

        private const double HEAVY_FREQ_HZ = 62.0;
        private const double LIGHT_FREQ_HZ = 170.0;
        private const double ENVELOPE_ATTACK = 0.35;
        private const double ENVELOPE_RELEASE = 0.015;

        // Voice-coil haptics can reproduce rumble values that are too weak to
        // move a conventional eccentric motor. Some games continuously emit
        // those tiny values during stick movement, turning them into an
        // unintended buzz. Match the effective noise floor of a normal motor,
        // then rescale the remaining range so full-strength rumble stays full.
        internal const byte RUMBLE_SYNTH_DEADZONE = 16;

        private readonly DualSenseDevice device;
        private readonly HidDevice hidDevice;
        private readonly byte[] outputBTCrc32Head = new byte[] { 0xA2 };

        private readonly object stateLock = new object();
        private Thread streamThread;
        private CancellationTokenSource streamCancellation;
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
        private DualSenseControllerOptions.AudioLatencyMode latencyMode =
            DualSenseControllerOptions.AudioLatencyMode.Smooth;
        private LatencyProfile profile = GetLatencyProfile(DualSenseControllerOptions.AudioLatencyMode.Smooth);

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
            int newAudioVolume, DualSenseControllerOptions.AudioLatencyMode newLatencyMode)
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
                    newEndpointId == endpointId && newAudioEnabled == audioEnabled &&
                    newLatencyMode == latencyMode)
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
                latencyMode = newLatencyMode;
                profile = GetLatencyProfile(newLatencyMode);

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
            CancellationTokenSource cancellationSource = new CancellationTokenSource();
            streamCancellation = cancellationSource;
            running = true;
            streamThread = new Thread(() => StreamLoop(cancellationSource))
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
            CancellationTokenSource cancellationSource = streamCancellation;
            Thread thread = streamThread;

            running = false;
            cancellationSource?.Cancel();
            if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            {
                thread.Join(500);
            }

            if (ReferenceEquals(streamThread, thread))
            {
                streamThread = null;
            }

            if (ReferenceEquals(streamCancellation, cancellationSource))
            {
                streamCancellation = null;
            }
        }

        private void StreamLoop(CancellationTokenSource cancellationSource)
        {
            CancellationToken cancellationToken = cancellationSource.Token;
            bool captureForHaptics = mode == DualSenseControllerOptions.HapticsMode.SystemAudio ||
                                     mode == DualSenseControllerOptions.HapticsMode.Mix;
            bool useRumbleSynth = mode == DualSenseControllerOptions.HapticsMode.RumbleToHaptics ||
                                  mode == DualSenseControllerOptions.HapticsMode.Mix;
            bool needCapture = captureForHaptics || audioEnabled;

            SampleRing hapticsRing = captureForHaptics ? new SampleRing(profile.HapticsRingBytes) : null;
            ShortRing audioRing = audioEnabled ? new ShortRing(profile.AudioRingSamples) : null;
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
                    if (!SendAudioVolumeSetup(out int setupError))
                    {
                        AppLogger.LogToGui($"{device.MacAddress}: BT audio amplifier setup write failed " +
                            $"(Win32 error {setupError})", true);
                    }

                    opusEncoder = OpusCodecFactory.CreateEncoder(AUDIO_SAMPLE_RATE, 2,
                        OpusApplication.OPUS_APPLICATION_AUDIO);
                    opusEncoder.Bitrate = OPUS_FRAME_BYTES * 8 * 100;
                    opusEncoder.UseVBR = false;
                    opusEncoder.Complexity = 5;

                    pcmFrame = new short[OPUS_SAMPLES_PER_FRAME * 2];
                    silenceOpus = new byte[OPUS_FRAME_BYTES];
                    EncodeOpusFrame(opusEncoder, new short[OPUS_SAMPLES_PER_FRAME * 2], silenceOpus);
                    opusQueue = new OpusFrameQueue(profile.OpusQueueDepth, OPUS_FRAME_BYTES);
                }

                byte[] report = new byte[audioEnabled ? AUDIO_REPORT_SIZE : HAPTICS_REPORT_SIZE];
                byte[] chunk = new byte[HAPTIC_CHUNK_BYTES];
                byte[] opusA = new byte[OPUS_FRAME_BYTES];
                bool primed = false;
                bool audioPrimed = false;
                long audioUnderruns = 0;
                long lastUnderrunLogTick = 0;
                int consecutiveFailures = 0;
                int firstWriteError = 0;
                int lastWriteError = 0;
                long tick = 0;
                bool synthStreamWasActive = false;

                Stopwatch clock = Stopwatch.StartNew();
                double nextDeadlineMs = 0.0;

                while (!cancellationToken.IsCancellationRequested)
                {
                    nextDeadlineMs += TICK_MS;
                    double wait = nextDeadlineMs - clock.Elapsed.TotalMilliseconds;
                    if (wait > 2.0)
                    {
                        Thread.Sleep((int)(wait - 1.5));
                    }

                    while (!cancellationToken.IsCancellationRequested &&
                        clock.Elapsed.TotalMilliseconds < nextDeadlineMs)
                    {
                        Thread.SpinWait(80);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // If we fall behind (a stalled BT write, a scheduler hiccup),
                    // send back-to-back to catch up: the controller's dejitter
                    // buffer absorbs the burst. Only resync (accepting an audio
                    // gap) after a stall too large to catch up from.
                    if (clock.Elapsed.TotalMilliseconds - nextDeadlineMs > profile.MaxCatchupMs)
                    {
                        nextDeadlineMs = clock.Elapsed.TotalMilliseconds;
                    }

                    bool hapticsActive = FillHapticsChunk(chunk, hapticsRing,
                        useRumbleSynth, ref primed);

                    // A continuous stream of nominally silent haptic packets can
                    // leave the voice-coil actuators faintly energized. In the
                    // pure rumble-synth pipeline there is no audio clock to
                    // maintain, so send one final centered chunk after an effect
                    // and stop writing until a real rumble signal arrives.
                    bool idleRumbleSynth = !audioEnabled && !captureForHaptics &&
                        useRumbleSynth && !hapticsActive;
                    if (idleRumbleSynth && !synthStreamWasActive)
                    {
                        tick++;
                        continue;
                    }

                    synthStreamWasActive = hapticsActive;

                    if (audioEnabled)
                    {
                        // Encode any pending captured audio into Opus frames.
                        while (audioRing.ReadExact(pcmFrame))
                        {
                            byte[] frame = opusQueue.RentSlot();
                            EncodeOpusFrame(opusEncoder, pcmFrame, frame);
                            opusQueue.CommitSlot();
                        }

                        // One self-contained 0x36 report per haptic clock slot.
                        // Each carries one 64-byte haptic chunk and one Opus frame.
                        if (!audioPrimed && opusQueue.Count >= profile.OpusPrebufferFrames)
                        {
                            audioPrimed = true;
                        }

                        bool gotAudio = audioPrimed && opusQueue.TryDequeue(opusA);
                        if (!gotAudio)
                        {
                            Buffer.BlockCopy(silenceOpus, 0, opusA, 0, OPUS_FRAME_BYTES);
                        }

                        if (audioPrimed && !gotAudio)
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

                        BuildAudioReport(report, chunk, opusA);
                    }
                    else
                    {
                        BuildHapticsReport(report, chunk);
                    }

                    if (hidDevice.WriteOutputReportViaInterrupt(report, 100, out int writeError))
                    {
                        consecutiveFailures = 0;
                        firstWriteError = 0;
                        lastWriteError = 0;
                    }
                    else
                    {
                        if (consecutiveFailures == 0)
                        {
                            firstWriteError = writeError;
                        }

                        lastWriteError = writeError;
                        if (++consecutiveFailures >= MAX_CONSECUTIVE_WRITE_FAILURES)
                        {
                            AppLogger.LogToGui($"{device.MacAddress}: BT haptics/audio stream aborted after " +
                                $"{consecutiveFailures} consecutive write failures " +
                                $"(Win32 first={firstWriteError}, last={lastWriteError}, " +
                                $"report=0x{report[0]:X2}, bytes={report.Length})", true);
                            break;
                        }
                    }

                    tick++;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui($"{device.MacAddress}: BT haptics/audio stream error: {ex.Message}", true);
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

                lock (stateLock)
                {
                    // An older generation may finish after Configure has already
                    // started its replacement. Only the generation still published
                    // as current may clear the shared active state.
                    if (ReferenceEquals(streamCancellation, cancellationSource))
                    {
                        running = false;
                        streamThread = null;
                        streamCancellation = null;
                    }
                }

                cancellationSource.Dispose();
            }
        }

        /// <summary>
        /// Fills one 64-byte haptic chunk (u8 offset-binary, silence = 0x80)
        /// from the capture ring and/or the rumble synth.
        /// </summary>
        private bool FillHapticsChunk(byte[] chunk, SampleRing ring, bool useRumbleSynth, ref bool primed)
        {
            int captured = 0;
            if (ring != null)
            {
                if (!primed && ring.Count >= profile.HapticsPrebufferBytes)
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
                bool pureRumbleSynth = ring == null;
                byte rawHeavy = device.CurrentRumbleHeavy;
                byte rawLight = device.CurrentRumbleLight;
                double heavyTarget = ScaleRumbleStrength(rawHeavy);
                double lightTarget = ScaleRumbleStrength(rawLight);
                double heavyInc = 2.0 * Math.PI * HEAVY_FREQ_HZ / SAMPLE_RATE;
                double lightInc = 2.0 * Math.PI * LIGHT_FREQ_HZ / SAMPLE_RATE;
                for (int i = 0; i < chunk.Length / 2; i++)
                {
                    heavyEnv += (heavyTarget - heavyEnv) *
                        (heavyTarget > heavyEnv ? ENVELOPE_ATTACK : ENVELOPE_RELEASE);
                    lightEnv += (lightTarget - lightEnv) *
                        (lightTarget > lightEnv ? ENVELOPE_ATTACK : ENVELOPE_RELEASE);

                    double rumbleLeft = heavyEnv * Math.Sin(heavyPhase);
                    double rumbleRight = lightEnv * Math.Sin(lightPhase);
                    double left = (chunk[i * 2] - 128) / 127.0 + rumbleLeft;
                    double right = (chunk[i * 2 + 1] - 128) / 127.0 + rumbleRight;
                    heavyPhase += heavyInc;
                    lightPhase += lightInc;

                    if (pureRumbleSynth)
                    {
                        // A full XInput motor command should span the actuator's
                        // full signed PCM range. The generic soft clipper maps a
                        // unit peak to only 50%, making game rumble unnecessarily weak.
                        chunk[i * 2] = UnitSampleToU8(rumbleLeft);
                        chunk[i * 2 + 1] = UnitSampleToU8(rumbleRight);
                    }
                    else
                    {
                        // Mix mode can contain both captured PCM and synthesized
                        // rumble, so use a smooth limiter to avoid hard clipping.
                        chunk[i * 2] = TanhSampleToU8(left * 1.5);
                        chunk[i * 2 + 1] = TanhSampleToU8(right * 1.5);
                    }
                }
            }

            return HasHapticSignal(chunk);
        }

        internal static double ScaleRumbleStrength(byte strength)
        {
            if (strength <= RUMBLE_SYNTH_DEADZONE)
            {
                return 0.0;
            }

            return (strength - RUMBLE_SYNTH_DEADZONE) /
                (double)(byte.MaxValue - RUMBLE_SYNTH_DEADZONE);
        }

        internal static bool HasHapticSignal(byte[] chunk)
        {
            for (int i = 0; i < chunk.Length; i++)
            {
                if (chunk[i] != 0x80)
                {
                    return true;
                }
            }

            return false;
        }

        internal static byte UnitSampleToU8(double sample)
        {
            return (byte)Math.Clamp(128.0 + sample * 127.0, 1.0, 255.0);
        }

        private static byte TanhSampleToU8(double sample)
        {
            return (byte)Math.Clamp(128.0 + Math.Tanh(sample) * 127.0, 1.0, 255.0);
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
        /// Self-contained report 0x36 layout (398 bytes), per DS5Dongle-AutoHaptics:
        /// config packet 0x11, SetStateData packet 0x10, then one signed 64-byte
        /// haptic PCM packet 0x12. Embedding state in every report is required on
        /// the Windows Bluetooth HID path used by this controller.
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
            report[5] = HAPTICS_CONTROLLER_BUFFER;
            report[6] = HAPTICS_CONTROLLER_BUFFER;
            report[7] = HAPTICS_CONTROLLER_BUFFER;
            report[8] = HAPTICS_CONTROLLER_BUFFER;
            report[9] = HAPTICS_CONTROLLER_BUFFER;
            report[10] = ++packetCounter;

            report[11] = 0x90; // SetStateData packet: PID 0x10 | sized
            report[12] = (byte)HAPTICS_STATE.Length;
            Buffer.BlockCopy(HAPTICS_STATE, 0, report, 13, HAPTICS_STATE.Length);

            report[76] = 0x92; // haptic audio packet: PID 0x12 | sized
            report[77] = HAPTIC_CHUNK_BYTES;
            for (int i = 0; i < HAPTIC_CHUNK_BYTES; i++)
            {
                // The internal ring is u8 offset-binary; report 0x36 carries s8 PCM.
                report[78 + i] = (byte)(chunk[i] ^ 0x80);
            }

            ApplyCrc(report, HAPTICS_REPORT_SIZE);
        }

        /// <summary>
        /// Self-contained report 0x36 with listening audio, per
        /// DS5Dongle-AutoHaptics: config, SetStateData, one signed haptic chunk,
        /// and one 200-byte Opus speaker/headphone packet.
        /// </summary>
        private void BuildAudioReport(byte[] report, byte[] chunk, byte[] opus)
        {
            Array.Clear(report, 0, AUDIO_REPORT_SIZE);
            report[0] = AUDIO_REPORT_ID;
            report[1] = (byte)((seq & 0x0F) << 4);
            seq = (byte)((seq + 1) & 0x0F);

            report[2] = 0x91; // config packet: PID 0x11 | sized
            report[3] = 0x07;
            report[4] = 0xFE;
            report[5] = HAPTICS_CONTROLLER_BUFFER;
            report[6] = HAPTICS_CONTROLLER_BUFFER;
            report[7] = HAPTICS_CONTROLLER_BUFFER;
            report[8] = HAPTICS_CONTROLLER_BUFFER;
            report[9] = HAPTICS_CONTROLLER_BUFFER;
            report[10] = ++packetCounter;

            report[11] = 0x90; // SetStateData packet: PID 0x10 | sized
            report[12] = (byte)HAPTICS_STATE.Length;
            Buffer.BlockCopy(HAPTICS_STATE, 0, report, 13, HAPTICS_STATE.Length);

            report[76] = 0x92; // haptic packet: PID 0x12 | sized
            report[77] = HAPTIC_CHUNK_BYTES;
            for (int i = 0; i < HAPTIC_CHUNK_BYTES; i++)
            {
                report[78 + i] = (byte)(chunk[i] ^ 0x80);
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

            report[142] = (byte)((headphone ? 0x16 : 0x13) | 0x80);
            report[143] = OPUS_FRAME_BYTES;
            Buffer.BlockCopy(opus, 0, report, 144, OPUS_FRAME_BYTES);

            ApplyCrc(report, AUDIO_REPORT_SIZE);
        }

        /// <summary>
        /// Sends a SetStateData container packet (PID 0x10 inside a 0x32 report)
        /// that unmutes the controller's audio amp: headphone/speaker volume 100
        /// with a mild speaker pre-gain boost. Mirrors what DS5Dongle emits when
        /// the USB host sets its volume; without this the Opus stream is silent.
        /// </summary>
        private bool SendAudioVolumeSetup(out int win32Error)
        {
            byte[] pkt = new byte[STATE_SETUP_REPORT_SIZE];
            pkt[0] = STATE_SETUP_REPORT_ID;
            // SetStateData uses a fixed command byte here, not the rolling
            // report sequence used by 0x11/0x12 audio containers. Sending a
            // sequence nibble causes the controller to ignore the amplifier
            // unmute/volume state while still accepting subsequent audio writes.
            pkt[1] = 0x10;

            pkt[2] = 0x90; // SetStateData packet: PID 0x10 | sized
            pkt[3] = 0x3F;

            // SetStateData payload starts at pkt[4] (offsets per Nielk1's layout)
            pkt[4] = 0xB0;      // AllowHeadphoneVolume | AllowSpeakerVolume | AllowAudioControl
            pkt[5] = 0x80;      // AllowAudioControl2
            pkt[4 + 4] = 0x64;  // VolumeHeadphones (max 0x7F)
            pkt[4 + 5] = 0x64;  // VolumeSpeaker (PS5 uses 0x3D..0x64)
            pkt[4 + 7] = 0x00;  // AudioControl: mic auto, default output path
            pkt[4 + 37] = 0x02; // AudioControl2: SpeakerCompPreGain = 2

            ApplyCrc(pkt, STATE_SETUP_REPORT_SIZE);
            return hidDevice.WriteOutputReportViaInterrupt(pkt, 100, out win32Error);
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
                        resampler.ResamplePrepare(frames, 2, out inBuffer, out inOffset);
                        for (int i = 0; i < frames; i++)
                        {
                            DownmixToStereo(samples, i * inChannels, inChannels,
                                out float left, out float right);
                            inBuffer[inOffset + i * 2] = left;
                            inBuffer[inOffset + i * 2 + 1] = right;
                        }

                        int outFrames = resampler.ResampleOut(resampleOut, 0, frames, resampleOut.Length / 2, 2);
                        audioRing.Write(resampleOut, outFrames * 2, volumeScale);
                    }

                    if (hapticsRing != null)
                    {
                        for (int i = 0; i < frames; i++)
                        {
                            DownmixToStereo(samples, i * inChannels, inChannels,
                                out float left, out float right);
                            double l = lpfL.Process(left);
                            double r = lpfR.Process(right);

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

        /// <summary>
        /// Downmixes common mono, stereo, quad, 5.0, 5.1, and 7.1 channel orders
        /// to stereo. Center is shared at -3 dB, LFE at -6 dB, and surround
        /// channels at -3 dB. Multichannel sums use a soft limiter so loud scenes
        /// cannot hard-clip before Opus encoding.
        /// </summary>
        internal static void DownmixToStereo(ReadOnlySpan<float> samples, int offset,
            int channels, out float left, out float right)
        {
            if (channels <= 0 || offset < 0 || offset + channels > samples.Length)
            {
                left = 0.0f;
                right = 0.0f;
                return;
            }

            float frontLeft = samples[offset];
            if (channels == 1)
            {
                left = frontLeft;
                right = frontLeft;
                return;
            }

            float frontRight = samples[offset + 1];
            if (channels == 2)
            {
                left = frontLeft;
                right = frontRight;
                return;
            }

            const float centerWeight = 0.70710678f;
            const float surroundWeight = 0.70710678f;
            const float lfeWeight = 0.5f;

            float mixedLeft = frontLeft;
            float mixedRight = frontRight;
            if (channels == 3)
            {
                float center = samples[offset + 2] * centerWeight;
                mixedLeft += center;
                mixedRight += center;
            }
            else if (channels == 4)
            {
                mixedLeft += samples[offset + 2] * surroundWeight;
                mixedRight += samples[offset + 3] * surroundWeight;
            }
            else if (channels == 5)
            {
                float center = samples[offset + 2] * centerWeight;
                mixedLeft += center + samples[offset + 3] * surroundWeight;
                mixedRight += center + samples[offset + 4] * surroundWeight;
            }
            else
            {
                // Standard WAVEFORMATEXTENSIBLE 5.1/7.1 order:
                // FL, FR, FC, LFE, BL, BR, [SL, SR].
                float center = samples[offset + 2] * centerWeight;
                float lfe = samples[offset + 3] * lfeWeight;
                mixedLeft += center + lfe + samples[offset + 4] * surroundWeight;
                mixedRight += center + lfe + samples[offset + 5] * surroundWeight;
                if (channels >= 8)
                {
                    mixedLeft += samples[offset + 6] * surroundWeight;
                    mixedRight += samples[offset + 7] * surroundWeight;
                }
            }

            left = MathF.Tanh(mixedLeft);
            right = MathF.Tanh(mixedRight);
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
