using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using VirtualDualSenseUsbip.Device;
using VirtualDualSenseUsbip.Live;
using VirtualDualSenseUsbip.Protocol;

if (args.Length >= 1 && args[0].Equals("selftest", StringComparison.OrdinalIgnoreCase))
{
    await UsbIpSelfTest.RunAsync();
    return;
}

if (args.Length >= 1 && args[0].Equals("devicetest", StringComparison.OrdinalIgnoreCase))
{
    string fixtures = args.Length >= 2
        ? args[1]
        : DefaultFixturesPath();
    Environment.Exit(DeviceSelfTest.Run(Path.GetFullPath(fixtures)));
}

if (args.Length >= 1 && args[0].Equals("servertest", StringComparison.OrdinalIgnoreCase))
{
    string fixtures = args.Length >= 2 ? args[1] : DefaultFixturesPath();
    await LiveServerSelfTest.RunAsync(Path.GetFullPath(fixtures));
    return;
}

if (args.Length >= 1 && args[0].Equals("inputtest", StringComparison.OrdinalIgnoreCase))
{
    double seconds = args.Length >= 2 ? ParseSeconds(args[1]) : 5;
    using var input = BluetoothDualSenseInputSource.Open(
        message => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}"));
    Console.WriteLine($"Reading {input.Description} for {seconds:0.###} seconds...");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    byte[] latest = input.CreateReport(64);
    Console.WriteLine($"Received {input.ValidReportCount} valid reports; " +
        $"rejected {input.InvalidReportCount}. Latest USB report: {Convert.ToHexString(latest)}");
    if (input.ValidReportCount == 0)
    {
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length >= 1 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
{
    string fixtures = GetOption(args, "--fixtures") ?? DefaultFixturesPath();
    string busId = GetOption(args, "--busid") ?? "1-1";
    string? capturePath = GetOption(args, "--capture");
    string inputMode = GetOption(args, "--input") ?? "neutral";
    string configurationMode = GetOption(args, "--configuration") ?? "hid";
    string suggestedSerial = configurationMode.Equals("composite", StringComparison.OrdinalIgnoreCase)
        ? "DS4WSPKCOMP001"
        : "DS4WSPKHID001";
    int port = ParsePort(GetOption(args, "--port"));

    string fullFixturesPath = Path.GetFullPath(fixtures);
    DescriptorSet descriptors = configurationMode.ToLowerInvariant() switch
    {
        "hid" => DescriptorSet.LoadHidOnlyFromFixtures(fullFixturesPath),
        "composite" => DescriptorSet.LoadFromFixtures(fullFixturesPath),
        _ => throw new ArgumentException("--configuration must be 'hid' or 'composite'."),
    };
    using BluetoothDualSenseInputSource? bluetoothInput =
        inputMode.Equals("bluetooth", StringComparison.OrdinalIgnoreCase)
            ? BluetoothDualSenseInputSource.Open(
                message => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}"))
            : inputMode.Equals("neutral", StringComparison.OrdinalIgnoreCase)
                ? null
                : throw new ArgumentException("--input must be 'neutral' or 'bluetooth'.");
    IInputReportSource inputReports = bluetoothInput is not null
        ? bluetoothInput
        : new NeutralInputReportSource();
    FeatureReportSet featureReports =
        FeatureReportSet.CreateVirtualDefaults(bluetoothInput?.CalibrationFeatureReport);
    using var server = new VirtualDualSenseServer(descriptors,
        new VirtualDualSenseServerOptions(port, busId), inputReports, featureReports);
    server.Log += message => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");

    StreamWriter? captureWriter = null;
    object captureGate = new();
    long hidOutputCount = 0;
    byte[]? lastLoggedTriggerBlock = null;
    long isoOutCount = 0;
    long isoOutBytes = 0;
    long isoOutPackets = 0;
    long firstIsoTimestamp = 0;
    long previousIsoTimestamp = 0;
    double minimumIsoGapMs = double.PositiveInfinity;
    double maximumIsoGapMs = 0;
    double[] isoWindowSumSquares = new double[4];
    int[] isoWindowPeaks = new int[4];
    long isoWindowFrames = 0;
    if (!string.IsNullOrWhiteSpace(capturePath))
    {
        string fullCapturePath = Path.GetFullPath(capturePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullCapturePath)!);
        captureWriter = new StreamWriter(fullCapturePath, append: true) { AutoFlush = true };
        Console.WriteLine($"Capturing HID output to {fullCapturePath}");
    }

    server.HidOutputReceived += capture =>
    {
        _ = bluetoothInput?.QueueTriggerReport(capture.Data);
        string hex = Convert.ToHexString(capture.Data);
        bool triggerChanged = false;
        long outputNumber;
        string? json = captureWriter != null
            ? JsonSerializer.Serialize(new
            {
                timestampUtc = capture.Timestamp,
                capture.SequenceNumber,
                capture.Transport,
                length = capture.Data.Length,
                dataHex = hex,
            })
            : null;

        lock (captureGate)
        {
            outputNumber = ++hidOutputCount;
            if (capture.Data.Length >= 33 && capture.Data[0] == 0x02 &&
                (capture.Data[1] & 0x0C) != 0)
            {
                byte[] triggerState = new byte[23];
                triggerState[0] = (byte)(capture.Data[1] & 0x0C);
                capture.Data.AsSpan(11, 22).CopyTo(triggerState.AsSpan(1));
                if (lastLoggedTriggerBlock == null ||
                    !triggerState.AsSpan().SequenceEqual(lastLoggedTriggerBlock))
                {
                    lastLoggedTriggerBlock = triggerState;
                    triggerChanged = true;
                }
            }
            if (captureWriter != null)
            {
                captureWriter.WriteLine(json);
            }
        }

        if (outputNumber == 1 || triggerChanged || outputNumber % 5000 == 0)
        {
            Console.WriteLine($"{capture.Timestamp.ToLocalTime():HH:mm:ss.fff} HID OUT " +
                $"total={outputNumber} seq={capture.SequenceNumber} via={capture.Transport} " +
                $"bytes={capture.Data.Length} {hex[..Math.Min(hex.Length, 64)]}");
        }
    };

    server.IsochronousOutReceived += capture =>
    {
        _ = bluetoothInput?.QueueHapticAudio(capture.Data);
        double[] captureSumSquares = new double[4];
        int[] capturePeaks = new int[4];
        int captureFrames = capture.Data.Length % 8 == 0 ? capture.Data.Length / 8 : 0;
        for (int frame = 0; frame < captureFrames; frame++)
        {
            int offset = frame * 8;
            for (int channel = 0; channel < 4; channel++)
            {
                short sample = BinaryPrimitives.ReadInt16LittleEndian(
                    capture.Data.AsSpan(offset + channel * 2, 2));
                double normalized = sample / 32768.0;
                captureSumSquares[channel] += normalized * normalized;
                capturePeaks[channel] = Math.Max(capturePeaks[channel], Math.Abs((int)sample));
            }
        }

        long count;
        long totalBytes;
        long totalPackets;
        long firstTimestamp;
        double minGapMs;
        double maxGapMs;
        double[] rmsPercent = new double[4];
        double[] peakPercent = new double[4];
        lock (captureGate)
        {
            double incomingGapMs = previousIsoTimestamp == 0
                ? 0
                : (capture.MonotonicTimestamp - previousIsoTimestamp) * 1000.0 /
                  Stopwatch.Frequency;
            if (incomingGapMs > 100)
            {
                isoOutCount = 0;
                isoOutBytes = 0;
                isoOutPackets = 0;
                firstIsoTimestamp = 0;
                minimumIsoGapMs = double.PositiveInfinity;
                maximumIsoGapMs = 0;
                Array.Clear(isoWindowSumSquares);
                Array.Clear(isoWindowPeaks);
                isoWindowFrames = 0;
            }

            count = ++isoOutCount;
            isoOutBytes += capture.Data.Length;
            isoOutPackets += capture.Packets.Count;
            totalBytes = isoOutBytes;
            totalPackets = isoOutPackets;
            if (firstIsoTimestamp == 0)
            {
                firstIsoTimestamp = capture.MonotonicTimestamp;
            }
            if (previousIsoTimestamp != 0 && incomingGapMs <= 100)
            {
                minimumIsoGapMs = Math.Min(minimumIsoGapMs, incomingGapMs);
                maximumIsoGapMs = Math.Max(maximumIsoGapMs, incomingGapMs);
            }
            previousIsoTimestamp = capture.MonotonicTimestamp;
            firstTimestamp = firstIsoTimestamp;
            minGapMs = minimumIsoGapMs;
            maxGapMs = maximumIsoGapMs;
            for (int channel = 0; channel < 4; channel++)
            {
                isoWindowSumSquares[channel] += captureSumSquares[channel];
                isoWindowPeaks[channel] = Math.Max(isoWindowPeaks[channel], capturePeaks[channel]);
            }
            isoWindowFrames += captureFrames;
            if (count == 1 || count % 500 == 0)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    rmsPercent[channel] = isoWindowFrames > 0
                        ? Math.Sqrt(isoWindowSumSquares[channel] / isoWindowFrames) * 100.0
                        : 0;
                    peakPercent[channel] = isoWindowPeaks[channel] * 100.0 / 32768.0;
                    isoWindowSumSquares[channel] = 0;
                    isoWindowPeaks[channel] = 0;
                }
                isoWindowFrames = 0;
            }
        }

        if (count == 1 || count % 500 == 0)
        {
            double elapsedSeconds = Math.Max(0.000001,
                (capture.MonotonicTimestamp - firstTimestamp) / (double)Stopwatch.Frequency);
            uint minPacket = capture.Packets.Min(packet => packet.Length);
            uint maxPacket = capture.Packets.Max(packet => packet.Length);
            string gapRange = double.IsPositiveInfinity(minGapMs)
                ? "n/a"
                : $"{minGapMs:0.###}..{maxGapMs:0.###}";
            string rate = count == 1
                ? "urbs/s=n/a KiB/s=n/a"
                : $"urbs/s={count / elapsedSeconds:0.0} KiB/s={totalBytes / elapsedSeconds / 1024:0.0}";
            string bluetoothStats = bluetoothInput == null
                ? string.Empty
                : $" bt36={bluetoothInput.RelayedHapticReportCount} " +
                  $"btq={bluetoothInput.HapticQueueBytes} " +
                  $"bt-underrun={bluetoothInput.HapticUnderrunCount} " +
                  $"bt-errors={bluetoothInput.HapticWriteErrorCount}";
            Console.WriteLine($"{capture.Timestamp.ToLocalTime():HH:mm:ss.fff} ISO OUT " +
                $"total={count} seq={capture.SequenceNumber} ep={capture.Endpoint} " +
                $"{rate} " +
                $"packets={totalPackets} current={capture.Packets.Count}x{minPacket}..{maxPacket} " +
                $"gap-ms={gapRange} rms%={string.Join('/', rmsPercent.Select(value => value.ToString("0.00")))} " +
                $"peak%={string.Join('/', peakPercent.Select(value => value.ToString("0.0")))} " +
                $"start={capture.StartFrame} interval={capture.Interval}{bluetoothStats}");
        }
    };

    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;

    try
    {
        server.Start();
        Console.WriteLine($"Virtual DualSense ({configurationMode} configuration) is ready for usbip-win2.");
        Console.WriteLine($"Attach from an elevated terminal:");
        Console.WriteLine($"  usbip attach -r 127.0.0.1 -b {busId} --serial {suggestedSerial} --once");
        await server.RunAsync(cancellation.Token);
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
        captureWriter?.Dispose();
    }
    return;
}

