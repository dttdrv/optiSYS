using System.Diagnostics;
using System.Runtime.InteropServices;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Services;

/// <summary>
/// Provides real-time memory information using Windows APIs
/// with optional performance counter support for detailed metrics.
/// Ported from optiRAM with fix for OOM bug (no 25MB working set cap on self).
/// </summary>
public sealed class MemoryInfoService : IDisposable
{
    private sealed class PerfCounterSet : IDisposable
    {
        public PerformanceCounter? StandbyNormal { get; }
        public PerformanceCounter? StandbyReserve { get; }
        public PerformanceCounter? Modified { get; }
        public PerformanceCounter? FreeZero { get; }

        public PerfCounterSet(
            PerformanceCounter? standbyNormal,
            PerformanceCounter? standbyReserve,
            PerformanceCounter? modified,
            PerformanceCounter? freeZero)
        {
            StandbyNormal = standbyNormal;
            StandbyReserve = standbyReserve;
            Modified = modified;
            FreeZero = freeZero;
        }

        public void Prime()
        {
            StandbyNormal?.NextValue();
            StandbyReserve?.NextValue();
            Modified?.NextValue();
            FreeZero?.NextValue();
        }

        public void Dispose()
        {
            StandbyNormal?.Dispose();
            StandbyReserve?.Dispose();
            Modified?.Dispose();
            FreeZero?.Dispose();
        }
    }

    private readonly INativeBridge? _native;
    private readonly object _counterInitLock = new();
    private PerfCounterSet? _counterSet;
    private volatile bool _perfCountersAvailable;
    private bool _warmUpStarted;
    private bool _disposed;
    private ulong _cachedCompressedBytes;
    private DateTime _compressedCacheExpiry = DateTime.UtcNow.AddSeconds(30);

    public MemoryInfo? CurrentInfo { get; private set; }

#pragma warning disable CS0067
    public event Action<MemoryInfo>? Updated;
#pragma warning restore CS0067

    public MemoryInfoService(INativeBridge native) { _native = native; }
    public MemoryInfoService() { }

    /// <summary>
    /// Primes performance counters in the background.
    /// Call once at startup for accurate Standby/Free/Modified metrics.
    /// </summary>
    public Task WarmUpAsync()
    {
        lock (_counterInitLock)
        {
            if (_warmUpStarted) return Task.CompletedTask;
            _warmUpStarted = true;
        }

        return Task.Run(() =>
        {
            PerfCounterSet? newSet = null;
            try
            {
                newSet = new PerfCounterSet(
                    new PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes"),
                    new PerformanceCounter("Memory", "Standby Cache Reserve Bytes"),
                    new PerformanceCounter("Memory", "Modified Page List Bytes"),
                    new PerformanceCounter("Memory", "Free & Zero Page List Bytes"));
                newSet.Prime();

                lock (_counterInitLock)
                {
                    if (_disposed) { newSet.Dispose(); return; }
                    _counterSet = newSet;
                    _perfCountersAvailable = true;
                    newSet = null;
                }
            }
            catch
            {
                _perfCountersAvailable = false;
                newSet?.Dispose();
            }
        });
    }

    public MemoryInfo GetCurrentMemoryInfo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var memStatus = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        NativeMethods.GlobalMemoryStatusEx(ref memStatus);

        var perfInfo = new NativeMethods.PERFORMANCE_INFORMATION
        {
            cb = (uint)Marshal.SizeOf<NativeMethods.PERFORMANCE_INFORMATION>()
        };
        NativeMethods.GetPerformanceInfo(ref perfInfo, perfInfo.cb);

        var pageSize = (ulong)perfInfo.PageSize;
        var physTotal = (ulong)perfInfo.PhysicalTotal * pageSize;
        var physAvail = (ulong)perfInfo.PhysicalAvailable * pageSize;
        var sysCache = (ulong)perfInfo.SystemCache * pageSize;
        var available = memStatus.ullAvailPhys;

        ulong standbyBytes = 0, modifiedBytes = 0, freeBytes = 0;
        var counterSet = _counterSet;
        if (_perfCountersAvailable && counterSet != null)
        {
            try
            {
                standbyBytes = (ulong)(counterSet.StandbyNormal?.NextValue() ?? 0)
                             + (ulong)(counterSet.StandbyReserve?.NextValue() ?? 0);
                modifiedBytes = (ulong)(counterSet.Modified?.NextValue() ?? 0);
                freeBytes = (ulong)(counterSet.FreeZero?.NextValue() ?? 0);
            }
            catch { _perfCountersAvailable = false; }
        }

        if (!_perfCountersAvailable)
        {
            // Fallback estimation
            standbyBytes = available > 0 ? Math.Min(sysCache, available) : 0;
            freeBytes = available > standbyBytes ? available - standbyBytes : 0;
        }

        ulong compressedBytes = GetCompressedMemoryBytes();

        return new MemoryInfo
        {
            TotalPhysicalBytes = (long)physTotal,
            AvailablePhysicalBytes = (long)available,
            CachedBytes = (long)sysCache,
            StandbyCacheBytes = (long)standbyBytes,
            FreeBytes = (long)freeBytes,
            ModifiedBytes = (long)modifiedBytes,
            CommittedBytes = (long)((ulong)perfInfo.CommitTotal * pageSize),
            CompressedBytes = (long)compressedBytes,
            KernelPagedBytes = (long)((ulong)perfInfo.KernelPaged * pageSize),
            KernelNonpagedBytes = (long)((ulong)perfInfo.KernelNonpaged * pageSize),
            CommitTotalBytes = (long)((ulong)perfInfo.CommitTotal * pageSize),
            CommitLimitBytes = (long)((ulong)perfInfo.CommitLimit * pageSize),
            ProcessCount = perfInfo.ProcessCount,
            ThreadCount = perfInfo.ThreadCount,
            HandleCount = perfInfo.HandleCount,
        };
    }

    private ulong GetCompressedMemoryBytes()
    {
        if (DateTime.UtcNow < _compressedCacheExpiry)
            return _cachedCompressedBytes;

        try
        {
            var procs = Process.GetProcessesByName("Memory Compression");
            if (procs.Length > 0)
            {
                _cachedCompressedBytes = (ulong)procs[0].WorkingSet64;
                foreach (var p in procs) p.Dispose();
            }
            else
            {
                _cachedCompressedBytes = 0;
            }
        }
        catch { _cachedCompressedBytes = 0; }

        _compressedCacheExpiry = DateTime.UtcNow.AddSeconds(30);
        return _cachedCompressedBytes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        PerfCounterSet? setToDispose;
        lock (_counterInitLock)
        {
            _perfCountersAvailable = false;
            setToDispose = _counterSet;
            _counterSet = null;
        }
        setToDispose?.Dispose();
    }
}
