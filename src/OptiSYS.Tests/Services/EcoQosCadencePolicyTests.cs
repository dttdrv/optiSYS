using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

/// <summary>
/// The maintenance cadence backs off only on eligible-but-no-op sweeps (the expensive case: a
/// full process enumeration that changed nothing). Skipped ticks are cheap eligibility checks
/// and must stay at the fast cadence so a power flip is picked up within one base interval;
/// real work snaps the cadence back so bursts of change are tracked closely.
/// </summary>
public sealed class EcoQosCadencePolicyTests
{
    private static EcoQosCadencePolicy Policy() =>
        new(baseInterval: TimeSpan.FromSeconds(6), maxInterval: TimeSpan.FromSeconds(60));

    [Fact]
    public void StartsAtTheBaseInterval()
    {
        Assert.Equal(TimeSpan.FromSeconds(6), Policy().Current);
    }

    [Fact]
    public void ConsecutiveNoOps_DoubleTheIntervalUpToTheCap()
    {
        var policy = Policy();

        Assert.Equal(12, policy.Next(EcoQosCadencePolicy.Outcome.NoOp).TotalSeconds);
        Assert.Equal(24, policy.Next(EcoQosCadencePolicy.Outcome.NoOp).TotalSeconds);
        Assert.Equal(48, policy.Next(EcoQosCadencePolicy.Outcome.NoOp).TotalSeconds);
        Assert.Equal(60, policy.Next(EcoQosCadencePolicy.Outcome.NoOp).TotalSeconds);   // capped
        Assert.Equal(60, policy.Next(EcoQosCadencePolicy.Outcome.NoOp).TotalSeconds);   // stays capped
    }

    [Fact]
    public void DidWork_SnapsBackToTheBaseInterval()
    {
        var policy = Policy();
        policy.Next(EcoQosCadencePolicy.Outcome.NoOp);
        policy.Next(EcoQosCadencePolicy.Outcome.NoOp);

        Assert.Equal(TimeSpan.FromSeconds(6), policy.Next(EcoQosCadencePolicy.Outcome.DidWork));
    }

    [Fact]
    public void Skipped_SnapsBackToTheBaseInterval()
    {
        // Ineligible ticks never run the sweep — they are a couple of cheap reads — so the
        // cadence stays fast while skipped and the next eligible state is seen promptly.
        var policy = Policy();
        policy.Next(EcoQosCadencePolicy.Outcome.NoOp);
        policy.Next(EcoQosCadencePolicy.Outcome.NoOp);

        Assert.Equal(TimeSpan.FromSeconds(6), policy.Next(EcoQosCadencePolicy.Outcome.Skipped));
    }

    [Fact]
    public void Reset_RestoresTheBaseInterval()
    {
        var policy = Policy();
        policy.Next(EcoQosCadencePolicy.Outcome.NoOp);
        policy.Next(EcoQosCadencePolicy.Outcome.NoOp);

        policy.Reset();

        Assert.Equal(TimeSpan.FromSeconds(6), policy.Current);
    }
}
