namespace OptiSYS.Core.Interfaces;

/// <summary>
/// Small lifecycle surface for the shared power-source monitor.
/// </summary>
public interface IPowerSourceMonitor : IDisposable
{
    void Start();
    void Stop();
}
