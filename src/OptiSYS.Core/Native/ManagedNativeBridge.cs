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
#pragma warning disable CS0414 // _disposed is used in Dispose pattern
    private volatile bool _disposed;
#pragma warning restore CS0414

    // ── Windows API P/Invoke declarations ────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(out MEMORYSTATUSEX lpBuffer);

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

    // ── INativeBridge implementation ──────────────────────────────────

    public bool GetBatteryInfo(out Interfaces.NativeBatteryInfo info)
    {
        info = default;
        if (!GetSystemPowerStatus(out var status))
            return false;

        info.PowerSource = status.ACLineStatus switch
        {
            1 => PowerSource.Ac,
            0 => PowerSource.Battery,
            _ => PowerSource.Unknown
        };
        info.HasBattery = status.BatteryFlag != 128;
        info.ChargePercent = status.BatteryLifePercent == 255 ? (byte)0 : status.BatteryLifePercent;
        info.DrainRateMilliwatts = 0;
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
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION,
            false, (uint)processId);
        if (handle == IntPtr.Zero) return false;
        try { return NativeMethods.SetProcessEcoQoS(handle, enable); }
        finally { NativeMethods.CloseHandle(handle); }
    }

    public bool SetTimerResolution(bool ignore, int processId)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION,
            false, (uint)processId);
        if (handle == IntPtr.Zero) return false;
        try { return NativeMethods.SetProcessTimerResolutionIgnore(handle, ignore); }
        finally { NativeMethods.CloseHandle(handle); }
    }

    public bool GetMemoryInfo(out Interfaces.NativeMemoryInfo info)
    {
        info = default;
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };

        if (!GlobalMemoryStatusEx(out memStatus))
            return false;

        info.TotalPhysicalBytes = (long)memStatus.ullTotalPhys;
        info.AvailablePhysicalBytes = (long)memStatus.ullAvailPhys;
        info.CommittedBytes = 0;
        info.StandbyCacheNormalPriorityBytes = 0;
        info.StandbyCacheReserveBytes = 0;
        info.ModifiedPageListBytes = 0;

        return true;
    }

    public long TrimProcessWorkingSet(int processId)
    {
        try
        {
            var handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
                false, (uint)processId);
            if (handle == IntPtr.Zero) return 0;

            try
            {
                var before = GetProcessWorkingSet64(processId);
                if (NativeMethods.EmptyWorkingSet(handle))
                    return Math.Max(0, GetProcessWorkingSet64(processId) - before);
                return 0;
            }
            finally { NativeMethods.CloseHandle(handle); }
        }
        catch { return 0; }
    }

    public bool EmptyWorkingSet(int processId)
    {
        try
        {
            var handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
                false, (uint)processId);
            if (handle == IntPtr.Zero) return false;
            try { return NativeMethods.EmptyWorkingSet(handle); }
            finally { NativeMethods.CloseHandle(handle); }
        }
        catch { return false; }
    }

    public bool ClearStandbyList() => false;
    public bool FlushModifications() => false;

    public int GetForegroundProcessId() => (int)NativeMethods.GetForegroundProcessId();

    public Interfaces.NativeProcessInfo[] GetProcessList()
    {
        var processes = Process.GetProcesses();
        var result = new List<Interfaces.NativeProcessInfo>(processes.Length);

        foreach (var proc in processes)
        {
            try
            {
                result.Add(new Interfaces.NativeProcessInfo
                {
                    ProcessId = proc.Id,
                    ProcessName = proc.ProcessName,
                    WorkingSetBytes = proc.WorkingSet64,
                    PrivateBytes = proc.PrivateMemorySize64,
                    PriorityClass = MapPriorityClass(proc.PriorityClass)
                });
            }
            catch { }
            finally { proc.Dispose(); }
        }

        return result.ToArray();
    }

    public bool SetProcessPriority(int processId, Models.ProcessPriorityClass priorityClass)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            proc.PriorityClass = MapPriorityClassBack(priorityClass);
            return true;
        }
        catch { return false; }
    }

    private static Models.ProcessPriorityClass MapPriorityClass(System.Diagnostics.ProcessPriorityClass pc) => pc switch
    {
        System.Diagnostics.ProcessPriorityClass.Idle => Models.ProcessPriorityClass.Idle,
        System.Diagnostics.ProcessPriorityClass.BelowNormal => Models.ProcessPriorityClass.BelowNormal,
        System.Diagnostics.ProcessPriorityClass.Normal => Models.ProcessPriorityClass.Normal,
        System.Diagnostics.ProcessPriorityClass.AboveNormal => Models.ProcessPriorityClass.AboveNormal,
        System.Diagnostics.ProcessPriorityClass.High => Models.ProcessPriorityClass.High,
        System.Diagnostics.ProcessPriorityClass.RealTime => Models.ProcessPriorityClass.RealTime,
        _ => Models.ProcessPriorityClass.Normal
    };

    private static System.Diagnostics.ProcessPriorityClass MapPriorityClassBack(Models.ProcessPriorityClass pc) => pc switch
    {
        Models.ProcessPriorityClass.Idle => System.Diagnostics.ProcessPriorityClass.Idle,
        Models.ProcessPriorityClass.BelowNormal => System.Diagnostics.ProcessPriorityClass.BelowNormal,
        Models.ProcessPriorityClass.Normal => System.Diagnostics.ProcessPriorityClass.Normal,
        Models.ProcessPriorityClass.AboveNormal => System.Diagnostics.ProcessPriorityClass.AboveNormal,
        Models.ProcessPriorityClass.High => System.Diagnostics.ProcessPriorityClass.High,
        Models.ProcessPriorityClass.RealTime => System.Diagnostics.ProcessPriorityClass.RealTime,
        _ => System.Diagnostics.ProcessPriorityClass.Normal
    };

    private static long GetProcessWorkingSet64(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.WorkingSet64; }
        catch { return 0; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
