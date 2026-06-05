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
    [InlineData(typeof(IEffectivePowerModeProvider))]
    [InlineData(typeof(IAdaptiveEcoQosController))]
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
    [InlineData(typeof(ITimerService), typeof(ThreadPoolTimerService))]
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

        // All nine built domains are now registered (was 3 — the six battery-category
        // domains were complete but unwired). Apply runs in this order; Revert reverses.
        Assert.Equal(
        [
            "ecoqos",
            "timer-resolution",
            "memory-optimize",
            "background-services",
            "usb-suspend",
            "network-power",
            "gpu-power",
            "cpu-parking",
            "disk-coalescing",
            "wifi-optimizer",
            "services-manual",
        ], domains);
    }

    /// <summary>
    /// Domain Ids must be unique. The enable gate moved from the engine's central switch onto
    /// each domain (<see cref="IOptimizationDomain.IsEnabled"/>), so there is no longer a string
    /// key-set to mirror; a duplicate Id (which would make snapshot/revert ambiguous) still fails
    /// here. Per-domain enable wiring is asserted by
    /// <see cref="OptimizationDomains_HaveAResponsiveEnablePredicate"/>.
    /// </summary>
    [Fact]
    public void OptimizationDomains_HaveUniqueIds()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var ids = provider.GetServices<IOptimizationDomain>().Select(d => d.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());   // no duplicate Ids
    }

    /// <summary>
    /// With the gate owned by each domain, the old "typo'd Id silently no-ops" trap is replaced by
    /// a different invariant: every registered domain must have a <em>responsive</em> enable
    /// predicate — one that is not hard-wired to a constant. We prove responsiveness by toggling
    /// every domain-enable flag on <see cref="Settings"/> and asserting each domain's
    /// <see cref="IOptimizationDomain.IsEnabled"/> differs between all-on and all-off. A domain that
    /// forgot to read its flag (always-on / always-off) fails here, catching the spiritual successor
    /// of the silent-no-op bug at registration time.
    /// </summary>
    [Fact]
    public void OptimizationDomains_HaveAResponsiveEnablePredicate()
    {
        using var provider = (ServiceProvider)BuildProvider();
        var domains = provider.GetServices<IOptimizationDomain>().ToArray();

        var allOff = AllDomainFlags(false);
        var allOn = AllDomainFlags(true);

        Assert.All(domains, d =>
            Assert.True(d.IsEnabled(allOn) != d.IsEnabled(allOff),
                $"Domain '{d.Id}' does not respond to any enable flag — its IsEnabled is hard-wired."));
    }

    /// <summary>A Settings with every domain-enable flag set to <paramref name="value"/>.</summary>
    private static Settings AllDomainFlags(bool value) => new()
    {
        EcoQosEnabled = value,
        TimerResolutionEnabled = value,
        BackgroundServicesEnabled = value,
        UsbSuspendEnabled = value,
        NetworkPowerEnabled = value,
        GpuPowerEnabled = value,
        CpuParkingEnabled = value,
        DiskCoalescingEnabled = value,
        WiFiOptimizerEnabled = value,
        ServicesManualEnabled = value,
        AutoOptimizeMemoryEnabled = value,
    };
}
