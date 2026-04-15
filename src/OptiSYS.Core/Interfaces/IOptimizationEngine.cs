using OptiSYS.Core.Models;

namespace OptiSYS.Core.Interfaces;

/// <summary>
/// Orchestrates optimization domains across battery and memory categories.
/// Manages snapshot/apply/revert lifecycle with transactional semantics.
/// </summary>
public interface IOptimizationEngine : IDisposable
{
    /// <summary>Whether an optimization operation is currently in progress.</summary>
    bool IsOptimizing { get; }

    /// <summary>Whether any domain is currently active.</summary>
    bool IsActive { get; }

    /// <summary>All registered optimization domains.</summary>
    IReadOnlyList<IOptimizationDomain> Domains { get; }

    /// <summary>Activate all enabled and supported domains for the given category.</summary>
    EngineResult ActivateCategory(string category);

    /// <summary>Activate a specific domain by ID.</summary>
    EngineResult ActivateDomain(string domainId);

    /// <summary>Revert all active domains to their pre-optimization state.</summary>
    EngineResult RevertAll();

    /// <summary>Revert a specific domain by ID.</summary>
    EngineResult RevertDomain(string domainId);

    /// <summary>Attempt crash recovery for any domains with stored snapshots.</summary>
    void TryCrashRecovery();

    /// <summary>Get live status for all domains.</summary>
    List<DomainStatus> GetAllStatuses();

    /// <summary>Event raised when domain status changes or optimization progress occurs.</summary>
    event Action<EngineEvent>? EventOccurred;
}

public sealed class EngineResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public List<ApplyResult> Results { get; init; } = [];
}

public sealed class EngineEvent
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Message { get; init; } = string.Empty;
    public string? DomainId { get; init; }
}
