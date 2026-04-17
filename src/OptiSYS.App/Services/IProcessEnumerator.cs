using OptiSYS.Core.Models;

namespace OptiSYS.Services;

/// <summary>
/// Thin seam over <see cref="System.Diagnostics.Process"/>. Lets <c>ProcessesViewModel</c>
/// enumerate and kill processes without taking a hard dependency on <c>System.Diagnostics</c>
/// — which matters for testability: <c>Process.GetProcesses()</c> returns live OS data, so
/// tests use a fake <see cref="IProcessEnumerator"/> returning canned lists instead.
/// </summary>
public interface IProcessEnumerator
{
    /// <summary>
    /// Snapshot of every process visible to this user. Entries that fail enumeration
    /// (access denied, exited mid-scan) are silently dropped — callers get what they can get.
    /// </summary>
    IReadOnlyList<ProcessMemoryInfo> EnumerateAll();

    /// <summary>
    /// Request termination of the process with the given PID.
    /// </summary>
    /// <returns><c>true</c> if the process was killed or had already exited; <c>false</c>
    /// if access was denied or the PID didn't exist.</returns>
    bool TryKill(int pid);
}
