using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using OptiSYS.Core.Interfaces;
using OptiSYS.Services;
using OptiSYS.Services.Elevation;

namespace OptiSYS;

/// <summary>
/// optiSYS — safe Windows runtime optimizer.
/// WinUI 3 application entry point. Keeps <see cref="OnLaunched"/> intentionally skinny:
/// build DI container → run crash recovery → create the minimal safe-mode window.
/// Each step is a single line so a reader can audit startup order at a glance.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The shell window. Exposed as static so child code (tests, diagnostics, tray icons)
    /// can reach it without walking the visual tree. Null before <see cref="OnLaunched"/>.
    /// </summary>
    public static Window? MainWindow { get; private set; }
    private IAppRuntimeCoordinator? _runtimeCoordinator;

    public App()
    {
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        StartupLog.Write("App ctor: handlers attached");
        InitializeComponent();
        StartupLog.Write("App ctor: InitializeComponent completed");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            StartupLog.Write("OnLaunched: entered");

            // Step 0 — elevated provisioning child. When launched via runas with
            // --provision-elevation, we hold an elevated token: register the logon task and
            // exit immediately, before any DI/window work (this instance never shows UI).
            if (IsProvisionElevationLaunch(Environment.GetCommandLineArgs()))
            {
                var created = new TaskSchedulerService().CreateOrUpdateTask();
                StartupLog.Write($"OnLaunched: --provision-elevation handled, taskCreated={created}; exiting");
                Environment.Exit(0);
                return;
            }

            // Step 1 — build the DI container. Must happen before `new MainWindow()` because
            // the shell resolves its application services through AppHost.Services.
            AppHost.Initialize();
            StartupLog.Write("OnLaunched: AppHost initialized");

            // Step 2 — roll back any optimizations that were active when we crashed. The
            // SnapshotStore persists baselines to disk, so a kill-9 / power-loss mid-optimize
            // leaves the system in a modified state. Calling this before the window appears
            // means the user sees a clean slate on startup.
            AppHost.Services.GetRequiredService<IOptimizationEngine>().TryCrashRecovery();
            StartupLog.Write("OnLaunched: crash recovery completed");

            // Step 3 — create the UI host before starting timer-backed runtime services. WinUI's
            // dispatcher-bound timers need a live window/dispatcher lifetime, so we activate
            // the shell only for foreground launches, then finish runtime startup on that UI context.
            //
            // MainWindow's ctor reads Settings for geometry and wires the native tray shell.
            MainWindow = new MainWindow();
            StartupLog.Write($"OnLaunched: window constructed type={MainWindow.GetType().FullName}");
            MainWindow.Closed += (_, _) =>
            {
                StartupLog.Write("MainWindow: Closed event");
                _runtimeCoordinator?.Dispose();
            };
            var backgroundLaunch = IsBackgroundLaunch(args.Arguments, Environment.GetCommandLineArgs());

            if (MainWindow is global::OptiSYS.MainWindow shell && backgroundLaunch)
            {
                shell.LaunchInBackground();
                StartupLog.Write("OnLaunched: background launch requested; window activation skipped");
            }
            else
            {
                MainWindow.Activate();
                StartupLog.Write("OnLaunched: window activated");
            }

            _ = StartRuntimeAfterWindowAsync(AppHost.Services);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("OnLaunched", ex);
            throw;
        }
    }

    public static IAppRuntimeCoordinator InitializeRuntime(IServiceProvider services)
        => InitializeRuntimeAsync(services).GetAwaiter().GetResult();

    internal static bool IsBackgroundLaunch(string activationArguments, IReadOnlyList<string> commandLineArguments) =>
        activationArguments.Contains("--background", StringComparison.OrdinalIgnoreCase) ||
        commandLineArguments.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));

    internal static bool IsProvisionElevationLaunch(IReadOnlyList<string> commandLineArguments) =>
        commandLineArguments.Any(arg =>
            string.Equals(arg, ElevationHelper.ProvisionArgument, StringComparison.OrdinalIgnoreCase));

    internal static async Task<IAppRuntimeCoordinator> InitializeRuntimeAsync(IServiceProvider services)
    {
        var coordinator = services.GetRequiredService<IAppRuntimeCoordinator>();
        await coordinator.StartAsync();
        return coordinator;
    }

    private async Task StartRuntimeAfterWindowAsync(IServiceProvider services)
    {
        try
        {
            _runtimeCoordinator = await InitializeRuntimeAsync(services);
            StartupLog.Write("OnLaunched: runtime coordinator started");
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("StartRuntimeAfterWindowAsync", ex);
            throw;
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        StartupLog.WriteException("Application.UnhandledException", e.Exception);

        if (e.Exception is InvalidCastException &&
            e.Exception.Message.Contains("No such interface supported", StringComparison.OrdinalIgnoreCase))
        {
            e.Handled = true;
            StartupLog.Write("Application.UnhandledException: handled WinUI activation InvalidCastException");
        }
    }

    private void OnCurrentDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            StartupLog.WriteException("AppDomain.CurrentDomain.UnhandledException", ex);
        }
        else
        {
            StartupLog.Write($"AppDomain.CurrentDomain.UnhandledException: non-Exception object={e.ExceptionObject}");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        StartupLog.WriteException("TaskScheduler.UnobservedTaskException", e.Exception);
    }
}
