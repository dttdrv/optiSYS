using Microsoft.Extensions.DependencyInjection;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Domains.Memory;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using OptiSYS.Services;

namespace OptiSYS;

/// <summary>
/// Tiny static container that owns the single <see cref="IServiceProvider"/> for the app.
/// Kept static (not a proper <c>IHost</c>) because WinUI 3 apps don't get a generic host
/// out of the box and for a single-window desktop shell we don't need scopes or lifetimes
/// beyond "application singleton."
///
/// <para>
/// <b>Lifecycle:</b> <see cref="Initialize"/> must be called exactly once, from
/// <see cref="App.OnLaunched"/>, BEFORE constructing <c>MainWindow</c>.
/// </para>
///
/// <para>
/// <b>Lifetime choices:</b>
/// <list type="bullet">
///   <item>Everything stateful (engine, battery/memory services, settings) is singleton —
///         the app has one of each, and crash-recovery / persistence assumes identity.</item>
/// </list>
/// </para>
/// </summary>
public static class AppHost
{
    /// <summary>Resolved after <see cref="Initialize"/>; null until then.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// Build the container once at startup. Idempotent — calling twice rebuilds (useful for
    /// design-time scenarios; production only calls once from <c>App.OnLaunched</c>).
    /// </summary>
    public static void Initialize()
    {
        var sc = new ServiceCollection();
        ConfigureServices(sc);
        Services = sc.BuildServiceProvider(validateScopes: false);
    }

    /// <summary>
    /// Registers every application service. Split out from <see cref="Initialize"/> so
    /// tests exercise the SAME registrations (no mirror-class drift) while building their
    /// own isolated <see cref="IServiceProvider"/> — important for xUnit parallel tests.
    /// </summary>
    public static void ConfigureServices(IServiceCollection sc)
    {
        // ── Core singletons ───────────────────────────────────────────────────
        // Settings: self-persisting model with a static Load() factory. Register
        // via factory so DI calls it exactly once and caches; multiple instances
        // would corrupt on-disk state.
        sc.AddSingleton<Settings>(_ => Settings.Load());

        // Native Windows bridge: centralizes the P/Invoke implementation used by
        // telemetry and runtime optimizer domains.
        sc.AddSingleton<INativeBridge>(_ => NativeBridgeFactory.Create());

        sc.AddSingleton<SnapshotStore>();

        // Battery/memory info + optimizer:  register concrete as singleton, then
        // alias interface to the same instance via factory. This keeps consumers
        // that ask for the concrete type (e.g. UnifiedOptimizationEngine internals)
        // and interface consumers (ViewModels) on the SAME instance.
        sc.AddSingleton<BatteryInfoService>();
        sc.AddSingleton<IBatteryInfoService>(p => p.GetRequiredService<BatteryInfoService>());

        sc.AddSingleton<MemoryInfoService>();
        sc.AddSingleton<IMemoryInfoService>(p => p.GetRequiredService<MemoryInfoService>());

        sc.AddSingleton<MemoryOptimizer>();
        sc.AddSingleton<IMemoryOptimizer>(p => p.GetRequiredService<MemoryOptimizer>());

        // Register domains explicitly so the unified engine consumes a deterministic
        // DI-managed order rather than constructing its own private set.
        sc.AddSingleton<IOptimizationDomain, EcoQosDomain>();
        sc.AddSingleton<IOptimizationDomain, TimerResolutionDomain>();
        sc.AddSingleton<IOptimizationDomain, MemoryOptimizerDomain>();

        // The battery-category domains below are complete but ship gated OFF (their
        // Settings flags default false), so registering them only makes them reachable
        // as opt-in — ActivateCategory skips any whose IsDomainEnabled flag is false.
        // Apply iterates registration order; Revert iterates the reverse.
        sc.AddSingleton<IOptimizationDomain, BackgroundServiceDomain>();
        sc.AddSingleton<IOptimizationDomain, UsbSuspendDomain>();
        sc.AddSingleton<IOptimizationDomain, NetworkPowerDomain>();
        sc.AddSingleton<IOptimizationDomain, GpuPowerDomain>();
        sc.AddSingleton<IOptimizationDomain, CpuParkingDomain>();
        sc.AddSingleton<IOptimizationDomain, DiskIoCoalescingDomain>();

        // Engine + monitor: expose both concrete and interface aliases so startup/runtime
        // services and tests can resolve the same singleton instances.
        sc.AddSingleton<UnifiedOptimizationEngine>();
        sc.AddSingleton<IOptimizationEngine>(p => p.GetRequiredService<UnifiedOptimizationEngine>());
        sc.AddSingleton<PowerSourceMonitor>();
        sc.AddSingleton<OptiSYS.Core.Interfaces.IPowerSourceMonitor>(p => p.GetRequiredService<PowerSourceMonitor>());

        // ── App-layer seams ───────────────────────────────────────────────────
        sc.AddSingleton<AppRuntimeCoordinator>();
        sc.AddSingleton<IAppRuntimeCoordinator>(p => p.GetRequiredService<AppRuntimeCoordinator>());
        sc.AddSingleton<QuietAutomationService>();
        sc.AddSingleton<IQuietAutomationService>(p => p.GetRequiredService<QuietAutomationService>());
        sc.AddSingleton<TrayIconService>();
        sc.AddSingleton<ITrayIconService>(p => p.GetRequiredService<TrayIconService>());
        sc.AddSingleton<ITimerService, DispatcherTimerService>();
        sc.AddSingleton<IStartupRegistrationStore, CurrentUserStartupRegistrationStore>();
        sc.AddSingleton<IExecutablePathProvider, ProcessExecutablePathProvider>();
        sc.AddSingleton<StartupRegistrationService>();
        sc.AddSingleton<IStartupRegistrationService>(p => p.GetRequiredService<StartupRegistrationService>());
    }
}
