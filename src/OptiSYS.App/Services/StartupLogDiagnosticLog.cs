using OptiSYS.Core.Interfaces;

namespace OptiSYS.Services;

/// <summary>
/// File-backed <see cref="IDiagnosticLog"/> that routes Core diagnostics into the SAME on-disk
/// sink as <see cref="StartupLog"/>, so engine apply/revert failures and crash-recovery outcomes
/// land in the single startup.log alongside the App's own breadcrumbs.
/// </summary>
internal sealed class StartupLogDiagnosticLog : IDiagnosticLog
{
    public void Write(string level, string category, string message) =>
        StartupLog.Write($"[{level}] {category}: {message}");
}
