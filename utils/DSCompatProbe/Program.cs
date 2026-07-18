/*
DSCompatProbe — Milestone 0 association-probe fixture collector.

Dumps how Windows sees the DualSense (and everything else on the HID/audio/USB
device-interface classes): every devnode property, full ancestor chains,
container IDs, all MMDevice endpoints with complete property stores, audio
client formats, and HID identity/report capabilities.

Principle (per design review): enumerate every property first, normalize the
known association keys second, never filter raw acquisition to what we
currently understand. Output: probe_runs/<timestamp>/*.json

See doc/M0_COMPAT_LAB_RUNBOOK.md for the full fixture procedure.
*/

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HidSharp;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DSCompatProbe;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("parsepcap", StringComparison.OrdinalIgnoreCase))
        {
            return PcapIsoExtractor.Run(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0].Equals("freezeusb", StringComparison.OrdinalIgnoreCase))
        {
            return UsbDescriptorFixture.RunFreeze(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0].Equals("verifyusb", StringComparison.OrdinalIgnoreCase))
        {
            return UsbDescriptorFixture.RunVerify(args.Skip(1).ToArray());
        }

        string outDir = Path.Combine(AppContext.BaseDirectory, "probe_runs",
            DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"Writing fixtures to {outDir}");

        WriteJson(outDir, "run.json", new
        {
            probeVersion = "1.0",
            timestampUtc = DateTime.UtcNow.ToString("o"),
            osVersion = Environment.OSVersion.VersionString,
            is64Bit = Environment.Is64BitOperatingSystem,
            args,
        });

        var devnodeCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("Enumerating device interfaces...");
        var interfaces = CollectInterfaces(devnodeCache);
        WriteJson(outDir, "interfaces.json", interfaces);

        Console.WriteLine($"Collected {devnodeCache.Count} devnodes (with ancestors).");
        WriteJson(outDir, "devnodes.json", devnodeCache);

        Console.WriteLine("Enumerating MMDevice endpoints...");
        WriteJson(outDir, "mmdevices.json", CollectMMDevices());

        Console.WriteLine("Enumerating HID devices...");
        WriteJson(outDir, "hid.json", CollectHid());

        Console.WriteLine("Done.");
        return 0;
    }

    private static void WriteJson(string dir, string name, object payload)
    {
        File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(payload, JsonOpts));
    }

    // ---------------------------------------------------------------- interfaces

    private static readonly (string Name, Guid Guid)[] InterfaceClasses =
    {
        ("GUID_DEVINTERFACE_HID", new Guid("4D1E55B2-F16F-11CF-88CB-001111000030")),
        ("KSCATEGORY_AUDIO", new Guid("6994AD04-93EF-11D0-A3CC-00A0C9223196")),
        ("KSCATEGORY_RENDER", new Guid("65E8773E-8F56-11D0-A3B9-00A0C9223196")),
        ("KSCATEGORY_CAPTURE", new Guid("65E8773D-8F56-11D0-A3B9-00A0C9223196")),
        ("KSCATEGORY_TOPOLOGY", new Guid("DDA54A40-1E4C-11D1-A050-405705C10000")),
        ("GUID_DEVINTERFACE_USB_DEVICE", new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED")),
        ("GUID_DEVINTERFACE_USB_HUB", new Guid("F18A0E88-C30C-11D0-8815-00A0C906BED8")),
        ("GUID_DEVINTERFACE_USB_HOST_CONTROLLER", new Guid("3ABF6F2D-71C4-462A-8A92-1E6861E6AF27")),
    };

    private static List<object> CollectInterfaces(Dictionary<string, object> devnodeCache)
    {
        var result = new List<object>();
        foreach ((string className, Guid classGuid) in InterfaceClasses)
        {
            foreach (string ifacePath in GetInterfaceList(classGuid))
            {
                string instanceId = GetInterfaceInstanceId(ifacePath);
                if (instanceId != null)
                {
                    CollectDevnodeWithAncestors(instanceId, devnodeCache);
                }

                result.Add(new
                {
                    interfaceClass = className,
                    interfaceClassGuid = classGuid.ToString(),
                    path = ifacePath,
                    deviceInstanceId = instanceId,
                });
            }
        }

        return result;
    }

    private static IEnumerable<string> GetInterfaceList(Guid classGuid)
    {
        const uint CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES = 1;
        Guid guid = classGuid;
        if (NativeMethods.CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, null,
                CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES) != 0 || len <= 1)
        {
            yield break;
        }

        char[] buffer = new char[len];
        if (NativeMethods.CM_Get_Device_Interface_ListW(ref guid, null, buffer, len,
                CM_GET_DEVICE_INTERFACE_LIST_ALL_DEVICES) != 0)
        {
            yield break;
        }

        foreach (string entry in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return entry;
        }
    }

    private static readonly NativeMethods.DEVPROPKEY DevpkeyDeviceInstanceId = new()
    {
        fmtid = new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"),
        pid = 256,
    };

    private static string GetInterfaceInstanceId(string interfacePath)
    {
        var key = DevpkeyDeviceInstanceId;
        uint size = 0;
        NativeMethods.CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, null, ref size, 0);
        if (size == 0)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        if (NativeMethods.CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, buffer, ref size, 0) != 0)
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    // ---------------------------------------------------------------- devnodes

    private static void CollectDevnodeWithAncestors(string instanceId, Dictionary<string, object> cache)
    {
        const uint CM_LOCATE_DEVNODE_PHANTOM = 1;
        if (cache.ContainsKey(instanceId))
        {
            return;
        }

        if (NativeMethods.CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != 0)
        {
            cache[instanceId] = new { error = "CM_Locate_DevNode failed" };
            return;
        }

        while (true)
        {
            string id = GetDeviceId(devInst) ?? $"<unknown devinst {devInst}>";
            if (!cache.ContainsKey(id))
            {
                cache[id] = DumpDevnode(devInst, id);
            }

            if (NativeMethods.CM_Get_Parent(out uint parent, devInst, 0) != 0)
            {
                break;
            }

            devInst = parent;
        }
    }

    private static string GetDeviceId(uint devInst)
    {
        char[] buffer = new char[400];
        if (NativeMethods.CM_Get_Device_IDW(devInst, buffer, (uint)buffer.Length, 0) != 0)
        {
            return null;
        }

        return new string(buffer).TrimEnd('\0');
    }

    private static object DumpDevnode(uint devInst, string instanceId)
    {
        var properties = new List<object>();
        uint count = 0;
        NativeMethods.CM_Get_DevNode_Property_Keys(devInst, null, ref count, 0);
        if (count > 0)
        {
            var keys = new NativeMethods.DEVPROPKEY[count];
            if (NativeMethods.CM_Get_DevNode_Property_Keys(devInst, keys, ref count, 0) == 0)
            {
                for (int i = 0; i < count; i++)
                {
                    properties.Add(DumpDevnodeProperty(devInst, keys[i]));
                }
            }
        }

        string parentId = null;
        if (NativeMethods.CM_Get_Parent(out uint parent, devInst, 0) == 0)
        {
            parentId = GetDeviceId(parent);
        }

        return new
        {
            instanceId,
            parentInstanceId = parentId,
            properties,
        };
    }

    private static object DumpDevnodeProperty(uint devInst, NativeMethods.DEVPROPKEY key)
    {
        uint size = 0;
        var k = key;
        NativeMethods.CM_Get_DevNode_PropertyW(devInst, ref k, out uint propType, null, ref size, 0);
        byte[] buffer = Array.Empty<byte>();
        if (size > 0)
        {
            buffer = new byte[size];
            if (NativeMethods.CM_Get_DevNode_PropertyW(devInst, ref k, out propType, buffer, ref size, 0) != 0)
            {
                buffer = Array.Empty<byte>();
            }
        }

        return new
        {
            fmtid = key.fmtid.ToString(),
            pid = key.pid,
            symbolicName = DevPropNames.Lookup(key),
            devpropType = $"0x{propType:X}",
            decodedValue = DecodeDevProp(propType, buffer),
            rawBase64 = buffer.Length > 0 && DecodeDevProp(propType, buffer) == null
                ? Convert.ToBase64String(buffer) : null,
        };
    }

    private static object DecodeDevProp(uint propType, byte[] data)
    {
        try
        {
            switch (propType)
            {
                case 0x12: // DEVPROP_TYPE_STRING
                    return Encoding.Unicode.GetString(data).TrimEnd('\0');
                case 0x2012: // DEVPROP_TYPE_STRING_LIST
                    return Encoding.Unicode.GetString(data)
                        .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                case 0x0D: // GUID
                    return data.Length >= 16 ? new Guid(data.AsSpan(0, 16).ToArray()).ToString() : null;
                case 0x07: // UINT32
                    return data.Length >= 4 ? BitConverter.ToUInt32(data) : null;
                case 0x06: // INT32
                    return data.Length >= 4 ? BitConverter.ToInt32(data) : null;
                case 0x05: // UINT16
                    return data.Length >= 2 ? BitConverter.ToUInt16(data) : null;
                case 0x11: // BOOLEAN
                    return data.Length >= 1 ? data[0] != 0 : null;
                case 0x10: // FILETIME
                    return data.Length >= 8
                        ? DateTime.FromFileTimeUtc(BitConverter.ToInt64(data)).ToString("o") : null;
                case 0x1003: // BINARY (BYTE | ARRAY)
                    return Convert.ToBase64String(data);
                default:
                    return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- MMDevices

    private static object CollectMMDevices()
    {
        var endpoints = new List<object>();
        using var enumerator = new MMDeviceEnumerator();

        var defaults = new Dictionary<string, string>();
        foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                try
                {
                    using MMDevice def = enumerator.GetDefaultAudioEndpoint(flow, role);
                    defaults[$"{flow}/{role}"] = def.ID;
                }
                catch (Exception) { }
            }
        }

        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
        {
            var props = new List<object>();
            try
            {
                var store = device.Properties;
                for (int i = 0; i < store.Count; i++)
                {
                    try
                    {
                        var kv = store[i];
                        var key = store.Get(i);
                        object value;
                        try
                        {
                            value = kv.Value switch
                            {
                                byte[] bytes => (object)Convert.ToBase64String(bytes),
                                null => null,
                                _ => kv.Value.ToString(),
                            };
                        }
                        catch (Exception ex)
                        {
                            value = $"<undecodable: {ex.GetType().Name}>";
                        }

                        props.Add(new
                        {
                            fmtid = key.formatId.ToString(),
                            pid = key.propertyId,
                            symbolicName = DevPropNames.LookupPkey(key.formatId, key.propertyId),
                            value,
                        });
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }

            object audioClientInfo = null;
            object sessions = null;
            if (device.State == DeviceState.Active)
            {
                audioClientInfo = ProbeAudioClient(device);
                sessions = ProbeSessions(device);
            }

            string friendly = null;
            try { friendly = device.FriendlyName; } catch (Exception) { }

            endpoints.Add(new
            {
                id = device.ID,
                dataFlow = device.DataFlow.ToString(),
                state = device.State.ToString(),
                friendlyName = friendly,
                properties = props,
                audioClient = audioClientInfo,
                sessions,
            });

            device.Dispose();
        }

        return new { defaults, endpoints };
    }

    /// <summary>
    /// Active audio sessions per endpoint — the PID↔endpoint mapping is often
    /// the most direct evidence of which device a game selected.
    /// </summary>
    private static object ProbeSessions(MMDevice device)
    {
        try
        {
            var list = new List<object>();
            var mgr = device.AudioSessionManager;
            var collection = mgr.Sessions;
            for (int i = 0; i < collection.Count; i++)
            {
                try
                {
                    var session = collection[i];
                    string processName = null;
                    uint pid = 0;
                    try
                    {
                        pid = session.GetProcessID;
                        if (pid != 0)
                        {
                            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                            processName = proc.ProcessName;
                        }
                    }
                    catch (Exception) { }

                    list.Add(new
                    {
                        pid,
                        processName,
                        state = session.State.ToString(),
                        displayName = session.DisplayName,
                        identifier = TryGet(() => session.GetSessionIdentifier),
                        instanceIdentifier = TryGet(() => session.GetSessionInstanceIdentifier),
                        isSystemSounds = session.IsSystemSoundsSession,
                    });
                }
                catch (Exception) { }
            }

            return list;
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private static string TryGet(Func<string> getter)
    {
        try { return getter(); } catch (Exception) { return null; }
    }

    private static object ProbeAudioClient(MMDevice device)
    {
        try
        {
            var client = device.AudioClient;
            var formatsToProbe = new (string Name, WaveFormat Format)[]
            {
                ("2ch48k16", new WaveFormat(48000, 16, 2)),
                ("4ch48k16", new WaveFormat(48000, 16, 4)),
                ("4ch48kFloat", WaveFormat.CreateIeeeFloatWaveFormat(48000, 4)),
            };

            var supported = new Dictionary<string, object>();
            foreach ((string name, WaveFormat fmt) in formatsToProbe)
            {
                bool shared = false, exclusive = false;
                try { shared = client.IsFormatSupported(AudioClientShareMode.Shared, fmt); } catch (Exception) { }
                try { exclusive = client.IsFormatSupported(AudioClientShareMode.Exclusive, fmt); } catch (Exception) { }
                supported[name] = new { shared, exclusive };
            }

            return new
            {
                mixFormat = DescribeFormat(client.MixFormat),
                defaultDevicePeriodHns = client.DefaultDevicePeriod,
                minimumDevicePeriodHns = client.MinimumDevicePeriod,
                formatSupport = supported,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private static object DescribeFormat(WaveFormat f)
    {
        if (f == null)
        {
            return null;
        }

        return new
        {
            encoding = f.Encoding.ToString(),
            sampleRate = f.SampleRate,
            channels = f.Channels,
            bitsPerSample = f.BitsPerSample,
            blockAlign = f.BlockAlign,
            averageBytesPerSecond = f.AverageBytesPerSecond,
        };
    }

    // ---------------------------------------------------------------- HID

    private static object CollectHid()
    {
        var devices = new List<object>();
        foreach (HidDevice dev in DeviceList.Local.GetHidDevices())
        {
            bool isSony = dev.VendorID == 0x054C;
            string product = null, manufacturer = null, serial = null;
            int maxIn = -1, maxOut = -1, maxFeature = -1;
            object reports = null;

            try { product = dev.GetProductName(); } catch (Exception) { }
            try { manufacturer = dev.GetManufacturer(); } catch (Exception) { }
            try { maxIn = dev.GetMaxInputReportLength(); } catch (Exception) { }
            try { maxOut = dev.GetMaxOutputReportLength(); } catch (Exception) { }
            try { maxFeature = dev.GetMaxFeatureReportLength(); } catch (Exception) { }

            if (isSony)
            {
                try { serial = dev.GetSerialNumber(); } catch (Exception) { }
                try
                {
                    reports = dev.GetReportDescriptor().Reports.Select(r => new
                    {
                        reportId = $"0x{r.ReportID:X2}",
                        type = r.ReportType.ToString(),
                        length = r.Length,
                    }).ToList();
                }
                catch (Exception) { }
            }

            devices.Add(new
            {
                path = dev.DevicePath,
                vendorId = $"0x{dev.VendorID:X4}",
                productId = $"0x{dev.ProductID:X4}",
                releaseNumberBcd = dev.ReleaseNumberBcd,
                isSony,
                product,
                manufacturer,
                serial,
                maxInputReportLength = maxIn,
                maxOutputReportLength = maxOut,
                maxFeatureReportLength = maxFeature,
                reports,
            });
        }

        return devices;
    }
}

/// <summary>Symbolic names for the DEVPKEYs/PKEYs that matter for association analysis.</summary>
internal static class DevPropNames
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // DEVPKEY_Device_* (fmtid a45c254e-df1c-4efd-8020-67d146a850e0)
        ["a45c254e-df1c-4efd-8020-67d146a850e0/2"] = "DEVPKEY_Device_DeviceDesc",
        ["a45c254e-df1c-4efd-8020-67d146a850e0/3"] = "DEVPKEY_Device_HardwareIds",
        ["a45c254e-df1c-4efd-8020-67d146a850e0/4"] = "DEVPKEY_Device_CompatibleIds",
        ["a45c254e-df1c-4efd-8020-67d146a850e0/6"] = "DEVPKEY_Device_Service",
        ["a45c254e-df1c-4efd-8020-67d146a850e0/9"] = "DEVPKEY_Device_Class",
        ["a45c254e-df1c-4efd-8020-67d146a850e0/10"] = "DEVPKEY_Device_ClassGuid",
        ["a45c254e-df1c-4efd-8020-67d146a850e0/11"] = "DEVPKEY_Device_Driver",
        ["a45c254e-df1c-4efd-8020-67d146a850e0/13"] = "DEVPKEY_Device_Manufacturer",
        ["a45c254e-df1c-4efd-8020-67d146a850e0/14"] = "DEVPKEY_Device_FriendlyName",
        ["a45c254e-df1c-4efd-8020-67d146a850e0/15"] = "DEVPKEY_Device_LocationInfo",
        ["a45c254e-df1c-4efd-8020-67d146a850e0/24"] = "DEVPKEY_Device_EnumeratorName",
        // Topology / relations (fmtid 4340a6c5-93fa-4706-972c-7b648008a5a7)
        ["4340a6c5-93fa-4706-972c-7b648008a5a7/4"] = "DEVPKEY_Device_BusReportedDeviceDesc",
        ["4340a6c5-93fa-4706-972c-7b648008a5a7/8"] = "DEVPKEY_Device_Parent",
        ["4340a6c5-93fa-4706-972c-7b648008a5a7/9"] = "DEVPKEY_Device_Children",
        ["4340a6c5-93fa-4706-972c-7b648008a5a7/10"] = "DEVPKEY_Device_Siblings",
        // Container
        ["8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c/2"] = "DEVPKEY_Device_ContainerId",
        // Instance id
        ["78c34fc8-104a-4aca-9ea4-524d52996e57/256"] = "DEVPKEY_Device_InstanceId",
        // Audio endpoint PKEYs (fmtid 1da5d803-d492-4edd-8c23-e0c0ffee7f0e)
        ["1da5d803-d492-4edd-8c23-e0c0ffee7f0e/0"] = "PKEY_AudioEndpoint_FormFactor",
        ["1da5d803-d492-4edd-8c23-e0c0ffee7f0e/2"] = "PKEY_AudioEndpoint_Association",
        ["1da5d803-d492-4edd-8c23-e0c0ffee7f0e/3"] = "PKEY_AudioEndpoint_PhysicalSpeakers",
        ["1da5d803-d492-4edd-8c23-e0c0ffee7f0e/4"] = "PKEY_AudioEndpoint_GUID",
        ["1da5d803-d492-4edd-8c23-e0c0ffee7f0e/8"] = "PKEY_AudioEndpoint_JackSubType",
        // Audio engine formats
        ["f19f064d-082c-4e27-bc73-6882a1bb8e4c/0"] = "PKEY_AudioEngine_DeviceFormat",
        ["e4870e26-3cc5-4cd2-ba46-ca0a9a70ed04/3"] = "PKEY_AudioEngine_OEMFormat",
        // Shell friendly name on endpoints
        ["b725f130-47ef-101a-a5f1-02608c9eebac/10"] = "PKEY_ItemNameDisplay",
    };

    public static string Lookup(NativeMethods.DEVPROPKEY key) =>
        Map.TryGetValue($"{key.fmtid}/{key.pid}", out string name) ? name : null;

    public static string LookupPkey(Guid fmtid, int pid) =>
        Map.TryGetValue($"{fmtid}/{pid}", out string name) ? name : null;
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid interfaceClassGuid,
        string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string deviceId,
        char[] buffer, uint bufferLen, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_PropertyW(string deviceInterface, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[] buffer, ref uint bufferSize, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, uint bufferLen, uint flags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Property_Keys(uint devInst, [Out] DEVPROPKEY[] propertyKeyArray,
        ref uint propertyKeyCount, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY propertyKey,
        out uint propertyType, byte[] buffer, ref uint bufferSize, uint flags);
}
