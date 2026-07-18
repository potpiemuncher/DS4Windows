using VirtualDualSenseUsbip.Protocol;

if (args.Length == 1 && args[0].Equals("selftest", StringComparison.OrdinalIgnoreCase))
{
    await UsbIpSelfTest.RunAsync();
    return;
}

Console.WriteLine("VirtualDualSenseUsbip M2.2 protocol core");
Console.WriteLine("Run with 'selftest' to execute the fragmentation and golden-vector checks.");
Console.WriteLine("VHCI attachment is intentionally gated on M2.1 driver qualification.");
