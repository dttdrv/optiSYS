using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Services;

/// <summary>
/// Provides memory information using INativeBridge.
/// </summary>
public sealed class MemoryInfoService : IDisposable
{
    private readonly INativeBridge _native;
    private System.Threading.Timer? _timer;
    private bool _disposed;

    public MemoryInfo? CurrentInfo { get; private set; }
    public event Action<MemoryInfo>? Updated;

    public MemoryInfoService(INativeBridge native)
    {
        _native = native;
    }

    public void Start(int intervalSeconds = 2)
    {
        _timer = new System.Threading.Timer(_ => Refresh(), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(intervalSeconds));
    }

    public void Stop() { _timer?.Dispose(); _timer = null; }

    private void Refresh()
    {
        if (_disposed) return;

        if (_native.GetMemoryInfo(out var nativeInfo))
        {
            CurrentInfo = new MemoryInfo
            {
                TotalPhysicalBytes = nativeInfo.TotalPhysicalBytes,
                AvailablePhysicalBytes = nativeInfo.AvailablePhysicalBytes,
                CommittedBytes = nativeInfo.CommittedBytes,
                StandbyCacheBytes = nativeInfo.StandbyCacheBytes,
                ModifiedBytes = nativeInfo.ModifiedPageListBytes,
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
