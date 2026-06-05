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

    // ── Foreground window → process id ────────────────────────────────

    internal static uint GetForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }
}
