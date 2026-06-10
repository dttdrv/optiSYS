using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Services;

/// <summary>Start/stop seam for the adaptive EcoQoS maintenance loop (mirrors IPowerSourceMonitor).</summary>
public interface IAdaptiveEcoQosController : IDisposable
{
    void Start();
    void Stop();
}

/// <summary>
/// Gates the EXPENSIVE full sweep, not the tick. The controller keeps ticking at the fixed fast
/// cadence doing only cheap reads (power source, effective power mode, foreground PID); this
/// policy decides which of those ticks runs the full process enumeration. Consecutive no-op
/// sweeps stretch the sweep gap geometrically (1 -> 2 -> 4 -> 8 -> capped ticks); a sweep that
/// did real work snaps the gap back, a change signal (foreground flip) forces a sweep on the
/// current tick, and Reset makes the next eligible tick sweep at once. Net effect: steady-state
/// enumeration cost drops ~10x while every user-facing transition — power flip, performance-mode
/// change, focus change — is still acted on within one base tick. Pure and clock-free.
/// </summary>
internal sealed class EcoQosCadencePolicy
{
    public enum Outcome
    {
        /// <summary>Tick bailed before any sweep decision (disabled / paused / inactive / on AC).</summary>
        Skipped,
        /// <summary>The tick changed something (sweep throttled/released, or a stand-down release).</summary>
        DidWork,
        /// <summary>An eligible sweep ran and changed nothing — the only gap-stretching signal.</summary>
        NoOp,
        /// <summary>Eligible cheap tick inside a stretched gap; the sweep was intentionally skipped.</summary>
        SweepDeferred,
    }

    private readonly int _maxGapTicks;
    private int _gapTicks = 1;
    private int _ticksSinceSweep;

    public EcoQosCadencePolicy(int maxGapTicks)
    {
        _maxGapTicks = Math.Max(1, maxGapTicks);
    }

    /// <summary>Current sweep gap in ticks (exposed for tests).</summary>
    public int GapTicks => _gapTicks;

    /// <summary>
    /// Decide whether this eligible tick runs the full sweep. A change signal forces the sweep
    /// immediately and snaps the gap back to every-tick.
    /// </summary>
    public bool ShouldSweep(bool changeSignal)
    {
        if (changeSignal)
            _gapTicks = 1;

        _ticksSinceSweep++;
        if (_ticksSinceSweep >= _gapTicks)
        {
            _ticksSinceSweep = 0;
            return true;
        }

        return false;
    }

    /// <summary>Feed back what the sweep found: real work keeps it fast, a no-op stretches the gap.</summary>
    public void RecordSweepOutcome(bool didWork) =>
        _gapTicks = didWork ? 1 : Math.Min(_gapTicks * 2, _maxGapTicks);

    public void Reset()
    {
        _gapTicks = 1;
        _ticksSinceSweep = 0;
    }
}

/// <summary>
/// Keeps the EcoQoS domain's desired state maintained while on battery: ticks at a fixed fast
/// cadence doing only cheap reads, and runs the full reconcile sweep on the adaptive gap decided
/// by <see cref="EcoQosCadencePolicy"/> — so the foreground app is always released within one
/// tick and newly-spawned background processes are caught by the next sweep. It only MAINTAINS —
/// initiating and reverting stay the engine's job on AC/DC transitions (via
/// <c>PowerSourceMonitor</c>), so there is a single owner of apply/revert.
/// </summary>
public sealed class AdaptiveEcoQosController : IAdaptiveEcoQosController
{
    private static readonly TimeSpan MaintainInterval = TimeSpan.FromSeconds(6);
    // 10 ticks x 6s = sweeps decay to once a minute in steady state; cheap ticks stay at 6s.
    private const int MaxSweepGapTicks = 10;
    // Idle deep-saver: after this much time without any user input (on battery), the sweep
    // widens from measured-drainers-only to all candidates. Short on purpose — the hint is
    // reversible within one tick of the user's return, so erring early costs nothing.
    private static readonly TimeSpan IdleDeepThreshold = TimeSpan.FromMinutes(3);

    private readonly EcoQosDomain _domain;
    private readonly INativeBridge _native;
    private readonly Settings _settings;
    private readonly IEffectivePowerModeProvider? _powerMode;
    private readonly EcoQosCadencePolicy _cadence = new(MaxSweepGapTicks);
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private int _lastForegroundPid;
    private bool _idleDeep;
    private bool _disposed;

