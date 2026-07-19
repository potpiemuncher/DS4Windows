using System.Xml;
using System.Xml.Serialization;
using DS4Windows;
using DS4Windows.InputDevices;
using DS4WinWPF.DS4Control.DTOXml;
using DS4WinWPF.DS4Forms.ViewModels;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeSettingsTests
{
    [TestMethod]
    public void DualSenseOptions_DefaultToValidatedNativeAudioConfiguration()
    {
        var options = new DualSenseControllerOptions(InputDeviceType.DualSense);

        Assert.IsTrue(options.NativeModeSpeakerAudio);
        Assert.AreEqual(DualSenseControllerOptions.AudioOutputRoute.Auto,
            options.NativeModeRoute);
    }

    [TestMethod]
    public void DualSenseOptions_PersistAndLoadNativeModeSettings()
    {
        var source = new DualSenseControllerOptions(InputDeviceType.DualSense)
        {
            NativeModeSpeakerAudio = false,
            NativeModeRoute = DualSenseControllerOptions.AudioOutputRoute.Speaker,
        };
        var document = new XmlDocument();
        XmlElement root = document.CreateElement("Controller");
        document.AppendChild(root);

        source.PersistSettings(document, root);

        var destination = new DualSenseControllerOptions(InputDeviceType.DualSense);
        destination.LoadSettings(document, root);
        Assert.IsFalse(destination.NativeModeSpeakerAudio);
        Assert.AreEqual(DualSenseControllerOptions.AudioOutputRoute.Speaker,
            destination.NativeModeRoute);
    }

    [TestMethod]
    public void BuildServerArguments_UsesPersistedNativeModeOptions()
    {
        var options = new DualSenseControllerOptions(InputDeviceType.DualSense)
        {
            NativeModeSpeakerAudio = false,
            NativeModeRoute = DualSenseControllerOptions.AudioOutputRoute.Headphone,
        };

        CollectionAssert.AreEqual(new[]
        {
            "serve",
            "--configuration", "composite",
            "--input", "bluetooth",
            "--speaker-audio", "off",
            "--route", "headphone",
        }, ControlService.BuildNativeModeServerArguments(options));
    }

    [TestMethod]
    public void AppSettingsDto_RoundTripsUsbipExecutablePath()
    {
        var source = new BackingStore
        {
            usbipExePath = @"D:\Tools\usbip.exe",
        };
        var dto = new AppSettingsDTO();
        dto.MapFrom(source);
        var serializer = new XmlSerializer(typeof(AppSettingsDTO));
        using var writer = new StringWriter();
        serializer.Serialize(writer, dto);
        using var reader = new StringReader(writer.ToString());
        dto = (AppSettingsDTO)serializer.Deserialize(reader);
        var destination = new BackingStore();

        dto.MapTo(destination);

        Assert.AreEqual(@"D:\Tools\usbip.exe", destination.usbipExePath);
    }

    [TestMethod]
    public void AppSettingsDto_UsesDefaultUsbipPathWhenValueIsBlank()
    {
        var destination = new BackingStore();
        var dto = new AppSettingsDTO
        {
            UsbipExePath = "  ",
        };

        dto.MapTo(destination);

        Assert.AreEqual(BackingStore.DEFAULT_USBIP_EXE_PATH,
            destination.usbipExePath);
    }

    [DataTestMethod]
    [DataRow(NativeModeState.Serving,
        "Server running — attaching virtual DualSense...")]
    [DataRow(NativeModeState.PadLost,
        "Pad lost — press PS, then Start Native Mode again.")]
    [DataRow(NativeModeState.SetupRequired,
        "Elevation setup required — select Set up native mode.")]
    public void StatusForState_ProvidesActionableNativeModeText(
        NativeModeState state, string expected)
    {
        Assert.AreEqual(expected,
            DualSenseControllerOptionsWrapper.StatusForState(state, null));
    }

    [TestMethod]
    public void AppendFixturesArgument_AddsPackagedFixturesBesideServer()
    {
        string[] baseArguments = { "serve", "--configuration", "composite" };
        string serverPath = @"C:\Apps\DS4Windows\native\VirtualDualSenseUsbip.exe";
        string expectedFixtures = @"C:\Apps\DS4Windows\native\fixtures\dualsense_usb_0ce6";

        string[] augmented = ControlService.AppendFixturesArgumentIfPackaged(
            baseArguments, serverPath,
            path => path == Path.Combine(expectedFixtures, "device.bin"));

        CollectionAssert.AreEqual(
            new[]
            {
                "serve", "--configuration", "composite",
                "--fixtures", expectedFixtures,
            },
            augmented);
    }

    [TestMethod]
    public void AppendFixturesArgument_LeavesDevelopmentLayoutUntouched()
    {
        string[] baseArguments = { "serve" };

        string[] untouched = ControlService.AppendFixturesArgumentIfPackaged(
            baseArguments,
            @"C:\repo\utils\VirtualDualSenseUsbip\bin\Release\net8.0\VirtualDualSenseUsbip.exe",
            _ => false);

        Assert.AreSame(baseArguments, untouched);
    }
}
