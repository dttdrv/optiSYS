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
    /// </returns>
    (int trimmed, int failed, int skipped, bool earlyExit) TrimProcessWorkingSets(long targetAvailableBytes = 0);

    /// <summary>Runs the full memory optimization pass used by the memory domain.</summary>
    /// <param name="deepClean">
    /// When <c>true</c>, unlocks the destructive one-shot reclaim steps — a full
    /// <c>PurgeStandbyList</c> and a system-wide <c>EmptySystemWorkingSets</c> trim, plus
    /// <c>CombinePhysicalMemory</c>. These can briefly cause disk activity / stutter and are
    /// NEVER reachable from any automatic path; only an explicit user "Deep clean now" action
    /// passes <c>true</c>. When <c>false</c> (the default), no system-wide trim, no full standby
    /// purge, and no forced page-combine occurs regardless of <paramref name="level"/>.
    /// </param>
    OptimizationResult OptimizeAll(
        OptimizationLevel level = OptimizationLevel.Conservative,
        int cacheMaxPercent = 0,
        int targetThresholdPercent = 0,
        bool isLowMemory = false,
        int accessedBitsDelayMs = 2000,
        bool effectivenessTrackingEnabled = true,
        bool deepClean = false);

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
