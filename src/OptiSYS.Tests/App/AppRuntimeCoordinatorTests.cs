using Microsoft.Extensions.DependencyInjection;
using Moq;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using OptiSYS;
using OptiSYS.Models;
using OptiSYS.Services;
using OptiSYS.Services.Elevation;
using Xunit;

namespace OptiSYS.Tests.App;

public class AppRuntimeCoordinatorTests
{
    [Fact]
    public async Task StartAsync_InitializesBatteryMemoryAndPowerMonitor_Once()
    {
        var battery = new Mock<IBatteryInfoService>();
        var memory = new Mock<IMemoryInfoService>();
        var powerMonitor = new Mock<IPowerSourceMonitor>();
        var automation = new Mock<IQuietAutomationService>();
        var tray = new Mock<ITrayIconService>();
        var startup = new Mock<IStartupRegistrationService>();
        var taskScheduler = new Mock<ITaskSchedulerService>();
        var adaptiveEcoQos = new Mock<IAdaptiveEcoQosController>();
        var settings = new Settings();
        var warmUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        memory.Setup(m => m.WarmUpAsync()).Returns(warmUp.Task);
        automation.Setup(a => a.StartAsync()).Returns(Task.CompletedTask);
        automation.Setup(a => a.GetActiveBatteryStatuses()).Returns([]);

        var coordinator = new AppRuntimeCoordinator(
            battery.Object,
            memory.Object,
            powerMonitor.Object,
            automation.Object,
            tray.Object,
            startup.Object,
            taskScheduler.Object,
            settings,
            adaptiveEcoQos.Object);

        var startTask = coordinator.StartAsync();

        Assert.False(startTask.IsCompleted);
        battery.Verify(b => b.Start(It.IsAny<int>()), Times.Once);
        powerMonitor.Verify(p => p.Start(), Times.Once);
        memory.Verify(m => m.WarmUpAsync(), Times.Once);
        automation.Verify(a => a.StartAsync(), Times.Never);

        warmUp.SetResult();
        await startTask;

        var secondStart = coordinator.StartAsync();

        Assert.Same(startTask, secondStart);
        battery.Verify(b => b.Start(It.IsAny<int>()), Times.Once);
        powerMonitor.Verify(p => p.Start(), Times.Once);
        memory.Verify(m => m.WarmUpAsync(), Times.Once);
        automation.Verify(a => a.StartAsync(), Times.Once);
        adaptiveEcoQos.Verify(c => c.Start(), Times.Once);
        startup.Verify(s => s.Apply(settings.StartWithWindows), Times.Once);
        tray.Verify(t => t.Update(It.IsAny<TraySnapshot>()), Times.Once);
    }

    [Theory]
    [InlineData(PowerSource.Battery, BatteryPreset.Saver)]
    [InlineData(PowerSource.Ac, BatteryPreset.Recommended)]
    public async Task PowerSourceChanged_AutoSwitchesBatteryPreset_WithoutTouchingOptimizationLevel(
        PowerSource newSource,
        BatteryPreset expectedPreset)
    {
        var battery = new Mock<IBatteryInfoService>();
        var memory = new Mock<IMemoryInfoService>();
        var powerMonitor = new Mock<IPowerSourceMonitor>();
        var automation = new Mock<IQuietAutomationService>();
        var tray = new Mock<ITrayIconService>();
        var startup = new Mock<IStartupRegistrationService>();
        var taskScheduler = new Mock<ITaskSchedulerService>();
        var adaptiveEcoQos = new Mock<IAdaptiveEcoQosController>();
        // Start opposite to the transition target so each case is a real change.
        var settings = new Settings
        {
            BatteryPreset = newSource == PowerSource.Battery ? BatteryPreset.Recommended : BatteryPreset.Saver,
            OptimizationLevel = OptimizationLevel.Aggressive,
        };

        memory.Setup(m => m.WarmUpAsync()).Returns(Task.CompletedTask);
        automation.Setup(a => a.StartAsync()).Returns(Task.CompletedTask);
        automation.Setup(a => a.GetActiveBatteryStatuses()).Returns([]);

        BatteryPreset? appliedPreset = null;
        automation.Setup(a => a.SetBatteryPreset(It.IsAny<BatteryPreset>()))
            .Callback<BatteryPreset>(p => appliedPreset = p);

        var coordinator = new AppRuntimeCoordinator(
            battery.Object,
            memory.Object,
            powerMonitor.Object,
            automation.Object,
            tray.Object,
            startup.Object,
            taskScheduler.Object,
            settings,
            adaptiveEcoQos.Object);

        await coordinator.StartAsync();

        powerMonitor.Raise(p => p.PowerSourceChanged += null, newSource);

        // Battery preset auto-switched via the canonical apply path...
        automation.Verify(a => a.SetBatteryPreset(expectedPreset), Times.Once);
        Assert.Equal(expectedPreset, appliedPreset);
        // ...and memory mode stayed sticky (never auto-changed by a power event).
        Assert.Equal(OptimizationLevel.Aggressive, settings.OptimizationLevel);
    }

