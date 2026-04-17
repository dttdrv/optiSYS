using Microsoft.UI.Xaml;

namespace OptiSYS.Services;

/// <summary>
/// Production <see cref="ITimerService"/> backed by <see cref="DispatcherTimer"/>.
/// Each <see cref="Start"/> spawns a new timer and returns a handle that stops it on dispose.
///
/// <para>
/// The constructor does NO dispatcher work, so the service can be constructed in contexts
/// without a live UI (e.g. DI container resolution during <see cref="App"/> startup or in
/// unit tests that only exercise the registration, never calling <see cref="Start"/>).
/// Actual dispatcher binding happens lazily when a caller invokes <see cref="Start"/>.
/// </para>
/// </summary>
public sealed class DispatcherTimerService : ITimerService
{
    public IDisposable Start(TimeSpan interval, Action tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => tick();
        timer.Start();

        return new TimerSubscription(timer);
    }

    /// <summary>
    /// Wraps a <see cref="DispatcherTimer"/> as an <see cref="IDisposable"/> so callers get
    /// lifetime-management semantics instead of having to hold the raw timer reference.
    /// </summary>
    private sealed class TimerSubscription : IDisposable
    {
        private DispatcherTimer? _timer;

        public TimerSubscription(DispatcherTimer timer) => _timer = timer;

        public void Dispose()
        {
            var t = _timer;
            _timer = null;
            t?.Stop();
        }
    }
}
