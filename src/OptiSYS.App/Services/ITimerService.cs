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
}
