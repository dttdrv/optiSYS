using OptiSYS.Core.Models;

namespace OptiSYS.Core.Interfaces;

/// <summary>
/// Minimal surface over <see cref="Services.MemoryOptimizer"/>.
///
/// <para>
/// Only <see cref="TrimProcessWorkingSets"/> is exposed for the first-cut app —
/// the concrete class has many other knobs (purge standby, flush modified, etc)
/// but the VM's "Optimize Memory" button drives a single entry point for v1.
/// Add methods here on demand as new UI affordances appear.
/// </para>
/// </summary>
public interface IMemoryOptimizer : IDisposable
{
    /// <summary>
    /// Walks the process list, enqueues the top-N working-set candidates, then calls
    /// <c>EmptyWorkingSet</c> on each. Excludes system-critical processes and the foreground app.
    /// </summary>
    /// <param name="targetAvailableBytes">
    /// If &gt; 0, stops early once this much physical memory is available (saves work
    /// when a quick trim already satisfies demand).
    /// </param>
    /// <returns>
    /// <c>trimmed</c>: how many processes were successfully emptied.
    /// <c>failed</c>: how many candidates returned error on OpenProcess/EmptyWorkingSet.
    /// <c>skipped</c>: candidates rejected (excluded, foreground, below minimum WS).
    /// <c>earlyExit</c>: true if the target was hit before exhausting the queue.
    /// <c>freedBytes</c>: total working-set bytes actually reclaimed (before-after), for an honest
    /// "Freed" figure rather than a noisy system-wide available-memory delta.
    /// </returns>
    (int trimmed, int failed, int skipped, bool earlyExit, long freedBytes) TrimProcessWorkingSets(long targetAvailableBytes = 0);

    /// <summary>
    /// Runs the full memory optimization pass (matches optiRAM's pipeline). The reclaim depth is
    /// driven by <paramref name="level"/>: Balanced does working-set trim + accessed-bits reset +
    /// modified-list flush + low-priority standby purge + file/registry cache flush + page-combine;
    /// Aggressive (Max) adds system-wide working-set empty + full standby-list purge. Adaptive
    /// escalation bails out early (reading live usage) when a lighter pass already drops below
    /// <paramref name="targetThresholdPercent"/>, so the heaviest steps only run under sustained
    /// pressure.
    /// </summary>
    OptimizationResult OptimizeAll(
        OptimizationLevel level = OptimizationLevel.Balanced,
        int cacheMaxPercent = 0,
        int targetThresholdPercent = 0,
        bool isLowMemory = false,
        int accessedBitsDelayMs = 2000,
        bool effectivenessTrackingEnabled = true);

    /// <summary>
    /// Lowers the memory priority of well-known background processes (indexers / updaters /
    /// sync daemons / telemetry) on a curated allowlist to <c>MEMORY_PRIORITY_LOW</c>. This is
    /// a pure page-eviction-order hint with zero disk IO, reversible by the OS to NORMAL, and
    /// is the only continuous memory lever that is safe by default. Critical and excluded
    /// (protected) processes are never touched. Returns the number of processes hinted.
    /// </summary>
    int HintBackgroundMemoryPriority();

    /// <summary>Processes excluded from trimming or optimization.</summary>
    HashSet<string> ExcludedProcesses { get; set; }
}
