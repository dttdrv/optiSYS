namespace OptiSYS.Services;

internal static class StartupLog
{
    // ~1 MB cap so the single persistent artifact can't grow unbounded; rolls to one .old file.
    private const long MaxBytes = 1024 * 1024;

    private static readonly string LogDirectory =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "optiSYS",
            "logs");

    private static readonly StartupLogWriter Writer = new(LogDirectory, MaxBytes);

    public static string PathOnDisk => Writer.PathOnDisk;

    public static void Write(string message) => Writer.Write(message);

    public static void WriteException(string context, Exception exception) =>
        Write($"{context}: {exception.GetType().FullName}: {exception}");

    /// <summary>
    /// Startup triage: if the previous session never recorded a clean exit, log the unexpected end,
    /// then arm the running-marker for this session. Call once early in startup.
    /// </summary>
    public static void BeginSession()
    {
        if (Writer.PreviousSessionEndedUnexpectedly())
            Write("Previous session ended unexpectedly (no clean-exit marker)");
        Writer.BeginSession();
    }

    /// <summary>Records a clean shutdown so the next launch won't flag an unexpected end.</summary>
    public static void MarkCleanExit() => Writer.MarkCleanExit();
}
