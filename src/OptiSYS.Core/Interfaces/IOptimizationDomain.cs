namespace OptiSYS.Core.Interfaces;

/// <summary>
/// Contract for an optimization strategy.
/// Each domain is independently testable, snapshotable, and revertible.
/// </summary>
public interface IOptimizationDomain : IDisposable
{
    /// <summary>Unique identifier (e.g., "ecoqos", "timer-resolution", "memory-trim").</summary>
    string Id { get; }

    /// <summary>Human-readable name for UI display.</summary>
    string DisplayName { get; }

    /// <summary>Category: "Battery" or "Memory".</summary>
    string Category { get; }

    /// <summary>Whether this optimization is supported on the current hardware/OS.</summary>
    bool IsSupported { get; }

    /// <summary>Whether this optimization is currently applied.</summary>
    bool IsActive { get; }

    /// <summary>Capture the current system state before optimization.</summary>
    DomainSnapshot CaptureBaseline();

    /// <summary>Apply the optimization using the baseline for reference.</summary>
    ApplyResult Apply(DomainSnapshot baseline);

    /// <summary>Revert to the exact state captured in the snapshot.</summary>
    void Revert(DomainSnapshot baseline);

    /// <summary>Get live status for UI binding.</summary>
    DomainStatus GetStatus();
}

public sealed class DomainSnapshot
{
    public string DomainId { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public Dictionary<string, object> State { get; init; } = new();
}

public sealed class ApplyResult
{
    public string DomainId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public Dictionary<string, object>? Metrics { get; init; }

    public static ApplyResult Ok(string domainId, string message = "") =>
        new() { DomainId = domainId, Success = true, Message = message };

    public static ApplyResult Fail(string domainId, string message) =>
        new() { DomainId = domainId, Success = false, Message = message };
}

public sealed class DomainStatus
{
    public string DomainId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool IsSupported { get; init; }
    public bool IsActive { get; init; }
    public string Summary { get; init; } = string.Empty;
    public Dictionary<string, object>? Details { get; init; }
}
