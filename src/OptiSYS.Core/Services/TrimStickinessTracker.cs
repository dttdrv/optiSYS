namespace OptiSYS.Core.Services;

/// <summary>
/// Learns, per process, whether working-set trims actually stick. <see cref="RecordTrim"/> notes
/// the pre-trim working set when a trim runs; the next pass's <see cref="Observe"/> consumes that
/// record and compares — a working set back at ≥85% of its pre-trim size means the trim was pure
/// re-fault churn (the process touched its pages straight back in). Two consecutive bounces make
/// the process a chronic re-faulter; one trim that sticks clears the strikes. Chronic processes
/// are DEMOTED in the trim order, not excluded — under real pressure they still get trimmed,
/// which also keeps this evidence fresh. Pure and clock-free; not thread-safe by itself (owned
/// and driven by one trim pass at a time).
/// </summary>
public sealed class TrimStickinessTracker
{
    private const double BounceRatio = 0.85;
    private const int ChronicStrikes = 2;

    private sealed class PidStats
    {
        public long? PendingPreTrimBytes;
        public int ConsecutiveBounces;
    }

    private readonly Dictionary<int, PidStats> _pids = [];

    /// <summary>A trim is about to run for <paramref name="pid"/>; remember what it weighed.</summary>
    public void RecordTrim(int pid, long preTrimWorkingSetBytes)
    {
        if (!_pids.TryGetValue(pid, out var stats))
        {
            stats = new PidStats();
            _pids[pid] = stats;
        }

        stats.PendingPreTrimBytes = preTrimWorkingSetBytes;
    }

    /// <summary>
    /// The pid showed up as a candidate again with this working set: judge the previous trim.
    /// No pending trim record means nothing to judge (the pid wasn't trimmed last pass).
    /// </summary>
    public void Observe(int pid, long workingSetBytes)
    {
        if (!_pids.TryGetValue(pid, out var stats) || stats.PendingPreTrimBytes is not { } preTrim)
            return;

        stats.PendingPreTrimBytes = null;
        if (preTrim <= 0)
            return;

        var bounced = workingSetBytes >= preTrim * BounceRatio;
        stats.ConsecutiveBounces = bounced ? stats.ConsecutiveBounces + 1 : 0;
    }

    public bool IsChronicRefaulter(int pid) =>
        _pids.TryGetValue(pid, out var stats) && stats.ConsecutiveBounces >= ChronicStrikes;

    /// <summary>Forget pids no longer alive as candidates (process exit / pid reuse hygiene).</summary>
    public void RetainOnly(IReadOnlyCollection<int> alivePids)
    {
        foreach (var pid in _pids.Keys.Where(p => !alivePids.Contains(p)).ToList())
            _pids.Remove(pid);
    }
}
