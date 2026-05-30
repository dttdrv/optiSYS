using OptiSYS.Core.Interfaces;

namespace OptiSYS.Core.Services;

/// <summary>
/// Pure selection of the heaviest processes for the dashboard's "memory-heavy apps" list:
/// keep only those at/above the byte threshold, largest first, capped at <paramref name="max"/>.
/// </summary>
public static class MemoryProcessSelector
{
    public static IReadOnlyList<(string name, long bytes)> SelectHeavy(
        IEnumerable<NativeProcessInfo> processes, long thresholdBytes, int max) =>
        processes
            .Where(p => p.WorkingSetBytes >= thresholdBytes)
            .OrderByDescending(p => p.WorkingSetBytes)
            .Take(max)
            .Select(p => (p.ProcessName, p.WorkingSetBytes))
            .ToList();
}
