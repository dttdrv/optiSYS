using OptiSYS.Core.Models;

namespace OptiSYS.Core.Interfaces;

/// <summary>
/// Small lifecycle surface for the shared power-source monitor.
/// </summary>
public interface IPowerSourceMonitor : IDisposable
{
    /// <summary>
    /// Raised when the machine transitions between AC and battery power. The argument is the
    /// new (current) <see cref="PowerSource"/>. App-layer consumers use this to auto-switch the
    /// efficiency profile.
    /// </summary>
    event Action<PowerSource>? PowerSourceChanged;

    /// <summary>The machine's current power source, read live.</summary>
    PowerSource CurrentPowerSource { get; }

    void Start();
    void Stop();
}
