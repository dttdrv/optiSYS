using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using OptiSYS.Services;
using Xunit;

namespace OptiSYS.Tests.App;

/// <summary>
/// Verifies the DI container wires up every service the app needs, with correct lifetimes.
///
/// <para>
/// These tests call <see cref="AppHost.ConfigureServices"/> directly against a fresh
/// <see cref="ServiceCollection"/> instead of touching the static <c>AppHost.Services</c>
/// singleton — this avoids parallel-test collisions and keeps each test self-contained.
/// </para>
/// </summary>
public class ServiceRegistrationTests
{
    /// <summary>Builds a provider via the same registration code production uses.</summary>
    private static IServiceProvider BuildProvider()
    {
        var sc = new ServiceCollection();
        OptiSYS.AppHost.ConfigureServices(sc);
        return sc.BuildServiceProvider(validateScopes: false);
    }

    // ── Singletons resolve and are non-null ──────────────────────────────────────

    [Theory]
    [InlineData(typeof(Settings))]
    [InlineData(typeof(INativeBridge))]
    [InlineData(typeof(SnapshotStore))]
    [InlineData(typeof(BatteryInfoService))]
    [InlineData(typeof(IBatteryInfoService))]
    [InlineData(typeof(MemoryInfoService))]
    [InlineData(typeof(IMemoryInfoService))]
    [InlineData(typeof(MemoryOptimizer))]
    [InlineData(typeof(IMemoryOptimizer))]
    [InlineData(typeof(UnifiedOptimizationEngine))]
    [InlineData(typeof(IOptimizationEngine))]
    [InlineData(typeof(PowerSourceMonitor))]
    [InlineData(typeof(IPowerSourceMonitor))]
    [InlineData(typeof(AppRuntimeCoordinator))]
    [InlineData(typeof(IAppRuntimeCoordinator))]
    [InlineData(typeof(QuietAutomationService))]
    [InlineData(typeof(IQuietAutomationService))]
    [InlineData(typeof(TrayIconService))]
    [InlineData(typeof(ITrayIconService))]
    [InlineData(typeof(ITimerService))]
    [InlineData(typeof(StartupRegistrationService))]
    [InlineData(typeof(IStartupRegistrationService))]
    public void RegisteredService_Resolves(Type serviceType)
    {
        using var provider = (ServiceProvider)BuildProvider();
        var instance = provider.GetService(serviceType);
        Assert.NotNull(instance);
    }

    // ── Interface registrations return an instance of the correct concrete type ──

    [Theory]
    [InlineData(typeof(INativeBridge))]                               // provided by factory
    [InlineData(typeof(IBatteryInfoService), typeof(BatteryInfoService))]
    [InlineData(typeof(IMemoryInfoService),  typeof(MemoryInfoService))]
    [InlineData(typeof(IMemoryOptimizer),    typeof(MemoryOptimizer))]
    [InlineData(typeof(IOptimizationEngine), typeof(UnifiedOptimizationEngine))]
    [InlineData(typeof(IPowerSourceMonitor), typeof(PowerSourceMonitor))]
    [InlineData(typeof(IAppRuntimeCoordinator), typeof(AppRuntimeCoordinator))]
    [InlineData(typeof(IQuietAutomationService), typeof(QuietAutomationService))]
    [InlineData(typeof(ITrayIconService), typeof(TrayIconService))]
    [InlineData(typeof(ITimerService), typeof(DispatcherTimerService))]
    [InlineData(typeof(IStartupRegistrationService), typeof(StartupRegistrationService))]
    public void InterfaceRegistration_ReturnsExpectedImplementation(Type serviceType, Type? expectedImplType = null)
    {
        using var provider = (ServiceProvider)BuildProvider();
        var instance = provider.GetService(serviceType);
        Assert.NotNull(instance);
        Assert.True(serviceType.IsInstanceOfType(instance),
            $"Resolved instance of type {instance!.GetType()} is not assignable to {serviceType}.");

        if (expectedImplType is not null)
            Assert.IsType(expectedImplType, instance);
    }

    // ── Singletons return the SAME instance on repeated resolves ─────────────────

    [Theory]
    [InlineData(typeof(Settings))]
    [InlineData(typeof(SnapshotStore))]
    [InlineData(typeof(BatteryInfoService))]
    [InlineData(typeof(MemoryInfoService))]
    [InlineData(typeof(MemoryOptimizer))]
    [InlineData(typeof(UnifiedOptimizationEngine))]
    [InlineData(typeof(PowerSourceMonitor))]
    [InlineData(typeof(AppRuntimeCoordinator))]
    [InlineData(typeof(IAppRuntimeCoordinator))]
    [InlineData(typeof(QuietAutomationService))]
    [InlineData(typeof(IQuietAutomationService))]
    [InlineData(typeof(TrayIconService))]
    [InlineData(typeof(ITrayIconService))]
    [InlineData(typeof(ITimerService))]
    [InlineData(typeof(StartupRegistrationService))]
    [InlineData(typeof(IStartupRegistrationService))]
    public void SingletonService_ReturnsSameInstance(Type serviceType)
    {
        using var provider = (ServiceProvider)BuildProvider();
        var first  = provider.GetRequiredService(serviceType);
        var second = provider.GetRequiredService(serviceType);
        Assert.Same(first, second);
    }

    /// <summary>
    /// Interface-aliased singletons (<see cref="IOptimizationEngine"/>, etc.) must resolve
    /// to the SAME instance as their concrete type. This is the tricky part of the "concrete
    /// + interface alias via factory" pattern — the factory lambda closes over the DI
    /// provider, so asking for either key hands back one underlying object.
    /// </summary>
    [Theory]
    [InlineData(typeof(BatteryInfoService),        typeof(IBatteryInfoService))]
    [InlineData(typeof(MemoryInfoService),         typeof(IMemoryInfoService))]
    [InlineData(typeof(MemoryOptimizer),           typeof(IMemoryOptimizer))]
    [InlineData(typeof(UnifiedOptimizationEngine), typeof(IOptimizationEngine))]
    [InlineData(typeof(PowerSourceMonitor),         typeof(IPowerSourceMonitor))]
    [InlineData(typeof(AppRuntimeCoordinator),     typeof(IAppRuntimeCoordinator))]
    [InlineData(typeof(QuietAutomationService),    typeof(IQuietAutomationService))]
    [InlineData(typeof(TrayIconService),           typeof(ITrayIconService))]
    [InlineData(typeof(StartupRegistrationService), typeof(IStartupRegistrationService))]
    public void InterfaceAlias_SharesInstanceWithConcreteRegistration(Type concreteType, Type interfaceType)
    {
        using var provider = (ServiceProvider)BuildProvider();
        var viaConcrete = provider.GetRequiredService(concreteType);
        var viaInterface = provider.GetRequiredService(interfaceType);
        Assert.Same(viaConcrete, viaInterface);
    }

    [Fact]
    public void OptimizationDomains_ResolveInDeterministicOrder()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var domains = provider.GetServices<IOptimizationDomain>().Select(d => d.Id).ToArray();

        Assert.Equal(
        [
            "ecoqos",
            "timer-resolution",
            "memory-optimize",
        ], domains);
    }

}
