using System.Text;

namespace OptiSYS.Services;

/// <summary>
/// The testable heart of <see cref="StartupLog"/>: a single append-only log file with size
/// rotation, plus a session marker used to detect that the previous run ended without a clean
/// exit (the triage signal for the "app silently disappears" reports). Path is injectable so the
/// rotation + marker logic can be unit-tested; production wires it over the real LocalAppData path.
/// </summary>
internal sealed class StartupLogWriter
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _logPath;
    private readonly string _runningMarkerPath;
    private readonly long _maxBytes;

    public StartupLogWriter(string directory, long maxBytes)
    {
        _directory = directory;
        _logPath = Path.Combine(directory, "startup.log");
        _runningMarkerPath = Path.Combine(directory, "session.running");
        _maxBytes = maxBytes;
    }

    public string PathOnDisk => _logPath;

    public void Write(string message)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            RotateIfNeeded();
            using var stream = new FileStream(
                _logPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
        }
    }

    /// <summary>
    /// True when the prior session armed the running-marker but never cleared it via a clean exit —
    /// i.e. it crashed or was killed. False on a clean install (no marker) or after a clean exit.
    /// Must be read BEFORE <see cref="BeginSession"/> arms the marker for the current run.
    /// </summary>
    public bool PreviousSessionEndedUnexpectedly() => File.Exists(_runningMarkerPath);

    /// <summary>Arms the running-marker for the current session.</summary>
    public void BeginSession()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(_runningMarkerPath, DateTime.Now.ToString("o"));
        }
    }

    /// <summary>Clears the running-marker, recording that this session ended cleanly.</summary>
    public void MarkCleanExit()
    {
        lock (_gate)
        {
            try { File.Delete(_runningMarkerPath); } catch { }
        }
    }

    // Roll the current log to a single .old when it exceeds the cap, so the one persistent
    // artifact can't grow unbounded. Best-effort: a locked .old (rare) just leaves the log in place.
    private void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists || info.Length < _maxBytes)
                return;

            var oldPath = _logPath + ".old";
            File.Delete(oldPath);
            File.Move(_logPath, oldPath);
        }
        catch
        {
            // Rotation is best-effort; never let a logging call throw.
        }
    }
}
