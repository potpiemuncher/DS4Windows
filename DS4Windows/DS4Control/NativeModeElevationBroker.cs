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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DS4Windows
{
    public enum NativeModeAttachFailureKind
    {
        None,
        UsbipMissing,
        SetupRequired,
        CommandFailed,
        DeviceArrivalTimeout,
    }

    public sealed class NativeModeAttachResult
    {
        private NativeModeAttachResult(bool success,
            NativeModeAttachFailureKind failureKind, string reason)
        {
            Success = success;
            FailureKind = failureKind;
            Reason = reason;
        }

        public bool Success { get; }
        public NativeModeAttachFailureKind FailureKind { get; }
        public string Reason { get; }

        public static NativeModeAttachResult Succeeded(string reason) =>
            new NativeModeAttachResult(true, NativeModeAttachFailureKind.None, reason);

        public static NativeModeAttachResult Failed(
            NativeModeAttachFailureKind kind, string reason) =>
            new NativeModeAttachResult(false, kind, reason);
    }

    internal sealed class NativeModeCommandResult
    {
        public NativeModeCommandResult(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            Output = output ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public int ExitCode { get; }
        public string Output { get; }
        public string Error { get; }
    }

    internal interface INativeModeCommandRunner
    {
        NativeModeCommandResult Run(string executable,
            IReadOnlyList<string> arguments);
        Task<NativeModeCommandResult> RunAsync(string executable,
            IReadOnlyList<string> arguments, bool elevate,
            CancellationToken cancellationToken);
    }

    internal sealed class NativeModeProcessRunner : INativeModeCommandRunner
    {
        public NativeModeCommandResult Run(string executable,
            IReadOnlyList<string> arguments)
        {
            try
            {
                using Process process = CreateProcess(executable, arguments,
                    elevate: false);
                if (!process.Start())
                    return new NativeModeCommandResult(-1, string.Empty,
                        $"Could not start {Path.GetFileName(executable)}.");

                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return new NativeModeCommandResult(process.ExitCode, output, error);
            }
            catch (Win32Exception ex)
            {
                return new NativeModeCommandResult(ex.NativeErrorCode,
                    string.Empty, ex.Message);
            }
        }

        public async Task<NativeModeCommandResult> RunAsync(string executable,
            IReadOnlyList<string> arguments, bool elevate,
            CancellationToken cancellationToken)
        {
            try
            {
                using Process process = CreateProcess(executable, arguments, elevate);
                if (!process.Start())
                {
                    return new NativeModeCommandResult(-1, string.Empty,
                        $"Could not start {Path.GetFileName(executable)}.");
                }

                Task<string> outputTask = elevate
                    ? Task.FromResult(string.Empty)
                    : process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = elevate
                    ? Task.FromResult(string.Empty)
                    : process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return new NativeModeCommandResult(process.ExitCode,
                    await outputTask.ConfigureAwait(false),
                    await errorTask.ConfigureAwait(false));
            }
            catch (Win32Exception ex)
            {
                string reason = ex.NativeErrorCode == 1223
                    ? "The elevation prompt was canceled."
                    : ex.Message;
                return new NativeModeCommandResult(ex.NativeErrorCode,
                    string.Empty, reason);
            }
        }

        private static Process CreateProcess(string executable,
            IReadOnlyList<string> arguments, bool elevate)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = elevate,
                CreateNoWindow = !elevate,
                WindowStyle = elevate ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                RedirectStandardOutput = !elevate,
                RedirectStandardError = !elevate,
                Verb = elevate ? "runas" : string.Empty,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            return new Process { StartInfo = startInfo };
        }
    }

    public sealed class NativeModeElevationBroker
    {
        public const string AttachTaskName = @"DS4Windows\NativeDualSenseAttach";
        public const string AttachArguments =
            "attach -r 127.0.0.1 -b 1-1 --serial DS4WSPKCOMP001 --once";
        private static readonly TimeSpan DefaultArrivalTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ArrivalPollInterval = TimeSpan.FromMilliseconds(100);

        private readonly Func<string, bool> fileExists;
        private readonly Func<bool> isAdministrator;
        private readonly INativeModeCommandRunner commandRunner;
        private readonly Func<bool> virtualDevicePresent;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly string schtasksPath;

        public NativeModeElevationBroker() : this(File.Exists,
            Global.IsAdministrator, new NativeModeProcessRunner(),
            NativeModeDevicePresence.IsVirtualDualSensePresent,
            Task.Delay, Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
        }

        internal NativeModeElevationBroker(Func<string, bool> fileExists,
            Func<bool> isAdministrator, INativeModeCommandRunner commandRunner,
            Func<bool> virtualDevicePresent,
            Func<TimeSpan, CancellationToken, Task> delay,
            string schtasksPath)
        {
            this.fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
            this.isAdministrator = isAdministrator ??
                throw new ArgumentNullException(nameof(isAdministrator));
            this.commandRunner = commandRunner ??
                throw new ArgumentNullException(nameof(commandRunner));
            this.virtualDevicePresent = virtualDevicePresent ??
                throw new ArgumentNullException(nameof(virtualDevicePresent));
            this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
            this.schtasksPath = schtasksPath ?? throw new ArgumentNullException(nameof(schtasksPath));
        }

        public bool IsUsbipInstalled(string usbipPath) =>
            !string.IsNullOrWhiteSpace(usbipPath) && fileExists(usbipPath);

        public bool IsTaskPresent()
        {
            NativeModeCommandResult result = commandRunner.Run(schtasksPath,
                BuildQueryTaskArguments());
            return result.ExitCode == 0;
        }

        /// <summary>
        /// Creates or replaces the on-demand task. The executable path is captured
        /// in the task action; changing UsbipExePath requires running setup again.
        /// Runtime task launches accept no attach arguments from DS4Windows.
        /// </summary>
        public async Task<NativeModeAttachResult> EnsureAttachTaskAsync(
            string usbipPath, CancellationToken cancellationToken = default)
        {
            if (!IsUsbipInstalled(usbipPath))
                return MissingUsbipResult(usbipPath);

            string currentUserSid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(currentUserSid))
            {
                return NativeModeAttachResult.Failed(
                    NativeModeAttachFailureKind.CommandFailed,
                    "Could not determine the current Windows user for native-mode setup.");
            }

            string taskXmlPath = Path.Combine(Path.GetTempPath(),
                $"DS4Windows-NativeDualSenseAttach-{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(taskXmlPath,
                    BuildAttachTaskXml(usbipPath, currentUserSid),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                NativeModeCommandResult result = await commandRunner.RunAsync(
                    schtasksPath, BuildCreateTaskArguments(taskXmlPath),
                    elevate: true, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    return NativeModeAttachResult.Failed(
                        NativeModeAttachFailureKind.CommandFailed,
                        CommandFailure("Could not create the native-mode attach task", result));
                }

                return NativeModeAttachResult.Succeeded(
                    "Native-mode elevation setup is complete.");
            }
            finally
            {
                try
                {
                    if (File.Exists(taskXmlPath))
                        File.Delete(taskXmlPath);
                }
                catch (IOException)
                {
                    // A stale XML file contains no secret and is safe to remove later.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        public async Task<NativeModeAttachResult> RunAttachAsync(string usbipPath,
            CancellationToken cancellationToken = default)
        {
            if (!IsUsbipInstalled(usbipPath))
                return MissingUsbipResult(usbipPath);

            string executable;
            IReadOnlyList<string> arguments;
            if (isAdministrator())
            {
                executable = usbipPath;
                arguments = BuildDirectAttachArguments();
            }
            else
            {
                if (!IsTaskPresent())
                {
                    return NativeModeAttachResult.Failed(
                        NativeModeAttachFailureKind.SetupRequired,
                        "Elevation setup required — select Set up native mode, approve the one-time UAC prompt, then Start again.");
                }

                executable = schtasksPath;
                arguments = BuildRunTaskArguments();
            }

            NativeModeCommandResult commandResult = await commandRunner.RunAsync(
                executable, arguments, elevate: false, cancellationToken)
                .ConfigureAwait(false);
            if (commandResult.ExitCode != 0)
            {
                return NativeModeAttachResult.Failed(
                    NativeModeAttachFailureKind.CommandFailed,
                    CommandFailure("USB/IP attach could not be started", commandResult));
            }

            bool arrived = await WaitForDeviceArrivalAsync(virtualDevicePresent,
                delay, DefaultArrivalTimeout, ArrivalPollInterval,
                cancellationToken).ConfigureAwait(false);
            return arrived
                ? NativeModeAttachResult.Succeeded("Virtual DualSense attached.")
                : NativeModeAttachResult.Failed(
                    NativeModeAttachFailureKind.DeviceArrivalTimeout,
                    "Virtual DualSense did not appear within 10 seconds; check the native server log and USB/IP driver state.");
        }

        public static string BuildAttachTaskXml(string usbipPath, string userSid)
        {
            if (string.IsNullOrWhiteSpace(usbipPath))
                throw new ArgumentException("A USB/IP executable path is required.", nameof(usbipPath));
            if (string.IsNullOrWhiteSpace(userSid))
                throw new ArgumentException("A Windows user SID is required.", nameof(userSid));

            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(ns + "Task", new XAttribute("version", "1.4"),
                    new XElement(ns + "RegistrationInfo",
                        new XElement(ns + "Description",
                            "Attaches the fixed local virtual DualSense exported by DS4Windows.")),
                    new XElement(ns + "Triggers"),
                    new XElement(ns + "Principals",
                        new XElement(ns + "Principal", new XAttribute("id", "Author"),
                            new XElement(ns + "UserId", userSid),
                            new XElement(ns + "LogonType", "InteractiveToken"),
                            new XElement(ns + "RunLevel", "HighestAvailable"))),
                    new XElement(ns + "Settings",
                        new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                        new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                        new XElement(ns + "StopIfGoingOnBatteries", "false"),
                        new XElement(ns + "AllowHardTerminate", "true"),
                        new XElement(ns + "StartWhenAvailable", "false"),
                        new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                        new XElement(ns + "AllowStartOnDemand", "true"),
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "Hidden", "false"),
                        new XElement(ns + "ExecutionTimeLimit", "PT1M"),
                        new XElement(ns + "Priority", "7")),
                    new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                        new XElement(ns + "Exec",
                            new XElement(ns + "Command", Path.GetFullPath(usbipPath)),
                            new XElement(ns + "Arguments", AttachArguments)))));
            return $"<?xml version=\"1.0\" encoding=\"utf-8\"?>{Environment.NewLine}" +
                document.Root.ToString(SaveOptions.DisableFormatting);
        }

        internal static string[] BuildQueryTaskArguments() =>
            new[] { "/Query", "/TN", AttachTaskName };

        internal static string[] BuildCreateTaskArguments(string taskXmlPath) =>
            new[] { "/Create", "/TN", AttachTaskName, "/XML", taskXmlPath, "/F" };

        internal static string[] BuildRunTaskArguments() =>
            new[] { "/Run", "/TN", AttachTaskName };

        internal static string[] BuildDirectAttachArguments() =>
            AttachArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        internal static async Task<bool> WaitForDeviceArrivalAsync(
            Func<bool> devicePresent,
            Func<TimeSpan, CancellationToken, Task> delay,
            TimeSpan timeout, TimeSpan pollInterval,
            CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            do
            {
                if (devicePresent())
                    return true;
                if (DateTimeOffset.UtcNow >= deadline)
                    return false;
                await delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
            while (true);
        }

        private static NativeModeAttachResult MissingUsbipResult(string path) =>
            NativeModeAttachResult.Failed(NativeModeAttachFailureKind.UsbipMissing,
                $"usbip.exe was not found at '{path}'. Install/qualify usbip-win2 using doc/M0_COMPAT_LAB_RUNBOOK.md and the M2 runbook, or correct UsbipExePath.");

        private static string CommandFailure(string prefix,
            NativeModeCommandResult result)
        {
            string detail = string.IsNullOrWhiteSpace(result.Error)
                ? result.Output.Trim()
                : result.Error.Trim();
            return string.IsNullOrWhiteSpace(detail)
                ? $"{prefix} (exit code {result.ExitCode})."
                : $"{prefix} (exit code {result.ExitCode}): {detail}";
        }
    }

    internal static class NativeModeDevicePresence
    {
        private const string VirtualDualSenseHardwareIdPrefix =
            @"USB\VID_054C&PID_0CE6";

        public static bool IsVirtualDualSensePresent()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID IS NOT NULL");
                using ManagementObjectCollection devices = searcher.Get();
                return devices.Cast<ManagementObject>().Any(device =>
                    (device["DeviceID"] as string)?.StartsWith(
                        VirtualDualSenseHardwareIdPrefix,
                        StringComparison.OrdinalIgnoreCase) == true);
            }
            catch (Exception ex) when (ex is ManagementException ||
                ex is UnauthorizedAccessException)
            {
                AppLogger.LogToGui(
                    $"[native] Unable to query virtual DualSense presence: {ex.Message}", true);
                return false;
            }
        }
    }
}
