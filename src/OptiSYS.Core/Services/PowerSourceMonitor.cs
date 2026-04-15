using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Services;

/// <summary>
/// Monitors power source changes and triggers battery optimization when switching to battery.
/// </summary>
public sealed class PowerSourceMonitor : IDisposable
{
    private readonly INativeBridge _native;
    private readonly Settings _settings;
    private readonly UnifiedOptimizationEngine _engine;
    private System.Threading.Timer? _pollTimer;
    private PowerSource _lastPowerSource;
    private bool _disposed;

    public event Action<PowerSource>? PowerSourceChanged;

    public PowerSourceMonitor(INativeBridge native, Settings settings, UnifiedOptimizationEngine engine)
    {
        _native = native;
        _settings = settings;
        _engine = engine;
        _lastPowerSource = _native.GetPowerSource();
    }

    public void Start()
    {
        _pollTimer = new System.Threading.Timer(PollCallback, null,
            TimeSpan.FromSeconds(_settings.DebouncePowerChangeSeconds),
            TimeSpan.FromSeconds(_settings.DebouncePowerChangeSeconds));
    }

    public void Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
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

            if (_settings.AutoOptimizeOnBattery)
            {
                if (current == PowerSource.Battery && previous == PowerSource.Ac)
                {
                    _engine.ActivateCategory("Battery");
                }
                else if (current == PowerSource.Ac && previous == PowerSource.Battery)
                {
                    _engine.RevertAll();
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
