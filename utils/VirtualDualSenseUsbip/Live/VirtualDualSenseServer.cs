using System.Collections.Concurrent;
using System.Diagnostics;
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

public sealed record IsochronousOutCapture(
    DateTimeOffset Timestamp,
    long MonotonicTimestamp,
    uint SequenceNumber,
    uint Endpoint,
    int StartFrame,
    int Interval,
    byte[] Data,
    IReadOnlyList<UsbIpIsoPacket> Packets);

/// <summary>
/// Local USB/IP v1.1.1 server exporting one synthetic wired DualSense.
/// Management clients receive DEVLIST/IMPORT records; after a successful import
/// the connection becomes a USB/IP URB session until detach/disconnect.
/// </summary>
public sealed class VirtualDualSenseServer : IDisposable
{
    private readonly DescriptorSet descriptors;
    private readonly VirtualDualSenseServerOptions options;
    private readonly TcpListener listener;
    private readonly UsbIpDeviceInfo deviceInfo;
    private readonly IInputReportSource inputReports;
    private readonly FeatureReportSet featureReports;
    private WindowsTimerResolution? timerResolution;
    private bool started;

    public int Port { get; private set; }
    public event Action<string>? Log;
    public event Action<HidOutputCapture>? HidOutputReceived;
    public event Action<IsochronousOutCapture>? IsochronousOutReceived;

    public VirtualDualSenseServer(DescriptorSet descriptors,
        VirtualDualSenseServerOptions? options = null,
        IInputReportSource? inputReports = null,
        FeatureReportSet? featureReports = null)
    {
        this.descriptors = descriptors;
        this.options = options ?? new VirtualDualSenseServerOptions();
        this.inputReports = inputReports ?? new NeutralInputReportSource();
        this.featureReports = featureReports ?? FeatureReportSet.CreateVirtualDefaults();
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
            descriptors.Interfaces.Select(info =>
                new UsbIpInterfaceInfo(info.Class, info.SubClass, info.Protocol)).ToArray());
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
        EmitLog($"USB/IP server listening on 127.0.0.1:{Port}; busid {options.BusId}; " +
            $"input {inputReports.Description}.");
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
                EmitLog($"Served OP_REP_DEVLIST for a {descriptors.NumInterfaces}-interface DualSense.");
                return;

            case UsbIpConstants.OpReqImport when operation.BusId == options.BusId:
                await writer.WriteAsync(
                    UsbIpCodec.EncodeOperationReply(UsbIpConstants.OpRepImport, 0, deviceInfo),
                    cancellationToken);
                EmitLog($"Imported busid {options.BusId}; beginning live URB session.");
                var session = new UsbIpDeviceSession(
                    stream, writer, descriptors, featureReports, inputReports,
                    options.EffectiveInputInterval,
                    capture => HidOutputReceived?.Invoke(capture),
                    capture => IsochronousOutReceived?.Invoke(capture), EmitLog);
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
    private const int MaxPendingIsochronousTransfers = 64;
    private const int ConnectionReset = -104; // -ECONNRESET

    private readonly Stream stream;
    private readonly SerializedStreamWriter writer;
    private readonly ControlEndpoint controlEndpoint;
    private readonly IInputReportSource inputReports;
    private readonly TimeSpan inputInterval;
    private readonly Action<HidOutputCapture> captureOutput;
    private readonly Action<IsochronousOutCapture> captureIsochronousOut;
    private readonly Action<string> log;
    private readonly IReadOnlyDictionary<uint, UsbEndpointDescriptorInfo> isochronousOutEndpoints;
    private readonly ConcurrentDictionary<uint, PendingTransfer> pendingInput = new();
    private readonly ConcurrentQueue<uint> pendingInputOrder = new();
    private readonly ConcurrentDictionary<uint, PendingTransfer> pendingIsochronous = new();
    private readonly ConcurrentQueue<uint> pendingIsochronousOrder = new();
    private readonly SemaphoreSlim pendingIsochronousSignal = new(0);

