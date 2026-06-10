using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

/// <summary>
/// The self-tuning loop for the cleanup pipeline: per-step EWMA of measured reclaim, active
/// after three samples (the old 10-samples-inside-30-minutes window never filled at real
/// automatic-cleanup cadences, so the learning was inert in production), plus an
/// explore/exploit re-probe — every Nth skip actually runs the step so evidence stays fresh
/// and a step that BECAME effective un-skips itself (e.g. standby purge once the cache fills).
/// </summary>
public sealed class StepEffectivenessTrackerTests
{
    private const long MB = 1024 * 1024;

    [Fact]
    public void UnknownStep_IsNeverSkipped()
    {
        Assert.False(new StepEffectivenessTracker().ShouldSkip("purge"));
    }

    [Fact]
    public void TwoIneffectiveSamples_AreNotEnoughEvidenceToSkip()
    {
        var tracker = new StepEffectivenessTracker();
        tracker.Record("purge", 0);
        tracker.Record("purge", 0);

        Assert.False(tracker.ShouldSkip("purge"));
    }

    [Fact]
    public void ThreeIneffectiveSamples_LearnTheSkip()
    {
        var tracker = new StepEffectivenessTracker();
        tracker.Record("purge", 0);
        tracker.Record("purge", 0);
        tracker.Record("purge", 0);

        Assert.True(tracker.ShouldSkip("purge"));
    }

    [Fact]
    public void EffectiveStep_IsNeverSkipped()
    {
        var tracker = new StepEffectivenessTracker();
        tracker.Record("trim", 50 * MB);
        tracker.Record("trim", 50 * MB);
        tracker.Record("trim", 50 * MB);

        Assert.False(tracker.ShouldSkip("trim"));
    }

    [Fact]
    public void EveryFifthSkip_IsAReprobe_SoEvidenceStaysFresh()
    {
        var tracker = new StepEffectivenessTracker();
        tracker.Record("purge", 0);
        tracker.Record("purge", 0);
        tracker.Record("purge", 0);

        Assert.True(tracker.ShouldSkip("purge"));    // skips 1..4
        Assert.True(tracker.ShouldSkip("purge"));
        Assert.True(tracker.ShouldSkip("purge"));
        Assert.True(tracker.ShouldSkip("purge"));
        Assert.False(tracker.ShouldSkip("purge"));   // 5th: run it again and re-measure
        Assert.True(tracker.ShouldSkip("purge"));    // cycle restarts
    }

    [Fact]
    public void StepThatBecameEffective_UnskipsItselfAfterAProbe()
    {
        // Standby purge frees nothing while the cache is empty, then plenty once it fills:
        // the probe's fresh sample must lift the EWMA back over the floor immediately.
        var tracker = new StepEffectivenessTracker();
        tracker.Record("purge", 0);
        tracker.Record("purge", 0);
        tracker.Record("purge", 0);
        Assert.True(tracker.ShouldSkip("purge"));

        tracker.Record("purge", 100 * MB);           // the re-probe found real yield

        Assert.False(tracker.ShouldSkip("purge"));
    }

    [Fact]
    public void NegativeReading_CountsAsIneffective_NotAsNegativeYield()
    {
        // available-after minus available-before goes negative whenever another process
        // allocates during the step; the clamp must treat that as "freed nothing", never as a
        // negative number poisoning the EWMA.
        var tracker = new StepEffectivenessTracker();
        tracker.Record("purge", -50 * MB);
        tracker.Record("purge", -50 * MB);
        tracker.Record("purge", -50 * MB);

        Assert.True(tracker.ShouldSkip("purge"));
    }

    [Fact]
    public void Reset_ClearsAllLearning()
    {
        var tracker = new StepEffectivenessTracker();
        tracker.Record("purge", 0);
        tracker.Record("purge", 0);
        tracker.Record("purge", 0);
        Assert.True(tracker.ShouldSkip("purge"));

        tracker.Reset();

        Assert.False(tracker.ShouldSkip("purge"));
    }
}
