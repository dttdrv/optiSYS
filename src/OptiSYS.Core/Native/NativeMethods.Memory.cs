using System.Runtime.InteropServices;

namespace OptiSYS.Core.Native;

/// <summary>
/// Memory management: global memory status, performance info, the NtSetSystemInformation
/// memory-list / combine / reconciliation commands, working-set trimming and quota,
/// system file cache, and per-process memory-priority hints.
/// </summary>
internal static partial class NativeMethods
{
    // ── Process quota / working set ───────────────────────────────────

    internal const uint PROCESS_SET_QUOTA = 0x0100;

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyWorkingSet(IntPtr hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessWorkingSetSizeEx(
        IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize, uint Flags);

    internal const uint QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x00000002;
    internal const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x00000001;
    internal const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x00000008;
    internal const uint QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x00000004;

    // ── System file cache ─────────────────────────────────────────────

    internal const int FILE_CACHE_MAX_HARD_ENABLE = 0x00000001;
    internal const int FILE_CACHE_MAX_HARD_DISABLE = 0x00000002;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetSystemFileCacheSize(IntPtr MinimumFileCacheSize, IntPtr MaximumFileCacheSize, int Flags);

    // ── Memory status ─────────────────────────────────────────────────

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    // ── Performance info ──────────────────────────────────────────────

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    // ── NtSetSystemInformation (memory management) ────────────────────

    internal const int SystemMemoryListInformation = 80;
    internal const int SystemCombinePhysicalMemoryInformation = 0x82;
    internal const int SystemRegistryReconciliationInformation = 0x9B;

    internal enum MemoryListCommand
    {
        MemoryCaptureAccessedBits = 0,
        MemoryCaptureAndResetAccessedBits = 1,
        MemoryEmptyWorkingSets = 2,
        MemoryFlushModifiedList = 3,
        MemoryPurgeStandbyList = 4,
        MemoryPurgeLowPriorityStandbyList = 5,
        MemoryCommandMax = 6
    }

    [LibraryImport("ntdll.dll")]
    internal static partial int NtSetSystemInformation(int SystemInformationClass, ref int SystemInformation, int SystemInformationLength);

    [LibraryImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
    internal static partial int NtSetSystemInformationNull(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_COMBINE_INFORMATION_EX
    {
        public IntPtr Handle;
        public UIntPtr PagesCombined;
    }

    [LibraryImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
    internal static partial int NtSetSystemInformationCombine(
        int SystemInformationClass, ref MEMORY_COMBINE_INFORMATION_EX SystemInformation, int SystemInformationLength);

    // ── Process memory priority ───────────────────────────────────────

    // Documented PROCESS_INFORMATION_CLASS::ProcessMemoryPriority is the FIRST entry = 0, used by the
    // public Get/SetProcessInformation. Do NOT use 0x27 (39): that is the undocumented
    // NtSetInformationProcess::ProcessPagePriority ordinal — a different enum — and passing it makes
    // Get/SetProcessInformation reject every call with ERROR_INVALID_PARAMETER (87).
    internal const int ProcessMemoryPriority = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORY_PRIORITY_INFORMATION
    {
        public uint MemoryPriority;
    }

    internal const uint MEMORY_PRIORITY_VERY_LOW = 1;
    internal const uint MEMORY_PRIORITY_LOW = 2;
    internal const uint MEMORY_PRIORITY_MEDIUM = 3;
    internal const uint MEMORY_PRIORITY_BELOW_NORMAL = 4;
    internal const uint MEMORY_PRIORITY_NORMAL = 5;

    internal static unsafe bool SetProcessMemoryPriority(IntPtr hProcess, uint priority)
    {
        var info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
        var ptr = (IntPtr)(&info);
        return SetProcessInformation(hProcess, ProcessMemoryPriority,
            ptr, (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>());
    }

    // Reads the process's current memory priority. Returns 0 when it can't be queried, so the
    // caller can capture-before-lower (to restore the exact prior value on revert).
    internal static unsafe uint GetProcessMemoryPriority(IntPtr hProcess)
    {
        var info = new MEMORY_PRIORITY_INFORMATION();
        var ptr = (IntPtr)(&info);
        return GetProcessInformation(hProcess, ProcessMemoryPriority,
            ptr, (uint)Marshal.SizeOf<MEMORY_PRIORITY_INFORMATION>())
            ? info.MemoryPriority
            : 0;
    }
}
