using System.ComponentModel;
using System.Diagnostics;

namespace OptiSYS.Services.Elevation;

/// <summary>
/// Bridges an unelevated optiSYS instance to an elevated one via the ShellExecute
/// <c>runas</c> verb (the UAC prompt). The elevated child runs the
/// <see cref="ProvisionArgument"/> startup branch, registers the logon task, and exits —
/// so the one-time UAC is the only prompt the user ever sees.
/// </summary>
public static class ElevationHelper
{
    public const string ProvisionArgument = "--provision-elevation";

    /// <summary>The (pure) launch descriptor for the elevated provisioning child.</summary>
    internal static ProcessStartInfo BuildProvisionPsi(string exePath) => new()
    {
        FileName = exePath,
        Arguments = ProvisionArgument,
        UseShellExecute = true,   // required for the runas verb
        Verb = "runas",
    };

    /// <summary>
    /// Launch the elevated provisioning child and wait for it to finish.
    /// Returns true when provisioning ran (UAC accepted, child exited 0); false when the
    /// user declined the UAC prompt (Win32 error 1223) or the path is unknown. Never throws.
    /// </summary>
    /// <param name="exePath">Defaults to <see cref="Environment.ProcessPath"/>.</param>
    /// <param name="starter">Process starter seam (defaults to <see cref="Process.Start(ProcessStartInfo)"/>); injected in tests.</param>
    public static bool RequestProvisioning(string? exePath = null, Func<ProcessStartInfo, Process?>? starter = null)
    {
        exePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        starter ??= psi => Process.Start(psi);
        try
        {
            var process = starter(BuildProvisionPsi(exePath));
            if (process == null)
                return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user clicked "No" on the UAC prompt. Caller leaves the
            // "grant admin" affordance visible; not an error.
            return false;
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ElevationHelper.RequestProvisioning", ex);
            return false;
        }
    }
}
