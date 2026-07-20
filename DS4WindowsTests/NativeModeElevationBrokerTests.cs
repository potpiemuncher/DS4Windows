using System.ComponentModel;
using System.Xml.Linq;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeElevationBrokerTests
{
    [TestMethod]
    public void BuildAttachTaskXml_UsesFixedHighestOnDemandAction()
    {
        string usbipPath = @"C:\Program Files\USBip & Tools\usbip.exe";
        string taskXml = NativeModeElevationBroker.BuildAttachTaskXml(
            usbipPath, "S-1-5-21-1-2-3-1001");
        // Task Scheduler rejects UTF-8 task XML ("unable to switch the
        // encoding"); the file must be written and declared as UTF-16.
        StringAssert.StartsWith(taskXml,
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        XDocument document = XDocument.Parse(taskXml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.AreEqual("HighestAvailable",
            document.Descendants(ns + "RunLevel").Single().Value);
        Assert.AreEqual(0, document.Descendants(ns + "Triggers").Single().Elements().Count());
        Assert.AreEqual(Path.GetFullPath(usbipPath),
            document.Descendants(ns + "Command").Single().Value);
        Assert.AreEqual(NativeModeElevationBroker.AttachArguments,
            document.Descendants(ns + "Arguments").Single().Value);
    }

    [TestMethod]
    public void TaskCommands_UseFixedTaskNameAndNoRuntimeAttachArguments()
    {
        CollectionAssert.AreEqual(new[]
        {
            "/Create", "/TN", NativeModeElevationBroker.AttachTaskName,
            "/XML", @"C:\Temp\attach.xml", "/F",
        }, NativeModeElevationBroker.BuildCreateTaskArguments(
            @"C:\Temp\attach.xml"));
        CollectionAssert.AreEqual(new[]
        {
            "/Run", "/TN", NativeModeElevationBroker.AttachTaskName,
        }, NativeModeElevationBroker.BuildRunTaskArguments());
    }

    [TestMethod]
    public async Task WaitForDeviceArrival_StopsAtFirstPresenceObservation()
    {
        int checks = 0;
        int delays = 0;

        bool arrived = await NativeModeElevationBroker.WaitForDeviceArrivalAsync(
            () => ++checks >= 3,
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.IsTrue(arrived);
        Assert.AreEqual(3, checks);
        Assert.AreEqual(2, delays);
    }

    [TestMethod]
    public async Task WaitForDeviceArrival_ReturnsFalseAtDeadline()
    {
        bool arrived = await NativeModeElevationBroker.WaitForDeviceArrivalAsync(
            () => false, (_, _) => Task.CompletedTask,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.IsFalse(arrived);
    }

    [TestMethod]
    public async Task RunAttach_ReportsSetupRequiredWhenTaskIsMissing()
    {
        var runner = new FakeCommandRunner
        {
            SynchronousResult = new NativeModeCommandResult(1, string.Empty,
                "The system cannot find the file specified."),
        };
        var broker = CreateBroker(runner, administrator: false,
            devicePresent: () => false);

        NativeModeAttachResult result = await broker.RunAttachAsync(
            @"C:\Program Files\USBip\usbip.exe");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(NativeModeAttachFailureKind.SetupRequired,
            result.FailureKind);
        StringAssert.Contains(result.Reason, "Elevation setup required");
        Assert.AreEqual(0, runner.AsyncCalls.Count);
    }

    [TestMethod]
    public async Task RunAttach_WhenElevatedRunsUsbipDirectlyThenConfirmsArrival()
    {
        var runner = new FakeCommandRunner();
        var broker = CreateBroker(runner, administrator: true,
            devicePresent: () => true);
        string usbipPath = @"D:\USBip\usbip.exe";

        NativeModeAttachResult result = await broker.RunAttachAsync(usbipPath);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, runner.AsyncCalls.Count);
        Assert.AreEqual(usbipPath, runner.AsyncCalls[0].Executable);
        CollectionAssert.AreEqual(new[]
        {
            "attach", "-r", "127.0.0.1", "-b", "1-1",
            "--serial", "DS4WSPKCOMP001", "--once",
        }, runner.AsyncCalls[0].Arguments);
        Assert.IsFalse(runner.AsyncCalls[0].Elevate);
    }

    [TestMethod]
    public async Task RunAttach_WhenNotElevatedRunsOnlyTheFixedTask()
    {
        var runner = new FakeCommandRunner();
        var broker = CreateBroker(runner, administrator: false,
            devicePresent: () => true);

        NativeModeAttachResult result = await broker.RunAttachAsync(
            @"C:\Program Files\USBip\usbip.exe");

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, runner.AsyncCalls.Count);
        Assert.AreEqual(@"C:\Windows\System32\schtasks.exe",
            runner.AsyncCalls[0].Executable);
        CollectionAssert.AreEqual(new[]
        {
            "/Run", "/TN", NativeModeElevationBroker.AttachTaskName,
        }, runner.AsyncCalls[0].Arguments);
    }

    [TestMethod]
    public async Task RunAttach_PresenceQueryFailureReturnsSafeAttachFailure()
    {
        var runner = new FakeCommandRunner();
        var broker = CreateBroker(runner, administrator: true,
            devicePresent: () => throw new Win32Exception(5, "access denied"));

        NativeModeAttachResult result = await broker.RunAttachAsync(
            @"C:\Program Files\USBip\usbip.exe");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(NativeModeAttachFailureKind.DeviceArrivalTimeout,
            result.FailureKind);
        StringAssert.Contains(result.Reason, "Could not query present devices");
    }

    [TestMethod]
    public void PresentEnumeration_UsesAllClassesAndExactVirtualParentOnly()
    {
        int observedFlags = 0;

        IReadOnlyList<string> matches = NativeModeDevicePresence
            .GetPresentVirtualDualSenseInstanceIds(flags =>
            {
                observedFlags = flags;
                return new[]
                {
                    NativeModeDevicePresence.VirtualDualSenseParentInstanceId,
                    @"USB\VID_054C&PID_0CE6\E82712345678",
                    @"USB\VID_054C&PID_0CE6&MI_01\DS4WSPKCOMP001&0001",
                };
            });

        Assert.AreEqual(NativeMethods.DIGCF_PRESENT |
            NativeMethods.DIGCF_ALLCLASSES, observedFlags);
        CollectionAssert.AreEqual(new[]
        {
            NativeModeDevicePresence.VirtualDualSenseParentInstanceId,
        }, matches.ToArray());
    }

    [TestMethod]
    public void DirectPresenceEnumeration_PropagatesFailureForFailClosedCleanup()
    {
        Assert.ThrowsException<Win32Exception>(() =>
            NativeModeDevicePresence.GetPresentVirtualDualSenseInstanceIds(
                _ => throw new Win32Exception(5, "probe failed")));
    }

    [TestMethod]
    public async Task ReadinessAwaiter_CompletesOnServingTransition()
    {
        NativeModeState state = NativeModeState.Starting;
        string detail = "Starting";
        EventHandler<NativeModeStateChangedEventArgs> stateChanged = null;
        Task waitTask = NativeModeReadinessAwaiter.WaitForServingAsync(
            () => (state, detail),
            handler => stateChanged += handler,
            handler => stateChanged -= handler,
            TimeSpan.FromSeconds(1), CancellationToken.None);

        state = NativeModeState.Serving;
        detail = "USB/IP server listening";
        stateChanged?.Invoke(this,
            new NativeModeStateChangedEventArgs(state, detail));
        await waitTask;

        Assert.IsNull(stateChanged);
    }

    [TestMethod]
    public async Task ReadinessAwaiter_PropagatesTerminalFailureDetail()
    {
        Task waitTask = NativeModeReadinessAwaiter.WaitForServingAsync(
            () => (NativeModeState.Faulted, "No physical Bluetooth DualSense"),
            _ => { }, _ => { }, TimeSpan.FromSeconds(1),
            CancellationToken.None);

        InvalidOperationException exception =
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await waitTask);
        StringAssert.Contains(exception.Message,
            "No physical Bluetooth DualSense");
    }

    [TestMethod]
    public async Task ReadinessAwaiter_ReportsBoundedTimeout()
    {
        Task waitTask = NativeModeReadinessAwaiter.WaitForServingAsync(
            () => (NativeModeState.Starting, "Starting"),
            _ => { }, _ => { }, TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        TimeoutException exception = await Assert.ThrowsExceptionAsync<TimeoutException>(
            async () => await waitTask);
        StringAssert.Contains(exception.Message, "did not become ready");
    }

    private static NativeModeElevationBroker CreateBroker(
        FakeCommandRunner runner, bool administrator,
        Func<bool> devicePresent)
    {
        return new NativeModeElevationBroker(_ => true, () => administrator,
            runner, devicePresent, (_, _) => Task.CompletedTask,
            @"C:\Windows\System32\schtasks.exe");
    }

    private sealed class FakeCommandRunner : INativeModeCommandRunner
    {
        public NativeModeCommandResult SynchronousResult { get; set; } =
            new NativeModeCommandResult(0, string.Empty, string.Empty);
        public NativeModeCommandResult AsynchronousResult { get; set; } =
            new NativeModeCommandResult(0, string.Empty, string.Empty);
        public List<CommandCall> AsyncCalls { get; } = new List<CommandCall>();

        public NativeModeCommandResult Run(string executable,
            IReadOnlyList<string> arguments) => SynchronousResult;

        public Task<NativeModeCommandResult> RunAsync(string executable,
            IReadOnlyList<string> arguments, bool elevate,
            CancellationToken cancellationToken)
        {
            AsyncCalls.Add(new CommandCall(executable, arguments.ToArray(), elevate));
            return Task.FromResult(AsynchronousResult);
        }
    }

    private sealed record CommandCall(string Executable, string[] Arguments,
        bool Elevate);
}
