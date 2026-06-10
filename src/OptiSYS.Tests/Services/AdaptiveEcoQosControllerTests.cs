using Moq;
using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

/// <summary>
/// The adaptive controller only MAINTAINS an already-active EcoQoS domain while on battery —
/// it never initiates or reverts (those stay the engine's job on power-source transitions).
/// A reconcile is observable as a call to INativeBridge.GetProcessList (the domain enumerates
/// through the same shared bridge the controller reads power source from).
/// </summary>
public class AdaptiveEcoQosControllerTests
{
    private static NativeProcessInfo Proc(int pid, string name) =>
        new() { ProcessId = pid, ProcessName = name };

    private static (AdaptiveEcoQosController controller, EcoQosDomain domain, Mock<INativeBridge> native) Build(
        PowerSource power, Settings settings)
    {
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetPowerSource()).Returns(power);
        native.Setup(n => n.GetForegroundProcessId()).Returns(1000);
        native.Setup(n => n.GetProcessList()).Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp") });
        native.Setup(n => n.SetEcoQos(It.IsAny<bool>(), It.IsAny<int>())).Returns(true);
        var domain = new EcoQosDomain(settings, native.Object);
        var controller = new AdaptiveEcoQosController(domain, native.Object, settings);
        return (controller, domain, native);
    }

    private static void Activate(EcoQosDomain domain) => domain.Apply(domain.CaptureBaseline());

    [Fact]
    public void MaintainOnce_OnBattery_WhenEnabledActiveAndNotPaused_Reconciles()
    {
        var settings = new Settings { EcoQosEnabled = true, AutomationPaused = false };
        var (controller, domain, native) = Build(PowerSource.Battery, settings);
        Activate(domain);
        native.Invocations.Clear();

        controller.MaintainOnce();

        native.Verify(n => n.GetProcessList(), Times.Once);
        controller.Dispose();
    }

    [Fact]
    public void MaintainOnce_OnAc_DoesNotReconcile()
    {
        var settings = new Settings { EcoQosEnabled = true };
        var (controller, domain, native) = Build(PowerSource.Ac, settings);
        Activate(domain);
        native.Invocations.Clear();

        controller.MaintainOnce();

        native.Verify(n => n.GetProcessList(), Times.Never);
        controller.Dispose();
    }

    [Fact]
    public void MaintainOnce_WhenPaused_DoesNotReconcile()
    {
        var settings = new Settings { EcoQosEnabled = true, AutomationPaused = true };
        var (controller, domain, native) = Build(PowerSource.Battery, settings);
        Activate(domain);
        native.Invocations.Clear();

        controller.MaintainOnce();

        native.Verify(n => n.GetProcessList(), Times.Never);
        controller.Dispose();
    }

    [Fact]
    public void MaintainOnce_WhenEcoQosDisabled_DoesNotReconcile()
    {
        var settings = new Settings { EcoQosEnabled = false };
        var (controller, domain, native) = Build(PowerSource.Battery, settings);
        Activate(domain);   // domain itself doesn't gate on the setting; the controller does
        native.Invocations.Clear();

        controller.MaintainOnce();

        native.Verify(n => n.GetProcessList(), Times.Never);
        controller.Dispose();
    }

    [Fact]
    public void MaintainOnce_WhenInactive_DoesNotReconcile()
    {
        var settings = new Settings { EcoQosEnabled = true };
        var (controller, domain, native) = Build(PowerSource.Battery, settings);
        // domain never activated -> IsActive is false, so there is nothing to maintain
        native.Invocations.Clear();

        controller.MaintainOnce();

        native.Verify(n => n.GetProcessList(), Times.Never);
        controller.Dispose();
    }

    // ── Cadence outcomes: what each tick reports drives the adaptive back-off ──

    [Fact]
    public void MaintainOnce_SteadyStateReconcile_ReportsNoOp()
    {
        // Activate already throttled the one background process; a sweep over the identical
        // process list changes nothing -> NoOp, the only outcome that backs the cadence off.
        var settings = new Settings { EcoQosEnabled = true, AutomationPaused = false };
        var (controller, domain, _) = Build(PowerSource.Battery, settings);
        Activate(domain);

        Assert.Equal(EcoQosCadencePolicy.Outcome.NoOp, controller.MaintainOnce());
        controller.Dispose();
    }

    [Fact]
    public void MaintainOnce_NewBackgroundProcess_ReportsDidWork()
    {
        var settings = new Settings { EcoQosEnabled = true, AutomationPaused = false };
        var (controller, domain, native) = Build(PowerSource.Battery, settings);
        Activate(domain);

        // A newly-spawned background process appears -> the sweep throttles it -> real work.
        native.Setup(n => n.GetProcessList())
            .Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp"), Proc(1002, "newbg") });

        Assert.Equal(EcoQosCadencePolicy.Outcome.DidWork, controller.MaintainOnce());
        controller.Dispose();
    }

    [Fact]
    public void MaintainOnce_WhenIneligible_ReportsSkipped()
    {
        // On AC the tick bails before the sweep — cheap check, so the cadence must stay fast.
        var settings = new Settings { EcoQosEnabled = true };
        var (controller, domain, _) = Build(PowerSource.Ac, settings);
        Activate(domain);

        Assert.Equal(EcoQosCadencePolicy.Outcome.Skipped, controller.MaintainOnce());
        controller.Dispose();
    }

    [Fact]
    public void MaintainOnce_AfterANoOpSweep_DefersTheNextSweep_WithoutEnumerating()
    {
        // Steady state: a no-op sweep stretches the gap, so the very next tick must stay cheap —
        // no process enumeration at all, just the eligibility + foreground reads.
        var settings = new Settings { EcoQosEnabled = true, AutomationPaused = false };
        var (controller, domain, native) = Build(PowerSource.Battery, settings);
        Activate(domain);

        controller.MaintainOnce();              // sweep (no-op) -> gap stretches
        native.Invocations.Clear();

        var outcome = controller.MaintainOnce();

        Assert.Equal(EcoQosCadencePolicy.Outcome.SweepDeferred, outcome);
        native.Verify(n => n.GetProcessList(), Times.Never);
        controller.Dispose();
    }

    [Fact]
    public void MaintainOnce_AStretchedGap_StillSweepsOnItsOwnWhenTheGapElapses()
    {
        // No change signal, no work: after a no-op sweep (gap 2) and one deferred tick, the
        // gap-elapsing tick must run the enumeration again by itself.
        var settings = new Settings { EcoQosEnabled = true, AutomationPaused = false };
        var (controller, domain, native) = Build(PowerSource.Battery, settings);
        Activate(domain);

        controller.MaintainOnce();              // sweep (no-op) -> gap 2
        controller.MaintainOnce();              // deferred
        native.Invocations.Clear();

        var outcome = controller.MaintainOnce();

        Assert.Equal(EcoQosCadencePolicy.Outcome.NoOp, outcome);
        native.Verify(n => n.GetProcessList(), Times.Once);
        controller.Dispose();
    }

    [Fact]
    public void MaintainOnce_ForegroundChange_ForcesAnImmediateSweep()
    {
        // Focus moved while the sweep gap was stretched: the change signal must force the sweep
        // on THIS tick (release the new foreground, throttle the old one), not wait out the gap.
        var settings = new Settings { EcoQosEnabled = true, AutomationPaused = false };
        var (controller, domain, native) = Build(PowerSource.Battery, settings);
        Activate(domain);

        controller.MaintainOnce();              // sweep (no-op) -> gap stretches
        native.Setup(n => n.GetForegroundProcessId()).Returns(1001);   // focus -> bgapp

        var outcome = controller.MaintainOnce();

        Assert.Equal(EcoQosCadencePolicy.Outcome.DidWork, outcome);    // 1001 released, 1000 throttled
        native.Verify(n => n.SetEcoQos(false, 1001), Times.Once);
        controller.Dispose();
    }
}
