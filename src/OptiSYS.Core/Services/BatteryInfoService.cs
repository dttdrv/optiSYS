using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Services;

/// <summary>
/// Provides battery information using INativeBridge.
/// </summary>
public sealed class BatteryInfoService : IDisposable
{
    private readonly INativeBridge _native;
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
        _timer = new System.Threading.Timer(_ => Refresh(), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(intervalSeconds));
    }

    public void Stop() { _timer?.Dispose(); _timer = null; }

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
