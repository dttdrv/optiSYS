namespace OptiSYS.Core.Interfaces;

using OptiSYS.Core.Models;

/// <summary>
/// Bridge to native Windows platform functions.
/// </summary>
public interface INativeBridge : IDisposable
{
    // Power / Battery
    bool GetBatteryInfo(out NativeBatteryInfo info);
    PowerSource GetPowerSource();
    bool SetEcoQos(bool enable, int processId);
    bool SetTimerResolution(bool increase, int processId);

    // Reads back a process's actual EcoQoS (EXECUTION_SPEED) throttling state via the documented
    // GetProcessInformation. true = verified throttled, false = verified not throttled,
    // null = unknown (access denied / exited) so the caller falls back to attempt-and-track.
    bool? IsEcoQosThrottled(int processId);

    // Memory
    bool GetMemoryInfo(out NativeMemoryInfo info);
    long TrimProcessWorkingSet(int processId);
    bool EmptyWorkingSet(int processId);
    bool ClearStandbyList();
    bool FlushModifications();

    // Process memory priority (capture-before-lower so the hint can be fully reverted).
    // GetProcessMemoryPriority returns 0 when the value can't be read.
    uint GetProcessMemoryPriority(int processId);
    bool SetProcessMemoryPriority(int processId, uint priority);

    // Process
    int GetForegroundProcessId();
    NativeProcessInfo[] GetProcessList();
    bool SetProcessPriority(int processId, ProcessPriorityClass priorityClass);

    // Drain measurement: total CPU time (kernel + user) for a process, or null when it can't be
    // read (access denied / exited) so the caller treats the sample as unknown.
    TimeSpan? GetProcessCpuTime(int processId);

    // Process ids with an ACTIVE audio session on the default render endpoint. Best-effort:
    // empty on failure (no device / COM error) — callers fall back to the static exclusions.
    IReadOnlyCollection<int> GetAudibleProcessIds();

    // Time since the last user input, system-wide (GetLastInputInfo). Zero on failure so the
    // safe interpretation is always "user present".
    TimeSpan GetUserIdleTime();

    // C-state guardian: cumulative context-switch counts per pid, from one system snapshot
    // (NtQuerySystemInformation). Null when the query fails — callers keep their previous
    // classification rather than acting on a guess.
    IReadOnlyDictionary<int, long>? GetProcessContextSwitchCounts();
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
