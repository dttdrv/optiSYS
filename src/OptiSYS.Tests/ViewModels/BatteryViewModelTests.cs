using Moq;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.ViewModels;
using Xunit;

namespace OptiSYS.Tests.ViewModels;

/// <summary>
/// Behavior contract for <see cref="BatteryViewModel"/>:
/// <list type="bullet">
///   <item>Each of 8 toggle commands routes to the correct domain id.</item>
///   <item>Toggle polarity is state-aware: inactive → Activate, active → Revert.</item>
///   <item>Activate-all uses the engine's category shortcut; Revert-all filters to battery only.</item>
///   <item>Battery snapshot flows from <see cref="IBatteryInfoService"/> via event subscription.</item>
///   <item>Null-arg guards on every ctor dependency.</item>
/// </list>
/// </summary>
public class BatteryViewModelTests
{
    // ── Fixtures ────────────────────────────────────────────────────────────────

    /// <summary>The 8 battery domain IDs — mirrored from the Core plan + Domains/Battery/*.cs.</summary>
    public static IEnumerable<object[]> BatteryDomainIds => new[]
    {
        new object[] { "ecoqos" },
        new object[] { "timer-resolution" },
        new object[] { "background-services" },
        new object[] { "usb-suspend" },
        new object[] { "network-power" },
        new object[] { "gpu-power" },
        new object[] { "cpu-parking" },
        new object[] { "disk-coalescing" },
    };

    private static List<DomainStatus> BuildStatuses(string? activeDomainId = null)
    {
        // Mimic engine.GetAllStatuses() — one DomainStatus per domain id.
        var ids = new[]
        {
            "ecoqos", "timer-resolution", "background-services", "usb-suspend",
            "network-power", "gpu-power", "cpu-parking", "disk-coalescing",
        };
        return ids.Select(id => new DomainStatus
        {
            DomainId    = id,
            DisplayName = id,
            Category    = "Battery",
            IsSupported = true,
            IsActive    = id == activeDomainId,
        }).ToList();
    }

    private static (Mock<IOptimizationEngine> engine,
                    Mock<IBatteryInfoService> battery,
                    Settings                  settings,
                    BatteryViewModel          vm)
        CreateVm(string? initialActive = null)
    {
        var engine   = new Mock<IOptimizationEngine>();
        var battery  = new Mock<IBatteryInfoService>();
        var settings = new Settings();

        engine.Setup(e => e.GetAllStatuses()).Returns(() => BuildStatuses(initialActive));
        engine.Setup(e => e.ActivateDomain(It.IsAny<string>())).Returns(new EngineResult { Success = true });
        engine.Setup(e => e.RevertDomain  (It.IsAny<string>())).Returns(new EngineResult { Success = true });
        engine.Setup(e => e.ActivateCategory(It.IsAny<string>())).Returns(new EngineResult { Success = true });

        var vm = new BatteryViewModel(engine.Object, battery.Object, settings);
        return (engine, battery, settings, vm);
    }

