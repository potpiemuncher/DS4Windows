/*
DSHapticsProto — DualSense Bluetooth haptics streaming prototype for DS4Windows.

Streams haptic PCM audio (3 kHz, signed 8-bit, stereo L/R actuator) to a
Bluetooth-connected DualSense / DualSense Edge using HID output report 0x32.

Protocol credit: reverse engineered by egormanga's SAxense project
(https://github.com/egormanga/SAxense, MPL-2.0). This is an independent C#
implementation of the documented wire format.

Usage:
  DSHapticsProto test [seconds=4] [freqHz=100] [amp=0.6]
      Tests the compact 0x32 format on both actuators.

  DSHapticsProto test36 [seconds=10] [freqHz=120] [amp=1.0]
      Plays a sine burst using the self-contained 0x36 format used on Windows.

  DSHapticsProto rumble [seconds=5]
      Plays compatibility rumble to verify the basic Bluetooth output path.

  DSHapticsProto capture [gain=3.0] [lpfHz=350]
      Captures system audio (WASAPI loopback), low-passes it, and streams it
      as haptics until Ctrl+C. Play a bass-heavy game/video and feel it.

  DSHapticsProto features
      Reads and prints the real controller's calibration, serial, and firmware
      feature reports without changing controller state.

  DSHapticsProto featuresusb
      Reads the same reports from the HID-only virtual USB DualSense.

  DSHapticsProto readusb [seconds=2]
      Reads virtual USB input reports and measures the interrupt-IN rate.

Run while the pad is connected over Bluetooth. Close DS4Windows/DSX/Steam
first so nothing else is holding or rewriting controller output state.
*/

using System.Diagnostics;
using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;
using NAudio.Wave;

namespace DSHapticsProto;

internal static class Program
{
    private const int SonyVid = 0x054C;
    private const int DualSensePid = 0x0CE6;
    private const int DualSenseEdgePid = 0x0DF2;

    private const int HapticsSampleRate = 3000;   // Hz, per channel
    private const int FramesPerReport = 32;       // 32 stereo frames = 64 bytes
    private const double ReportPeriodMs = FramesPerReport * 1000.0 / HapticsSampleRate; // ~10.667 ms

    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "test";

        HidDevice device = mode is "featuresusb" or "readusb"
            ? FindUsbDualSense()
            : FindBtDualSense();
        if (device == null)
        {
            Console.Error.WriteLine("No Bluetooth-connected DualSense found (VID 054C, PID 0CE6/0DF2 with large output reports).");
            return 1;
        }

        Console.WriteLine($"Using device: {device.DevicePath}");

        if (mode == "probe")
        {
            ResolveReport32Size(device, dumpAll: true);
            return 0;
        }

        using SafeFileHandle handle = NativeHid.OpenDevice(device.DevicePath);
        if (handle.IsInvalid)
        {
            Console.Error.WriteLine($"Failed to open device (Win32 error {Marshal.GetLastWin32Error()}). Is another app holding it exclusively?");
            return 1;
        }

