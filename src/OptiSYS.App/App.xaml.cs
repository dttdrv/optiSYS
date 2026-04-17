using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Services;

namespace OptiSYS;

/// <summary>
/// optiSYS — Unified Windows Optimization Suite.
/// WinUI 3 application entry point. Keeps <see cref="OnLaunched"/> intentionally skinny:
/// enable privileges → build DI container → run crash recovery → show window.
/// Each step is a single line so a reader can audit startup order at a glance.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The shell window. Exposed as static so child code (tests, diagnostics, tray icons)
    /// can reach it without walking the visual tree. Null before <see cref="OnLaunched"/>.
    /// </summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Step 1 — elevate privileges BEFORE any component construction. Services that capture
        // system state (working-set trim, ETW, native power queries) rely on these being on;
        // doing it here guarantees every later `new XxxService()` sees the enabled token.
        //
        // Return value is deliberately ignored: a partial failure (e.g. unprivileged standard
        // user) degrades gracefully — domains that need a missing privilege report
        // `IsSupported = false` and the UI disables their toggle. Aborting startup would be
        // user-hostile.
        _ = PrivilegeManager.EnableAllRequired();

        // Step 2 — build the DI container. Must happen before `new MainWindow()` because
        // MainWindow (and its pages) resolve their ViewModels through AppHost.Services.
        AppHost.Initialize();

        // Step 3 — roll back any optimizations that were active when we crashed. The
        // SnapshotStore persists baselines to disk, so a kill-9 / power-loss mid-optimize
        // leaves the system in a modified state. Calling this before the window appears
        // means the user sees a clean slate on startup.
        AppHost.Services.GetRequiredService<IOptimizationEngine>().TryCrashRecovery();

        // Step 4 — finally show the UI. MainWindow's ctor reads Settings for geometry and
        // resolves NavView / ContentFrame via the XAML-generated partial.
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
