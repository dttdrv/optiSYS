namespace OptiSYS.Core.Interfaces;

/// <summary>
/// Minimal internal diagnostic sink. Not a user-facing surface — it exists so the engine and
/// runtime services can route significant events (apply/revert failures, crash-recovery
/// outcomes) to the single on-disk log the App owns. Kept to one method so any Core consumer
/// can take it as an optional dependency without pulling in a logging framework.
/// </summary>
public interface IDiagnosticLog
{
    /// <param name="level">Severity tag, e.g. "info", "warn", "error".</param>
    /// <param name="category">Originating component, e.g. "engine".</param>
    /// <param name="message">Human-readable line.</param>
    void Write(string level, string category, string message);
}

/// <summary>
/// No-op default so Core consumers (and tests) can construct the engine without a sink.
/// </summary>
public sealed class NullDiagnosticLog : IDiagnosticLog
{
    public static readonly NullDiagnosticLog Instance = new();

    private NullDiagnosticLog() { }

    public void Write(string level, string category, string message) { }
}