        NativeHid.TimeBeginPeriod(1);
        try
        {
            switch (mode)
            {
                case "probewrite":
                {
                    RunWriteProbe(handle);
                    return 0;
                }
                case "test":
                {
                    double seconds = args.Length > 1 ? double.Parse(args[1]) : 4.0;
                    double freq = args.Length > 2 ? double.Parse(args[2]) : 100.0;
                    double amp = args.Length > 3 ? double.Parse(args[3]) : 0.6;
                    if (args.Length > 4) HapticReport.TotalSize = int.Parse(args[4]);
                    SendHapticsModeSetup(handle);
                    Console.WriteLine($"Sine test: {seconds:0.#} s @ {freq:0.#} Hz, amplitude {amp:0.##}, report size {HapticReport.TotalSize}");
                    RunSineTest(handle, seconds, freq, amp);
                    return 0;
                }
                case "test36":
                {
                    double seconds = args.Length > 1 ? double.Parse(args[1]) : 10.0;
                    double freq = args.Length > 2 ? double.Parse(args[2]) : 120.0;
                    double amp = args.Length > 3 ? double.Parse(args[3]) : 1.0;
                    Console.WriteLine($"Self-contained 0x36 sine test: {seconds:0.#} s @ {freq:0.#} Hz, amplitude {amp:0.##}");
                    RunSineTest36(handle, seconds, freq, amp);
                    return 0;
                }
                case "rumble":
                {
                    double seconds = args.Length > 1 ? double.Parse(args[1]) : 5.0;
                    Console.WriteLine($"Compatibility rumble test: {seconds:0.#} s at full strength");
                    RunCompatibilityRumbleTest(handle, seconds);
                    return 0;
                }
                case "capture":
                {
                    double gain = args.Length > 1 ? double.Parse(args[1]) : 3.0;
                    double lpfHz = args.Length > 2 ? double.Parse(args[2]) : 350.0;
                    SendHapticsModeSetup(handle);
                    Console.WriteLine($"System audio capture: gain {gain:0.##}, low-pass {lpfHz:0.#} Hz. Ctrl+C to stop.");
                    RunCapture(handle, gain, lpfHz);
                    return 0;
                }
                case "features":
                case "featuresusb":
                {
                    RunFeatureDump(handle);
                    return 0;
                }
                case "readusb":
                {
                    double seconds = args.Length > 1 ? double.Parse(args[1]) : 2.0;
                    RunUsbReadRate(handle, seconds);
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"Unknown mode '{mode}'. Use 'test36', 'test', 'rumble', 'capture', 'features', 'featuresusb', 'readusb', or 'probe'.");
                    return 2;
            }
        }
        finally
        {
            NativeHid.TimeEndPeriod(1);
        }
    }

    private static void RunFeatureDump(SafeFileHandle handle)
    {
        foreach ((byte reportId, int length, string name) in new[]
        {
            ((byte)0x05, 41, "calibration"),
            ((byte)0x09, 64, "serial/pairing"),
            ((byte)0x20, 64, "firmware"),
        })
        {
            byte[] report = new byte[length];
            report[0] = reportId;
            bool success = NativeHid.GetFeature(handle, report, out int error);
            Console.WriteLine($"feature 0x{reportId:X2} {name}: success={success} " +
                $"length={length} error={error}");
            if (success)
            {
                Console.WriteLine(Convert.ToHexString(report));
            }
        }
    }

    private static void RunUsbReadRate(SafeFileHandle handle, double seconds)
    {
        byte[] report = new byte[64];
        int count = 0;
        long bytes = 0;
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            if (!NativeHid.Read(handle, report, out uint read, out int error))
            {
                throw new IOException($"Virtual USB ReadFile failed after {count} reports (error {error}).");
            }
            count++;
            bytes += read;
        }

        timer.Stop();
        double rate = count / timer.Elapsed.TotalSeconds;
        Console.WriteLine($"Read {count} reports / {bytes} bytes in {timer.Elapsed.TotalSeconds:0.000} s " +
            $"({rate:0.0} reports/s). Last report: {Convert.ToHexString(report)}");
    }

    /// <summary>
    /// The BT interface exposes the large streaming output reports (0x32..0x39),
    /// so its max output report length is far bigger than USB's. That property
    /// is how we distinguish a BT-connected pad from a wired one.
    /// </summary>
    private static HidDevice FindBtDualSense()
    {
        foreach (HidDevice dev in DeviceList.Local.GetHidDevices(SonyVid))
        {
            if (dev.ProductID != DualSensePid && dev.ProductID != DualSenseEdgePid)
                continue;

            int maxOut;
            try { maxOut = dev.GetMaxOutputReportLength(); }
            catch { continue; }

            Console.WriteLine($"Found DualSense (PID 0x{dev.ProductID:X4}), max output report {maxOut} bytes.");
            if (maxOut >= 100)
                return dev;
        }

        return null;
    }

    private static HidDevice FindUsbDualSense()
    {
        foreach (HidDevice dev in DeviceList.Local.GetHidDevices(SonyVid))
        {
            if (dev.ProductID != DualSensePid && dev.ProductID != DualSenseEdgePid)
                continue;

            int maxOut;
            try { maxOut = dev.GetMaxOutputReportLength(); }
            catch { continue; }

            Console.WriteLine($"Found USB DualSense candidate (PID 0x{dev.ProductID:X4}), " +
                $"max output report {maxOut} bytes.");
            if (maxOut is > 0 and <= 64)
                return dev;
        }

        return null;
    }

    /// <summary>
    /// Reads the true total byte size of output report 0x32 from the HID report
    /// descriptor. HidSharp's length convention is calibrated against report 0x31,
    /// which is known to be 78 bytes including the report ID.
    /// </summary>
    private static int ResolveReport32Size(HidDevice device, bool dumpAll)
    {
        var descriptor = device.GetReportDescriptor();
        int len31 = -1, len32 = -1;
        foreach (var report in descriptor.Reports)
        {
            if (dumpAll)
                Console.WriteLine($"  {report.ReportType} report 0x{report.ReportID:X2}: Length={report.Length}");

            if (report.ReportType != HidSharp.Reports.ReportType.Output)
                continue;
            if (report.ReportID == 0x31)
                len31 = report.Length;
            else if (report.ReportID == 0x32)
                len32 = report.Length;
        }

        if (len31 < 0 || len32 < 0)
            return -1;

        int idAdjust = 78 - len31; // 0 if HidSharp counts the ID byte, 1 if not
        if (idAdjust != 0 && idAdjust != 1)
            Console.WriteLine($"WARNING: unexpected 0x31 length {len31}; report sizing may be off.");

        return len32 + idAdjust;
    }

    /// <summary>
    /// Empirically determines which write shapes the BT HID stack + controller accept.
    /// First proves the write path with a harmless no-op 0x31 report (all effect
    /// flags zero — the pad changes nothing), then sweeps 0x32 sizes.
    /// </summary>
    private static void RunWriteProbe(SafeFileHandle handle)
    {
        // No-op 0x31: [0]=0x31, [1]=0x02 (DATA tag), flag bytes zero, CRC over first 74.
        byte[] rep31 = new byte[78];
        rep31[0] = 0x31;
        rep31[1] = 0x02;
        uint crc = Crc32.Update(0xFFFFFFFF, 0xA2);
        crc = Crc32.Update(crc, rep31.AsSpan(0, 74));
        crc = ~crc;
        rep31[74] = (byte)crc;
        rep31[75] = (byte)(crc >> 8);
        rep31[76] = (byte)(crc >> 16);
        rep31[77] = (byte)(crc >> 24);
        ProbeOne(handle, "0x31 no-op @78", rep31);

        // Report 0x32 transports signed 8-bit PCM. Zero is therefore silence.
        byte[] silence = new byte[HapticReport.AudioBytes];

        foreach (int size in new[] { 141, 142, 547 })
        {
            HapticReport.TotalSize = size;
            byte[] rep = new byte[size];
            HapticReport.Build(rep, 0, 0, silence);
            ProbeOne(handle, $"0x32 crc@{size - 4} @{size}", rep);
        }

        // 547-byte buffer, but laid out as a 142-byte report zero-padded to max length.
        HapticReport.TotalSize = 142;
        byte[] padded = new byte[547];
        HapticReport.Build(padded, 0, 0, silence);
        ProbeOne(handle, "0x32 crc@138 padded to @547", padded);
    }

    private static void ProbeOne(SafeFileHandle handle, string label, byte[] report)
    {
        for (int i = 0; i < 3; i++)
        {
            bool ok = NativeHid.Write(handle, report, out bool raw, out uint written, out int err);
            Console.WriteLine($"  {label}: ok={ok} WriteFile={raw} written={written}/{report.Length} err={err}");
            Thread.Sleep(15);
        }
    }

    /// <summary>
    /// A freshly powered-on Bluetooth controller needs a packetized SetStateData
    /// command before it accepts PCM haptics. This is the exact 0x32/0x10
    /// initialization shape used by DS5Dongle when its HID interrupt channel opens.
    /// </summary>
    private static void SendHapticsModeSetup(SafeFileHandle handle)
    {
        byte[] report = new byte[142];
        report[0] = 0x32;
        report[1] = 0x10;
        report[2] = 0x90; // packet 0x10 | sized flag
        report[3] = 0x3F; // 63-byte SetStateData payload

        Haptics36Report.CopyKnownStateTo(report.AsSpan(4, Haptics36Report.StateBytes));

        uint crc = Crc32.Update(0xFFFFFFFF, 0xA2);
        crc = Crc32.Update(crc, report.AsSpan(0, 138));
        crc = ~crc;
        report[138] = (byte)crc;
        report[139] = (byte)(crc >> 8);
        report[140] = (byte)(crc >> 16);
        report[141] = (byte)(crc >> 24);

        bool ok = NativeHid.Write(handle, report, out bool raw, out uint written, out int err);
        Console.WriteLine($"Haptics mode setup: ok={ok} WriteFile={raw} written={written}/{report.Length} err={err}");
        Thread.Sleep(50);
    }

    private static void RunCompatibilityRumbleTest(SafeFileHandle handle, double seconds)
    {
        int writes = Math.Max(1, (int)Math.Ceiling(seconds * 50.0));
        byte sequence = 0;
        int failures = 0;

        for (int i = 0; i < writes + 5; i++)
        {
            bool active = i < writes;
            byte[] report = new byte[78];
            report[0] = 0x31;
            report[1] = (byte)((sequence++ & 0x0F) << 4);
            report[2] = 0x10;
            report[3] = 0x03; // compatible vibration + select compatibility haptics
            report[5] = active ? (byte)0xFF : (byte)0x00; // right/light
            report[6] = active ? (byte)0xFF : (byte)0x00; // left/heavy

            uint crc = Crc32.Update(0xFFFFFFFF, 0xA2);
            crc = Crc32.Update(crc, report.AsSpan(0, 74));
            crc = ~crc;
            report[74] = (byte)crc;
            report[75] = (byte)(crc >> 8);
            report[76] = (byte)(crc >> 16);
            report[77] = (byte)(crc >> 24);

            if (!NativeHid.Write(handle, report, out _, out _, out _))
            {
                failures++;
            }

            Thread.Sleep(20);
        }

        Console.WriteLine($"Compatibility rumble done. {writes + 5} reports, {failures} write failures.");
    }

    private static void RunSineTest(SafeFileHandle handle, double seconds, double freq, double amp)
    {
        amp = Math.Clamp(amp, 0.0, 1.0);
        long totalReports = (long)Math.Ceiling(seconds * 1000.0 / ReportPeriodMs);
        var sender = new ReportSender(handle);
        byte[] audio = new byte[FramesPerReport * 2];
        double phase = 0.0;
        double phaseInc = 2.0 * Math.PI * freq / HapticsSampleRate;

        for (long n = 0; n < totalReports; n++)
        {
            for (int i = 0; i < FramesPerReport; i++)
            {
                sbyte signedSample = (sbyte)Math.Clamp(amp * 127.0 * Math.Sin(phase), -127.0, 127.0);
                byte s = unchecked((byte)signedSample);
                phase += phaseInc;
                audio[i * 2] = s;     // left actuator
                audio[i * 2 + 1] = s; // right actuator
            }

            if (!sender.SendPaced(audio))
                return;
        }

        // Trailing silence so the actuators settle cleanly.
        Array.Clear(audio);
        for (int i = 0; i < 6; i++)
            sender.SendPaced(audio);

        Console.WriteLine($"Done. {sender.ReportsSent} reports sent, {sender.WriteErrors} write errors.");
    }

    private static void RunSineTest36(SafeFileHandle handle, double seconds, double freq, double amp)
    {
        amp = Math.Clamp(amp, 0.0, 1.0);
        long totalReports = (long)Math.Ceiling(seconds * 1000.0 / ReportPeriodMs);
        var sender = new Report36Sender(handle);
        byte[] audio = new byte[FramesPerReport * 2];
        double phase = 0.0;
        double phaseInc = 2.0 * Math.PI * freq / HapticsSampleRate;

        for (long n = 0; n < totalReports; n++)
        {
            for (int i = 0; i < FramesPerReport; i++)
            {
                sbyte signedSample = (sbyte)Math.Clamp(amp * 127.0 * Math.Sin(phase), -127.0, 127.0);
                phase += phaseInc;
                byte s = unchecked((byte)signedSample);
                audio[i * 2] = s;
                audio[i * 2 + 1] = s;
            }

            if (!sender.SendPaced(audio))
                return;
        }

        Array.Clear(audio);
        for (int i = 0; i < 6; i++)
            sender.SendPaced(audio);

        Console.WriteLine($"Done. {sender.ReportsSent} reports sent, {sender.WriteErrors} write errors.");
    }

    private static void RunCapture(SafeFileHandle handle, double gain, double lpfHz)
    {
        var ring = new SampleRing(capacityBytes: 1920); // ~320 ms hard cap on buffered haptics
        const int prebufferBytes = 576;                 // ~96 ms cushion before draining starts

        using (var mmEnum = new NAudio.CoreAudioApi.MMDeviceEnumerator())
        {
            var dev = mmEnum.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            Console.WriteLine($"Capturing loopback of default output: {dev.FriendlyName}");
            Console.WriteLine("(If your game/music plays on a different output device, make that one the Windows default.)");
        }

        using var capture = new WasapiLoopbackCapture();
        int inRate = capture.WaveFormat.SampleRate;
        int inChannels = capture.WaveFormat.Channels;
        var lpfL = new BiquadLowPass(lpfHz, inRate);
        var lpfR = new BiquadLowPass(lpfHz, inRate);
        int decimPhase = 0;

        object meterGate = new();
        double meterSumSquares = 0;
        long meterSamples = 0;

        Console.WriteLine($"Loopback format: {inRate} Hz, {inChannels} ch, {capture.WaveFormat.Encoding}");

        capture.DataAvailable += (_, e) =>
        {
            // WASAPI shared-mode loopback delivers 32-bit float frames at the engine mix format.
            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
            double sumSq = 0;
            int frames = 0;
            for (int i = 0; i + inChannels <= samples.Length; i += inChannels)
            {
                double rawL = samples[i];
                double l = lpfL.Process(rawL);
                double r = lpfR.Process(inChannels > 1 ? samples[i + 1] : samples[i]);
                sumSq += rawL * rawL;
                frames++;

                decimPhase += HapticsSampleRate;
                if (decimPhase < inRate)
                    continue;
                decimPhase -= inRate;

                ring.Write(SoftClipToS8Byte(l * gain), SoftClipToS8Byte(r * gain));
            }

            lock (meterGate)
            {
                meterSumSquares += sumSq;
                meterSamples += frames;
            }
        };

        capture.RecordingStopped += (_, e) =>
            Console.Error.WriteLine($"Capture stopped{(e.Exception != null ? $" with error: {e.Exception.Message}" : ".")}");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        capture.StartRecording();

        var sender = new ReportSender(handle);
        byte[] audio = new byte[FramesPerReport * 2];
        bool primed = false;
        long ticks = 0;

        while (!cts.IsCancellationRequested)
        {
            if (!primed && ring.Count >= prebufferBytes)
                primed = true;

            int got = primed ? ring.ReadPartial(audio) : 0;
            if (got < audio.Length)
                Array.Clear(audio, got, audio.Length - got);
            if (primed && got == 0)
                primed = false; // source went quiet; rebuffer before draining again

            if (!sender.SendPaced(audio))
                break;

            if (++ticks % 94 == 0) // ~once per second
            {
                double rms;
                lock (meterGate)
                {
                    rms = meterSamples > 0 ? Math.Sqrt(meterSumSquares / meterSamples) : 0;
                    meterSumSquares = 0;
                    meterSamples = 0;
                }

                Console.WriteLine($"in={rms * 100,5:0.0}% ring={ring.Count,4}B primed={(primed ? 1 : 0)} sent={sender.ReportsSent} err={sender.WriteErrors}");
            }
        }

        capture.StopRecording();
        Console.WriteLine($"Stopped. {sender.ReportsSent} reports sent, {sender.WriteErrors} write errors.");
    }

    private static byte SoftClipToS8Byte(double x)
    {
        double y = x / (1.0 + Math.Abs(x)); // smooth limiter, no hard clipping artifacts
        sbyte signedSample = (sbyte)Math.Clamp(y * 127.0, -127.0, 127.0);
        return unchecked((byte)signedSample);
    }
}

