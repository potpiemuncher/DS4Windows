using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using VirtualDualSenseUsbip.Device;
using VirtualDualSenseUsbip.Protocol;

namespace VirtualDualSenseUsbip.Live;

public sealed record VirtualDualSenseServerOptions(
    int Port = 3240,
    string BusId = "1-1",
    TimeSpan? InputInterval = null)
{
    public TimeSpan EffectiveInputInterval => InputInterval ?? TimeSpan.FromMilliseconds(4);
}

public sealed record HidOutputCapture(
    DateTimeOffset Timestamp,
    uint SequenceNumber,
    string Transport,
    byte[] Data);

/// <summary>
/// Local USB/IP v1.1.1 server exporting one synthetic HID-only DualSense.
/// Management clients receive DEVLIST/IMPORT records; after a successful import
/// the connection becomes a USB/IP URB session until detach/disconnect.
/// </summary>
public sealed class VirtualDualSenseServer : IDisposable
{
    private readonly DescriptorSet descriptors;
    private readonly VirtualDualSenseServerOptions options;
    private readonly TcpListener listener;
    private readonly UsbIpDeviceInfo deviceInfo;
    private WindowsTimerResolution? timerResolution;
    private bool started;

    public int Port { get; private set; }
    public event Action<string>? Log;
    public event Action<HidOutputCapture>? HidOutputReceived;

