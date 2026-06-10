using System.Diagnostics;
using System.Runtime.InteropServices;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Services;

/// <summary>
/// Core memory optimization service ported from optiRAM.
/// Performs working set trimming, standby list purging, and various
/// system memory management operations based on optimization level.
/// </summary>
public sealed class MemoryOptimizer : IMemoryOptimizer
{
    private static readonly HashSet<string> DefaultExclusions =
        new(Settings.CriticalProcessExclusions, StringComparer.OrdinalIgnoreCase);

    private readonly IMemoryInfoService _memoryInfo;
    private readonly INativeBridge? _native;
    // Seam over the system-wide memory-list commands + live available-physical read (which bypass
    // INativeBridge). Default is the real native path; tests inject a fake to make OptimizeAll's
    // escalation/early-exit deterministic. See IMemorySystemOps.
    private readonly IMemorySystemOps _systemOps;
    private readonly StepEffectivenessTracker _tracker = new();
    // PIDs we lowered to LOW + their captured prior priority, so the hint can be fully reverted.
    private readonly Dictionary<int, uint> _loweredPriorities = new();
    // Guards _loweredPriorities: the threadpool watcher writes to it while a revert/dispose on
    // another thread snapshots+clears it. Never held across native calls.
    private readonly object _priorityLock = new();
    private bool _disposed;

    public HashSet<string> ExcludedProcesses { get; set; } = new(DefaultExclusions, StringComparer.OrdinalIgnoreCase);

    public MemoryOptimizer(IMemoryInfoService memoryInfo, INativeBridge? native = null)
        : this(memoryInfo, native, new NativeMemorySystemOps()) { }

    // Test seam ctor: inject a fake IMemorySystemOps to drive OptimizeAll deterministically.
    internal MemoryOptimizer(IMemoryInfoService memoryInfo, INativeBridge? native, IMemorySystemOps systemOps)
    {
        _memoryInfo = memoryInfo;
        _native = native;
        _systemOps = systemOps;
    }

    // Convenience ctor for legacy/standalone use — wraps its own MemoryInfoService,
    // which upcasts cleanly to IMemoryInfoService for the primary ctor.
    public MemoryOptimizer() : this(new MemoryInfoService()) { }

