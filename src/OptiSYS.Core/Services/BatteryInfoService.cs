using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Services;

/// <summary>
/// Provides battery information using INativeBridge.
/// </summary>
public sealed class BatteryInfoService : IBatteryInfoService
{
    private readonly INativeBridge _native;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private bool _disposed;

    public BatteryInfo? CurrentInfo { get; private set; }
    public event Action<BatteryInfo>? Updated;

    public BatteryInfoService(INativeBridge native)
    {
        _native = native;
    }

    public void Start(int intervalSeconds = 5)
    {
        lock (_gate)
        {
            if (_disposed || _timer != null)
                return;

            var dueTime = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
            _timer = new System.Threading.Timer(_ => Refresh(), null, dueTime, dueTime);
        }

        Refresh();
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

    private void Refresh()
    {
        if (_disposed) return;

        if (_native.GetBatteryInfo(out var nativeInfo))
        {
            CurrentInfo = new BatteryInfo
            {
                PowerSource = nativeInfo.PowerSource,
                HasBattery = nativeInfo.HasBattery,
                ChargePercent = nativeInfo.ChargePercent,
                DrainRateMilliwatts = nativeInfo.DrainRateMilliwatts,
                EstimatedTimeRemainingSeconds = nativeInfo.EstimatedTimeRemainingSeconds,
            };
            Updated?.Invoke(CurrentInfo);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
