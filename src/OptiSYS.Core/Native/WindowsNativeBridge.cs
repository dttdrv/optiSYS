using System.Diagnostics;
using System.Runtime.InteropServices;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Native;

/// <summary>
/// Windows-native bridge using direct P/Invoke to Windows APIs.
/// </summary>
public sealed class WindowsNativeBridge : INativeBridge
{
#pragma warning disable CS0414 // _disposed is used in Dispose pattern
    private volatile bool _disposed;
#pragma warning restore CS0414

    // ── Windows API P/Invoke declarations ────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

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
        info.ChargePercent = status.BatteryLifePercent is 255 or > 100
            ? (byte)0
            : status.BatteryLifePercent;
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

    public bool? IsEcoQosThrottled(int processId)
    {
        // Readback only needs QUERY_LIMITED_INFORMATION (works for protected processes that reject
        // the heavier QUERY_INFORMATION right). A failed open or query -> null ("unknown").
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var state = NativeMethods.GetProcessEcoQoSState(handle);
            return state is { } s ? NativeMethods.IsEcoQoSThrottled(s) : null;
        }
        finally { NativeMethods.CloseHandle(handle); }
    }

    public bool GetMemoryInfo(out Interfaces.NativeMemoryInfo info)
    {
        info = default;
        var memStatus = new NativeMethods.MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
        };

        if (!NativeMethods.GlobalMemoryStatusEx(ref memStatus))
            return false;

        info.TotalPhysicalBytes = (long)memStatus.ullTotalPhys;
        info.AvailablePhysicalBytes = (long)memStatus.ullAvailPhys;

        var perf = new NativeMethods.PERFORMANCE_INFORMATION
        {
            cb = (uint)Marshal.SizeOf<NativeMethods.PERFORMANCE_INFORMATION>()
        };
        if (NativeMethods.GetPerformanceInfo(ref perf, perf.cb))
        {
            var pageSize = (long)perf.PageSize;
            info.CommittedBytes = CheckedMultiply(perf.CommitTotal, pageSize);
            info.StandbyCacheNormalPriorityBytes = CheckedMultiply(perf.SystemCache, pageSize);
        }

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
                if (!NativeMethods.EmptyWorkingSet(handle))
                {
                    return 0;
                }

                var after = GetProcessWorkingSet64(processId);
                var freed = Math.Max(0, before - after);
                if (freed > 0)
                {
                    return freed;
                }

                return before > 0 ? 1 : 0;
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

    public uint GetProcessMemoryPriority(int processId)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION, false, (uint)processId);
        if (handle == IntPtr.Zero) return 0;
        try { return NativeMethods.GetProcessMemoryPriority(handle); }
        finally { NativeMethods.CloseHandle(handle); }
    }

    public bool SetProcessMemoryPriority(int processId, uint priority)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SET_INFORMATION, false, (uint)processId);
        if (handle == IntPtr.Zero) return false;
        try { return NativeMethods.SetProcessMemoryPriority(handle, priority); }
        finally { NativeMethods.CloseHandle(handle); }
    }

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

    private static long CheckedMultiply(UIntPtr value, long multiplier)
    {
        try
        {
            return checked((long)value.ToUInt64() * multiplier);
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