/// <summary>
/// Builds and paces DualSense BT haptic stream reports (ID 0x32).
///
/// Report layout, per SAxense's reverse engineering (total size read from the
/// device's HID descriptor at runtime; CRC always occupies the last 4 bytes):
///   [0]        0x32 report ID
///   [1]        low nibble: tag (0), high nibble: rolling sequence 0..15
///   [2]        0x91 = config packet header (PID 0x11 | sized flag 0x80)
///   [3]        0x07 = config payload length
///   [4..10]    FE 00 00 00 00 FF cc   (cc = incrementing counter)
///   [11]       0x92 = audio packet header (PID 0x12 | sized flag 0x80)
///   [12]       0x40 = audio payload length (64)
///   [13..76]   64 bytes PCM: signed 8-bit, stereo interleaved L/R, 3000 Hz
///   [77..N-5]  zero padding
///   [N-4..N-1] CRC-32 (little-endian) over 0xA2 || report[0..N-5]
/// </summary>
internal static class HapticReport
{
    public static int TotalSize = 142;
    public const int AudioOffset = 13;
    public const int AudioBytes = 64;

    public static void Build(byte[] report, byte seq, byte counter, ReadOnlySpan<byte> audio64)
    {
        int crcOffset = TotalSize - 4;
        Array.Clear(report, 0, TotalSize);
        report[0] = 0x32;
        report[1] = (byte)((seq & 0x0F) << 4);

        report[2] = 0x91; // PID 0x11 | 0x80 (sized)
        report[3] = 0x07;
        report[4] = 0xFE;
        report[9] = 0xFF;
        report[10] = counter;

        report[11] = 0x92; // PID 0x12 | 0x80 (sized)
        report[12] = (byte)AudioBytes;
        audio64.CopyTo(report.AsSpan(AudioOffset, AudioBytes));

        uint crc = Crc32.Update(0xFFFFFFFF, 0xA2);
        crc = Crc32.Update(crc, report.AsSpan(0, crcOffset));
        crc = ~crc;
        report[crcOffset] = (byte)crc;
        report[crcOffset + 1] = (byte)(crc >> 8);
        report[crcOffset + 2] = (byte)(crc >> 16);
        report[crcOffset + 3] = (byte)(crc >> 24);
    }
}

