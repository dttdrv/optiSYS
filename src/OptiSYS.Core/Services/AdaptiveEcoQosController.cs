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
/// Keeps the EcoQoS domain's desired state maintained while on battery: re-runs the domain's
/// reconcile on a cadence so the foreground app is always released and newly-spawned background
/// processes are caught. It only MAINTAINS — initiating and reverting stay the engine's job on
/// AC/DC transitions (via <c>PowerSourceMonitor</c>), so there is a single owner of apply/revert.
/// </summary>
public sealed class AdaptiveEcoQosController : IAdaptiveEcoQosController
{
    private static readonly TimeSpan MaintainInterval = TimeSpan.FromSeconds(6);

    private readonly EcoQosDomain _domain;
    private readonly INativeBridge _native;
    private readonly Settings _settings;
    private readonly IEffectivePowerModeProvider? _powerMode;
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
            _timer = new System.Threading.Timer(_ => MaintainOnce(), null, MaintainInterval, MaintainInterval);
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

    internal void MaintainOnce()
    {
        if (_disposed)
            return;

        // Only maintain what is already active, opted-in, not paused, and on battery. The IsActive
        // gate is what keeps this from ever initiating — the engine owns the first apply (and the
        // matching crash-recovery snapshot) on the AC -> DC transition.
        if (!_settings.EcoQosEnabled || _settings.AutomationPaused || !_domain.IsActive)
            return;

        if (_native.GetPowerSource() != PowerSource.Battery)
            return;

        // Follow, never fight: when the user picked a high-performance effective mode
        // (HighPerformance / MaxPerformance / GameMode), Windows opts every app OUT of throttling.
        // Stand down — release anything we had throttled (reversible, back to OS-managed) and skip
        // reconcile so we never re-throttle background apps the user/OS wants at full speed.
        if (_powerMode is { } pm && EffectivePowerModeDecision.IsHighPerformance(pm.Current))
        {
            // The outer guard already established _domain.IsActive, so there is throttling to release.
            try { _domain.Revert(_domain.CaptureBaseline()); } catch { }
            return;
        }

        try { _domain.Reconcile(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
