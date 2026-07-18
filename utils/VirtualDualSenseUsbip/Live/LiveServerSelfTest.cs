using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using VirtualDualSenseUsbip.Device;
using VirtualDualSenseUsbip.Protocol;

namespace VirtualDualSenseUsbip.Live;

public static class LiveServerSelfTest
{
    private const uint DeviceId = 0x00010001;

    public static async Task RunAsync(string fixturesDir)
    {
        Console.WriteLine("servertest: Bluetooth-to-USB input conversion");
        TestBluetoothInputConversion();

        Console.WriteLine("servertest: derive HID-only configuration");
        DescriptorSet descriptors = DescriptorSet.LoadHidOnlyFromFixtures(fixturesDir);
        Require(descriptors.NumInterfaces == 1 && descriptors.HidInterfaceNumber == 0,
            "HID-only descriptor set did not expose exactly interface zero");
        Require(descriptors.ConfigurationDescriptorLength == 41,
            $"unexpected HID-only configuration length {descriptors.ConfigurationDescriptorLength}");

        using var server = new VirtualDualSenseServer(descriptors,
            new VirtualDualSenseServerOptions(Port: 0,
                InputInterval: TimeSpan.FromMilliseconds(200)));
        var captured = new TaskCompletionSource<HidOutputCapture>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.HidOutputReceived += output => captured.TrySetResult(output);
        server.Start();

        using var cancellation = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cancellation.Token);
        try
        {
            Console.WriteLine("servertest: OP_REQ_DEVLIST");
            await TestDeviceListAsync(server.Port);

            Console.WriteLine("servertest: OP_REQ_IMPORT + EP0 + interrupt endpoints");
            await TestImportSessionAsync(server.Port, captured.Task);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        Console.WriteLine("PASS: live USB/IP management, HID-only EP0, interrupt IN/OUT, and UNLINK.");
    }

    private static void TestBluetoothInputConversion()
    {
        byte[] bluetooth = new byte[78];
        bluetooth[0] = 0x31;
        bluetooth[1] = 0x10;
        for (int i = 2; i < 74; i++)
        {
            bluetooth[i] = (byte)(i * 3);
        }
        uint crc = ComputeBluetoothCrc(0xA1, bluetooth.AsSpan(0, 74));
        BinaryPrimitives.WriteUInt32LittleEndian(bluetooth.AsSpan(74), crc);

        Require(BluetoothDualSenseInputSource.TryConvertBluetoothReport(
                bluetooth, out byte[] usb) &&
                usb.Length == 64 && usb[0] == 0x01 &&
                usb.AsSpan(1).SequenceEqual(bluetooth.AsSpan(2, 63)),
            "Bluetooth input did not map byte-exact to USB report 0x01");

        bluetooth[10] ^= 0x01;
        Require(!BluetoothDualSenseInputSource.TryConvertBluetoothReport(bluetooth, out _),
            "Bluetooth input with an invalid CRC was accepted");
    }

    private static uint ComputeBluetoothCrc(byte seed, ReadOnlySpan<byte> data)
    {
        uint state = UpdateCrc32(0xFFFFFFFF, seed);
        foreach (byte value in data)
        {
            state = UpdateCrc32(state, value);
        }
        return ~state;
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

    private static async Task TestDeviceListAsync(int port)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(EncodeOperation(UsbIpConstants.OpReqDevList));

        byte[] reply = await UsbIpCodec.ReadExactlyAsync(stream, 12 + 312 + 4);
        Require(U16(reply, 0) == UsbIpConstants.Version &&
                U16(reply, 2) == UsbIpConstants.OpRepDevList &&
                U32(reply, 4) == 0 && U32(reply, 8) == 1,
            "OP_REP_DEVLIST header mismatch");
        Require(U16(reply, 12 + 256 + 32 + 12) == 0x054C &&
                U16(reply, 12 + 256 + 32 + 14) == 0x0CE6,
            "OP_REP_DEVLIST VID/PID mismatch");
        Require(reply[^4] == 3 && reply[^3] == 0 && reply[^2] == 0,
            "OP_REP_DEVLIST HID interface mismatch");
    }

    private static async Task TestImportSessionAsync(int port, Task<HidOutputCapture> captureTask)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(EncodeOperation(UsbIpConstants.OpReqImport, "1-1"));