    /// <summary>
    /// Maps a domain id to the <see cref="BatteryViewModel"/> command that toggles it.
    /// Kept in one place so the parametrized tests stay readable.
    /// </summary>
    private static System.Windows.Input.ICommand CommandFor(BatteryViewModel vm, string id) => id switch
    {
        "ecoqos"              => vm.ToggleEcoqosCommand,
        "timer-resolution"    => vm.ToggleTimerResolutionCommand,
        "background-services" => vm.ToggleBackgroundServicesCommand,
        "usb-suspend"         => vm.ToggleUsbSuspendCommand,
        "network-power"       => vm.ToggleNetworkPowerCommand,
        "gpu-power"           => vm.ToggleGpuPowerCommand,
        "cpu-parking"         => vm.ToggleCpuParkingCommand,
        "disk-coalescing"     => vm.ToggleDiskCoalescingCommand,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    // ── Null-arg guards ────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullEngine_Throws()
    {
        var battery = new Mock<IBatteryInfoService>().Object;
        Assert.Throws<ArgumentNullException>(() =>
            new BatteryViewModel(null!, battery, new Settings()));
    }

    [Fact]
    public void Ctor_NullBattery_Throws()
    {
        var engine = new Mock<IOptimizationEngine>().Object;
        Assert.Throws<ArgumentNullException>(() =>
            new BatteryViewModel(engine, null!, new Settings()));
    }

    [Fact]
    public void Ctor_NullSettings_Throws()
    {
        var engine  = new Mock<IOptimizationEngine>().Object;
        var battery = new Mock<IBatteryInfoService>().Object;
        Assert.Throws<ArgumentNullException>(() =>
            new BatteryViewModel(engine, battery, null!));
    }

    // ── Toggle routing: each of 8 commands activates the matching domain when off ─

    [Theory]
    [MemberData(nameof(BatteryDomainIds))]
    public void Toggle_WhenDomainInactive_CallsActivateDomain(string id)
    {
        var (engine, _, _, vm) = CreateVm(initialActive: null);

        CommandFor(vm, id).Execute(null);

        engine.Verify(e => e.ActivateDomain(id), Times.Once);
        engine.Verify(e => e.RevertDomain(It.IsAny<string>()), Times.Never);
    }

    // ── Toggle routing: each of 8 commands reverts the matching domain when on ─

    [Theory]
    [MemberData(nameof(BatteryDomainIds))]
    public void Toggle_WhenDomainActive_CallsRevertDomain(string id)
    {
        var (engine, _, _, vm) = CreateVm(initialActive: id);

        CommandFor(vm, id).Execute(null);

        engine.Verify(e => e.RevertDomain(id), Times.Once);
        engine.Verify(e => e.ActivateDomain(It.IsAny<string>()), Times.Never);
    }

    // ── Activate-all & Revert-all ─────────────────────────────────────────────

    [Fact]
    public void ActivateAll_CallsActivateCategoryBattery()
    {
        var (engine, _, _, vm) = CreateVm();

        vm.ActivateAllCommand.Execute(null);

        engine.Verify(e => e.ActivateCategory(
            It.Is<string>(s => s.Equals("battery", StringComparison.OrdinalIgnoreCase))),
            Times.Once);
    }

    [Fact]
    public void RevertAll_RevertsOnlyBatteryCategoryDomainsThatAreActive()
    {
        // Arrange — half battery/half memory, some active some not, to prove the filter works.
        var engine   = new Mock<IOptimizationEngine>();
        var battery  = new Mock<IBatteryInfoService>().Object;
        var settings = new Settings();

        engine.Setup(e => e.GetAllStatuses()).Returns(new List<DomainStatus>
        {
            new() { DomainId = "ecoqos",            Category = "Battery", IsActive = true  },
            new() { DomainId = "cpu-parking",       Category = "Battery", IsActive = true  },
            new() { DomainId = "timer-resolution",  Category = "Battery", IsActive = false }, // not active → skip
            new() { DomainId = "memory-optimize",   Category = "Memory",  IsActive = true  }, // wrong category → skip
        });
        engine.Setup(e => e.RevertDomain(It.IsAny<string>())).Returns(new EngineResult { Success = true });

        var vm = new BatteryViewModel(engine.Object, battery, settings);

        // Act
        vm.RevertAllCommand.Execute(null);

        // Assert — exactly the two active battery domains got reverted.
        engine.Verify(e => e.RevertDomain("ecoqos"),           Times.Once);
        engine.Verify(e => e.RevertDomain("cpu-parking"),      Times.Once);
        engine.Verify(e => e.RevertDomain("timer-resolution"), Times.Never);
        engine.Verify(e => e.RevertDomain("memory-optimize"),  Times.Never);
    }

    // ── IsXxxActive flags reflect engine state after Refresh ──────────────────

    [Fact]
    public void RefreshCommand_SyncsIsActiveFlagsFromEngine()
    {
        var (engine, _, _, vm) = CreateVm(initialActive: null);
        // Flip the engine's response to mark ecoqos active, then refresh.
        engine.Setup(e => e.GetAllStatuses()).Returns(BuildStatuses(activeDomainId: "ecoqos"));

        vm.RefreshCommand.Execute(null);

        Assert.True (vm.IsEcoqosActive);
        Assert.False(vm.IsTimerResolutionActive);
        Assert.False(vm.IsCpuParkingActive);
    }

    // ── BatteryInfo flows from the battery service ────────────────────────────

    [Fact]
    public void BatteryUpdated_Event_UpdatesBatteryInfo()
    {
        var (_, battery, _, vm) = CreateVm();
        var info = new BatteryInfo { ChargePercent = 55 };

        battery.Raise(b => b.Updated += null!, info);

        Assert.Same(info, vm.BatteryInfo);
    }
}
