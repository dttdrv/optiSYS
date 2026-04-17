using System.ComponentModel;
using System.Diagnostics;
using OptiSYS.Core.Models;
using CorePriority = OptiSYS.Core.Models.ProcessPriorityClass;
using SysPriority  = System.Diagnostics.ProcessPriorityClass;

namespace OptiSYS.Services;

/// <summary>
/// Production <see cref="IProcessEnumerator"/> backed by <see cref="Process.GetProcesses"/>.
///
/// <para>
/// Defensive around every per-process read — <c>Process.WorkingSet64</c>/<c>PrivateMemorySize64</c>
/// throw <see cref="Win32Exception"/>/<see cref="InvalidOperationException"/>/<see cref="UnauthorizedAccessException"/>
/// when the process exits mid-read or lives in a restricted session. We drop those entries
/// instead of aborting the whole scan, matching user expectations ("show me what you can").
/// </para>
///
/// <para>
/// <see cref="CorePriority"/> is mapped from <see cref="SysPriority"/> via an int cast —
/// Core's enum was deliberately shadowed to keep Core BCL-decoupled for the eventual Zig
/// backend, and both enums share identical Win32 priority-class values.
/// </para>
/// </summary>
public sealed class ProcessEnumerator : IProcessEnumerator
{
    public IReadOnlyList<ProcessMemoryInfo> EnumerateAll()
    {
        var processes = Process.GetProcesses();
        var results = new List<ProcessMemoryInfo>(processes.Length);

        foreach (var proc in processes)
        {
            try
            {
                results.Add(new ProcessMemoryInfo
                {
                    ProcessId       = proc.Id,
                    ProcessName     = proc.ProcessName,
                    WorkingSetBytes = proc.WorkingSet64,
                    PrivateBytes    = proc.PrivateMemorySize64,
                    PriorityClass   = MapPriority(SafePriority(proc)),
                    // IsForeground / IsExcluded intentionally left at default —
                    // the optimizer layer owns those classifications; the enumerator
                    // just reports what the OS exposes.
                });
            }
            catch (Win32Exception)             { /* access denied — skip */ }
            catch (InvalidOperationException)  { /* process exited mid-read — skip */ }
            catch (UnauthorizedAccessException){ /* elevated process from low-IL — skip */ }
            finally { proc.Dispose(); }
        }

        return results;
    }

    public bool TryKill(int pid)
    {
        Process? proc = null;
        try
        {
            proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: false);
            return true;
        }
        catch (ArgumentException)          { return false; } // PID doesn't exist
        catch (InvalidOperationException)  { return true;  } // already exited → as good as killed
        catch (Win32Exception)             { return false; } // access denied / not terminable
        catch (UnauthorizedAccessException){ return false; }
        finally { proc?.Dispose(); }
    }

    // Reading PriorityClass can throw on system/virtual processes; fall back to Normal
    // rather than dropping the whole row when only the priority read fails.
    private static SysPriority SafePriority(Process p)
    {
        try { return p.PriorityClass; }
        catch { return SysPriority.Normal; }
    }

    private static CorePriority MapPriority(SysPriority p) => (CorePriority)(int)p;
}