    public AdaptiveEcoQosController(
        EcoQosDomain domain,
        INativeBridge native,
        Settings settings,
        IEffectivePowerModeProvider? powerMode = null)
    {
        _domain = domain ?? throw new ArgumentNullException(nameof(domain));
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        // Optional: when absent (API unavailable / not wired), the controller behaves exactly as
        // before — no effective-power-mode gating.
        _powerMode = powerMode;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer != null)
                return;
            _powerMode?.Start();
            // One-shot timer, re-armed at the fixed base interval after each tick completes —
            // one-shot guarantees a slow sweep can never overlap the next tick.
            _cadence.Reset();
            _timer = new System.Threading.Timer(
                _ => OnMaintainTick(), null, MaintainInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnMaintainTick()
    {
        MaintainOnce();
        lock (_gate)
        {
            // Null after Stop/Dispose raced this tick — the timer is gone; do not re-arm.
            _timer?.Change(MaintainInterval, Timeout.InfiniteTimeSpan);
        }
    }

    public void Stop()
    {
        System.Threading.Timer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
        _powerMode?.Stop();
    }

    internal EcoQosCadencePolicy.Outcome MaintainOnce()
    {
        if (_disposed)
            return EcoQosCadencePolicy.Outcome.Skipped;

        // Only maintain what is already active, opted-in, not paused, and on battery. The IsActive
        // gate is what keeps this from ever initiating — the engine owns the first apply (and the
        // matching crash-recovery snapshot) on the AC -> DC transition. Ineligible ticks reset the
        // sweep gap so the first eligible tick sweeps at once.
        if (!_settings.EcoQosEnabled || _settings.AutomationPaused || !_domain.IsActive)
        {
            _cadence.Reset();
            return EcoQosCadencePolicy.Outcome.Skipped;
        }

        if (_native.GetPowerSource() != PowerSource.Battery)
        {
            _cadence.Reset();
            return EcoQosCadencePolicy.Outcome.Skipped;
        }

        // Follow, never fight: when the user picked a high-performance effective mode
        // (HighPerformance / MaxPerformance / GameMode), Windows opts every app OUT of throttling.
        // Stand down — release anything we had throttled (reversible, back to OS-managed) and skip
        // reconcile so we never re-throttle background apps the user/OS wants at full speed.
        // Checked every tick (a cheap property read), so the stand-down lands within one tick.
        if (_powerMode is { } pm && EffectivePowerModeDecision.IsHighPerformance(pm.Current))
        {
            // The outer guard already established _domain.IsActive, so there is throttling to release.
            try { _domain.Revert(_domain.CaptureBaseline()); } catch { }
            _cadence.RecordSweepOutcome(didWork: true);
            return EcoQosCadencePolicy.Outcome.DidWork;
        }

        // Idle deep-saver: with the user away past the threshold there is nothing to keep
        // snappy, so the sweep widens to all candidates (audible/protected stay exempt — music
        // keeps playing). GetUserIdleTime returns zero on failure, so the safe reading is
        // always "user present". The mode flip in EITHER direction forces a sweep through any
        // stretched gap: savings start on the idle tick, and the first input back releases the
        // widened throttles within one tick.
        var idleDeep = _native.GetUserIdleTime() >= IdleDeepThreshold;
        var idleModeChanged = idleDeep != _idleDeep;
        _idleDeep = idleDeep;

        // Focus changes are user-facing (a just-focused app must be released promptly), so they
        // force the sweep through any stretched gap; otherwise the gap decides whether this tick
        // pays for a full process enumeration.
        var foregroundPid = _native.GetForegroundProcessId();
        var foregroundChanged = foregroundPid != _lastForegroundPid;
        _lastForegroundPid = foregroundPid;

        if (!_cadence.ShouldSweep(foregroundChanged || idleModeChanged))
            return EcoQosCadencePolicy.Outcome.SweepDeferred;

        try
        {
            var result = _domain.Reconcile(widenToAllCandidates: idleDeep);
            // "Work" includes adopting processes the OS already had throttled (ItemsSkipped also
            // counts releases) — slightly conservative: it errs toward sweeping more often.
            var didWork = result.ItemsOptimized > 0 || result.ItemsSkipped > 0;
            _cadence.RecordSweepOutcome(didWork);
            return didWork
                ? EcoQosCadencePolicy.Outcome.DidWork
                : EcoQosCadencePolicy.Outcome.NoOp;
        }
        catch
        {
            // A failing sweep yields no information about change; stretch the gap rather than
            // hammer a failing path every tick.
            _cadence.RecordSweepOutcome(didWork: false);
            return EcoQosCadencePolicy.Outcome.NoOp;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
