using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

/// <summary>
/// Pins the predictive pre-emptive-trim decision. The clock is injected so OLS slopes are
/// deterministic. Threshold 50, lead 15s, gap 10, commit gate 0.65 throughout.
/// </summary>
public class MemoryTrendPredictorTests
{
    private const int Threshold = 50;
    private const double HighCommit = 0.70;
    private const double LowCommit = 0.40;

    // A controllable clock: the closure captures `t`, so advancing it moves "now".
    private static (MemoryTrendPredictor predictor, Action<int> advance) Build(int window = 10)
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var predictor = new MemoryTrendPredictor(
            trendWindow: window, predictiveLeadSeconds: 15, hysteresisGap: 10,
            commitTrigger: 0.65, utcNow: () => t);
        return (predictor, seconds => t = t.AddSeconds(seconds));
    }

    /// <summary>Feeds a steep rising trend (30→40→45 over 20s); slope ≈ 0.75 %/s.</summary>
    private static MemoryTrendPredictor RisingTrend()
    {
        var (p, advance) = Build();
        p.Observe(30); advance(10);
        p.Observe(40); advance(10);
        p.Observe(45);
        return p;
    }

    [Fact]
    public void Fires_OnRisingTrend_ProjectedToBreach_UnderCommitPressure()
    {
        var p = RisingTrend();
        // projected = 45 + 0.75*15 = 56.25 ≥ 50, usage 45 < 50, commit > gate → fire.
        Assert.True(p.ShouldPreemptivelyTrim(45, HighCommit, Threshold));
    }

    [Fact]
    public void DoesNotFire_WhenCommitBelowGate_FileCopyFillingStandbyCache()
    {
        var p = RisingTrend();
        // Same rising usage, but real committed demand is low (a file copy filling reclaimable
        // standby cache) → must NOT trim. This is the unnoticeability guarantee.
        Assert.False(p.ShouldPreemptivelyTrim(45, LowCommit, Threshold));
    }

    [Fact]
    public void DoesNotFire_WhenTrendFlatOrDeclining()
    {
        var (p, advance) = Build();
        p.Observe(45); advance(10);
        p.Observe(40); advance(10);
        p.Observe(30);
        Assert.False(p.ShouldPreemptivelyTrim(30, HighCommit, Threshold)); // slope ≤ 0
    }

    [Fact]
    public void DoesNotFire_WhenAlreadyAtOrAboveThreshold()
    {
        var p = RisingTrend();
        Assert.False(p.ShouldPreemptivelyTrim(50, HighCommit, Threshold)); // reactive path's job
    }

    [Fact]
    public void DoesNotFire_WithFewerThanThreeSamples()
    {
        var (p, advance) = Build();
        p.Observe(30); advance(10);
        p.Observe(48);
        Assert.False(p.ShouldPreemptivelyTrim(48, HighCommit, Threshold)); // slope undefined < 3 samples
    }

    [Fact]
    public void FiresOnce_ThenStaysDisarmed_UntilPressureClears_ThenReArms()
    {
        var p = RisingTrend();

        Assert.True(p.ShouldPreemptivelyTrim(45, HighCommit, Threshold));   // fires, disarms
        Assert.False(p.ShouldPreemptivelyTrim(46, HighCommit, Threshold));  // one-shot: still disarmed
        Assert.False(p.ShouldPreemptivelyTrim(55, HighCommit, Threshold));  // ≥ threshold: false, but re-arms
        Assert.True(p.ShouldPreemptivelyTrim(45, HighCommit, Threshold));   // re-armed → fires again
    }

    [Fact]
    public void ReArms_WhenUsageFallsBelowThresholdMinusGap()
    {
        var p = RisingTrend();

        Assert.True(p.ShouldPreemptivelyTrim(45, HighCommit, Threshold));   // fires, disarms
        // 20 ≤ 50-10 → re-arms; projected 20 + 0.75*15 = 31.25 < 50 so no fire on this reading.
        Assert.False(p.ShouldPreemptivelyTrim(20, HighCommit, Threshold));
        Assert.True(p.ShouldPreemptivelyTrim(45, HighCommit, Threshold));   // re-armed → fires again
    }
}
