using System.Diagnostics;
using OptiSYS.Core.Native;

namespace OptiSYS.Lab.Probes;

/// <summary>
/// Answers "which processes are actually burning CPU in the background?" — the measurement that
/// grounds the targeted-EcoQoS thresholds. Samples every process's total CPU time twice over a
/// window and reports the per-process delta as % of ONE core, separating the foreground process
/// (never throttled) from background burners. Read-only.
/// </summary>
public sealed class DrainProbe : ILabProbe
{
    private static readonly TimeSpan SampleWindow = TimeSpan.FromSeconds(8);

    public string Name => "drain";
    public string Description => "per-process background CPU burn over an 8s window (% of one core)";
    public bool RequiresElevation => false;

    public ProbeResult Run()
    {
        var bridge = new WindowsNativeBridge();
        var foregroundPid = bridge.GetForegroundProcessId();
        var selfPid = Environment.ProcessId;

        var first = SampleCpuTimes();
        var sw = Stopwatch.StartNew();
        Thread.Sleep(SampleWindow);
        var second = SampleCpuTimes();
        sw.Stop();

        var burns = new List<(int Pid, string Name, double CorePercent)>();
        foreach (var (pid, (name, cpuAfter)) in second)
        {
            if (!first.TryGetValue(pid, out var before))
                continue;   // started mid-window; no clean delta

            var corePercent = (cpuAfter - before.Cpu).TotalMilliseconds / sw.Elapsed.TotalMilliseconds * 100;
            if (corePercent > 0.05)
                burns.Add((pid, name, corePercent));
        }

        var background = burns
            .Where(b => b.Pid != foregroundPid && b.Pid != selfPid)
            .OrderByDescending(b => b.CorePercent)
            .ToList();

        var result = new ProbeResult { ProbeName = Name };

        var summary = new ProbeSection { Title = $"Background CPU burn over {sw.Elapsed.TotalSeconds:F1}s" };
        summary.Add("processes sampled", $"{second.Count}");
        summary.Add("background burners (>0.05% core)", $"{background.Count}");
        summary.Add("total background burn", $"{background.Sum(b => b.CorePercent):F1}% of one core");
        var audible = bridge.GetAudibleProcessIds();
        summary.Add("audible processes (EcoQoS-exempt)", audible.Count == 0
            ? "none"
            : string.Join(", ", audible.Select(pid =>
                $"{(second.TryGetValue(pid, out var s) ? s.Name : "?")} (pid {pid})")));
        var fgBurn = burns.FirstOrDefault(b => b.Pid == foregroundPid);
        summary.Add("foreground process", fgBurn.Name is { Length: > 0 }
            ? $"{fgBurn.Name} (pid {foregroundPid}) {fgBurn.CorePercent:F1}%"
            : $"pid {foregroundPid} (idle this window)");
        result.Sections.Add(summary);

        var top = new ProbeSection { Title = "Top background burners (% of one core)" };
        foreach (var b in background.Take(15))
            top.Add($"{b.Name} (pid {b.Pid})", $"{b.CorePercent:F2}%");
        if (background.Count == 0)
            top.Add("none", "background is quiet right now — re-run during normal use");
        result.Sections.Add(top);

        result.Sections.Add(new ProbeSection { Title = "How to read this" }
            .Add("threshold question", "a targeted EcoQoS should catch sustained burners well above this list's noise floor; re-run a few times during real use to see what a sensible enter threshold (e.g. 2-5% of a core) would and would not capture")
            .Add("method", "two Process.TotalProcessorTime samples per PID over the window; deltas are immune to per-tick scheduling noise but blind to processes that exit mid-window"));

        return result;
    }

    private static Dictionary<int, (string Name, TimeSpan Cpu)> SampleCpuTimes()
    {
        var map = new Dictionary<int, (string, TimeSpan)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                map[p.Id] = (p.ProcessName, p.TotalProcessorTime);
            }
            catch
            {
                // Access denied / exited — protected system processes are invisible to the
                // unelevated tool that would do the throttling too, so skipping them is faithful.
            }
            finally
            {
                p.Dispose();
            }
        }

        return map;
    }
}
