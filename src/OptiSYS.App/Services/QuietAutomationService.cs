using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;

namespace OptiSYS.Services;

public interface IQuietAutomationService : IDisposable
{
    event Action? StateChanged;

    string LastActivity { get; }
    DateTimeOffset? LastActivityAt { get; }
    bool IsCleanupRunning { get; }
    long TotalFreedBytes { get; }

    Task StartAsync();
    Task RunMemoryCleanupAsync();
    Task RunDeepCleanAsync();
    void SetBatteryPreset(BatteryPreset preset);
    void ApplyBatteryPreset();
    void SetAutomationPaused(bool paused);
    IReadOnlyList<DomainStatus> GetActiveBatteryStatuses();
}

public sealed class QuietAutomationService : IQuietAutomationService
{
    private readonly Settings _settings;
    private readonly IMemoryInfoService _memory;
    private readonly IMemoryOptimizer _optimizer;
    private readonly IOptimizationEngine _engine;
    private readonly ITimerService _timer;
    private readonly MemoryTrendPredictor _predictor;
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private static readonly TimeSpan HintInterval = TimeSpan.FromSeconds(30);
    private IDisposable? _memoryWatcher;
    private DateTimeOffset? _lastCleanupAt;
    private DateTimeOffset? _lastHintAt;
    private int _started;
    private bool _disposed;

    public event Action? StateChanged;

    public string LastActivity { get; private set; } = "Safe optimization is ready.";
    public DateTimeOffset? LastActivityAt { get; private set; }
    public bool IsCleanupRunning { get; private set; }
    public long TotalFreedBytes { get; private set; }

    public QuietAutomationService(
        Settings settings,
        IBatteryInfoService battery,
        IMemoryInfoService memory,
        IMemoryOptimizer optimizer,
        IOptimizationEngine engine,
        ITimerService timer)
        : this(settings, battery, memory, optimizer, engine, timer, predictor: null)
    {
    }

    // Test seam: injects a clock-controlled predictor. Internal so MS.DI never sees it and
    // always picks the public ctor above (which builds a default predictor).
    internal QuietAutomationService(
        Settings settings,
        IBatteryInfoService battery,
        IMemoryInfoService memory,
        IMemoryOptimizer optimizer,
        IOptimizationEngine engine,
        ITimerService timer,
        MemoryTrendPredictor? predictor)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _optimizer = optimizer ?? throw new ArgumentNullException(nameof(optimizer));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _predictor = predictor ?? new MemoryTrendPredictor();

