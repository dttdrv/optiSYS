using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Models;

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
    private readonly IQuietAutomationService _automation;
    private readonly ITrayIconService _tray;
    private readonly IStartupRegistrationService _startup;
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
        Settings settings)
    {
        _battery = battery ?? throw new ArgumentNullException(nameof(battery));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _powerSourceMonitor = powerSourceMonitor ?? throw new ArgumentNullException(nameof(powerSourceMonitor));
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
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
        _battery.Start();
        _powerSourceMonitor.Start();
        // Startup begins on the WinUI thread. We keep that context through warm-up so the
        // first automation timer can bind to the UI dispatcher safely.
        await _memory.WarmUpAsync();
        await _automation.StartAsync();
        _startup.Apply(_settings.StartWithWindows);
        _battery.Updated += OnBatteryUpdated;
        _automation.StateChanged += OnAutomationStateChanged;
        _powerSourceMonitor.PowerSourceChanged += OnPowerSourceChanged;
        RefreshTraySnapshot();
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
        _powerSourceMonitor.PowerSourceChanged -= OnPowerSourceChanged;
        _powerSourceMonitor.Stop();
        _battery.Stop();
        _battery.Dispose();
        _memory.Dispose();
        _powerSourceMonitor.Dispose();
        _automation.Dispose();
    }

    private void OnBatteryUpdated(BatteryInfo info) => RefreshTraySnapshot();

    private void OnAutomationStateChanged() => RefreshTraySnapshot();

    private void RefreshTraySnapshot()
    {
        var memory = _memory.CurrentInfo;
        var battery = _battery.CurrentInfo;
        var batteryActive = _automation.GetActiveBatteryStatuses().Count > 0;
        var health = TrayHealthEvaluator.Evaluate(memory, battery, batteryActive, _settings.AutomationPaused);
        var score = _scoreCalculator.Evaluate(memory, battery, _automation.TotalFreedBytes);
        var preset = _settings.BatteryPreset == BatteryPreset.Saver ? "Saver" : "Recommended";
        var memoryPart = memory is null ? "memory warming up" : $"memory {memory.UsagePercent:0}%";
        var batteryPart = battery is null
            ? "battery warming up"
            : battery.IsOnBattery
                ? $"battery {battery.ChargePercent:0}%"
                : "plugged in";
        var automationPart = _settings.AutomationPaused ? "safe optimization paused" : "safe optimization";

        _tray.Update(new TraySnapshot
        {
            HealthState = health,
            Score = score,
            Tooltip = $"optiSYS - {memoryPart}, {batteryPart}, {automationPart}",
            BatteryPresetLabel = preset,
            AutomationPaused = _settings.AutomationPaused,
        });
    }
}
