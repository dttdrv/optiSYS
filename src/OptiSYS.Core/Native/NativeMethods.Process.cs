using System.Runtime.InteropServices;

namespace OptiSYS.Core.Native;

/// <summary>
/// Process control: handles, access rights, EcoQoS / timer-resolution power throttling,
/// and memory-priority helpers.
/// </summary>
internal static partial class NativeMethods
{
    // ── Process access rights ────────────────────────────────────────
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint PROCESS_SET_INFORMATION = 0x0200;

    // ── Process information classes ──────────────────────────────────
    internal const int ProcessPowerThrottling = 4;

    // ── Power throttling flags ───────────────────────────────────────
    internal const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    internal const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    // ── kernel32.dll ─────────────────────────────────────────────────

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, IntPtr ProcessInformation, uint ProcessInformationSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessInformation(IntPtr hProcess, int ProcessInformationClass, IntPtr ProcessInformation, uint ProcessInformationSize);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetCurrentProcessId();

    // ── EcoQoS / timer-resolution power throttling ────────────────────

    // Pure builders for the power-throttling state — split out so the masks are unit-testable
    // without a real process handle. Revert RESETS to OS-managed (Control=0, State=0): clearing
    // the override returns the process to Windows-managed QoS. Sending Control=flag with State=0
    // instead PINS the process to high performance ("never throttle"), the opposite of revert.

    internal static PROCESS_POWER_THROTTLING_STATE BuildEcoQoSState(bool enable) =>
        enable
            ? new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
            }
            : new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = 0,
                StateMask = 0
            };

    internal static PROCESS_POWER_THROTTLING_STATE BuildTimerResolutionState(bool ignore) =>
        ignore
            ? new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask = PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION
            }
            : new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = 0,
                StateMask = 0
            };

    internal static unsafe bool SetProcessEcoQoS(IntPtr hProcess, bool enable)
    {
        var state = BuildEcoQoSState(enable);
        var ptr = (IntPtr)(&state);
        return SetProcessInformation(hProcess, ProcessPowerThrottling, ptr, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }

    // Pure decode (unit-testable without a process handle): a process is verified in EcoQoS only
    // when EXECUTION_SPEED is set in BOTH the control and state masks. OS-managed (0,0) and
    // pinned-high (Control=flag, State=0) are not EcoQoS.
    internal static bool IsEcoQoSThrottled(in PROCESS_POWER_THROTTLING_STATE state) =>
        (state.ControlMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0 &&
        (state.StateMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0;

    // Reads the process's current power-throttling state via the documented GetProcessInformation
    // (ProcessPowerThrottling). Returns null when it can't be queried (access denied, exited) so the
    // caller can treat the result as "unknown" and fall back. Needs only QUERY_LIMITED_INFORMATION.
    internal static unsafe PROCESS_POWER_THROTTLING_STATE? GetProcessEcoQoSState(IntPtr hProcess)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE();
        var ptr = (IntPtr)(&state);
        return GetProcessInformation(hProcess, ProcessPowerThrottling,
            ptr, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>())
            ? state
            : null;
    }

    internal static unsafe bool SetProcessTimerResolutionIgnore(IntPtr hProcess, bool ignore)
    {
        var state = BuildTimerResolutionState(ignore);
        var ptr = (IntPtr)(&state);
        return SetProcessInformation(hProcess, ProcessPowerThrottling, ptr, (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }

    // ── Process CPU time (drain measurement) ─────────────────────────

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(
        IntPtr hProcess, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    // Total CPU time (kernel + user). FILETIME's 100ns unit IS the TimeSpan tick, so the sum
    // converts directly. Null when the query fails (access denied / exited) — "unknown".
    internal static TimeSpan? GetProcessCpuTime(IntPtr hProcess) =>
        GetProcessTimes(hProcess, out _, out _, out var kernelTime, out var userTime)
            ? TimeSpan.FromTicks(kernelTime + userTime)
            : null;

    // ── Per-process context-switch counts (wakeup pressure) ──────────
    // Well-known x64 SYSTEM_PROCESS_INFORMATION layout: process entry header 256 bytes
    // (NextEntryOffset +0, NumberOfThreads +4, UniqueProcessId +80), followed by
    // NumberOfThreads x 80-byte SYSTEM_THREAD_INFORMATION entries with ContextSwitches +64.
    // The Lab `wakeup` probe self-checks these offsets against the system Context Switches/sec
    // counter on every run.

    private const int SystemProcessInformationClass = 5;
    private const int ProcessEntryHeaderBytes = 256;
    private const int ThreadEntryBytes = 80;
    private const int ThreadContextSwitchesOffset = 64;

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQuerySystemInformation(
        int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    /// <summary>
    /// One snapshot of every process's cumulative context-switch count (threads summed).
    /// Null when the query fails so callers treat the sweep as "no fresh evidence".
    /// </summary>
    internal static IReadOnlyDictionary<int, long>? GetProcessContextSwitchCounts()
    {
        var size = 1 << 20;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            while (NtQuerySystemInformation(SystemProcessInformationClass, buffer, size, out var needed) != 0)
            {
                if (needed <= size)
                    return null;   // a non-length error

                size = needed + (64 * 1024);
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
            }

            var counts = new Dictionary<int, long>();
            var offset = 0;
            // Bounds-guarded walk: the kernel returns well-formed data, but an out-of-bounds
            // Marshal read raises an AccessViolationException that a managed catch does NOT
            // swallow — so every offset is validated against the allocation before it is read.
            while (offset >= 0 && offset <= size - ProcessEntryHeaderBytes)
            {
                var entry = buffer + offset;
                var nextOffset = Marshal.ReadInt32(entry, 0);
                var threadCount = Marshal.ReadInt32(entry, 4);
                var pid = (int)Marshal.ReadIntPtr(entry, 80);

                if (pid > 4 && threadCount >= 0)
                {
                    // Clamp the thread walk to what actually fits inside the buffer.
                    var maxThreads = (size - offset - ProcessEntryHeaderBytes) / ThreadEntryBytes;
                    var safeThreads = Math.Min(threadCount, maxThreads);

                    long switches = 0;
                    for (var t = 0; t < safeThreads; t++)
                    {
                        var thread = entry + ProcessEntryHeaderBytes + t * ThreadEntryBytes;
                        switches += (uint)Marshal.ReadInt32(thread, ThreadContextSwitchesOffset);
                    }

                    counts[pid] = switches;
                }

                if (nextOffset <= 0)
                    break;
                offset += nextOffset;
            }

            return counts;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ── Parent process id ─────────────────────────────────────────────

    private const int ProcessBasicInformationClass = 0;
    private const int ProcessBasicInformationBytes = 48;          // x64 PROCESS_BASIC_INFORMATION
    private const int InheritedFromUniqueProcessIdOffset = 40;

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass, IntPtr processInformation,
        int processInformationLength, out int returnLength);

    /// <summary>Parent pid via PROCESS_BASIC_INFORMATION; null when it can't be read.</summary>
    internal static int? GetParentProcessId(IntPtr hProcess)
    {
        var buffer = Marshal.AllocHGlobal(ProcessBasicInformationBytes);
        try
        {
            return NtQueryInformationProcess(
                hProcess, ProcessBasicInformationClass, buffer, ProcessBasicInformationBytes, out _) == 0
                ? (int)Marshal.ReadIntPtr(buffer, InheritedFromUniqueProcessIdOffset)
                : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ── Foreground window → process id ────────────────────────────────

    internal static uint GetForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }
}
