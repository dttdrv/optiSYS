using Microsoft.Extensions.DependencyInjection;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using OptiSYS.Services;
using OptiSYS.ViewModels;

namespace OptiSYS;

/// <summary>
/// Tiny static container that owns the single <see cref="IServiceProvider"/> for the app.
/// Kept static (not a proper <c>IHost</c>) because WinUI 3 apps don't get a generic host
/// out of the box and for a single-window desktop shell we don't need scopes or lifetimes
/// beyond "application singleton" vs "per-navigation transient."
///
/// <para>
/// <b>Lifecycle:</b> <see cref="Initialize"/> must be called exactly once, from
/// <see cref="App.OnLaunched"/>, BEFORE constructing <c>MainWindow</c>. Pages resolve their
/// ViewModel in the code-behind ctor via <see cref="Services"/>.
/// <see cref="GetRequiredService{T}"/>.
/// </para>
///
/// <para>
/// <b>Lifetime choices:</b>
/// <list type="bullet">
///   <item>Everything stateful (engine, battery/memory services, settings) is singleton —
///         the app has one of each, and crash-recovery / persistence assumes identity.</item>
///   <item>ViewModels are transient so each navigation gets a fresh VM with fresh timers;
///         re-visiting a page doesn't resurrect stale state.</item>
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

        // Native bridge: picks Zig-or-managed at runtime; factory keeps that logic
        // in one place and out of the container config.
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

        // Engine + monitor:  PowerSourceMonitor takes a concrete UnifiedOptimizationEngine
        // in its ctor, so we register the concrete type as the "root of truth" and expose
        // IOptimizationEngine as an alias factory.
        sc.AddSingleton<UnifiedOptimizationEngine>();
        sc.AddSingleton<IOptimizationEngine>(p => p.GetRequiredService<UnifiedOptimizationEngine>());
        sc.AddSingleton<PowerSourceMonitor>();

        // ── App-layer seams ───────────────────────────────────────────────────
        sc.AddSingleton<ITimerService, DispatcherTimerService>();
        sc.AddSingleton<IProcessEnumerator, ProcessEnumerator>();

        // ── ViewModels (transient — fresh instance per navigation) ────────────
        sc.AddTransient<DashboardViewModel>();
        sc.AddTransient<BatteryViewModel>();
        sc.AddTransient<MemoryViewModel>();
        sc.AddTransient<ProcessesViewModel>();
        sc.AddTransient<SettingsViewModel>();
    }
}