        byte[] importReply = await UsbIpCodec.ReadExactlyAsync(stream, 8 + 312);
        Require(U16(importReply, 0) == UsbIpConstants.Version &&
                U16(importReply, 2) == UsbIpConstants.OpRepImport &&
                U32(importReply, 4) == 0,
            "OP_REP_IMPORT header mismatch");

        byte[] getDevice = Setup(0x80, UsbStandardRequest.GetDescriptor,
            value: 0x0100, index: 0, length: 18);
        await stream.WriteAsync(EncodeSubmit(1, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 18, getDevice));
        SubmitReply deviceReply = await ReadSubmitReplyAsync(stream, hasInputPayload: true);
        Require(deviceReply.SequenceNumber == 1 && deviceReply.Status == 0 &&
                deviceReply.Data.Length == 18 && deviceReply.Data[1] == UsbDescriptorType.Device,
            "live EP0 device descriptor mismatch");

        byte[] getConfiguration = Setup(0x80, UsbStandardRequest.GetDescriptor,
            value: 0x0200, index: 0, length: 255);
        await stream.WriteAsync(EncodeSubmit(2, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 255, getConfiguration));
        SubmitReply configurationReply = await ReadSubmitReplyAsync(stream, hasInputPayload: true);
        Require(configurationReply.SequenceNumber == 2 && configurationReply.Status == 0 &&
                configurationReply.Data.Length == 41 && configurationReply.Data[4] == 1 &&
                configurationReply.Data[11] == 0,
            "live EP0 HID-only configuration mismatch");

        byte[] getPairing = Setup(0xA1, UsbHidRequest.GetReport,
            value: 0x0309, index: 0, length: 20);
        await stream.WriteAsync(EncodeSubmit(20, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 20, getPairing));
        SubmitReply pairingReply = await ReadSubmitReplyAsync(stream, hasInputPayload: true);
        Require(pairingReply.SequenceNumber == 20 && pairingReply.Status == 0 &&
                pairingReply.Data.Length == 20 && pairingReply.Data[0] == 0x09 &&
                pairingReply.Data[6] == 0x02,
            "virtual pairing feature report mismatch");

        byte[] getFirmware = Setup(0xA1, UsbHidRequest.GetReport,
            value: 0x0320, index: 0, length: 64);
        await stream.WriteAsync(EncodeSubmit(21, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 64, getFirmware));
        SubmitReply firmwareReply = await ReadSubmitReplyAsync(stream, hasInputPayload: true);
        Require(firmwareReply.SequenceNumber == 21 && firmwareReply.Status == 0 &&
                firmwareReply.Data.Length == 64 && firmwareReply.Data[0] == 0x20,
            "firmware feature report mismatch");

        byte[] getCalibration = Setup(0xA1, UsbHidRequest.GetReport,
            value: 0x0305, index: 0, length: 41);
        await stream.WriteAsync(EncodeSubmit(22, UsbIpConstants.DirectionIn,
            endpoint: 0, transferLength: 41, getCalibration));
        SubmitReply calibrationReply = await ReadSubmitReplyAsync(stream, hasInputPayload: false);
        Require(calibrationReply.SequenceNumber == 22 &&
                calibrationReply.Status == ControlResult.Stall && calibrationReply.ActualLength == 0,
            "unknown calibration feature report must stall");

        byte[] setConfiguration = Setup(0x00, UsbStandardRequest.SetConfiguration,
            value: 1, index: 0, length: 0);
        await stream.WriteAsync(EncodeSubmit(3, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setConfiguration));
        SubmitReply setReply = await ReadSubmitReplyAsync(stream, hasInputPayload: false);
        Require(setReply.SequenceNumber == 3 && setReply.Status == 0 && setReply.ActualLength == 0,
            "SET_CONFIGURATION failed");

        byte[] outputReport = new byte[64];
        outputReport[0] = 0x02;
        outputReport[1] = 0xFF;
        outputReport[11] = 0x21;
        await stream.WriteAsync(EncodeSubmit(4, UsbIpConstants.DirectionOut,
            endpoint: 3, transferLength: outputReport.Length,
            setup: new byte[8], outputReport));
        SubmitReply outputReply = await ReadSubmitReplyAsync(stream, hasInputPayload: false);
        HidOutputCapture capture = await captureTask.WaitAsync(TimeSpan.FromSeconds(2));
        Require(outputReply.SequenceNumber == 4 && outputReply.Status == 0 &&
                outputReply.ActualLength == 64 && capture.Transport == "interrupt-out" &&
                capture.Data.AsSpan().SequenceEqual(outputReport),
            "interrupt OUT capture or completion mismatch");