Console.WriteLine("VirtualDualSenseUsbip M2.2-M2.5 protocol, HID, UAC1, and ISO spike");
Console.WriteLine("  selftest              USB/IP protocol golden vectors + fragmentation");
Console.WriteLine("  devicetest [fixtures] replay captured EP0 enumeration byte-exact");
Console.WriteLine("  servertest [fixtures] exercise the live server over loopback TCP");
Console.WriteLine("  inputtest [seconds]   validate physical BT input and USB report conversion");
Console.WriteLine("  serve [--fixtures DIR] [--port 3240] [--busid 1-1] [--capture FILE]");
Console.WriteLine("        [--input neutral|bluetooth] [--configuration hid|composite]");

static string DefaultFixturesPath() => Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "DSCompatProbe", "fixtures", "dualsense_usb_0ce6");

static string? GetOption(string[] arguments, string name)
{
    for (int i = 1; i < arguments.Length; i++)
    {
        if (!arguments[i].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }
        if (i + 1 >= arguments.Length)
        {
            throw new ArgumentException($"{name} requires a value.");
        }
        return arguments[i + 1];
    }
    return null;
}

static int ParsePort(string? value)
{
    if (value == null)
    {
        return 3240;
    }
    if (!int.TryParse(value, out int port) || port is < 1024 or > 65535)
    {
        throw new ArgumentOutOfRangeException(nameof(value), "Port must be between 1024 and 65535.");
    }
    return port;
}

static double ParseSeconds(string value)
{
    if (!double.TryParse(value, out double seconds) || !double.IsFinite(seconds) || seconds <= 0)
    {
        throw new ArgumentOutOfRangeException(nameof(value), "Seconds must be a positive number.");
    }
    return seconds;
}
