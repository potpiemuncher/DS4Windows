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
        Require(descriptors.Interfaces.Count == 1 &&
                descriptors.Interfaces[0] == new UsbInterfaceDescriptorInfo(0, 3, 0, 0),
            "HID-only topology metadata mismatch");

        Console.WriteLine("servertest: exact composite topology");
        DescriptorSet composite = DescriptorSet.LoadFromFixtures(fixturesDir);
        Require(composite.ConfigurationDescriptorLength == 227 &&
                composite.NumInterfaces == 4 && composite.Interfaces.Count == 4,
            "composite descriptor set did not preserve all four interfaces");
        Require(composite.Interfaces[0] == new UsbInterfaceDescriptorInfo(0, 1, 1, 0) &&
                composite.Interfaces[1] == new UsbInterfaceDescriptorInfo(1, 1, 2, 0) &&
                composite.Interfaces[2] == new UsbInterfaceDescriptorInfo(2, 1, 2, 0) &&
                composite.Interfaces[3] == new UsbInterfaceDescriptorInfo(3, 3, 0, 0),
            "composite interface class topology mismatch");
        Require(composite.Endpoints.Contains(new UsbEndpointDescriptorInfo(1, 1, 0x01, 0x09, 392, 4)) &&
                composite.Endpoints.Contains(new UsbEndpointDescriptorInfo(2, 1, 0x82, 0x05, 196, 4)) &&
                composite.Endpoints.Contains(new UsbEndpointDescriptorInfo(3, 0, 0x84, 0x03, 64, 6)) &&
                composite.Endpoints.Contains(new UsbEndpointDescriptorInfo(3, 0, 0x03, 0x03, 64, 6)),
            "composite endpoint topology mismatch");
        await TestCompositeDeviceListAsync(composite);

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
            await TestDeviceListAsync(server.Port, descriptors.Interfaces);

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

        Console.WriteLine("PASS: live USB/IP management, HID-only EP0, interrupt IN/OUT, " +
            "packet-preserving ISO OUT, and UNLINK.");
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

        byte[] usbOutput = new byte[48];
        usbOutput[0] = 0x02;
        usbOutput[1] = 0x0C;
        usbOutput[11] = 0x22;
        usbOutput[12] = 0x12;
        usbOutput[13] = 0x00;
        usbOutput[14] = 0x21;
        usbOutput[22] = 0x05;
        usbOutput[45] = 0xFF; // must not leak unrelated lightbar state
        Require(BluetoothDualSenseInputSource.TryBuildBluetoothTriggerReport(
                usbOutput, out byte[] bluetoothOutput) &&
                bluetoothOutput.Length == 78 && bluetoothOutput[0] == 0x31 &&
                bluetoothOutput[1] == 0x02 && bluetoothOutput[2] == 0x0C &&
                bluetoothOutput.AsSpan(12, 11).SequenceEqual(usbOutput.AsSpan(11, 11)) &&
                bluetoothOutput.AsSpan(23, 11).SequenceEqual(usbOutput.AsSpan(22, 11)) &&
                bluetoothOutput[46] == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(bluetoothOutput.AsSpan(74)) ==
                    ComputeBluetoothCrc(0xA2, bluetoothOutput.AsSpan(0, 74)),
            "USB output did not map to an authenticated trigger-only Bluetooth report");
        usbOutput[1] = 0x02;
        Require(!BluetoothDualSenseInputSource.TryBuildBluetoothTriggerReport(
                usbOutput, out _),
            "output without adaptive-trigger update flags must not alter trigger state");

        byte[] usbAudio = new byte[16 * 8];
        for (int frame = 0; frame < 16; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(usbAudio.AsSpan(frame * 8 + 4, 2), 16384);
            BinaryPrimitives.WriteInt16LittleEndian(usbAudio.AsSpan(frame * 8 + 6, 2), -16384);
        }
        byte[] bluetoothPcm = new byte[2];
        Require(BluetoothDualSenseInputSource.ConvertUsbHapticPcm(usbAudio, bluetoothPcm) == 2 &&
                bluetoothPcm[0] == 0x40 && bluetoothPcm[1] == 0xC0,
            "USB 48 kHz channels 3/4 did not decimate to signed 3 kHz stereo PCM");

        byte[] hapticChunk = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        byte[] hapticReport = new byte[398];
        BluetoothDualSenseInputSource.BuildBluetoothHapticReport(
            hapticReport, sequence: 3, packetCounter: 9, hapticChunk);
        Require(hapticReport[0] == 0x36 && hapticReport[1] == 0x30 &&
                hapticReport[2] == 0x91 && hapticReport[10] == 9 &&
                hapticReport[11] == 0x90 && hapticReport[12] == 63 &&
                hapticReport[76] == 0x92 && hapticReport[77] == 64 &&
                hapticReport.AsSpan(78, 64).SequenceEqual(hapticChunk) &&
                BinaryPrimitives.ReadUInt32LittleEndian(hapticReport.AsSpan(394, 4)) ==
                    ComputeBluetoothCrc(0xA2, hapticReport.AsSpan(0, 394)),
            "Bluetooth report 0x36 native-haptic container or CRC mismatch");
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

    private static async Task TestCompositeDeviceListAsync(DescriptorSet descriptors)
    {
        using var server = new VirtualDualSenseServer(descriptors,
            new VirtualDualSenseServerOptions(Port: 0,
                InputInterval: TimeSpan.FromMilliseconds(200)));
        var captured = new TaskCompletionSource<IsochronousOutCapture>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.IsochronousOutReceived += output => captured.TrySetResult(output);
        server.Start();
        using var cancellation = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cancellation.Token);
        try
        {
            await TestDeviceListAsync(server.Port, descriptors.Interfaces);
            Console.WriteLine("servertest: composite isochronous OUT completion");
            await TestCompositeIsoOutAsync(server.Port, captured.Task);
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
    }

    private static async Task TestCompositeIsoOutAsync(int port,
        Task<IsochronousOutCapture> captureTask)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(EncodeOperation(UsbIpConstants.OpReqImport, "1-1"));
        byte[] importReply = await UsbIpCodec.ReadExactlyAsync(stream, 8 + 312);
        Require(U16(importReply, 2) == UsbIpConstants.OpRepImport && U32(importReply, 4) == 0,
            "composite ISO import failed");

        byte[] setConfiguration = Setup(0x00, UsbStandardRequest.SetConfiguration,
            value: 1, index: 0, length: 0);
        await stream.WriteAsync(EncodeSubmit(1, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setConfiguration));
        SubmitReply configurationReply = await ReadSubmitReplyAsync(stream, hasInputPayload: false);
        Require(configurationReply.Status == 0, "composite SET_CONFIGURATION failed");

        byte[] setPlaybackInterface = Setup(0x01, UsbStandardRequest.SetInterface,
            value: 1, index: 1, length: 0);
        await stream.WriteAsync(EncodeSubmit(2, UsbIpConstants.DirectionOut,
            endpoint: 0, transferLength: 0, setPlaybackInterface));
        SubmitReply interfaceReply = await ReadSubmitReplyAsync(stream, hasInputPayload: false);
        Require(interfaceReply.Status == 0, "playback SET_INTERFACE alt 1 failed");

        byte[] audio = new byte[768];
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] = (byte)(i * 17);
        }
        var packets = new[]
        {
            new UsbIpIsoPacket(0, 384, 0, 0),
            new UsbIpIsoPacket(384, 384, 0, 0),
        };
        await stream.WriteAsync(EncodeIsoSubmit(3, endpoint: 1, startFrame: 1234,
            interval: 1, audio, packets));
        IsoSubmitReply isoReply = await ReadIsoSubmitReplyAsync(stream);
        IsochronousOutCapture capture = await captureTask.WaitAsync(TimeSpan.FromSeconds(2));
        Require(isoReply.SequenceNumber == 3 && isoReply.Status == 0 &&
                isoReply.ActualLength == audio.Length && isoReply.StartFrame == 1234 &&
                isoReply.Packets.Count == 2 &&
                isoReply.Packets.All(packet => packet.ActualLength == 384 && packet.Status == 0) &&
                capture.Endpoint == 1 && capture.StartFrame == 1234 &&
                capture.Data.AsSpan().SequenceEqual(audio),
            "ISO OUT capture or packet-preserving completion mismatch");

        Console.WriteLine("servertest: ISO UNLINK wins pacing race without late RET_SUBMIT");
        byte[] pacedAudio = new byte[38400];
        var pacedPackets = Enumerable.Range(0, 100)
            .Select(index => new UsbIpIsoPacket(checked((uint)(index * 384)), 384, 0, 0))
            .ToArray();
        await stream.WriteAsync(EncodeIsoSubmit(4, endpoint: 1, startFrame: 1236,
            interval: 4, pacedAudio, pacedPackets));
        await Task.Delay(TimeSpan.FromMilliseconds(2));
        await stream.WriteAsync(EncodeUnlink(5, unlinkSequence: 4));
        byte[] unlinkReply = await UsbIpCodec.ReadExactlyAsync(stream,
            UsbIpConstants.UrbHeaderLength);
        Require(U32(unlinkReply, 0) == UsbIpConstants.RetUnlink &&
                U32(unlinkReply, 4) == 5 && I32(unlinkReply, 20) == -104,
            "paced ISO UNLINK did not cancel the pending completion");

        using var noLateCompletion = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(120));
        try
        {
            await UsbIpCodec.ReadExactlyAsync(stream, UsbIpConstants.UrbHeaderLength,
                noLateCompletion.Token);
            throw new InvalidOperationException(
                "canceled ISO transfer emitted a late RET_SUBMIT");
        }
        catch (OperationCanceledException) when (noLateCompletion.IsCancellationRequested)
        {
        }
    }

    private static async Task TestDeviceListAsync(int port,
        IReadOnlyList<UsbInterfaceDescriptorInfo> expectedInterfaces)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(EncodeOperation(UsbIpConstants.OpReqDevList));

        byte[] reply = await UsbIpCodec.ReadExactlyAsync(stream,
            12 + 312 + expectedInterfaces.Count * 4);
        Require(U16(reply, 0) == UsbIpConstants.Version &&
                U16(reply, 2) == UsbIpConstants.OpRepDevList &&
                U32(reply, 4) == 0 && U32(reply, 8) == 1,
            "OP_REP_DEVLIST header mismatch");
        Require(U16(reply, 12 + 256 + 32 + 12) == 0x054C &&
                U16(reply, 12 + 256 + 32 + 14) == 0x0CE6,
            "OP_REP_DEVLIST VID/PID mismatch");
        Require(reply[12 + 311] == expectedInterfaces.Count,
            "OP_REP_DEVLIST interface count mismatch");
        for (int i = 0; i < expectedInterfaces.Count; i++)
        {
            int offset = 12 + 312 + i * 4;
            UsbInterfaceDescriptorInfo expected = expectedInterfaces[i];
            Require(reply[offset] == expected.Class &&
                    reply[offset + 1] == expected.SubClass &&
                    reply[offset + 2] == expected.Protocol,
                $"OP_REP_DEVLIST interface {i} class mismatch");
        }
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

    private static byte[] EncodeIsoSubmit(uint sequence, uint endpoint, int startFrame,
        int interval, byte[] outputData, IReadOnlyList<UsbIpIsoPacket> packets)
    {
        byte[] bytes = new byte[UsbIpConstants.UrbHeaderLength + outputData.Length +
            packets.Count * UsbIpConstants.IsoDescriptorLength];
        WriteU32(bytes, 0, UsbIpConstants.CmdSubmit);
        WriteU32(bytes, 4, sequence);
        WriteU32(bytes, 8, DeviceId);
        WriteU32(bytes, 12, UsbIpConstants.DirectionOut);
        WriteU32(bytes, 16, endpoint);
        WriteI32(bytes, 24, outputData.Length);
        WriteI32(bytes, 28, startFrame);
        WriteI32(bytes, 32, packets.Count);
        WriteI32(bytes, 36, interval);
        outputData.CopyTo(bytes, UsbIpConstants.UrbHeaderLength);
        int descriptorOffset = UsbIpConstants.UrbHeaderLength + outputData.Length;
        for (int i = 0; i < packets.Count; i++)
        {
            UsbIpIsoPacket packet = packets[i];
            int offset = descriptorOffset + i * UsbIpConstants.IsoDescriptorLength;
            WriteU32(bytes, offset, packet.Offset);
            WriteU32(bytes, offset + 4, packet.Length);
            WriteU32(bytes, offset + 8, packet.ActualLength);
            WriteI32(bytes, offset + 12, packet.Status);
        }
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

    private static async Task<IsoSubmitReply> ReadIsoSubmitReplyAsync(Stream stream)
    {
        byte[] header = await UsbIpCodec.ReadExactlyAsync(stream, UsbIpConstants.UrbHeaderLength);
        Require(U32(header, 0) == UsbIpConstants.RetSubmit, "ISO RET_SUBMIT command mismatch");
        int numberOfPackets = I32(header, 32);
        byte[] descriptors = await UsbIpCodec.ReadExactlyAsync(stream,
            numberOfPackets * UsbIpConstants.IsoDescriptorLength);
        var packets = new UsbIpIsoPacket[numberOfPackets];
        for (int i = 0; i < packets.Length; i++)
        {
            int offset = i * UsbIpConstants.IsoDescriptorLength;
            packets[i] = new UsbIpIsoPacket(
                U32(descriptors, offset),
                U32(descriptors, offset + 4),
                U32(descriptors, offset + 8),
                I32(descriptors, offset + 12));
        }
        return new IsoSubmitReply(
            U32(header, 4),
            I32(header, 20),
            I32(header, 24),
            I32(header, 28),
            I32(header, 36),
            packets);
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

    private sealed record IsoSubmitReply(
        uint SequenceNumber,
        int Status,
        int ActualLength,
        int StartFrame,
        int ErrorCount,
        IReadOnlyList<UsbIpIsoPacket> Packets);
}
