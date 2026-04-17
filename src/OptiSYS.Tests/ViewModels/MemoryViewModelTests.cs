using Moq;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Services;
using OptiSYS.ViewModels;
using Xunit;

namespace OptiSYS.Tests.ViewModels;

/// <summary>
/// Behavior contract for <see cref="MemoryViewModel"/>:
/// <list type="bullet">
///   <item>Auto-polls memory every 2s via <see cref="ITimerService"/>.</item>
///   <item>Derives <see cref="PressureLevel"/> from <c>MemoryInfo.UsagePercent</c> with
///         canonical thresholds (&lt;60 Normal, [60,75) Elevated, [75,90) High, ≥90 Critical).</item>
///   <item>Optimize command flips <c>IsOptimizing</c> during the async call and records
///         a human-readable result string on completion.</item>
///   <item>Null-arg ctor guards.</item>
/// </list>
/// </summary>
public class MemoryViewModelTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (Mock<IMemoryInfoService> memory,
                    Mock<IMemoryOptimizer>   optimizer,
                    Mock<ITimerService>      timer,
                    Action?                  capturedTick,
                    MemoryViewModel          vm)
        CreateVm(MemoryInfo? initialSnapshot = null)
    {
        var memory    = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();
        var timer     = new Mock<ITimerService>();

        // Return a stable snapshot for every GetCurrentMemoryInfo() call so tests can
        // assert on expected state. Tests that care about the pull-model set their own.
        memory.Setup(m => m.GetCurrentMemoryInfo())
              .Returns(initialSnapshot ?? new MemoryInfo());

        Action? captured = null;
        timer.Setup(t => t.Start(It.IsAny<TimeSpan>(), It.IsAny<Action>()))
             .Callback<TimeSpan, Action>((_, tick) => captured = tick)
             .Returns(Mock.Of<IDisposable>());

        var vm = new MemoryViewModel(memory.Object, optimizer.Object, timer.Object);
        return (memory, optimizer, timer, captured, vm);
    }

    // ── Null-arg guards ───────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullMemoryInfo_Throws()
    {
        var optimizer = new Mock<IMemoryOptimizer>().Object;
        var timer     = new Mock<ITimerService>().Object;
        Assert.Throws<ArgumentNullException>(() =>
            new MemoryViewModel(null!, optimizer, timer));
    }

    [Fact]
    public void Ctor_NullOptimizer_Throws()
    {
        var memory = new Mock<IMemoryInfoService>().Object;
        var timer  = new Mock<ITimerService>().Object;
        Assert.Throws<ArgumentNullException>(() =>
            new MemoryViewModel(memory, null!, timer));
    }

    [Fact]
    public void Ctor_NullTimer_Throws()
    {
        var memory    = new Mock<IMemoryInfoService>().Object;
        var optimizer = new Mock<IMemoryOptimizer>().Object;
        Assert.Throws<ArgumentNullException>(() =>
            new MemoryViewModel(memory, optimizer, null!));
    }

    // ── Polling ───────────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_StartsTimerAtTwoSeconds()
    {
        var (_, _, timer, _, _) = CreateVm();
        timer.Verify(t => t.Start(TimeSpan.FromSeconds(2), It.IsAny<Action>()), Times.Once);
    }

    [Fact]
    public void TimerTick_SetsMemoryInfoFromService()
    {
        var snapshot = new MemoryInfo { TotalPhysicalBytes = 8L << 30, AvailablePhysicalBytes = 3L << 30 };
        var (_, _, _, capturedTick, vm) = CreateVm(initialSnapshot: snapshot);

        // Simulate the timer firing.
        capturedTick!();

        Assert.Same(snapshot, vm.MemoryInfo);
    }

    // ── PressureLevel derivation ──────────────────────────────────────────────

    [Theory]
    [InlineData( 0.0,  PressureLevel.Normal)]
    [InlineData(59.9,  PressureLevel.Normal)]
    [InlineData(60.0,  PressureLevel.Elevated)]
    [InlineData(74.9,  PressureLevel.Elevated)]
    [InlineData(75.0,  PressureLevel.High)]
    [InlineData(89.9,  PressureLevel.High)]
    [InlineData(90.0,  PressureLevel.Critical)]
    [InlineData(99.9,  PressureLevel.Critical)]
    public void PressureLevel_DerivedFromUsagePercent(double usagePercent, PressureLevel expected)
    {
        // Build a MemoryInfo with the exact usage we want — 100GB total, varying available.
        const long total = 100L * 1024 * 1024 * 1024;
        long available = (long)(total * (1 - usagePercent / 100));
        var snapshot = new MemoryInfo { TotalPhysicalBytes = total, AvailablePhysicalBytes = available };

        var (_, _, _, tick, vm) = CreateVm(initialSnapshot: snapshot);
        tick!();    // trigger one poll to populate MemoryInfo

        Assert.Equal(expected, vm.PressureLevel);
    }

    // ── OptimizeCommand ───────────────────────────────────────────────────────

    [Fact]
    public async Task OptimizeCommand_CallsTrimProcessWorkingSets()
    {
        var (_, optimizer, _, _, vm) = CreateVm();
        optimizer.Setup(o => o.TrimProcessWorkingSets(It.IsAny<long>()))
                 .Returns((5, 0, 10, false));

        await vm.OptimizeCommand.ExecuteAsync();

        optimizer.Verify(o => o.TrimProcessWorkingSets(It.IsAny<long>()), Times.Once);
    }

    [Fact]
    public async Task OptimizeCommand_SetsLastOptimizationResult_OnCompletion()
    {
        var (_, optimizer, _, _, vm) = CreateVm();
        optimizer.Setup(o => o.TrimProcessWorkingSets(It.IsAny<long>()))
                 .Returns((7, 2, 42, false));

        await vm.OptimizeCommand.ExecuteAsync();

        Assert.NotNull(vm.LastOptimizationResult);
        Assert.Contains("7", vm.LastOptimizationResult); // mention trimmed count somewhere
    }

    [Fact]
    public async Task OptimizeCommand_TogglesIsOptimizingDuringExecution()
    {
        var (_, optimizer, _, _, vm) = CreateVm();
        var gate = new TaskCompletionSource<bool>();

        optimizer.Setup(o => o.TrimProcessWorkingSets(It.IsAny<long>()))
                 .Returns(() =>
                 {
                     // Block until the test signals — lets us inspect IsOptimizing mid-flight.
                     gate.Task.GetAwaiter().GetResult();
                     return (3, 0, 5, false);
                 });

        Assert.False(vm.IsOptimizing);

        var task = vm.OptimizeCommand.ExecuteAsync();

        // Yield so the Task.Run inside the command actually schedules.
        await Task.Delay(50);
        Assert.True(vm.IsOptimizing);

        gate.SetResult(true);
        await task;

        Assert.False(vm.IsOptimizing);
    }
}
