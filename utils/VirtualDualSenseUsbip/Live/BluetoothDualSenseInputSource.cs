using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace VirtualDualSenseUsbip.Live;

public enum SpeakerAudioRoute
{
    Auto,
    Speaker,
    Headphone,
}

/// <summary>
/// Opt-in audio coupling for the Bluetooth bridge. Speaker audio relays USB
/// isochronous channels 1/2 as Opus inside report 0x36; Microphone enables the
/// physical pad microphone and serves its decoded PCM to isochronous IN.
/// </summary>
public sealed record BluetoothAudioOptions(
    bool SpeakerAudio = false,
    bool Microphone = false,
    SpeakerAudioRoute Route = SpeakerAudioRoute.Auto)
{
    public static BluetoothAudioOptions Disabled { get; } = new();
}

/// <summary>
/// Reads authenticated 78-byte input reports from a real Bluetooth DualSense
/// and publishes their shared 63-byte payload as wired USB report 0x01. A
/// latest-value output worker relays only adaptive-trigger blocks from wired
/// report 0x02 without delaying USB/IP completion or changing rumble/audio.
///
/// With <see cref="BluetoothAudioOptions.SpeakerAudio"/> the 0x36 haptic
/// stream also carries one 200-byte Opus frame per report (channels 1/2 of the
/// USB playback endpoint, resampled 48 kHz -> 45 kHz effective delivery) so the
/// controller speaker/headphone plays native game audio. With
/// <see cref="BluetoothAudioOptions.Microphone"/> the physical microphone is
/// enabled while the host has the capture interface open; its 71-byte Opus
/// mono frames are decoded and served to the virtual mic endpoint.
///
/// Protocol research credit: egormanga/SAxense and awalol/DS5Dongle.
/// </summary>
public sealed class BluetoothDualSenseInputSource : IInputReportSource, IUsbAudioRelay, IDisposable
{
    private const int SonyVendorId = 0x054C;
    private const int DualSenseProductId = 0x0CE6;
    private const int DualSenseEdgeProductId = 0x0DF2;
    private const int BluetoothInputLength = 78;
    private const int BluetoothOutputLength = 78;
    private const int UsbInputLength = 64;
    private const byte BluetoothInputCrcSeed = 0xA1;
    private const byte BluetoothOutputCrcSeed = 0xA2;
    private const byte BluetoothFeatureCrcSeed = 0xA3;
    private const int BluetoothHapticOutputLength = 398;
    private const int BluetoothControlOutputLength = 142;
    private const int HapticFramesPerReport = 32;
    private const int HapticBytesPerReport = HapticFramesPerReport * 2;
    private const int UsbAudioChannels = 4;
    private const int UsbAudioBytesPerFrame = UsbAudioChannels * 2;
    private const int UsbToBluetoothHapticDecimation = 16;
    private const double HapticReportPeriodMs = HapticFramesPerReport * 1000.0 / 3000.0;

    // Listening audio inside report 0x36 (values proven by the DS4Windows
    // streamer): one 200-byte CBR Opus frame per ~10.667 ms report, 48 kHz
    // stereo, 480 samples/channel. One frame is consumed per haptic slot, so
    // source audio must be delivered at an effective 45 kHz or it backlogs.
    private const int OpusFrameBytes = 200;
    private const int OpusSamplesPerFrame = 480;
    private const int OpusSampleRate = 48000;
    private const int AudioDeliveryRate = 45000;
    private const int SpeakerFrameQueueDepth = 4;
    private const int SpeakerPrebufferFrames = 2;
    private const double IsochronousKeepAliveMs = 1000.0;
    private const double HeadsetDebounceMs = 250.0;

    // Microphone: 71-byte Opus mono 48 kHz frames inside flagged 0x31 input
    // reports; served to USB as 48 kHz stereo signed 16-bit.
    private const int MicOpusFrameBytes = 71;
    private const int MicOpusPayloadOffset = 3;
    private const int MicRingCapacityShorts = 48000 * 2 * 3 / 20; // ~150 ms stereo
    private const double MicRateMeasureSeconds = 1.0;
    private const double MicSlotRateThreshold = 96.5; // below => 45 kHz effective

    private readonly SafeFileHandle handle;
    private readonly int inputReportLength;
    private readonly Action<string> log;
    private readonly Thread readThread;
    private readonly Thread outputThread;
    private readonly Thread hapticThread;
    private readonly AutoResetEvent outputAvailable = new(initialState: false);
    private readonly AutoResetEvent hapticAvailable = new(initialState: false);
    private readonly object outputQueueGate = new();
    private readonly object outputWriteGate = new();
    private byte[] latestReport = new NeutralInputReportSource().CreateReport(UsbInputLength);
    private byte[]? pendingTriggerOutput;
    private byte[]? lastQueuedTriggerOutput;
    private readonly HapticPcmRing hapticPcm = new(capacityBytes: 1200);
    private long lastHapticInputTimestamp;
    private long lastIsochronousOutTimestamp;
    private int hapticStreamingRequested;
    private long validReportCount;
    private long invalidReportCount;
    private long relayedTriggerReportCount;
    private long relayedHapticReportCount;
    private long relayedAudioReportCount;
    private long hapticWriteErrorCount;
    private long hapticUnderrunCount;
    private long speakerUnderrunCount;
    private int disposed;

    // Speaker relay state
    private readonly BluetoothAudioOptions audioOptions;
    private readonly IOpusEncoder? speakerEncoder;
    private readonly StereoLinearResampler? speakerResampler;
    private readonly ShortRing? speakerPcm;
    private readonly OpusFrameQueue? speakerFrames;
    private readonly short[] speakerExtractScratch = new short[SpeakerChunkFrames * 2];
    private readonly short[] speakerResampleScratch = new short[(SpeakerChunkFrames + 4) * 2];
    private const int SpeakerChunkFrames = 48;
    private int playbackInterfaceActive;
    private int playbackVolumeBits = BitConverter.SingleToInt32Bits(1.0f);
    private int headsetRawState;
    private long headsetRawSince;
    private int headsetStableState;
    private int controlSequence;

