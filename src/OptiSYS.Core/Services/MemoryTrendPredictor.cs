namespace OptiSYS.Core.Services;

/// <summary>
/// Predicts imminent memory pressure so a gentle, background-only Conservative trim can fire
/// <em>before</em> the reactive usage threshold is crossed — smoothing the experience without
/// ever acting during normal use. The trend estimate is Holt double exponential smoothing
/// (level + per-second trend), which discounts old samples exponentially: a burst that starts
/// after a long flat period is seen within two or three ticks, where a windowed least-squares
/// fit would average the burst against minutes of stale flat samples and miss the breach.
/// O(1) state — no sample window is stored — and the per-second trend makes irregular sampling
/// cadences safe.
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
    // Smoothing factors: alpha tracks the level, beta the trend. 0.5/0.5 reacts within two or
    // three watcher ticks while still halving single-tick noise; on exactly linear input the
    // trend converges to the true slope immediately, so the fire timing is deterministic.
    private const double Alpha = 0.5;
    private const double Beta = 0.5;

    // A trend needs at least two intervals (three observations) before it is trusted — one
    // interval is a delta, not a trend, and firing on it would be a hair trigger on noise.
    private const int MinObservations = 3;

    private readonly int _predictiveLeadSeconds;
    private readonly int _hysteresisGap;
    private readonly double _commitTrigger;
    private readonly Func<DateTime> _utcNow;

    private double _level;
    private double _trendPerSecond;
    private DateTime _lastObservedAt;
    private int _observations;
    private bool _armed = true;

    public MemoryTrendPredictor(
        int predictiveLeadSeconds = 15,
        int hysteresisGap = 10,
        double commitTrigger = 0.65,
        Func<DateTime>? utcNow = null)
    {
        _predictiveLeadSeconds = Math.Max(1, predictiveLeadSeconds);
        _hysteresisGap = Math.Max(1, hysteresisGap);
        _commitTrigger = commitTrigger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Current trend estimate in usage-percent per second. Read by the watcher's
    /// adaptive cadence (a flat trend is half of the "calm" definition) and by tests.</summary>
    public double TrendPerSecond => _trendPerSecond;

    /// <summary>
    /// Feed the latest sample. Must be called every monitoring tick (even while a cleanup
    /// cooldown is active) so the trend stays accurate. <paramref name="usagePercent"/> is 0–100.
    /// </summary>
    public void Observe(double usagePercent)
    {
        var now = _utcNow();

        if (_observations == 0)
        {
            _level = usagePercent;
            _trendPerSecond = 0;
        }
        else
        {
            var dt = (now - _lastObservedAt).TotalSeconds;
            if (dt <= 0)
                return;   // same-instant duplicate: no information, keep the state untouched

            if (_observations == 1)
            {
                // Initialize the trend from the first real interval so linear input converges
                // to the exact slope immediately instead of warming up from zero.
                _trendPerSecond = (usagePercent - _level) / dt;
                _level = usagePercent;
            }
            else
            {
                var forecast = _level + _trendPerSecond * dt;
                var newLevel = Alpha * usagePercent + (1 - Alpha) * forecast;
                _trendPerSecond = Beta * ((newLevel - _level) / dt) + (1 - Beta) * _trendPerSecond;
                _level = newLevel;
            }
        }

        _lastObservedAt = now;
        _observations++;
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

        if (_observations < MinObservations || _trendPerSecond <= 0)
            return false;

        var projected = usagePercent + _trendPerSecond * _predictiveLeadSeconds;
        if (projected < thresholdPercent)
            return false;

        _armed = false; // fire once; re-arms only after pressure clears
        return true;
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
