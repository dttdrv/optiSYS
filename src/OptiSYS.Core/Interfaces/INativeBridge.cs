namespace OptiSYS.Core.Interfaces;

using OptiSYS.Core.Models;

/// <summary>
/// Bridge to native platform functions. Implemented by ZigNativeBridge (calls optisys_core.dll)
/// or ManagedNativeBridge (P/Invoke fallback when Zig DLL unavailable).
/// </summary>
public interface INativeBridge : IDisposable
{
    // Power / Battery
    bool GetBatteryInfo(out NativeBatteryInfo info);
    PowerSource GetPowerSource();
    bool SetEcoQos(bool enable, int processId);
    bool SetTimerResolution(bool increase, int processId);

    // Memory
    bool GetMemoryInfo(out NativeMemoryInfo info);
    long TrimProcessWorkingSet(int processId);
    bool EmptyWorkingSet(int processId);
    bool ClearStandbyList();
    bool FlushModifications();

    // Process
    int GetForegroundProcessId();
    NativeProcessInfo[] GetProcessList();
    bool SetProcessPriority(int processId, ProcessPriorityClass priorityClass);
}

public struct NativeBatteryInfo
{
    public PowerSource PowerSource;
    public bool HasBattery;
    public byte ChargePercent;
    public int DrainRateMilliwatts;
    public int EstimatedTimeRemainingSeconds;
}

public struct NativeMemoryInfo
{
    public long TotalPhysicalBytes;
    public long AvailablePhysicalBytes;
    public long CommittedBytes;
    public long StandbyCacheNormalPriorityBytes;
    public long StandbyCacheReserveBytes;
    public long ModifiedPageListBytes;
}

public struct NativeProcessInfo
{
    public int ProcessId;
    public string ProcessName;
    public long WorkingSetBytes;
    public long PrivateBytes;
    public ProcessPriorityClass PriorityClass;
}
