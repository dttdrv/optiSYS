namespace OptiSYS.Core.Services;

/// <summary>
/// Predicts imminent memory pressure so a gentle, background-only Conservative trim can fire
/// <em>before</em> the reactive usage threshold is crossed — smoothing the experience without
/// ever acting during normal use. Ported from optiRAM's OLS-slope + armed/hysteresis trigger,
/// with one added safety gate: a commit-ratio floor.
///
/// <para>
/// <b>Why the commit gate matters.</b> <c>UsagePercent</c> counts standby cache as "used", so a
/// large file copy filling reclaimable cache makes usage climb even though there is no real
/// memory demand. <c>CommitTotal/CommitLimit</c> does not move for cache, so requiring
/// <c>commitRatio &gt; CommitTrigger</c> means the predictor only fires under genuine demand —
/// keeping pre-emptive trims unnoticeable.
/// </para>
///
/// <para>Pure and deterministic: the clock is injected, so behavior is fully unit-testable.</para>
/// </summary>
public sealed class MemoryTrendPredictor
{
    private readonly int _trendWindow;
    private readonly int _predictiveLeadSeconds;
    private readonly int _hysteresisGap;
    private readonly double _commitTrigger;
    private readonly Func<DateTime> _utcNow;

    private readonly Queue<(DateTime time, double usage)> _history = new();
    private bool _armed = true;

    public MemoryTrendPredictor(
        int trendWindow = 10,
        int predictiveLeadSeconds = 15,
        int hysteresisGap = 10,
        double commitTrigger = 0.65,
        Func<DateTime>? utcNow = null)
    {
        _trendWindow = Math.Max(3, trendWindow);
        _predictiveLeadSeconds = Math.Max(1, predictiveLeadSeconds);
        _hysteresisGap = Math.Max(1, hysteresisGap);
        _commitTrigger = commitTrigger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Feed the latest sample into the trend window. Must be called every monitoring tick (even
    /// while a cleanup cooldown is active) so the slope stays accurate. <paramref name="usagePercent"/>
    /// is 0–100.
    /// </summary>
    public void Observe(double usagePercent)
    {
        _history.Enqueue((_utcNow(), usagePercent));
        while (_history.Count > _trendWindow)
            _history.Dequeue();
    }

    /// <summary>
    /// True when the usage trend is projected to reach <paramref name="thresholdPercent"/> within
    /// the predictive lead time AND real commit demand exceeds the gate. One-shot: returns true at
    /// most once until the pressure clears and re-arms (see <see cref="UpdateArm"/>). Disarms on fire.
    /// </summary>
    public bool ShouldPreemptivelyTrim(double usagePercent, double commitRatio, int thresholdPercent)
    {
        UpdateArm(usagePercent, thresholdPercent);

        // Only a *pre-emptive* signal: at/above threshold is the reactive path's job, not ours.
        if (!_armed || usagePercent >= thresholdPercent)
            return false;

        if (commitRatio <= _commitTrigger)
            return false;

        var slopePerSecond = ComputeUsageSlopePerSecond();
        if (slopePerSecond <= 0)
            return false;

        var projected = usagePercent + slopePerSecond * _predictiveLeadSeconds;
        if (projected < thresholdPercent)
            return false;

        _armed = false; // fire once; re-arms only after pressure clears
        return true;
    }

    /// <summary>
    /// Ordinary-least-squares slope of usage% over the sampled window, in percent-per-second.
    /// Positive ⇒ usage rising. Needs ≥3 samples; returns 0 otherwise or on a degenerate fit.
    /// </summary>
    internal double ComputeUsageSlopePerSecond()
    {
        if (_history.Count < 3)
            return 0;

        var samples = _history.ToArray();
        var t0 = samples[0].time;
        int n = samples.Length;
        double sumT = 0, sumU = 0, sumTU = 0, sumT2 = 0;

        for (int i = 0; i < n; i++)
        {
            double t = (samples[i].time - t0).TotalSeconds;
            double u = samples[i].usage;
            sumT += t;
            sumU += u;
            sumTU += t * u;
            sumT2 += t * t;
        }

        double denominator = n * sumT2 - sumT * sumT;
        if (Math.Abs(denominator) < 0.0001)
            return 0;

        return (n * sumTU - sumT * sumU) / denominator;
    }

    /// <summary>
    /// Hysteresis: re-arm when usage reaches the threshold (reactive territory) or falls back below
    /// <c>threshold - gap</c>. Between fire and recovery the predictor stays disarmed so it cannot
    /// thrash on every tick.
    /// </summary>
    private void UpdateArm(double usagePercent, int thresholdPercent)
    {
        if (usagePercent >= thresholdPercent)
        {
            _armed = true;
            return;
        }

        var rearmAt = Math.Max(0, thresholdPercent - _hysteresisGap);
        if (usagePercent <= rearmAt)
            _armed = true;
    }
}
