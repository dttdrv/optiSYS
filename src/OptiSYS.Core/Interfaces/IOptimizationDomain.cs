using System.Text.Json;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Interfaces;

/// <summary>
/// Contract for an optimization strategy.
/// Each domain is independently testable, snapshotable, and revertible.
/// </summary>
public interface IOptimizationDomain : IDisposable
{
    string Id { get; }
    string DisplayName { get; }
    string Category { get; }
    bool IsSupported { get; }
    bool IsActive { get; }

    /// <summary>
    /// Whether this domain is opted in for the given settings. The enable gate is owned by each
    /// domain (not a central switch), so a new domain becomes active by registration alone and a
    /// typo can never silently leave it disabled. The engine consults this before applying.
    /// </summary>
    bool IsEnabled(Settings settings);

    DomainSnapshot CaptureBaseline();
    ApplyResult Apply(DomainSnapshot baseline);
    void Revert(DomainSnapshot baseline);
    DomainStatus GetStatus();
}

/// <summary>
/// Optional capability for domains whose revert can partially fail (e.g. writing power-scheme
/// values). When a domain implements this, the engine removes its crash-recovery snapshot ONLY
/// when the revert reports success — so a failed restore retains the snapshot for a later retry
/// instead of discarding the only copy of the user's original values.
/// </summary>
public interface IVerifiableRevert
{
    /// <summary>Revert and report whether every restore operation succeeded.</summary>
    bool TryRevert(DomainSnapshot baseline);
}

/// <summary>
/// Captures pre-optimization state for crash recovery.
/// Uses JsonElement for flexible type storage across domains.
/// </summary>
public sealed class DomainSnapshot
{
    public string DomainId { get; init; } = string.Empty;
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
    public Dictionary<string, JsonElement> State { get; init; } = [];

    public void Set<T>(string key, T value)
    {
        State[key] = JsonSerializer.SerializeToElement(value);
    }

    public T? Get<T>(string key)
    {
        if (!State.TryGetValue(key, out var element))
            return default;
        return element.Deserialize<T>();
    }

    public bool Has(string key) => State.ContainsKey(key);
}

/// <summary>
/// Result of applying a single optimization domain.
/// </summary>
public sealed class ApplyResult
{
    public bool Success { get; init; }
    public string DomainId { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int ItemsOptimized { get; init; }
    public int ItemsFailed { get; init; }
    public int ItemsSkipped { get; init; }
    public TimeSpan Duration { get; init; }

    public static ApplyResult Ok(string domainId, string message = "",
        int optimized = 0, int failed = 0, int skipped = 0, TimeSpan duration = default) =>
        new() { Success = true, DomainId = domainId, Message = message,
                ItemsOptimized = optimized, ItemsFailed = failed, ItemsSkipped = skipped, Duration = duration };

    public static ApplyResult Fail(string domainId, string message, TimeSpan duration = default) =>
        new() { Success = false, DomainId = domainId, Message = message, Duration = duration };
}

/// <summary>
/// Live status of an optimization domain, bound to the UI.
/// </summary>
public sealed class DomainStatus
{
    public string DomainId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool IsSupported { get; init; }
    public bool IsActive { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string[] Details { get; init; } = [];
}