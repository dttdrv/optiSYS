using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Services;

/// <summary>
/// Monitors power source changes and applies only safe runtime battery optimizations.
/// </summary>
public sealed class PowerSourceMonitor : IPowerSourceMonitor
{
    private readonly INativeBridge _native;
    private readonly Settings _settings;
    private readonly IOptimizationEngine _engine;
    private readonly object _gate = new();
    private System.Threading.Timer? _pollTimer;
    private PowerSource _lastPowerSource;
    private bool _disposed;

    public event Action<PowerSource>? PowerSourceChanged;

    public PowerSource CurrentPowerSource => _native.GetPowerSource();

    public PowerSourceMonitor(INativeBridge native, Settings settings, IOptimizationEngine engine)
    {
        _native = native;
        _settings = settings;
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _lastPowerSource = _native.GetPowerSource();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _pollTimer != null)
                return;

            var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.DebouncePowerChangeSeconds));
            _pollTimer = new System.Threading.Timer(PollCallback, null, interval, interval);
        }
    }

    public void Stop()
    {
        System.Threading.Timer? timer;
        lock (_gate)
        {
            timer = _pollTimer;
            _pollTimer = null;
        }

        timer?.Dispose();
    }

    private void PollCallback(object? state)
    {
        if (_disposed) return;

        var current = _native.GetPowerSource();
        if (current != _lastPowerSource)
        {
            var previous = _lastPowerSource;
            _lastPowerSource = current;
            PowerSourceChanged?.Invoke(current);

            if (!_settings.AutoOptimizeOnBattery || _settings.AutomationPaused)
            {
                return;
            }

            if (current == PowerSource.Battery && previous == PowerSource.Ac)
            {
                _engine.ActivateCategory("Battery");
            }
            else if (current == PowerSource.Ac && previous == PowerSource.Battery)
            {
                // Symmetric with the AC->DC apply (ActivateCategory("Battery")): on AC, revert the
                // FULL Battery category — every active battery-category domain — not a hand-picked
                // subset. Otherwise opted-in persistent registry/power-scheme domains
                // (disk-coalescing, network-power, gpu-power) would stay applied for the whole AC
                // session until a clean exit. Reverse order mirrors the engine's RevertAll convention.
                foreach (var domain in _engine.Domains
                    .Where(d => d.IsActive &&
                                d.Category.Equals("Battery", StringComparison.OrdinalIgnoreCase))
                    .Reverse())
                {
                    _engine.RevertDomain(domain.Id);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
