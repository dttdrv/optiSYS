namespace OptiSYS.Core.Services;

/// <summary>One process's total CPU time at a sampling instant.</summary>
public readonly record struct ProcessCpuSample(int ProcessId, TimeSpan TotalCpuTime);

/// <summary>
/// Classifies background processes as drainers from observed CPU-time deltas. Burn is the
/// window-average percent of ONE core: (cpu[last] - cpu[first]) / (t[last] - t[first]), over the
/// last <see cref="WindowSize"/> observations — so a hot burst enters while it burns and decays
/// out as the window cools, and irregular sweep cadences (6s..60s apart) are handled by the
/// time normalization rather than assumptions about spacing.
///
/// Enter/exit hysteresis (defaults 3% / 1%) is grounded by the OptiSYS.Lab `drain` probe: real
/// background burners sit well above 3% of a core sustained while the shell/utility noise floor
/// stays under 2%, so the gray zone between the lines keeps borderline processes from flapping.
///
/// Pure and clock-free: timestamps come in with the samples. Not thread-safe by itself — the
/// owner observes from one sweep at a time.
/// </summary>
public sealed class BackgroundDrainTracker
{
    private const int WindowSize = 5;

    private readonly double _enterCorePercent;
    private readonly double _exitCorePercent;
    private readonly Dictionary<int, ProcessHistory> _histories = [];
    private readonly HashSet<int> _drainers = [];

    public BackgroundDrainTracker(double enterCorePercent = 3.0, double exitCorePercent = 1.0)
    {
        _enterCorePercent = enterCorePercent;
        _exitCorePercent = Math.Min(exitCorePercent, enterCorePercent);
    }

    /// <summary>Process ids currently classified as background drainers.</summary>
    public IReadOnlySet<int> CurrentDrainers => _drainers;

    /// <summary>
    /// Feed one sweep's samples. Processes absent from <paramref name="samples"/> are forgotten;
    /// a CPU time lower than previously seen means the pid was reused and resets that history.
    /// </summary>
    public void Observe(IReadOnlyList<ProcessCpuSample> samples, DateTime utcNow)
    {
        var seen = new HashSet<int>();
        foreach (var sample in samples)
        {
            seen.Add(sample.ProcessId);

            if (!_histories.TryGetValue(sample.ProcessId, out var history))
            {
                history = new ProcessHistory();
                _histories[sample.ProcessId] = history;
            }

            history.Append(utcNow, sample.TotalCpuTime);
            UpdateClassification(sample.ProcessId, history);
        }

        // Forget what no longer exists (or became invisible): a gone process cannot drain.
        foreach (var pid in _histories.Keys.Where(p => !seen.Contains(p)).ToList())
        {
            _histories.Remove(pid);
            _drainers.Remove(pid);
        }
    }

    private void UpdateClassification(int pid, ProcessHistory history)
    {
        var burn = history.WindowCorePercent();
        if (burn is null)
        {
            _drainers.Remove(pid);   // no measurable window yet (single observation / pid reset)
            return;
        }

        if (burn >= _enterCorePercent)
        {
            _drainers.Add(pid);
        }
        else if (burn < _exitCorePercent)
        {
            _drainers.Remove(pid);
        }
        // Between exit and enter: hysteresis — keep the previous classification.
    }

    private sealed class ProcessHistory
    {
        private readonly Queue<(DateTime Time, TimeSpan Cpu)> _window = new();

        public void Append(DateTime time, TimeSpan cpu)
        {
            // CPU time only ever grows for a live process; a decrease means the pid was reused
            // by a fresh process — restart its history rather than produce a negative burn.
            if (_window.Count > 0 && cpu < _window.Last().Cpu)
                _window.Clear();

            _window.Enqueue((time, cpu));
            while (_window.Count > WindowSize)
                _window.Dequeue();
        }

        /// <summary>Window-average burn as % of one core; null when a delta isn't measurable yet.</summary>
        public double? WindowCorePercent()
        {
            if (_window.Count < 2)
                return null;

            var first = _window.Peek();
            var last = _window.Last();
            var elapsed = (last.Time - first.Time).TotalMilliseconds;
            if (elapsed <= 0)
                return null;

            return (last.Cpu - first.Cpu).TotalMilliseconds / elapsed * 100.0;
        }
    }
}
