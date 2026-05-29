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
    private readonly StepEffectivenessTracker _tracker = new();
    private bool _disposed;

    public HashSet<string> ExcludedProcesses { get; set; } = new(DefaultExclusions, StringComparer.OrdinalIgnoreCase);

    public MemoryOptimizer(IMemoryInfoService memoryInfo, INativeBridge? native = null)
    {
        _memoryInfo = memoryInfo;
        _native = native;
    }

    // Convenience ctor for legacy/standalone use — wraps its own MemoryInfoService,
    // which upcasts cleanly to IMemoryInfoService for the primary ctor.
    public MemoryOptimizer() : this(new MemoryInfoService()) { }

    public (int trimmed, int failed, int skipped, bool earlyExit) TrimProcessWorkingSets(long targetAvailableBytes = 0)
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

        int trimmed = 0, failed = 0;
        bool earlyExit = false;

        foreach (var (pid, _) in processInfos)
        {
            try
            {
                if (EmptyWorkingSet(pid))
                    trimmed++;
                else
                    failed++;

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

        return (trimmed, failed, skipped, earlyExit);
    }

    public bool PurgeStandbyList()
    {
        ThrowIfDisposed();
        int command = (int)NativeMethods.MemoryListCommand.MemoryPurgeStandbyList;
        int result = NativeMethods.NtSetSystemInformation(
            NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));
        return result >= 0;
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

    private static bool LowerProcessMemoryPriority(int pid)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SET_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero)
            return false;
        try
        {
            return NativeMethods.SetProcessMemoryPriority(handle, NativeMethods.MEMORY_PRIORITY_LOW);
        }
        finally { NativeMethods.CloseHandle(handle); }
    }

    private bool EmptyWorkingSet(int pid)
    {
        if (_native?.EmptyWorkingSet(pid) == true)
            return true;

        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
            false, (uint)pid);

        if (handle == IntPtr.Zero)
            return false;

        try { return NativeMethods.EmptyWorkingSet(handle); }
        finally { NativeMethods.CloseHandle(handle); }
    }

    public bool PurgeLowPriorityStandby()
    {
        ThrowIfDisposed();
        int command = (int)NativeMethods.MemoryListCommand.MemoryPurgeLowPriorityStandbyList;
        int result = NativeMethods.NtSetSystemInformation(
            NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));
        return result >= 0;
    }

    public bool FlushModifiedList()
    {
        ThrowIfDisposed();
        int command = (int)NativeMethods.MemoryListCommand.MemoryFlushModifiedList;
        int result = NativeMethods.NtSetSystemInformation(
            NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));
        return result >= 0;
    }

    public bool CaptureAndResetAccessedBits()
    {
        ThrowIfDisposed();
        int command = (int)NativeMethods.MemoryListCommand.MemoryCaptureAndResetAccessedBits;
        int result = NativeMethods.NtSetSystemInformation(
            NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));
        return result >= 0;
    }

    public bool EmptySystemWorkingSets()
    {
        ThrowIfDisposed();
        int command = (int)NativeMethods.MemoryListCommand.MemoryEmptyWorkingSets;
        int result = NativeMethods.NtSetSystemInformation(
            NativeMethods.SystemMemoryListInformation, ref command, sizeof(int));
        return result >= 0;
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
        var beforeAvailable = (double)beforeInfo.AvailablePhysicalBytes;
        var actualLevel = OptimizationLevel.Conservative;
        int processesTrimmed = 0;

        try
        {
            long trimTarget = 0;
            if (targetThresholdPercent > 0 && beforeInfo.TotalPhysicalBytes > 0)
                trimTarget = (long)((double)beforeInfo.TotalPhysicalBytes * (1.0 - targetThresholdPercent / 100.0));

            var (trimmed, _, _, earlyExit) = TrimProcessWorkingSets(trimTarget);
            processesTrimmed = trimmed;
            methodsUsed.Add(earlyExit ? "Working Set Trim (early exit)" : "Working Set Trim");

            if (targetThresholdPercent > 0 && GetUsagePercentLive() < targetThresholdPercent)
                return BuildResult(sw, methodsUsed, beforeAvailable, beforeInfo, processesTrimmed, actualLevel);

            var effectiveLevel = level;
            if (targetThresholdPercent > 0)
            {
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

                if (targetThresholdPercent > 0 && GetUsagePercentLive() < targetThresholdPercent)
                    return BuildResult(sw, methodsUsed, beforeAvailable, beforeInfo, processesTrimmed, actualLevel);

                // Max (Aggressive): full reclaim — system-wide working-set empty + full standby
                // purge (matching optiRAM). Reached only under sustained pressure: the live
                // escalation checks above already bailed out when a lighter pass sufficed, and the
                // threshold + cooldown gate upstream means this fires when it's genuinely needed.
                if (effectiveLevel >= OptimizationLevel.Aggressive)
                {
                    actualLevel = OptimizationLevel.Aggressive;
                    if (EmptySystemWorkingSets()) methodsUsed.Add("System Working Set Empty");
                    if (PurgeStandbyList()) methodsUsed.Add("Standby List Purge");
                }
            }

            return BuildResult(sw, methodsUsed, beforeAvailable, beforeInfo, processesTrimmed, actualLevel);
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
        double beforeAvailable, MemoryInfo beforeInfo, int processesTrimmed, OptimizationLevel actualLevel)
    {
        sw.Stop();
        var afterInfo = _memoryInfo.GetCurrentMemoryInfo();
        var afterAvailable = (double)afterInfo.AvailablePhysicalBytes;
        var freed = Math.Max(0, (long)(afterAvailable - beforeAvailable));

        return new OptimizationResult
        {
            Success = true,
            Message = $"Freed {OptimizationResult.FormatBytesStatic(freed)} in {sw.ElapsedMilliseconds}ms",
            FreedBytes = freed,
            ProcessesTrimmed = processesTrimmed,
            Duration = sw.Elapsed,
            ActualLevelUsed = actualLevel,
            MethodsUsed = methodsUsed.ToArray(),
        };
    }

    private static ulong GetAvailablePhysicalBytesQuick()
    {
        var ms = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        NativeMethods.GlobalMemoryStatusEx(ref ms);
        return ms.ullAvailPhys;
    }

    private static int GetUsagePercentLive()
    {
        var ms = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        NativeMethods.GlobalMemoryStatusEx(ref ms);
        return (int)ms.dwMemoryLoad;
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
/// Tracks effectiveness of each optimization step to skip ineffective ones.
/// Ported from optiRAM.
/// </summary>
public sealed class StepEffectivenessTracker
{
    private readonly Dictionary<string, Queue<long>> _history = new();
    private readonly int _windowSize;
    private readonly long _minEffectiveBytes;
    private DateTime _lastResetUtc = DateTime.UtcNow;
    private static readonly TimeSpan ResetInterval = TimeSpan.FromMinutes(30);

    public StepEffectivenessTracker(int windowSize = 10, long minEffectiveBytes = 1_048_576)
    {
        _windowSize = windowSize;
        _minEffectiveBytes = minEffectiveBytes;
    }

    public void Record(string stepName, long freedBytes)
    {
        MaybeAutoReset();
        if (!_history.TryGetValue(stepName, out var queue))
        {
            queue = new Queue<long>();
            _history[stepName] = queue;
        }
        queue.Enqueue(Math.Max(0, freedBytes));
        while (queue.Count > _windowSize)
            queue.Dequeue();
    }

    public bool ShouldSkip(string stepName)
    {
        MaybeAutoReset();
        if (!_history.TryGetValue(stepName, out var queue) || queue.Count < _windowSize)
            return false;
        return queue.Average() < _minEffectiveBytes;
    }

    public void Reset()
    {
        _history.Clear();
        _lastResetUtc = DateTime.UtcNow;
    }

    private void MaybeAutoReset()
    {
        if (DateTime.UtcNow - _lastResetUtc >= ResetInterval)
            Reset();
    }
}

internal enum SelfTrimReason
{
    Startup,
    Periodic,
    PostOptimization
}
