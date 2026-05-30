using System.Reflection;
using Moq;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class PowerSourceMonitorTests
{
    [Fact]
    public void Start_DoesNotReplaceExistingTimer()
    {
        var native = new Mock<INativeBridge>();
        native.SetupSequence(n => n.GetPowerSource())
            .Returns(PowerSource.Ac)
            .Returns(PowerSource.Ac);

        using var monitor = new PowerSourceMonitor(
            native.Object,
            new Settings(),
            Mock.Of<IOptimizationEngine>());

        monitor.Start();
        var firstTimer = GetTimer(monitor);
        monitor.Start();
        var secondTimer = GetTimer(monitor);

        Assert.NotNull(firstTimer);
        Assert.Same(firstTimer, secondTimer);
    }

    [Fact]
    public void PollCallback_OnAcToBattery_ActivatesSafeBatteryCategory()
    {
        var native = new Mock<INativeBridge>();
        native.SetupSequence(n => n.GetPowerSource())
            .Returns(PowerSource.Ac)
            .Returns(PowerSource.Battery);

        var engine = new Mock<IOptimizationEngine>();
        engine.Setup(e => e.ActivateCategory("Battery"))
            .Returns(new EngineResult { Success = true });

        using var monitor = new PowerSourceMonitor(
            native.Object,
            new Settings { AutoOptimizeOnBattery = true },
            engine.Object);

        InvokePollCallback(monitor);

        engine.Verify(e => e.ActivateCategory("Battery"), Times.Once);
        engine.Verify(e => e.RevertAll(), Times.Never);
    }

    [Fact]
    public void PollCallback_OnBatteryToAc_RevertsOnlySafeRuntimeBatteryDomains()
    {
        var native = new Mock<INativeBridge>();
        native.SetupSequence(n => n.GetPowerSource())
            .Returns(PowerSource.Battery)
            .Returns(PowerSource.Ac);

        var engine = new Mock<IOptimizationEngine>();
        engine.Setup(e => e.RevertDomain(It.IsAny<string>()))
            .Returns(new EngineResult { Success = true });

        using var monitor = new PowerSourceMonitor(
            native.Object,
            new Settings { AutoOptimizeOnBattery = true },
            engine.Object);

        InvokePollCallback(monitor);

        engine.Verify(e => e.RevertDomain("timer-resolution"), Times.Once);
        engine.Verify(e => e.RevertDomain("ecoqos"), Times.Once);
        engine.Verify(e => e.RevertDomain("cpu-parking"), Times.Once);   // persistent plan write -> restore on AC
        engine.Verify(e => e.RevertAll(), Times.Never);
        engine.Verify(e => e.ActivateCategory(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void PollCallback_WhenAutomationPaused_DoesNotMutateBatteryDomains()
    {
        var native = new Mock<INativeBridge>();
        native.SetupSequence(n => n.GetPowerSource())
            .Returns(PowerSource.Ac)
            .Returns(PowerSource.Battery);

        var engine = new Mock<IOptimizationEngine>();

        using var monitor = new PowerSourceMonitor(
            native.Object,
            new Settings { AutoOptimizeOnBattery = true, AutomationPaused = true },
            engine.Object);

        InvokePollCallback(monitor);

        engine.Verify(e => e.ActivateCategory(It.IsAny<string>()), Times.Never);
        engine.Verify(e => e.RevertAll(), Times.Never);
    }

    private static object? GetTimer(PowerSourceMonitor monitor) =>
        typeof(PowerSourceMonitor)
            .GetField("_pollTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(monitor);

    private static void InvokePollCallback(PowerSourceMonitor monitor)
    {
        typeof(PowerSourceMonitor)
            .GetMethod("PollCallback", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(monitor, [null]);
    }
}
