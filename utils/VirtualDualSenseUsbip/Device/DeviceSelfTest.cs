using System.Text.Json;

namespace VirtualDualSenseUsbip.Device;

/// <summary>
/// Replays the real captured EP0 enumeration (enumeration.json) against the
/// emulated device and asserts every response is byte-identical. This validates
/// the descriptor set and control-endpoint state machine entirely offline — no
/// driver, no hardware — so M2.3's EP0 layer is proven before the M2.1 attach.
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

        string enumPath = Path.Combine(fixturesDir, "enumeration.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(enumPath));
        JsonElement.ArrayEnumerator entries = doc.RootElement.EnumerateArray();

        int checks = 0;
        var pending = new List<JsonElement>();
        foreach (JsonElement entry in entries)
        {
            pending.Add(entry);
        }

        for (int i = 0; i < pending.Count; i++)
        {
            JsonElement req = pending[i];
            if (req.GetProperty("setup").ValueKind == JsonValueKind.Null)
            {
                continue; // response entry; handled alongside its request
            }

            JsonElement setupEl = req.GetProperty("setup");
            var setup = new UsbSetupPacket(
                (byte)setupEl.GetProperty("bmRequestType").GetInt32(),
                (byte)setupEl.GetProperty("bRequest").GetInt32(),
                (ushort)setupEl.GetProperty("wValue").GetInt32(),
                (ushort)setupEl.GetProperty("wIndex").GetInt32(),
                (ushort)setupEl.GetProperty("wLength").GetInt32());

            // The response is the next entry (data stage / status stage).
            byte[] expected = Array.Empty<byte>();
            if (i + 1 < pending.Count)
            {
                JsonElement resp = pending[i + 1];
                string? b64 = resp.GetProperty("dataBase64").ValueKind == JsonValueKind.String
                    ? resp.GetProperty("dataBase64").GetString()
                    : null;
                if (b64 != null)
                {
                    expected = Convert.FromBase64String(b64);
                }
            }

            ControlResult result = ep0.Handle(setup, ReadOnlySpan<byte>.Empty);
            if (result.Status != 0)
            {
                Console.Error.WriteLine($"FAIL frame {req.GetProperty("frame").GetInt32()}: " +
                    $"request 0x{setup.Request:X2} stalled (status {result.Status}).");
                return 1;
            }

            if (!result.Data.AsSpan().SequenceEqual(expected))
            {
                Console.Error.WriteLine($"FAIL frame {req.GetProperty("frame").GetInt32()}: " +
                    $"request 0x{setup.Request:X2} returned {result.Data.Length} bytes, " +
                    $"expected {expected.Length}.");
                Console.Error.WriteLine($"  got:      {Convert.ToHexString(result.Data)}");
                Console.Error.WriteLine($"  expected: {Convert.ToHexString(expected)}");
                return 1;
            }

            checks++;
            Console.WriteLine($"OK   frame {req.GetProperty("frame").GetInt32(),4}: " +
                $"req 0x{setup.Request:X2} wValue 0x{setup.Value:X4} -> {result.Data.Length} bytes match");
        }

        // The capture ends by activating audio-streaming interface 1, alt 1.
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

        Console.WriteLine($"PASS: {checks} EP0 exchanges replay byte-exact; device configured, " +
            $"audio-streaming alt setting activated.");
        return 0;
    }
}
