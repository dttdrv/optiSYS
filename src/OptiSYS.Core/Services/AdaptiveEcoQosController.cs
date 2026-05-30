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
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private bool _disposed;

    public AdaptiveEcoQosController(EcoQosDomain domain, INativeBridge native, Settings settings)
    {
        _domain = domain ?? throw new ArgumentNullException(nameof(domain));
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _timer != null)
                return;
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

        try { _domain.Reconcile(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
