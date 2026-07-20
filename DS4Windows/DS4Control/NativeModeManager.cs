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
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows
{
    public enum NativeModeState
    {
        Starting,
        Serving,
        Attached,
        PadLost,
        SetupRequired,
        Stopped,
        Faulted,
    }

    public sealed class NativeModeStateChangedEventArgs : EventArgs
    {
        public NativeModeStateChangedEventArgs(NativeModeState state, string detail)
        {
            State = state;
            Detail = detail;
        }

        public NativeModeState State { get; }
        public string Detail { get; }
    }

    public sealed class NativeModeStatsSnapshot
    {
        public NativeModeStatsSnapshot(string isochronousOut, string audio,
            string speakerRebuffer, DateTimeOffset updatedAt)
        {
            IsochronousOut = isochronousOut;
            Audio = audio;
            SpeakerRebuffer = speakerRebuffer;
            UpdatedAt = updatedAt;
        }

        public string IsochronousOut { get; }
        public string Audio { get; }
        public string SpeakerRebuffer { get; }
        public DateTimeOffset UpdatedAt { get; }
    }

    /// <summary>
    /// Owns the VirtualDualSenseUsbip child process. Controller release,
    /// elevated attach, and UI integration are deliberately later phases.
    /// </summary>
    public sealed class NativeModeManager : IAsyncDisposable
    {
        private const string ServerExecutableName = "VirtualDualSenseUsbip.exe";
        private static readonly TimeSpan DeviceRemovalTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DeviceRemovalPollInterval = TimeSpan.FromMilliseconds(200);

        private readonly SemaphoreSlim lifecycleGate = new SemaphoreSlim(1, 1);
        private readonly object stateGate = new object();
        private readonly object statsGate = new object();
        private volatile Process serverProcess;
        private Task standardOutputTask = Task.CompletedTask;
        private Task standardErrorTask = Task.CompletedTask;
        private Task exitMonitorTask = Task.CompletedTask;
        private volatile bool stopping;
        private NativeModeState state = NativeModeState.Stopped;
        private string stateDetail = "Native mode server stopped.";
        private NativeModeStatsSnapshot latestStats =
            new NativeModeStatsSnapshot(null, null, null, DateTimeOffset.MinValue);

        public event EventHandler<NativeModeStateChangedEventArgs> StateChanged;
        public event EventHandler StatsChanged;

        public NativeModeState State
        {
            get
            {
                lock (stateGate)
                    return state;
            }
        }

        public NativeModeStatsSnapshot LatestStats
        {
            get
            {
                lock (statsGate)
                    return latestStats;
            }
        }

        /// <summary>
        /// Remains true until teardown has disposed the child, including after
        /// an unexpected exit or a failed stop that should be retried.
        /// </summary>
        public bool HasOwnedProcess => serverProcess != null;

        public async Task WaitForServingAsync(TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            await NativeModeReadinessAwaiter.WaitForServingAsync(GetStateSnapshot,
                handler => StateChanged += handler,
                handler => StateChanged -= handler,
                timeout, cancellationToken).ConfigureAwait(false);
        }

        public static string LocateServerExecutable()
        {
            string baseDirectory = AppContext.BaseDirectory;
            IEnumerable<string> packagedCandidates = new[]
            {
                Path.Combine(baseDirectory, "native", ServerExecutableName),
                Path.Combine(baseDirectory, ServerExecutableName),
            };

            foreach (string candidate in packagedCandidates)
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }

            DirectoryInfo directory = new DirectoryInfo(baseDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "utils",
                    "VirtualDualSenseUsbip", "bin", "Release", "net8.0",
                    ServerExecutableName);
                if (File.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }

            throw new FileNotFoundException(
                $"Could not find {ServerExecutableName}. Expected it under the application " +
                "native directory or utils\\VirtualDualSenseUsbip\\bin\\Release\\net8.0.");
        }

        public async Task StartAsync(IEnumerable<string> arguments,
            CancellationToken cancellationToken = default)
        {
            if (arguments == null)
                throw new ArgumentNullException(nameof(arguments));

            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (serverProcess != null && !serverProcess.HasExited)
                    throw new InvalidOperationException("Native mode server is already running.");

                if (serverProcess != null)
                {
                    await Task.WhenAll(standardOutputTask, standardErrorTask,
                        exitMonitorTask).ConfigureAwait(false);
                    serverProcess.Dispose();
                    serverProcess = null;
                }

                lock (statsGate)
                {
                    latestStats = new NativeModeStatsSnapshot(
                        null, null, null, DateTimeOffset.MinValue);
                }

                string executablePath = LocateServerExecutable();
                var startInfo = new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string argument in arguments)
                    startInfo.ArgumentList.Add(argument);

                var process = new Process
                {
                    StartInfo = startInfo,
                };

                stopping = false;
                SetState(NativeModeState.Starting, "Starting native mode server.");
                try
                {
                    if (!process.Start())
                        throw new InvalidOperationException("Native mode server did not start.");
                }
                catch (Exception ex)
                {
                    process.Dispose();
                    SetState(NativeModeState.Faulted, ex.Message);
                    throw;
                }

                serverProcess = process;
                standardOutputTask = PumpLinesAsync(process.StandardOutput, false);
                standardErrorTask = PumpLinesAsync(process.StandardError, true);
                exitMonitorTask = MonitorExitAsync(process);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        /// <summary>
        /// Records PnP attach completion. The elevation broker added in Phase 4
        /// will call this only after observing the virtual device.
        /// </summary>
        public void MarkAttached()
        {
            NativeModeState current = State;
            if (current != NativeModeState.Serving && current != NativeModeState.Attached)
                throw new InvalidOperationException(
                    $"Cannot mark native mode attached while state is {current}.");

            SetState(NativeModeState.Attached, "Virtual DualSense attached.");
        }

        public void MarkSetupRequired(string detail)
        {
            SetState(NativeModeState.SetupRequired, detail);
        }

        public void MarkPadLost(string detail)
        {
            SetState(NativeModeState.PadLost, detail);
        }

        public void MarkFaulted(string detail)
        {
            SetState(NativeModeState.Faulted, detail);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Process process = serverProcess;
                stopping = true;

                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between HasExited and Kill.
                    }

                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    await Task.WhenAll(standardOutputTask, standardErrorTask,
                        exitMonitorTask).ConfigureAwait(false);
                    process.Dispose();
                    serverProcess = null;
                }

                if (process != null)
                {
                    // Once teardown starts, finish the bounded removal check even
                    // if the initiating UI operation is canceled.
                    await WaitForVirtualDeviceRemovalAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                SetState(NativeModeState.Stopped, "Native mode server stopped.");
            }
            finally
            {
                stopping = false;
                lifecycleGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            lifecycleGate.Dispose();
        }

        private async Task PumpLinesAsync(StreamReader reader, bool warning)
        {
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (ProcessLogLine(line, warning))
                    AppLogger.LogToGui($"[native] {line}", warning);
            }
        }

        internal bool ProcessLogLine(string line, bool warning)
        {
            NativeModeLogKind kind = NativeModeLogClassifier.Classify(line);
            HandleLogLine(line, kind);
            return NativeModeLogPolicy.ShouldForwardToGui(kind, warning);
        }

        private void HandleLogLine(string line, NativeModeLogKind kind)
        {
            switch (kind)
            {
                case NativeModeLogKind.ServerListening:
                    SetState(NativeModeState.Serving, line);
                    break;
                case NativeModeLogKind.PadOpenFailure:
                    SetState(NativeModeState.Faulted, line);
                    break;
                case NativeModeLogKind.PadLost:
                    SetState(NativeModeState.PadLost,
                        "The physical pad was lost; press PS and start native mode again.");
                    break;
                case NativeModeLogKind.IsochronousOutStats:
                    UpdateStats(isochronousOut: line);
                    break;
                case NativeModeLogKind.AudioStats:
                    UpdateStats(audio: line);
                    break;
                case NativeModeLogKind.SpeakerRebuffer:
                    UpdateStats(speakerRebuffer: line);
                    break;
            }
        }

        private async Task MonitorExitAsync(Process process)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!stopping)
                    SetState(NativeModeState.Faulted, ex.Message);
                return;
            }

            if (!stopping && ReferenceEquals(serverProcess, process) &&
                State != NativeModeState.PadLost && State != NativeModeState.Faulted)
            {
                SetState(NativeModeState.Faulted,
                    $"Native mode server exited unexpectedly with code {process.ExitCode}.");
            }
        }

        private void UpdateStats(string isochronousOut = null, string audio = null,
            string speakerRebuffer = null)
        {
            EventHandler handler;
            lock (statsGate)
            {
                latestStats = new NativeModeStatsSnapshot(
                    isochronousOut ?? latestStats.IsochronousOut,
                    audio ?? latestStats.Audio,
                    speakerRebuffer ?? latestStats.SpeakerRebuffer,
                    DateTimeOffset.Now);
                handler = StatsChanged;
            }

            handler?.Invoke(this, EventArgs.Empty);
        }

        private void SetState(NativeModeState newState, string detail)
        {
            EventHandler<NativeModeStateChangedEventArgs> handler;
            lock (stateGate)
            {
                if (state == newState)
                    return;

                state = newState;
                stateDetail = detail;
                handler = StateChanged;
            }

            handler?.Invoke(this, new NativeModeStateChangedEventArgs(newState, detail));
        }

        private static async Task WaitForVirtualDeviceRemovalAsync(
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + DeviceRemovalTimeout;
            while (NativeModeDevicePresence.IsVirtualDualSensePresent() &&
                DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(DeviceRemovalPollInterval, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (NativeModeDevicePresence.IsVirtualDualSensePresent())
            {
                AppLogger.LogToGui(
                    "[native] Virtual DualSense is still present after server shutdown.", true);
            }
        }

        private (NativeModeState State, string Detail) GetStateSnapshot()
        {
            lock (stateGate)
                return (state, stateDetail);
        }
    }

    internal static class NativeModeReadinessAwaiter
    {
        public static async Task WaitForServingAsync(
            Func<(NativeModeState State, string Detail)> getState,
            Action<EventHandler<NativeModeStateChangedEventArgs>> subscribe,
            Action<EventHandler<NativeModeStateChangedEventArgs>> unsubscribe,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void ApplyState(NativeModeState state, string detail)
            {
                switch (state)
                {
                    case NativeModeState.Serving:
                    case NativeModeState.Attached:
                        completion.TrySetResult(true);
                        break;
                    case NativeModeState.PadLost:
                    case NativeModeState.SetupRequired:
                    case NativeModeState.Stopped:
                    case NativeModeState.Faulted:
                        completion.TrySetException(new InvalidOperationException(
                            string.IsNullOrWhiteSpace(detail)
                                ? $"Native mode entered {state} before the server became ready."
                                : detail));
                        break;
                }
            }

            EventHandler<NativeModeStateChangedEventArgs> handler =
                (_, e) => ApplyState(e.State, e.Detail);
            subscribe(handler);
            try
            {
                (NativeModeState current, string detail) = getState();
                ApplyState(current, detail);
                try
                {
                    await completion.Task.WaitAsync(timeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException(
                        $"Native mode server did not become ready within {timeout.TotalSeconds:0.#} seconds.");
                }
            }
            finally
            {
                unsubscribe(handler);
            }
        }
    }
}
