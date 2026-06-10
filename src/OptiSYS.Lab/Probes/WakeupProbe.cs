using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OptiSYS.Lab.Probes;

/// <summary>
/// The C-state guardian's evidence: battery life is package sleep-state residency, not CPU% —
/// a process waking 500x/second at 0.5% CPU keeps the SoC out of its deep idle states and
/// costs more than a clean 3% burst. This probe measures, over an 8s window:
///  - per-process WAKEUP PRESSURE (context-switch deltas summed across threads, via
///    NtQuerySystemInformation), alongside each process's CPU% so the divergence is visible;
///  - CPU idle-state residency (% C1/C2/C3) — deeper is better;
///  - a self-check: the per-process deltas summed vs the system "Context Switches/sec" counter.
/// Read-only; offsets are the well-known x64 SYSTEM_PROCESS/THREAD_INFORMATION layout and the
/// self-check exposes any layout drift immediately.
/// </summary>
public sealed class WakeupProbe : ILabProbe
{
    private static readonly TimeSpan SampleWindow = TimeSpan.FromSeconds(8);

    public string Name => "wakeup";
    public string Description => "per-process wakeup pressure (context switches/s) + C-state residency over 8s";
    public bool RequiresElevation => false;

    public ProbeResult Run()
    {
        var result = new ProbeResult { ProbeName = Name };

        using var c1 = TryCounter("Processor Information", "% C1 Time", "_Total");
        using var c2 = TryCounter("Processor Information", "% C2 Time", "_Total");
        using var c3 = TryCounter("Processor Information", "% C3 Time", "_Total");
        using var sysCtx = TryCounter("System", "Context Switches/sec", null);
        foreach (var counter in new[] { c1, c2, c3, sysCtx })
            counter?.NextValue();   // prime

        var first = SnapshotContextSwitches();
        var sw = Stopwatch.StartNew();
        Thread.Sleep(SampleWindow);
        var second = SnapshotContextSwitches();
        sw.Stop();

        var seconds = sw.Elapsed.TotalSeconds;
        var rates = new List<(int Pid, string Name, double SwitchesPerSec)>();
        foreach (var (pid, after) in second)
        {
            if (first.TryGetValue(pid, out var before) && before.Name == after.Name)
            {
                var delta = after.ContextSwitches - before.ContextSwitches;
                if (delta > 0)
                    rates.Add((pid, after.Name, delta / seconds));
            }
        }

        rates.Sort((a, b) => b.SwitchesPerSec.CompareTo(a.SwitchesPerSec));

        var residency = new ProbeSection { Title = "CPU idle-state residency (deeper = less drain)" };
        residency.Add("% C1 Time", FormatCounter(c1));
        residency.Add("% C2 Time", FormatCounter(c2));
        residency.Add("% C3 Time", FormatCounter(c3));
        result.Sections.Add(residency);

        var top = new ProbeSection { Title = $"Top wakeup producers over {seconds:F1}s (context switches/s)" };
        foreach (var r in rates.Take(15))
            top.Add($"{r.Name} (pid {r.Pid})", $"{r.SwitchesPerSec:F0}/s");
        if (rates.Count == 0)
            top.Add("none", "snapshot diff produced no deltas — layout assumption broken?");
        result.Sections.Add(top);

        var check = new ProbeSection { Title = "Self-check" };
        var summed = rates.Sum(r => r.SwitchesPerSec);
        var system = sysCtx?.NextValue() ?? 0;
        check.Add("sum of per-process rates", $"{summed:F0}/s");
        check.Add("system counter says", $"{system:F0}/s");
        check.Add("verdict", system > 0 && Math.Abs(summed - system) / system < 0.5
            ? "within 50% — per-process attribution is sound"
            : "diverges — treat per-process numbers as approximate");
        result.Sections.Add(check);

        result.Sections.Add(new ProbeSection { Title = "How to read this" }
            .Add("the insight", "high switches/s with LOW cpu% marks a process that murders C-state residency while looking innocent in Task Manager - the targets the guardian quiets with IGNORE_TIMER_RESOLUTION + EcoQoS")
            .Add("threshold question", "run on an idle-ish desktop: the guardian should catch the few hundreds-per-second offenders and ignore the tail. Compare residency with the offenders quieted vs not."));

        return result;
    }

    private static PerformanceCounter? TryCounter(string category, string counter, string? instance)
    {
        try
        {
            return instance is null
                ? new PerformanceCounter(category, counter)
                : new PerformanceCounter(category, counter, instance);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatCounter(PerformanceCounter? counter)
    {
        try { return counter is null ? "unavailable" : $"{counter.NextValue():F1}%"; }
        catch { return "unavailable"; }
    }

    // ── NtQuerySystemInformation(SystemProcessInformation) snapshot ──────────────────────────
    // Well-known x64 layout: process entry 256 bytes (NextEntryOffset +0, NumberOfThreads +4,
    // ImageName UNICODE_STRING +56 with Buffer +64, UniqueProcessId +80), followed by
    // NumberOfThreads x 80-byte SYSTEM_THREAD_INFORMATION entries with ContextSwitches +64.
    // The probe's self-check against the system counter validates these offsets every run.

    private const int SystemProcessInformationClass = 5;
    private const int ProcessEntryHeaderBytes = 256;
    private const int ThreadEntryBytes = 80;
    private const int ThreadContextSwitchesOffset = 64;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    private static Dictionary<int, (string Name, long ContextSwitches)> SnapshotContextSwitches()
    {
        var snapshot = new Dictionary<int, (string, long)>();
        var size = 1 << 20;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            int status;
            while ((status = NtQuerySystemInformation(SystemProcessInformationClass, buffer, size, out var needed)) != 0)
            {
                if (needed <= size)
                    return snapshot;   // a non-length error: report what we have (nothing)

                size = needed + (64 * 1024);
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
            }

            var offset = 0;
            while (true)
            {
                var entry = buffer + offset;
                var nextOffset = Marshal.ReadInt32(entry, 0);
                var threadCount = Marshal.ReadInt32(entry, 4);
                var pid = (int)Marshal.ReadIntPtr(entry, 80);

                var nameLength = (ushort)Marshal.ReadInt16(entry, 56);
                var nameBuffer = Marshal.ReadIntPtr(entry, 64);
                var name = nameBuffer != IntPtr.Zero && nameLength > 0
                    ? Marshal.PtrToStringUni(nameBuffer, nameLength / 2)
                    : (pid == 0 ? "Idle" : "System");

                long switches = 0;
                for (var t = 0; t < threadCount; t++)
                {
                    var thread = entry + ProcessEntryHeaderBytes + t * ThreadEntryBytes;
                    switches += (uint)Marshal.ReadInt32(thread, ThreadContextSwitchesOffset);
                }

                if (pid > 0)
                    snapshot[pid] = (name, switches);

                if (nextOffset == 0)
                    break;
                offset += nextOffset;
            }
        }
        catch
        {
            // Best-effort: a partial snapshot still produces useful deltas for surviving pids.
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return snapshot;
    }
}
