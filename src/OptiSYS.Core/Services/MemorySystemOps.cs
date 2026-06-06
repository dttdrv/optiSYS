using System.Runtime.InteropServices;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Services;

/// <summary>
/// Test seam over the system-wide memory-management native calls that <see cref="MemoryOptimizer"/>
/// drives directly (the NtSetSystemInformation memory-list commands and the live available-physical
/// read). These do NOT go through <c>INativeBridge</c> (the production bridge stubs them out), so this
/// thin internal seam is what lets <c>OptimizeAll</c>'s escalation/early-exit logic be tested
/// deterministically. The default implementation is the real native path — production behavior is
/// unchanged.
/// </summary>
internal interface IMemorySystemOps
{
    /// <summary>Live available physical bytes (drives early-exit checks and the before→after reclaim).</summary>
    long AvailablePhysicalBytes();

    /// <summary>Runs one NtSetSystemInformation memory-list command; true on NTSTATUS &gt;= 0.</summary>
    bool RunMemoryListCommand(NativeMethods.MemoryListCommand command);
}

/// <summary>Real native implementation — identical to the prior inline calls.</summary>
internal sealed class NativeMemorySystemOps : IMemorySystemOps
{
    public long AvailablePhysicalBytes()
    {
        var ms = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };
        NativeMethods.GlobalMemoryStatusEx(ref ms);
        return (long)ms.ullAvailPhys;
    }

    public bool RunMemoryListCommand(NativeMethods.MemoryListCommand command)
    {
        int cmd = (int)command;
        int result = NativeMethods.NtSetSystemInformation(
            NativeMethods.SystemMemoryListInformation, ref cmd, sizeof(int));
        return result >= 0;
    }
}
