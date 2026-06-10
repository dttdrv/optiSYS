using System.Diagnostics;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;

namespace OptiSYS.Core.Domains.Battery;

/// <summary>
/// Marks background DRAINERS with EcoQoS (efficiency mode): throttling is targeted by evidence —
/// only processes with measured, sustained CPU burn (see <see cref="BackgroundDrainTracker"/>;
/// thresholds grounded by the OptiSYS.Lab `drain` probe) are throttled, never the whole
/// background set. The foreground app, shell, protected/excluded apps, and processes with an
/// ACTIVE audio session are exempt regardless of burn (an explicit EcoQoS overrides the OS's
/// audio-gets-full-QoS heuristic). On modern CPUs with E-cores this schedules the caught
/// background work on efficiency cores at lower frequency.
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
    private readonly INativeBridge _native;
    private readonly Func<DateTime> _utcNow;
    private readonly BackgroundDrainTracker _drainTracker = new();
    private bool _applied;
    private readonly HashSet<uint> _throttledPids = [];
    private readonly object _gate = new();

    public string Id => "ecoqos";
    public string DisplayName => "Process Power Throttling";
    public string Category => "Battery";
    public bool IsSupported => Environment.OSVersion.Version >= new Version(10, 0, 16299);

    /// <summary>
    /// "Engaged" (applied and not reverted) — NOT "currently throttling &gt; 0 pids". The first
    /// sweeps legitimately throttle nothing while burn evidence accumulates, and the adaptive
    /// controller's IsActive gate must keep maintaining through that phase.
    /// </summary>
    public bool IsActive => _applied;

    public bool IsEnabled(Settings settings) => settings.EcoQosEnabled;

    public EcoQosDomain(Settings settings, INativeBridge native, Func<DateTime>? utcNow = null)
    {
        _settings = settings;
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public DomainSnapshot CaptureBaseline()
    {
        var snapshot = new DomainSnapshot { DomainId = Id };
        snapshot.Set("captured", true);
        return snapshot;
    }

    public ApplyResult Apply(DomainSnapshot baseline)
    {
        // First application: arm the domain and take the first burn samples. The baseline carries
        // no PID list — revert is dynamic (the live tracked set is authoritative), mirroring how
        // MemoryOptimizerDomain leans on the OS to undo its effect rather than a snapshot.
        baseline.Set("captured", true);
        _applied = true;
        return Reconcile();
    }

    /// <summary>
    /// Idempotent reconcile toward the desired state: every CANDIDATE background process
    /// (non-foreground, non-excluded, non-shell, non-self) gets its CPU time sampled, and the
    /// ones the drain tracker classifies as sustained burners — minus anything with an active
    /// audio session — are in efficiency mode; everything else is released. Safe to call
    /// repeatedly — the adaptive controller invokes it on its sweep cadence while on battery, so
    /// focus changes, new drainers, and cooled drainers are tracked continuously.
    ///
    /// <para><paramref name="widenToAllCandidates"/> is the idle deep-saver: with the user away
    /// there is nothing to keep snappy, so the evidence gate relaxes and EVERY candidate gets the
    /// hint. The static exemptions (shell, protected, audible) are about safety, not evidence,
    /// and hold in every mode. The next targeted sweep's desired-set diff releases the
    /// widened-only throttles automatically.</para>
    /// </summary>
    public ApplyResult Reconcile(bool widenToAllCandidates = false)
    {
        var sw = Stopwatch.StartNew();

        var nativeForegroundPid = _native.GetForegroundProcessId();
        var foregroundPid = nativeForegroundPid > 0 ? (uint)nativeForegroundPid : 0u;
        var selfPid = (uint)Environment.ProcessId;
        var exclusions = new HashSet<string>(
            _settings.EcoQosExcludedProcesses.Concat(_settings.ProtectedApplications),
            StringComparer.OrdinalIgnoreCase);

        // Candidates pass the static rules; the drain tracker then decides who is actually
        // burning. Unreadable CPU time (null) means no evidence — never throttle on a guess.
        var candidates = new HashSet<uint>();
        var samples = new List<ProcessCpuSample>();
        foreach (var (pid, name) in EnumerateProcesses())
        {
            if (pid == selfPid || pid == foregroundPid || pid <= 4)
                continue;
            if (IsShellProcess(name) || exclusions.Contains(name))
                continue;
            candidates.Add(pid);
            if (_native.GetProcessCpuTime((int)pid) is { } cpuTime)
                samples.Add(new ProcessCpuSample((int)pid, cpuTime));
        }

        _drainTracker.Observe(samples, _utcNow());

        var audible = _native.GetAudibleProcessIds();
        var desired = widenToAllCandidates
            ? new HashSet<uint>(candidates.Where(pid => !audible.Contains((int)pid)))
            : new HashSet<uint>(
                _drainTracker.CurrentDrainers
                    .Where(pid => candidates.Contains((uint)pid) && !audible.Contains(pid))
                    .Select(pid => (uint)pid));

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
        }

        sw.Stop();
        return ApplyResult.Ok(Id,
            $"{verified} background drainers verified in efficiency mode",
            optimized: throttled, failed: failed, skipped: released + alreadyThrottled, duration: sw.Elapsed);
    }

    public void Revert(DomainSnapshot baseline)
    {
        // Dynamic revert: un-throttle the live tracked set (which includes anything a later
        // reconcile added). No persisted PID list — after a crash a fresh instance has nothing
        // to replay, and any leftover hint is invisible and self-clears when the process exits.
        // Burn histories are kept: on re-apply, known burners re-classify without a cold start.
        lock (_gate)
        {
            ReleaseAllLocked();
            _applied = false;
        }
    }

    /// <summary>
    /// The "follow, never fight" stand-down: release every throttle (reversible, back to
    /// OS-managed) but STAY ENGAGED — unlike <see cref="Revert"/>, the maintenance loop keeps
    /// running and re-throttles drainers once the high-performance mode ends, instead of
    /// staying dark until the next AC→DC transition.
    /// </summary>
    public void Suspend()
    {
        lock (_gate)
        {
            ReleaseAllLocked();
        }
    }

    private void ReleaseAllLocked()
    {
        foreach (var pid in _throttledPids)
        {
            try { SetEcoQos((int)pid, enable: false); } catch { }
        }

        _throttledPids.Clear();
    }

    public DomainStatus GetStatus() => new()
    {
        DomainId = Id,
        DisplayName = DisplayName,
        Category = Category,
        IsSupported = IsSupported,
        IsActive = _applied,
        Summary = _applied ? $"{_throttledPids.Count} background drainers throttled" : "Inactive",
        Details = _applied
            ? [$"Excluded: {string.Join(", ", _settings.EcoQosExcludedProcesses.Concat(_settings.ProtectedApplications).Distinct(StringComparer.OrdinalIgnoreCase).Take(5))}..."]
            : []
    };

    internal static bool IsShellProcess(string processName) =>
        ShellProcessExclusions.Contains(processName);

    // Enumeration goes through the native bridge (mockable, so the reconcile is unit-testable).
    private List<(uint pid, string name)> EnumerateProcesses() =>
        _native.GetProcessList().Select(p => ((uint)p.ProcessId, p.ProcessName)).ToList();

    // Readback through the bridge seam (mockable). Returns null = unknown when the read fails or it
    // throws — never propagates, so reconcile degrades to attempt-and-track.
    private bool? IsEcoQosThrottled(int pid)
    {
        try { return _native.IsEcoQosThrottled(pid); }
        catch { return null; }
    }

    private bool SetEcoQos(int pid, bool enable) => _native.SetEcoQos(enable, pid);

    public void Dispose() { }
}
