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
    private static readonly DateTime T0 = new(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);

    private DateTime _now = T0;
    private readonly Dictionary<int, double> _cpuSeconds = [];

    private static NativeProcessInfo Proc(int pid, string name) =>
        new() { ProcessId = pid, ProcessName = name };

    private (AdaptiveEcoQosController controller, EcoQosDomain domain, Mock<INativeBridge> native)
        Build(EffectivePowerMode mode, Settings settings)
    {
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetPowerSource()).Returns(PowerSource.Battery);
        native.Setup(n => n.GetForegroundProcessId()).Returns(1000);
        native.Setup(n => n.GetProcessList()).Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp") });
        native.Setup(n => n.SetEcoQos(It.IsAny<bool>(), It.IsAny<int>())).Returns(true);
        native.Setup(n => n.GetProcessCpuTime(It.IsAny<int>()))
            .Returns<int>(pid => _cpuSeconds.TryGetValue(pid, out var s) ? TimeSpan.FromSeconds(s) : null);
        native.Setup(n => n.GetAudibleProcessIds()).Returns([]);
        _cpuSeconds.TryAdd(1000, 0);
        _cpuSeconds.TryAdd(1001, 0);

        var provider = new Mock<IEffectivePowerModeProvider>();
        provider.Setup(p => p.Current).Returns(mode);

        var domain = new EcoQosDomain(settings, native.Object, () => _now);
        var controller = new AdaptiveEcoQosController(domain, native.Object, settings, provider.Object);
        return (controller, domain, native);
    }

    private static void Activate(EcoQosDomain domain) => domain.Apply(domain.CaptureBaseline());

    /// <summary>Make 1001 a throttled drainer via direct reconciles (bypassing the stand-down).</summary>
    private void WarmDrainer(EcoQosDomain domain)
    {
        _cpuSeconds[1001] += 0.5;          // 5% of a core over the next 10s
        _now = _now.AddSeconds(10);
        domain.Reconcile();
    }

    [Fact]
    public void MaintainOnce_AfterLeavingHighPerformanceMode_ReThrottlesOnTheNextTick()
    {
        // The hole this pins: a stand-down used to disarm the domain entirely, so one Game Mode
        // session on battery disabled EcoQoS until the next AC->DC replug. A stand-down is a
        // SUSPENSION: once the user leaves the high-performance mode, the next tick must sweep
        // and re-throttle the still-burning drainer.
        var settings = new Settings { EcoQosEnabled = true, AutomationPaused = false };
        var mode = EffectivePowerMode.Balanced;

        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetPowerSource()).Returns(PowerSource.Battery);
        native.Setup(n => n.GetForegroundProcessId()).Returns(1000);
        native.Setup(n => n.GetProcessList()).Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp") });
        native.Setup(n => n.SetEcoQos(It.IsAny<bool>(), It.IsAny<int>())).Returns(true);
        native.Setup(n => n.GetProcessCpuTime(It.IsAny<int>()))
            .Returns<int>(pid => _cpuSeconds.TryGetValue(pid, out var s) ? TimeSpan.FromSeconds(s) : null);
        native.Setup(n => n.GetAudibleProcessIds()).Returns([]);
        _cpuSeconds.TryAdd(1000, 0);
        _cpuSeconds.TryAdd(1001, 0);

        var provider = new Mock<IEffectivePowerModeProvider>();
        provider.Setup(p => p.Current).Returns(() => mode);

        var domain = new EcoQosDomain(settings, native.Object, () => _now);
        var controller = new AdaptiveEcoQosController(domain, native.Object, settings, provider.Object);

        Activate(domain);
        WarmDrainer(domain);                       // 1001 throttled as a drainer
        mode = EffectivePowerMode.GameMode;
        controller.MaintainOnce();                 // stand-down: 1001 released
        mode = EffectivePowerMode.Balanced;        // user left game mode
        _cpuSeconds[1001] += 0.5;                  // still burning
        _now = _now.AddSeconds(10);

        var outcome = controller.MaintainOnce();

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Exactly(2));   // warm-up + re-throttle
        Assert.Equal(EcoQosCadencePolicy.Outcome.DidWork, outcome);
        controller.Dispose();
    }

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
        Activate(domain);
        WarmDrainer(domain);              // 1001 classified and throttled
        native.Invocations.Clear();

        var outcome = controller.MaintainOnce();

        // Reversible stand-down: throttling already applied is released (back to OS-managed).
        // Releasing is real work, so the cadence outcome must be DidWork (stays fast while the
        // user is in a high-performance mode with throttling still to unwind). The domain stays
        // ENGAGED — a stand-down is a suspension, not a disarm, so the controller keeps
        // maintaining and can re-throttle once the user leaves the high-performance mode.
        native.Verify(n => n.SetEcoQos(false, 1001), Times.Once);
        Assert.True(domain.IsActive);
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
