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
    int port = ParsePort(GetOption(args, "--port"));

    DescriptorSet descriptors = DescriptorSet.LoadHidOnlyFromFixtures(Path.GetFullPath(fixtures));
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
            if (capture.Data.Length >= 33 && capture.Data[0] == 0x02)
            {
                ReadOnlySpan<byte> triggerBlock = capture.Data.AsSpan(11, 22);
                if (lastLoggedTriggerBlock == null ||
                    !triggerBlock.SequenceEqual(lastLoggedTriggerBlock))
                {
                    lastLoggedTriggerBlock = triggerBlock.ToArray();
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
        Console.WriteLine("HID-only virtual DualSense is ready for usbip-win2.");
        Console.WriteLine($"Attach from an elevated terminal:");
        Console.WriteLine($"  usbip attach -r 127.0.0.1 -b {busId} --serial DS4WSPKHID001 --once");
        await server.RunAsync(cancellation.Token);
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
        captureWriter?.Dispose();
    }
    return;
}

Console.WriteLine("VirtualDualSenseUsbip M2.2 protocol core + M2.3 live HID device");
Console.WriteLine("  selftest              USB/IP protocol golden vectors + fragmentation");
Console.WriteLine("  devicetest [fixtures] replay captured EP0 enumeration byte-exact");
Console.WriteLine("  servertest [fixtures] exercise the live server over loopback TCP");
Console.WriteLine("  inputtest [seconds]   validate physical BT input and USB report conversion");
Console.WriteLine("  serve [--fixtures DIR] [--port 3240] [--busid 1-1] [--capture FILE]");
Console.WriteLine("        [--input neutral|bluetooth]");

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
