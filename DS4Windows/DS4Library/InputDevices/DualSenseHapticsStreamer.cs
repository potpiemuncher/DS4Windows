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
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DS4Windows.InputDevices
{
    /// <summary>
    /// Streams haptic PCM audio (3 kHz, unsigned 8-bit, stereo L/R actuator) to a
    /// Bluetooth-connected DualSense using HID output report 0x32, enabling
    /// audio-driven haptic feedback without a USB cable.
    ///
    /// Wire protocol reverse engineered by egormanga's SAxense project
    /// (https://github.com/egormanga/SAxense). Windows requires the report to be
    /// written as exactly 142 bytes (report ID + 141 data bytes).
    /// </summary>
    public class DualSenseHapticsStreamer
    {
        private const int REPORT_SIZE = 142;
        private const int CRC_OFFSET = REPORT_SIZE - 4;
        private const int AUDIO_OFFSET = 13;
        private const int AUDIO_BYTES = 64;
        private const int SAMPLE_RATE = 3000;
        private const int FRAMES_PER_REPORT = AUDIO_BYTES / 2;
        private const double PERIOD_MS = FRAMES_PER_REPORT * 1000.0 / SAMPLE_RATE; // ~10.667 ms
        private const int RING_CAPACITY = 1920;   // ~320 ms of buffered samples
        private const int PREBUFFER_BYTES = 576;  // ~96 ms cushion before draining
        private const int MAX_CONSECUTIVE_WRITE_FAILURES = 50;

        // Rumble-to-haptics synthesis: the heavy (left) motor is voiced as deep
        // rumble, the light (right) motor as a higher buzz, with envelope
        // smoothing so steps in motor values do not click.
        private const double HEAVY_FREQ_HZ = 62.0;
        private const double LIGHT_FREQ_HZ = 170.0;
        private const double ENVELOPE_ATTACK = 0.35;  // per-sample smoothing coefficients
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

        public bool Active => running;

        public DualSenseHapticsStreamer(DualSenseDevice device, HidDevice hidDevice)
        {
            this.device = device;
            this.hidDevice = hidDevice;
        }

        public void Configure(DualSenseControllerOptions.HapticsMode newMode,
            double newGain, int newLowPassHz, string newEndpointId)
        {
            lock (stateLock)
            {
                newGain = Math.Clamp(newGain, 0.1, 10.0);
                newLowPassHz = Math.Clamp(newLowPassHz, 40, 1000);
                newEndpointId ??= string.Empty;

                // Gain applies live; everything else needs a pipeline restart.
                if (running && newMode == mode &&
                    newLowPassHz == lowPassHz && newEndpointId == endpointId)
                {
                    gain = newGain;
                    return;
                }

                bool wasRunning = running;
                StopLocked();

                mode = newMode;
                gain = newGain;
                lowPassHz = newLowPassHz;
                endpointId = newEndpointId;

                if (mode != DualSenseControllerOptions.HapticsMode.Off)
                {
                    StartLocked();
                }
                else if (wasRunning)
                {
                    AppLogger.LogToGui($"{device.MacAddress}: BT haptics streaming stopped", false);
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
            AppLogger.LogToGui($"{device.MacAddress}: BT haptics streaming started ({mode})", false);
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
            bool useCapture = mode == DualSenseControllerOptions.HapticsMode.SystemAudio ||
                              mode == DualSenseControllerOptions.HapticsMode.Mix;
            bool useRumbleSynth = mode == DualSenseControllerOptions.HapticsMode.RumbleToHaptics ||
                                  mode == DualSenseControllerOptions.HapticsMode.Mix;

            SampleRing ring = useCapture ? new SampleRing(RING_CAPACITY) : null;
            WasapiLoopbackCapture capture = null;

            try
            {
                if (useCapture)
                {
                    capture = CreateCapture(ring);
                    capture?.StartRecording();
                }

                byte[] report = new byte[REPORT_SIZE];
                byte[] audio = new byte[AUDIO_BYTES];
                byte seq = 0;
                byte counter = 0;
                bool primed = false;
                int consecutiveFailures = 0;
                double heavyEnv = 0.0, lightEnv = 0.0;
                double heavyPhase = 0.0, lightPhase = 0.0;
                double heavyInc = 2.0 * Math.PI * HEAVY_FREQ_HZ / SAMPLE_RATE;
                double lightInc = 2.0 * Math.PI * LIGHT_FREQ_HZ / SAMPLE_RATE;

                Stopwatch clock = Stopwatch.StartNew();
                double nextDeadlineMs = 0.0;

                while (running)
                {
                    nextDeadlineMs += PERIOD_MS;
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

                    // System stall: resync instead of bursting a backlog of reports.
                    if (clock.Elapsed.TotalMilliseconds - nextDeadlineMs > 100.0)
                    {
                        nextDeadlineMs = clock.Elapsed.TotalMilliseconds;
                    }

                    int captured = 0;
                    if (useCapture)
                    {
                        if (!primed && ring.Count >= PREBUFFER_BYTES)
                        {
                            primed = true;
                        }

                        captured = primed ? ring.ReadPartial(audio) : 0;
                        if (primed && captured == 0)
                        {
                            primed = false; // source dried up; rebuffer before draining again
                        }
                    }

                    if (captured < AUDIO_BYTES)
                    {
                        Array.Fill(audio, (byte)0x80, captured, AUDIO_BYTES - captured);
                    }

                    if (useRumbleSynth)
                    {
                        double heavyTarget = device.CurrentRumbleHeavy / 255.0;
                        double lightTarget = device.CurrentRumbleLight / 255.0;
                        for (int i = 0; i < FRAMES_PER_REPORT; i++)
                        {
                            heavyEnv += (heavyTarget - heavyEnv) *
                                (heavyTarget > heavyEnv ? ENVELOPE_ATTACK : ENVELOPE_RELEASE);
                            lightEnv += (lightTarget - lightEnv) *
                                (lightTarget > lightEnv ? ENVELOPE_ATTACK : ENVELOPE_RELEASE);

                            double left = (audio[i * 2] - 128) / 127.0 +
                                heavyEnv * Math.Sin(heavyPhase);
                            double right = (audio[i * 2 + 1] - 128) / 127.0 +
                                lightEnv * Math.Sin(lightPhase);
                            heavyPhase += heavyInc;
                            lightPhase += lightInc;

                            audio[i * 2] = SoftClipToU8(left);
                            audio[i * 2 + 1] = SoftClipToU8(right);
                        }
                    }

                    BuildReport(report, seq, counter, audio);
                    seq = (byte)((seq + 1) & 0x0F);
                    counter++;

                    if (hidDevice.WriteOutputReportViaInterrupt(report, 100))
                    {
                        consecutiveFailures = 0;
                    }
                    else if (++consecutiveFailures >= MAX_CONSECUTIVE_WRITE_FAILURES)
                    {
                        AppLogger.LogToGui($"{device.MacAddress}: BT haptics stream aborted after repeated write failures", true);
                        running = false;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui($"{device.MacAddress}: BT haptics stream error: {ex.Message}", true);
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
            }
        }

        private WasapiLoopbackCapture CreateCapture(SampleRing ring)
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

                AppLogger.LogToGui($"{device.MacAddress}: haptics capturing audio from \"{endpointName ?? "default output"}\"", false);

                int inRate = capture.WaveFormat.SampleRate;
                int inChannels = capture.WaveFormat.Channels;
                BiquadLowPass lpfL = new BiquadLowPass(lowPassHz, inRate);
                BiquadLowPass lpfR = new BiquadLowPass(lowPassHz, inRate);
                int decimPhase = 0;

                capture.DataAvailable += (sender, e) =>
                {
                    double captureGain = gain; // field read: live intensity changes
                    // WASAPI shared-mode loopback delivers 32-bit float frames.
                    ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(
                        e.Buffer.AsSpan(0, e.BytesRecorded));
                    for (int i = 0; i + inChannels <= samples.Length; i += inChannels)
                    {
                        double l = lpfL.Process(samples[i]);
                        double r = lpfR.Process(inChannels > 1 ? samples[i + 1] : samples[i]);

                        decimPhase += SAMPLE_RATE;
                        if (decimPhase < inRate)
                        {
                            continue;
                        }

                        decimPhase -= inRate;
                        ring.Write(SoftClipToU8(l * captureGain), SoftClipToU8(r * captureGain));
                    }
                };

                return capture;
            }
            catch (Exception ex)
            {
                AppLogger.LogToGui($"{device.MacAddress}: failed to open audio capture for haptics: {ex.Message}", true);
                capture?.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Report 0x32 layout (142 bytes total):
        ///   [0]=0x32, [1]=sequence&lt;&lt;4, [2..10]=config packet 0x11
        ///   (FE 00 00 00 00 FF counter), [11..76]=audio packet 0x12 with 64
        ///   PCM bytes, zero padding, CRC-32 over 0xA2 || first 138 bytes
        ///   stored little-endian in the last 4 bytes.
        /// </summary>
        private void BuildReport(byte[] report, byte seq, byte counter, byte[] audio)
        {
            Array.Clear(report, 0, REPORT_SIZE);
            report[0] = 0x32;
            report[1] = (byte)((seq & 0x0F) << 4);

            report[2] = 0x91; // config packet: PID 0x11 | sized flag 0x80
            report[3] = 0x07;
            report[4] = 0xFE;
            report[9] = 0xFF;
            report[10] = counter;

            report[11] = 0x92; // audio packet: PID 0x12 | sized flag 0x80
            report[12] = (byte)AUDIO_BYTES;
            Buffer.BlockCopy(audio, 0, report, AUDIO_OFFSET, AUDIO_BYTES);

            uint calcCrc32 = ~Crc32Algorithm.Compute(outputBTCrc32Head);
            calcCrc32 = ~Crc32Algorithm.CalculateBasicHash(ref calcCrc32, ref report, 0, CRC_OFFSET);
            report[CRC_OFFSET] = (byte)calcCrc32;
            report[CRC_OFFSET + 1] = (byte)(calcCrc32 >> 8);
            report[CRC_OFFSET + 2] = (byte)(calcCrc32 >> 16);
            report[CRC_OFFSET + 3] = (byte)(calcCrc32 >> 24);
        }

        private static byte SoftClipToU8(double x)
        {
            double y = x / (1.0 + Math.Abs(x));
            return (byte)Math.Clamp(128.0 + y * 127.0, 0, 255);
        }

        /// <summary>Byte ring for interleaved L/R u8 samples with bounded latency.</summary>
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
                    // Overwrite oldest when full: fresher haptics beat growing latency.
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
