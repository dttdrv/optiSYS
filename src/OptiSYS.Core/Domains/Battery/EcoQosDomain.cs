using System.Diagnostics;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Domains.Battery;

/// <summary>
/// Marks background processes with EcoQoS (efficiency mode).
/// On modern CPUs with E-cores, this schedules background work on efficiency cores
/// at lower frequency. Foreground app is never touched.
/// </summary>
public sealed class EcoQosDomain : IOptimizationDomain
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
    private readonly INativeBridge? _native;
    private bool _isActive;
    private readonly HashSet<uint> _throttledPids = [];
    private readonly object _gate = new();

    public string Id => "ecoqos";
    public string DisplayName => "Process Power Throttling";
    public string Category => "Battery";
    public bool IsSupported => Environment.OSVersion.Version >= new Version(10, 0, 16299);
    public bool IsActive => _isActive;

    public bool IsEnabled(Settings settings) => settings.EcoQosEnabled;

    public EcoQosDomain(Settings settings, INativeBridge? native = null)
    {
        _settings = settings;
        _native = native;
    }

    public DomainSnapshot CaptureBaseline()
    {
        var snapshot = new DomainSnapshot { DomainId = Id };
        snapshot.Set("captured", true);
        return snapshot;
    }

    public ApplyResult Apply(DomainSnapshot baseline)
    {
        // First application: reconcile from an empty tracked set. The baseline carries no PID
        // list — revert is dynamic (the live tracked set is authoritative), mirroring how
        // MemoryOptimizerDomain leans on the OS to undo its effect rather than a snapshot.
        baseline.Set("captured", true);
        return Reconcile();
    }

    /// <summary>
    /// Idempotent reconcile toward the desired state: every non-foreground, non-excluded,
    /// non-shell, non-self background process is in efficiency mode; the foreground process is
    /// always released. Safe to call repeatedly — the adaptive controller invokes it on a cadence
    /// while on battery so focus changes and newly-spawned processes are tracked continuously.
    /// </summary>
    public ApplyResult Reconcile()
    {
        var sw = Stopwatch.StartNew();

        var nativeForegroundPid = _native?.GetForegroundProcessId() ?? 0;
        var foregroundPid = nativeForegroundPid > 0
            ? (uint)nativeForegroundPid
            : NativeMethods.GetForegroundProcessId();
        var selfPid = (uint)Environment.ProcessId;
        var exclusions = new HashSet<string>(
            _settings.EcoQosExcludedProcesses.Concat(_settings.ProtectedApplications),
            StringComparer.OrdinalIgnoreCase);

        var desired = new HashSet<uint>();
        foreach (var (pid, name) in EnumerateProcesses())
        {
            if (pid == selfPid || pid == foregroundPid || pid <= 4)
                continue;
            if (IsShellProcess(name) || exclusions.Contains(name))
                continue;
            desired.Add(pid);
        }

        int throttled = 0, released = 0, failed = 0, alreadyThrottled = 0, verified = 0, active;
        lock (_gate)
        {
            // Release whatever we previously throttled that is no longer desired: the app that
            // just took focus, a now-excluded match, or a process that has exited.
            foreach (var pid in _throttledPids.Where(p => !desired.Contains(p)).ToList())
            {
                try { SetEcoQos((int)pid, enable: false); } catch { }
                _throttledPids.Remove(pid);
                released++;
            }

            // Throttle newly-desired processes (background apps, newly spawned, newly backgrounded).
            foreach (var pid in desired)
            {
                if (_throttledPids.Contains(pid))
                    continue;

                // Readback first: if the OS (or a prior session) already has this process in EcoQoS,
                // skip the redundant write — idempotent, less churn — but still track + count it.
                // A null readback means "unknown" (access denied / exited): fall back to attempting.
                var preState = IsEcoQosThrottled((int)pid);
                if (preState == true)
                {
                    _throttledPids.Add(pid);
                    alreadyThrottled++;
                    verified++;
                    continue;
                }

                if (SetEcoQos((int)pid, enable: true))
                {
                    _throttledPids.Add(pid);
                    throttled++;

                    // Confirm the write actually took (detect a silent driver/OS no-op). Unknown or
                    // not-throttled means tracked-but-unverified — the count stays defensible.
                    if (IsEcoQosThrottled((int)pid) == true)
                        verified++;
                }
                else
                {
                    failed++;
                }
            }

            active = _throttledPids.Count;
            _isActive = active > 0;
        }

        sw.Stop();
        return ApplyResult.Ok(Id,
            $"{verified} background processes verified in efficiency mode",
            optimized: throttled, failed: failed, skipped: released + alreadyThrottled, duration: sw.Elapsed);
    }

    public void Revert(DomainSnapshot baseline)
    {
        // Dynamic revert: un-throttle the live tracked set (which includes anything a later
        // reconcile added). No persisted PID list — after a crash a fresh instance has nothing
        // to replay, and any leftover hint is invisible and self-clears when the process exits.
        lock (_gate)
        {
            foreach (var pid in _throttledPids)
            {
                try { SetEcoQos((int)pid, enable: false); } catch { }
            }

            _throttledPids.Clear();
            _isActive = false;
        }
    }

    public DomainStatus GetStatus() => new()
    {
        DomainId = Id,
        DisplayName = DisplayName,
        Category = Category,
        IsSupported = IsSupported,
        IsActive = _isActive,
        Summary = _isActive ? $"{_throttledPids.Count} processes throttled" : "Inactive",
        Details = _isActive
            ? [$"Excluded: {string.Join(", ", _settings.EcoQosExcludedProcesses.Concat(_settings.ProtectedApplications).Distinct(StringComparer.OrdinalIgnoreCase).Take(5))}..."]
            : []
    };

    internal static bool IsShellProcess(string processName) =>
        ShellProcessExclusions.Contains(processName);

    // Enumeration goes through the native bridge (mockable, so the reconcile is unit-testable),
    // falling back to Process.GetProcesses() when no bridge is supplied — the same pattern
    // MemoryOptimizer uses.
    private List<(uint pid, string name)> EnumerateProcesses()
    {
        var native = _native?.GetProcessList();
        if (native is { Length: > 0 })
            return native.Select(p => ((uint)p.ProcessId, p.ProcessName)).ToList();

        var list = new List<(uint, string)>();
        foreach (var proc in Process.GetProcesses())
        {
            try { list.Add(((uint)proc.Id, proc.ProcessName)); }
            catch { }
            finally { proc.Dispose(); }
        }
        return list;
    }

    // Readback through the bridge seam (mockable). Returns null = unknown when no bridge is wired,
    // the read fails, or it throws — never propagates, so reconcile degrades to attempt-and-track.
    private bool? IsEcoQosThrottled(int pid)
    {
        try { return _native?.IsEcoQosThrottled(pid); }
        catch { return null; }
    }

    private bool SetEcoQos(int pid, bool enable)
    {
        if (_native?.SetEcoQos(enable, pid) == true)
            return true;

        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SET_INFORMATION | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false, (uint)pid);

        if (handle == IntPtr.Zero)
            return false;

        try { return NativeMethods.SetProcessEcoQoS(handle, enable); }
        finally { NativeMethods.CloseHandle(handle); }
    }

    public void Dispose() { }
}
