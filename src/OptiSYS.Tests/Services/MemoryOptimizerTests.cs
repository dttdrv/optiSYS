using Moq;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class MemoryOptimizerTests
{
    [Fact]
    public void TrimProcessWorkingSets_UsesNativeBridgeProcessListAndTrim()
    {
        var memoryInfo = new Mock<IMemoryInfoService>();
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetForegroundProcessId()).Returns(7);
        native.Setup(n => n.GetProcessList()).Returns([
            new NativeProcessInfo
            {
                ProcessId = 42,
                ProcessName = "candidate",
                WorkingSetBytes = 64L * 1024 * 1024,
                PrivateBytes = 16L * 1024 * 1024,
                PriorityClass = ProcessPriorityClass.Normal,
            },
            new NativeProcessInfo
            {
                ProcessId = 43,
                ProcessName = "tiny",
                WorkingSetBytes = 4L * 1024 * 1024,
                PrivateBytes = 2L * 1024 * 1024,
                PriorityClass = ProcessPriorityClass.Normal,
            },
        ]);
        native.Setup(n => n.TrimProcessWorkingSet(42)).Returns(50L * 1024 * 1024);   // 50 MB actually freed

        using var optimizer = new MemoryOptimizer(memoryInfo.Object, native.Object)
        {
            ExcludedProcesses = [],
        };

        var result = optimizer.TrimProcessWorkingSets();

        Assert.Equal(1, result.trimmed);
        Assert.Equal(0, result.failed);
        Assert.Equal(1, result.skipped);
        Assert.Equal(50L * 1024 * 1024, result.freedBytes);   // honest "Freed": real reclaimed bytes, not a vanity delta
        native.Verify(n => n.TrimProcessWorkingSet(42), Times.Once);
        native.Verify(n => n.TrimProcessWorkingSet(43), Times.Never);
    }

    [Fact]
    public void HintBackgroundMemoryPriority_CapturesPriorValue_AndRestoreRestoresIt()
    {
        // OneDrive is on the curated background allowlist; we capture its prior memory priority
        // before lowering so revert can put the EXACT prior value back (not just a guess).
        var memoryInfo = new Mock<IMemoryInfoService>();
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetProcessList()).Returns([
            new NativeProcessInfo { ProcessId = 100, ProcessName = "OneDrive" },
            new NativeProcessInfo { ProcessId = 200, ProcessName = "Dropbox" },
        ]);
        // Prior priorities: NORMAL (5) and MEDIUM (3) — both must be restored verbatim.
        native.Setup(n => n.GetProcessMemoryPriority(100)).Returns(5);
        native.Setup(n => n.GetProcessMemoryPriority(200)).Returns(3);
        native.Setup(n => n.SetProcessMemoryPriority(It.IsAny<int>(), It.IsAny<uint>())).Returns(true);

        using var optimizer = new MemoryOptimizer(memoryInfo.Object, native.Object);

        var hinted = optimizer.HintBackgroundMemoryPriority();

        Assert.Equal(2, hinted);
        native.Verify(n => n.SetProcessMemoryPriority(100, 2u), Times.Once);   // lowered to LOW
        native.Verify(n => n.SetProcessMemoryPriority(200, 2u), Times.Once);

        optimizer.RestoreBackgroundMemoryPriority();

        native.Verify(n => n.SetProcessMemoryPriority(100, 5u), Times.Once);   // restored to captured NORMAL
        native.Verify(n => n.SetProcessMemoryPriority(200, 3u), Times.Once);   // restored to captured MEDIUM
    }

    [Fact]
    public void RestoreBackgroundMemoryPriority_WhenPriorUnreadable_RestoresToNormal()
    {
        // If the prior value couldn't be read (returns 0), revert falls back to NORMAL (5) so the
        // process is never left stuck at the lowered hint.
        var memoryInfo = new Mock<IMemoryInfoService>();
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetProcessList()).Returns([
            new NativeProcessInfo { ProcessId = 100, ProcessName = "OneDrive" },
        ]);
        native.Setup(n => n.GetProcessMemoryPriority(100)).Returns(0);   // unreadable
        native.Setup(n => n.SetProcessMemoryPriority(It.IsAny<int>(), It.IsAny<uint>())).Returns(true);

        using var optimizer = new MemoryOptimizer(memoryInfo.Object, native.Object);

        optimizer.HintBackgroundMemoryPriority();
        optimizer.RestoreBackgroundMemoryPriority();

        native.Verify(n => n.SetProcessMemoryPriority(100, 5u), Times.Once);   // fallback NORMAL
    }
}