    [Fact]
    public void Dispose_StopsBatteryAndPowerMonitor()
    {
        var battery = new Mock<IBatteryInfoService>();
        var memory = new Mock<IMemoryInfoService>();
        var powerMonitor = new Mock<IPowerSourceMonitor>();
        var automation = new Mock<IQuietAutomationService>();
        var tray = new Mock<ITrayIconService>();
        var startup = new Mock<IStartupRegistrationService>();
        var taskScheduler = new Mock<ITaskSchedulerService>();
        var adaptiveEcoQos = new Mock<IAdaptiveEcoQosController>();
        var settings = new Settings();

        var coordinator = new AppRuntimeCoordinator(
            battery.Object,
            memory.Object,
            powerMonitor.Object,
            automation.Object,
            tray.Object,
            startup.Object,
            taskScheduler.Object,
            settings,
            adaptiveEcoQos.Object);

        coordinator.Dispose();

        battery.Verify(b => b.Stop(), Times.Once);
        powerMonitor.Verify(p => p.Stop(), Times.Once);
        adaptiveEcoQos.Verify(c => c.Dispose(), Times.Once);
        automation.Verify(a => a.Dispose(), Times.Once);
    }

    [Fact]
    public void InitializeRuntime_StartsCoordinatorExactlyOnce()
    {
        var services = new ServiceCollection();
        var coordinator = new Mock<IAppRuntimeCoordinator>();

        coordinator.Setup(c => c.StartAsync()).Returns(Task.CompletedTask);
        services.AddSingleton<IAppRuntimeCoordinator>(coordinator.Object);

        var resolved = OptiSYS.App.InitializeRuntime(services.BuildServiceProvider());

        Assert.Same(coordinator.Object, resolved);
        coordinator.Verify(c => c.StartAsync(), Times.Once);
    }

    [Theory]
    [InlineData("--background", new[] { "OptiSYS.exe" }, true)]
    [InlineData("", new[] { "OptiSYS.exe", "--background" }, true)]
    [InlineData("", new[] { "OptiSYS.exe" }, false)]
    public void IsBackgroundLaunch_ChecksActivationAndProcessArguments(
        string activationArguments,
        string[] commandLineArguments,
        bool expected)
    {
        Assert.Equal(expected, OptiSYS.App.IsBackgroundLaunch(activationArguments, commandLineArguments));
    }

    [Theory]
    [InlineData(new[] { "OptiSYS.exe" }, false)]
    [InlineData(new[] { "OptiSYS.exe", "--background" }, false)]
    [InlineData(new[] { "OptiSYS.exe", "--provision-elevation" }, true)]
    [InlineData(new[] { "OptiSYS.exe", "--PROVISION-ELEVATION" }, true)]
    public void IsProvisionElevationLaunch_DetectsArgumentCaseInsensitively(
        string[] commandLineArguments,
        bool expected)
    {
        Assert.Equal(expected, OptiSYS.App.IsProvisionElevationLaunch(commandLineArguments));
    }

    [Fact]
    public async Task ConfigureAutostartBackend_RunKeyMode_AppliesRunKey_AndLeavesTaskUntouched()
    {
        var settings = new Settings { UseTaskScheduler = false, StartWithWindows = true };
        var (coordinator, startup, task) = BuildForBackend(settings);

        await coordinator.StartAsync();

        startup.Verify(s => s.Apply(true), Times.Once);     // today's HKCU Run-key path, unchanged
        task.Verify(t => t.NeedsProvisioning(), Times.Never);
        task.Verify(t => t.CreateOrUpdateTask(), Times.Never);
    }

    [Fact]
    public async Task ConfigureAutostartBackend_TaskModeElevated_NeedsProvisioning_SelfHealsSilently()
    {
        var settings = new Settings { UseTaskScheduler = true };
        var (coordinator, startup, task) = BuildForBackend(settings);
        task.Setup(t => t.NeedsProvisioning()).Returns(true);
        task.Setup(t => t.IsElevated).Returns(true);

        await coordinator.StartAsync();

        task.Verify(t => t.CreateOrUpdateTask(), Times.Once);  // already elevated → no UAC
        startup.Verify(s => s.Apply(false), Times.Once);       // Run key removed (no double launch)
        Assert.False(settings.ElevationPending);
    }

