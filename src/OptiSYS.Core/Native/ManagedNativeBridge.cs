using System.Diagnostics;
using System.Runtime.InteropServices;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Native;

/// <summary>
/// Managed fallback bridge using direct P/Invoke to Windows APIs.
/// Used when the Zig native DLL (optisys_core.dll) is unavailable.
/// </summary>
public sealed class ManagedNativeBridge : INativeBridge
{
    private bool _disposed;

    // ── Windows API P/Invoke declarations ────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(out MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(ProcessAccessFlags dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr hProcess, PROCESS_POWER_THROTTLING_STATE state);

    // ── Struct layouts ────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public int Length;
        public long MemoryLoad;
        public long TotalPhysical;
        public long AvailablePhysical;
        public long TotalPageFile;
        public long AvailablePageFile;
        public long TotalVirtual;
        public long AvailableVirtual;
    }

    [Flags]
    private enum ProcessAccessFlags : uint
    {
        QueryLimitedInformation = 0x1000,
        ProcessSetInformation = 0x0200,
        ProcessVMOperation = 0x0008,
        ProcessVMRead = 0x0010,
        ProcessVMWrite = 0x0020
    }

    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private const uint PROCESS_POWER_NOTIFICATION_ENABLED = 0x00000001;

    // ── INativeBridge implementation ──────────────────────────────────

    public bool GetBatteryInfo(out NativeBatteryInfo info)
    {
        info = default;
        if (!GetSystemPowerStatus(out var status))
            return false;

        info.PowerSource = status.ACLineStatus switch
        {
            1 => 1, // AC
            0 => 2, // Battery
            _ => 0  // Unknown
        };
        info.HasBattery = status.BatteryFlag != 128; // 128 = no battery
        info.ChargePercent = status.BatteryLifePercent == 255 ? (byte)0 : status.BatteryLifePercent;
        info.DrainRateMilliwatts = 0; // Not available via GetSystemPowerStatus
        info.EstimatedTimeRemainingSeconds = status.BatteryLifeTime == -1 ? 0 : status.BatteryLifeTime;

        return true;
    }

    public PowerSource GetPowerSource()
    {
        if (!GetSystemPowerStatus(out var status))
            return PowerSource.Unknown;
        return status.ACLineStatus switch
        {
            1 => PowerSource.Ac,
            0 => PowerSource.Battery,
            _ => PowerSource.Unknown
        };
    }

    public bool SetEcoQos(bool enable, int processId)
    {
        // EcoQoS via SetProcessInformation
        try
        {
            var handle = OpenProcess(ProcessAccessFlags.ProcessSetInformation, false, processId);
            if (handle == IntPtr.Zero) return false;

            try
            {
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = PROCESS_POWER_NOTIFICATION_ENABLED,
                    StateMask = enable ? PROCESS_POWER_NOTIFICATION_ENABLED : 0
                };
                return SetProcessInformation(handle, state);
            }
            finally { CloseHandle(handle); }
        }
        catch { return false; }
    }

    public bool SetTimerResolution(bool increase, int processId)
    {
        // Timer resolution management - advanced, requires NtSetTimerResolution
        return false;
    }

    public bool GetMemoryInfo(out NativeMemoryInfo info)
    {
        info = default;
        var memStatus = new MEMORYSTATUSEX { Length = Marshal.SizeOf<MEMORYSTATUSEX>() };

        if (!GlobalMemoryStatusEx(out memStatus))
            return false;

        info.TotalPhysicalBytes = memStatus.TotalPhysical;
        info.AvailablePhysicalBytes = memStatus.AvailablePhysical;
        info.CommittedBytes = 0;
        info.StandbyCacheBytes = 0;
        info.ModifiedPageListBytes = 0;

        return true;
    }

    public long TrimProcessWorkingSet(int processId)
    {
        try
        {
            var handle = OpenProcess(ProcessAccessFlags.ProcessVMOperation | ProcessAccessFlags.ProcessVMRead, false, processId);
            if (handle == IntPtr.Zero) return 0;

            try
            {
                var before = GetProcessWorkingSet64(processId);
                if (EmptyWorkingSet(handle))
                    return Math.Max(0, GetProcessWorkingSet64(processId) - before);
                return 0;
            }
            finally { CloseHandle(handle); }
        }
        catch { return 0; }
    }

    public bool EmptyWorkingSet(int processId)
    {
        try
        {
            var handle = OpenProcess(ProcessAccessFlags.ProcessVMOperation, false, processId);
            if (handle == IntPtr.Zero) return false;

            try { return EmptyWorkingSet(handle); }
            finally { CloseHandle(handle); }
        }
        catch { return false; }
    }

    public bool ClearStandbyList()
    {
        // Requires NtSetSystemInformation - elevated only
        return false;
    }

    public bool FlushModifications()
    {
        // Requires NtSetSystemInformation - elevated only
        return false;
    }

    public int GetForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    public NativeProcessInfo[] GetProcessList()
    {
        var processes = Process.GetProcesses();
        var result = new List<NativeProcessInfo>(processes.Length);
        var fgPid = GetForegroundProcessId();

        foreach (var proc in processes)
        {
            try
            {
                result.Add(new NativeProcessInfo
                {
                    ProcessId = proc.Id,
                    ProcessName = proc.ProcessName,
                    WorkingSetBytes = proc.WorkingSet64,
                    PrivateBytes = proc.PrivateMemorySize64,
                    IsForeground = proc.Id == fgPid,
                    IsExcluded = false,
                    PriorityClass = (ProcessPriorityClass)(int)proc.PriorityClass
                });
            }
            catch { }
            finally { proc.Dispose(); }
        }

        return result.ToArray();
    }

    public bool SetProcessPriority(int processId, ProcessPriorityClass priorityClass)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            proc.PriorityClass = (System.Diagnostics.ProcessPriorityClass)priorityClass;
            return true;
        }
        catch { return false; }
    }

    private static long GetProcessWorkingSet64(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.WorkingSet64; }
        catch { return 0; }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
