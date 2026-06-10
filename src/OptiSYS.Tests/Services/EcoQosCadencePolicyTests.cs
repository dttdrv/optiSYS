using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

/// <summary>
/// The policy gates the EXPENSIVE full sweep, not the tick: the controller keeps ticking at the
/// fast base cadence doing only cheap reads, while consecutive no-op sweeps stretch the sweep
/// gap geometrically (1 -> 2 -> 4 -> 8 -> capped). A change signal (foreground flip) forces an
/// immediate sweep, real work snaps the gap back, and Reset makes the next eligible tick sweep
/// at once — so every user-facing transition is still acted on within one base tick.
/// </summary>
public sealed class EcoQosCadencePolicyTests
{
    private static EcoQosCadencePolicy Policy() => new(maxGapTicks: 10);

    [Fact]
    public void FirstTick_SweepsImmediately()
    {
        Assert.True(Policy().ShouldSweep(changeSignal: false));
    }

    [Fact]
    public void ConsecutiveNoOpSweeps_DoubleTheGapUpToTheCap()
    {
        var policy = Policy();

        policy.RecordSweepOutcome(didWork: false);
        Assert.Equal(2, policy.GapTicks);
        policy.RecordSweepOutcome(didWork: false);
        Assert.Equal(4, policy.GapTicks);
        policy.RecordSweepOutcome(didWork: false);
        Assert.Equal(8, policy.GapTicks);
        policy.RecordSweepOutcome(didWork: false);
        Assert.Equal(10, policy.GapTicks);    // capped
        policy.RecordSweepOutcome(didWork: false);
        Assert.Equal(10, policy.GapTicks);    // stays capped
    }

    [Fact]
    public void WithGapOfTwo_EveryOtherTickSweeps()
    {
        var policy = Policy();
        Assert.True(policy.ShouldSweep(false));       // first tick sweeps
        policy.RecordSweepOutcome(didWork: false);    // no-op -> gap 2

        Assert.False(policy.ShouldSweep(false));      // deferred
        Assert.True(policy.ShouldSweep(false));       // gap reached -> sweep
    }

    [Fact]
    public void DidWork_SnapsTheGapBackToOne()
    {
        var policy = Policy();
        policy.RecordSweepOutcome(didWork: false);
        policy.RecordSweepOutcome(didWork: false);
        Assert.Equal(4, policy.GapTicks);

        policy.RecordSweepOutcome(didWork: true);

        Assert.Equal(1, policy.GapTicks);
    }

    [Fact]
    public void ChangeSignal_ForcesAnImmediateSweep_EvenMidGap()
    {
        var policy = Policy();
        Assert.True(policy.ShouldSweep(false));
        policy.RecordSweepOutcome(didWork: false);    // gap 2
        policy.RecordSweepOutcome(didWork: false);    // gap 4 (simulating later no-ops)

        Assert.False(policy.ShouldSweep(false));      // deferred, mid-gap

        Assert.True(policy.ShouldSweep(changeSignal: true));   // focus flip -> sweep NOW
        Assert.Equal(1, policy.GapTicks);                      // and the gap is back to fast
    }

    [Fact]
    public void Reset_MakesTheNextTickSweepImmediately()
    {
        var policy = Policy();
        Assert.True(policy.ShouldSweep(false));
        policy.RecordSweepOutcome(didWork: false);    // gap 2; counter mid-stride

        policy.Reset();

        Assert.True(policy.ShouldSweep(false));
        Assert.Equal(1, policy.GapTicks);
    }
}
