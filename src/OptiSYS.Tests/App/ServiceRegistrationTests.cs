using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.UI.Xaml;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using OptiSYS.Services;
using OptiSYS.ViewModels;
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

    /// <summary>
    /// Same wiring, but <see cref="ITimerService"/> is swapped for a no-op before the provider
    /// is built. Needed for tests that actually <b>resolve ViewModels</b>: Dashboard and Memory
    /// call <c>timer.Start(...)</c> in their ctors, and the real <see cref="DispatcherTimerService"/>
    /// instantiates a <see cref="DispatcherTimer"/> — which requires a live UI dispatcher the
    /// xUnit test thread doesn't have. The substitution keeps DI graph verification runnable
    /// outside a WinUI host without weakening the ITimerService-registration tests elsewhere.
    /// </summary>
    private static IServiceProvider BuildProviderForViewModelResolution()
    {
        var sc = new ServiceCollection();
        OptiSYS.AppHost.ConfigureServices(sc);
        // Replace (not add — RemoveAll first) so both the interface and the concrete slot swap.
        sc.RemoveAll<ITimerService>();
        sc.AddSingleton<ITimerService, NoOpTimerService>();
        return sc.BuildServiceProvider(validateScopes: false);
    }

    /// <summary>Timer stub that returns a dispose-only subscription without touching WinUI.</summary>
    private sealed class NoOpTimerService : ITimerService
    {
        public IDisposable Start(TimeSpan interval, Action tick) => new NoOpSubscription();
        private sealed class NoOpSubscription : IDisposable { public void Dispose() { } }
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
    [InlineData(typeof(ITimerService))]
    [InlineData(typeof(IProcessEnumerator))]
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
    [InlineData(typeof(ITimerService),       typeof(DispatcherTimerService))]
    [InlineData(typeof(IProcessEnumerator),  typeof(ProcessEnumerator))]
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
    [InlineData(typeof(ITimerService))]
    [InlineData(typeof(IProcessEnumerator))]
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
    public void InterfaceAlias_SharesInstanceWithConcreteRegistration(Type concreteType, Type interfaceType)
    {
        using var provider = (ServiceProvider)BuildProvider();
        var viaConcrete = provider.GetRequiredService(concreteType);
        var viaInterface = provider.GetRequiredService(interfaceType);
        Assert.Same(viaConcrete, viaInterface);
    }

    // ── ViewModels are transient (fresh instance per resolve) ────────────────────

    [Theory]
    [InlineData(typeof(DashboardViewModel))]
    [InlineData(typeof(BatteryViewModel))]
    [InlineData(typeof(MemoryViewModel))]
    [InlineData(typeof(ProcessesViewModel))]
    [InlineData(typeof(SettingsViewModel))]
    public void ViewModel_IsTransient(Type vmType)
    {
        // Uses the no-op-timer provider because Dashboard/Memory ctors call timer.Start,
        // which would instantiate a DispatcherTimer — illegal on a non-UI thread.
        using var provider = (ServiceProvider)BuildProviderForViewModelResolution();
        var first  = provider.GetRequiredService(vmType);
        var second = provider.GetRequiredService(vmType);
        Assert.NotSame(first, second);
    }

    // ── Sanity check: every ViewModel resolves without throwing ──────────────────

    [Fact]
    public void AllViewModels_ResolveWithoutError()
    {
        // See ViewModel_IsTransient for why we substitute ITimerService here.
        using var provider = (ServiceProvider)BuildProviderForViewModelResolution();
        Assert.NotNull(provider.GetRequiredService<DashboardViewModel>());
        Assert.NotNull(provider.GetRequiredService<BatteryViewModel>());
        Assert.NotNull(provider.GetRequiredService<MemoryViewModel>());
        Assert.NotNull(provider.GetRequiredService<ProcessesViewModel>());
        Assert.NotNull(provider.GetRequiredService<SettingsViewModel>());
    }
}