    public VirtualDualSenseServer(DescriptorSet descriptors,
        VirtualDualSenseServerOptions? options = null)
    {
        this.descriptors = descriptors;
        this.options = options ?? new VirtualDualSenseServerOptions();
        listener = new TcpListener(IPAddress.Loopback, this.options.Port);
        deviceInfo = new UsbIpDeviceInfo(
            "/virtual/ds4windows/dualsense-hid",
            this.options.BusId,
            BusNumber: 1,
            DeviceNumber: 1,
            Speed: 3, // USB_SPEED_HIGH; matches the captured wired controller
            descriptors.VendorId,
            descriptors.ProductId,
            descriptors.DeviceBcd,
            DeviceClass: 0,
            DeviceSubClass: 0,
            DeviceProtocol: 0,
            ConfigurationValue: 0,
            descriptors.NumConfigurations,
            descriptors.NumInterfaces,
            new[] { new UsbIpInterfaceInfo(3, 0, 0) });
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(listener.Server.SafeHandle.IsClosed, this);
        if (started)
        {
            throw new InvalidOperationException("The USB/IP server has already started.");
        }

        timerResolution = WindowsTimerResolution.Begin(1);
        try
        {
            listener.Start();
        }
        catch
        {
            timerResolution.Dispose();
            timerResolution = null;
            throw;
        }
        started = true;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        EmitLog($"USB/IP server listening on 127.0.0.1:{Port}; busid {options.BusId}.");
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (!started)
        {
            throw new InvalidOperationException("Call Start before RunAsync.");
        }

        using CancellationTokenRegistration registration = cancellationToken.Register(listener.Stop);
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            using (client)
            {
                client.NoDelay = true;
                try
                {
                    await HandleClientAsync(client, cancellationToken);
                }
                catch (EndOfStreamException)
                {
                    EmitLog("USB/IP client disconnected.");
                }
                catch (IOException ex)
                {
                    EmitLog($"USB/IP connection ended: {ex.Message}");
                }
                catch (SocketException ex)
                {
                    EmitLog($"USB/IP socket ended: {ex.Message}");
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        NetworkStream stream = client.GetStream();
        await using var writer = new SerializedStreamWriter(stream);
        UsbIpOperation operation = await UsbIpCodec.ReadOperationAsync(stream, cancellationToken);

        if (operation.Version != UsbIpConstants.Version || operation.Status != 0)
        {
            ushort replyCode = operation.Code == UsbIpConstants.OpReqImport
                ? UsbIpConstants.OpRepImport
                : UsbIpConstants.OpRepDevList;
            await writer.WriteAsync(
                UsbIpCodec.EncodeOperationReply(replyCode, status: 1), cancellationToken);
            EmitLog($"Rejected USB/IP operation version 0x{operation.Version:X4}, " +
                $"code 0x{operation.Code:X4}, status {operation.Status}.");
            return;
        }

        switch (operation.Code)
        {
            case UsbIpConstants.OpReqDevList:
                await writer.WriteAsync(
                    UsbIpCodec.EncodeOperationReply(UsbIpConstants.OpRepDevList, 0,
                        deviceInfo, includeInterfaceList: true), cancellationToken);
                EmitLog("Served OP_REP_DEVLIST for the HID-only DualSense.");
                return;

            case UsbIpConstants.OpReqImport when operation.BusId == options.BusId:
                await writer.WriteAsync(
                    UsbIpCodec.EncodeOperationReply(UsbIpConstants.OpRepImport, 0, deviceInfo),
                    cancellationToken);
                EmitLog($"Imported busid {options.BusId}; beginning live URB session.");
                var session = new UsbIpDeviceSession(
                    stream, writer, descriptors, options.EffectiveInputInterval,
                    capture => HidOutputReceived?.Invoke(capture), EmitLog);
                await session.RunAsync(cancellationToken);
                return;

            case UsbIpConstants.OpReqImport:
                await writer.WriteAsync(
                    UsbIpCodec.EncodeOperationReply(UsbIpConstants.OpRepImport, status: 1),
                    cancellationToken);
                EmitLog($"Rejected unknown busid '{operation.BusId}'.");
                return;

            default:
                await writer.WriteAsync(
                    UsbIpCodec.EncodeOperationReply(UsbIpConstants.OpRepDevList, status: 1),
                    cancellationToken);
                EmitLog($"Rejected unsupported operation 0x{operation.Code:X4}.");
                return;
        }
    }

    private void EmitLog(string message) => Log?.Invoke(message);

    public void Dispose()
    {
        listener.Stop();
        timerResolution?.Dispose();
        timerResolution = null;
        GC.SuppressFinalize(this);
    }
}

internal sealed class UsbIpDeviceSession
{
    private const uint DeviceId = 0x00010001;
    private const uint HidInterruptOutEndpoint = 3;
    private const uint HidInterruptInEndpoint = 4;
    private const int ConnectionReset = -104; // -ECONNRESET

    private readonly Stream stream;
    private readonly SerializedStreamWriter writer;
    private readonly ControlEndpoint controlEndpoint;
    private readonly TimeSpan inputInterval;
    private readonly Action<HidOutputCapture> captureOutput;
    private readonly Action<string> log;
    private readonly ConcurrentDictionary<uint, UsbIpSubmit> pendingInput = new();
    private readonly ConcurrentQueue<uint> pendingInputOrder = new();
    private byte frameCounter;

    public UsbIpDeviceSession(Stream stream, SerializedStreamWriter writer,
        DescriptorSet descriptors, TimeSpan inputInterval,
        Action<HidOutputCapture> captureOutput, Action<string> log)
    {
        this.stream = stream;
        this.writer = writer;
        controlEndpoint = new ControlEndpoint(descriptors);
        this.inputInterval = inputInterval;
        this.captureOutput = captureOutput;
        this.log = log;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task inputPump = PumpInterruptInputAsync(sessionCancellation.Token);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UsbIpCommand command = await UsbIpCodec.ReadCommandAsync(stream, cancellationToken);
                switch (command)
                {
                    case UsbIpSubmit submit:
                        await HandleSubmitAsync(submit, cancellationToken);
                        break;
                    case UsbIpUnlink unlink:
                        await HandleUnlinkAsync(unlink, cancellationToken);
                        break;
                }
            }
        }
        finally
        {
            sessionCancellation.Cancel();
            try
            {
                await inputPump;
            }
            catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task HandleSubmitAsync(UsbIpSubmit submit, CancellationToken cancellationToken)
    {
        if (submit.Basic.DeviceId != DeviceId)
        {
            log($"Rejecting seq {submit.Basic.SequenceNumber}: unexpected device id " +
                $"0x{submit.Basic.DeviceId:X8}.");
            await ReplySubmitAsync(submit.Basic.SequenceNumber, ControlResult.Stall,
                Array.Empty<byte>(), cancellationToken);
            return;
        }

        if (submit.NumberOfPackets > 0)
        {
            log($"Rejecting seq {submit.Basic.SequenceNumber}: ISO traffic is gated until M2.5.");
            await ReplySubmitAsync(submit.Basic.SequenceNumber, ControlResult.Stall,
                Array.Empty<byte>(), cancellationToken);
            return;
        }

        if (submit.Basic.Endpoint == 0)
        {
            await HandleControlAsync(submit, cancellationToken);
        }
        else if (submit.Basic.Endpoint == HidInterruptInEndpoint &&
                 submit.Basic.Direction == UsbIpConstants.DirectionIn)
        {
            if (!pendingInput.TryAdd(submit.Basic.SequenceNumber, submit))
            {
                throw new InvalidDataException(
                    $"Duplicate pending sequence {submit.Basic.SequenceNumber}.");
            }
            pendingInputOrder.Enqueue(submit.Basic.SequenceNumber);
        }
        else if (submit.Basic.Endpoint == HidInterruptOutEndpoint &&
                 submit.Basic.Direction == UsbIpConstants.DirectionOut)
        {
            captureOutput(new HidOutputCapture(DateTimeOffset.UtcNow,
                submit.Basic.SequenceNumber, "interrupt-out", submit.TransferBuffer.ToArray()));
            await ReplySubmitAsync(submit.Basic.SequenceNumber, status: 0,
                data: Array.Empty<byte>(), cancellationToken,
                actualLength: submit.TransferBuffer.Length);
        }
        else
        {
            log($"Stalling unsupported endpoint {submit.Basic.Endpoint} direction " +
                $"{submit.Basic.Direction} (seq {submit.Basic.SequenceNumber}).");
            await ReplySubmitAsync(submit.Basic.SequenceNumber, ControlResult.Stall,
                Array.Empty<byte>(), cancellationToken);
        }
    }

    private async Task HandleControlAsync(UsbIpSubmit submit, CancellationToken cancellationToken)
    {
        UsbSetupPacket setup = UsbSetupPacket.Parse(submit.Setup);
        uint expectedDirection = setup.DeviceToHost
            ? UsbIpConstants.DirectionIn
            : UsbIpConstants.DirectionOut;
        if (submit.Basic.Direction != expectedDirection)
        {
            log($"Stalling EP0 seq {submit.Basic.SequenceNumber}: setup/USB-IP direction mismatch.");
            await ReplySubmitAsync(submit.Basic.SequenceNumber, ControlResult.Stall,
                Array.Empty<byte>(), cancellationToken);
            return;
        }

        if (setup.Type == UsbSetupPacket.TypeClass && setup.Request == UsbHidRequest.SetReport)
        {
            captureOutput(new HidOutputCapture(DateTimeOffset.UtcNow,
                submit.Basic.SequenceNumber, "control-set-report", submit.TransferBuffer.ToArray()));
        }

        ControlResult result = controlEndpoint.Handle(setup, submit.TransferBuffer);
        int actualLength = result.Status == 0
            ? (submit.Basic.Direction == UsbIpConstants.DirectionIn
                ? result.Data.Length
                : submit.TransferBuffer.Length)
            : 0;

        if (result.Status != 0)
        {
            log($"EP0 stalled: bmRequestType 0x{setup.RequestType:X2}, request " +
                $"0x{setup.Request:X2}, value 0x{setup.Value:X4}, index 0x{setup.Index:X4}, " +
                $"length {setup.Length}.");
        }

        await ReplySubmitAsync(submit.Basic.SequenceNumber, result.Status,
            result.Data, cancellationToken, actualLength);
    }

    private async Task HandleUnlinkAsync(UsbIpUnlink unlink, CancellationToken cancellationToken)
    {
        bool removed = pendingInput.TryRemove(unlink.UnlinkSequenceNumber, out _);
        int status = removed ? ConnectionReset : 0;
        await writer.WriteAsync(
            UsbIpCodec.Encode(new UsbIpUnlinkReply(unlink.Basic.SequenceNumber, status)),
            cancellationToken);
    }

    private async Task PumpInterruptInputAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(inputInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            UsbIpSubmit? submit = null;
            while (pendingInputOrder.TryDequeue(out uint sequence))
            {
                if (pendingInput.TryRemove(sequence, out submit))
                {
                    break;
                }
            }

            if (submit == null)
            {
                continue;
            }

            byte[] report = CreateNeutralInputReport(submit.TransferBufferLength);
            await ReplySubmitAsync(submit.Basic.SequenceNumber, status: 0,
                report, cancellationToken);
        }
    }

    private byte[] CreateNeutralInputReport(int requestedLength)
    {
        byte[] report = new byte[Math.Min(64, requestedLength)];
        if (report.Length == 0)
        {
            return report;
        }

        report[0] = 0x01;
        for (int i = 1; i <= 4 && i < report.Length; i++)
        {
            report[i] = 0x80; // centered LX, LY, RX, RY
        }
        if (report.Length > 7)
        {
            report[7] = frameCounter++;
        }
        if (report.Length > 8)
        {
            report[8] = 0x08; // neutral d-pad, face buttons released
        }
        return report;
    }

    private ValueTask ReplySubmitAsync(uint sequenceNumber, int status, byte[] data,
        CancellationToken cancellationToken, int? actualLength = null)
    {
        var reply = new UsbIpSubmitReply(sequenceNumber, status,
            actualLength ?? data.Length,
            StartFrame: -1,
            NumberOfPackets: 0,
            ErrorCount: 0,
            data,
            Array.Empty<UsbIpIsoPacket>());
        return writer.WriteAsync(UsbIpCodec.Encode(reply), cancellationToken);
    }
}
