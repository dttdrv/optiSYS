namespace OptiSYS.Core.Services;

/// <summary>One process's cumulative context-switch count at a sampling instant.</summary>
public readonly record struct ProcessWakeupSample(int ProcessId, long ContextSwitches);

/// <summary>
/// The C-state guardian's classifier. Battery life is package sleep-state residency, not CPU%:
/// a process waking hundreds of times a second at near-zero CPU keeps the SoC out of its deep
/// idle states and drains more than a clean burst — and it looks innocent in every CPU-ranked
/// list. This tracker classifies background processes by their context-switch RATE,
/// window-averaged over the last <see cref="WindowSize"/> observations with enter/exit
/// hysteresis (defaults 300/s in, 100/s out — grounded by the Lab `wakeup` probe: real
/// storm-makers run in the hundreds-to-thousands per second while the shell tail stays under
/// ~150/s). Same proven shape as <see cref="BackgroundDrainTracker"/>: time-normalized math
/// over irregular sweep spacing, pid-reuse reset on a decreasing counter, absent pids
/// forgotten. Pure and clock-free.
/// </summary>
public sealed class WakeupPressureTracker
{
    private const int WindowSize = 5;

    private readonly double _enterSwitchesPerSecond;
    private readonly double _exitSwitchesPerSecond;
    private readonly Dictionary<int, ProcessHistory> _histories = [];
    private readonly HashSet<int> _stormers = [];

    public WakeupPressureTracker(double enterSwitchesPerSecond = 300, double exitSwitchesPerSecond = 100)
    {
        _enterSwitchesPerSecond = enterSwitchesPerSecond;
        _exitSwitchesPerSecond = Math.Min(exitSwitchesPerSecond, enterSwitchesPerSecond);
    }

    /// <summary>Process ids currently classified as wakeup storm-makers.</summary>
    public IReadOnlySet<int> CurrentStormers => _stormers;

    /// <summary>
    /// Feed one sweep's samples. Processes absent from <paramref name="samples"/> are forgotten;
    /// a counter lower than previously seen means the pid was reused and resets that history.
    /// </summary>
    public void Observe(IReadOnlyList<ProcessWakeupSample> samples, DateTime utcNow)
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

            history.Append(utcNow, sample.ContextSwitches);
            UpdateClassification(sample.ProcessId, history);
        }

        foreach (var pid in _histories.Keys.Where(p => !seen.Contains(p)).ToList())
        {
            _histories.Remove(pid);
            _stormers.Remove(pid);
        }
    }

    private void UpdateClassification(int pid, ProcessHistory history)
    {
        var rate = history.WindowSwitchesPerSecond();
        if (rate is null)
        {
            _stormers.Remove(pid);
            return;
        }

        if (rate >= _enterSwitchesPerSecond)
        {
            _stormers.Add(pid);
        }
        else if (rate < _exitSwitchesPerSecond)
        {
            _stormers.Remove(pid);
        }
        // Between exit and enter: hysteresis — keep the previous classification.
    }

    private sealed class ProcessHistory
    {
        private readonly Queue<(DateTime Time, long Switches)> _window = new();

        public void Append(DateTime time, long switches)
        {
            // The cumulative counter only ever grows for a live process; a decrease means the
            // pid was reused by a fresh process — restart its history.
            if (_window.Count > 0 && switches < _window.Last().Switches)
                _window.Clear();

            _window.Enqueue((time, switches));
            while (_window.Count > WindowSize)
                _window.Dequeue();
        }

        public double? WindowSwitchesPerSecond()
        {
            if (_window.Count < 2)
                return null;

            var first = _window.Peek();
            var last = _window.Last();
            var elapsedSeconds = (last.Time - first.Time).TotalSeconds;
            if (elapsedSeconds <= 0)
                return null;

            return (last.Switches - first.Switches) / elapsedSeconds;
        }
    }
}
