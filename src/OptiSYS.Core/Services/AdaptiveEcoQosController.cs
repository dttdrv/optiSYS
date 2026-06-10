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
/// Adaptive back-off for the maintenance cadence. Only an eligible-but-no-op sweep — a full
/// process enumeration that changed nothing — doubles the next interval (up to the cap): in
/// steady state on battery the sweep decays from 6s to once a minute. Anything that did real
/// work snaps back to the base cadence so bursts of change are tracked closely, and skipped
/// (ineligible) ticks also stay at base because they cost a couple of reads — keeping power
/// flips detected within one base interval. Pure and clock-free, so fully unit-testable.
/// </summary>
internal sealed class EcoQosCadencePolicy
{
    public enum Outcome
    {
        /// <summary>Tick bailed before the sweep (disabled / paused / inactive / on AC).</summary>
        Skipped,
        /// <summary>The sweep changed something (throttled, released, or newly tracked).</summary>
        DidWork,
        /// <summary>An eligible sweep ran and changed nothing — the only back-off signal.</summary>
        NoOp,
    }

    private readonly TimeSpan _baseInterval;
    private readonly TimeSpan _maxInterval;

    public EcoQosCadencePolicy(TimeSpan baseInterval, TimeSpan maxInterval)
    {
        _baseInterval = baseInterval;
        _maxInterval = maxInterval;
        Current = baseInterval;
    }

    public TimeSpan Current { get; private set; }

    public TimeSpan Next(Outcome outcome)
    {
        Current = outcome == Outcome.NoOp
            ? TimeSpan.FromTicks(Math.Min(Current.Ticks * 2, _maxInterval.Ticks))
            : _baseInterval;
        return Current;
    }

    public void Reset() => Current = _baseInterval;
}

/// <summary>
/// Keeps the EcoQoS domain's desired state maintained while on battery: re-runs the domain's
/// reconcile on an adaptive cadence (see <see cref="EcoQosCadencePolicy"/>) so the foreground app
/// is always released and newly-spawned background processes are caught. It only MAINTAINS —
/// initiating and reverting stay the engine's job on AC/DC transitions (via
/// <c>PowerSourceMonitor</c>), so there is a single owner of apply/revert.
/// </summary>
public sealed class AdaptiveEcoQosController : IAdaptiveEcoQosController
{
    private static readonly TimeSpan MaintainInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan MaxMaintainInterval = TimeSpan.FromSeconds(60);

    private readonly EcoQosDomain _domain;
    private readonly INativeBridge _native;
    private readonly Settings _settings;
    private readonly IEffectivePowerModeProvider? _powerMode;
    private readonly EcoQosCadencePolicy _cadence = new(MaintainInterval, MaxMaintainInterval);
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
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
            // One-shot timer, re-armed after each tick with the policy's next interval. One-shot
            // also guarantees a slow sweep can never overlap the next tick.
            _cadence.Reset();
            _timer = new System.Threading.Timer(
                _ => OnMaintainTick(), null, _cadence.Current, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnMaintainTick()
    {
        var next = _cadence.Next(MaintainOnce());
        lock (_gate)
        {
            // Null after Stop/Dispose raced this tick — the timer is gone; do not re-arm.
            _timer?.Change(next, Timeout.InfiniteTimeSpan);
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
        // matching crash-recovery snapshot) on the AC -> DC transition.
        if (!_settings.EcoQosEnabled || _settings.AutomationPaused || !_domain.IsActive)
            return EcoQosCadencePolicy.Outcome.Skipped;

        if (_native.GetPowerSource() != PowerSource.Battery)
            return EcoQosCadencePolicy.Outcome.Skipped;

        // Follow, never fight: when the user picked a high-performance effective mode
        // (HighPerformance / MaxPerformance / GameMode), Windows opts every app OUT of throttling.
        // Stand down — release anything we had throttled (reversible, back to OS-managed) and skip
        // reconcile so we never re-throttle background apps the user/OS wants at full speed.
        if (_powerMode is { } pm && EffectivePowerModeDecision.IsHighPerformance(pm.Current))
        {
            // The outer guard already established _domain.IsActive, so there is throttling to release.
            try { _domain.Revert(_domain.CaptureBaseline()); } catch { }
            return EcoQosCadencePolicy.Outcome.DidWork;
        }

        try
        {
            var result = _domain.Reconcile();
            return result.ItemsOptimized > 0 || result.ItemsSkipped > 0
                ? EcoQosCadencePolicy.Outcome.DidWork
                : EcoQosCadencePolicy.Outcome.NoOp;
        }
        catch
        {
            // A failing sweep yields no information about change; back off rather than hammer it.
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
