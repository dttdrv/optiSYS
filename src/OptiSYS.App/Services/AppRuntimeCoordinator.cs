using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using OptiSYS.Models;
using OptiSYS.Services.Elevation;

namespace OptiSYS.Services;

/// <summary>
/// Coordinates one-time startup for process-wide services that should outlive page navigation.
/// </summary>
public interface IAppRuntimeCoordinator : IDisposable
{
    Task StartAsync();
}

public sealed class AppRuntimeCoordinator : IAppRuntimeCoordinator
{
    private readonly IBatteryInfoService _battery;
    private readonly IMemoryInfoService _memory;
    private readonly OptiSYS.Core.Interfaces.IPowerSourceMonitor _powerSourceMonitor;
    private readonly IAdaptiveEcoQosController _adaptiveEcoQos;
    private readonly IQuietAutomationService _automation;
    private readonly ITrayIconService _tray;
    private readonly IStartupRegistrationService _startup;
    private readonly ITaskSchedulerService _taskScheduler;
    private readonly IOptimizationEngine _engine;
    private readonly Settings _settings;
    private readonly HealthScoreCalculator _scoreCalculator = new();
    private readonly object _startGate = new();
    private Task? _startTask;
    private bool _disposed;

    public AppRuntimeCoordinator(
        IBatteryInfoService battery,
        IMemoryInfoService memory,
        OptiSYS.Core.Interfaces.IPowerSourceMonitor powerSourceMonitor,
        IQuietAutomationService automation,
        ITrayIconService tray,
        IStartupRegistrationService startup,
        ITaskSchedulerService taskScheduler,
        Settings settings,
        IAdaptiveEcoQosController adaptiveEcoQos,
        IOptimizationEngine engine)
    {
        _battery = battery ?? throw new ArgumentNullException(nameof(battery));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _powerSourceMonitor = powerSourceMonitor ?? throw new ArgumentNullException(nameof(powerSourceMonitor));
        _adaptiveEcoQos = adaptiveEcoQos ?? throw new ArgumentNullException(nameof(adaptiveEcoQos));
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _taskScheduler = taskScheduler ?? throw new ArgumentNullException(nameof(taskScheduler));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public Task StartAsync()
    {
        lock (_startGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _startTask ??= StartCoreAsync();
            return _startTask;
        }
    }

    private async Task StartCoreAsync()
    {
        // Per-step breadcrumbs: if startup stalls or automation fails to start, the on-disk log
        // localizes the failing step instead of going silent (the "app disappears" triage signal).
        StartupLog.Write("runtime: battery start");
        _battery.Start();
        StartupLog.Write("runtime: power-source monitor start");
        _powerSourceMonitor.Start();
        // Booting while already on battery never produces an AC->DC transition, so apply the Battery
        // category once at startup. The engine's own enabled/active/in-progress guards keep this from
        // double-applying when a later real transition fires.
        if (_powerSourceMonitor.CurrentPowerSource == PowerSource.Battery &&
            _settings.AutoOptimizeOnBattery && !_settings.AutomationPaused)
        {
            _engine.ActivateCategory("Battery");
        }
        StartupLog.Write("runtime: adaptive ecoqos start");
        _adaptiveEcoQos.Start();
        // Startup begins on the WinUI thread. We keep that context through warm-up so the
        // first automation timer can bind to the UI dispatcher safely.
        StartupLog.Write("runtime: memory warm-up");
        await _memory.WarmUpAsync();
        StartupLog.Write("runtime: automation start");
        await _automation.StartAsync();
        ConfigureAutostartBackend();
        _battery.Updated += OnBatteryUpdated;
        _automation.StateChanged += OnAutomationStateChanged;
        _automation.MemorySampled += OnMemorySampled;
        _powerSourceMonitor.PowerSourceChanged += OnPowerSourceChanged;
        RefreshTraySnapshot();
    }

    /// <summary>
    /// Selects the autostart backend. Default (<see cref="Settings.UseTaskScheduler"/> = false)
    /// is the plain HKCU Run-key path — unchanged from before. When the elevated-logon mode is
    /// enabled, the Task Scheduler task IS the autostart, so the Run key is removed (no double
    /// launch); if the task is missing/stale we self-heal silently when already elevated, or
    /// flag <see cref="Settings.ElevationPending"/> for the UI banner when we are not (never
    /// prompts for UAC at boot). The task is left untouched on the off-path to keep the common
    /// startup cheap — disabling the mode deletes it at the toggle, not here.
    /// </summary>
    private void ConfigureAutostartBackend()
    {
        if (!_settings.UseTaskScheduler)
        {
            _startup.Apply(_settings.StartWithWindows);
            return;
        }

        _startup.Apply(false);

        if (!_taskScheduler.NeedsProvisioning())
        {
            _settings.ElevationPending = false;
            return;
        }

        if (_taskScheduler.IsElevated)
        {
            _taskScheduler.CreateOrUpdateTask();
            _settings.ElevationPending = false;
        }
        else
        {
            _settings.ElevationPending = true;
        }
    }

    /// <summary>
    /// Auto-switches the efficiency profile on a power-source transition: on battery (DC) selects
    /// the Saver preset, plugged in (AC) selects Recommended. Memory mode (OptimizationLevel) is
    /// intentionally left untouched — it stays sticky/user-set. <see cref="IQuietAutomationService.SetBatteryPreset"/>
    /// is a no-op when the preset is unchanged (so AC→AC / DC→DC re-polls cause no churn) and is
    /// responsible for applying the safe domain settings, persisting, and raising StateChanged.
    /// </summary>
    private void OnPowerSourceChanged(PowerSource source)
    {
        var preset = source == PowerSource.Battery ? BatteryPreset.Saver : BatteryPreset.Recommended;
        _automation.SetBatteryPreset(preset);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _battery.Updated -= OnBatteryUpdated;
        _automation.StateChanged -= OnAutomationStateChanged;
        _automation.MemorySampled -= OnMemorySampled;
        _powerSourceMonitor.PowerSourceChanged -= OnPowerSourceChanged;
        _powerSourceMonitor.Stop();
        _adaptiveEcoQos.Dispose();
        _battery.Stop();
        _battery.Dispose();
        _memory.Dispose();
        _powerSourceMonitor.Dispose();
        _automation.Dispose();
    }

    private void OnBatteryUpdated(BatteryInfo info) => RefreshTraySnapshot();

    private void OnAutomationStateChanged() => RefreshTraySnapshot();

    // The memory-% number rides the watcher's sampling cadence so it never sits stale between the
    // sparse battery/automation events (no dedicated timer is added for this).
    private void OnMemorySampled() => RefreshTraySnapshot();

    private void RefreshTraySnapshot()
    {
        var memory = _memory.CurrentInfo;
        var battery = _battery.CurrentInfo;
        var batteryActive = _automation.GetActiveBatteryStatuses().Count > 0;
        var health = TrayHealthEvaluator.Evaluate(memory, battery, batteryActive, _settings.AutomationPaused);
        var score = _scoreCalculator.Evaluate(memory, battery, _automation.TotalFreedBytes);
        var preset = _settings.BatteryPreset == BatteryPreset.Saver ? "Saver" : "Recommended";

        // Icon number is memory usage percent; tooltip carries battery draw + efficiency.
        var number = TrayHealthEvaluator.MemoryDisplayNumber(memory);
        var tooltip = TrayHealthEvaluator.BatteryTooltip(battery, health);

        _tray.Update(new TraySnapshot
        {
            HealthState = health,
            Score = score,
            DisplayNumber = number,
            Tooltip = tooltip,
            BatteryPresetLabel = preset,
            AutomationPaused = _settings.AutomationPaused,
        });
    }
}
