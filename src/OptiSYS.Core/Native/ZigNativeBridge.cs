using System.Runtime.InteropServices;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

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
    private static extern int optisys_power_snapshot(out NativeBatteryInfo info);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int optisys_power_source();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int optisys_memory_init();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int optisys_memory_snapshot(out NativeMemoryInfo info);

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
    private struct NativeBatteryInfo
    {
        public int PowerSource;
        [MarshalAs(UnmanagedType.U1)] public bool HasBattery;
        public byte ChargePercent;
        public int DrainRateMilliwatts;
        public int EstimatedTimeRemainingSeconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMemoryInfo
    {
        public long TotalPhysicalBytes;
        public long AvailablePhysicalBytes;
        public long CommittedBytes;
        public long StandbyCacheBytes;
        public long ModifiedPageListBytes;
    }

    // ── INativeBridge implementation ──────────────────────────────────

    public bool GetBatteryInfo(out NativeBatteryInfo info)
    {
        try
        {
            var result = optisys_power_snapshot(out var nativeInfo);
            if (result == 0)
            {
                info = nativeInfo;
                return true;
            }
        }
        catch (DllNotFoundException) { }

        info = default;
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

    public bool GetMemoryInfo(out NativeMemoryInfo info)
    {
        try
        {
            var result = optisys_memory_snapshot(out var nativeInfo);
            if (result == 0)
            {
                info = nativeInfo;
                return true;
            }
        }
        catch (DllNotFoundException) { }

        info = default;
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

    public bool ClearStandbyList()
    {
        // Not yet in Zig bridge
        return false;
    }

    public bool FlushModifications()
    {
        // Not yet in Zig bridge
        return false;
    }

    public int GetForegroundProcessId()
    {
        // Fallback - managed implementation needed
        return 0;
    }

    public NativeProcessInfo[] GetProcessList()
    {
        // Fallback - managed implementation needed
        return [];
    }

    public bool SetProcessPriority(int processId, ProcessPriorityClass priorityClass)
    {
        // Not yet in Zig bridge
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { optisys_shutdown(); } catch { }
    }
}
