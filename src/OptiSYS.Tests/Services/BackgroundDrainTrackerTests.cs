using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

/// <summary>
/// The tracker classifies processes as drainers from observed CPU-time deltas — % of one core
/// sustained over its sliding window — with enter/exit hysteresis (enter >= 3%, exit < 1%,
/// grounded by the Lab drain probe: real burners sit well above 3% while the shell noise floor
/// stays under 2%). Pure and clock-free: timestamps come in with the samples, so irregular
/// sweep cadences (6s to 60s apart) are handled by time-normalized math, not assumptions.
/// </summary>
public sealed class BackgroundDrainTrackerTests
{
    private static readonly DateTime T0 = new(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);

    private static ProcessCpuSample Cpu(int pid, double cpuSeconds) =>
        new(pid, TimeSpan.FromSeconds(cpuSeconds));

    [Fact]
    public void SingleObservation_NeverMarksADrainer()
    {
        var tracker = new BackgroundDrainTracker();

        tracker.Observe([Cpu(100, cpuSeconds: 50)], T0);

        Assert.Empty(tracker.CurrentDrainers);
    }

    [Fact]
    public void SustainedBurnAboveEnter_BecomesADrainer()
    {
        var tracker = new BackgroundDrainTracker();

        tracker.Observe([Cpu(100, 50.0)], T0);
        tracker.Observe([Cpu(100, 50.5)], T0.AddSeconds(10));   // 0.5s over 10s = 5% of a core

        Assert.Contains(100, tracker.CurrentDrainers);
    }

    [Fact]
    public void QuietProcess_IsNotADrainer()
    {
        var tracker = new BackgroundDrainTracker();

        tracker.Observe([Cpu(100, 50.0)], T0);
        tracker.Observe([Cpu(100, 50.05)], T0.AddSeconds(10));  // 0.5% of a core

        Assert.Empty(tracker.CurrentDrainers);
    }

    [Fact]
    public void GrayZoneBurn_ThatNeverCrossedEnter_StaysOut()
    {
        var tracker = new BackgroundDrainTracker();

        tracker.Observe([Cpu(100, 50.0)], T0);
        tracker.Observe([Cpu(100, 50.2)], T0.AddSeconds(10));   // 2%: above exit, below enter

        Assert.Empty(tracker.CurrentDrainers);
    }

    [Fact]
    public void Hysteresis_KeepsADrainerThrottled_WhileInTheGrayZone()
    {
        var tracker = new BackgroundDrainTracker();

        tracker.Observe([Cpu(100, 50.0)], T0);
        tracker.Observe([Cpu(100, 50.5)], T0.AddSeconds(10));   // 5% -> enters
        tracker.Observe([Cpu(100, 50.7)], T0.AddSeconds(20));   // window avg 3.5% -> still in
        tracker.Observe([Cpu(100, 50.8)], T0.AddSeconds(30));   // avg 2.7% (gray zone) -> stays in

        Assert.Contains(100, tracker.CurrentDrainers);
    }

    [Fact]
    public void Hysteresis_ReleasesADrainer_OnceBurnFallsBelowExit()
    {
        // Window of 5: after enough quiet observations the early burn slides out and the
        // window average drops below the 1% exit line.
        var tracker = new BackgroundDrainTracker();

        tracker.Observe([Cpu(100, 50.0)], T0);
        tracker.Observe([Cpu(100, 50.5)], T0.AddSeconds(10));   // 5% -> enters
        tracker.Observe([Cpu(100, 50.5)], T0.AddSeconds(20));   // idle from here on
        tracker.Observe([Cpu(100, 50.5)], T0.AddSeconds(30));
        tracker.Observe([Cpu(100, 50.5)], T0.AddSeconds(40));
        tracker.Observe([Cpu(100, 50.5)], T0.AddSeconds(50));   // window avg 0% -> exits

        Assert.Empty(tracker.CurrentDrainers);
    }

    [Fact]
    public void Burst_EntersWhileHot_AndExitsOnceTheWindowCools()
    {
        // A genuinely hot burst SHOULD be throttled while it burns; releasing it is the window's
        // job. Burn is averaged across the whole retained window (5 observations), so the burst
        // decays out and the process is released once the average falls below the exit line.
        var tracker = new BackgroundDrainTracker();

        tracker.Observe([Cpu(100, 50.0)], T0);
        tracker.Observe([Cpu(100, 51.0)], T0.AddSeconds(10));   // 10% burst -> enters
        Assert.Contains(100, tracker.CurrentDrainers);

        tracker.Observe([Cpu(100, 51.0)], T0.AddSeconds(20));   // idle from here on
        tracker.Observe([Cpu(100, 51.0)], T0.AddSeconds(30));
        tracker.Observe([Cpu(100, 51.0)], T0.AddSeconds(40));
        tracker.Observe([Cpu(100, 51.0)], T0.AddSeconds(50));
        tracker.Observe([Cpu(100, 51.0)], T0.AddSeconds(60));   // burst slid out of the window

        Assert.Empty(tracker.CurrentDrainers);
    }

    [Fact]
    public void ProcessAbsentFromASweep_IsForgotten()
    {
        var tracker = new BackgroundDrainTracker();

        tracker.Observe([Cpu(100, 50.0)], T0);
        tracker.Observe([Cpu(100, 50.5)], T0.AddSeconds(10));   // drainer
        tracker.Observe([Cpu(200, 1.0)], T0.AddSeconds(20));    // pid 100 gone

        Assert.Empty(tracker.CurrentDrainers);
    }

    [Fact]
    public void CpuTimeGoingBackwards_MeansPidReuse_AndResetsHistory()
    {
        var tracker = new BackgroundDrainTracker();

        tracker.Observe([Cpu(100, 50.0)], T0);
        tracker.Observe([Cpu(100, 50.5)], T0.AddSeconds(10));   // drainer
        tracker.Observe([Cpu(100, 0.1)], T0.AddSeconds(20));    // fresh process under a reused pid

        Assert.Empty(tracker.CurrentDrainers);
    }

    [Fact]
    public void IndependentProcesses_AreClassifiedIndependently()
    {
        var tracker = new BackgroundDrainTracker();

        tracker.Observe([Cpu(100, 50.0), Cpu(200, 10.0)], T0);
        tracker.Observe([Cpu(100, 50.5), Cpu(200, 10.01)], T0.AddSeconds(10));

        Assert.Contains(100, tracker.CurrentDrainers);
        Assert.DoesNotContain(200, tracker.CurrentDrainers);
    }
}
