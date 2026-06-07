namespace OptiSYS.Core.Interfaces;

/// <summary>
/// The user's effective power mode as reported by Windows
/// (<c>EFFECTIVE_POWER_MODE</c>). <see cref="Unknown"/> is the graceful-degradation value used
/// when the OS API is unavailable (older Windows) or registration failed — callers must treat it
/// as "no signal, behave as if there were no provider."
/// </summary>
public enum EffectivePowerMode
{
    Unknown = -1,
    BatterySaver = 0,
    BetterBattery = 1,
    Balanced = 2,
    HighPerformance = 3,
    MaxPerformance = 4,
    GameMode = 5,
    MixedReality = 6,
}

/// <summary>
/// Read-only seam over the user's effective power mode. OptiSYS consumes this signal to
/// "follow, never fight" the user's explicit choice — it never writes the power mode.
/// The native registration lives behind this interface so the consuming decision logic is
/// fully unit-testable against a fake, and the thin interop stays isolated.
/// </summary>
public interface IEffectivePowerModeProvider : IDisposable
{
    /// <summary>The latest effective power mode, or <see cref="EffectivePowerMode.Unknown"/>
    /// when no signal is available.</summary>
    EffectivePowerMode Current { get; }

    /// <summary>Raised when <see cref="Current"/> changes. Invoked on an arbitrary thread (the OS
    /// notification callback) — subscribers that touch UI must marshal to the UI thread.</summary>
    event Action? Changed;

    /// <summary>Begins listening for effective-power-mode changes. No-op / degrades to
    /// <see cref="EffectivePowerMode.Unknown"/> when the OS API is unavailable.</summary>
    void Start();

    /// <summary>Stops listening and releases the OS registration.</summary>
    void Stop();
}

/// <summary>
/// The pure "follow, never fight" decision rule, isolated from the controller and any native call
/// so it is trivially unit-testable.
/// </summary>
public static class EffectivePowerModeDecision
{
    /// <summary>
    /// True when the user picked a high-performance effective mode where Windows opts apps OUT of
    /// throttling. OptiSYS must stand down its own EcoQoS throttling in these modes rather than
    /// fight the user's explicit choice. All battery-efficient / Balanced / Unknown modes return
    /// false (OptiSYS proceeds as normal).
    /// </summary>
    public static bool IsHighPerformance(EffectivePowerMode mode) => mode is
        EffectivePowerMode.HighPerformance or
        EffectivePowerMode.MaxPerformance or
        EffectivePowerMode.GameMode;
}
