namespace VirtualDualSenseUsbip.Device;

/// <summary>
/// EP0 control-transfer state machine for the virtual wired DualSense.
///
/// Treats EP0 as a stateful device (current configuration, per-interface alt
/// settings, idle rates) rather than a descriptor lookup table. Standard
/// descriptor requests are served byte-exact from <see cref="DescriptorSet"/>;
/// unsupported or malformed requests stall rather than fabricate success.
/// </summary>
public sealed class ControlEndpoint
{
    private readonly DescriptorSet descriptors;
    private readonly Dictionary<byte, byte> interfaceAltSettings = new();
    private readonly Dictionary<byte, byte> idleRates = new();

    public byte ConfigurationValue { get; private set; }
    public bool SelfPowered { get; init; } = true; // config bmAttributes 0xC0

    /// <summary>Raised on SET_INTERFACE so the audio layer can (de)activate an
    /// isochronous streaming alt setting. (interface, altSetting).</summary>
    public event Action<byte, byte>? InterfaceAltChanged;

    public ControlEndpoint(DescriptorSet descriptors)
    {
        this.descriptors = descriptors;
    }

    public byte GetAltSetting(byte interfaceNumber) =>
        interfaceAltSettings.GetValueOrDefault(interfaceNumber);

    /// <summary>Handles one EP0 SETUP transaction. <paramref name="outData"/> is
    /// the host-to-device data stage payload (empty for IN transfers).</summary>
    public ControlResult Handle(UsbSetupPacket setup, ReadOnlySpan<byte> outData)
    {
        return setup.Type switch
        {
            UsbSetupPacket.TypeStandard => HandleStandard(setup),
            UsbSetupPacket.TypeClass => HandleClass(setup, outData),
            _ => ControlResult.Stalled(),
        };
    }

    private ControlResult HandleStandard(UsbSetupPacket setup)
    {
        switch (setup.Request)
        {
            case UsbStandardRequest.GetDescriptor:
                return descriptors.GetDescriptor(setup);

            case UsbStandardRequest.SetConfiguration:
                ConfigurationValue = (byte)setup.Value;
                return ControlResult.Ack();

            case UsbStandardRequest.GetConfiguration:
                return ControlResult.Ok(new[] { ConfigurationValue });

            case UsbStandardRequest.SetInterface:
            {
                byte interfaceNumber = (byte)setup.Index;
                byte alt = (byte)setup.Value;
                interfaceAltSettings[interfaceNumber] = alt;
                InterfaceAltChanged?.Invoke(interfaceNumber, alt);
                return ControlResult.Ack();
            }

            case UsbStandardRequest.GetInterface:
                return ControlResult.Ok(new[] { GetAltSetting((byte)setup.Index) });

            case UsbStandardRequest.GetStatus:
                return HandleGetStatus(setup);

            case UsbStandardRequest.SetAddress:
                // The virtual host controller owns addressing; accept harmlessly.
                return ControlResult.Ack();

            case UsbStandardRequest.ClearFeature:
            case UsbStandardRequest.SetFeature:
                // Accept ENDPOINT_HALT / remote-wake toggles; nothing to persist
                // for the spike beyond acknowledging them.
                return ControlResult.Ack();

            default:
                return ControlResult.Stalled();
        }
    }

    private ControlResult HandleGetStatus(UsbSetupPacket setup)
    {
        ushort status = setup.Recipient switch
        {
            // D0 self-powered, D1 remote-wake (report self-powered per config 0xC0).
            UsbSetupPacket.RecipientDevice => (ushort)(SelfPowered ? 0x0001 : 0x0000),
            _ => 0x0000, // interface reserved-zero; endpoint halt not tracked in spike
        };

        return ControlResult.Ok(new[] { (byte)(status & 0xFF), (byte)(status >> 8) });
    }

    private ControlResult HandleClass(UsbSetupPacket setup, ReadOnlySpan<byte> outData)
    {
        // HID class requests are directed at the HID interface.
        switch (setup.Request)
        {
            case UsbHidRequest.SetIdle:
                idleRates[(byte)(setup.Value & 0xFF)] = (byte)(setup.Value >> 8);
                return ControlResult.Ack();

            case UsbHidRequest.GetIdle:
                return ControlResult.Ok(new[] { idleRates.GetValueOrDefault((byte)(setup.Value & 0xFF)) });

            case UsbHidRequest.SetProtocol:
            case UsbHidRequest.SetReport:
                // Accept output/feature writes; routing to the BT relay is added
                // in a later milestone. Acknowledge with no data stage.
                return ControlResult.Ack();

            case UsbHidRequest.GetProtocol:
                return ControlResult.Ok(new byte[] { 1 }); // report protocol

            case UsbHidRequest.GetReport:
                // Feature/input report reads over EP0. Real feature reports must be
                // captured before we can answer authentically; stall for now so we
                // never feed a game fabricated calibration data.
                return ControlResult.Stalled();

            default:
                return ControlResult.Stalled();
        }
    }
}
