using System.Diagnostics;
using System.Runtime.InteropServices;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Services;

/// <summary>
/// Provides real-time memory information using Windows APIs.
/// Ported from optiRAM with performance counter support.
/// </summary>
public sealed class MemoryInfoService : IDisposable
{
    private readonly INativeBridge? _native;
    private bool _disposed;
    private ulong _cachedCompressedBytes;
    private DateTime _compressedCacheExpiry = DateTime.UtcNow.AddSeconds(30);

    public MemoryInfo? CurrentInfo { get; private set; }
    public event Action<MemoryInfo>? Updated;

    public MemoryInfoService(INativeBridge native)
    {
        _native = native;
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

        ulong compressedBytes = GetCompressedMemoryBytes();

        return new MemoryInfo
        {
            TotalPhysicalBytes = (long)physTotal,
            AvailablePhysicalBytes = (long)available,
            CachedBytes = (long)sysCache,
            StandbyCacheBytes = 0, // Performance counters not available in this path
            FreeBytes = (long)(available > sysCache ? available - sysCache : 0),
            ModifiedBytes = 0,
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
    }
}
