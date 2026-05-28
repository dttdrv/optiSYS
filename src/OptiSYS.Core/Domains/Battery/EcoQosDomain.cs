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

    public string Id => "ecoqos";
    public string DisplayName => "Process Power Throttling";
    public string Category => "Battery";
    public bool IsSupported => Environment.OSVersion.Version >= new Version(10, 0, 16299);
    public bool IsActive => _isActive;

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
        var sw = Stopwatch.StartNew();
        int throttled = 0, failed = 0, skipped = 0;
        var nativeForegroundPid = _native?.GetForegroundProcessId() ?? 0;
        var foregroundPid = nativeForegroundPid > 0
            ? (uint)nativeForegroundPid
            : NativeMethods.GetForegroundProcessId();
        var selfPid = Environment.ProcessId;
        var exclusions = new HashSet<string>(
            _settings.EcoQosExcludedProcesses.Concat(_settings.ProtectedApplications),
            StringComparer.OrdinalIgnoreCase);

        _throttledPids.Clear();

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var pid = (uint)proc.Id;
                if (pid == selfPid || pid == foregroundPid || pid <= 4)
                {
                    skipped++;
                    continue;
                }

                var name = proc.ProcessName;
                if (IsShellProcess(name) || exclusions.Contains(name))
                {
                    skipped++;
                    continue;
                }

                if (SetEcoQos(proc.Id, enable: true))
                {
                    _throttledPids.Add(pid);
                    throttled++;
                }
                else
                {
                    failed++;
                }
            }
            catch { skipped++; }
            finally { proc.Dispose(); }
        }

        _isActive = throttled > 0;
        sw.Stop();

        baseline.Set("throttledPids", _throttledPids.ToList());

        return ApplyResult.Ok(Id,
            $"Throttled {throttled} processes (skipped {skipped}, failed {failed})",
            optimized: throttled, failed: failed, skipped: skipped, duration: sw.Elapsed);
    }

    public void Revert(DomainSnapshot baseline)
    {
        var pidsToRevert = _throttledPids.Count > 0
            ? _throttledPids
            : new HashSet<uint>(baseline.Get<List<uint>>("throttledPids") ?? []);

        foreach (var pid in pidsToRevert)
        {
            try
            {
                SetEcoQos((int)pid, enable: false);
            }
            catch { }
        }

        _throttledPids.Clear();
        _isActive = false;
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
