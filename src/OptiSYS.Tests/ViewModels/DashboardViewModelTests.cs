using Moq;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Services;
using OptiSYS.ViewModels;
using Xunit;

namespace OptiSYS.Tests.ViewModels;

/// <summary>
/// Behavior contract for <see cref="DashboardViewModel"/>:
/// <list type="bullet">
///   <item>Constructor takes three dependencies; nulls throw.</item>
///   <item>Battery updates arrive via event subscription (push model).</item>
///   <item>Memory updates arrive via timer tick (pull model — MemoryInfoService has no event).</item>
///   <item>RefreshCommand forces an immediate read of both.</item>
///   <item>Dispose stops the timer subscription (prevents zombie ticks after navigation).</item>
/// </list>
/// </summary>
public class DashboardViewModelTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (Mock<IBatteryInfoService> battery,
                    Mock<IMemoryInfoService>  memory,
                    Mock<ITimerService>       timer,
                    Mock<IDisposable>         timerSub)
        CreateMocks()
    {
        var battery  = new Mock<IBatteryInfoService>();
        var memory   = new Mock<IMemoryInfoService>();
        var timer    = new Mock<ITimerService>();
        var timerSub = new Mock<IDisposable>();

        // Default: timer.Start returns a disposable handle we can verify gets disposed.
        timer.Setup(t => t.Start(It.IsAny<TimeSpan>(), It.IsAny<Action>()))
             .Returns(timerSub.Object);

        return (battery, memory, timer, timerSub);
    }

    // ── Null-arg guards ────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullBattery_Throws()
    {
        var (_, memory, timer, _) = CreateMocks();
        Assert.Throws<ArgumentNullException>(() =>
            new DashboardViewModel(null!, memory.Object, timer.Object));
    }

    [Fact]
    public void Ctor_NullMemory_Throws()
    {
        var (battery, _, timer, _) = CreateMocks();
        Assert.Throws<ArgumentNullException>(() =>
            new DashboardViewModel(battery.Object, null!, timer.Object));
    }

    [Fact]
    public void Ctor_NullTimer_Throws()
    {
        var (battery, memory, _, _) = CreateMocks();
        Assert.Throws<ArgumentNullException>(() =>
            new DashboardViewModel(battery.Object, memory.Object, null!));
    }

    // ── Startup behavior ───────────────────────────────────────────────────────

    [Fact]
    public void Ctor_StartsBatteryPolling()
    {
        var (battery, memory, timer, _) = CreateMocks();
        _ = new DashboardViewModel(battery.Object, memory.Object, timer.Object);

        battery.Verify(b => b.Start(It.IsAny<int>()), Times.Once,
            "Dashboard must kick off battery polling on construction.");
    }

    [Fact]
    public void Ctor_StartsMemoryTimer_WithTwoSecondCadence()
    {
        var (battery, memory, timer, _) = CreateMocks();
        _ = new DashboardViewModel(battery.Object, memory.Object, timer.Object);

        timer.Verify(t => t.Start(TimeSpan.FromSeconds(2), It.IsAny<Action>()), Times.Once);
    }

    // ── Battery event → property update (push model) ───────────────────────────

    [Fact]
    public void BatteryUpdated_Event_UpdatesBatteryInfoProperty()
    {
        var (battery, memory, timer, _) = CreateMocks();
        var vm = new DashboardViewModel(battery.Object, memory.Object, timer.Object);
        var info = new BatteryInfo { ChargePercent = 42 };

        battery.Raise(b => b.Updated += null!, info);

        Assert.Same(info, vm.BatteryInfo);
    }

    // ── Timer tick → memory refresh (pull model) ───────────────────────────────

    [Fact]
    public void TimerTick_InvokesGetCurrentMemoryInfoAndUpdatesProperty()
    {
        var (battery, memory, timer, _) = CreateMocks();

        Action? capturedTick = null;
        timer.Setup(t => t.Start(It.IsAny<TimeSpan>(), It.IsAny<Action>()))
             .Callback<TimeSpan, Action>((_, tick) => capturedTick = tick)
             .Returns(Mock.Of<IDisposable>());

        var snapshot = new MemoryInfo { TotalPhysicalBytes = 16L * 1024 * 1024 * 1024 };
        memory.Setup(m => m.GetCurrentMemoryInfo()).Returns(snapshot);

        var vm = new DashboardViewModel(battery.Object, memory.Object, timer.Object);

        Assert.NotNull(capturedTick);
        capturedTick!();

        Assert.Same(snapshot, vm.MemoryInfo);
        memory.Verify(m => m.GetCurrentMemoryInfo(), Times.Once);
    }

    // ── RefreshCommand ─────────────────────────────────────────────────────────

    [Fact]
    public void RefreshCommand_IsNotNullAndCanExecute()
    {
        var (battery, memory, timer, _) = CreateMocks();
        var vm = new DashboardViewModel(battery.Object, memory.Object, timer.Object);

        Assert.NotNull(vm.RefreshCommand);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public void RefreshCommand_Execute_UpdatesBothInfosFromServices()
    {
        var (battery, memory, timer, _) = CreateMocks();

        var batterySnapshot = new BatteryInfo { ChargePercent = 77 };
        var memorySnapshot  = new MemoryInfo  { TotalPhysicalBytes = 8L * 1024 * 1024 * 1024 };

        battery.SetupGet(b => b.CurrentInfo).Returns(batterySnapshot);
        memory .Setup   (m => m.GetCurrentMemoryInfo()).Returns(memorySnapshot);

        var vm = new DashboardViewModel(battery.Object, memory.Object, timer.Object);
        vm.RefreshCommand.Execute(null);

        Assert.Same(batterySnapshot, vm.BatteryInfo);
        Assert.Same(memorySnapshot,  vm.MemoryInfo);
    }

    // ── Teardown ───────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DisposesTimerSubscription()
    {
        var (battery, memory, timer, timerSub) = CreateMocks();
        var vm = new DashboardViewModel(battery.Object, memory.Object, timer.Object);

        vm.Dispose();

        timerSub.Verify(d => d.Dispose(), Times.Once,
            "Dashboard must dispose the timer handle to stop zombie ticks after navigation.");
    }
}
