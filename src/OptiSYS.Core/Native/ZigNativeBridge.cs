using System.Runtime.InteropServices;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

using IFaceBatteryInfo = OptiSYS.Core.Interfaces.NativeBatteryInfo;
using IFaceMemoryInfo = OptiSYS.Core.Interfaces.NativeMemoryInfo;
using IFaceProcessInfo = OptiSYS.Core.Interfaces.NativeProcessInfo;

namespace OptiSYS.Core.Native;

/// <summary>
/// Zig native bridge - calls optisys_core.dll via P/Invoke.
/// Falls back to ManagedNativeBridge when the Zig DLL is unavailable.
/// </summary>
public sealed class ZigNativeBridge : INativeBridge
{
    private const string DllName = "optisys_core.dll";
    private bool _disposed;

    // ── P/Invoke declarations matching bridge.h ──────────────────────

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int optisys_power_init();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int optisys_power_snapshot(out ZigBatteryInfo info);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int optisys_power_source();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int optisys_memory_init();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int optisys_memory_snapshot(out ZigMemoryInfo info);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int optisys_memory_optimize(int level, int excludedCount, IntPtr excludedPids);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int optisys_process_list(IntPtr buffer, int bufferSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern long optisys_process_trim(int pid);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int optisys_set_eco_qos(int pid, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void optisys_shutdown();

    // ── Native struct layouts matching bridge.zig ────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct ZigBatteryInfo
    {
        public int PowerSource;
        [MarshalAs(UnmanagedType.U1)] public bool HasBattery;
        public byte ChargePercent;
        public int DrainRateMilliwatts;
        public int EstimatedTimeRemainingSeconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ZigMemoryInfo
    {
        public long TotalPhysicalBytes;
        public long AvailablePhysicalBytes;
        public long CommittedBytes;
        public long StandbyCacheBytes;
        public long ModifiedPageListBytes;
    }

    // ── INativeBridge implementation ──────────────────────────────────

    public bool GetBatteryInfo(out IFaceBatteryInfo info)
    {
        info = default;
        try
        {
            var result = optisys_power_snapshot(out var nativeInfo);
            if (result == 0)
            {
                info.PowerSource = nativeInfo.PowerSource switch
                {
                    1 => PowerSource.Ac,
                    0 => PowerSource.Battery,
                    _ => PowerSource.Unknown
                };
                info.HasBattery = nativeInfo.HasBattery;
                info.ChargePercent = nativeInfo.ChargePercent;
                info.DrainRateMilliwatts = nativeInfo.DrainRateMilliwatts;
                info.EstimatedTimeRemainingSeconds = nativeInfo.EstimatedTimeRemainingSeconds;
                return true;
            }
        }
        catch (DllNotFoundException) { }
        return false;
    }

    public PowerSource GetPowerSource()
    {
        try { return (PowerSource)optisys_power_source(); }
        catch (DllNotFoundException) { return PowerSource.Unknown; }
    }

    public bool SetEcoQos(bool enable, int processId)
    {
        try { return optisys_set_eco_qos(processId, enable) == 0; }
        catch (DllNotFoundException) { return false; }
    }

    public bool SetTimerResolution(bool increase, int processId)
    {
        // Timer resolution is not yet in Zig bridge - falls back to managed
        return false;
    }

    public bool GetMemoryInfo(out IFaceMemoryInfo info)
    {
        info = default;
        try
        {
            var result = optisys_memory_snapshot(out var nativeInfo);
            if (result == 0)
            {
                info.TotalPhysicalBytes = nativeInfo.TotalPhysicalBytes;
                info.AvailablePhysicalBytes = nativeInfo.AvailablePhysicalBytes;
                info.CommittedBytes = nativeInfo.CommittedBytes;
                info.StandbyCacheNormalPriorityBytes = 0;
                info.StandbyCacheReserveBytes = 0;
                info.ModifiedPageListBytes = nativeInfo.ModifiedPageListBytes;
                return true;
            }
        }
        catch (DllNotFoundException) { }
        return false;
    }

    public long TrimProcessWorkingSet(int processId)
    {
        try { return optisys_process_trim(processId); }
        catch (DllNotFoundException) { return 0; }
    }

    public bool EmptyWorkingSet(int processId)
    {
        try { return optisys_process_trim(processId) > 0; }
        catch (DllNotFoundException) { return false; }
    }

    public bool ClearStandbyList() => false;
    public bool FlushModifications() => false;
    public int GetForegroundProcessId() => 0;
    public IFaceProcessInfo[] GetProcessList() => [];
    public bool SetProcessPriority(int processId, Models.ProcessPriorityClass priorityClass) => false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { optisys_shutdown(); } catch { }
    }
}