/// <summary>
/// Builds DS5Dongle-AutoHaptics' self-contained Bluetooth report 0x36. Unlike
/// the compact 0x32 stream, every report carries SetStateData before the PCM.
/// </summary>
internal static class Haptics36Report
{
    public const int TotalSize = 398;
    public const int StateBytes = 63;
    private const int AudioOffset = 78;
    private const int AudioBytes = 64;

    private static readonly byte[] KnownState = new byte[StateBytes]
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

    public static void CopyKnownStateTo(Span<byte> destination) => KnownState.CopyTo(destination);

    public static void Build(byte[] report, byte seq, byte counter, ReadOnlySpan<byte> audio64)
    {
        Array.Clear(report);
        report[0] = 0x36;
        report[1] = (byte)((seq & 0x0F) << 4);

        report[2] = 0x91; // config packet 0x11 | sized
        report[3] = 0x07;
        report[4] = 0xFE;
        report[5] = 0x20;
        report[6] = 0x20;
        report[7] = 0x20;
        report[8] = 0x20;
        report[9] = 0x20;
        report[10] = counter;

        report[11] = 0x90; // SetStateData packet 0x10 | sized
        report[12] = StateBytes;
        KnownState.CopyTo(report.AsSpan(13, StateBytes));

        report[76] = 0x92; // haptics packet 0x12 | sized
        report[77] = AudioBytes;
        audio64.CopyTo(report.AsSpan(AudioOffset, AudioBytes));

        uint crc = Crc32.Update(0xFFFFFFFF, 0xA2);
        crc = Crc32.Update(crc, report.AsSpan(0, TotalSize - 4));
        crc = ~crc;
        report[TotalSize - 4] = (byte)crc;
        report[TotalSize - 3] = (byte)(crc >> 8);
        report[TotalSize - 2] = (byte)(crc >> 16);
        report[TotalSize - 1] = (byte)(crc >> 24);
    }
}