        await stream.WriteAsync(EncodeSubmit(5, UsbIpConstants.DirectionIn,
            endpoint: 4, transferLength: 64, setup: new byte[8]));
        SubmitReply inputReply = await ReadSubmitReplyAsync(stream, hasInputPayload: true);
        Require(inputReply.SequenceNumber == 5 && inputReply.Status == 0 &&
                inputReply.Data.Length == 64 && inputReply.Data[0] == 0x01 &&
                inputReply.Data[1] == 0x80 && inputReply.Data[8] == 0x08,
            "neutral interrupt IN report mismatch");

        await stream.WriteAsync(EncodeSubmit(6, UsbIpConstants.DirectionIn,
            endpoint: 4, transferLength: 64, setup: new byte[8]));
        await stream.WriteAsync(EncodeUnlink(7, unlinkSequence: 6));
        byte[] unlinkReply = await UsbIpCodec.ReadExactlyAsync(stream, UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkReply, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkReply, 4) == 7 && I32(unlinkReply, 20) == -104,
            "pending interrupt IN UNLINK mismatch");
    }

    private static byte[] EncodeOperation(ushort code, string? busId = null)
    {
        byte[] bytes = new byte[code == UsbIpConstants.OpReqImport ? 40 : 8];
        WriteU16(bytes, 0, UsbIpConstants.Version);
        WriteU16(bytes, 2, code);
        if (busId != null)
        {
            byte[] encoded = Encoding.ASCII.GetBytes(busId);
            encoded.CopyTo(bytes, 8);
        }
        return bytes;
    }

    private static byte[] EncodeSubmit(uint sequence, uint direction, uint endpoint,
        int transferLength, byte[] setup, byte[]? outputData = null)
    {
        byte[] payload = outputData ?? Array.Empty<byte>();
        byte[] bytes = new byte[UsbIpConstants.UrbHeaderLength + payload.Length];
        WriteU32(bytes, 0, UsbIpConstants.CmdSubmit);
        WriteU32(bytes, 4, sequence);
        WriteU32(bytes, 8, DeviceId);
        WriteU32(bytes, 12, direction);
        WriteU32(bytes, 16, endpoint);
        WriteI32(bytes, 24, transferLength);
        WriteI32(bytes, 28, 0);
        WriteI32(bytes, 32, -1);
        WriteI32(bytes, 36, endpoint is 3 or 4 ? 6 : 0);
        setup.CopyTo(bytes, 40);
        payload.CopyTo(bytes, UsbIpConstants.UrbHeaderLength);
        return bytes;
    }

    private static byte[] EncodeUnlink(uint sequence, uint unlinkSequence)
    {
        byte[] bytes = new byte[UsbIpConstants.UrbHeaderLength];
        WriteU32(bytes, 0, UsbIpConstants.CmdUnlink);
        WriteU32(bytes, 4, sequence);
        WriteU32(bytes, 8, DeviceId);
        WriteU32(bytes, 20, unlinkSequence);
        return bytes;
    }

    private static byte[] Setup(byte requestType, byte request,
        ushort value, ushort index, ushort length)
    {
        byte[] setup = new byte[8];
        setup[0] = requestType;
        setup[1] = request;
        BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(2, 2), value);
        BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(4, 2), index);
        BinaryPrimitives.WriteUInt16LittleEndian(setup.AsSpan(6, 2), length);
        return setup;
    }

    private static async Task<SubmitReply> ReadSubmitReplyAsync(Stream stream, bool hasInputPayload)
    {
        byte[] header = await UsbIpCodec.ReadExactlyAsync(stream, UsbIpConstants.UrbHeaderLength);
        Require(U32(header, 0) == UsbIpConstants.RetSubmit, "RET_SUBMIT command mismatch");
        int actualLength = I32(header, 24);
        byte[] data = hasInputPayload && actualLength > 0
            ? await UsbIpCodec.ReadExactlyAsync(stream, actualLength)
            : Array.Empty<byte>();
        return new SubmitReply(U32(header, 4), I32(header, 20), actualLength, data);
    }

    private static ushort U16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    private static int I32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
    private static void WriteU16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), value);
    private static void WriteU32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
    private static void WriteI32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset, 4), value);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record SubmitReply(
        uint SequenceNumber,
        int Status,
        int ActualLength,
        byte[] Data);
}
