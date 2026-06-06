using System.Runtime.InteropServices;
using OptiSYS.Core.Native;
using Xunit;

namespace OptiSYS.Tests.Native;

/// <summary>
/// Value-guard for the documented PROCESS_INFORMATION_CLASS used by the per-process memory-priority
/// hint. The class is <c>ProcessMemoryPriority = 0</c> (the first entry of the documented enum). The
/// undocumented value <c>0x27</c> is the NtSetInformationProcess ProcessPagePriority ordinal and must
/// NOT be used here — passing it makes Get/SetProcessInformation reject every call with Win32 87.
/// Mirrors <see cref="PowerThrottlingStateTests"/>: pin the contract so it cannot silently drift.
/// </summary>
public class ProcessMemoryPriorityInfoClassTests
{
    [Fact]
    public void ProcessMemoryPriority_IsDocumentedZeroBasedInfoClass()
    {
        Assert.Equal(0, NativeMethods.ProcessMemoryPriority);
        Assert.NotEqual(0x27, NativeMethods.ProcessMemoryPriority);
    }

    [Fact]
    public void GetProcessMemoryPriority_ForCurrentProcess_ReturnsValidPriority()
    {
        // Integration: the real GetProcessInformation against our own process must succeed with the
        // correct info class and return a documented MEMORY_PRIORITY value in 1..5. If the info class
        // were wrong (0x27) this would fail with Win32 87 and return 0.
        var self = Process.GetCurrentProcessHandle();
        var priority = NativeMethods.GetProcessMemoryPriority(self);

        Assert.InRange(priority, NativeMethods.MEMORY_PRIORITY_VERY_LOW, NativeMethods.MEMORY_PRIORITY_NORMAL);
    }

    private static class Process
    {
        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentProcess();

        // Pseudo-handle (-1) for the current process; valid for Query/Set on self without OpenProcess.
        public static nint GetCurrentProcessHandle() => GetCurrentProcess();
    }
}