internal sealed class Report36Sender
{
    private readonly SafeFileHandle handle;
    private readonly byte[] report = new byte[Haptics36Report.TotalSize];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double nextDeadlineMs;
    private byte seq;
    private byte counter;

    public long ReportsSent { get; private set; }
    public long WriteErrors { get; private set; }

    public Report36Sender(SafeFileHandle handle) => this.handle = handle;

    public bool SendPaced(ReadOnlySpan<byte> audio64)
    {
        nextDeadlineMs += 64.0 * 500.0 / 3000.0;
        double wait = nextDeadlineMs - clock.Elapsed.TotalMilliseconds;
        if (wait > 2.0)
            Thread.Sleep((int)(wait - 1.5));
        while (clock.Elapsed.TotalMilliseconds < nextDeadlineMs)
            Thread.SpinWait(80);

        if (clock.Elapsed.TotalMilliseconds - nextDeadlineMs > 100.0)
            nextDeadlineMs = clock.Elapsed.TotalMilliseconds;

        Haptics36Report.Build(report, seq, counter, audio64);
        seq = (byte)((seq + 1) & 0x0F);
        counter++;

        if (!NativeHid.Write(handle, report, out bool rawResult, out uint written, out int error))
        {
            WriteErrors++;
            if (WriteErrors <= 3)
                Console.Error.WriteLine($"0x36 write failed: WriteFile={rawResult}, bytesWritten={written}/{report.Length}, Win32 error {error}");
            if (WriteErrors > 20 && WriteErrors > ReportsSent / 2)
                return false;
        }
        else
        {
            ReportsSent++;
        }

        return true;
    }
}

