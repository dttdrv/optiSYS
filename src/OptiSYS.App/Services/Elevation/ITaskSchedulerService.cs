namespace OptiSYS.Services.Elevation;

/// <summary>
/// Self-provisions a Windows Task Scheduler task that relaunches optiSYS with
/// <c>HighestAvailable</c> privileges at logon — giving silent admin elevation after a
/// single one-time UAC prompt, so the system-mutating domains can write HKLM power
/// schemes / SCM start-types without nagging on every autostart.
///
/// <para>
/// The app manifest stays <c>asInvoker</c> (switching to <c>requireAdministrator</c> would
/// force UAC on every launch and break Explorer drag-drop via UIPI). Task creation requires
/// an elevated token, so it only happens from the <c>--provision-elevation</c> startup branch
/// (launched via runas) or self-heals inline when we are already elevated.
/// </para>
/// </summary>
public interface ITaskSchedulerService
{
    /// <summary>True when the current process holds an elevated (Administrator) token.</summary>
    bool IsElevated { get; }

    /// <summary>True when the "OptiSYS" logon task is registered.</summary>
    bool TaskExists();

    /// <summary>
    /// True when the task is missing, or its registered executable path no longer matches
    /// this process (an in-place upgrade moved the exe) — i.e. (re)registration is required.
    /// </summary>
    bool NeedsProvisioning();

    /// <summary>
    /// Create or overwrite the logon task. MUST be called from an elevated process; returns
    /// false (never throws) when the schtasks invocation fails or we lack elevation.
    /// </summary>
    bool CreateOrUpdateTask();

    /// <summary>Delete the logon task (opt-out / uninstall). Returns false on failure, never throws.</summary>
    bool DeleteTask();
}
