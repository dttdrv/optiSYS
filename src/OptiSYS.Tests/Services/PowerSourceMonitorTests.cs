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
    public void PollCallback_OnBatteryToAc_RevertsFullBatteryCategory_IncludingPersistentDomains()
    {
        // The AC revert must be SYMMETRIC with the AC->DC apply (ActivateCategory("Battery")):
        // every active battery-category domain is reverted on AC, not a hand-picked subset.
        // Otherwise opted-in persistent registry/power-scheme domains (disk-coalescing,
        // network-power, gpu-power) stay applied for the whole AC session.
        var native = new Mock<INativeBridge>();
        native.SetupSequence(n => n.GetPowerSource())
            .Returns(PowerSource.Battery)
            .Returns(PowerSource.Ac);

        var batteryDomains = new[]
        {
            "ecoqos", "timer-resolution", "cpu-parking",
            "disk-coalescing", "network-power", "gpu-power",
        };
        var domains = batteryDomains
            .Select(id => (IOptimizationDomain)new FakeDomain(id, "Battery", isActive: true))
            .Append(new FakeDomain("memory-optimize", "Memory", isActive: true))   // other category: untouched
            .Append(new FakeDomain("usb-suspend", "Battery", isActive: false))     // inactive: untouched
            .ToList();

        var engine = new Mock<IOptimizationEngine>();
        engine.SetupGet(e => e.Domains).Returns(domains);
        engine.Setup(e => e.RevertDomain(It.IsAny<string>()))
            .Returns(new EngineResult { Success = true });

        using var monitor = new PowerSourceMonitor(
            native.Object,
            new Settings { AutoOptimizeOnBattery = true },
            engine.Object);

        InvokePollCallback(monitor);

        foreach (var id in batteryDomains)
            engine.Verify(e => e.RevertDomain(id), Times.Once);

        engine.Verify(e => e.RevertDomain("memory-optimize"), Times.Never);  // other category
        engine.Verify(e => e.RevertDomain("usb-suspend"), Times.Never);      // inactive
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

    /// <summary>Minimal domain stub so the monitor can enumerate engine.Domains by category/active.</summary>
    private sealed class FakeDomain : IOptimizationDomain
    {
        public FakeDomain(string id, string category, bool isActive)
        {
            Id = id;
            Category = category;
            IsActive = isActive;
        }

        public string Id { get; }
        public string DisplayName => Id;
        public string Category { get; }
        public bool IsSupported => true;
        public bool IsActive { get; }
        public bool IsEnabled(Settings settings) => true;
        public DomainSnapshot CaptureBaseline() => new() { DomainId = Id };
        public ApplyResult Apply(DomainSnapshot baseline) => ApplyResult.Ok(Id);
        public void Revert(DomainSnapshot baseline) { }
        public DomainStatus GetStatus() => new() { DomainId = Id, DisplayName = Id, Category = Category, IsActive = IsActive };
        public void Dispose() { }
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
