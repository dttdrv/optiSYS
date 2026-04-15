using System.Diagnostics;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;

namespace OptiSYS.Core.Domains.Memory;

/// <summary>
/// Memory optimization domain that performs working set trimming,
/// standby list purging, and system memory management.
/// Wraps MemoryOptimizer as an IOptimizationDomain for the unified engine.
/// </summary>
public sealed class MemoryOptimizerDomain : IOptimizationDomain
{
    private readonly Settings _settings;
    private readonly MemoryOptimizer _optimizer;
    private readonly MemoryInfoService _memoryInfo;
    private bool _isActive;
    private bool _disposed;

    public string Id => "memory-optimize";
    public string DisplayName => "Memory Optimization";
    public string Category => "Memory";
    public bool IsSupported => true;
    public bool IsActive => _isActive;

    public MemoryOptimizerDomain(Settings settings)
    {
        _settings = settings;
        _memoryInfo = new MemoryInfoService(new Native.ManagedNativeBridge());
        _optimizer = new MemoryOptimizer(_memoryInfo);
    }

    internal MemoryOptimizerDomain(Settings settings, MemoryOptimizer optimizer, MemoryInfoService memoryInfo)
    {
        _settings = settings;
        _optimizer = optimizer;
        _memoryInfo = memoryInfo;
    }

    public DomainSnapshot CaptureBaseline()
    {
        var snapshot = new DomainSnapshot { DomainId = Id };

        try
        {
            var info = _memoryInfo.GetCurrentMemoryInfo();
            snapshot.Set("totalBytes", info.TotalPhysicalBytes);
            snapshot.Set("availableBytes", info.AvailablePhysicalBytes);
            snapshot.Set("usagePercent", info.UsagePercent);
        }
        catch { }

        return snapshot;
    }

    public ApplyResult Apply(DomainSnapshot baseline)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            _optimizer.ExcludedProcesses = new HashSet<string>(
                _settings.MemoryExcludedProcesses, StringComparer.OrdinalIgnoreCase);

            var result = _optimizer.OptimizeAll(
                level: _settings.OptimizationLevel,
                cacheMaxPercent: _settings.CacheMaxPercent,
                targetThresholdPercent: _settings.MemoryThresholdPercent,
                accessedBitsDelayMs: _settings.AccessedBitsDelayMs,
                effectivenessTrackingEnabled: _settings.EffectivenessTrackingEnabled);

            _isActive = true;
            sw.Stop();

            if (result.Success)
            {
                return ApplyResult.Ok(Id, result.Message,
                    optimized: result.ProcessesTrimmed,
                    duration: sw.Elapsed);
            }

            return ApplyResult.Fail(Id, result.Message ?? "Unknown error", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ApplyResult.Fail(Id, $"Memory optimization failed: {ex.Message}", sw.Elapsed);
        }
    }

    public void Revert(DomainSnapshot baseline)
    {
        // Memory optimization is inherently reversible: Windows pages data back as needed
        _isActive = false;
    }

    public DomainStatus GetStatus() => new()
    {
        DomainId = Id,
        DisplayName = DisplayName,
        Category = Category,
        IsSupported = IsSupported,
        IsActive = _isActive,
        Summary = _isActive ? "Memory optimized" : "Idle",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _optimizer.Dispose();
        _memoryInfo.Dispose();
    }
}
