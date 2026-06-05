using OptiSYS.Core.Native;
using Xunit;

namespace OptiSYS.Tests.Native;

/// <summary>
/// Pure-mask coverage for the process power-throttling state builder. Applying EcoQoS / timer-ignore
/// must set both Control and State masks; reverting must RESET to OS-managed (Control=0, State=0)
/// rather than pinning the process to high-QoS (Control=flag, State=0).
/// </summary>
public class PowerThrottlingStateTests
{
    [Fact]
    public void EcoQosApply_SetsExecutionSpeedControlAndState()
    {
        var state = NativeMethods.BuildEcoQoSState(enable: true);

        Assert.Equal(NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED, state.ControlMask);
        Assert.Equal(NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED, state.StateMask);
    }

    [Fact]
    public void EcoQosRevert_ResetsToOsManaged_ZeroControlZeroState()
    {
        var state = NativeMethods.BuildEcoQoSState(enable: false);

        Assert.Equal(0u, state.ControlMask);   // OS-managed, not pinned high
        Assert.Equal(0u, state.StateMask);
    }

    [Fact]
    public void TimerIgnoreApply_SetsIgnoreTimerControlAndState()
    {
        var state = NativeMethods.BuildTimerResolutionState(ignore: true);

        Assert.Equal(NativeMethods.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION, state.ControlMask);
        Assert.Equal(NativeMethods.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION, state.StateMask);
    }

    [Fact]
    public void TimerIgnoreRevert_ResetsToOsManaged_ZeroControlZeroState()
    {
        var state = NativeMethods.BuildTimerResolutionState(ignore: false);

        Assert.Equal(0u, state.ControlMask);   // OS-managed, not pinned
        Assert.Equal(0u, state.StateMask);
    }

    // ── Readback decode (item #25) ───────────────────────────────────
    // The readback decodes a queried PROCESS_POWER_THROTTLING_STATE: EcoQoS is verified ON only
    // when the EXECUTION_SPEED bit is set in BOTH the control and state masks (Windows reports an
    // explicitly-throttled process this way). OS-managed (Control=0,State=0) and pinned-high
    // (Control=flag,State=0) are NOT EcoQoS.

    [Fact]
    public void IsEcoQoSThrottled_True_WhenExecutionSpeedSetInBothMasks()
    {
        var state = NativeMethods.BuildEcoQoSState(enable: true);

        Assert.True(NativeMethods.IsEcoQoSThrottled(state));
    }

    [Fact]
    public void IsEcoQoSThrottled_False_WhenOsManaged()
    {
        var state = NativeMethods.BuildEcoQoSState(enable: false);

        Assert.False(NativeMethods.IsEcoQoSThrottled(state));
    }

    [Fact]
    public void IsEcoQoSThrottled_False_WhenPinnedHigh_ControlSetStateClear()
    {
        var pinnedHigh = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
        {
            Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = 0
        };

        Assert.False(NativeMethods.IsEcoQoSThrottled(pinnedHigh));
    }
}