    public (int trimmed, int failed, int skipped, bool earlyExit, long freedBytes) TrimProcessWorkingSets(long targetAvailableBytes = 0)
    {
        ThrowIfDisposed();

        const int maxCandidates = 64;
        const long minimumWorkingSetBytes = 32L * 1024 * 1024;

        var nativeForegroundPid = _native?.GetForegroundProcessId() ?? 0;
        var foregroundPid = nativeForegroundPid > 0
            ? (uint)nativeForegroundPid
            : NativeMethods.GetForegroundProcessId();
        var selfPid = (uint)Environment.ProcessId;

        int skipped = 0;
        var candidates = new PriorityQueue<(int pid, long workingSet), long>();

        var nativeProcesses = _native?.GetProcessList();
        if (nativeProcesses is { Length: > 0 })
        {
            foreach (var proc in nativeProcesses)
            {
                if (ExcludedProcesses.Contains(proc.ProcessName)
                    || proc.ProcessId == foregroundPid
                    || proc.ProcessId == selfPid
                    || proc.WorkingSetBytes < minimumWorkingSetBytes)
                {
                    skipped++;
                    continue;
                }

                candidates.Enqueue((proc.ProcessId, proc.WorkingSetBytes), proc.WorkingSetBytes);
                if (candidates.Count > maxCandidates)
                    candidates.Dequeue();
            }
        }
        else
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (ExcludedProcesses.Contains(proc.ProcessName)
                        || proc.Id == foregroundPid
                        || proc.Id == selfPid)
                    {
                        skipped++;
                        continue;
                    }

                    var workingSet = proc.WorkingSet64;
                    if (workingSet < minimumWorkingSetBytes)
                    {
                        skipped++;
                        continue;
                    }

                    candidates.Enqueue((proc.Id, workingSet), workingSet);
                    if (candidates.Count > maxCandidates)
                        candidates.Dequeue();
                }
                catch { skipped++; }
                finally { proc.Dispose(); }
            }
        }

        var processInfos = new List<(int pid, long workingSet)>(candidates.Count);
        while (candidates.Count > 0)
            processInfos.Add(candidates.Dequeue());

        processInfos.Sort((a, b) => b.workingSet.CompareTo(a.workingSet));

        // Heat demotion: a hot candidate's actively-touched pages fault straight back, so its
        // trim only partially sticks (the cold fraction) while costing re-fault churn. Cold
        // candidates therefore trim FIRST; hot ones run last and only while the reclaim target
        // is still unmet — under real pressure the burner IS reclaimed (this may be the only
        // lever that reaches a protected burner, e.g. a leftover node process), while a met
        // target leaves it in peace. No target (the manual full pass) trims everything.
        // No bridge (legacy path) -> no demotion.
        var hotPids = _native is null
            ? []
            : IdentifyHotPids(
                processInfos.Select(p => p.pid).ToList(),
                _native.GetProcessCpuTime,
                Thread.Sleep);
        if (hotPids.Count > 0)
        {
            var demoted = new List<(int pid, long workingSet)>(processInfos.Count);
            demoted.AddRange(processInfos.Where(p => !hotPids.Contains(p.pid)));
            demoted.AddRange(processInfos.Where(p => hotPids.Contains(p.pid)));
            processInfos = demoted;
        }

        int trimmed = 0, failed = 0;
        long freedBytes = 0;
        bool earlyExit = false;

        foreach (var (pid, _) in processInfos)
        {
            // Before each demoted (hot) trim, re-check the target: once the cold trims have met
            // it, every remaining hot trim would be pure churn.
            if (targetAvailableBytes > 0 && hotPids.Contains(pid)
                && (long)GetAvailablePhysicalBytesQuick() >= targetAvailableBytes)
            {
                earlyExit = true;
                break;
            }

            try
            {
                var freed = TrimOne(pid);
                if (freed > 0)
                {
                    trimmed++;
                    freedBytes += freed;
                }
                else
                {
                    failed++;
                }

                if (targetAvailableBytes > 0 && trimmed > 0 && trimmed % 8 == 0)
                {
                    if ((long)GetAvailablePhysicalBytesQuick() >= targetAvailableBytes)
                    {
                        earlyExit = true;
                        break;
                    }
                }
            }
            catch { failed++; }
        }

        return (trimmed, failed, skipped, earlyExit, freedBytes);
    }

    public bool PurgeStandbyList()
    {
        ThrowIfDisposed();
        return _systemOps.RunMemoryListCommand(NativeMethods.MemoryListCommand.MemoryPurgeStandbyList);
    }

    // "Hot" = burned at least this share of one core during the dwell — clearly executing, well
    // above measurement noise on a 200ms window (10ms of CPU). Below it, pages are cold enough
    // that trimming sticks.
    private const double HotCorePercentDuringDwell = 5.0;
    private static readonly TimeSpan HeatDwell = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Identify candidates actively burning CPU right now: sample each pid's total CPU time,
    /// dwell briefly, sample again. Unreadable CPU (null) is never hot — the gate must not act
    /// on a guess. The sampling functions are injected so the decision is unit-testable without
    /// real processes or a real sleep.
    /// </summary>
    internal static HashSet<int> IdentifyHotPids(
        IReadOnlyList<int> pids, Func<int, TimeSpan?> cpuTimeOf, Action<TimeSpan> dwell)
    {
        var before = new Dictionary<int, TimeSpan>(pids.Count);
        foreach (var pid in pids)
        {
            if (cpuTimeOf(pid) is { } cpu)
                before[pid] = cpu;
        }

        dwell(HeatDwell);

        var hot = new HashSet<int>();
        foreach (var pid in pids)
        {
            if (before.TryGetValue(pid, out var first) && cpuTimeOf(pid) is { } second)
            {
                var burnPercent = (second - first).TotalMilliseconds / HeatDwell.TotalMilliseconds * 100.0;
                if (burnPercent >= HotCorePercentDuringDwell)
                    hot.Add(pid);
            }
        }

        return hot;
    }

    /// <summary>
    /// Curated allowlist of known background processes eligible for a memory-priority hint,
    /// minus anything on the critical or excluded (protected) lists. Built once per instance.
    /// </summary>
    private HashSet<string> BuildBackgroundPriorityAllowlist()
    {
        var allow = new HashSet<string>(
            Settings.BackgroundMemoryPriorityAllowlist, StringComparer.OrdinalIgnoreCase);

        // Never touch anything the user/system protects or anything system-critical.
        allow.ExceptWith(DefaultExclusions);
        allow.ExceptWith(ExcludedProcesses);
        return allow;
    }

    public int HintBackgroundMemoryPriority()
    {
        ThrowIfDisposed();

        var allow = BuildBackgroundPriorityAllowlist();
        if (allow.Count == 0)
            return 0;

        var selfPid = (uint)Environment.ProcessId;
        int hinted = 0;

        var nativeProcesses = _native?.GetProcessList();
        if (nativeProcesses is { Length: > 0 })
        {
            foreach (var proc in nativeProcesses)
            {
                if (proc.ProcessId == selfPid || !allow.Contains(proc.ProcessName))
                    continue;
                if (LowerProcessMemoryPriority(proc.ProcessId))
                    hinted++;
            }
            return hinted;
        }

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id == selfPid || !allow.Contains(proc.ProcessName))
                    continue;
                if (LowerProcessMemoryPriority(proc.Id))
                    hinted++;
            }
            catch { /* process exited or access denied — skip */ }
            finally { proc.Dispose(); }
        }

        return hinted;
    }

    private bool LowerProcessMemoryPriority(int pid)
    {
        // Capture the current priority BEFORE lowering, so revert can restore the exact prior value.
        // Already-tracked PIDs keep their original capture (don't overwrite LOW back onto itself).
        lock (_priorityLock)
        {
            if (!_loweredPriorities.ContainsKey(pid))
            {
                var prior = _native?.GetProcessMemoryPriority(pid) ?? GetProcessMemoryPriorityRaw(pid);
                _loweredPriorities[pid] = prior;
            }
        }

        if (_native is not null)
            return _native.SetProcessMemoryPriority(pid, NativeMethods.MEMORY_PRIORITY_LOW);

        return SetProcessMemoryPriorityRaw(pid, NativeMethods.MEMORY_PRIORITY_LOW);
    }

    /// <summary>
    /// Restores the memory priority of every process this instance lowered, back to its captured
    /// prior value (NORMAL when the prior couldn't be read). A process that has exited or denies
    /// access is a clean skip — it can't be left in a lowered state, and the rest still restore.
    /// </summary>
    public void RestoreBackgroundMemoryPriority()
    {
        ThrowIfDisposed();

        // Snapshot + clear under the lock, then do the native restores OUTSIDE it so we never hold
        // the lock across native calls (and a concurrent watcher tick can't mutate mid-enumeration).
        List<KeyValuePair<int, uint>> toRestore;
        lock (_priorityLock)
        {
            toRestore = new List<KeyValuePair<int, uint>>(_loweredPriorities);
            _loweredPriorities.Clear();
        }

        foreach (var (pid, prior) in toRestore)
        {
            var target = prior != 0 ? prior : NativeMethods.MEMORY_PRIORITY_NORMAL;
            try
            {
                if (_native is not null)
                    _native.SetProcessMemoryPriority(pid, target);
                else
                    SetProcessMemoryPriorityRaw(pid, target);
            }
            catch { /* process exited or access denied — skip */ }
        }
    }

    private static uint GetProcessMemoryPriorityRaw(int pid)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return 0;
        try { return NativeMethods.GetProcessMemoryPriority(handle); }
        finally { NativeMethods.CloseHandle(handle); }
    }

    private static bool SetProcessMemoryPriorityRaw(int pid, uint priority)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SET_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return false;
        try
        {
            return NativeMethods.SetProcessMemoryPriority(handle, priority);
        }
        finally { NativeMethods.CloseHandle(handle); }
    }

    // Trim one process's working set and return the bytes actually freed (before-after), so the
    // caller can report a real, attributable "Freed" figure instead of a noisy system-wide delta.
    // >0 = trimmed; 0 = failed/no-op. Routes through the bridge (which measures before/after);
    // falls back to a bool empty (reported as 1 byte) only when no bridge is present.
    private long TrimOne(int pid)
    {
        if (_native is not null)
            return _native.TrimProcessWorkingSet(pid);

        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
            false, (uint)pid);

        if (handle == IntPtr.Zero)
            return 0;

        try { return NativeMethods.EmptyWorkingSet(handle) ? 1 : 0; }
        finally { NativeMethods.CloseHandle(handle); }
    }

    public bool PurgeLowPriorityStandby()
    {
        ThrowIfDisposed();
        return _systemOps.RunMemoryListCommand(NativeMethods.MemoryListCommand.MemoryPurgeLowPriorityStandbyList);
    }

    public bool FlushModifiedList()
    {
        ThrowIfDisposed();
        return _systemOps.RunMemoryListCommand(NativeMethods.MemoryListCommand.MemoryFlushModifiedList);
    }

    public bool CaptureAndResetAccessedBits()
    {
        ThrowIfDisposed();
        return _systemOps.RunMemoryListCommand(NativeMethods.MemoryListCommand.MemoryCaptureAndResetAccessedBits);
    }

    public bool EmptySystemWorkingSets()
    {
        ThrowIfDisposed();
        return _systemOps.RunMemoryListCommand(NativeMethods.MemoryListCommand.MemoryEmptyWorkingSets);
    }

    public bool FlushSystemFileCache()
    {
        ThrowIfDisposed();
        return NativeMethods.SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0);
    }

    public bool SetFileCacheHardMax(long maxBytes)
    {
        ThrowIfDisposed();
        if (maxBytes <= 0)
            return NativeMethods.SetSystemFileCacheSize(IntPtr.Zero, IntPtr.Zero,
                NativeMethods.FILE_CACHE_MAX_HARD_DISABLE);
        return NativeMethods.SetSystemFileCacheSize(IntPtr.Zero, new IntPtr(maxBytes),
            NativeMethods.FILE_CACHE_MAX_HARD_ENABLE);
    }

    public bool FlushRegistryCache()
    {
        ThrowIfDisposed();
        int result = NativeMethods.NtSetSystemInformationNull(
            NativeMethods.SystemRegistryReconciliationInformation, IntPtr.Zero, 0);
        return result >= 0;
    }

    public long CombinePhysicalMemory()
    {
        ThrowIfDisposed();
        var info = new NativeMethods.MEMORY_COMBINE_INFORMATION_EX();
        int result = NativeMethods.NtSetSystemInformationCombine(
            NativeMethods.SystemCombinePhysicalMemoryInformation,
            ref info, Marshal.SizeOf<NativeMethods.MEMORY_COMBINE_INFORMATION_EX>());
        return result >= 0 ? (long)(ulong)info.PagesCombined : -1;
    }

    public static bool SetProcessWorkingSetCap(int pid, long maxBytes)
    {
        // Minimum 50MB working set to prevent OOM in rendering pipelines
        const long minimumCap = 50L * 1024 * 1024;
        if (maxBytes > 0 && maxBytes < minimumCap)
            maxBytes = minimumCap;

        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
            false, (uint)pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var minSize = new IntPtr(1024 * 1024);
            var maxSize = new IntPtr(maxBytes);
            return NativeMethods.SetProcessWorkingSetSizeEx(handle, minSize, maxSize,
                NativeMethods.QUOTA_LIMITS_HARDWS_MIN_DISABLE | NativeMethods.QUOTA_LIMITS_HARDWS_MAX_ENABLE);
        }
        finally { NativeMethods.CloseHandle(handle); }
    }

    public OptimizationResult OptimizeAll(
        OptimizationLevel level = OptimizationLevel.Balanced,
        int cacheMaxPercent = 0,
        int targetThresholdPercent = 0,
        bool isLowMemory = false,
        int accessedBitsDelayMs = 2000,
        bool effectivenessTrackingEnabled = true)
    {
        ThrowIfDisposed();

        var sw = Stopwatch.StartNew();
        var methodsUsed = new List<string>();
        var beforeInfo = _memoryInfo.GetCurrentMemoryInfo();
        var actualLevel = OptimizationLevel.Conservative;
        int processesTrimmed = 0;
        // Snapshot available physical before any reclaim so FreedBytes can report the real, whole-pass
        // increase (standby/system-WS reclaim included), not just the working-set-trim sum.
        long availableBefore = _systemOps.AvailablePhysicalBytes();

        // When no pressure target is given (target == 0), the caller is an explicit "run the requested
        // level now" — skip the trim-only early-exits so the full requested pipeline always runs.
        bool pressureGated = targetThresholdPercent > 0;

        try
        {
            long trimTarget = 0;
            if (pressureGated && beforeInfo.TotalPhysicalBytes > 0)
                trimTarget = (long)((double)beforeInfo.TotalPhysicalBytes * (1.0 - targetThresholdPercent / 100.0));

            var (trimmed, _, _, earlyExit, trimmedBytes) = TrimProcessWorkingSets(trimTarget);
            processesTrimmed = trimmed;
            methodsUsed.Add(earlyExit ? "Working Set Trim (early exit)" : "Working Set Trim");

            if (pressureGated && GetUsagePercentLive(beforeInfo.TotalPhysicalBytes) < targetThresholdPercent)
                return BuildResult(sw, methodsUsed, ComputeFreed(availableBefore, trimmedBytes), processesTrimmed, actualLevel);

            // Compression cap is a safety, not a pressure gate: an OS already compressing heavily would
            // just re-fault after a purge, so cap Aggressive→Balanced regardless of how we were called.
            var effectiveLevel = level;
            double compressedRatio = beforeInfo.TotalPhysicalBytes > 0
                ? (double)beforeInfo.CompressedBytes / beforeInfo.TotalPhysicalBytes
                : 0;

            if (compressedRatio > 0.15 && effectiveLevel == OptimizationLevel.Aggressive)
            {
                // OS already compressing heavily — purging would just re-fault. Cap to Balanced.
                effectiveLevel = OptimizationLevel.Balanced;
                methodsUsed.Add("Level capped (high compression)");
            }
            else if (compressedRatio < 0.05 && isLowMemory && effectiveLevel < OptimizationLevel.Balanced)
            {
                // Little compression but real low-memory pressure — escalate (ported from optiRAM).
                effectiveLevel = OptimizationLevel.Balanced;
                methodsUsed.Add("Level raised (low compression + low memory)");
            }

            if (effectiveLevel >= OptimizationLevel.Balanced)
            {
                actualLevel = OptimizationLevel.Balanced;

                RunTrackedStep("Access Bits Reset", CaptureAndResetAccessedBits, methodsUsed, effectivenessTrackingEnabled);
                RunTrackedStep("Modified List Flush", FlushModifiedList, methodsUsed, effectivenessTrackingEnabled);

                if (accessedBitsDelayMs > 0)
                    Thread.Sleep(Math.Min(accessedBitsDelayMs, 250));

                if (effectiveLevel == OptimizationLevel.Balanced)
                    RunTrackedStep("Low-Priority Standby Purge", PurgeLowPriorityStandby, methodsUsed, effectivenessTrackingEnabled);

                RunTrackedStep("File Cache Flush", FlushSystemFileCache, methodsUsed, effectivenessTrackingEnabled);
                RunTrackedStep("Registry Cache Flush", FlushRegistryCache, methodsUsed, effectivenessTrackingEnabled);

                // Page-combine (dedup identical pages) — part of the Balanced+ pipeline, matching
                // optiRAM. Only runs under genuine pressure (the threshold + cooldown gate upstream).
                var pagesCombined = CombinePhysicalMemory();
                if (pagesCombined > 0)
                    methodsUsed.Add($"Page Combine ({pagesCombined} pages)");

                if (cacheMaxPercent > 0)
                {
                    var totalRam = (double)beforeInfo.TotalPhysicalBytes;
                    var maxCacheBytes = (long)(totalRam * cacheMaxPercent / 100.0);
                    if (SetFileCacheHardMax(maxCacheBytes))
                        methodsUsed.Add($"Cache Cap {cacheMaxPercent}%");
                }

                if (pressureGated && GetUsagePercentLive(beforeInfo.TotalPhysicalBytes) < targetThresholdPercent)
                    return BuildResult(sw, methodsUsed, ComputeFreed(availableBefore, trimmedBytes), processesTrimmed, actualLevel);

                // Max (Aggressive): full reclaim — system-wide working-set empty + full standby
                // purge (matching optiRAM). When pressure-gated, reached only under sustained pressure
                // (the live early-exits above bail when a lighter pass sufficed); when called with
                // target == 0 (explicit "Max"), always runs so the requested level does real work.
                if (effectiveLevel >= OptimizationLevel.Aggressive)
                {
                    actualLevel = OptimizationLevel.Aggressive;
                    if (EmptySystemWorkingSets()) methodsUsed.Add("System Working Set Empty");
                    if (PurgeStandbyList()) methodsUsed.Add("Standby List Purge");
                }
            }

            return BuildResult(sw, methodsUsed, ComputeFreed(availableBefore, trimmedBytes), processesTrimmed, actualLevel);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new OptimizationResult
            {
                Success = false,
                Message = ex.Message,
                Duration = sw.Elapsed,
            };
        }
    }

    private bool RunTrackedStep(string stepName, Func<bool> step, List<string> methodsUsed, bool trackingEnabled)
    {
        if (trackingEnabled && _tracker.ShouldSkip(stepName))
        {
            methodsUsed.Add($"Skipped: {stepName} (ineffective)");
            return false;
        }

        long before = trackingEnabled ? (long)GetAvailablePhysicalBytesQuick() : 0;
        bool success = step();
        if (trackingEnabled)
        {
            long after = (long)GetAvailablePhysicalBytesQuick();
            _tracker.Record(stepName, after - before);
        }

        if (success) methodsUsed.Add(stepName);
        return success;
    }

    private OptimizationResult BuildResult(Stopwatch sw, List<string> methodsUsed,
        long freedBytes, int processesTrimmed, OptimizationLevel actualLevel)
    {
        sw.Stop();
        return new OptimizationResult
        {
            Success = true,
            Message = $"Freed {OptimizationResult.FormatBytesStatic(freedBytes)} in {sw.ElapsedMilliseconds}ms",
            FreedBytes = freedBytes,
            ProcessesTrimmed = processesTrimmed,
            Duration = sw.Elapsed,
            ActualLevelUsed = actualLevel,
            MethodsUsed = methodsUsed.ToArray(),
        };
    }

    private long GetAvailablePhysicalBytesQuick() => _systemOps.AvailablePhysicalBytes();

    // Live usage % derived from the seam's available-physical read against the (stable) total. Used by
    // OptimizeAll's pressure-gated early-exits; total comes from the pass's beforeInfo snapshot.
    private int GetUsagePercentLive(long totalPhysicalBytes)
    {
        if (totalPhysicalBytes <= 0) return 0;
        long available = _systemOps.AvailablePhysicalBytes();
        long used = totalPhysicalBytes - available;
        return (int)Math.Clamp(used * 100.0 / totalPhysicalBytes, 0, 100);
    }

    // Whole-pass reclaim: the increase in available physical (standby/system-WS reclaim included),
    // but never below the attributable working-set-trim sum so a trim-only pass still reports honestly.
    private long ComputeFreed(long availableBefore, long trimmedBytes)
    {
        long liveDelta = _systemOps.AvailablePhysicalBytes() - availableBefore;
        return Math.Max(liveDelta, trimmedBytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _memoryInfo.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>
/// Self-tuning for the cleanup pipeline: learns, per machine and per step, which optimization
/// steps actually reclaim memory here and skips the ones that don't. Per-step EWMA (alpha 0.5),
/// active after three samples — the previous 10-samples-inside-30-minutes window never filled at
/// real automatic-cleanup cadences (a few passes per hour), so the learning was inert in
/// production. Staleness is handled by explore/exploit instead of a timed forget-everything
/// reset: every fifth skip runs the step anyway and re-measures, and because alpha 0.5 halves
/// history, one good fresh sample lifts the EWMA straight back over the floor — a step that
/// BECAME effective (standby purge once the cache fills) un-skips itself. Pure and clock-free.
/// </summary>
public sealed class StepEffectivenessTracker
{
    private const double Alpha = 0.5;
    private const int MinSamples = 3;
    private const int ProbeEverySkips = 5;

    private sealed class StepStats
    {
        public double EwmaBytes;
        public int Samples;
        public int SkipsSinceProbe;
    }

    private readonly Dictionary<string, StepStats> _steps = new();
    private readonly long _minEffectiveBytes;

    public StepEffectivenessTracker(long minEffectiveBytes = 1_048_576)
    {
        _minEffectiveBytes = minEffectiveBytes;
    }

    public void Record(string stepName, long freedBytes)
    {
        var clamped = Math.Max(0, freedBytes);
        if (!_steps.TryGetValue(stepName, out var stats))
        {
            stats = new StepStats { EwmaBytes = clamped };
            _steps[stepName] = stats;
        }
        else
        {
            stats.EwmaBytes = Alpha * clamped + (1 - Alpha) * stats.EwmaBytes;
        }

        stats.Samples++;
    }

    public bool ShouldSkip(string stepName)
    {
        if (!_steps.TryGetValue(stepName, out var stats) || stats.Samples < MinSamples)
            return false;
        if (stats.EwmaBytes >= _minEffectiveBytes)
            return false;

        stats.SkipsSinceProbe++;
        if (stats.SkipsSinceProbe >= ProbeEverySkips)
        {
            stats.SkipsSinceProbe = 0;
            return false;   // explore: run the step and refresh the evidence
        }

        return true;
    }

    public void Reset() => _steps.Clear();
}

internal enum SelfTrimReason
{
    Startup,
    Periodic,
    PostOptimization
}
