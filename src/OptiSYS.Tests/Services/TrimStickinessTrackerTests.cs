using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

/// <summary>
/// Learns, per process, whether working-set trims actually stick: a trim whose working set is
/// back near its pre-trim size by the next pass was pure re-fault churn. Two consecutive
/// bounces classify the process a chronic re-faulter (demoted in the trim order, like hot
/// processes — still trimmed under real pressure, which keeps the evidence fresh); one trim
/// that sticks clears the strikes. Pure and clock-free.
/// </summary>
public sealed class TrimStickinessTrackerTests
{
    private const long MB = 1024 * 1024;

    [Fact]
    public void UnknownPid_IsNotChronic()
    {
        Assert.False(new TrimStickinessTracker().IsChronicRefaulter(42));
    }

    [Fact]
    public void OneBounce_IsNotYetChronic()
    {
        var tracker = new TrimStickinessTracker();
        tracker.RecordTrim(42, preTrimWorkingSetBytes: 100 * MB);
        tracker.Observe(42, workingSetBytes: 95 * MB);    // 95% back -> bounce #1

        Assert.False(tracker.IsChronicRefaulter(42));
    }

    [Fact]
    public void TwoConsecutiveBounces_AreChronic()
    {
        var tracker = new TrimStickinessTracker();
        tracker.RecordTrim(42, 100 * MB);
        tracker.Observe(42, 95 * MB);                     // bounce #1
        tracker.RecordTrim(42, 95 * MB);
        tracker.Observe(42, 90 * MB);                     // bounce #2 (94% of pre-trim)

        Assert.True(tracker.IsChronicRefaulter(42));
    }

    [Fact]
    public void ATrimThatSticks_ClearsTheStrikes()
    {
        var tracker = new TrimStickinessTracker();
        tracker.RecordTrim(42, 100 * MB);
        tracker.Observe(42, 95 * MB);                     // bounce #1
        tracker.RecordTrim(42, 95 * MB);
        tracker.Observe(42, 30 * MB);                     // stuck: the pages stayed out

        tracker.RecordTrim(42, 30 * MB);
        tracker.Observe(42, 29 * MB);                     // bounce again, but strikes restarted

        Assert.False(tracker.IsChronicRefaulter(42));
    }

    [Fact]
    public void ObserveWithoutAPendingTrim_RecordsNoVerdict()
    {
        var tracker = new TrimStickinessTracker();
        tracker.Observe(42, 100 * MB);
        tracker.Observe(42, 100 * MB);

        Assert.False(tracker.IsChronicRefaulter(42));
    }

    [Fact]
    public void RetainOnly_ForgetsExitedProcesses()
    {
        var tracker = new TrimStickinessTracker();
        tracker.RecordTrim(42, 100 * MB);
        tracker.Observe(42, 95 * MB);
        tracker.RecordTrim(42, 95 * MB);
        tracker.Observe(42, 90 * MB);
        Assert.True(tracker.IsChronicRefaulter(42));

        tracker.RetainOnly([7]);                          // 42 exited

        Assert.False(tracker.IsChronicRefaulter(42));
    }
}
