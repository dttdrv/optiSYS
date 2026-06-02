using System;
using System.Threading;

namespace OptiSYS.Services;

/// <summary>
/// <see cref="ITimerService"/> backed by a <see cref="System.Threading.Timer"/> (threadpool),
/// NOT a UI <c>DispatcherTimer</c>.
///
/// <para>
/// <b>Why this exists:</b> the memory watcher must run even on a background/logon launch
/// (<c>--background</c>), where the window is never activated. A <c>DispatcherTimer</c> only ticks
/// while a UI message pump is processing its queue, so on the hidden-window autostart path it
/// silently never fires — the watcher appeared "not running at all" until the user opened the
/// window and pressed the button. A threadpool timer ticks regardless of window/dispatcher state
/// (the same pattern <c>BatteryInfoService</c> already uses), and the watcher's heavy work is
/// already pushed to <c>Task.Run</c> while UI updates marshal via <c>DispatcherQueue.TryEnqueue</c>.
/// </para>
/// </summary>
public sealed class ThreadPoolTimerService : ITimerService
{
    public IDisposable Start(TimeSpan interval, Action tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        var subscription = new TimerSubscription();
        var timer = new Timer(_ =>
        {
            try
            {
                tick();
            }
            catch (Exception ex)
            {
                StartupLog.WriteException("ThreadPoolTimerService.Tick", ex);
            }
        }, null, interval, interval);

        subscription.Bind(timer);
        StartupLog.Write($"ThreadPoolTimerService: Started interval={interval}");
        return subscription;
    }

    private sealed class TimerSubscription : IDisposable
    {
        private Timer? _timer;

        public void Bind(Timer timer) => _timer = timer;

        public void Dispose()
        {
            var t = _timer;
            _timer = null;
            t?.Dispose();
        }
    }
}
