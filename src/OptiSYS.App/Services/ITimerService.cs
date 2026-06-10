namespace OptiSYS.Services;

/// <summary>
/// UI-thread timer seam. Lets ViewModels schedule periodic work without referencing
/// <c>Microsoft.UI.Xaml.DispatcherTimer</c> directly — that dependency would make
/// VMs un-testable (DispatcherTimer requires a running dispatcher, absent in xUnit).
///
/// <para>
/// Each <see cref="Start"/> returns an <see cref="IDisposable"/> subscription handle.
/// Dispose stops that specific timer; this mirrors the <c>IObservable&lt;T&gt;.Subscribe</c>
/// shape so multiple cadences can coexist without ambiguity about "which timer to stop."
/// </para>
/// </summary>
public interface ITimerService
{
    /// <summary>
    /// Begin invoking <paramref name="tick"/> every <paramref name="interval"/>. Ticks
    /// arrive on the thread that called <see cref="Start"/> (the UI thread for VMs).
    /// </summary>
    /// <returns>Disposable handle — dispose to stop the timer.</returns>
    IDisposable Start(TimeSpan interval, Action tick);

    /// <summary>
    /// Like <see cref="Start"/>, but the delay before each tick is re-read from
    /// <paramref name="nextInterval"/> after the previous tick completes — for adaptive
    /// cadences (e.g. the memory watcher stretching its tick while the system is calm).
    /// One-shot re-arming also guarantees a slow tick can never overlap the next.
    /// </summary>
    /// <returns>Disposable handle — dispose to stop the timer.</returns>
    IDisposable StartAdaptive(Func<TimeSpan> nextInterval, Action tick);
}