internal sealed class ReportSender
{
    private readonly SafeFileHandle handle;
    private readonly byte[] report = new byte[HapticReport.TotalSize];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double nextDeadlineMs;
    private byte seq;
    private byte counter;

    public long ReportsSent { get; private set; }
    public long WriteErrors { get; private set; }

    public ReportSender(SafeFileHandle handle) => this.handle = handle;

    /// <summary>Waits until the next 10.67 ms slot, then writes one report. Returns false on fatal I/O failure.</summary>
    public bool SendPaced(ReadOnlySpan<byte> audio64)
    {
        nextDeadlineMs += HapticReport.AudioBytes * 500.0 / 3000.0; // 64 bytes / (3000 Hz * 2 ch) * 1000
        double wait = nextDeadlineMs - clock.Elapsed.TotalMilliseconds;
        if (wait > 2.0)
            Thread.Sleep((int)(wait - 1.5));
        while (clock.Elapsed.TotalMilliseconds < nextDeadlineMs)
            Thread.SpinWait(80);

        // If we fell badly behind (system stall), resync rather than bursting.
        if (clock.Elapsed.TotalMilliseconds - nextDeadlineMs > 100.0)
            nextDeadlineMs = clock.Elapsed.TotalMilliseconds;

        HapticReport.Build(report, seq, counter, audio64);
        seq = (byte)((seq + 1) & 0x0F);
        counter++;

        if (!NativeHid.Write(handle, report, out bool rawResult, out uint written, out int error))
        {
            WriteErrors++;
            if (WriteErrors <= 3)
                Console.Error.WriteLine($"write failed: WriteFile={rawResult}, bytesWritten={written}/{report.Length}, Win32 error {error}");

            if (WriteErrors > 20 && WriteErrors > ReportsSent / 2)
            {
                Console.Error.WriteLine("Aborting: persistent write failures.");
                return false;
            }
        }
        else
        {
            ReportsSent++;
        }

        return true;
    }
}

