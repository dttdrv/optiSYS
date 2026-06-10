using Moq;
using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

/// <summary>
/// "Follow, never fight": when the user has picked a high-performance effective power mode
/// (HighPerformance / MaxPerformance / GameMode), the adaptive controller must STAND DOWN —
/// it stops re-throttling background processes and releases any throttling it had applied,
/// deferring to the user's explicit choice. In every other mode (Balanced, BetterBattery,
/// BatterySaver, Unknown) it behaves exactly as before. The native registration is isolated
/// behind <see cref="IEffectivePowerModeProvider"/> so the decision is fully unit-tested.
/// </summary>
public class AdaptiveEcoQosFollowNeverFightTests
{
    private static NativeProcessInfo Proc(int pid, string name) =>
        new() { ProcessId = pid, ProcessName = name };

    private static (AdaptiveEcoQosController controller, EcoQosDomain domain, Mock<INativeBridge> native)
        Build(EffectivePowerMode mode, Settings settings)
    {
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetPowerSource()).Returns(PowerSource.Battery);
        native.Setup(n => n.GetForegroundProcessId()).Returns(1000);
        native.Setup(n => n.GetProcessList()).Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp") });
        native.Setup(n => n.SetEcoQos(It.IsAny<bool>(), It.IsAny<int>())).Returns(true);

        var provider = new Mock<IEffectivePowerModeProvider>();
        provider.Setup(p => p.Current).Returns(mode);

        var domain = new EcoQosDomain(settings, native.Object);
        var controller = new AdaptiveEcoQosController(domain, native.Object, settings, provider.Object);
        return (controller, domain, native);
    }

    private static void Activate(EcoQosDomain domain) => domain.Apply(domain.CaptureBaseline());

    [Theory]
    [InlineData(EffectivePowerMode.HighPerformance)]
    [InlineData(EffectivePowerMode.MaxPerformance)]
    [InlineData(EffectivePowerMode.GameMode)]
    public void MaintainOnce_HighPerformanceMode_StandsDown_DoesNotReconcile(EffectivePowerMode mode)
    {
        var settings = new Settings { EcoQosEnabled = true, AutomationPaused = false };
        var (controller, domain, native) = Build(mode, settings);
        Activate(domain);
        native.Invocations.Clear();

        controller.MaintainOnce();

        // Stood down: no fresh reconcile (no re-throttling of background apps the user wants full-speed).
        native.Verify(n => n.GetProcessList(), Times.Never);
        controller.Dispose();
    }

    [Theory]
    [InlineData(EffectivePowerMode.HighPerformance)]
    [InlineData(EffectivePowerMode.MaxPerformance)]
    [InlineData(EffectivePowerMode.GameMode)]
    public void MaintainOnce_HighPerformanceMode_ReleasesPreviouslyThrottledProcesses(EffectivePowerMode mode)
    {
        var settings = new Settings { EcoQosEnabled = true, AutomationPaused = false };
        var (controller, domain, native) = Build(mode, settings);
        Activate(domain);                 // throttles 1001
        native.Invocations.Clear();

        var outcome = controller.MaintainOnce();

        // Reversible stand-down: throttling already applied is released (back to OS-managed).
        // Releasing is real work, so the cadence outcome must be DidWork (stays fast while the
        // user is in a high-performance mode with throttling still to unwind).
        native.Verify(n => n.SetEcoQos(false, 1001), Times.Once);
        Assert.False(domain.IsActive);
        Assert.Equal(EcoQosCadencePolicy.Outcome.DidWork, outcome);
        controller.Dispose();
    }

    [Theory]
    [InlineData(EffectivePowerMode.Balanced)]
    [InlineData(EffectivePowerMode.BetterBattery)]
    [InlineData(EffectivePowerMode.BatterySaver)]
    [InlineData(EffectivePowerMode.Unknown)]
    public void MaintainOnce_NonHighPerformanceMode_Reconciles_AsToday(EffectivePowerMode mode)
    {
        var settings = new Settings { EcoQosEnabled = true, AutomationPaused = false };
        var (controller, domain, native) = Build(mode, settings);
        Activate(domain);
        native.Invocations.Clear();

        controller.MaintainOnce();

        native.Verify(n => n.GetProcessList(), Times.Once);
        controller.Dispose();
    }

    [Fact]
    public void MaintainOnce_NullProvider_BehavesExactlyAsToday()
    {
        // Graceful degradation: no provider supplied (e.g. API unavailable) -> today's behavior.
        var settings = new Settings { EcoQosEnabled = true, AutomationPaused = false };
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetPowerSource()).Returns(PowerSource.Battery);
        native.Setup(n => n.GetForegroundProcessId()).Returns(1000);
        native.Setup(n => n.GetProcessList()).Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp") });
        native.Setup(n => n.SetEcoQos(It.IsAny<bool>(), It.IsAny<int>())).Returns(true);
        var domain = new EcoQosDomain(settings, native.Object);
        var controller = new AdaptiveEcoQosController(domain, native.Object, settings, powerMode: null);
        domain.Apply(domain.CaptureBaseline());
        native.Invocations.Clear();

        controller.MaintainOnce();

        native.Verify(n => n.GetProcessList(), Times.Once);
        controller.Dispose();
    }
}

/// <summary>
/// The pure "follow, never fight" decision rule, isolated from the controller and any native call.
/// </summary>
public class EffectivePowerModeDecisionTests
{
    [Theory]
    [InlineData(EffectivePowerMode.HighPerformance, true)]
    [InlineData(EffectivePowerMode.MaxPerformance, true)]
    [InlineData(EffectivePowerMode.GameMode, true)]
    [InlineData(EffectivePowerMode.Balanced, false)]
    [InlineData(EffectivePowerMode.BetterBattery, false)]
    [InlineData(EffectivePowerMode.BatterySaver, false)]
    [InlineData(EffectivePowerMode.MixedReality, false)]
    [InlineData(EffectivePowerMode.Unknown, false)]
    public void IsHighPerformance_FollowsUserHighPerformanceChoice(EffectivePowerMode mode, bool expected)
    {
        Assert.Equal(expected, EffectivePowerModeDecision.IsHighPerformance(mode));
    }
}
