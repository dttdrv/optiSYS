using OptiSYS.Core.Models;

namespace OptiSYS.Core.Interfaces;

/// <summary>
/// Minimal surface over <see cref="Services.MemoryInfoService"/>.
///
/// <para>
/// Unlike <see cref="IBatteryInfoService"/> this type does NOT expose an
/// <c>Updated</c> event — the concrete class declares one but never fires it
/// (there is no internal timer). ViewModels poll via <c>ITimerService</c> +
/// <see cref="GetCurrentMemoryInfo"/> instead; omitting the event from the
/// interface keeps tests honest about where the work actually happens.
/// </para>
///
/// <para>
/// <see cref="CurrentInfo"/> is kept in the surface as a convenience cache —
/// the concrete service populates it on each <see cref="GetCurrentMemoryInfo"/>
/// call; it stays null until the first call.
/// </para>
/// </summary>
public interface IMemoryInfoService : IDisposable
{
    /// <summary>Most recently fetched snapshot, or null before first call.</summary>
    MemoryInfo? CurrentInfo { get; }

    /// <summary>Synchronously collects a fresh memory snapshot via native APIs + perf counters.</summary>
    MemoryInfo GetCurrentMemoryInfo();

    /// <summary>Primes performance counters in the background. Call once at startup.</summary>
    Task WarmUpAsync();
}