/// <summary>Fixed-size byte ring for interleaved L/R signed-PCM bytes, tuned for bounded latency.</summary>
internal sealed class SampleRing
{
    private readonly byte[] buffer;
    private readonly object gate = new();
    private int head;
    private int count;

    public long Underruns { get; private set; }
    public int Count { get { lock (gate) return count; } }

    public SampleRing(int capacityBytes) => buffer = new byte[capacityBytes];

    public void Write(byte left, byte right)
    {
        lock (gate)
        {
            // Overwrite oldest data when full: fresher haptics beat growing latency.
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

    /// <summary>
    /// Copies up to dest.Length bytes (rounded down to whole L/R frames) and
    /// returns how many were copied. The caller pads any shortfall with silence,
    /// so a slow producer degrades gracefully instead of going all-or-nothing.
    /// </summary>
    public int ReadPartial(byte[] dest)
    {
        lock (gate)
        {
            int n = Math.Min(count, dest.Length) & ~1;
            if (n < dest.Length)
                Underruns++;

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
internal sealed class BiquadLowPass
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

internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        return table;
    }

    public static uint Update(uint state, byte value) =>
        (state >> 8) ^ Table[(state ^ value) & 0xFF];

    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            state = Update(state, b);
        return state;
    }
}

internal static class NativeHid
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle file, byte[] buffer, uint bytesToWrite,
        out uint bytesWritten, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint bytesToRead,
        out uint bytesRead, IntPtr overlapped);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject,
        byte[] reportBuffer, uint reportBufferLength);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint ms);

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    public static SafeFileHandle OpenDevice(string devicePath) =>
        CreateFile(devicePath, GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    public static bool Write(SafeFileHandle handle, byte[] report, out bool rawResult, out uint written, out int error)
    {
        rawResult = WriteFile(handle, report, (uint)report.Length, out written, IntPtr.Zero);
        error = rawResult ? 0 : Marshal.GetLastWin32Error();
        // Windows' BT HID stack reports bytesWritten as the max output report
        // length (547) regardless of the actual report size, so only the
        // WriteFile result is meaningful.
        return rawResult;
    }

    public static bool GetFeature(SafeFileHandle handle, byte[] report, out int error)
    {
        bool result = HidD_GetFeature(handle, report, (uint)report.Length);
        error = result ? 0 : Marshal.GetLastWin32Error();
        return result;
    }

    public static bool Read(SafeFileHandle handle, byte[] report, out uint read, out int error)
    {
        bool result = ReadFile(handle, report, (uint)report.Length, out read, IntPtr.Zero);
        error = result ? 0 : Marshal.GetLastWin32Error();
        return result;
    }
}
