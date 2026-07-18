using System.Buffers.Binary;
using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace VirtualDualSenseUsbip.Live;

/// <summary>
/// Reads authenticated 78-byte input reports from a real Bluetooth DualSense
/// and publishes their shared 63-byte payload as wired USB report 0x01.
/// </summary>
public sealed class BluetoothDualSenseInputSource : IInputReportSource, IDisposable
{
    private const int SonyVendorId = 0x054C;
    private const int DualSenseProductId = 0x0CE6;
    private const int DualSenseEdgeProductId = 0x0DF2;
    private const int BluetoothInputLength = 78;
    private const int UsbInputLength = 64;
    private const byte BluetoothInputCrcSeed = 0xA1;
    private const byte BluetoothFeatureCrcSeed = 0xA3;

    private readonly SafeFileHandle handle;
    private readonly int inputReportLength;
    private readonly Action<string> log;
    private readonly Thread readThread;
    private byte[] latestReport = new NeutralInputReportSource().CreateReport(UsbInputLength);
    private long validReportCount;
    private long invalidReportCount;
    private int disposed;

    public string Description { get; }
    public byte[]? CalibrationFeatureReport { get; }
    public long ValidReportCount => Interlocked.Read(ref validReportCount);
    public long InvalidReportCount => Interlocked.Read(ref invalidReportCount);

    private BluetoothDualSenseInputSource(HidDevice device, SafeFileHandle handle,
        int inputReportLength, Action<string> log)
    {
        this.handle = handle;
        this.inputReportLength = inputReportLength;
        this.log = log;
        Description = $"Bluetooth DualSense (PID 0x{device.ProductID:X4})";
        CalibrationFeatureReport = ReadUsbCalibrationFeature(handle, log);
        readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "Virtual DualSense Bluetooth input",
        };
        readThread.Start();
    }

    public static BluetoothDualSenseInputSource Open(Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Bluetooth DualSense input requires Windows HID.");
        }

        Action<string> logger = log ?? (_ => { });
        foreach (HidDevice device in DeviceList.Local.GetHidDevices(SonyVendorId))
        {
            if (device.ProductID is not (DualSenseProductId or DualSenseEdgeProductId))
            {
                continue;
            }

            int maxOutput;
            int maxInput;
            try
            {
                maxOutput = device.GetMaxOutputReportLength();
                maxInput = device.GetMaxInputReportLength();
            }
            catch
            {
                continue;
            }

            // The physical Bluetooth collection exposes 547-byte streaming
            // output reports. The virtual wired device exposes only 48 bytes.
            if (maxOutput < 100 || maxInput < BluetoothInputLength)
            {
                continue;
            }

            SafeFileHandle handle = NativeMethods.OpenDevice(device.DevicePath);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException($"Could not open the Bluetooth DualSense (Win32 error {error}). " +
                    "Close DS4Windows, DSX, and Steam input, then retry.");
            }

            logger($"Opened Bluetooth DualSense input (PID 0x{device.ProductID:X4}, " +
                $"input {maxInput} bytes). Hardware address is not logged or persisted.");
            return new BluetoothDualSenseInputSource(device, handle, maxInput, logger);
        }

        throw new InvalidOperationException("No physical Bluetooth DualSense with streaming reports was found.");
    }

    public byte[] CreateReport(int requestedLength)
    {
        byte[] current = Volatile.Read(ref latestReport);
        return current.AsSpan(0, Math.Clamp(requestedLength, 0, UsbInputLength)).ToArray();
    }

    internal static bool TryConvertBluetoothReport(ReadOnlySpan<byte> bluetoothReport,
        out byte[] usbReport)
    {
        usbReport = Array.Empty<byte>();
        if (bluetoothReport.Length < BluetoothInputLength || bluetoothReport[0] != 0x31 ||
            !HasValidBluetoothCrc(bluetoothReport[..BluetoothInputLength], BluetoothInputCrcSeed))
        {
            return false;
        }

        usbReport = new byte[UsbInputLength];
        usbReport[0] = 0x01;
        bluetoothReport.Slice(2, UsbInputLength - 1).CopyTo(usbReport.AsSpan(1));
        return true;
    }

    private void ReadLoop()
    {
        byte[] buffer = new byte[inputReportLength];
        while (Volatile.Read(ref disposed) == 0)
        {
            if (!NativeMethods.ReadFile(handle, buffer, (uint)buffer.Length,
                    out uint bytesRead, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (Volatile.Read(ref disposed) == 0)
                {
                    log($"Bluetooth input stopped after {ValidReportCount} valid reports " +
                        $"(Win32 error {error}).");
                }
                return;
            }

            if (bytesRead < BluetoothInputLength ||
                !TryConvertBluetoothReport(buffer.AsSpan(0, (int)bytesRead), out byte[] usbReport))
            {
                Interlocked.Increment(ref invalidReportCount);
                continue;
            }

            Volatile.Write(ref latestReport, usbReport);
            Interlocked.Increment(ref validReportCount);
        }
    }

    private static byte[]? ReadUsbCalibrationFeature(SafeFileHandle handle, Action<string> log)
    {
        byte[] calibration = new byte[41];
        calibration[0] = 0x05;
        if (!NativeMethods.HidD_GetFeature(handle, calibration, (uint)calibration.Length))
        {
            log($"Bluetooth calibration feature 0x05 was unavailable " +
                $"(Win32 error {Marshal.GetLastWin32Error()}); the virtual report will stall.");
            return null;
        }
        if (!HasValidBluetoothCrc(calibration, BluetoothFeatureCrcSeed))
        {
            log("Bluetooth calibration feature 0x05 failed CRC validation; the virtual report will stall.");
            return null;
        }

        // USB uses the same 41-byte calibration payload but does not authenticate
        // its final four transport bytes. Preserve real calibration and clear the
        // Bluetooth-only CRC before serving it from the virtual wired device.
        calibration.AsSpan(calibration.Length - 4).Clear();
        log("Loaded physical calibration for runtime forwarding; values are not logged or persisted.");
        return calibration;
    }

    private static bool HasValidBluetoothCrc(ReadOnlySpan<byte> report, byte seed)
    {
        if (report.Length < 5)
        {
            return false;
        }

        int crcOffset = report.Length - 4;
        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(report[crcOffset..]);
        uint state = 0xFFFFFFFF;
        state = UpdateCrc32(state, seed);
        foreach (byte value in report[..crcOffset])
        {
            state = UpdateCrc32(state, value);
        }
        return expected == ~state;
    }

    private static uint UpdateCrc32(uint state, byte value)
    {
        state ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            state = (state & 1) != 0 ? 0xEDB88320U ^ (state >> 1) : state >> 1;
        }
        return state;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        _ = NativeMethods.CancelIoEx(handle, IntPtr.Zero);
        if (Thread.CurrentThread != readThread)
        {
            _ = readThread.Join(TimeSpan.FromSeconds(2));
        }
        handle.Dispose();
        GC.SuppressFinalize(this);
    }

    private static class NativeMethods
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint ShareReadWrite = 0x00000003;
        private const uint OpenExisting = 3;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
            uint shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(SafeFileHandle file, byte[] buffer,
            uint bytesToRead, out uint bytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject,
            byte[] reportBuffer, uint reportBufferLength);

        internal static SafeFileHandle OpenDevice(string devicePath) =>
            CreateFile(devicePath, GenericRead | GenericWrite, ShareReadWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
    }
}
