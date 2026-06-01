using System.Diagnostics;

namespace OptiSYS.Lab.Probes;

/// <summary>
/// Answers "does the cpu-parking max-state cap actually do anything, and how does it interact with
/// Windows efficiency mode?" by reporting, for the ACTIVE power scheme:
///  - the applied AC/DC min &amp; max processor-state caps (read via powercfg — the same values
///    CpuParkingDomain writes), and
///  - the live "% Processor Performance" counter (reads &gt;100 when turbo is active; ceilings near the
///    max-state cap when the cap actually bites).
/// Read-only. Run it with the cap on vs off, and with Windows Energy Saver on vs off, and compare
/// the "% Processor Performance" ceiling to see whether the cap or EPP is the binding constraint.
/// </summary>
public sealed class CpuPerfProbe : ILabProbe
{
    private readonly bool _load;

    /// <param name="load">When true, saturate all cores during sampling so the % Processor
    /// Performance ceiling reflects the cap rather than idle demand.</param>
    public CpuPerfProbe(bool load = false) => _load = load;

    public string Name => "cpuperf";
    public string Description => "CPU processor-state caps vs live performance % (add --load to stress-test the cap)";
    public bool RequiresElevation => false;

    public ProbeResult Run()
    {
        var result = new ProbeResult { ProbeName = Name };

        var caps = new ProbeSection { Title = "Active scheme processor-state caps (powercfg)" };
        AddCap(caps, "Maximum processor state", "PROCTHROTTLEMAX");
        AddCap(caps, "Minimum processor state", "PROCTHROTTLEMIN");
        result.Sections.Add(caps);

        var live = new ProbeSection
        {
            Title = _load
                ? $"Live CPU performance UNDER LOAD ({Environment.ProcessorCount} cores saturated, ~3s)"
                : "Live CPU performance (idle — reflects demand, not the cap; use --load to test the cap)"
        };
        try
        {
            using var perf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
            perf.NextValue();

            using var load = _load ? new CpuLoad() : null;

            var samples = new List<float>();
            int n = _load ? 6 : 3;
            for (int i = 0; i < n; i++)
            {
                Thread.Sleep(500);
                samples.Add(perf.NextValue());
            }
            live.Add("% Processor Performance", string.Join(", ", samples.Select(s => $"{s:F0}%")));
            live.Add("observed ceiling", $"{samples.Max():F0}%");
            live.Add("vs DC max-state cap", CompareToCap(samples.Max()));
        }
        catch (Exception ex)
        {
            live.Add("counter", $"unavailable: {ex.Message}");
        }
        result.Sections.Add(live);

        result.Sections.Add(new ProbeSection { Title = "How to read this" }
            .Add("method", "On battery, run `cpuperf --load` with optiSYS ACTIVE (cap=85) then PAUSED (cap=100); compare the observed ceiling. A ceiling that tracks the cap proves it bites.")
            .Add("note", "PROCTHROTTLEMAX (the cap) and Windows Energy Saver / EPP are different knobs — a frequency ceiling vs an efficiency bias — and they coexist. --load isolates the cap's effect."));

        return result;
    }

    /// <summary>
    /// State the relationship between the observed ceiling and the configured DC cap WITHOUT
    /// claiming causation from a single run. A ceiling well below the cap means some OTHER limit
    /// (thermal / package-power / all-core turbo) is binding, not PROCTHROTTLEMAX. The only sound
    /// proof the cap bites is comparing two runs at different cap values (see "How to read this").
    /// </summary>
    private string CompareToCap(float ceiling)
    {
        if (!_load)
            return "idle reading — does NOT indicate the cap; re-run with --load on battery";

        int? dcCap = null;
        try { dcCap = PowercfgProcessorParser.ParseAcDcIndex(RunPowercfg("PROCTHROTTLEMAX")).dc; } catch { }

        if (dcCap is null)
            return $"observed {ceiling:F0}% under load; could not read DC cap to compare";
        if (ceiling > dcCap + 3)
            return $"observed {ceiling:F0}% EXCEEDS the {dcCap}% cap → cap not enforced on this run (likely on AC)";
        if (ceiling < dcCap - 5)
            return $"observed {ceiling:F0}% is BELOW the {dcCap}% cap → another limit (thermal/TDP/all-core turbo) is binding, not the cap";
        return $"observed {ceiling:F0}% ≈ the {dcCap}% cap → consistent with the cap binding, but confirm by comparing a cap=100 run";
    }

    private static void AddCap(ProbeSection section, string label, string settingAlias)
    {
        try
        {
            var output = RunPowercfg(settingAlias);
            var (ac, dc) = PowercfgProcessorParser.ParseAcDcIndex(output);
            section.Add($"{label} (AC)", ac.HasValue ? $"{ac}%" : "n/a");
            section.Add($"{label} (DC)", dc.HasValue ? $"{dc}%" : "n/a");
        }
        catch (Exception ex)
        {
            section.Add(label, $"error: {ex.Message}");
        }
    }

    private static string RunPowercfg(string settingAlias)
    {
        var psi = new ProcessStartInfo("powercfg", $"/q SCHEME_CURRENT SUB_PROCESSOR {settingAlias}")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("powercfg did not start");
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10_000);
        return output;
    }

    /// <summary>Saturates every logical core with tight FP work until disposed, so the % Processor
    /// Performance counter reflects the cap ceiling rather than idle demand.</summary>
    private sealed class CpuLoad : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread[] _threads;

        public CpuLoad()
        {
            _threads = new Thread[Environment.ProcessorCount];
            for (int i = 0; i < _threads.Length; i++)
            {
                _threads[i] = new Thread(() =>
                {
                    var token = _cts.Token;
                    double x = 1.0000001;
                    while (!token.IsCancellationRequested)
                        x = Math.Sqrt(x) * 1.0000001 + 1.0; // tight, non-elidable FP busy-loop
                    GC.KeepAlive(x);
                }) { IsBackground = true, Priority = ThreadPriority.Highest };
                _threads[i].Start();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            foreach (var t in _threads) t.Join(1000);
            _cts.Dispose();
        }
    }
}
