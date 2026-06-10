using OptiSYS.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public sealed class StartupLogWriterTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "optiSYS-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Write_WhenLogExceedsCap_RollsToSingleOldFile_AndStartsFresh()
    {
        // Tiny cap so a couple of lines trip rotation deterministically.
        var writer = new StartupLogWriter(_dir, maxBytes: 200);

        for (var i = 0; i < 50; i++)
            writer.Write($"line {i} padding padding padding padding");

        var logPath = Path.Combine(_dir, "startup.log");
        var oldPath = logPath + ".old";

        Assert.True(File.Exists(logPath));
        Assert.True(File.Exists(oldPath));                       // rolled exactly once to .old
        Assert.True(new FileInfo(logPath).Length <= 200 + 256);  // fresh file stays near the cap
        Assert.False(File.Exists(oldPath + ".old"));             // only a SINGLE .old is kept
    }

    [Fact]
    public void DetectPreviousSession_FirstEverRun_IsNotReportedAsUnexpected()
    {
        var writer = new StartupLogWriter(_dir, maxBytes: 1_000_000);

        // No prior marker file at all (clean install) → not an unexpected end.
        Assert.False(writer.PreviousSessionEndedUnexpectedly());
    }

    [Fact]
    public void DetectPreviousSession_AfterCleanExit_IsNotUnexpected()
    {
        var writer = new StartupLogWriter(_dir, maxBytes: 1_000_000);
        writer.BeginSession();        // arms "running" state
        writer.MarkCleanExit();       // clean shutdown

        var next = new StartupLogWriter(_dir, maxBytes: 1_000_000);
        Assert.False(next.PreviousSessionEndedUnexpectedly());
    }

    [Fact]
    public void DetectPreviousSession_WhenNoCleanExit_IsReportedAsUnexpected()
    {
        var writer = new StartupLogWriter(_dir, maxBytes: 1_000_000);
        writer.BeginSession();        // arms "running" state, then "crashes" (no MarkCleanExit)

        var next = new StartupLogWriter(_dir, maxBytes: 1_000_000);
        Assert.True(next.PreviousSessionEndedUnexpectedly());
    }

    [Fact]
    public void MarkCleanExit_WhenThisProcessNeverArmedTheSession_LeavesTheMainSessionsMarkerIntact()
    {
        var mainSession = new StartupLogWriter(_dir, maxBytes: 1_000_000);
        mainSession.BeginSession();   // the long-running app arms its marker

        // A short-lived helper instance (e.g. the --provision-elevation child) exits cleanly
        // without ever arming a session of its own — it must not disarm the main session.
        var helperChild = new StartupLogWriter(_dir, maxBytes: 1_000_000);
        helperChild.MarkCleanExit();

        var next = new StartupLogWriter(_dir, maxBytes: 1_000_000);
        Assert.True(next.PreviousSessionEndedUnexpectedly());
    }
}
