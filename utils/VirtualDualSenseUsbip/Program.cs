using VirtualDualSenseUsbip.Device;
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
        : Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "DSCompatProbe", "fixtures", "dualsense_usb_0ce6");
    Environment.Exit(DeviceSelfTest.Run(Path.GetFullPath(fixtures)));
}

Console.WriteLine("VirtualDualSenseUsbip M2.2 protocol core + M2.3 device layer (offline)");
Console.WriteLine("  selftest              USB/IP protocol golden vectors + fragmentation");
Console.WriteLine("  devicetest [fixtures] replay captured EP0 enumeration byte-exact");
Console.WriteLine("VHCI attachment is intentionally gated on M2.1 driver qualification.");