    // Microphone state
    private readonly IOpusDecoder? microphoneDecoder;
    private readonly ShortRing? microphonePcm;
    private readonly short[] microphoneMonoScratch = new short[OpusSamplesPerFrame];
    private readonly short[] microphoneStereoScratch = new short[OpusSamplesPerFrame * 2];
    // 45 kHz -> 48 kHz upsampling can emit up to 16/15 of the input frames.
    private readonly short[] microphoneResampleScratch =
        new short[(OpusSamplesPerFrame * 16 / 15 + 8) * 2];
    private StereoLinearResampler? microphoneResampler;
    private int captureInterfaceActive;
    private int captureVolumeBits = BitConverter.SingleToInt32Bits(1.0f);
    private int microphoneRateDecision; // 0 = measuring, else source samples/s
    private long microphoneMeasureStart;
    private long microphoneMeasureCount;
    private long microphoneReportCount;
    private long microphoneDecodeErrorCount;
    private long microphoneDeliveredBytes;
    private long microphoneUnderrunCount;
    private int microphonePacketFrames = 48;

    public string Description { get; }
    public byte[]? CalibrationFeatureReport { get; }
    public long ValidReportCount => Interlocked.Read(ref validReportCount);
    public long InvalidReportCount => Interlocked.Read(ref invalidReportCount);
    public long RelayedTriggerReportCount => Interlocked.Read(ref relayedTriggerReportCount);
    public long RelayedHapticReportCount => Interlocked.Read(ref relayedHapticReportCount);
    public long RelayedAudioReportCount => Interlocked.Read(ref relayedAudioReportCount);
    public long HapticWriteErrorCount => Interlocked.Read(ref hapticWriteErrorCount);
    public long HapticUnderrunCount => Interlocked.Read(ref hapticUnderrunCount);
    public long SpeakerUnderrunCount => Interlocked.Read(ref speakerUnderrunCount);
    public int HapticQueueBytes => hapticPcm.Count;
    public int SpeakerQueueFrames => speakerFrames?.Count ?? 0;
    public long MicrophoneReportCount => Interlocked.Read(ref microphoneReportCount);
    public long MicrophoneDecodeErrorCount => Interlocked.Read(ref microphoneDecodeErrorCount);
    public long MicrophoneDeliveredBytes => Interlocked.Read(ref microphoneDeliveredBytes);
    public long MicrophoneUnderrunCount => Interlocked.Read(ref microphoneUnderrunCount);
    public int MicrophoneQueueFrames => (microphonePcm?.Count ?? 0) / 2;
    public int MicrophoneRateDecision => Volatile.Read(ref microphoneRateDecision);
    public bool PlaybackInterfaceActive => Volatile.Read(ref playbackInterfaceActive) != 0;
    public bool CaptureInterfaceActive => Volatile.Read(ref captureInterfaceActive) != 0;

    private float PlaybackVolume => BitConverter.Int32BitsToSingle(Volatile.Read(ref playbackVolumeBits));
    private float CaptureVolume => BitConverter.Int32BitsToSingle(Volatile.Read(ref captureVolumeBits));

    private BluetoothDualSenseInputSource(HidDevice device, SafeFileHandle handle,
        int inputReportLength, Action<string> log, BluetoothAudioOptions audioOptions)
    {
        this.handle = handle;
        this.inputReportLength = inputReportLength;
        this.log = log;
        this.audioOptions = audioOptions;
        Description = $"Bluetooth DualSense (PID 0x{device.ProductID:X4})";
        CalibrationFeatureReport = ReadUsbCalibrationFeature(handle, log);

        if (audioOptions.SpeakerAudio)
        {
            speakerEncoder = OpusCodecFactory.CreateEncoder(OpusSampleRate, 2,
                OpusApplication.OPUS_APPLICATION_AUDIO);
            speakerEncoder.Bitrate = OpusFrameBytes * 8 * 100;
            speakerEncoder.UseVBR = false;
            speakerEncoder.Complexity = 5;
            speakerResampler = new StereoLinearResampler(OpusSampleRate, AudioDeliveryRate);
            speakerPcm = new ShortRing(AudioDeliveryRate * 2 / 5); // ~200 ms stereo
            speakerFrames = new OpusFrameQueue(SpeakerFrameQueueDepth, OpusFrameBytes);
        }

        if (audioOptions.Microphone)
        {
            microphoneDecoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, 1);
            microphonePcm = new ShortRing(MicRingCapacityShorts);
        }

        readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "Virtual DualSense Bluetooth input",
        };
        outputThread = new Thread(OutputLoop)
        {
            IsBackground = true,
            Name = "Virtual DualSense trigger output",
        };
        hapticThread = new Thread(HapticLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "Virtual DualSense native haptic audio",
        };
        readThread.Start();
        outputThread.Start();
        hapticThread.Start();
    }

    public static BluetoothDualSenseInputSource Open(Action<string>? log = null,
        BluetoothAudioOptions? audioOptions = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Bluetooth DualSense input requires Windows HID.");
        }

        Action<string> logger = log ?? (_ => { });
        BluetoothAudioOptions options = audioOptions ?? BluetoothAudioOptions.Disabled;
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
            if (options.SpeakerAudio)
            {
                logger("Speaker-audio relay enabled: USB channels 1/2 -> Opus -> report 0x36.");
            }
            if (options.Microphone)
            {
                logger("Microphone relay enabled: pad mic -> Opus decode -> isochronous IN.");
            }
            return new BluetoothDualSenseInputSource(device, handle, maxInput, logger, options);
        }

        throw new InvalidOperationException("No physical Bluetooth DualSense with streaming reports was found.");
    }

    public byte[] CreateReport(int requestedLength)
    {
        byte[] current = Volatile.Read(ref latestReport);
        return current.AsSpan(0, Math.Clamp(requestedLength, 0, UsbInputLength)).ToArray();
    }

    /// <summary>
    /// Queues the latest adaptive-trigger state for a dedicated Bluetooth
    /// writer. Repeated states are coalesced so a game's high-rate USB output
    /// stream cannot create an unbounded queue or block USB/IP completion.
    /// </summary>
    public bool QueueTriggerReport(ReadOnlySpan<byte> usbOutputReport)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            !TryBuildBluetoothTriggerReport(usbOutputReport, out byte[] bluetoothReport))
        {
            return false;
        }

        lock (outputQueueGate)
        {
            if (lastQueuedTriggerOutput != null &&
                lastQueuedTriggerOutput.AsSpan().SequenceEqual(bluetoothReport))
            {
                return true;
            }
            lastQueuedTriggerOutput = bluetoothReport;
            Interlocked.Exchange(ref pendingTriggerOutput, bluetoothReport);
        }
        outputAvailable.Set();
        return true;
    }

    /// <summary>
    /// Queues native wired DualSense audio. Haptic channels 3/4 are reduced to
    /// the 3 kHz signed 8-bit stereo stream; when the speaker relay is enabled,
    /// channels 1/2 are resampled to the 45 kHz delivery domain for Opus
    /// encoding. The USB endpoint is 4-channel, 48 kHz, signed 16-bit PCM.
    /// </summary>
    public bool QueueHapticAudio(ReadOnlySpan<byte> usbFourChannelPcm16)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            usbFourChannelPcm16.Length < UsbAudioBytesPerFrame * UsbToBluetoothHapticDecimation ||
            usbFourChannelPcm16.Length % UsbAudioBytesPerFrame != 0)
        {
            return false;
        }

        Interlocked.Exchange(ref lastIsochronousOutTimestamp, Stopwatch.GetTimestamp());
        bool speakerRelay = speakerPcm != null;
        if (speakerRelay)
        {
            ExtractSpeakerAudio(usbFourChannelPcm16);
        }

        int outputBytes = usbFourChannelPcm16.Length /
            (UsbAudioBytesPerFrame * UsbToBluetoothHapticDecimation) * 2;
        byte[] converted = new byte[outputBytes];
        int written = ConvertUsbHapticPcm(usbFourChannelPcm16, converted);
        bool hasEnergy = false;
        for (int i = 0; i < written; i++)
        {
            if (converted[i] != 0)
            {
                hasEnergy = true;
                break;
            }
        }

        if (hasEnergy)
        {
            hapticPcm.Write(converted.AsSpan(0, written));
            Interlocked.Exchange(ref lastHapticInputTimestamp, Stopwatch.GetTimestamp());
        }

        // Channels 1/2 commonly stay active while native haptic channels are
        // silent. The stream must run whenever the playback interface is open
        // and the speaker relay wants the audio clock, or when haptic energy
        // arrives; otherwise stay idle so silent packets cannot keep the
        // voice coils faintly energized.
        bool wantStream = hasEnergy ||
            (speakerRelay && Volatile.Read(ref playbackInterfaceActive) != 0);
        if (wantStream && Interlocked.Exchange(ref hapticStreamingRequested, 1) == 0)
        {
            hapticAvailable.Set();
        }
        return true;
    }

    private void ExtractSpeakerAudio(ReadOnlySpan<byte> usbFourChannelPcm16)
    {
        float volume = PlaybackVolume;
        int totalFrames = usbFourChannelPcm16.Length / UsbAudioBytesPerFrame;
        int frameOffset = 0;
        while (frameOffset < totalFrames)
        {
            int take = Math.Min(totalFrames - frameOffset, SpeakerChunkFrames);
            for (int i = 0; i < take; i++)
            {
                int byteOffset = (frameOffset + i) * UsbAudioBytesPerFrame;
                int left = BinaryPrimitives.ReadInt16LittleEndian(
                    usbFourChannelPcm16.Slice(byteOffset, 2));
                int right = BinaryPrimitives.ReadInt16LittleEndian(
                    usbFourChannelPcm16.Slice(byteOffset + 2, 2));
                speakerExtractScratch[i * 2] = ScaleSample(left, volume);
                speakerExtractScratch[i * 2 + 1] = ScaleSample(right, volume);
            }

            int outSamples = speakerResampler!.Resample(
                speakerExtractScratch.AsSpan(0, take * 2), speakerResampleScratch);
            if (outSamples > 0)
            {
                speakerPcm!.Write(speakerResampleScratch.AsSpan(0, outSamples));
            }
            frameOffset += take;
        }
    }

    private static short ScaleSample(int sample, float volume)
    {
        return (short)Math.Clamp((int)MathF.Round(sample * volume), short.MinValue, short.MaxValue);
    }

    internal static int ConvertUsbHapticPcm(ReadOnlySpan<byte> usbFourChannelPcm16,
        Span<byte> bluetoothStereoPcm8)
    {
        int usbFrames = usbFourChannelPcm16.Length / UsbAudioBytesPerFrame;
        int groups = Math.Min(usbFrames / UsbToBluetoothHapticDecimation,
            bluetoothStereoPcm8.Length / 2);
        for (int group = 0; group < groups; group++)
        {
            int leftSum = 0;
            int rightSum = 0;
            for (int sample = 0; sample < UsbToBluetoothHapticDecimation; sample++)
            {
                int frameOffset = (group * UsbToBluetoothHapticDecimation + sample) *
                    UsbAudioBytesPerFrame;
                leftSum += BinaryPrimitives.ReadInt16LittleEndian(
                    usbFourChannelPcm16.Slice(frameOffset + 4, 2));
                rightSum += BinaryPrimitives.ReadInt16LittleEndian(
                    usbFourChannelPcm16.Slice(frameOffset + 6, 2));
            }

            int left = leftSum / UsbToBluetoothHapticDecimation / 256;
            int right = rightSum / UsbToBluetoothHapticDecimation / 256;
            bluetoothStereoPcm8[group * 2] = unchecked((byte)(sbyte)Math.Clamp(left, -128, 127));
            bluetoothStereoPcm8[group * 2 + 1] = unchecked((byte)(sbyte)Math.Clamp(right, -128, 127));
        }
        return groups * 2;
    }

    internal static bool TryConvertBluetoothReport(ReadOnlySpan<byte> bluetoothReport,
        out byte[] usbReport)
    {
        usbReport = Array.Empty<byte>();
        if (bluetoothReport.Length < BluetoothInputLength || bluetoothReport[0] != 0x31 ||
            (bluetoothReport[1] & 0x02) != 0 ||
            !HasValidBluetoothCrc(bluetoothReport[..BluetoothInputLength], BluetoothInputCrcSeed))
        {
            return false;
        }

        usbReport = new byte[UsbInputLength];
        usbReport[0] = 0x01;
        bluetoothReport.Slice(2, UsbInputLength - 1).CopyTo(usbReport.AsSpan(1));
        return true;
    }

    /// <summary>
    /// Extracts the 71-byte Opus mono payload from a microphone-flagged 0x31
    /// input report (byte 1 bit 1 set), validating length and CRC first so a
    /// malformed report can neither over-read nor reach the gamepad parser.
    /// </summary>
    internal static bool TryExtractMicrophoneOpusPayload(ReadOnlySpan<byte> bluetoothReport,
        Span<byte> opusPayload)
    {
        if (bluetoothReport.Length < BluetoothInputLength || bluetoothReport[0] != 0x31 ||
            (bluetoothReport[1] & 0x02) == 0 || opusPayload.Length < MicOpusFrameBytes ||
            !HasValidBluetoothCrc(bluetoothReport[..BluetoothInputLength], BluetoothInputCrcSeed))
        {
            return false;
        }

        bluetoothReport.Slice(MicOpusPayloadOffset, MicOpusFrameBytes)
            .CopyTo(opusPayload);
        return true;
    }

    internal static bool TryBuildBluetoothTriggerReport(ReadOnlySpan<byte> usbOutputReport,
        out byte[] bluetoothReport)
    {
        bluetoothReport = Array.Empty<byte>();
        if (usbOutputReport.Length < 33 || usbOutputReport[0] != 0x02)
        {
            return false;
        }

        byte triggerUpdateFlags = (byte)(usbOutputReport[1] & 0x0C);
        if (triggerUpdateFlags == 0)
        {
            return false;
        }

        bluetoothReport = new byte[BluetoothOutputLength];
        bluetoothReport[0] = 0x31;
        bluetoothReport[1] = 0x02; // DATA tag
        bluetoothReport[2] = triggerUpdateFlags;
        if ((triggerUpdateFlags & 0x04) != 0)
        {
            usbOutputReport.Slice(11, 11).CopyTo(bluetoothReport.AsSpan(12));
        }
        if ((triggerUpdateFlags & 0x08) != 0)
        {
            usbOutputReport.Slice(22, 11).CopyTo(bluetoothReport.AsSpan(23));
        }
        uint crc = ComputeBluetoothCrc(BluetoothOutputCrcSeed,
            bluetoothReport.AsSpan(0, BluetoothOutputLength - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bluetoothReport.AsSpan(BluetoothOutputLength - 4), crc);
        return true;
    }

    private void ReadLoop()
    {
        byte[] buffer = new byte[inputReportLength];
        byte[] opusPayload = new byte[MicOpusFrameBytes];
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

            ReadOnlySpan<byte> report = buffer.AsSpan(0, (int)bytesRead);
            if (bytesRead >= BluetoothInputLength && buffer[0] == 0x31 &&
                (buffer[1] & 0x02) != 0)
            {
                if (TryExtractMicrophoneOpusPayload(report, opusPayload))
                {
                    HandleMicrophoneFrame(opusPayload);
                }
                else
                {
                    Interlocked.Increment(ref invalidReportCount);
                }
                continue;
            }

            if (bytesRead < BluetoothInputLength ||
                !TryConvertBluetoothReport(report, out byte[] usbReport))
            {
                Interlocked.Increment(ref invalidReportCount);
                continue;
            }

            // Byte 55 of the Bluetooth report (USB report byte 54) carries the
            // plug state; bit 0 is the headset. Debounced in the haptic loop.
            int headset = (buffer[55] & 0x01) != 0 ? 1 : 0;
            if (Volatile.Read(ref headsetRawState) != headset)
            {
                Volatile.Write(ref headsetRawState, headset);
                Interlocked.Exchange(ref headsetRawSince, Stopwatch.GetTimestamp());
            }

            Volatile.Write(ref latestReport, usbReport);
            Interlocked.Increment(ref validReportCount);
        }
    }

    private void HandleMicrophoneFrame(ReadOnlySpan<byte> opusPayload)
    {
        long count = Interlocked.Increment(ref microphoneReportCount);
        if (microphoneDecoder == null || microphonePcm == null ||
            Volatile.Read(ref captureInterfaceActive) == 0)
        {
            return;
        }

        // The output direction is slaved to the ~10.667 ms haptic slot clock
        // (45 kHz effective). Whether the microphone direction shares that
        // clock or runs at a true 48 kHz is measured live from the arrival
        // rate of the first second of frames; 45 kHz sources are resampled so
        // recorded pitch stays correct.
        if (Volatile.Read(ref microphoneRateDecision) == 0)
        {
            long start = Interlocked.Read(ref microphoneMeasureStart);
            if (start == 0)
            {
                Interlocked.Exchange(ref microphoneMeasureStart, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref microphoneMeasureCount, 0);
                return;
            }

            long measured = Interlocked.Increment(ref microphoneMeasureCount);
            double elapsed = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
            if (elapsed < MicRateMeasureSeconds)
            {
                return;
            }

            double rate = measured / elapsed;
            int decision = rate < MicSlotRateThreshold ? AudioDeliveryRate : OpusSampleRate;
            if (decision == AudioDeliveryRate)
            {
                microphoneResampler = new StereoLinearResampler(AudioDeliveryRate, OpusSampleRate);
            }
            Volatile.Write(ref microphoneRateDecision, decision);
            log($"Microphone frame rate measured at {rate:0.0}/s; treating source as " +
                $"{decision} Hz{(decision == AudioDeliveryRate ? " (resampling to 48 kHz)" : string.Empty)}.");
        }

        int samples = microphoneDecoder.Decode(opusPayload, microphoneMonoScratch,
            OpusSamplesPerFrame, false);
        if (samples <= 0 || samples > OpusSamplesPerFrame)
        {
            long errors = Interlocked.Increment(ref microphoneDecodeErrorCount);
            if (errors <= 3)
            {
                log($"Microphone Opus decode failed (result {samples}).");
            }
            return;
        }

        float volume = CaptureVolume;
        for (int i = 0; i < samples; i++)
        {
            short value = ScaleSample(microphoneMonoScratch[i], volume);
            microphoneStereoScratch[i * 2] = value;
            microphoneStereoScratch[i * 2 + 1] = value;
        }

        StereoLinearResampler? resampler = microphoneResampler;
        if (resampler != null)
        {
            int outSamples = resampler.Resample(
                microphoneStereoScratch.AsSpan(0, samples * 2), microphoneResampleScratch);
            if (outSamples > 0)
            {
                microphonePcm.Write(microphoneResampleScratch.AsSpan(0, outSamples));
            }
        }
        else
        {
            microphonePcm.Write(microphoneStereoScratch.AsSpan(0, samples * 2));
        }

        if (count == 200 || count % 20000 == 0)
        {
            log($"Microphone streaming: {count} frames received, ring {MicrophoneQueueFrames} frames.");
        }
    }

    private void OutputLoop()
    {
        while (true)
        {
            outputAvailable.WaitOne();
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            byte[]? report = Interlocked.Exchange(ref pendingTriggerOutput, null);
            if (report == null)
            {
                continue;
            }

            if (!WriteBluetoothReport(report, out int error))
            {
                if (Volatile.Read(ref disposed) == 0)
                {
                    log($"Bluetooth adaptive-trigger relay write failed (Win32 error {error}).");
                }
                continue;
            }

            long count = Interlocked.Increment(ref relayedTriggerReportCount);
            if (count == 1)
            {
                log("Relayed the first game-authored adaptive-trigger state to Bluetooth.");
            }
        }
    }

    private void HapticLoop()
    {
        byte[] chunk = new byte[HapticBytesPerReport];
        byte[] report = new byte[BluetoothHapticOutputLength];
        byte[] opusFrame = new byte[OpusFrameBytes];
        short[] encoderBlock = new short[OpusSamplesPerFrame * 2];
        byte sequence = 0;
        byte packetCounter = 0;

        while (true)
        {
            hapticAvailable.WaitOne();
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            // About 30 ms protects the 10.667 ms Bluetooth clock from the
            // measured 2..17 ms USB/IP arrival jitter without unbounded delay.
            Thread.Sleep(30);
            Stopwatch clock = Stopwatch.StartNew();
            double nextDeadlineMs = 0;
            int trailingSilenceReports = 0;
            bool loggedStart = false;
            bool audioSession = false;
            bool audioPrimed = false;

            while (Volatile.Read(ref disposed) == 0)
            {
                int copied = hapticPcm.ReadPartial(chunk);
                if (copied < chunk.Length)
                {
                    Array.Clear(chunk, copied, chunk.Length - copied);
                }

                double inputAgeMs = (Stopwatch.GetTimestamp() -
                    Interlocked.Read(ref lastHapticInputTimestamp)) * 1000.0 / Stopwatch.Frequency;
                if (copied < chunk.Length && inputAgeMs <= 100)
                {
                    Interlocked.Increment(ref hapticUnderrunCount);
                }
                if (copied == 0 && inputAgeMs > 100)
                {
                    trailingSilenceReports++;
                }
                else
                {
                    trailingSilenceReports = 0;
                }

                nextDeadlineMs += HapticReportPeriodMs;
                double waitMs = nextDeadlineMs - clock.Elapsed.TotalMilliseconds;
                if (waitMs > 2)
                {
                    Thread.Sleep((int)(waitMs - 1));
                }
                while (clock.Elapsed.TotalMilliseconds < nextDeadlineMs)
                {
                    Thread.SpinWait(80);
                }
                if (clock.Elapsed.TotalMilliseconds - nextDeadlineMs > 100)
                {
                    nextDeadlineMs = clock.Elapsed.TotalMilliseconds;
                }

                bool audioKeepAlive = speakerPcm != null &&
                    (Volatile.Read(ref playbackInterfaceActive) != 0 ||
                     IsochronousOutAgeMs() <= IsochronousKeepAliveMs);
                if (audioKeepAlive && !audioSession)
                {
                    // Entering audio mode: reset the frame pipeline and unmute
                    // the controller amplifier before the first audio report.
                    audioSession = true;
                    audioPrimed = false;
                    speakerFrames!.Clear();
                    speakerPcm!.Reset();
                    if (!SendAmplifierSetup(out int setupError))
                    {
                        log($"Controller audio amplifier setup write failed (Win32 error {setupError}).");
                    }
                    else
                    {
                        log("Controller audio amplifier initialized for native speaker relay.");
                    }
                }

                if (audioSession)
                {
                    while (speakerPcm!.ReadExact(encoderBlock))
                    {
                        byte[] slot = speakerFrames!.RentSlot();
                        EncodeOpusFrame(speakerEncoder!, encoderBlock, slot);
                        speakerFrames.CommitSlot();
                    }

                    if (!audioPrimed && speakerFrames!.Count >= SpeakerPrebufferFrames)
                    {
                        audioPrimed = true;
                    }

                    bool gotFrame = audioPrimed && speakerFrames!.TryDequeue(opusFrame);
                    if (!gotFrame)
                    {
                        if (audioPrimed)
                        {
                            audioPrimed = false; // ran dry: rebuffer before resuming
                            Interlocked.Increment(ref speakerUnderrunCount);
                        }
                        // Encode silence through the live encoder so the
                        // controller-side Opus decoder state stays continuous.
                        Array.Clear(encoderBlock);
                        EncodeOpusFrame(speakerEncoder!, encoderBlock, opusFrame);
                    }

                    BuildBluetoothAudioReport(report, sequence, packetCounter, chunk,
                        opusFrame, UseHeadphoneRoute());
                }
                else
                {
                    BuildBluetoothHapticReport(report, sequence, packetCounter, chunk);
                }

                sequence = (byte)((sequence + 1) & 0x0F);
                packetCounter++;
                if (!WriteBluetoothReport(report, out int error))
                {
                    long errors = Interlocked.Increment(ref hapticWriteErrorCount);
                    if (errors <= 3 && Volatile.Read(ref disposed) == 0)
                    {
                        log($"Bluetooth native-haptic relay write failed (Win32 error {error}).");
                    }
                }
                else
                {
                    long count = Interlocked.Increment(ref relayedHapticReportCount);
                    if (audioSession)
                    {
                        Interlocked.Increment(ref relayedAudioReportCount);
                    }
                    if (!loggedStart)
                    {
                        loggedStart = true;
                        log($"Relayed native USB audio to Bluetooth report 0x36 " +
                            $"(first stream report {count}{(audioSession ? ", with listening audio" : string.Empty)}).");
                    }
                }

                if (trailingSilenceReports < 6 || audioKeepAlive)
                {
                    continue;
                }

                Interlocked.Exchange(ref hapticStreamingRequested, 0);
                if ((hapticPcm.Count > 0 ||
                     (speakerPcm != null && Volatile.Read(ref playbackInterfaceActive) != 0)) &&
                    Interlocked.Exchange(ref hapticStreamingRequested, 1) == 0)
                {
                    continue;
                }
                log($"Bluetooth native audio stream idled after " +
                    $"{RelayedHapticReportCount} total report 0x36 writes.");
                break;
            }
        }
    }

    private double IsochronousOutAgeMs()
    {
        long last = Interlocked.Read(ref lastIsochronousOutTimestamp);
        if (last == 0)
        {
            return double.PositiveInfinity;
        }
        return (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;
    }

    private bool UseHeadphoneRoute()
    {
        switch (audioOptions.Route)
        {
            case SpeakerAudioRoute.Headphone:
                return true;
            case SpeakerAudioRoute.Speaker:
                return false;
            default:
                int raw = Volatile.Read(ref headsetRawState);
                if (Volatile.Read(ref headsetStableState) != raw)
                {
                    double stableMs = (Stopwatch.GetTimestamp() -
                        Interlocked.Read(ref headsetRawSince)) * 1000.0 / Stopwatch.Frequency;
                    if (stableMs >= HeadsetDebounceMs)
                    {
                        Volatile.Write(ref headsetStableState, raw);
                    }
                }
                return Volatile.Read(ref headsetStableState) != 0;
        }
    }

    private static void EncodeOpusFrame(IOpusEncoder encoder, ReadOnlySpan<short> pcm, Span<byte> dest)
    {
        int written = encoder.Encode(pcm, OpusSamplesPerFrame, dest, dest.Length);
        if (written > 0 && written < dest.Length)
        {
            dest[written..].Clear();
        }
    }

    internal static void BuildBluetoothHapticReport(Span<byte> report, byte sequence,
        byte packetCounter, ReadOnlySpan<byte> signedStereoPcm8)
    {
        BuildBluetoothStreamReport(report, sequence, packetCounter, signedStereoPcm8,
            ReadOnlySpan<byte>.Empty, headphoneRoute: false);
    }

    /// <summary>
    /// Builds the audio-bearing variant of report 0x36: the haptic layout plus
    /// one 200-byte Opus listening-audio packet routed to the speaker (0x13)
    /// or headphone (0x16). Layout proven by the DS4Windows streamer.
    /// </summary>
    internal static void BuildBluetoothAudioReport(Span<byte> report, byte sequence,
        byte packetCounter, ReadOnlySpan<byte> signedStereoPcm8, ReadOnlySpan<byte> opusFrame,
        bool headphoneRoute)
    {
        if (opusFrame.Length < OpusFrameBytes)
        {
            throw new ArgumentException("Opus frame buffer is too short.");
        }
        BuildBluetoothStreamReport(report, sequence, packetCounter, signedStereoPcm8,
            opusFrame[..OpusFrameBytes], headphoneRoute);
    }

    private static void BuildBluetoothStreamReport(Span<byte> report, byte sequence,
        byte packetCounter, ReadOnlySpan<byte> signedStereoPcm8, ReadOnlySpan<byte> opusFrame,
        bool headphoneRoute)
    {
        if (report.Length < BluetoothHapticOutputLength ||
            signedStereoPcm8.Length < HapticBytesPerReport)
        {
            throw new ArgumentException("Bluetooth haptic report buffers are too short.");
        }

        report[..BluetoothHapticOutputLength].Clear();
        report[0] = 0x36;
        report[1] = (byte)((sequence & 0x0F) << 4);
        report[2] = 0x91;
        report[3] = 0x07;
        report[4] = 0xFE;
        report[5] = 0x20;
        report[6] = 0x20;
        report[7] = 0x20;
        report[8] = 0x20;
        report[9] = 0x20;
        report[10] = packetCounter;
        report[11] = 0x90;
        report[12] = (byte)KnownHapticState.Length;
        KnownHapticState.CopyTo(report.Slice(13, KnownHapticState.Length));
        report[76] = 0x92;
        report[77] = HapticBytesPerReport;
        signedStereoPcm8[..HapticBytesPerReport].CopyTo(
            report.Slice(78, HapticBytesPerReport));
        if (!opusFrame.IsEmpty)
        {
            report[142] = (byte)((headphoneRoute ? 0x16 : 0x13) | 0x80);
            report[143] = OpusFrameBytes;
            opusFrame.CopyTo(report.Slice(144, OpusFrameBytes));
        }
        uint crc = ComputeBluetoothCrc(BluetoothOutputCrcSeed,
            report[..(BluetoothHapticOutputLength - 4)]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            report.Slice(BluetoothHapticOutputLength - 4, 4), crc);
    }

    /// <summary>
    /// Builds the packetized SetStateData report that unmutes the controller
    /// audio amplifier (headphone/speaker volume plus a mild speaker pre-gain).
    /// Byte-identical to the user-validated DS4Windows streamer setup; the
    /// fixed 0x10 second byte is required or the controller ignores the state.
    /// </summary>
    internal static byte[] BuildAmplifierSetupReport()
    {
        byte[] pkt = new byte[BluetoothControlOutputLength];
        pkt[0] = 0x32;
        pkt[1] = 0x10;
        pkt[2] = 0x90; // SetStateData packet: PID 0x10 | sized
        pkt[3] = 0x3F;
        pkt[4] = 0xB0;      // AllowHeadphoneVolume | AllowSpeakerVolume | AllowAudioControl
        pkt[5] = 0x80;      // AllowAudioControl2
        pkt[8] = 0x64;      // VolumeHeadphones (max 0x7F)
        pkt[9] = 0x64;      // VolumeSpeaker (PS5 uses 0x3D..0x64)
        pkt[11] = 0x00;     // AudioControl: mic auto, default output path
        pkt[41] = 0x02;     // AudioControl2: SpeakerCompPreGain = 2
        uint crc = ComputeBluetoothCrc(BluetoothOutputCrcSeed,
            pkt.AsSpan(0, BluetoothControlOutputLength - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            pkt.AsSpan(BluetoothControlOutputLength - 4), crc);
        return pkt;
    }

    /// <summary>
    /// Builds the config-packet report that turns the physical microphone
    /// stream on (0x03) or off (0x02), per awalol/DS5Dongle. Uses a rolling
    /// sequence in the high nibble of byte 1 like other repeatable state.
    /// </summary>
    internal static byte[] BuildMicrophoneControlReport(byte sequence, bool enable)
    {
        byte[] pkt = new byte[BluetoothControlOutputLength];
        pkt[0] = 0x32;
        pkt[1] = (byte)((sequence & 0x0F) << 4);
        pkt[2] = 0x91; // config packet: PID 0x11 | sized
        pkt[3] = 0x01;
        pkt[4] = enable ? (byte)0x03 : (byte)0x02;
        uint crc = ComputeBluetoothCrc(BluetoothOutputCrcSeed,
            pkt.AsSpan(0, BluetoothControlOutputLength - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            pkt.AsSpan(BluetoothControlOutputLength - 4), crc);
        return pkt;
    }

    private bool SendAmplifierSetup(out int win32Error)
    {
        return WriteBluetoothReport(BuildAmplifierSetupReport(), out win32Error);
    }

    private static readonly byte[] KnownHapticState =
    {
        0xFD, 0xF7, 0x00, 0x00,
        0x7F, 0x64, 0xFF, 0x09, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A,
        0x07, 0x00, 0x00, 0x02, 0x01, 0x00, 0xFF, 0xD7, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    };

    // ---- IUsbAudioRelay ----

    public void SetPlaybackInterfaceActive(bool active)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        int newState = active ? 1 : 0;
        if (Interlocked.Exchange(ref playbackInterfaceActive, newState) == newState)
        {
            return;
        }

        log($"USB playback interface {(active ? "opened" : "closed")} by the host.");
        if (active && speakerPcm != null &&
            Interlocked.Exchange(ref hapticStreamingRequested, 1) == 0)
        {
            hapticAvailable.Set();
        }
    }

    public void SetCaptureInterfaceActive(bool active)
    {
        if (Volatile.Read(ref disposed) != 0 || microphonePcm == null)
        {
            return;
        }

        int newState = active ? 1 : 0;
        if (Interlocked.Exchange(ref captureInterfaceActive, newState) == newState)
        {
            return;
        }

        if (active)
        {
            microphonePcm.Reset();
            Volatile.Write(ref microphoneRateDecision, 0);
            Interlocked.Exchange(ref microphoneMeasureStart, 0);
            Interlocked.Exchange(ref microphoneMeasureCount, 0);
            microphonePacketFrames = 48;
        }

        byte sequence = (byte)(Interlocked.Increment(ref controlSequence) & 0x0F);
        byte[] reportBytes = BuildMicrophoneControlReport(sequence, active);
        if (!WriteBluetoothReport(reportBytes, out int error))
        {
            log($"Microphone {(active ? "enable" : "disable")} write failed (Win32 error {error}).");
            return;
        }
        log($"Physical controller microphone {(active ? "enabled" : "disabled")}.");
    }

    public void SetPlaybackVolume(float linear)
    {
        Interlocked.Exchange(ref playbackVolumeBits,
            BitConverter.SingleToInt32Bits(Math.Clamp(linear, 0f, 1f)));
    }

    public void SetCaptureVolume(float linear)
    {
        Interlocked.Exchange(ref captureVolumeBits,
            BitConverter.SingleToInt32Bits(Math.Clamp(linear, 0f, 1f)));
    }

    public int FillMicrophonePacket(Span<byte> destination, int nominalBytes)
    {
        ShortRing? ring = microphonePcm;
        if (ring == null || Volatile.Read(ref captureInterfaceActive) == 0 ||
            Volatile.Read(ref microphoneRateDecision) == 0)
        {
            return 0;
        }

        int capacityFrames = destination.Length / 4;
        if (capacityFrames == 0)
        {
            return 0;
        }

        UpdateMicrophonePacketFrames(ring.Count / 2);
        int targetFrames = Math.Clamp(microphonePacketFrames, 1, capacityFrames);
        int read = ring.ReadFramesLittleEndian(destination, targetFrames);
        if (read == 0)
        {
            // Empty ring: the caller sends nominal-length silence so the
            // capture cadence never depends on Bluetooth timing.
            if (Interlocked.Read(ref microphoneDeliveredBytes) > 0)
            {
                Interlocked.Increment(ref microphoneUnderrunCount);
            }
            return 0;
        }

        Interlocked.Add(ref microphoneDeliveredBytes, read * 4L);
        return read * 4;
    }

    /// <summary>
    /// Sticky 47/48/49-frame packet sizing with hysteresis. The asynchronous
    /// endpoint lets the device own the sample clock, so small persistent
    /// drift is absorbed by slightly larger or smaller packets instead of
    /// underruns; the wide re-entry thresholds stop per-packet flapping.
    /// </summary>
    private void UpdateMicrophonePacketFrames(int depthFrames)
    {
        int current = microphonePacketFrames;
        int next = current;
        switch (current)
        {
            case 48:
                if (depthFrames > 1920)
                {
                    next = 49; // > 40 ms backlog: drain slightly faster
                }
                else if (depthFrames < 480 && depthFrames > 0)
                {
                    next = 47; // < 10 ms: feed slightly slower
                }
                break;
            case 49:
                if (depthFrames < 1440)
                {
                    next = 48;
                }
                break;
            default:
                if (depthFrames > 960)
                {
                    next = 48;
                }
                break;
        }
        microphonePacketFrames = next;
    }

    private bool WriteBluetoothReport(byte[] report, out int error)
    {
        lock (outputWriteGate)
        {
            bool result = NativeMethods.WriteFile(handle, report, (uint)report.Length,
                out _, IntPtr.Zero);
            error = result ? 0 : Marshal.GetLastWin32Error();
            return result;
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
        return expected == ComputeBluetoothCrc(seed, report[..crcOffset]);
    }

    private static uint ComputeBluetoothCrc(byte seed, ReadOnlySpan<byte> report)
    {
        uint state = UpdateCrc32(0xFFFFFFFF, seed);
        foreach (byte value in report)
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // Turn the physical microphone off if this bridge enabled it, clear
        // both trigger motors, and land the actuators at silence so nothing
        // remains latched after detach.
        if (microphonePcm != null &&
            Interlocked.Exchange(ref captureInterfaceActive, 0) != 0)
        {
            byte sequence = (byte)(Interlocked.Increment(ref controlSequence) & 0x0F);
            _ = WriteBluetoothReport(BuildMicrophoneControlReport(sequence, enable: false), out _);
        }
        byte[] clearUsbOutput = new byte[48];
        clearUsbOutput[0] = 0x02;
        clearUsbOutput[1] = 0x0C;
        if (TryBuildBluetoothTriggerReport(clearUsbOutput, out byte[] clearBluetoothOutput))
        {
            _ = WriteBluetoothReport(clearBluetoothOutput, out _);
        }
        byte[] silentHapticReport = new byte[BluetoothHapticOutputLength];
        BuildBluetoothHapticReport(silentHapticReport, 0, 0, new byte[HapticBytesPerReport]);
        _ = WriteBluetoothReport(silentHapticReport, out _);

        _ = NativeMethods.CancelIoEx(handle, IntPtr.Zero);
        outputAvailable.Set();
        hapticAvailable.Set();
        if (Thread.CurrentThread != readThread)
        {
            _ = readThread.Join(TimeSpan.FromSeconds(2));
        }
        if (Thread.CurrentThread != outputThread)
        {
            _ = outputThread.Join(TimeSpan.FromSeconds(2));
        }
        if (Thread.CurrentThread != hapticThread)
        {
            _ = hapticThread.Join(TimeSpan.FromSeconds(2));
        }
        (speakerEncoder as IDisposable)?.Dispose();
        (microphoneDecoder as IDisposable)?.Dispose();
        handle.Dispose();
        outputAvailable.Dispose();
        hapticAvailable.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class HapticPcmRing
    {
        private readonly byte[] buffer;
        private readonly object gate = new();
        private int head;
        private int count;

        public HapticPcmRing(int capacityBytes) => buffer = new byte[capacityBytes & ~1];
        public int Count { get { lock (gate) return count; } }

        public void Write(ReadOnlySpan<byte> source)
        {
            lock (gate)
            {
                int bytes = source.Length & ~1;
                for (int i = 0; i < bytes; i += 2)
                {
                    if (count > buffer.Length - 2)
                    {
                        head = (head + 2) % buffer.Length;
                        count -= 2;
                    }
                    int tail = (head + count) % buffer.Length;
                    buffer[tail] = source[i];
                    buffer[(tail + 1) % buffer.Length] = source[i + 1];
                    count += 2;
                }
            }
        }

        public int ReadPartial(Span<byte> destination)
        {
            lock (gate)
            {
                int bytes = Math.Min(count, destination.Length) & ~1;
                for (int i = 0; i < bytes; i++)
                {
                    destination[i] = buffer[head];
                    head = (head + 1) % buffer.Length;
                }
                count -= bytes;
                return bytes;
            }
        }
    }

    /// <summary>
    /// Ring of interleaved stereo 16-bit samples with drop-oldest overflow so
    /// backlog (and therefore latency) stays bounded. Writes and reads move in
    /// whole stereo frames (two shorts) to preserve channel alignment.
    /// </summary>
    private sealed class ShortRing
    {
        private readonly short[] buffer;
        private readonly object gate = new();
        private int head;
        private int count;

        public ShortRing(int capacitySamples) => buffer = new short[capacitySamples & ~1];
        public int Count { get { lock (gate) return count; } }

        public void Reset()
        {
            lock (gate)
            {
                head = 0;
                count = 0;
            }
        }

        public void Write(ReadOnlySpan<short> source)
        {
            lock (gate)
            {
                int samples = source.Length & ~1;
                for (int i = 0; i < samples; i += 2)
                {
                    if (count > buffer.Length - 2)
                    {
                        head = (head + 2) % buffer.Length;
                        count -= 2;
                    }
                    int tail = (head + count) % buffer.Length;
                    buffer[tail] = source[i];
                    buffer[(tail + 1) % buffer.Length] = source[i + 1];
                    count += 2;
                }
            }
        }

        /// <summary>Reads exactly destination.Length samples or leaves state unchanged.</summary>
        public bool ReadExact(Span<short> destination)
        {
            lock (gate)
            {
                if (count < destination.Length)
                {
                    return false;
                }

                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = buffer[head];
                    head = (head + 1) % buffer.Length;
                }
                count -= destination.Length;
                return true;
            }
        }

        /// <summary>
        /// Reads whole stereo frames as little-endian bytes; all-or-nothing so
        /// a USB packet is either fully real audio or fully caller silence.
        /// Returns the frames read (0 when fewer than requested are buffered).
        /// </summary>
        public int ReadFramesLittleEndian(Span<byte> destination, int frames)
        {
            lock (gate)
            {
                int samples = frames * 2;
                if (count < samples || destination.Length < frames * 4)
                {
                    return 0;
                }

                for (int i = 0; i < samples; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(
                        destination.Slice(i * 2, 2), buffer[head]);
                    head = (head + 1) % buffer.Length;
                }
                count -= samples;
                return frames;
            }
        }
    }

    /// <summary>Small fixed-size queue of Opus frames with drop-oldest overflow.</summary>
    private sealed class OpusFrameQueue
    {
        private readonly byte[][] slots;
        private readonly object gate = new();
        private int head;
        private int count;

        public int Count { get { lock (gate) return count; } }

        public OpusFrameQueue(int depth, int frameBytes)
        {
            slots = new byte[depth][];
            for (int i = 0; i < depth; i++)
            {
                slots[i] = new byte[frameBytes];
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                head = 0;
                count = 0;
            }
        }

        /// <summary>Returns the buffer to encode into; call CommitSlot afterwards.</summary>
        public byte[] RentSlot()
        {
            lock (gate)
            {
                if (count >= slots.Length)
                {
                    head = (head + 1) % slots.Length; // drop oldest
                    count--;
                }

                return slots[(head + count) % slots.Length];
            }
        }

        public void CommitSlot()
        {
            lock (gate)
            {
                if (count < slots.Length)
                {
                    count++;
                }
            }
        }

        public bool TryDequeue(byte[] dest)
        {
            lock (gate)
            {
                if (count == 0)
                {
                    return false;
                }

                Buffer.BlockCopy(slots[head], 0, dest, 0, dest.Length);
                head = (head + 1) % slots.Length;
                count--;
                return true;
            }
        }
    }

    /// <summary>
    /// Streaming linear-interpolation resampler for interleaved stereo 16-bit
    /// PCM. One frame is carried between calls so chunked input produces the
    /// same sample sequence as one continuous buffer. Linear interpolation is
    /// adequate here: the 48<->45 kHz ratio is small, the controller speaker
    /// and Opus both roll off the top octave, and it keeps this utility free
    /// of audio-framework dependencies.
    /// </summary>
    internal sealed class StereoLinearResampler
    {
        private const int ChunkFrames = 64;
        private readonly double step;
        private readonly short[] window = new short[(ChunkFrames + 1) * 2];
        private double phase;
        private bool primed;

        public StereoLinearResampler(int sourceRate, int destinationRate)
        {
            if (sourceRate <= 0 || destinationRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceRate));
            }
            step = (double)sourceRate / destinationRate;
        }

        /// <summary>Resamples interleaved stereo samples; returns samples written.</summary>
        public int Resample(ReadOnlySpan<short> input, Span<short> output)
        {
            int total = 0;
            while (input.Length >= 2)
            {
                int frames = Math.Min(input.Length / 2, ChunkFrames);
                total += ResampleChunk(input[..(frames * 2)], output[total..]);
                input = input[(frames * 2)..];
            }
            return total;
        }

        private int ResampleChunk(ReadOnlySpan<short> input, Span<short> output)
        {
            int inFrames = input.Length / 2;
            if (!primed)
            {
                window[0] = input[0];
                window[1] = input[1];
                primed = true;
                phase = 0.0; // position 0 = carried frame = first real frame
            }

            input.CopyTo(window.AsSpan(2, input.Length));
            // window now holds inFrames + 1 frames: [0] carried, [1..inFrames] new.
            int written = 0;
            while (phase < inFrames)
            {
                int index = (int)phase;
                double frac = phase - index;
                int leftBase = window[index * 2];
                int rightBase = window[index * 2 + 1];
                int left = leftBase + (int)Math.Round(
                    (window[(index + 1) * 2] - leftBase) * frac);
                int right = rightBase + (int)Math.Round(
                    (window[(index + 1) * 2 + 1] - rightBase) * frac);
                output[written++] = (short)Math.Clamp(left, short.MinValue, short.MaxValue);
                output[written++] = (short)Math.Clamp(right, short.MinValue, short.MaxValue);
                phase += step;
            }

            window[0] = window[inFrames * 2];
            window[1] = window[inFrames * 2 + 1];
            phase -= inFrames;
            return written;
        }
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
        internal static extern bool WriteFile(SafeFileHandle file, byte[] buffer,
            uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

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