    public UsbIpDeviceSession(Stream stream, SerializedStreamWriter writer,
        DescriptorSet descriptors, FeatureReportSet featureReports,
        IInputReportSource inputReports, TimeSpan inputInterval,
        Action<HidOutputCapture> captureOutput,
        Action<IsochronousOutCapture> captureIsochronousOut,
        Action<string> log)
    {
        this.stream = stream;
        this.writer = writer;
        controlEndpoint = new ControlEndpoint(descriptors, featureReports);
        controlEndpoint.InterfaceAltChanged += (interfaceNumber, alternateSetting) =>
            log($"SET_INTERFACE interface {interfaceNumber} alt {alternateSetting}.");
        this.inputReports = inputReports;
        this.inputInterval = inputInterval;
        this.captureOutput = captureOutput;
        this.captureIsochronousOut = captureIsochronousOut;
        this.log = log;
        isochronousOutEndpoints = descriptors.Endpoints
            .Where(endpoint => (endpoint.Address & 0x80) == 0 &&
                (endpoint.Attributes & 0x03) == 0x01)
            .ToDictionary(endpoint => (uint)(endpoint.Address & 0x0F));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task inputPump = PumpInterruptInputAsync(sessionCancellation.Token);
        Task isochronousPump = PumpIsochronousCompletionsAsync(sessionCancellation.Token);
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
            foreach (Task pump in new[] { inputPump, isochronousPump })
            {
                try
                {
                    await pump;
                }
                catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
                {
                }
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
            await HandleIsochronousAsync(submit, cancellationToken);
            return;
        }

        if (submit.Basic.Endpoint == 0)
        {
            await HandleControlAsync(submit, cancellationToken);
        }
        else if (submit.Basic.Endpoint == HidInterruptInEndpoint &&
                 submit.Basic.Direction == UsbIpConstants.DirectionIn)
        {
            if (!pendingInput.TryAdd(submit.Basic.SequenceNumber, new PendingTransfer(submit)))
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

    private async Task HandleIsochronousAsync(UsbIpSubmit submit,
        CancellationToken cancellationToken)
    {
        UsbEndpointDescriptorInfo? endpoint = null;
        bool knownPlaybackEndpoint = submit.Basic.Direction == UsbIpConstants.DirectionOut &&
            isochronousOutEndpoints.TryGetValue(submit.Basic.Endpoint, out endpoint);
        bool activeAlternateSetting = knownPlaybackEndpoint && endpoint != null &&
            controlEndpoint.GetAltSetting(endpoint.InterfaceNumber) == endpoint.AlternateSetting;
        bool validPacketSizes = knownPlaybackEndpoint && endpoint != null &&
            submit.IsoPackets.All(packet => packet.Length <= endpoint.MaxPacketSize);
        long describedLength = submit.IsoPackets.Sum(packet => (long)packet.Length);
        bool validPayload = describedLength == submit.TransferBuffer.Length &&
            submit.TransferBuffer.Length == submit.TransferBufferLength;

        if (!knownPlaybackEndpoint || !activeAlternateSetting || !validPacketSizes || !validPayload)
        {
            log($"Rejecting ISO seq {submit.Basic.SequenceNumber}: endpoint={submit.Basic.Endpoint}, " +
                $"direction={submit.Basic.Direction}, packets={submit.NumberOfPackets}, " +
                $"bytes={submit.TransferBuffer.Length}, described={describedLength}, " +
                $"alt-active={activeAlternateSetting}.");
            await ReplyIsochronousAsync(submit, ControlResult.Stall, successful: false,
                cancellationToken);
            return;
        }

        if (pendingIsochronous.Count >= MaxPendingIsochronousTransfers ||
            !pendingIsochronous.TryAdd(submit.Basic.SequenceNumber, new PendingTransfer(submit)))
        {
            log($"Rejecting ISO seq {submit.Basic.SequenceNumber}: pending queue limit or duplicate.");
            await ReplyIsochronousAsync(submit, ControlResult.Stall, successful: false,
                cancellationToken);
            return;
        }

        captureIsochronousOut(new IsochronousOutCapture(
            DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(),
            submit.Basic.SequenceNumber,
            submit.Basic.Endpoint,
            submit.StartFrame,
            submit.Interval,
            submit.TransferBuffer.ToArray(),
            submit.IsoPackets.ToArray()));
        pendingIsochronousOrder.Enqueue(submit.Basic.SequenceNumber);
        pendingIsochronousSignal.Release();
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
        int status = await UnlinkPendingAsync(pendingInput, unlink.UnlinkSequenceNumber,
            cancellationToken);
        if (status == 0)
        {
            status = await UnlinkPendingAsync(pendingIsochronous, unlink.UnlinkSequenceNumber,
                cancellationToken);
        }
        await writer.WriteAsync(
            UsbIpCodec.Encode(new UsbIpUnlinkReply(unlink.Basic.SequenceNumber, status)),
            cancellationToken);
    }

    private static async Task<int> UnlinkPendingAsync(
        ConcurrentDictionary<uint, PendingTransfer> pendingTransfers,
        uint sequenceNumber,
        CancellationToken cancellationToken)
    {
        if (!pendingTransfers.TryGetValue(sequenceNumber, out PendingTransfer? pending))
        {
            return 0;
        }

        if (pending.TryCancel())
        {
            pendingTransfers.TryRemove(sequenceNumber, out _);
            return ConnectionReset;
        }

        // Completion won the race. USB/IP status 0 is only valid after the
        // corresponding RET_SUBMIT is on the wire; otherwise the client can
        // observe RET_UNLINK followed by a second completion for the same URB.
        await pending.Completion.WaitAsync(cancellationToken);
        return 0;
    }

    private async Task PumpInterruptInputAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(inputInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            PendingTransfer? pending = null;
            while (pendingInputOrder.TryDequeue(out uint sequence))
            {
                if (pendingInput.TryGetValue(sequence, out pending))
                {
                    break;
                }
            }

            if (pending == null || !pending.TryBeginCompletion())
            {
                continue;
            }

            try
            {
                UsbIpSubmit submit = pending.Submit;
                byte[] report = inputReports.CreateReport(submit.TransferBufferLength);
                await ReplySubmitAsync(submit.Basic.SequenceNumber, status: 0,
                    report, cancellationToken);
                pending.Complete();
            }
            catch (Exception ex)
            {
                pending.Fail(ex);
                throw;
            }
            finally
            {
                pendingInput.TryRemove(pending.Submit.Basic.SequenceNumber, out _);
            }
        }
    }

    private async Task PumpIsochronousCompletionsAsync(CancellationToken cancellationToken)
    {
        long nextCompletionTimestamp = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await pendingIsochronousSignal.WaitAsync(cancellationToken);

            PendingTransfer? pending = null;
            while (pendingIsochronousOrder.TryDequeue(out uint sequence))
            {
                if (pendingIsochronous.TryGetValue(sequence, out pending))
                {
                    break;
                }
            }
            if (pending == null)
            {
                continue;
            }

            UsbIpSubmit submit = pending.Submit;
            UsbEndpointDescriptorInfo endpoint = isochronousOutEndpoints[submit.Basic.Endpoint];
            long durationTicks = IsochronousDurationTicks(endpoint.Interval,
                submit.NumberOfPackets);
            long now = Stopwatch.GetTimestamp();
            long completionTimestamp = checked(
                Math.Max(nextCompletionTimestamp, now) + durationTicks);

            await DelayUntilAsync(completionTimestamp, cancellationToken);
            if (!pending.TryBeginCompletion())
            {
                continue;
            }

            try
            {
                await ReplyIsochronousAsync(submit, status: 0, successful: true,
                    cancellationToken);
                pending.Complete();
            }
            catch (Exception ex)
            {
                pending.Fail(ex);
                throw;
            }
            finally
            {
                pendingIsochronous.TryRemove(submit.Basic.SequenceNumber, out _);
            }

            now = Stopwatch.GetTimestamp();
            // Store the last completion point, not the following deadline. The
            // next URB adds its own packet duration and never bursts to catch up.
            nextCompletionTimestamp = Math.Max(completionTimestamp, now);
        }
    }

