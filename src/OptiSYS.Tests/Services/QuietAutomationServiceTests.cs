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
        Assert.True(settings.CpuParkingEnabled);    // opt-in: startup PRESERVES the user's enabled choice, never forces it
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

        // Await each tick's full evaluation (including the gated Task.Run cleanup) before the next
        // tick: the cleanup gate releases only after OptimizeAll returns, so polling on the call
        // count alone let the second tick race the first one's gate under threadpool contention.
        timer.Tick();
        await service.LastEvaluationForTests;
        optimizer.Verify(o => o.OptimizeAll(
            OptimizationLevel.Aggressive, 0, 60, false, 0, true), Times.Once);

        timer.Tick();   // still critical, still inside the 300s cooldown
        await service.LastEvaluationForTests;
        optimizer.Verify(o => o.OptimizeAll(
            OptimizationLevel.Aggressive, 0, 60, false, 0, true), Times.Exactly(2));
    }

    [Fact]
    public async Task TimerTick_SustainedCritical_FutileFullReclaimDoesNotRepeatEveryTick()
    {
        // Thrash guard: when a full critical reclaim completes but frees ~nothing (<1% of physical),
        // what remains is unreclaimable working sets — repeating the Aggressive pass every tick
        // reclaims nothing and costs re-faults. The escalation disarms for the rest of the episode;
        // sustained pressure stays with the cooldown-spaced reactive path. (Effective passes DO
        // repeat — pinned by TimerTick_WhenMemoryCritical_EscalatesToAggressive_BypassingCooldown.)
        var settings = new Settings
        {
            MemoryThresholdPercent = 60,
            MemoryCriticalThresholdPercent = 85,
            MemoryCooldownSeconds = 300,
        };
        var timer = new FakeTimerService();
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        memory.Setup(m => m.GetCurrentMemoryInfo()).Returns(Mem(90, commitRatio: 0.5));
        optimizer.Setup(o => o.OptimizeAll(OptimizationLevel.Aggressive, 0, 60, false, 0, true))
            .Returns(new OptimizationResult { Success = true, FreedBytes = 0 });   // ran, freed nothing

        var service = CreateService(settings, timer, memory, optimizer);
        await service.StartAsync();

        timer.Tick(); await service.LastEvaluationForTests;   // 90% -> fires the one full reclaim
        timer.Tick(); await service.LastEvaluationForTests;   // still 90% -> disarmed, must not fire
        timer.Tick(); await service.LastEvaluationForTests;

        optimizer.Verify(o => o.OptimizeAll(
            It.IsAny<OptimizationLevel>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task TimerTick_CriticalEscalation_RearmsOnceUsageFallsBelowCriticalMinusGap()
    {
        // crit 85, gap 10 -> the episode ends at <=75. 90 (futile fire #1) -> 90 (disarmed) ->
        // 70 (re-arms; reactive path is cooldown-blocked) -> 90 (fire #2 for the new burst).
        var settings = new Settings
        {
            MemoryThresholdPercent = 60,
            MemoryCriticalThresholdPercent = 85,
            HysteresisGap = 10,
            MemoryCooldownSeconds = 300,
        };
        var timer = new FakeTimerService();
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        memory.SetupSequence(m => m.GetCurrentMemoryInfo())
            .Returns(Mem(90, commitRatio: 0.5))
            .Returns(Mem(90, commitRatio: 0.5))
            .Returns(Mem(70, commitRatio: 0.5))
            .Returns(Mem(90, commitRatio: 0.5));
        optimizer.Setup(o => o.OptimizeAll(OptimizationLevel.Aggressive, 0, 60, false, 0, true))
            .Returns(new OptimizationResult { Success = true, FreedBytes = 0 });

        var service = CreateService(settings, timer, memory, optimizer);
        await service.StartAsync();

        timer.Tick(); await service.LastEvaluationForTests;   // 90% -> fire #1 (futile -> disarm)
        timer.Tick(); await service.LastEvaluationForTests;   // 90% -> disarmed
        timer.Tick(); await service.LastEvaluationForTests;   // 70% <= 75 -> re-arm
        timer.Tick(); await service.LastEvaluationForTests;   // 90% -> fire #2

        optimizer.Verify(o => o.OptimizeAll(
            OptimizationLevel.Aggressive, 0, 60, false, 0, true), Times.Exactly(2));
    }

    [Fact]
    public async Task TimerTick_CriticalEscalation_BoundaryHoverDoesNotRearm()
    {
        // Oscillating across the critical line must not re-fire per crossing: 90 (futile fire) ->
        // 80 (below crit 85 but above the 75 re-arm line -> stays disarmed) -> 90 (no fire).
        var settings = new Settings
        {
            MemoryThresholdPercent = 60,
            MemoryCriticalThresholdPercent = 85,
            HysteresisGap = 10,
            MemoryCooldownSeconds = 300,
        };
        var timer = new FakeTimerService();
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        memory.SetupSequence(m => m.GetCurrentMemoryInfo())
            .Returns(Mem(90, commitRatio: 0.5))
            .Returns(Mem(80, commitRatio: 0.5))
            .Returns(Mem(90, commitRatio: 0.5));
        optimizer.Setup(o => o.OptimizeAll(OptimizationLevel.Aggressive, 0, 60, false, 0, true))
            .Returns(new OptimizationResult { Success = true, FreedBytes = 0 });

        var service = CreateService(settings, timer, memory, optimizer);
        await service.StartAsync();

        timer.Tick(); await service.LastEvaluationForTests;   // 90% -> fire (futile -> disarm)
        timer.Tick(); await service.LastEvaluationForTests;   // 80% -> hover, no re-arm
        timer.Tick(); await service.LastEvaluationForTests;   // 90% -> still disarmed

        optimizer.Verify(o => o.OptimizeAll(
            OptimizationLevel.Aggressive, 0, 60, false, 0, true), Times.Once);
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
    public void DefaultSettings_MatchPredictorCtorDefaults_SoDefaultConfigIsUnchanged()
    {
        // Wiring the knobs must be behaviour-preserving at default config: the Settings defaults
        // must equal the predictor's hardcoded ctor defaults, otherwise a default install would see
        // different predictive behaviour after wiring. This pins that invariant.
        var settings = new Settings();
        Assert.Equal(10, settings.TrendWindowSize);
        Assert.Equal(15, settings.PredictiveLeadSeconds);
        Assert.Equal(10, settings.HysteresisGap);
        Assert.Equal(0.65, settings.CommitRatioTrigger);
    }

    [Fact]
    public async Task BuildsPredictorFromSettingsKnobs_NonDefaultCommitGateChangesFireDecision()
    {
        // Proves the commit-ratio gate reaches the predictor the service builds. The projection
        // passes either way (lead wiring is pinned by the test above): slope 0.5 %/s at usage 70,
        // lead 40 -> 70 + 0.5*40 = 90 >= 80. The discriminator is the commit gate at ratio 0.50:
        //   default gate 0.65 -> 0.50 <= 0.65 -> would NOT fire
        //   wired   gate 0.30 -> 0.50 >  0.30 -> fires
        // So a pre-emptive cleanup on the third tick is observable ONLY if CommitRatioTrigger
        // was wired from Settings into the predictor.
        var settings = new Settings
        {
            MemoryThresholdPercent = 80,
            MemoryCriticalThresholdPercent = 95,
            MemoryCooldownSeconds = 15,
            PredictiveLeadSeconds = 40,
            CommitRatioTrigger = 0.30,     // non-default (default is 0.65)
        };
        var timer = new FakeTimerService();
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        // Rising 60 -> 65 -> 70 over 20s => slope exactly 0.5 %/s; commit demand only 0.50.
        memory.SetupSequence(m => m.GetCurrentMemoryInfo())
            .Returns(Mem(60, commitRatio: 0.50))
            .Returns(Mem(65, commitRatio: 0.50))
            .Returns(Mem(70, commitRatio: 0.50));

        optimizer.Setup(o => o.OptimizeAll(OptimizationLevel.Balanced, 0, 80, false, 0, true))
            .Returns(new OptimizationResult { Success = true, FreedBytes = 5 });

        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var service = new QuietAutomationService(
            settings, Mock.Of<IBatteryInfoService>(), memory.Object, optimizer.Object,
            Mock.Of<IOptimizationEngine>(), timer, utcNow: () => now);
        await service.StartAsync();

        timer.Tick(); await service.LastEvaluationForTests;            // 60 @ t0  (<3 samples)
        now = now.AddSeconds(10);
        timer.Tick(); await service.LastEvaluationForTests;            // 65 @ t10 (2 samples)
        now = now.AddSeconds(10);
        timer.Tick(); await service.LastEvaluationForTests;            // 70 @ t20 -> fire (gate 0.30)

        await WaitForAssertionAsync(() =>
            optimizer.Verify(o => o.OptimizeAll(
                OptimizationLevel.Balanced, 0, 80, false, 0, true), Times.Once));
    }

    [Fact]
    public async Task BuildsPredictorFromSettingsKnobs_NonDefaultLeadSecondsChangesFireDecision()
    {
        // Proves the Settings knobs reach the predictor the service builds (not a default one).
        // The trend gives slope = 0.5 %/s at usage 70 (threshold 80). The fire test is
        //   usage + slope * lead >= threshold:
        //     default lead 15 -> 70 + 0.5*15 = 77.5  < 80 -> would NOT fire
        //     wired   lead 40 -> 70 + 0.5*40 = 90.0 >= 80 -> fires
        // So a pre-emptive cleanup on the third tick is observable ONLY if PredictiveLeadSeconds
        // was wired from Settings into the predictor.
        var settings = new Settings
        {
            MemoryThresholdPercent = 80,
            MemoryCriticalThresholdPercent = 95,
            MemoryCooldownSeconds = 15,
            TrendWindowSize = 10,
            PredictiveLeadSeconds = 40,    // non-default (default is 15)
            HysteresisGap = 10,
        };
        var timer = new FakeTimerService();
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        // Rising 60 -> 65 -> 70 over 20s => slope exactly 0.5 %/s; all below the 80 threshold.
        memory.SetupSequence(m => m.GetCurrentMemoryInfo())
            .Returns(Mem(60, commitRatio: 0.70))
            .Returns(Mem(65, commitRatio: 0.70))
            .Returns(Mem(70, commitRatio: 0.70));

        optimizer.Setup(o => o.OptimizeAll(OptimizationLevel.Balanced, 0, 80, false, 0, true))
            .Returns(new OptimizationResult { Success = true, FreedBytes = 5 });

        // Deterministic clock wired into the predictor the service builds FROM SETTINGS.
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var service = new QuietAutomationService(
            settings, Mock.Of<IBatteryInfoService>(), memory.Object, optimizer.Object,
            Mock.Of<IOptimizationEngine>(), timer, utcNow: () => now);
        await service.StartAsync();

        timer.Tick(); await service.LastEvaluationForTests;            // 60 @ t0  (<3 samples)
        now = now.AddSeconds(10);
        timer.Tick(); await service.LastEvaluationForTests;            // 65 @ t10 (2 samples)
        now = now.AddSeconds(10);
        timer.Tick(); await service.LastEvaluationForTests;            // 70 @ t20 -> fire (lead 40)

        await WaitForAssertionAsync(() =>
            optimizer.Verify(o => o.OptimizeAll(
                OptimizationLevel.Balanced, 0, 80, false, 0, true), Times.Once));
    }

    [Fact]
    public async Task RunMemoryCleanupAsync_Manual_PassesTargetZero_SoFullRequestedLevelRuns()
    {
        // Manual "Optimize now" must pass targetThresholdPercent: 0 so OptimizeAll skips the trim-only
        // early-exits and runs the user's selected level in full (Max's deep steps included). The
        // watcher path stays pressure-gated (covered by TimerTick_WhenMemoryAboveThreshold).
        var settings = new Settings { MemoryThresholdPercent = 75, OptimizationLevel = OptimizationLevel.Aggressive };
        var timer = new FakeTimerService();
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        optimizer.Setup(o => o.OptimizeAll(
                It.IsAny<OptimizationLevel>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns(new OptimizationResult { Success = true, FreedBytes = 10 });

        var service = CreateService(settings, timer, memory, optimizer);
        await service.StartAsync();

        await service.RunMemoryCleanupAsync();

        optimizer.Verify(o => o.OptimizeAll(
            OptimizationLevel.Aggressive, 0, 0, false, 0, true), Times.Once);   // target == 0
        optimizer.Verify(o => o.OptimizeAll(
            It.IsAny<OptimizationLevel>(), It.IsAny<int>(),
            It.Is<int>(t => t != 0), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
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
    public async Task TimerTick_RaisesMemorySampled_ForTrayRefresh()
    {
        // The tray's memory-% number rides the watcher's cadence: each evaluated sample raises
        // MemorySampled so the coordinator can re-render without a dedicated timer.
        var settings = new Settings { MemoryThresholdPercent = 80 };
        var timer = new FakeTimerService();
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        // Below threshold so no cleanup runs — proves the signal fires on a plain sample.
        memory.Setup(m => m.GetCurrentMemoryInfo()).Returns(new MemoryInfo
        {
            TotalPhysicalBytes = 100,
            AvailablePhysicalBytes = 50,
        });

        var service = CreateService(settings, timer, memory, optimizer);
        await service.StartAsync();

        var sampled = 0;
        service.MemorySampled += () => Interlocked.Increment(ref sampled);

        timer.Tick();
        await service.LastEvaluationForTests;

        Assert.Equal(1, sampled);
    }

    [Fact]
    public async Task Dispose_RestoresBackgroundMemoryPriority()
    {
        // The watcher lowers background-process memory priority across the session; on teardown we
        // must put every lowered process back so none is left at a lowered priority for its lifetime.
        var settings = new Settings();
        var timer = new FakeTimerService();
        var optimizer = new Mock<IMemoryOptimizer>();

        var service = CreateService(settings, timer, optimizer: optimizer);
        await service.StartAsync();

        service.Dispose();

        optimizer.Verify(o => o.RestoreBackgroundMemoryPriority(), Times.Once);
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
        Assert.False(settings.EcoQosEnabled);       // opt-in — never force-enabled
        Assert.False(settings.TimerResolutionEnabled);
        Assert.False(settings.BackgroundServicesEnabled);
        Assert.True(settings.CpuParkingEnabled);    // left at its default (auto-on-battery); ApplySafeDomainSettings neither forces nor disables it
    }

    [Fact]
    public async Task StartAsync_DoesNotForceEnableCpuParking_RespectsRememberedOptIn()
    {
        // CPU parking is opt-in now (it mutates the user-facing "Minimum Processor State" power
        // setting) and the choice must persist across restarts — so startup must NOT force it on.
        var settings = new Settings { CpuParkingEnabled = false };
        var timer = new FakeTimerService();
        var service = CreateService(settings, timer);

        await service.StartAsync();

        Assert.False(settings.CpuParkingEnabled);
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

    [Fact]
    public async Task StartAsync_WithRealThreadPoolTimer_RunsMemoryOptimizer_WithoutAnyDispatcher()
    {
        // Regression sentinel: the watcher must tick on a background/logon launch where no UI
        // message pump exists. A DispatcherTimer-backed service would never fire here (no dispatcher),
        // so this fails on the old regression and passes only with the threadpool-backed timer.
        var settings = new Settings
        {
            MemoryCheckIntervalSeconds = 1,
            MemoryThresholdPercent = 50,
            AutoOptimizeMemoryEnabled = true,
            AutomationPaused = false,
            HasCompletedOnboarding = false,
        };
        var memory = new Mock<IMemoryInfoService>();
        var optimizer = new Mock<IMemoryOptimizer>();

        memory.Setup(m => m.GetCurrentMemoryInfo()).Returns(new MemoryInfo
        {
            TotalPhysicalBytes = 100,
            AvailablePhysicalBytes = 30,   // 70% usage: above the 50% threshold
        });
        optimizer.Setup(o => o.OptimizeAll(
                It.IsAny<OptimizationLevel>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns(new OptimizationResult { Success = true, FreedBytes = 10 });

        var service = new QuietAutomationService(
            settings,
            Mock.Of<IBatteryInfoService>(),
            memory.Object,
            optimizer.Object,
            Mock.Of<IOptimizationEngine>(),
            new ThreadPoolTimerService());

        using var disposable = service;
        await service.StartAsync();

        // No dispatcher driven: the real threadpool timer must tick on its own within ~3s
        // (1s interval + scheduling slack); 300 attempts * 10ms = 3s budget.
        await WaitForAssertionAsync(() => optimizer.Verify(o => o.OptimizeAll(
            It.IsAny<OptimizationLevel>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<bool>()), Times.AtLeastOnce), attempts: 300);
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

    private static async Task WaitForAssertionAsync(Action assertion, int attempts = 20)
    {
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
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
