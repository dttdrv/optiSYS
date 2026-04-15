using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Domains.Memory;

/// <summary>
/// Memory optimization domain that wraps the MemoryOptimizer service
/// as an IOptimizationDomain for use in the unified engine.
/// </summary>
public sealed class MemoryOptimizerDomain : IOptimizationDomain
{
    private readonly Settings _settings;
    private bool _isActive;

    public string Id => "memory-optimize";
    public string DisplayName => "Memory Optimization";
    public string Category => "Memory";
    public bool IsSupported => true; // Always supported on Windows
    public bool IsActive => _isActive;

    public MemoryOptimizerDomain(Settings settings)
    {
        _settings = settings;
    }

    public DomainSnapshot CaptureBaseline()
    {
        // Captures current memory state as a baseline for potential revert
        return new DomainSnapshot
        {
            DomainId = Id,
            Timestamp = DateTime.UtcNow,
            State = new Dictionary<string, object>
            {
                ["active"] = _isActive,
            }
        };
    }

    public ApplyResult Apply(DomainSnapshot baseline)
    {
        // The actual memory optimization is performed by the MemoryOptimizer service
        // This domain simply tracks the activation state
        _isActive = true;
        return ApplyResult.Ok(Id, "Memory optimization activated");
    }

    public void Revert(DomainSnapshot baseline)
    {
        // Memory optimization is inherently reversible — Windows will page data back as needed
        _isActive = false;
    }

    public DomainStatus GetStatus()
    {
        return new DomainStatus
        {
            DomainId = Id,
            DisplayName = DisplayName,
            Category = Category,
            IsSupported = IsSupported,
            IsActive = _isActive,
            Summary = _isActive ? "Active" : "Idle"
        };
    }

    public void Dispose()
    {
        _isActive = false;
    }
}
