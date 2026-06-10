using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

/// <summary>
/// The C-state guardian's classifier: a process's wakeup pressure is its context-switch rate,
/// window-averaged over the retained observations, with enter/exit hysteresis (300/s in,
/// 100/s out — grounded by the Lab wakeup probe: real storm-makers run in the hundreds-to-
/// thousands per second while the shell tail stays under ~150/s). The insight it encodes:
/// battery life is package C-state residency, and a low-CPU process waking hundreds of times a
/// second hurts more than a clean burst. Pure and clock-free, same shape as the drain tracker.
/// </summary>
public sealed class WakeupPressureTrackerTests
{
    private static readonly DateTime T0 = new(2026, 6, 11, 12, 0, 0, DateTimeKind.Utc);

    private static ProcessWakeupSample Switches(int pid, long total) => new(pid, total);

    [Fact]
    public void SingleObservation_IsNeverAStormer()
    {
        var tracker = new WakeupPressureTracker();

        tracker.Observe([Switches(100, 50_000)], T0);

        Assert.Empty(tracker.CurrentStormers);
    }

    [Fact]
    public void SustainedStorm_AboveEnterRate_IsClassified()
    {
        var tracker = new WakeupPressureTracker();

        tracker.Observe([Switches(100, 50_000)], T0);
        tracker.Observe([Switches(100, 54_000)], T0.AddSeconds(10));   // 400/s >= 300/s

        Assert.Contains(100, tracker.CurrentStormers);
    }

    [Fact]
    public void ShellTailRates_StayOut()
    {
        var tracker = new WakeupPressureTracker();

        tracker.Observe([Switches(100, 50_000)], T0);
        tracker.Observe([Switches(100, 51_500)], T0.AddSeconds(10));   // 150/s: the noise floor

        Assert.Empty(tracker.CurrentStormers);
    }

    [Fact]
    public void Hysteresis_KeepsAStormerIn_WhileBetweenExitAndEnter()
    {
        var tracker = new WakeupPressureTracker();

        tracker.Observe([Switches(100, 0)], T0);
        tracker.Observe([Switches(100, 4_000)], T0.AddSeconds(10));    // 400/s -> in
        tracker.Observe([Switches(100, 6_000)], T0.AddSeconds(20));    // window avg 300/s
        tracker.Observe([Switches(100, 7_500)], T0.AddSeconds(30));    // avg 250/s (gray) -> stays

        Assert.Contains(100, tracker.CurrentStormers);
    }

    [Fact]
    public void CalmedStormer_BelowExitRate_IsReleased()
    {
        var tracker = new WakeupPressureTracker();

        tracker.Observe([Switches(100, 0)], T0);
        tracker.Observe([Switches(100, 4_000)], T0.AddSeconds(10));    // in
        tracker.Observe([Switches(100, 4_100)], T0.AddSeconds(20));    // quieting
        tracker.Observe([Switches(100, 4_200)], T0.AddSeconds(30));
        tracker.Observe([Switches(100, 4_300)], T0.AddSeconds(40));
        tracker.Observe([Switches(100, 4_400)], T0.AddSeconds(50));    // window avg 10/s -> out

        Assert.Empty(tracker.CurrentStormers);
    }

    [Fact]
    public void CounterGoingBackwards_MeansPidReuse_AndResetsHistory()
    {
        var tracker = new WakeupPressureTracker();

        tracker.Observe([Switches(100, 50_000)], T0);
        tracker.Observe([Switches(100, 54_000)], T0.AddSeconds(10));   // stormer
        tracker.Observe([Switches(100, 200)], T0.AddSeconds(20));      // fresh process, reused pid

        Assert.Empty(tracker.CurrentStormers);
    }

    [Fact]
    public void ProcessAbsentFromASweep_IsForgotten()
    {
        var tracker = new WakeupPressureTracker();

        tracker.Observe([Switches(100, 0)], T0);
        tracker.Observe([Switches(100, 4_000)], T0.AddSeconds(10));    // stormer
        tracker.Observe([Switches(200, 10)], T0.AddSeconds(20));       // pid 100 gone

        Assert.Empty(tracker.CurrentStormers);
    }
}