    private static long IsochronousDurationTicks(byte endpointInterval, int packetCount)
    {
        // High-speed USB bInterval is 2^(bInterval-1) microframes; one
        // microframe is 125 us. The captured DualSense endpoint uses 4,
        // therefore each packet represents exactly 1 ms of audio.
        int interval = Math.Clamp(endpointInterval, (byte)1, (byte)16);
        double seconds = packetCount * 0.000125 * (1 << (interval - 1));
        return Math.Max(1, checked((long)Math.Round(seconds * Stopwatch.Frequency)));
    }

    private static async Task DelayUntilAsync(long targetTimestamp,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            long remainingTicks = targetTimestamp - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }
            TimeSpan remaining = TimeSpan.FromSeconds(
                remainingTicks / (double)Stopwatch.Frequency);
            await Task.Delay(remaining, cancellationToken);
        }
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

    private ValueTask ReplyIsochronousAsync(UsbIpSubmit submit, int status, bool successful,
        CancellationToken cancellationToken)
    {
        UsbIpIsoPacket[] completedPackets = submit.IsoPackets
            .Select(packet => packet with
            {
                ActualLength = successful ? packet.Length : 0,
                Status = successful ? 0 : status,
            })
            .ToArray();
        int actualLength = successful
            ? checked((int)completedPackets.Sum(packet => (long)packet.ActualLength))
            : 0;
        var reply = new UsbIpSubmitReply(
            submit.Basic.SequenceNumber,
            status,
            actualLength,
            submit.StartFrame,
            submit.NumberOfPackets,
            successful ? 0 : submit.NumberOfPackets,
            Array.Empty<byte>(),
            completedPackets);
        return writer.WriteAsync(UsbIpCodec.Encode(reply), cancellationToken);
    }

    private sealed class PendingTransfer
    {
        private const int Pending = 0;
        private const int Completing = 1;
        private const int Canceled = 2;
        private const int Completed = 3;

        private readonly TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int state = Pending;

        public PendingTransfer(UsbIpSubmit submit)
        {
            Submit = submit;
        }

        public UsbIpSubmit Submit { get; }
        public Task Completion => completion.Task;

        public bool TryCancel()
        {
            if (Interlocked.CompareExchange(ref state, Canceled, Pending) != Pending)
            {
                return false;
            }

            completion.TrySetResult();
            return true;
        }

        public bool TryBeginCompletion() =>
            Interlocked.CompareExchange(ref state, Completing, Pending) == Pending;

        public void Complete()
        {
            Volatile.Write(ref state, Completed);
            completion.TrySetResult();
        }

        public void Fail(Exception exception)
        {
            Volatile.Write(ref state, Completed);
            completion.TrySetException(exception);
        }
    }
}
