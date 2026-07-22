// SPDX-License-Identifier: GPL-3.0-or-later

namespace VirtualDualSenseUsbip.Device;

/// <summary>
/// Exercises the shipped byte-exact descriptors and control-endpoint state
/// machine entirely offline, with no driver or controller required.
/// </summary>
public static class DeviceSelfTest
{
    public static int Run(string fixturesDir)
    {
        if (!Directory.Exists(fixturesDir))
        {
            Console.Error.WriteLine($"Fixtures directory not found: {fixturesDir}");
            return 1;
        }

        var descriptors = DescriptorSet.LoadFromFixtures(fixturesDir);
        var ep0 = new ControlEndpoint(descriptors);

        byte lastAltInterface = 0xFF, lastAlt = 0xFF;
        ep0.InterfaceAltChanged += (iface, alt) => { lastAltInterface = iface; lastAlt = alt; };

        Console.WriteLine($"Loaded descriptors: VID_{descriptors.VendorId:X4} PID_{descriptors.ProductId:X4}, " +
            $"{descriptors.NumInterfaces} interfaces, HID interface {descriptors.HidInterfaceNumber}.");

        int checks = 0;
        var descriptorChecks = new[]
        {
            (
                "device",
                new UsbSetupPacket(0x80, UsbStandardRequest.GetDescriptor,
                    0x0100, 0, 18),
                File.ReadAllBytes(Path.Combine(fixturesDir, "device.bin"))
            ),
            (
                "configuration",
                new UsbSetupPacket(0x80, UsbStandardRequest.GetDescriptor,
                    0x0200, 0, (ushort)descriptors.ConfigurationDescriptorLength),
                File.ReadAllBytes(Path.Combine(fixturesDir, "configuration.bin"))
            ),
            (
                "language string",
                new UsbSetupPacket(0x80, UsbStandardRequest.GetDescriptor,
                    0x0300, 0, 4),
                File.ReadAllBytes(Path.Combine(fixturesDir,
                    "string-0-lang-0000.bin"))
            ),
            (
                "manufacturer string",
                new UsbSetupPacket(0x80, UsbStandardRequest.GetDescriptor,
                    0x0301, 0x0409, 62),
                File.ReadAllBytes(Path.Combine(fixturesDir,
                    "string-1-lang-0409.bin"))
            ),
            (
                "product string",
                new UsbSetupPacket(0x80, UsbStandardRequest.GetDescriptor,
                    0x0302, 0x0409, 60),
                File.ReadAllBytes(Path.Combine(fixturesDir,
                    "string-2-lang-0409.bin"))
            ),
            (
                "HID report",
                new UsbSetupPacket(0x81, UsbStandardRequest.GetDescriptor,
                    0x2200, descriptors.HidInterfaceNumber, 289),
                File.ReadAllBytes(Path.Combine(fixturesDir, "hid-report.bin"))
            ),
        };

        foreach ((string name, UsbSetupPacket setup, byte[] expected)
            in descriptorChecks)
        {
            ControlResult result = ep0.Handle(setup, ReadOnlySpan<byte>.Empty);
            if (result.Status != 0)
            {
                Console.Error.WriteLine(
                    $"FAIL {name}: request 0x{setup.Request:X2} stalled " +
                    $"(status {result.Status}).");
                return 1;
            }

            if (!result.Data.AsSpan().SequenceEqual(expected))
            {
                Console.Error.WriteLine($"FAIL {name}: " +
                    $"request 0x{setup.Request:X2} returned {result.Data.Length} bytes, " +
                    $"expected {expected.Length}.");
                Console.Error.WriteLine($"  got:      {Convert.ToHexString(result.Data)}");
                Console.Error.WriteLine($"  expected: {Convert.ToHexString(expected)}");
                return 1;
            }

            checks++;
            Console.WriteLine($"OK   {name,-20} req 0x{setup.Request:X2} " +
                $"wValue 0x{setup.Value:X4} -> {result.Data.Length} bytes match");
        }

        ControlResult setConfiguration = ep0.Handle(
            new UsbSetupPacket(0x00, UsbStandardRequest.SetConfiguration,
                1, 0, 0),
            ReadOnlySpan<byte>.Empty);
        ControlResult setRenderInterface = ep0.Handle(
            new UsbSetupPacket(0x01, UsbStandardRequest.SetInterface,
                1, 1, 0),
            ReadOnlySpan<byte>.Empty);
        checks += 2;

        if (setConfiguration.Status != 0 || setRenderInterface.Status != 0)
        {
            Console.Error.WriteLine(
                "FAIL: configuration or render-interface activation stalled.");
            return 1;
        }

        if (lastAltInterface != 1 || lastAlt != 1)
        {
            Console.Error.WriteLine($"FAIL: expected SET_INTERFACE(if=1, alt=1); saw if={lastAltInterface} alt={lastAlt}.");
            return 1;
        }

        if (ep0.ConfigurationValue != 1)
        {
            Console.Error.WriteLine($"FAIL: expected configuration 1, saw {ep0.ConfigurationValue}.");
            return 1;
        }

        var getSpeakerMute = new UsbSetupPacket(
            RequestType: 0xA1,
            Request: 0x81,
            Value: 0x0100,
            Index: 0x0200,
            Length: 1);
        ControlResult initialMute = ep0.Handle(getSpeakerMute, ReadOnlySpan<byte>.Empty);
        if (initialMute.Status != 0 || initialMute.Data.Length != 1 || initialMute.Data[0] != 0)
        {
            Console.Error.WriteLine("FAIL: UAC speaker-mute GET_CUR did not return unmuted state.");
            return 1;
        }

        var setSpeakerMute = getSpeakerMute with { RequestType = 0x21, Request = 0x01 };
        ControlResult setMute = ep0.Handle(setSpeakerMute, new byte[] { 1 });
        ControlResult updatedMute = ep0.Handle(getSpeakerMute, ReadOnlySpan<byte>.Empty);
        if (setMute.Status != 0 || updatedMute.Status != 0 || updatedMute.Data[0] != 1)
        {
            Console.Error.WriteLine("FAIL: UAC speaker-mute SET_CUR state did not round-trip.");
            return 1;
        }

        var getSpeakerVolume = getSpeakerMute with { Value = 0x0200, Length = 2 };
        ControlResult initialVolume = ep0.Handle(getSpeakerVolume, ReadOnlySpan<byte>.Empty);
        if (initialVolume.Status != 0 || !initialVolume.Data.AsSpan().SequenceEqual(new byte[] { 0, 0 }))
        {
            Console.Error.WriteLine("FAIL: UAC speaker-volume GET_CUR did not return 0 dB.");
            return 1;
        }

        var setSpeakerVolume = getSpeakerVolume with { RequestType = 0x21, Request = 0x01 };
        ControlResult setVolume = ep0.Handle(setSpeakerVolume, new byte[] { 0x00, 0xFF });
        ControlResult updatedVolume = ep0.Handle(getSpeakerVolume, ReadOnlySpan<byte>.Empty);
        if (setVolume.Status != 0 || updatedVolume.Status != 0 ||
            !updatedVolume.Data.AsSpan().SequenceEqual(new byte[] { 0x00, 0xFF }))
        {
            Console.Error.WriteLine("FAIL: UAC speaker-volume SET_CUR state did not round-trip.");
            return 1;
        }

        var getSpeakerVolumeMin = getSpeakerVolume with { Request = 0x82 };
        var getSpeakerVolumeMax = getSpeakerVolume with { Request = 0x83 };
        var getSpeakerVolumeRes = getSpeakerVolume with { Request = 0x84 };
        if (!ep0.Handle(getSpeakerVolumeMin, ReadOnlySpan<byte>.Empty).Data.AsSpan()
                .SequenceEqual(new byte[] { 0x00, 0x9C }) ||
            !ep0.Handle(getSpeakerVolumeMax, ReadOnlySpan<byte>.Empty).Data.AsSpan()
                .SequenceEqual(new byte[] { 0x00, 0x00 }) ||
            !ep0.Handle(getSpeakerVolumeRes, ReadOnlySpan<byte>.Empty).Data.AsSpan()
                .SequenceEqual(new byte[] { 0x00, 0x01 }))
        {
            Console.Error.WriteLine("FAIL: UAC speaker-volume min/max/resolution responses are invalid.");
            return 1;
        }

        Console.WriteLine($"PASS: {checks} EP0 checks byte-exact; device configured, " +
            $"audio-streaming alt setting activated; UAC mute/volume controls and range passed.");
        return 0;
    }
}
