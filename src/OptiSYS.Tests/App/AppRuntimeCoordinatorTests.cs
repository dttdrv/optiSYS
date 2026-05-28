using Microsoft.Extensions.DependencyInjection;
using Moq;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS;
using OptiSYS.Models;
using OptiSYS.Services;
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
            settings);

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
        startup.Verify(s => s.Apply(settings.StartWithWindows), Times.Once);
        tray.Verify(t => t.Update(It.IsAny<TraySnapshot>()), Times.Once);
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
        var settings = new Settings();

        var coordinator = new AppRuntimeCoordinator(
            battery.Object,
            memory.Object,
            powerMonitor.Object,
            automation.Object,
            tray.Object,
            startup.Object,
            settings);

        coordinator.Dispose();

        battery.Verify(b => b.Stop(), Times.Once);
        powerMonitor.Verify(p => p.Stop(), Times.Once);
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
                settings);

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
