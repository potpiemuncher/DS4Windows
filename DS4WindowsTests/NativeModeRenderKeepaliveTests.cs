using System.Collections.Concurrent;
using DS4Windows;

namespace DS4WindowsTests;

[TestClass]
public class NativeModeRenderKeepaliveTests
{
    [TestMethod]
    public async Task WaitForReady_SelectsOnlyNewDualSenseRenderEndpoint()
    {
        var accessor = new FakeEndpointAccessor();
        accessor.Add("existing-speakers", "SteelSeries Sonar - Gaming");
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var presence = new FakeDevicePresence { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);

        keepalive.BeginSession();
        accessor.Add("other-new", "HDMI Output");
        accessor.Add("virtual-render",
            "Speakers (DualSense Wireless Controller)");
        notifications.Notify();

        await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(new[] { "virtual-render" },
            factory.EndpointIds.ToArray());
        Assert.AreEqual(1, factory.Output.StartCount);
        Assert.IsTrue(keepalive.HasSession);

        await RemoveAndReleaseAsync(keepalive, accessor, notifications,
            presence, factory.Output);
    }

    [TestMethod]
    public async Task WaitForReady_DoesNotCompleteUntilOutputStartReturns()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var output = new FakeOutput { BlockStart = true };
        var factory = new FakeOutputFactory(output);
        var presence = new FakeDevicePresence { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);

        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");
        Task ready = Task.Run(async () =>
            await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(2)));

        Assert.IsTrue(output.StartEntered.Wait(TimeSpan.FromSeconds(1)));
        Assert.IsFalse(ready.IsCompleted,
            "Attached readiness must wait for WASAPI Play to succeed.");
        output.AllowStart.Set();
        await ready;

        await RemoveAndReleaseAsync(keepalive, accessor, notifications,
            presence, output);
    }

    [TestMethod]
    public async Task ExistingDualSenseEndpoint_IsNeverClaimedByNewSession()
    {
        var accessor = new FakeEndpointAccessor();
        accessor.Add("preexisting-pad", "DualSense Wireless Controller");
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var presence = new FakeDevicePresence();
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);

        keepalive.BeginSession();

        await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            keepalive.WaitForReadyAsync(TimeSpan.FromMilliseconds(50)));
        Assert.AreEqual(0, factory.EndpointIds.Count);

        keepalive.BeginTeardown();
        await keepalive.CompleteTeardownAsync();
        Assert.IsFalse(keepalive.HasSession);
    }

    [TestMethod]
    public async Task Teardown_RetainsOutputUntilEndpointAndParentAreGone()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var presence = new FakeDevicePresence { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);
        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");
        await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1));

        // This models the interval before and through Process.Kill.
        keepalive.BeginTeardown();
        await Task.Delay(150);
        Assert.AreEqual(0, presence.CheckCount);
        await keepalive.CompleteTeardownAsync();
        Assert.IsTrue(presence.CheckCount > 0);
        Assert.AreEqual(0, factory.Output.DisposeCount);
        Assert.IsTrue(keepalive.HasSession);

        // Endpoint inactivity alone is not enough while the USB parent remains.
        accessor.Remove("virtual-render");
        notifications.Notify();
        await Task.Delay(150);
        Assert.AreEqual(0, factory.Output.DisposeCount);
        Assert.IsTrue(keepalive.HasSession);

        presence.Present = false;
        notifications.Notify();
        await WaitUntilAsync(() => factory.Output.DisposeCount == 1);
        Assert.IsFalse(keepalive.HasSession);
    }

    [TestMethod]
    public async Task TeardownTimeout_ReleasesOnLaterRemovalNotification()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var presence = new FakeDevicePresence { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);
        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");
        await keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1));

        keepalive.BeginTeardown();
        await keepalive.CompleteTeardownAsync();
        Assert.AreEqual(0, factory.Output.DisposeCount);

        accessor.Remove("virtual-render");
        presence.Present = false;
        notifications.Notify();

        await WaitUntilAsync(() => factory.Output.DisposeCount == 1);
        Assert.AreEqual(1, notifications.DisposeCount);
        Assert.IsFalse(keepalive.HasSession);
    }

    [TestMethod]
    public async Task PartiallyStartedOutput_IsRetainedAfterStartFailure()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var output = new FakeOutput { StartException = new InvalidOperationException("init") };
        var factory = new FakeOutputFactory(output);
        var presence = new FakeDevicePresence { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);
        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(1)));
        keepalive.BeginTeardown();
        await keepalive.CompleteTeardownAsync();
        Assert.AreEqual(0, output.DisposeCount,
            "A possibly activated client must survive while the device exists.");

        accessor.Remove("virtual-render");
        presence.Present = false;
        notifications.Notify();
        await WaitUntilAsync(() => output.DisposeCount == 1);
    }

    [TestMethod]
    public async Task ConcurrentReadinessAndNotifications_CreateOneOutput()
    {
        var accessor = new FakeEndpointAccessor();
        var notifications = new FakeNotificationSource();
        var factory = new FakeOutputFactory();
        var presence = new FakeDevicePresence { Present = true };
        NativeModeRenderKeepalive keepalive = CreateKeepalive(accessor,
            notifications, factory, presence);
        keepalive.BeginSession();
        accessor.Add("virtual-render", "DualSense Wireless Controller");

        Task[] waiters = Enumerable.Range(0, 8)
            .Select(_ => keepalive.WaitForReadyAsync(TimeSpan.FromSeconds(2)))
            .ToArray();
        Parallel.For(0, 50, _ => notifications.Notify());
        await Task.WhenAll(waiters);

        Assert.AreEqual(1, factory.EndpointIds.Count);
        Assert.AreEqual(1, factory.Output.StartCount);

        await RemoveAndReleaseAsync(keepalive, accessor, notifications,
            presence, factory.Output);
    }

    private static NativeModeRenderKeepalive CreateKeepalive(
        FakeEndpointAccessor accessor, FakeNotificationSource notifications,
        FakeOutputFactory factory, FakeDevicePresence presence) =>
        new NativeModeRenderKeepalive(accessor, notifications, factory, presence,
            (_, _) => { });

    private static async Task RemoveAndReleaseAsync(
        NativeModeRenderKeepalive keepalive, FakeEndpointAccessor accessor,
        FakeNotificationSource notifications, FakeDevicePresence presence,
        FakeOutput output)
    {
        keepalive.BeginTeardown();
        accessor.Remove("virtual-render");
        presence.Present = false;
        notifications.Notify();
        await keepalive.CompleteTeardownAsync();
        await WaitUntilAsync(() => output.DisposeCount == 1);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.IsTrue(predicate(), "The asynchronous lifecycle transition timed out.");
    }

    private sealed class FakeEndpointAccessor : INativeModeAudioEndpointAccessor
    {
        private readonly object gate = new();
        private readonly List<NativeModeAudioEndpoint> renderEndpoints = new();

        public IReadOnlyList<NativeModeAudioEndpoint> GetActiveEndpoints(
            NativeModeAudioFlow flow)
        {
            lock (gate)
                return flow == NativeModeAudioFlow.Render
                    ? renderEndpoints.ToArray()
                    : Array.Empty<NativeModeAudioEndpoint>();
        }

        public string GetDefaultEndpointId(NativeModeAudioFlow flow,
            NativeModeAudioRole role) => throw new NotSupportedException();

        public void SetDefaultEndpoint(string endpointId,
            NativeModeAudioRole role) => throw new NotSupportedException();

        public void Add(string id, string name)
        {
            lock (gate)
                renderEndpoints.Add(new NativeModeAudioEndpoint(id, name));
        }

        public void Remove(string id)
        {
            lock (gate)
            {
                renderEndpoints.RemoveAll(endpoint => string.Equals(endpoint.Id,
                    id, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private sealed class FakeNotificationSource :
        INativeModeAudioNotificationSource
    {
        private readonly object gate = new();
        private readonly List<Action> callbacks = new();
        public int DisposeCount;

        public IDisposable Subscribe(Action endpointOrDefaultChanged)
        {
            lock (gate)
                callbacks.Add(endpointOrDefaultChanged);
            return new Subscription(this, endpointOrDefaultChanged);
        }

        public void Notify()
        {
            Action[] snapshot;
            lock (gate)
                snapshot = callbacks.ToArray();
            foreach (Action callback in snapshot)
                callback();
        }

        private void Remove(Action callback)
        {
            lock (gate)
                callbacks.Remove(callback);
            Interlocked.Increment(ref DisposeCount);
        }

        private sealed class Subscription : IDisposable
        {
            private FakeNotificationSource owner;
            private readonly Action callback;

            public Subscription(FakeNotificationSource owner, Action callback)
            {
                this.owner = owner;
                this.callback = callback;
            }

            public void Dispose() =>
                Interlocked.Exchange(ref owner, null)?.Remove(callback);
        }
    }

    private sealed class FakeOutputFactory : INativeModeRenderOutputFactory
    {
        private readonly FakeOutput suppliedOutput;
        public ConcurrentQueue<string> EndpointIds { get; } = new();
        public FakeOutput Output { get; private set; }

        public FakeOutputFactory(FakeOutput suppliedOutput = null)
        {
            this.suppliedOutput = suppliedOutput;
        }

        public INativeModeRenderOutput Create(string endpointId)
        {
            EndpointIds.Enqueue(endpointId);
            Output = suppliedOutput ?? new FakeOutput();
            return Output;
        }
    }

    private sealed class FakeOutput : INativeModeRenderOutput
    {
        public bool BlockStart;
        public Exception StartException;
        public ManualResetEventSlim StartEntered { get; } = new(false);
        public ManualResetEventSlim AllowStart { get; } = new(false);
        public int StartCount;
        public int DisposeCount;

        public void Start()
        {
            Interlocked.Increment(ref StartCount);
            StartEntered.Set();
            if (BlockStart)
                Assert.IsTrue(AllowStart.Wait(TimeSpan.FromSeconds(2)));
            if (StartException != null)
                throw StartException;
        }

        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private sealed class FakeDevicePresence : INativeModeVirtualDevicePresence
    {
        public volatile bool Present;
        public int CheckCount;

        public bool IsPresent()
        {
            Interlocked.Increment(ref CheckCount);
            return Present;
        }
    }
}
