/*
UsbDescriptorFixture -- freezes the byte-exact descriptors needed by the M2
virtual wired DualSense spike.

Usage:
  DSCompatProbe freezeusb <capture.pcap> <usbDeviceAddress> [outDir]
  DSCompatProbe verifyusb <fixtureDir>

USBPcap's --inject-descriptors supplies the device and complete configuration
descriptors even when capture starts after enumeration. HidSharp supplies the
HID report descriptor when the real controller is presently connected by USB.
Every parsed descriptor retains its raw bytes; verification reconstructs the
original streams and checks SHA-256 hashes so decoding can never silently
change the emulated wire image.
*/

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HidSharp;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace DSCompatProbe;

internal static class UsbDescriptorFixture
{
    private const ushort SonyVendorId = 0x054C;
    private const ushort DualSenseProductId = 0x0CE6;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int RunFreeze(string[] args)
    {
        if (args.Length < 2 || !ushort.TryParse(args[1], out ushort deviceAddress))
        {
            Console.Error.WriteLine("usage: DSCompatProbe freezeusb <capture.pcap> <usbDeviceAddress> [outDir]");
            return 2;
        }

        string pcapPath = Path.GetFullPath(args[0]);
        string outDir = Path.GetFullPath(args.Length > 2
            ? args[2]
            : Path.Combine(Path.GetDirectoryName(pcapPath)!, "descriptor_fixture"));
        PrepareOutputDirectory(outDir);

        List<ControlPacket> controls = ReadControlPackets(pcapPath, deviceAddress);
        byte[] device = FindRequestedDescriptor(controls, 0x01) ?? FindDescriptor(controls, 0x01);
        byte[] configuration = FindRequestedDescriptor(controls, 0x02) ?? FindDescriptor(controls, 0x02);
        if (device == null || configuration == null)
        {
            Console.Error.WriteLine("Capture does not contain injected device/configuration descriptors. " +
                "Capture again with capture-usb.ps1 -InjectDescriptors.");
            return 1;
        }

        var files = new List<FixtureFile>();
        files.Add(WriteFixtureFile(outDir, "device.bin", device, DecodeDevice(device)));
        files.Add(WriteFixtureFile(outDir, "configuration.bin", configuration,
            DecodeConfiguration(configuration)));

        int advertisedHidLength = GetAdvertisedHidReportLength(configuration);
        byte[] hidReport = FindRequestedDescriptor(controls, 0x22);
        string hidSource = hidReport == null ? null : "USBPcap control-transfer response";
        HubPort hubPort = null;
        string hubError = null;
        if (hidReport == null && TryOpenDualSenseHubPort(out hubPort, out hubError))
        {
            hidReport = ReadDescriptorFromHub(hubPort, 0x22, 0, 3, advertisedHidLength, 0x81);
            if (hidReport != null)
            {
                hidSource = $"IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION (parent hub, port {hubPort.Port})";
            }
        }
        else if (hidReport == null && hubError != null)
        {
            Console.Error.WriteLine($"Direct USB descriptor read unavailable: {hubError}");
        }
        byte[] reconstructedHid = null;
        reconstructedHid = TryReadWiredHidReportDescriptor(out string hidPath,
            out string manufacturer, out string product);
        if (hidReport != null)
        {
            files.Add(WriteFixtureFile(outDir, "hid-report.bin", hidReport,
                new { source = hidSource, length = hidReport.Length,
                    reportIds = DecodeHidReportIds(hidReport) }));

        }

        var capturedStrings = FindRequestedDescriptors(controls, 0x03);
        foreach ((UsbSetup setup, byte[] bytes) in capturedStrings)
        {
            int index = setup.WValue & 0xFF;
            files.Add(WriteFixtureFile(outDir, $"string-{index}-lang-{setup.WIndex:X4}.bin", bytes,
                new { index, languageId = $"0x{setup.WIndex:X4}", value = DecodeUsbString(bytes) }));
        }

        if (capturedStrings.Count == 0 && hubPort != null)
        {
            byte[] languages = ReadDescriptorFromHub(hubPort, 0x03, 0, 0, 255, 0x80);
            if (languages != null)
            {
                files.Add(WriteFixtureFile(outDir, "string-0-lang-0000.bin", languages,
                    new { index = 0, languageId = "0x0000", value = DecodeUsbString(languages) }));
                ushort language = languages.Length >= 4 ? BitConverter.ToUInt16(languages, 2) : (ushort)0x0409;
                foreach (int index in new[] { 1, 2 })
                {
                    byte[] value = ReadDescriptorFromHub(hubPort, 0x03, (byte)index, language, 255, 0x80);
                    if (value != null)
                    {
                        files.Add(WriteFixtureFile(outDir, $"string-{index}-lang-{language:X4}.bin", value,
                            new { index, languageId = $"0x{language:X4}", value = DecodeUsbString(value) }));
                    }
                }
            }
        }

        if (reconstructedHid != null)
        {
            // HidSharp reconstructs this from Windows HID parser metadata. It is
            // deliberately diagnostic-only: do not feed it to the USB emulator.
            WriteFixtureFile(outDir, "hid-report-windows-reconstructed.bin", reconstructedHid,
                new { source = "HidSharp/Windows reconstruction; not wire-exact",
                    length = reconstructedHid.Length, reportIds = DecodeHidReportIds(reconstructedHid) });
        }

        // Keep readable strings available even if enumeration did not request
        // them. These are marked constructed and are not part of byte-exact
        // fixture verification.
        WriteConstructedString(outDir, 0, "languages", new byte[] { 4, 3, 0x09, 0x04 });
        if (!string.IsNullOrEmpty(manufacturer))
            WriteConstructedString(outDir, 1, "manufacturer", EncodeUsbString(manufacturer));
        if (!string.IsNullOrEmpty(product))
            WriteConstructedString(outDir, 2, "product", EncodeUsbString(product));

        var exchanges = controls.Select(p => new
        {
            p.Frame,
            p.TimestampUtc,
            p.Endpoint,
            p.Status,
            p.Setup,
            dataLength = p.Data.Length,
            dataBase64 = p.Data.Length == 0 ? null : Convert.ToBase64String(p.Data),
        }).ToArray();
        File.WriteAllText(Path.Combine(outDir, "enumeration.json"),
            JsonSerializer.Serialize(exchanges, JsonOptions));

        bool complete = hidReport != null && hidReport.Length == advertisedHidLength;
        var manifest = new
        {
            schemaVersion = 1,
            sourcePcap = Path.GetFileName(pcapPath),
            deviceAddress,
            sourcePcapLastWriteUtc = File.GetLastWriteTimeUtc(pcapPath).ToString("O"),
            wiredHidInterfaceFound = hidPath != null,
            hidSource,
            complete,
            missing = complete ? Array.Empty<string>() : new[]
            {
                hidReport == null
                    ? $"{advertisedHidLength}-byte wire HID report descriptor (capture with -RestartDualSense; Windows reconstruction is not byte-exact)"
                    : $"HID descriptor mismatch: configuration advertises {advertisedHidLength} bytes, current wired device returned {hidReport.Length}; recapture injected descriptors from the current device"
            },
            files,
        };
        File.WriteAllText(Path.Combine(outDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));

        int verify = VerifyDirectory(outDir, requireComplete: false);
        Console.WriteLine($"Fixture written to {outDir}");
        Console.WriteLine(complete
            ? "M2.0 descriptor fixture is complete."
            : "Partial fixture: device/configuration are frozen; connect the pad by USB and rerun to add HID.");
        return verify;
    }

