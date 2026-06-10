using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

/// <summary>
/// The memory watcher's tick is its own battery cost: when usage sits far below the threshold
/// AND the Holt trend is flat, the tick stretches (5 -> 10 -> 20s cap, ~4x fewer wakeups); the
/// first sign of pressure — usage near the threshold or a rising trend — snaps it back to base
/// on that very tick. One calm sample is never enough to stretch (noise discipline), and the
/// modest cap keeps the critical-escalation safety net within one stretched tick of a burst.
/// </summary>
public sealed class WatcherCadencePolicyTests
{
    private static WatcherCadencePolicy Policy() =>
        new(baseInterval: TimeSpan.FromSeconds(5), maxInterval: TimeSpan.FromSeconds(20));

    [Fact]
    public void StartsAtBase_AndOneCalmSampleDoesNotStretch()
    {
        var policy = Policy();
        Assert.Equal(TimeSpan.FromSeconds(5), policy.Current);

        policy.Observe(usagePercent: 30, thresholdPercent: 50, trendPerSecond: 0);

        Assert.Equal(TimeSpan.FromSeconds(5), policy.Current);
    }

    [Fact]
    public void SustainedCalm_DoublesTheTickUpToTheCap()
    {
        var policy = Policy();
        policy.Observe(30, 50, 0);
        policy.Observe(30, 50, 0);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.Current);

        policy.Observe(30, 50, 0);
        Assert.Equal(TimeSpan.FromSeconds(20), policy.Current);

        policy.Observe(30, 50, 0);
        Assert.Equal(TimeSpan.FromSeconds(20), policy.Current);   // capped
    }

    [Fact]
    public void UsageNearTheThreshold_IsNotCalm_AndSnapsBack()
    {
        var policy = Policy();
        policy.Observe(30, 50, 0);
        policy.Observe(30, 50, 0);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.Current);

        policy.Observe(40, 50, 0);   // inside the 15-point headroom band -> pressure territory

        Assert.Equal(TimeSpan.FromSeconds(5), policy.Current);
    }

    [Fact]
    public void AfterASnapBack_ReStretchingNeedsAFreshCalmStreak()
    {
        // The snap must reset the streak, not just the interval: one calm sample after pressure
        // is not yet evidence of calm.
        var policy = Policy();
        policy.Observe(30, 50, 0);
        policy.Observe(30, 50, 0);                     // stretched to 10s
        policy.Observe(45, 50, 0);                     // pressure -> snap + streak reset

        policy.Observe(30, 50, 0);                     // calm #1 again
        Assert.Equal(TimeSpan.FromSeconds(5), policy.Current);

        policy.Observe(30, 50, 0);                     // calm #2 -> stretch resumes
        Assert.Equal(TimeSpan.FromSeconds(10), policy.Current);
    }

    [Fact]
    public void RisingTrend_IsNotCalm_EvenWithLowUsage()
    {
        var policy = Policy();
        policy.Observe(30, 50, 0);
        policy.Observe(30, 50, trendPerSecond: 0.5);   // climbing fast despite low usage

        Assert.Equal(TimeSpan.FromSeconds(5), policy.Current);
    }
}
