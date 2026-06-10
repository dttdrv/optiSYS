namespace OptiSYS.Core.Services;

/// <summary>
/// Adaptive cadence for the memory watcher tick — the watcher's own wakeups are a battery cost.
/// Calm means usage at least <see cref="CalmHeadroomPercent"/> points below the reactive
/// threshold AND a flat-or-falling Holt trend; from the second consecutive calm sample the tick
/// doubles toward the cap (~4x fewer wakeups in steady state), and the first non-calm sample
/// snaps it back to base on that very tick. The cap stays modest on purpose: a worst-case
/// allocation burst is seen within one stretched tick, where the critical-escalation path
/// reclaims immediately and bypasses the cooldown. Pure and clock-free.
/// </summary>
public sealed class WatcherCadencePolicy
{
    private const double CalmHeadroomPercent = 15;
    private const double CalmTrendPerSecond = 0.05;

    private readonly TimeSpan _baseInterval;
    private readonly TimeSpan _maxInterval;
    private int _calmStreak;

    public WatcherCadencePolicy(TimeSpan baseInterval, TimeSpan maxInterval)
    {
        _baseInterval = baseInterval;
        _maxInterval = maxInterval < baseInterval ? baseInterval : maxInterval;
        Current = baseInterval;
    }

    public TimeSpan Current { get; private set; }

    public void Observe(double usagePercent, int thresholdPercent, double trendPerSecond)
    {
        var calm = usagePercent <= thresholdPercent - CalmHeadroomPercent
                   && trendPerSecond <= CalmTrendPerSecond;

        if (!calm)
        {
            _calmStreak = 0;
            Current = _baseInterval;
            return;
        }

        _calmStreak++;
        if (_calmStreak >= 2)
        {
            var doubled = TimeSpan.FromTicks(Current.Ticks * 2);
            Current = doubled < _maxInterval ? doubled : _maxInterval;
        }
    }
}