    public static int RunVerify(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: DSCompatProbe verifyusb <fixtureDir>");
            return 2;
        }

        return VerifyDirectory(Path.GetFullPath(args[0]), requireComplete: true);
    }

    private static int VerifyDirectory(string dir, bool requireComplete)
    {
        string manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Missing {manifestPath}");
            return 1;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        bool complete = doc.RootElement.GetProperty("complete").GetBoolean();
        int failures = 0;
        byte[] configuration = null;
        byte[] hidReport = null;
        foreach (JsonElement file in doc.RootElement.GetProperty("files").EnumerateArray())
        {
            string name = file.GetProperty("name").GetString()!;
            string expectedHash = file.GetProperty("sha256").GetString()!;
            string path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"FAIL missing {name}");
                failures++;
                continue;
            }

            byte[] bytes = File.ReadAllBytes(path);
            string actualHash = Hash(bytes);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"FAIL hash {name}: {actualHash} != {expectedHash}");
                failures++;
            }

            if (name == "device.bin")
            {
                failures += VerifyRoundTrip(name, bytes, DecodeDevice(bytes).Descriptors);
            }
            else if (name == "configuration.bin")
            {
                failures += VerifyRoundTrip(name, bytes, DecodeConfiguration(bytes).Descriptors);
                configuration = bytes;
            }
            else if (name == "hid-report.bin")
            {
                hidReport = bytes;
            }
        }

        if (configuration != null && hidReport != null)
        {
            int advertised = GetAdvertisedHidReportLength(configuration);
            if (hidReport.Length != advertised)
            {
                Console.Error.WriteLine($"FAIL HID length: configuration advertises {advertised}, file has {hidReport.Length}");
                failures++;
            }
        }

        if (requireComplete && !complete)
        {
            Console.Error.WriteLine("FAIL fixture is marked incomplete (wired HID report descriptor missing).");
            failures++;
        }

        if (failures == 0)
        {
            Console.WriteLine($"PASS: all fixture bytes round-trip and hashes match ({(complete ? "complete" : "partial")}).");
            return 0;
        }

        Console.Error.WriteLine($"FAIL: {failures} fixture verification error(s).");
        return 1;
    }

    private static void PrepareOutputDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            return;
        }

        string[] files = Directory.GetFiles(dir);
        if (files.Length == 0) return;
        if (!File.Exists(Path.Combine(dir, "manifest.json")))
        {
            throw new InvalidOperationException(
                $"Refusing to overwrite non-fixture directory {dir} (manifest.json not found).");
        }
        foreach (string file in files) File.Delete(file);
    }

    private static int VerifyRoundTrip(string name, byte[] original, IReadOnlyList<DecodedDescriptor> descriptors)
    {
        byte[] reconstructed = descriptors.SelectMany(d => Convert.FromHexString(d.RawHex)).ToArray();
        if (original.SequenceEqual(reconstructed))
        {
            return 0;
        }

        Console.Error.WriteLine($"FAIL round-trip {name}");
        return 1;
    }

    private static FixtureFile WriteFixtureFile(string outDir, string name, byte[] bytes, object decoded)
    {
        File.WriteAllBytes(Path.Combine(outDir, name), bytes);
        File.WriteAllText(Path.Combine(outDir, Path.ChangeExtension(name, ".json")),
            JsonSerializer.Serialize(decoded, JsonOptions));
        return new FixtureFile(name, bytes.Length, Hash(bytes));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] FindDescriptor(IEnumerable<ControlPacket> packets, byte descriptorType)
    {
        return packets.Select(p => p.Data)
            .FirstOrDefault(d => d.Length >= 2 && d[1] == descriptorType && d[0] >= 2);
    }

    private static List<ControlPacket> ReadControlPackets(string pcapPath, ushort deviceAddress)
    {
        using var stream = File.OpenRead(pcapPath);
        using var reader = new BinaryReader(stream);
        uint magic = reader.ReadUInt32();
        bool nanos = magic == 0xA1B23C4D;
        if (magic != 0xA1B2C3D4 && !nanos)
        {
            throw new InvalidDataException($"Unsupported pcap magic 0x{magic:X8}");
        }
        stream.Seek(20, SeekOrigin.Current);

        var result = new List<ControlPacket>();
        int frame = 0;
        while (stream.Position + 16 <= stream.Length)
        {
            frame++;
            uint seconds = reader.ReadUInt32();
            uint fraction = reader.ReadUInt32();
            uint included = reader.ReadUInt32();
            reader.ReadUInt32();
            if (included > int.MaxValue || stream.Position + included > stream.Length)
            {
                break;
            }
            byte[] packet = reader.ReadBytes((int)included);
            if (packet.Length < 28)
            {
                continue;
            }

            ushort headerLength = BitConverter.ToUInt16(packet, 0);
            ushort device = BitConverter.ToUInt16(packet, 19);
            byte endpoint = packet[21];
            byte transfer = packet[22];
            uint dataLength = BitConverter.ToUInt32(packet, 23);
            if (device != deviceAddress || transfer != 2 || headerLength + dataLength > packet.Length)
            {
                continue;
            }

            byte[] data = packet.AsSpan(headerLength, (int)dataLength).ToArray();
            UsbSetup setup = null;
            if (data.Length == 8 && (endpoint & 0x0F) == 0)
            {
                setup = new UsbSetup(data[0], data[1], BitConverter.ToUInt16(data, 2),
                    BitConverter.ToUInt16(data, 4), BitConverter.ToUInt16(data, 6));
            }

            long fractionalTicks = nanos ? fraction / 100L : fraction * 10L;
            long ticks = (long)seconds * TimeSpan.TicksPerSecond + fractionalTicks;
            result.Add(new ControlPacket(frame,
                DateTime.UnixEpoch.AddTicks(ticks).ToString("O"), endpoint,
                BitConverter.ToUInt64(packet, 2), BitConverter.ToUInt32(packet, 10),
                setup, setup == null ? data : Array.Empty<byte>()));
        }
        return result;
    }

    private static byte[] TryReadWiredHidReportDescriptor(out string path,
        out string manufacturer, out string product)
    {
        path = null;
        manufacturer = null;
        product = null;
        HidDevice device = DeviceList.Local.GetHidDevices(SonyVendorId, DualSenseProductId)
            .FirstOrDefault(d => d.DevicePath.Contains("vid_054c&pid_0ce6",
                StringComparison.OrdinalIgnoreCase));
        if (device == null)
        {
            return null;
        }

        path = device.DevicePath;
        try { manufacturer = device.GetManufacturer(); } catch { }
        try { product = device.GetProductName(); } catch { }
        try { return device.GetRawReportDescriptor(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read wired HID report descriptor: {ex.Message}");
            return null;
        }
    }

    private static bool TryOpenDualSenseHubPort(out HubPort hubPort, out string error)
    {
        hubPort = null;
        error = null;
        try
        {
            Guid usbDeviceGuid = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
            string controllerInstance = EnumerateInterfaces(usbDeviceGuid)
                .Select(GetInterfaceInstanceId)
                .FirstOrDefault(id => id != null &&
                    id.StartsWith("USB\\VID_054C&PID_0CE6\\", StringComparison.OrdinalIgnoreCase));
            if (controllerInstance == null)
            {
                error = "wired DualSense composite USB interface not found";
                return false;
            }

            if (NativeMethods.CM_Locate_DevNodeW(out uint controller, controllerInstance, 0) != 0 ||
                NativeMethods.CM_Get_Parent(out uint parentHub, controller, 0) != 0)
            {
                error = "could not locate DualSense parent USB hub";
                return false;
            }
            string parentId = GetDeviceId(parentHub);
            uint port = GetUInt32Property(controller,
                new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 30);

            Guid hubGuid = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");
            string hubPath = EnumerateInterfaces(hubGuid)
                .FirstOrDefault(path => string.Equals(GetInterfaceInstanceId(path), parentId,
                    StringComparison.OrdinalIgnoreCase));
            if (hubPath == null || port == 0)
            {
                error = $"could not map parent hub {parentId} / port {port} to an interface";
                return false;
            }
            hubPort = new HubPort(hubPath, port);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static byte[] ReadDescriptorFromHub(HubPort hubPort, byte descriptorType,
        byte descriptorIndex, ushort wIndex, int requestedLength, byte requestType)
    {
        const uint GenericWrite = 0x40000000;
        const uint FileShareWrite = 0x00000002;
        const uint OpenExisting = 3;
        const uint IoctlUsbGetDescriptorFromNodeConnection = 0x00220410;

        using SafeFileHandle handle = CreateFile(hubPort.HubPath, GenericWrite, FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Console.Error.WriteLine($"Could not open USB hub (Win32 {Marshal.GetLastWin32Error()}).");
            return null;
        }

        byte[] input = new byte[12 + requestedLength];
        byte[] output = new byte[input.Length];
        BitConverter.GetBytes(hubPort.Port).CopyTo(input, 0); // ConnectionIndex
        input[4] = requestType;                              // bmRequestType
        input[5] = 6;                                        // GET_DESCRIPTOR
        BitConverter.GetBytes((ushort)((descriptorType << 8) | descriptorIndex)).CopyTo(input, 6);
        BitConverter.GetBytes(wIndex).CopyTo(input, 8);
        BitConverter.GetBytes((ushort)requestedLength).CopyTo(input, 10);

        if (!DeviceIoControl(handle, IoctlUsbGetDescriptorFromNodeConnection,
                input, input.Length, output, output.Length, out uint returned, IntPtr.Zero))
        {
            Console.Error.WriteLine($"USB descriptor request type 0x{descriptorType:X2} failed " +
                $"(Win32 {Marshal.GetLastWin32Error()}).");
            return null;
        }
        if (returned <= 12) return null;
        int length = (int)returned - 12;
        if (descriptorType == 0x03 && output[12] >= 2)
            length = Math.Min(length, output[12]);
        return output.AsSpan(12, length).ToArray();
    }

    private static IEnumerable<string> EnumerateInterfaces(Guid guid)
    {
        uint length = 0;
        NativeMethods.CM_Get_Device_Interface_List_SizeW(out length, ref guid, null, 0);
        if (length <= 1) yield break;
        char[] buffer = new char[length];
        if (NativeMethods.CM_Get_Device_Interface_ListW(ref guid, null, buffer, length, 0) != 0)
            yield break;
        foreach (string value in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            yield return value;
    }

    private static string GetInterfaceInstanceId(string interfacePath)
    {
        var key = new NativeMethods.DEVPROPKEY
        {
            fmtid = new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), pid = 256,
        };
        uint size = 0;
        NativeMethods.CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, null, ref size, 0);
        if (size == 0) return null;
        byte[] bytes = new byte[size];
        return NativeMethods.CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, bytes, ref size, 0) == 0
            ? Encoding.Unicode.GetString(bytes).TrimEnd('\0') : null;
    }

    private static string GetDeviceId(uint devInst)
    {
        char[] chars = new char[512];
        return NativeMethods.CM_Get_Device_IDW(devInst, chars, (uint)chars.Length, 0) == 0
            ? new string(chars).TrimEnd('\0') : null;
    }

    private static uint GetUInt32Property(uint devInst, Guid fmtid, uint pid)
    {
        var key = new NativeMethods.DEVPROPKEY { fmtid = fmtid, pid = pid };
        uint size = 4;
        byte[] bytes = new byte[4];
        return NativeMethods.CM_Get_DevNode_PropertyW(devInst, ref key, out _, bytes, ref size, 0) == 0
            ? BitConverter.ToUInt32(bytes) : 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
        uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        [In, Out] byte[] inBuffer, int inBufferSize, [In, Out] byte[] outBuffer,
        int outBufferSize, out uint bytesReturned, IntPtr overlapped);

    private static byte[] FindRequestedDescriptor(IReadOnlyList<ControlPacket> packets, byte descriptorType)
    {
        return FindRequestedDescriptors(packets, descriptorType).Select(x => x.Bytes).FirstOrDefault();
    }

    private static List<(UsbSetup Setup, byte[] Bytes)> FindRequestedDescriptors(
        IReadOnlyList<ControlPacket> packets, byte descriptorType)
    {
        var result = new List<(UsbSetup, byte[])>();
        for (int i = 0; i < packets.Count; i++)
        {
            ControlPacket request = packets[i];
            if (request.Setup == null || request.Setup.BRequest != 6 ||
                (request.Setup.WValue >> 8) != descriptorType)
                continue;

            for (int j = i + 1; j < packets.Count; j++)
            {
                ControlPacket response = packets[j];
                if (response.Setup != null) continue;
                if (request.IrpId != 0 && response.IrpId != request.IrpId) continue;
                if ((response.Endpoint & 0x80) == 0 || response.Data.Length == 0) continue;
                result.Add((request.Setup, response.Data));
                break;
            }
        }
        return result;
    }

    private static void WriteConstructedString(string outDir, int index, string label, byte[] bytes)
    {
        string name = $"constructed-string-{index}-{label}.bin";
        File.WriteAllBytes(Path.Combine(outDir, name), bytes);
        File.WriteAllText(Path.Combine(outDir, Path.ChangeExtension(name, ".json")),
            JsonSerializer.Serialize(new { source = "constructed from Windows string value; not wire-captured",
                value = DecodeUsbString(bytes), rawHex = Convert.ToHexString(bytes) }, JsonOptions));
    }

    private static string DecodeUsbString(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[1] != 3) return null;
        if (bytes.Length == 4 && bytes[0] == 4) return "LANGID 0x" + BitConverter.ToUInt16(bytes, 2).ToString("X4");
        int length = Math.Min(bytes[0], bytes.Length);
        return Encoding.Unicode.GetString(bytes, 2, Math.Max(0, length - 2));
    }

    private static DecodedStream DecodeDevice(byte[] bytes)
    {
        if (bytes.Length != 18 || bytes[0] != 18 || bytes[1] != 1)
        {
            throw new InvalidDataException("Expected one 18-byte USB device descriptor.");
        }
        var fields = new Dictionary<string, object>
        {
            ["bcdUSB"] = $"0x{BitConverter.ToUInt16(bytes, 2):X4}",
            ["bDeviceClass"] = bytes[4], ["bDeviceSubClass"] = bytes[5],
            ["bDeviceProtocol"] = bytes[6], ["bMaxPacketSize0"] = bytes[7],
            ["idVendor"] = $"0x{BitConverter.ToUInt16(bytes, 8):X4}",
            ["idProduct"] = $"0x{BitConverter.ToUInt16(bytes, 10):X4}",
            ["bcdDevice"] = $"0x{BitConverter.ToUInt16(bytes, 12):X4}",
            ["iManufacturer"] = bytes[14], ["iProduct"] = bytes[15],
            ["iSerialNumber"] = bytes[16], ["bNumConfigurations"] = bytes[17],
        };
        return new DecodedStream(bytes.Length,
            new[] { new DecodedDescriptor(0, bytes.Length, 1, "Device", Convert.ToHexString(bytes), fields) });
    }

    private static DecodedStream DecodeConfiguration(byte[] bytes)
    {
        var descriptors = new List<DecodedDescriptor>();
        int offset = 0;
        while (offset < bytes.Length)
        {
            int length = bytes[offset];
            if (length < 2 || offset + length > bytes.Length)
            {
                throw new InvalidDataException($"Invalid descriptor length {length} at offset {offset}.");
            }
            byte type = bytes[offset + 1];
            byte[] raw = bytes.AsSpan(offset, length).ToArray();
            var fields = new Dictionary<string, object>();
            string name = type switch
            {
                2 => "Configuration", 4 => "Interface", 5 => "Endpoint",
                0x21 => "HID", 0x24 => "ClassSpecificInterface",
                0x25 => "ClassSpecificEndpoint", _ => $"Type0x{type:X2}",
            };
            if (type == 2 && length >= 9)
            {
                fields["wTotalLength"] = BitConverter.ToUInt16(raw, 2);
                fields["bNumInterfaces"] = raw[4]; fields["bConfigurationValue"] = raw[5];
                fields["bmAttributes"] = $"0x{raw[7]:X2}"; fields["bMaxPower"] = raw[8];
            }
            else if (type == 4 && length >= 9)
            {
                fields["bInterfaceNumber"] = raw[2]; fields["bAlternateSetting"] = raw[3];
                fields["bNumEndpoints"] = raw[4]; fields["bInterfaceClass"] = $"0x{raw[5]:X2}";
                fields["bInterfaceSubClass"] = $"0x{raw[6]:X2}";
                fields["bInterfaceProtocol"] = $"0x{raw[7]:X2}";
            }
            else if (type == 5 && length >= 7)
            {
                fields["bEndpointAddress"] = $"0x{raw[2]:X2}";
                fields["bmAttributes"] = $"0x{raw[3]:X2}";
                fields["wMaxPacketSize"] = BitConverter.ToUInt16(raw, 4);
                fields["bInterval"] = raw[6];
            }
            else if (type == 0x21 && length >= 9)
            {
                fields["bcdHID"] = $"0x{BitConverter.ToUInt16(raw, 2):X4}";
                fields["reportDescriptorLength"] = BitConverter.ToUInt16(raw, 7);
            }
            descriptors.Add(new DecodedDescriptor(offset, length, type, name,
                Convert.ToHexString(raw), fields));
            offset += length;
        }

        if (descriptors[0].Type != 2 || BitConverter.ToUInt16(bytes, 2) != bytes.Length)
        {
            throw new InvalidDataException("Configuration wTotalLength does not match captured bytes.");
        }
        return new DecodedStream(bytes.Length, descriptors);
    }

    private static int GetAdvertisedHidReportLength(byte[] configuration)
    {
        DecodedDescriptor hid = DecodeConfiguration(configuration).Descriptors
            .FirstOrDefault(d => d.Type == 0x21);
        if (hid == null || !hid.Fields.TryGetValue("reportDescriptorLength", out object value))
        {
            throw new InvalidDataException("Configuration has no HID descriptor/report length.");
        }
        return Convert.ToInt32(value);
    }

    private static string[] DecodeHidReportIds(byte[] descriptor)
    {
        var ids = new SortedSet<byte>();
        for (int i = 0; i + 1 < descriptor.Length; i++)
        {
            if (descriptor[i] == 0x85) ids.Add(descriptor[++i]);
        }
        return ids.Select(id => $"0x{id:X2}").ToArray();
    }

    private static byte[] EncodeUsbString(string value)
    {
        byte[] text = Encoding.Unicode.GetBytes(value);
        if (text.Length > 252) throw new InvalidDataException("USB string is too long.");
        return new[] { (byte)(text.Length + 2), (byte)3 }.Concat(text).ToArray();
    }

    private sealed record FixtureFile(string Name, int Length, string Sha256);
    private sealed record HubPort(string HubPath, uint Port);
    private sealed record ControlPacket(int Frame, string TimestampUtc, byte Endpoint,
        ulong IrpId, uint Status, UsbSetup Setup, byte[] Data);
    private sealed record UsbSetup(byte BmRequestType, byte BRequest, ushort WValue,
        ushort WIndex, ushort WLength);
    private sealed record DecodedStream(int Length, IReadOnlyList<DecodedDescriptor> Descriptors);
    private sealed record DecodedDescriptor(int Offset, int Length, byte Type, string Name,
        string RawHex, IReadOnlyDictionary<string, object> Fields);
}
