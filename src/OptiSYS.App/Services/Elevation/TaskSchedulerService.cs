using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Xml.Linq;
using OptiSYS.Core.Services;

namespace OptiSYS.Services.Elevation;

/// <summary>
/// <see cref="ITaskSchedulerService"/> implemented over the built-in <c>schtasks.exe</c> —
/// zero runtime dependency, the same XML-driven approach the optiRAM/optiBAT siblings ship.
/// Everything is driven by authored task XML + process exit codes (never by parsing
/// localized command output), so it is locale-independent.
/// </summary>
public sealed class TaskSchedulerService : ITaskSchedulerService
{
    internal const string TaskName = "OptiSYS";
    internal const string LaunchArguments = "--background";
    private const int SchTasksTimeoutMs = 15000;

    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private readonly IExecutablePathProvider _pathProvider;

    public TaskSchedulerService() : this(new ProcessExecutablePathProvider()) { }

    public TaskSchedulerService(IExecutablePathProvider pathProvider)
    {
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
    }

    public bool IsElevated => PrivilegeManager.IsProcessElevated();

    public bool TaskExists()
    {
        var (exit, _) = RunSchTasks($"/Query /TN \"{TaskName}\" /FO LIST", captureOutput: false);
        return exit == 0;
    }

    public bool NeedsProvisioning()
    {
        if (!TaskExists())
            return true;

        // Exists — re-register only if we can PROVE the registered path differs from ours.
        // Any introspection failure is treated as "fine" so transient schtasks/XML hiccups
        // never nag the user with a spurious elevation banner.
        var expectedPath = _pathProvider.GetExecutablePath();
        if (string.IsNullOrWhiteSpace(expectedPath))
            return false;

        var (exit, xml) = RunSchTasks($"/Query /TN \"{TaskName}\" /XML ONELINE", captureOutput: true);
        if (exit != 0 || string.IsNullOrWhiteSpace(xml))
            return false;

        var registeredCommand = ParseTaskCommand(xml);
        return IsStale(registeredCommand, expectedPath);
    }

    public bool CreateOrUpdateTask()
    {
        var exePath = _pathProvider.GetExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            StartupLog.Write("TaskSchedulerService: CreateOrUpdateTask skipped — executable path unavailable");
            return false;
        }

        string? tempXml = null;
        try
        {
            var xml = BuildTaskXml(exePath, LaunchArguments, startAtLogon: true);
            // Random filename in temp prevents a TOCTOU symlink/replacement attack on the XML.
            tempXml = Path.Combine(Path.GetTempPath(), $"OptiSYS_Task_{Guid.NewGuid():N}.xml");
            File.WriteAllText(tempXml, xml, System.Text.Encoding.Unicode);

            // /F overwrites an existing task → idempotent create-or-update.
            var (exit, _) = RunSchTasks($"/Create /TN \"{TaskName}\" /XML \"{tempXml}\" /F", captureOutput: false);
            if (exit != 0)
                StartupLog.Write($"TaskSchedulerService: schtasks /Create exited {exit}");
            return exit == 0;
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("TaskSchedulerService.CreateOrUpdateTask", ex);
            return false;
        }
        finally
        {
            if (tempXml != null)
                try { File.Delete(tempXml); } catch { /* best-effort temp cleanup */ }
        }
    }

    public bool DeleteTask()
    {
        var (exit, _) = RunSchTasks($"/Delete /TN \"{TaskName}\" /F", captureOutput: false);
        return exit == 0;
    }

    /// <summary>
    /// Pure builder for the task XML. The action launches optiSYS with <paramref name="arguments"/>
    /// (e.g. <c>--background</c>) under the current user's SID (portable across renames),
    /// <c>InteractiveToken</c> logon (tray visible, no stored password) and <c>HighestAvailable</c>
    /// run level. A 15s logon-trigger delay keeps the elevated relaunch out of the logon storm.
    /// </summary>
    internal static string BuildTaskXml(string exePath, string arguments, bool startAtLogon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);

        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
            throw new InvalidOperationException("Unable to resolve the current Windows identity SID for task registration.");

        var workingDir = Path.GetDirectoryName(exePath) ?? "";

        var triggers = startAtLogon
            ? """
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT15S</Delay>
                </LogonTrigger>
              </Triggers>
              """
            : string.Empty;

        var argumentsElement = string.IsNullOrEmpty(arguments)
            ? string.Empty
            : $"\n          <Arguments>{SecurityElement.Escape(arguments)}</Arguments>";

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>optiSYS</Author>
                <Description>optiSYS — safe Windows runtime optimizer (elevated launch)</Description>
              </RegistrationInfo>
              {triggers}
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(userSid)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>{argumentsElement}
                  <WorkingDirectory>{SecurityElement.Escape(workingDir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>Extract the &lt;Command&gt; path from a task's exported XML; null if absent/unparseable.</summary>
    internal static string? ParseTaskCommand(string taskXml)
    {
        try
        {
            var doc = XDocument.Parse(taskXml);
            return doc.Root?.Element(TaskNs + "Actions")?.Element(TaskNs + "Exec")?.Element(TaskNs + "Command")?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Stale only when we positively read a registered command that differs from the expected
    /// path. A null/blank registered command (couldn't introspect) is NOT stale — we never nag
    /// on uncertainty.
    /// </summary>
    internal static bool IsStale(string? registeredCommand, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(registeredCommand))
            return false;
        return !string.Equals(registeredCommand.Trim().Trim('"'), expectedPath.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static (int exitCode, string output) RunSchTasks(string arguments, bool captureOutput)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, string.Empty);

            var output = captureOutput ? p.StandardOutput.ReadToEnd() : string.Empty;
            if (!p.WaitForExit(SchTasksTimeoutMs))
            {
                try { p.Kill(); } catch { }
                return (-1, output);
            }
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("TaskSchedulerService.RunSchTasks", ex);
            return (-1, string.Empty);
        }
    }
}