    [Fact]
    public async Task ConfigureAutostartBackend_TaskModeUnelevated_NeedsProvisioning_FlagsPending_NoBootPrompt()
    {
        var settings = new Settings { UseTaskScheduler = true };
        var (coordinator, startup, task) = BuildForBackend(settings);
        task.Setup(t => t.NeedsProvisioning()).Returns(true);
        task.Setup(t => t.IsElevated).Returns(false);

        await coordinator.StartAsync();

        task.Verify(t => t.CreateOrUpdateTask(), Times.Never); // never prompts for UAC at boot
        startup.Verify(s => s.Apply(false), Times.Once);
        Assert.True(settings.ElevationPending);                // UI banner drives the one-time grant
    }

    [Fact]
    public async Task ConfigureAutostartBackend_TaskModeAlreadyProvisioned_NoReprovision_ClearsPending()
    {
        var settings = new Settings { UseTaskScheduler = true, ElevationPending = true };
        var (coordinator, startup, task) = BuildForBackend(settings);
        task.Setup(t => t.NeedsProvisioning()).Returns(false);

        await coordinator.StartAsync();

        task.Verify(t => t.CreateOrUpdateTask(), Times.Never);
        startup.Verify(s => s.Apply(false), Times.Once);
        Assert.False(settings.ElevationPending);
    }

    private static (AppRuntimeCoordinator coordinator, Mock<IStartupRegistrationService> startup, Mock<ITaskSchedulerService> task)
        BuildForBackend(Settings settings)
    {
        var battery = new Mock<IBatteryInfoService>();
        var memory = new Mock<IMemoryInfoService>();
        var powerMonitor = new Mock<IPowerSourceMonitor>();
        var automation = new Mock<IQuietAutomationService>();
        var tray = new Mock<ITrayIconService>();
        var startup = new Mock<IStartupRegistrationService>();
        var task = new Mock<ITaskSchedulerService>();
        var adaptiveEcoQos = new Mock<IAdaptiveEcoQosController>();

        memory.Setup(m => m.WarmUpAsync()).Returns(Task.CompletedTask);
        automation.Setup(a => a.StartAsync()).Returns(Task.CompletedTask);
        automation.Setup(a => a.GetActiveBatteryStatuses()).Returns([]);

        var coordinator = new AppRuntimeCoordinator(
            battery.Object,
            memory.Object,
            powerMonitor.Object,
            automation.Object,
            tray.Object,
            startup.Object,
            task.Object,
            settings,
            adaptiveEcoQos.Object);
        return (coordinator, startup, task);
    }

    [Fact]
    public async Task StartAsync_PreservesCallerSynchronizationContext_ForAutomationStartup()
    {
        var originalContext = SynchronizationContext.Current;
        var startupContext = new ImmediateSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(startupContext);

        try
        {
            var battery = new Mock<IBatteryInfoService>();
            var memory = new Mock<IMemoryInfoService>();
        var powerMonitor = new Mock<IPowerSourceMonitor>();
        var automation = new ContextCapturingAutomationService();
        var tray = new Mock<ITrayIconService>();
        var startup = new Mock<IStartupRegistrationService>();
        var taskScheduler = new Mock<ITaskSchedulerService>();
        var adaptiveEcoQos = new Mock<IAdaptiveEcoQosController>();
        var settings = new Settings();

        memory.Setup(m => m.WarmUpAsync()).Returns(async () =>
            {
                await Task.Yield();
            });

            var coordinator = new AppRuntimeCoordinator(
                battery.Object,
                memory.Object,
                powerMonitor.Object,
                automation,
                tray.Object,
                startup.Object,
                taskScheduler.Object,
                settings,
                adaptiveEcoQos.Object);

            await coordinator.StartAsync();

            Assert.Same(startupContext, automation.StartContext);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class ContextCapturingAutomationService : IQuietAutomationService
    {
        public SynchronizationContext? StartContext { get; private set; }

#pragma warning disable CS0067
        public event Action? StateChanged;
#pragma warning restore CS0067

        public string LastActivity => string.Empty;
        public DateTimeOffset? LastActivityAt => null;
        public bool IsCleanupRunning => false;
        public long TotalFreedBytes => 0;

        public Task StartAsync()
        {
            StartContext = SynchronizationContext.Current;
            return Task.CompletedTask;
        }

        public Task RunMemoryCleanupAsync() => Task.CompletedTask;

        public void SetBatteryPreset(OptiSYS.Core.Models.BatteryPreset preset)
        {
        }

        public void ApplyBatteryPreset()
        {
        }

        public void SetAutomationPaused(bool paused)
        {
        }

        public IReadOnlyList<OptiSYS.Core.Interfaces.DomainStatus> GetActiveBatteryStatuses() => [];

        public void Dispose()
        {
        }
    }
}
