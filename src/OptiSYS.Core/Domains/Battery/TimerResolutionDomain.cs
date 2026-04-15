using System.Diagnostics;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Domains.Battery;

/// <summary>
/// Clamps inflated timer resolutions back to default.
/// Many apps set the system timer to 1ms or 0.5ms, forcing 1000-2000 CPU wakeups/sec.
/// On Windows 11 22H2+: uses PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION.
/// On older Windows: falls back to NtSetTimerResolution to reset the global timer.
/// </summary>
public sealed class TimerResolutionDomain : IOptimizationDomain
{
    private readonly Settings _settings;
    private bool _isActive;
    private uint _originalResolution;
    private readonly HashSet<uint> _processesIgnored = [];

    private const uint DEFAULT_RESOLUTION = 156250; // 15.625ms in 100ns units

    public string Id => "timer-resolution";
    public string DisplayName => "Timer Resolution";
    public string Category => "Battery";
    public bool IsSupported => true;
    public bool IsActive => _isActive;

    public TimerResolutionDomain(Settings settings) { _settings = settings; }

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
        _originalResolution = baseline.Get<uint>("currentResolution");
        int ignored = 0, failed = 0, skipped = 0;
        var exclusions = new HashSet<string>(_settings.EcoQosExcludedProcesses, StringComparer.OrdinalIgnoreCase);

        _processesIgnored.Clear();

        var isWin11 = Environment.OSVersion.Version >= new Version(10, 0, 22621);

        if (isWin11)
        {
            var foregroundPid = NativeMethods.GetForegroundProcessId();

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

                    if (exclusions.Contains(proc.ProcessName))
                    {
                        skipped++;
                        continue;
                    }

                    var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_SET_INFORMATION, false, pid);
                    if (handle == IntPtr.Zero) { skipped++; continue; }

                    try
                    {
                        if (NativeMethods.SetProcessTimerResolutionIgnore(handle, true))
                        {
                            _processesIgnored.Add(pid);
                            ignored++;
                        }
                        else { failed++; }
                    }
                    finally { NativeMethods.CloseHandle(handle); }
                }
                catch { skipped++; }
                finally { proc.Dispose(); }
            }
        }

        if (_originalResolution < DEFAULT_RESOLUTION)
        {
            NativeMethods.NtSetTimerResolution(DEFAULT_RESOLUTION, true, out _);
        }

        _isActive = true;
        sw.Stop();

        var msg = isWin11
            ? $"Ignored timer on {ignored} processes, reset global to 15.6ms"
            : "Reset global timer resolution to 15.6ms";

        return ApplyResult.Ok(Id, msg, optimized: ignored, failed: failed, skipped: skipped, duration: sw.Elapsed);
    }

    public void Revert(DomainSnapshot baseline)
    {
        foreach (var pid in _processesIgnored)
        {
            var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_SET_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) continue;
            try { NativeMethods.SetProcessTimerResolutionIgnore(handle, false); }
            finally { NativeMethods.CloseHandle(handle); }
        }
        _processesIgnored.Clear();

        var original = baseline.Get<uint>("currentResolution");
        if (original > 0 && original != DEFAULT_RESOLUTION)
            NativeMethods.NtSetTimerResolution(original, true, out _);
        else
            NativeMethods.NtSetTimerResolution(DEFAULT_RESOLUTION, false, out _);

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

    public void Dispose() { }
}