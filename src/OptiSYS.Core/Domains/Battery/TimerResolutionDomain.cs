using System.Diagnostics;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Domains.Battery;

/// <summary>
/// Clamps inflated timer resolutions back to default.
/// Many apps set the system timer to 1ms or 0.5ms, forcing 1000-2000 CPU wakeups/sec.
/// On Windows 11 22H2+: uses PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION.
/// Older Windows builds are observation-only here because changing the global timer
/// resolution can affect the whole workstation.
/// </summary>
public sealed class TimerResolutionDomain : IOptimizationDomain
{
    private static readonly HashSet<string> ShellProcessExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
        "TextInputHost",
        "SystemSettings",
        "Widgets",
    };

    private readonly Settings _settings;
    private readonly INativeBridge _native;
    private bool _isActive;
    private readonly HashSet<uint> _processesIgnored = [];

    public string Id => "timer-resolution";
    public string DisplayName => "Timer Resolution";
    public string Category => "Battery";
    public bool IsSupported => true;
    public bool IsActive => _isActive;

    public bool IsEnabled(Settings settings) => settings.TimerResolutionEnabled;

    public TimerResolutionDomain(Settings settings, INativeBridge native)
    {
        _settings = settings;
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public DomainSnapshot CaptureBaseline()
    {
        var snapshot = new DomainSnapshot { DomainId = Id };
        NativeMethods.NtQueryTimerResolution(out var min, out var max, out var current);
        snapshot.Set("currentResolution", current);
        snapshot.Set("minResolution", min);
        snapshot.Set("maxResolution", max);
        return snapshot;
    }

    public ApplyResult Apply(DomainSnapshot baseline)
    {
        var sw = Stopwatch.StartNew();
        int ignored = 0, failed = 0, skipped = 0;
        var exclusions = new HashSet<string>(
            _settings.TimerResolutionExcludedProcesses.Concat(_settings.ProtectedApplications),
            StringComparer.OrdinalIgnoreCase);

        _processesIgnored.Clear();

        var isWin11 = Environment.OSVersion.Version >= new Version(10, 0, 22621);

        if (isWin11)
        {
            var nativeForegroundPid = _native.GetForegroundProcessId();
            var foregroundPid = nativeForegroundPid > 0 ? (uint)nativeForegroundPid : 0u;

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var pid = (uint)proc.Id;
                    if (pid <= 4 || pid == Environment.ProcessId || pid == foregroundPid)
                    {
                        skipped++;
                        continue;
                    }

                    if (IsShellProcess(proc.ProcessName) || exclusions.Contains(proc.ProcessName))
                    {
                        skipped++;
                        continue;
                    }

                    if (SetTimerResolutionIgnore(proc.Id, enable: true))
                    {
                        _processesIgnored.Add(pid);
                        ignored++;
                    }
                    else { failed++; }
                }
                catch { skipped++; }
                finally { proc.Dispose(); }
            }
        }

        _isActive = ignored > 0;
        sw.Stop();

        var msg = isWin11
            ? $"Ignored timer on {ignored} background processes"
            : "Safe timer clamp requires Windows 11 22H2 or newer";

        return ApplyResult.Ok(Id, msg, optimized: ignored, failed: failed, skipped: skipped, duration: sw.Elapsed);
    }

    public void Revert(DomainSnapshot baseline)
    {
        foreach (var pid in _processesIgnored)
        {
            try
            {
                SetTimerResolutionIgnore((int)pid, enable: false);
            }
            catch { }
        }
        _processesIgnored.Clear();

        _isActive = false;
    }

    public DomainStatus GetStatus()
    {
        NativeMethods.NtQueryTimerResolution(out _, out _, out var current);
        var currentMs = current / 10000.0;

        return new DomainStatus
        {
            DomainId = Id,
            DisplayName = DisplayName,
            Category = Category,
            IsSupported = IsSupported,
            IsActive = _isActive,
            Summary = _isActive
                ? $"Timer at {currentMs:F1}ms, {_processesIgnored.Count} processes clamped"
                : $"Timer at {currentMs:F1}ms",
        };
    }

    private bool SetTimerResolutionIgnore(int pid, bool enable) =>
        _native.SetTimerResolution(enable, pid);

    internal static bool IsShellProcess(string processName) =>
        ShellProcessExclusions.Contains(processName);

    public void Dispose() { }
}
