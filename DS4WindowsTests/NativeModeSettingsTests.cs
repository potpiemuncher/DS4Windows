using System.Xml;
using System.Xml.Serialization;
using System.Threading;
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
        Assert.AreEqual(50, options.NativeModeSpeakerVolume);
        Assert.AreEqual(DualSenseControllerOptions.AudioOutputRoute.Auto,
            options.NativeModeRoute);
    }

    [TestMethod]
    public void DualSenseOptionsDto_DefaultsNativeSpeakerVolumeToFiftyPercent()
    {
        var options = new DualSenseControllerOptsDTO();

        Assert.AreEqual(50, options.NativeModeSpeakerVolume);
    }

    [TestMethod]
    public void DualSenseOptions_PersistAndLoadNativeModeSettings()
    {
        var source = new DualSenseControllerOptions(InputDeviceType.DualSense)
        {
            NativeModeSpeakerAudio = false,
            NativeModeSpeakerVolume = 25,
            NativeModeRoute = DualSenseControllerOptions.AudioOutputRoute.Speaker,
        };
        var document = new XmlDocument();
        XmlElement root = document.CreateElement("Controller");
        document.AppendChild(root);

        source.PersistSettings(document, root);

        var destination = new DualSenseControllerOptions(InputDeviceType.DualSense);
        destination.LoadSettings(document, root);
        Assert.IsFalse(destination.NativeModeSpeakerAudio);
        Assert.AreEqual(25, destination.NativeModeSpeakerVolume);
        Assert.AreEqual(DualSenseControllerOptions.AudioOutputRoute.Speaker,
            destination.NativeModeRoute);
    }

    [TestMethod]
    public void BuildServerArguments_UsesPersistedNativeModeOptions()
    {
        var options = new DualSenseControllerOptions(InputDeviceType.DualSense)
        {
            NativeModeSpeakerAudio = false,
            NativeModeSpeakerVolume = 30,
            NativeModeRoute = DualSenseControllerOptions.AudioOutputRoute.Headphone,
        };

        CollectionAssert.AreEqual(new[]
        {
            "serve",
            "--configuration", "composite",
            "--input", "bluetooth",
            "--speaker-audio", "off",
            "--speaker-volume", "30",
            "--route", "headphone",
        }, ControlService.BuildNativeModeServerArguments(options));
    }

    [DataTestMethod]
    [DataRow(-1, 0)]
    [DataRow(101, 100)]
    public void NativeModeSpeakerVolume_ClampsToValidRange(int value, int expected)
    {
        var options = new DualSenseControllerOptions(InputDeviceType.DualSense)
        {
            NativeModeSpeakerVolume = value,
        };

        Assert.AreEqual(expected, options.NativeModeSpeakerVolume);
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
    public void NativeModeControls_ConfirmedDeferredCleanupRestoresStoppedUi()
    {
        var retained =
            DualSenseControllerOptionsWrapper.ProjectNativeModeControls(
                operationInProgress: false,
                sessionActive: true,
                NativeModeState.Stopped);

        Assert.AreEqual("Stop Native Mode", retained.ButtonText);
        Assert.IsTrue(retained.CanToggle);
        Assert.IsFalse(retained.SettingsEnabled);
        Assert.IsFalse(retained.SetupCanRun);

        var released =
            DualSenseControllerOptionsWrapper.ProjectNativeModeControls(
                operationInProgress: false,
                sessionActive: false,
                NativeModeState.Stopped);

        Assert.AreEqual("Start Native Mode", released.ButtonText);
        Assert.IsTrue(released.CanToggle);
        Assert.IsTrue(released.SettingsEnabled);
        Assert.IsTrue(released.SetupCanRun);
    }

    [TestMethod]
    public void NativeModeActivityRefresh_PostsToCapturedUiContext()
    {
        var context = new RecordingSynchronizationContext();
        int refreshCount = 0;

        DualSenseControllerOptionsWrapper.DispatchNativeModePropertyRefresh(
            context, () => refreshCount++);

        Assert.AreEqual(1, context.PostCount);
        Assert.AreEqual(0, refreshCount);

        context.RunPostedCallback();

        Assert.AreEqual(1, refreshCount);
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

    private sealed class RecordingSynchronizationContext :
        SynchronizationContext
    {
        private SendOrPostCallback postedCallback;
        private object postedState;

        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback callback, object state)
        {
            postedCallback = callback;
            postedState = state;
            PostCount++;
        }

        public void RunPostedCallback()
        {
            Assert.IsNotNull(postedCallback);
            SendOrPostCallback callback = postedCallback;
            postedCallback = null;
            callback(postedState);
        }
    }
}
