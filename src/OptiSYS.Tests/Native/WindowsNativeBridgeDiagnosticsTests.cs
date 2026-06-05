using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Native;
using Xunit;

namespace OptiSYS.Tests.Native;

/// <summary>
/// The bridge captures the Win32 last-error immediately after a failing native call and routes it
/// (operation name + numeric code) to the injected <see cref="IDiagnosticLog"/>, so a field failure
/// ("0 processes affected") is diagnosable as access-denied vs not-supported vs process-gone.
///
/// The real P/Invokes can't be made to fail deterministically in a unit test, so the seam is tested
/// at its smallest mockable boundary: a fake last-error reader + a recording log, driving the
/// internal failure-logging helper the production call sites all funnel through.
/// </summary>
public class WindowsNativeBridgeDiagnosticsTests
{
    private sealed class RecordingLog : IDiagnosticLog
    {
        public readonly List<(string level, string category, string message)> Entries = [];
        public void Write(string level, string category, string message) =>
            Entries.Add((level, category, message));
    }

    [Fact]
    public void LogWin32Failure_RecordsOperationName_AndWin32ErrorCode()
    {
        var log = new RecordingLog();
        using var bridge = new WindowsNativeBridge(log, lastError: () => 5); // ERROR_ACCESS_DENIED

        bridge.LogWin32Failure("SetEcoQos", 1234);

        var entry = Assert.Single(log.Entries);
        Assert.Equal("native", entry.category);
        Assert.Contains("SetEcoQos", entry.message);
        Assert.Contains("1234", entry.message);
        Assert.Contains("5", entry.message);
    }

    [Fact]
    public void LogWin32Failure_WithNullLog_DoesNotThrow()
    {
        // Degrade to no-op when no sink is supplied (default NullDiagnosticLog).
        using var bridge = new WindowsNativeBridge(log: null, lastError: () => 87);

        var ex = Record.Exception(() => bridge.LogWin32Failure("SetProcessMemoryPriority", 7));

        Assert.Null(ex);
    }

    [Fact]
    public void FailingSetEcoQos_OnUnopenableProcess_LogsWin32Failure()
    {
        // PID 0 (System Idle Process) cannot be opened with SET_INFORMATION, so the bridge's
        // OpenProcess fails and the boundary logging fires with a real last-error code. This
        // exercises the production call site end-to-end (not just the helper).
        var log = new RecordingLog();
        using var bridge = new WindowsNativeBridge(log);

        var ok = bridge.SetEcoQos(true, 0);

        Assert.False(ok);
        Assert.Contains(log.Entries, e => e.category == "native" && e.message.Contains("SetEcoQos"));
    }
}
