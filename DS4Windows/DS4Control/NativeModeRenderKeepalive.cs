/*
DS4Windows
Copyright (C) 2026  DS4Windows contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DS4Windows
{
    internal interface INativeModeRenderOutput : IDisposable
    {
        void Start();
    }

    internal interface INativeModeRenderOutputFactory
    {
        INativeModeRenderOutput Create(string endpointId);
    }

    internal interface INativeModeVirtualDevicePresence
    {
        bool IsPresent();
    }

    /// <summary>
    /// Keeps one shared-mode render client open on the virtual DualSense for
    /// the complete composite session. The game may close and reopen its own
    /// client during focus changes; this client prevents that transition from
    /// closing the kernel USB audio render pin.
    /// </summary>
    internal sealed class NativeModeRenderKeepalive
    {
        private static readonly TimeSpan EndpointPollInterval =
            TimeSpan.FromMilliseconds(100);

        private readonly object sessionGate = new object();
        private readonly INativeModeAudioEndpointAccessor endpointAccessor;
        private readonly INativeModeAudioNotificationSource notificationSource;
        private readonly INativeModeRenderOutputFactory outputFactory;
        private readonly INativeModeVirtualDevicePresence devicePresence;
        private readonly Action<string, bool> log;
        private Session currentSession;

        public NativeModeRenderKeepalive() : this(
            new WindowsNativeModeAudioEndpointAccessor(),
            new WindowsNativeModeAudioNotificationSource(),
            new WasapiNativeModeRenderOutputFactory(),
            new WindowsNativeModeVirtualDevicePresence(),
            (message, warning) => AppLogger.LogToGui(message, warning))
        {
        }

        internal NativeModeRenderKeepalive(
            INativeModeAudioEndpointAccessor endpointAccessor,
            INativeModeAudioNotificationSource notificationSource,
            INativeModeRenderOutputFactory outputFactory,
            INativeModeVirtualDevicePresence devicePresence,
            Action<string, bool> log)
        {
            this.endpointAccessor = endpointAccessor ??
                throw new ArgumentNullException(nameof(endpointAccessor));
            this.notificationSource = notificationSource ??
                throw new ArgumentNullException(nameof(notificationSource));
            this.outputFactory = outputFactory ??
                throw new ArgumentNullException(nameof(outputFactory));
            this.devicePresence = devicePresence ??
                throw new ArgumentNullException(nameof(devicePresence));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public bool HasSession
        {
            get
            {
                lock (sessionGate)
                    return currentSession != null;
            }
        }

        /// <summary>
        /// Captures active render endpoints and registers for endpoint changes.
        /// Call this before USB/IP attach so only the newly-added virtual
        /// DualSense can be selected.
        /// </summary>
        public void BeginSession()
        {
            lock (sessionGate)
            {
                if (currentSession != null)
                {
                    throw new InvalidOperationException(
                        "A native-mode render keepalive session is already active.");
                }
            }

            HashSet<string> activeBeforeAttach = endpointAccessor
                .GetActiveEndpoints(NativeModeAudioFlow.Render)
                .Select(endpoint => endpoint.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var session = new Session(activeBeforeAttach, endpointAccessor,
                notificationSource, outputFactory, devicePresence, log,
                SessionReleased);

            lock (sessionGate)
            {
                if (currentSession != null)
                {
                    throw new InvalidOperationException(
                        "A native-mode render keepalive session is already active.");
                }
                currentSession = session;
            }

            try
            {
                session.StartMonitoring();
            }
            catch
            {
                lock (sessionGate)
                {
                    if (ReferenceEquals(currentSession, session))
                        currentSession = null;
                }
                session.ReleaseBeforeOutputOpen();
                throw;
            }
        }

        /// <summary>
        /// Does not complete until an infinite silent render client is running
        /// on the newly-added DualSense endpoint. Consequently the caller must
        /// not publish Attached before this task succeeds.
        /// </summary>
        public Task WaitForReadyAsync(TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Session session;
            lock (sessionGate)
                session = currentSession;
            if (session == null)
            {
                throw new InvalidOperationException(
                    "The native-mode render keepalive session has not started.");
            }

            return session.WaitForReadyAsync(timeout, cancellationToken);
        }

        /// <summary>
        /// Arms deferred release before the server process is killed. This does
        /// not stop or dispose the render client while the endpoint/device is
        /// still present.
        /// </summary>
        public void BeginTeardown()
        {
            Session session;
            lock (sessionGate)
                session = currentSession;
            session?.BeginTeardown();
        }

        /// <summary>
        /// Rechecks removal after the child-process teardown has completed. If
        /// removal timed out, the session remains subscribed and owns the
        /// playing output until a later notification/poll confirms that both
        /// the endpoint and virtual parent are gone.
        /// </summary>
        public Task CompleteTeardownAsync()
        {
            Session session;
            lock (sessionGate)
                session = currentSession;
            return session?.CheckRemovalNowAsync() ?? Task.CompletedTask;
        }

        private void SessionReleased(Session session)
        {
            lock (sessionGate)
            {
                if (ReferenceEquals(currentSession, session))
                    currentSession = null;
            }
        }

        private sealed class Session
        {
            private const string DualSenseEndpointMarker =
                "DualSense Wireless Controller";

            private readonly object stateGate = new object();
            private readonly HashSet<string> activeBeforeAttach;
            private readonly INativeModeAudioEndpointAccessor endpointAccessor;
            private readonly INativeModeAudioNotificationSource notificationSource;
            private readonly INativeModeRenderOutputFactory outputFactory;
            private readonly INativeModeVirtualDevicePresence devicePresence;
            private readonly Action<string, bool> log;
            private readonly Action<Session> released;
            private readonly SemaphoreSlim endpointChanged = new SemaphoreSlim(0, 1);
            private readonly SemaphoreSlim operationGate = new SemaphoreSlim(1, 1);
            private readonly CancellationTokenSource readinessCancellation =
                new CancellationTokenSource();

            private IDisposable notificationRegistration;
            private INativeModeRenderOutput output;
            private string endpointId;
            private Exception startupFailure;
            private bool removalMonitorStarted;
            private bool ready;
            private bool teardownStarted;
            private bool releaseCompleted;
            private bool retentionLogged;

            public Session(HashSet<string> activeBeforeAttach,
                INativeModeAudioEndpointAccessor endpointAccessor,
                INativeModeAudioNotificationSource notificationSource,
                INativeModeRenderOutputFactory outputFactory,
                INativeModeVirtualDevicePresence devicePresence,
                Action<string, bool> log, Action<Session> released)
            {
                this.activeBeforeAttach = activeBeforeAttach;
                this.endpointAccessor = endpointAccessor;
                this.notificationSource = notificationSource;
                this.outputFactory = outputFactory;
                this.devicePresence = devicePresence;
                this.log = log;
                this.released = released;
            }

            public void StartMonitoring()
            {
                IDisposable registration = notificationSource.Subscribe(SignalChanged);
                lock (stateGate)
                {
                    if (releaseCompleted)
                    {
                        registration.Dispose();
                        return;
                    }
                    notificationRegistration = registration;
                }
            }

            public async Task WaitForReadyAsync(TimeSpan timeout,
                CancellationToken cancellationToken)
            {
                if (timeout <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(timeout));

                using var timeoutCancellation = new CancellationTokenSource(timeout);
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeoutCancellation.Token,
                    readinessCancellation.Token);

                try
                {
                    while (true)
                    {
                        linkedCancellation.Token.ThrowIfCancellationRequested();
                        if (await TryStartOutputAsync().ConfigureAwait(false))
                            return;

                        await endpointChanged.WaitAsync(EndpointPollInterval,
                            linkedCancellation.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (
                    timeoutCancellation.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested &&
                    !readinessCancellation.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "The virtual DualSense render endpoint did not become ready.");
                }
            }

            public void BeginTeardown()
            {
                lock (stateGate)
                {
                    if (releaseCompleted || teardownStarted)
                        return;
                    teardownStarted = true;
                }

                readinessCancellation.Cancel();
                SignalChanged();
            }

            public async Task CheckRemovalNowAsync()
            {
                BeginTeardown();
                SignalChanged();
                if (await TryReleaseAfterRemovalAsync().ConfigureAwait(false))
                    return;

                bool startMonitor = false;
                lock (stateGate)
                {
                    if (!releaseCompleted && !removalMonitorStarted)
                    {
                        removalMonitorStarted = true;
                        startMonitor = true;
                    }
                }

                if (startMonitor)
                    _ = Task.Run(MonitorRemovalAsync);
                SignalChanged();
            }

            public void ReleaseBeforeOutputOpen()
            {
                readinessCancellation.Cancel();
                ReleaseResources(null, null);
            }

            private async Task<bool> TryStartOutputAsync()
            {
                await operationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    lock (stateGate)
                    {
                        if (releaseCompleted || teardownStarted)
                            throw new OperationCanceledException(
                                "Native-mode render keepalive teardown has started.");
                        if (ready)
                            return true;
                        if (startupFailure != null)
                        {
                            throw new InvalidOperationException(
                                "The native-mode render keepalive could not start.",
                                startupFailure);
                        }
                    }

                    NativeModeAudioEndpoint candidate = endpointAccessor
                        .GetActiveEndpoints(NativeModeAudioFlow.Render)
                        .FirstOrDefault(endpoint =>
                            !activeBeforeAttach.Contains(endpoint.Id) &&
                            endpoint.FriendlyName?.Contains(DualSenseEndpointMarker,
                                StringComparison.OrdinalIgnoreCase) == true);
                    if (candidate == null)
                        return false;

                    INativeModeRenderOutput createdOutput = null;
                    try
                    {
                        createdOutput = outputFactory.Create(candidate.Id);
                        lock (stateGate)
                        {
                            endpointId = candidate.Id;
                            // Publish ownership before Init/Play. If either
                            // partially activates the pin and then throws, the
                            // failed lease must survive until PnP removal too.
                            output = createdOutput;
                        }

                        createdOutput.Start();
                        lock (stateGate)
                            ready = true;
                        log($"[native] Holding the virtual DualSense render pin open " +
                            $"on '{candidate.FriendlyName}'.", false);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        lock (stateGate)
                            startupFailure = ex;
                        // Do not dispose a possibly activated WASAPI client while
                        // the composite device is attached. The startup caller
                        // will remove the virtual device, then teardown releases it.
                        throw new InvalidOperationException(
                            "Could not start the mandatory virtual DualSense render " +
                            $"keepalive on '{candidate.FriendlyName}': {ex.Message}", ex);
                    }
                }
                finally
                {
                    operationGate.Release();
                }
            }

            private async Task MonitorRemovalAsync()
            {
                while (!await TryReleaseAfterRemovalAsync().ConfigureAwait(false))
                {
                    try
                    {
                        await endpointChanged.WaitAsync(EndpointPollInterval)
                            .ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            }

            private async Task<bool> TryReleaseAfterRemovalAsync()
            {
                await operationGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    INativeModeRenderOutput outputToDispose;
                    IDisposable registrationToDispose;
                    string trackedEndpointId;
                    lock (stateGate)
                    {
                        if (releaseCompleted)
                            return true;
                        if (!teardownStarted)
                            return false;

                        outputToDispose = output;
                        trackedEndpointId = endpointId;
                    }

                    // No output was ever opened, so there is no render pin whose
                    // close must be deferred until PnP removal.
                    if (outputToDispose == null)
                    {
                        lock (stateGate)
                        {
                            registrationToDispose = notificationRegistration;
                            notificationRegistration = null;
                            releaseCompleted = true;
                        }
                        ReleaseResources(registrationToDispose, null);
                        return true;
                    }

                    bool endpointPresent;
                    bool virtualDevicePresent;
                    try
                    {
                        endpointPresent = endpointAccessor
                            .GetActiveEndpoints(NativeModeAudioFlow.Render)
                            .Any(endpoint => string.Equals(endpoint.Id,
                                trackedEndpointId,
                                StringComparison.OrdinalIgnoreCase));
                        virtualDevicePresent = devicePresence.IsPresent();
                    }
                    catch (Exception ex)
                    {
                        log($"[native] Could not confirm virtual audio removal; " +
                            $"the render keepalive remains active: {ex.Message}", true);
                        return false;
                    }

                    // Require both signals. An endpoint can become inactive while
                    // the parent still exists; disposing at that point could be
                    // the very voluntary alt-setting transition this guard avoids.
                    if (endpointPresent || virtualDevicePresent)
                    {
                        bool shouldLog;
                        lock (stateGate)
                        {
                            shouldLog = !retentionLogged;
                            retentionLogged = true;
                        }
                        if (shouldLog)
                        {
                            log("[native] Virtual audio removal is not complete; " +
                                "retaining the render keepalive until the endpoint and " +
                                "device are both gone.", true);
                        }
                        return false;
                    }

                    lock (stateGate)
                    {
                        registrationToDispose = notificationRegistration;
                        notificationRegistration = null;
                        output = null;
                        releaseCompleted = true;
                    }
                    ReleaseResources(registrationToDispose, outputToDispose);
                    log("[native] Released the render keepalive after confirmed " +
                        "virtual endpoint and device removal.", false);
                    return true;
                }
                finally
                {
                    operationGate.Release();
                }
            }

            private void ReleaseResources(IDisposable registration,
                INativeModeRenderOutput outputToDispose)
            {
                try
                {
                    registration?.Dispose();
                }
                catch (Exception ex)
                {
                    log($"[native] Could not unregister render endpoint monitoring: " +
                        ex.Message, true);
                }

                try
                {
                    outputToDispose?.Dispose();
                }
                catch (Exception ex)
                {
                    log($"[native] Could not dispose the removed render endpoint: " +
                        ex.Message, true);
                }

                readinessCancellation.Dispose();
                endpointChanged.Dispose();
                released(this);
            }

            private void SignalChanged()
            {
                lock (stateGate)
                {
                    if (releaseCompleted)
                        return;
                }

                try
                {
                    endpointChanged.Release();
                }
                catch (SemaphoreFullException)
                {
                    // One pending pulse is enough to force a fresh enumeration.
                }
                catch (ObjectDisposedException)
                {
                    // A final callback can race notification unregistration.
                }
            }
        }
    }

    internal sealed class WasapiNativeModeRenderOutputFactory :
        INativeModeRenderOutputFactory
    {
        public INativeModeRenderOutput Create(string endpointId) =>
            new WasapiNativeModeRenderOutput(endpointId);

        private sealed class WasapiNativeModeRenderOutput :
            INativeModeRenderOutput
        {
            private readonly string endpointId;
            private MMDevice endpoint;
            private WasapiOut output;
            private int started;
            private int disposed;

            public WasapiNativeModeRenderOutput(string endpointId)
            {
                this.endpointId = !string.IsNullOrWhiteSpace(endpointId)
                    ? endpointId
                    : throw new ArgumentException("An endpoint id is required.",
                        nameof(endpointId));
            }

            public void Start()
            {
                if (Interlocked.Exchange(ref started, 1) != 0)
                    throw new InvalidOperationException("The render output already started.");

                using var enumerator = new MMDeviceEnumerator();
                endpoint = enumerator.GetDevice(endpointId);
                using AudioClient audioClient = endpoint.AudioClient;
                WaveFormat mixFormat = audioClient.MixFormat;

                output = new WasapiOut(endpoint, AudioClientShareMode.Shared,
                    useEventSync: true, latency: 20);
                output.Init(new SilenceProvider(mixFormat));
                output.Play();
                // WasapiOut.Play publishes Playing before its worker calls
                // IAudioClient.Start. Clock movement proves the render pin is
                // actually running before Native Mode can report Attached.
                DateTimeOffset deadline = DateTimeOffset.UtcNow +
                    TimeSpan.FromSeconds(3);
                Exception lastStartFailure = null;
                while (DateTimeOffset.UtcNow < deadline &&
                    output.PlaybackState == PlaybackState.Playing)
                {
                    try
                    {
                        if (output.GetPosition() > 0)
                            return;
                    }
                    catch (Exception ex)
                    {
                        lastStartFailure = ex;
                    }
                    Thread.Sleep(10);
                }

                if (lastStartFailure != null)
                    throw lastStartFailure;
                if (output.PlaybackState == PlaybackState.Playing)
                    throw new TimeoutException();
                if (output.PlaybackState != PlaybackState.Playing)
                {
                    throw new InvalidOperationException(
                        "WASAPI did not enter the playing state.");
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return;

                // Deliberately do not call Stop. The owner invokes Dispose only
                // after endpoint and parent removal, when no alt-setting change
                // can be sent to the virtual USB device.
                output?.Dispose();
                endpoint?.Dispose();
            }
        }
    }

    internal sealed class WindowsNativeModeVirtualDevicePresence :
        INativeModeVirtualDevicePresence
    {
        public bool IsPresent() =>
            NativeModeDevicePresence.IsVirtualDualSensePresent();
    }
}
