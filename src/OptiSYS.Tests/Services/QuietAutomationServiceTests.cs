using Moq;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using OptiSYS.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public sealed class QuietAutomationServiceTests
{
    [Fact]
    public async Task StartAsync_ForcesOnlySafeRuntimeDomains()
    {
        var settings = new Settings
        {
            BackgroundServicesEnabled = true,
            UsbSuspendEnabled = true,
            NetworkPowerEnabled = true,
            GpuPowerEnabled = true,
            CpuParkingEnabled = true,
            DiskCoalescingEnabled = true,
        };
        var timer = new FakeTimerService();
        var service = CreateService(settings, timer);

        await service.StartAsync();

        Assert.False(settings.EcoQosEnabled);       // opt-in now — never force-enabled
        Assert.False(settings.TimerResolutionEnabled);
        Assert.False(settings.BackgroundServicesEnabled);
        Assert.False(settings.UsbSuspendEnabled);
        Assert.False(settings.NetworkPowerEnabled);
        Assert.False(settings.GpuPowerEnabled);
        Assert.True(settings.CpuParkingEnabled);    // now an enabled battery optimization (DC min state -> 0%)
        Assert.False(settings.DiskCoalescingEnabled);
    }

    [Fact]
    public async Task StartAsync_ExcludesOnlyCriticalSystemProcesses_NotAUserList()
    {
        // optiRAM parity: no user-managed exclusion list — only critical system processes are
        // excluded (foreground + self are skipped inside the trim itself).
        var settings = new Settings
        {
            MemoryExcludedProcesses = ["custom-daemon"],
            ProtectedApplications = ["Code", "WindowsTerminal"],
        };
        var timer = new FakeTimerService();
        var optimizer = new Mock<IMemoryOptimizer>();
        optimizer.SetupProperty(o => o.ExcludedProcesses, []);

        var service = CreateService(settings, timer, optimizer: optimizer);

        await service.StartAsync();

        Assert.Contains("explorer", optimizer.Object.ExcludedProcesses);              // critical system process
        Assert.DoesNotContain("custom-daemon", optimizer.Object.ExcludedProcesses);   // user list ignored
        Assert.DoesNotContain("Code", optimizer.Object.ExcludedProcesses);
    }

    [Fact]
    public async Task TimerTick_WhenMemoryBelowThreshold_DoesNotRunOptimizerOrEngine()
    {
        var settings = new Settings { MemoryThresholdPercent = 80 };
        var timer = new FakeTimerService();
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();
        var engine = new Mock<IOptimizationEngine>();

        memory.Setup(m => m.GetCurrentMemoryInfo()).Returns(new MemoryInfo
        {
            TotalPhysicalBytes = 100,
            AvailablePhysicalBytes = 50,
        });

        var service = CreateService(settings, timer, memory, optimizer, engine);
        await service.StartAsync();

        timer.Tick();

        optimizer.Verify(o => o.OptimizeAll(
            It.IsAny<OptimizationLevel>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<bool>(),
            It.IsAny<int>(),
            It.IsAny<bool>()), Times.Never);
        engine.Verify(e => e.ActivateCategory(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TimerTick_WhenMemoryAboveThreshold_RunsCleanupAtSelectedMode()
    {
        // Default mode is Balanced; the automatic path runs the full pipeline at that level.
        var settings = new Settings { MemoryThresholdPercent = 50 };
        var timer = new FakeTimerService();
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        memory.Setup(m => m.GetCurrentMemoryInfo()).Returns(new MemoryInfo
        {
            TotalPhysicalBytes = 100,
            AvailablePhysicalBytes = 30,   // 70% usage: above threshold, below the 85% critical mark
        });
        optimizer.Setup(o => o.OptimizeAll(
                OptimizationLevel.Balanced, 0, 50, false, 0, true))
            .Returns(new OptimizationResult { Success = true, FreedBytes = 10 });

        var service = CreateService(settings, timer, memory, optimizer);
        await service.StartAsync();

        timer.Tick();

        await WaitForAssertionAsync(() =>
            optimizer.Verify(o => o.OptimizeAll(
                OptimizationLevel.Balanced, 0, 50, false, 0, true), Times.Once));
    }

    [Fact]
    public async Task TimerTick_WhenMemoryCritical_EscalatesToAggressive_BypassingCooldown()
    {
        // OOM prevention: at/above the critical mark, reclaim hard immediately at the full
        // (Aggressive) level, and AGAIN on the next tick even within the cooldown window — so a
        // fast allocation burst can't blow through the free-RAM buffer between spaced-out cleanups.
        var settings = new Settings
        {
            MemoryThresholdPercent = 60,
            MemoryCriticalThresholdPercent = 85,
            MemoryCooldownSeconds = 300,   // long cooldown: proves the critical path bypasses it
        };
        var timer = new FakeTimerService();
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        memory.Setup(m => m.GetCurrentMemoryInfo()).Returns(Mem(90, commitRatio: 0.5));   // 90% >= 85% critical
        optimizer.Setup(o => o.OptimizeAll(OptimizationLevel.Aggressive, 0, 60, false, 0, true))
            .Returns(new OptimizationResult { Success = true, FreedBytes = 10 });

        var service = CreateService(settings, timer, memory, optimizer);
        await service.StartAsync();

        timer.Tick();
        await WaitForAssertionAsync(() => optimizer.Verify(o => o.OptimizeAll(
            OptimizationLevel.Aggressive, 0, 60, false, 0, true), Times.Once));

        timer.Tick();   // still critical, still inside the 300s cooldown
        await WaitForAssertionAsync(() => optimizer.Verify(o => o.OptimizeAll(
            OptimizationLevel.Aggressive, 0, 60, false, 0, true), Times.Exactly(2)));
    }

    [Fact]
    public async Task TimerTick_PredictivelyTrimsBelowThreshold_OnRisingTrendUnderCommitPressure()
    {
        var settings = new Settings { MemoryThresholdPercent = 80, MemoryCriticalThresholdPercent = 95, MemoryCooldownSeconds = 15 };
        var timer = new FakeTimerService();
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        // Usage rises 60→70→78 — all BELOW the 80 reactive threshold — under high commit (0.70).
        memory.SetupSequence(m => m.GetCurrentMemoryInfo())
            .Returns(Mem(60, commitRatio: 0.70))
            .Returns(Mem(70, commitRatio: 0.70))
            .Returns(Mem(78, commitRatio: 0.70));

        optimizer.Setup(o => o.OptimizeAll(OptimizationLevel.Balanced, 0, 80, false, 0, true))
            .Returns(new OptimizationResult { Success = true, FreedBytes = 5 });

        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var predictor = new MemoryTrendPredictor(utcNow: () => now);

        var service = new QuietAutomationService(
            settings, Mock.Of<IBatteryInfoService>(), memory.Object, optimizer.Object,
            Mock.Of<IOptimizationEngine>(), timer, predictor);
        await service.StartAsync();

        timer.Tick();                 // 60 @ t0  — <3 samples, no fire
        now = now.AddSeconds(10);
        timer.Tick();                 // 70 @ t10 — 2 samples, no fire
        now = now.AddSeconds(10);
        timer.Tick();                 // 78 @ t20 — slope ≈0.9%/s, projected ≈91.5 ≥ 80 → pre-emptive trim

        await WaitForAssertionAsync(() =>
            optimizer.Verify(o => o.OptimizeAll(
                OptimizationLevel.Balanced, 0, 80, false, 0, true), Times.Once));
    }

    [Fact]
    public async Task RunDeepCleanAsync_RunsAggressiveMaxReclaim()
    {
        var settings = new Settings { MemoryThresholdPercent = 50 };
        var timer = new FakeTimerService();
        var optimizer = new Mock<IMemoryOptimizer>();
        optimizer.Setup(o => o.OptimizeAll(
                OptimizationLevel.Aggressive, 0, 0, false, 0, true))
            .Returns(new OptimizationResult { Success = true, FreedBytes = 42 });

        var service = CreateService(settings, timer, optimizer: optimizer);
        await service.StartAsync();

        await service.RunDeepCleanAsync();

        // Explicit, user-initiated Deep Clean runs the FULL pipeline unconditionally (threshold 0),
        // not gated by the automatic 60% threshold — so the button actually deep-cleans on demand.
        optimizer.Verify(o => o.OptimizeAll(
            OptimizationLevel.Aggressive, 0, 0, false, 0, true), Times.Once);
        Assert.Equal(42, service.TotalFreedBytes);
    }

    [Fact]
    public async Task TimerTick_HintsBackgroundMemoryPriorityEveryCycle()
    {
        var settings = new Settings { MemoryThresholdPercent = 80 };
        var timer = new FakeTimerService();
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        // Below threshold so no cleanup runs — proves the hint fires independently of cleanup.
        memory.Setup(m => m.GetCurrentMemoryInfo()).Returns(new MemoryInfo
        {
            TotalPhysicalBytes = 100,
            AvailablePhysicalBytes = 50,
        });

        var service = CreateService(settings, timer, memory, optimizer);
        await service.StartAsync();

        timer.Tick();

        await WaitForAssertionAsync(() =>
            optimizer.Verify(o => o.HintBackgroundMemoryPriority(), Times.Once));
    }

    [Fact]
    public async Task ApplyBatteryPreset_ActivatesOnlyEngineCategoryAfterSafeSettings()
    {
        var settings = new Settings();
        var timer = new FakeTimerService();
        var engine = new Mock<IOptimizationEngine>();
        engine.Setup(e => e.GetAllStatuses()).Returns([]);
        engine.Setup(e => e.ActivateCategory("Battery")).Returns(new EngineResult { Success = true });

        var service = CreateService(settings, timer, engine: engine);
        await service.StartAsync();

        service.ApplyBatteryPreset();

        engine.Verify(e => e.ActivateCategory("Battery"), Times.Once);
        Assert.False(settings.EcoQosEnabled);       // opt-in now — never force-enabled
        Assert.False(settings.TimerResolutionEnabled);
        Assert.False(settings.BackgroundServicesEnabled);
        Assert.True(settings.CpuParkingEnabled);    // force-enabled AIO battery optimization
    }

    [Fact]
    public async Task StartAsync_WhenNotPaused_ActivatesWiFiOptimizer()
    {
        var settings = new Settings { AutomationPaused = false };
        var engine = new Mock<IOptimizationEngine>();
        var service = CreateService(settings, new FakeTimerService(), engine: engine);

        await service.StartAsync();

        engine.Verify(e => e.ActivateDomain("wifi-optimizer"), Times.Once);
    }

    [Fact]
    public async Task StartAsync_WhenPaused_DoesNotActivateWiFiOptimizer()
    {
        var settings = new Settings { AutomationPaused = true };
        var engine = new Mock<IOptimizationEngine>();
        var service = CreateService(settings, new FakeTimerService(), engine: engine);

        await service.StartAsync();

        engine.Verify(e => e.ActivateDomain("wifi-optimizer"), Times.Never);
    }

    [Fact]
    public async Task SetAutomationPaused_RevertsWiFiOnPause_AndReactivatesOnResume()
    {
        var settings = new Settings { AutomationPaused = false };
        var engine = new Mock<IOptimizationEngine>();
        var service = CreateService(settings, new FakeTimerService(), engine: engine);
        await service.StartAsync();   // activate #1 (not paused)

        service.SetAutomationPaused(true);
        engine.Verify(e => e.RevertDomain("wifi-optimizer"), Times.Once);

        service.SetAutomationPaused(false);
        engine.Verify(e => e.ActivateDomain("wifi-optimizer"), Times.Exactly(2)); // startup + resume
    }

    private static QuietAutomationService CreateService(
        Settings settings,
        FakeTimerService timer,
        Mock<IMemoryInfoService>? memory = null,
        Mock<IMemoryOptimizer>? optimizer = null,
        Mock<IOptimizationEngine>? engine = null)
    {
        memory ??= new Mock<IMemoryInfoService>();
        optimizer ??= new Mock<IMemoryOptimizer>();
        engine ??= new Mock<IOptimizationEngine>();

        return new QuietAutomationService(
            settings,
            Mock.Of<IBatteryInfoService>(),
            memory.Object,
            optimizer.Object,
            engine.Object,
            timer);
    }

    private static MemoryInfo Mem(double usagePercent, double commitRatio) => new()
    {
        TotalPhysicalBytes = 100,
        AvailablePhysicalBytes = (long)(100 - usagePercent),
        CommitTotalBytes = (long)(commitRatio * 100),
        CommitLimitBytes = 100,
    };

    private static async Task WaitForAssertionAsync(Action assertion)
    {
        Exception? last = null;
        for (var i = 0; i < 20; i++)
        {
            try
            {
                assertion();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(10);
            }
        }

        throw last ?? new TimeoutException("Assertion did not pass in time.");
    }

    private sealed class FakeTimerService : ITimerService
    {
        private Action? _tick;

        public IDisposable Start(TimeSpan interval, Action tick)
        {
            _tick = tick;
            return Mock.Of<IDisposable>();
        }

        public void Tick() => _tick?.Invoke();
    }
}