        ArgumentNullException.ThrowIfNull(battery);
    }

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        ApplySafeDomainSettings();
        // AIO baked-in constants (no UI knobs anymore): memory auto-optimization and effectiveness
        // tracking are always on under the hood; the master switch governs the run state via pause.
        _settings.AutoOptimizeMemoryEnabled = true;
        _settings.EffectivenessTrackingEnabled = true;
        RefreshMemoryExclusions();
        if (!_settings.AutomationPaused)
            SetOptionalOptimizations(true);   // Wi-Fi + (admin) services tune-up are part of the AIO set
        _memoryWatcher = _timer.Start(
            TimeSpan.FromSeconds(Math.Max(1, _settings.MemoryCheckIntervalSeconds)),
            () => _ = EvaluateMemoryPressureAsync());
        Publish("Safe background optimization started.");
        return Task.CompletedTask;
    }

    public async Task RunMemoryCleanupAsync()
    {
        await RunCleanupCoreAsync(triggeredByThreshold: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicit, user-initiated one-shot maximum reclaim — runs the Aggressive (Max) pipeline
    /// regardless of the selected mode (full standby purge + system-wide working-set empty +
    /// page-combine). The automatic path uses the user's selected mode instead.
    /// </summary>
    public async Task RunDeepCleanAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!await _cleanupGate.WaitAsync(0).ConfigureAwait(false))
        {
            Publish("Safe cleanup is already running.");
            return;
        }

        try
        {
            IsCleanupRunning = true;
            RaiseStateChanged();
            RefreshMemoryExclusions();

            var result = await Task.Run(() => _optimizer.OptimizeAll(
                level: OptimizationLevel.Aggressive,
                cacheMaxPercent: 0,
                targetThresholdPercent: 0,   // explicit Deep Clean: full pipeline unconditionally (no threshold gate)
                accessedBitsDelayMs: 0,
                effectivenessTrackingEnabled: _settings.EffectivenessTrackingEnabled)).ConfigureAwait(false);

            _lastCleanupAt = DateTimeOffset.UtcNow;

            if (!result.Success)
            {
                Publish($"Deep clean skipped: {result.Message}");
                return;
            }

            if (result.FreedBytes > 0)
            {
                TotalFreedBytes += result.FreedBytes;
            }

            var freedDisplay = result.FreedBytes > 0
                ? OptimizationResult.FormatBytesStatic(result.FreedBytes)
                : "a lighter working set";
            Publish($"Deep clean finished; recovered {freedDisplay}.");
        }
        finally
        {
            IsCleanupRunning = false;
            _cleanupGate.Release();
            RaiseStateChanged();
        }
    }

    public void SetBatteryPreset(BatteryPreset preset)
    {
        if (_settings.BatteryPreset == preset)
        {
            return;
        }

        _settings.BatteryPreset = preset;
        ApplySafeDomainSettings();
        _settings.SaveDebounced();
        Publish($"{GetBatteryPresetLabel(preset)} safe runtime mode is ready.");
    }

    public void ApplyBatteryPreset()
    {
        ApplySafeDomainSettings();
        _settings.SaveDebounced();
        RevertActiveBatteryDomains();

        var result = _engine.ActivateCategory("Battery");
        Publish(result.Success
            ? $"{GetBatteryPresetLabel(_settings.BatteryPreset)} safe runtime mode is active."
            : $"Safe runtime mode skipped: {result.Message}");
    }

    public void SetAutomationPaused(bool paused)
    {
        if (_settings.AutomationPaused == paused)
        {
            return;
        }

        _settings.AutomationPaused = paused;
        _settings.SaveDebounced();
        SetOptionalOptimizations(!paused);   // revert Wi-Fi + services when paused, re-apply when resumed
        Publish(paused
            ? "Safe optimization is paused."
            : "Safe optimization resumed.");
    }

    // The AIO opt-in domains activated/reverted with the master switch. Each is a clean no-op when
    // unsupported (no Wi-Fi adapter; services-manual when unelevated), so this stays frictionless.
    private static readonly string[] OptionalDomainIds = ["wifi-optimizer", "services-manual"];

    /// <summary>
    /// Activate or revert the optional AIO domains alongside the master automatic-optimization
    /// state. Per-domain wrapped so one domain's hiccup can never disrupt the others or automation.
    /// </summary>
    private void SetOptionalOptimizations(bool enable)
    {
        foreach (var id in OptionalDomainIds)
        {
            try
            {
                if (enable) _engine.ActivateDomain(id);
                else _engine.RevertDomain(id);
            }
            catch (Exception ex)
            {
                Publish($"{id} {(enable ? "activation" : "revert")} skipped: {ex.Message}");
            }
        }
    }

    public IReadOnlyList<DomainStatus> GetActiveBatteryStatuses() =>
        _engine.GetAllStatuses()
            .Where(status =>
                status.Category.Equals("Battery", StringComparison.OrdinalIgnoreCase) &&
                status.IsActive)
            .ToArray();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _memoryWatcher?.Dispose();
        _memoryWatcher = null;
        _cleanupGate.Dispose();
    }

    private async Task EvaluateMemoryPressureAsync()
    {
        try
        {
            if (_settings.AutomationPaused || !_settings.AutoOptimizeMemoryEnabled)
            {
                return;
            }

            // Background-priority hint enumerates processes, so with the fast (~2s) dynamic tick we
            // run it on a slower ~30s cadence rather than every cycle — overhead stays negligible.
            if (_lastHintAt is null || DateTimeOffset.UtcNow - _lastHintAt.Value >= HintInterval)
            {
                HintBackgroundPrioritySafely();
                _lastHintAt = DateTimeOffset.UtcNow;
            }

            var info = _memory.GetCurrentMemoryInfo();
            if (info.TotalPhysicalBytes <= 0)
            {
                Publish("Memory telemetry is waiting for the first valid sample.");
                return;
            }

            // Feed the trend window every cycle (even during cooldown) so the slope stays accurate.
            _predictor.Observe(info.UsagePercent);

            // OOM prevention: at/above the critical mark, reclaim hard immediately at the full
            // (Aggressive) level and bypass the cooldown — a fast allocation burst (e.g. many large
            // processes) must not blow through the free-RAM buffer between spaced-out cleanups.
            if (info.UsagePercent >= _settings.MemoryCriticalThresholdPercent)
            {
                Publish($"Critical memory pressure ({info.UsagePercent:0}%); full reclaim.");
                await RunCleanupCoreAsync(triggeredByThreshold: true, forceLevel: OptimizationLevel.Aggressive).ConfigureAwait(false);
                return;
            }

            if (_lastCleanupAt is not null &&
                DateTimeOffset.UtcNow - _lastCleanupAt.Value < TimeSpan.FromSeconds(_settings.MemoryCooldownSeconds))
            {
                return;
            }

            if (info.UsagePercent >= _settings.MemoryThresholdPercent)
            {
                await RunCleanupCoreAsync(triggeredByThreshold: true).ConfigureAwait(false);
                return;
            }

            // Pre-emptive path: below the reactive threshold, but the usage trend is projected to
            // breach it shortly AND real commit demand is high (not just reclaimable cache filling).
            // Fires the same mode-level reclaim — just early — so pressure is headed off before it bites.
            var commitRatio = info.CommitLimitBytes > 0
                ? (double)info.CommitTotalBytes / info.CommitLimitBytes
                : 0;

            if (_predictor.ShouldPreemptivelyTrim(info.UsagePercent, commitRatio, _settings.MemoryThresholdPercent))
            {
                Publish($"Memory trending toward pressure ({info.UsagePercent:0}% used, {commitRatio * 100:0}% committed); trimming early.");
                await RunCleanupCoreAsync(triggeredByThreshold: true).ConfigureAwait(false);
                return;
            }

            Publish($"Memory sampled at {info.UsagePercent:0}% usage; no cleanup needed.");
        }
        catch (Exception ex)
        {
            Publish($"Memory watcher skipped a sample: {ex.Message}");
        }
    }

    private async Task RunCleanupCoreAsync(bool triggeredByThreshold, OptimizationLevel? forceLevel = null)
    {
        if (_settings.AutomationPaused && triggeredByThreshold)
        {
            return;
        }

        if (!await _cleanupGate.WaitAsync(0).ConfigureAwait(false))
        {
            Publish("Safe cleanup is already running.");
            return;
        }

        try
        {
            IsCleanupRunning = true;
            RaiseStateChanged();
            RefreshMemoryExclusions();

            // Automatic cleanup runs at the user's selected mode (Balanced or Max) — the full
            // optiRAM-parity pipeline, with adaptive escalation bailing early when a lighter pass
            // suffices. Gated by the threshold + cooldown so the heavy steps fire only under pressure.
            var result = await Task.Run(() => _optimizer.OptimizeAll(
                level: forceLevel ?? _settings.OptimizationLevel,
                cacheMaxPercent: 0,
                targetThresholdPercent: _settings.MemoryThresholdPercent,
                accessedBitsDelayMs: 0,
                effectivenessTrackingEnabled: _settings.EffectivenessTrackingEnabled)).ConfigureAwait(false);

            _lastCleanupAt = DateTimeOffset.UtcNow;

            if (!result.Success)
            {
                Publish($"Safe cleanup skipped: {result.Message}");
                return;
            }

            if (result.FreedBytes > 0)
            {
                TotalFreedBytes += result.FreedBytes;
            }

            var freedDisplay = result.FreedBytes > 0
                ? OptimizationResult.FormatBytesStatic(result.FreedBytes)
                : "a lighter working set";
            var triggerLabel = triggeredByThreshold ? "Automatic safe cleanup" : "Safe cleanup";
            Publish($"{triggerLabel} finished; recovered {freedDisplay}.");
        }
        finally
        {
            IsCleanupRunning = false;
            _cleanupGate.Release();
            RaiseStateChanged();
        }
    }

    private void RevertActiveBatteryDomains()
    {
        foreach (var status in GetActiveBatteryStatuses())
        {
            _engine.RevertDomain(status.DomainId);
        }
    }

    private void ApplySafeDomainSettings()
    {
        // EcoQoS + Timer Resolution are NOT force-enabled: they throttle ALL non-foreground
        // processes, so they are opt-in only (respect the user's setting; default off). This method
        // only keeps the heavier opt-in domains disabled until the user explicitly enables them.
        // CPU parking is the exception: an invisible, reversible plan internal (DC min processor
        // state -> 0 on battery), so it is force-enabled as part of the AIO set.
        _settings.BackgroundServicesEnabled = false;
        _settings.UsbSuspendEnabled = false;
        _settings.NetworkPowerEnabled = false;
        _settings.GpuPowerEnabled = false;
        _settings.CpuParkingEnabled = true;
        _settings.DiskCoalescingEnabled = false;
    }

    private void HintBackgroundPrioritySafely()
    {
        try
        {
            _optimizer.HintBackgroundMemoryPriority();
        }
        catch (Exception ex)
        {
            Publish($"Background memory hint skipped: {ex.Message}");
        }
    }

    private void RefreshMemoryExclusions()
    {
        // Match optiRAM: exclude only critical SYSTEM processes. The foreground app + self are
        // skipped inside TrimProcessWorkingSets, and trimming a background app just moves its pages
        // to standby (faulted back lazily) — so no user-managed "protected apps" list is needed.
        _optimizer.ExcludedProcesses = new HashSet<string>(
            Settings.CriticalProcessExclusions, StringComparer.OrdinalIgnoreCase);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();

    private static string GetBatteryPresetLabel(BatteryPreset preset) => preset switch
    {
        BatteryPreset.Saver => "Saver",
        _ => "Recommended",
    };

    private void Publish(string message)
    {
        LastActivity = message;
        LastActivityAt = DateTimeOffset.Now;
        RaiseStateChanged();
    }
}
