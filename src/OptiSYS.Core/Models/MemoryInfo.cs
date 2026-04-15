namespace OptiSYS.Core.Models;

/// <summary>
/// Real-time memory state information.
/// </summary>
public sealed class MemoryInfo
{
    public long TotalPhysicalBytes { get; init; }
    public long AvailablePhysicalBytes { get; init; }
    public long CommittedBytes { get; init; }
    public long StandbyCacheBytes { get; init; }
    public long ModifiedBytes { get; init; }
    public long FreeBytes { get; init; }
    public long CachedBytes { get; init; }
    public long CompressedBytes { get; init; }
    public long KernelPagedBytes { get; init; }
    public long KernelNonpagedBytes { get; init; }
    public long CommitTotalBytes { get; init; }
    public long CommitLimitBytes { get; init; }
    public uint ProcessCount { get; init; }
    public uint ThreadCount { get; init; }
    public uint HandleCount { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public long UsedBytes => TotalPhysicalBytes - AvailablePhysicalBytes;
    public double UsagePercent => TotalPhysicalBytes > 0 ? (double)UsedBytes / TotalPhysicalBytes * 100 : 0;
    public double AvailablePercent => TotalPhysicalBytes > 0 ? (double)AvailablePhysicalBytes / TotalPhysicalBytes * 100 : 0;

    public string TotalDisplay => FormatBytes(TotalPhysicalBytes);
    public string AvailableDisplay => FormatBytes(AvailablePhysicalBytes);
    public string UsedDisplay => FormatBytes(UsedBytes);

    public double TotalGB => TotalPhysicalBytes / (1024.0 * 1024 * 1024);
    public double UsedGB => UsedBytes / (1024.0 * 1024 * 1024);
    public double AvailableGB => AvailablePhysicalBytes / (1024.0 * 1024 * 1024);
    public double StandbyGB => StandbyCacheBytes / (1024.0 * 1024 * 1024);
    public double CompressedMB => CompressedBytes / (1024.0 * 1024);
    public double CommitPercent => CommitLimitBytes > 0 ? (double)CommitTotalBytes / CommitLimitBytes * 100 : 0;

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        _ => $"{bytes} B"
    };
}

public enum PressureLevel
{
    Normal,
    Elevated,
    High,
    Critical
}

/// <summary>
/// Result of a memory optimization pass.
/// </summary>
public sealed class OptimizationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public long FreedBytes { get; init; }
    public int ProcessesTrimmed { get; init; }
    public int ProcessesSkipped { get; init; }
    public int ProcessesFailed { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public string FreedDisplay => FormatBytesStatic(FreedBytes);

    public static string FormatBytesStatic(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        _ => $"{bytes} B"
    };
}

/// <summary>
/// Per-process memory information.
/// </summary>
public sealed class ProcessMemoryInfo
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public long WorkingSetBytes { get; init; }
    public long PrivateBytes { get; init; }
    public bool IsForeground { get; init; }
    public bool IsExcluded { get; init; }
    public ProcessPriorityClass PriorityClass { get; init; }
}

public enum ProcessPriorityClass
{
    Idle = 64,
    BelowNormal = 16384,
    Normal = 32,
    AboveNormal = 32768,
    High = 128,
    RealTime = 256
}
