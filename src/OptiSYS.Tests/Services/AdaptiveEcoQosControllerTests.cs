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
}
