using OptiSYS.Core.Models;

namespace OptiSYS.Core.Interfaces;

/// <summary>
/// Minimal surface over <see cref="Services.BatteryInfoService"/> — exposes only what
/// ViewModels consume so test doubles (Moq) stay tiny.
///
/// <para>
/// The service runs a background timer: <see cref="Start"/> kicks it off, each tick
/// refreshes <see cref="CurrentInfo"/> and raises <see cref="Updated"/>. VMs bind to
/// <see cref="CurrentInfo"/> directly and/or subscribe to <see cref="Updated"/>.
/// </para>
/// </summary>
public interface IBatteryInfoService : IDisposable
{
    /// <summary>Last successfully polled battery snapshot, or null before first tick.</summary>
    BatteryInfo? CurrentInfo { get; }

    /// <summary>Fires on each successful poll — subscribers get the fresh snapshot.</summary>
    event Action<BatteryInfo>? Updated;

    /// <summary>Begin polling every <paramref name="intervalSeconds"/> seconds (default 5s).</summary>
    void Start(int intervalSeconds = 5);

    /// <summary>Stop the polling timer. <see cref="CurrentInfo"/> retains its last value.</summary>
    void Stop();
}
