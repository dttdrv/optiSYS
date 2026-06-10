using Moq;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class MemoryOptimizerTests
{
    // Fake over the system-memory seam: records every memory-list command and models available
    // physical as a counter that each command lifts by a fixed reclaim, so a deeper pass (more
    // commands) yields a larger before→after delta — exactly what OptimizeAll's FreedBytes reports.
    private sealed class FakeSystemOps : IMemorySystemOps
    {
        private long _available;
        private readonly long _reclaimPerCommand;
        public List<NativeMethods.MemoryListCommand> Commands { get; } = new();

        public FakeSystemOps(long initialAvailable, long reclaimPerCommand)
        {
            _available = initialAvailable;
            _reclaimPerCommand = reclaimPerCommand;
        }

        public long AvailablePhysicalBytes() => _available;

        public bool RunMemoryListCommand(NativeMethods.MemoryListCommand command)
        {
            Commands.Add(command);
            _available += _reclaimPerCommand;
            return true;
        }
    }

    private static Mock<IMemoryInfoService> MemoryInfo(long totalBytes, long compressedBytes = 0)
    {
        var mock = new Mock<IMemoryInfoService>();
        mock.Setup(m => m.GetCurrentMemoryInfo()).Returns(new MemoryInfo
        {
            TotalPhysicalBytes = totalBytes,
            CompressedBytes = compressedBytes,
        });
        return mock;
    }

    private static Mock<INativeBridge> EmptyProcessBridge()
    {
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetForegroundProcessId()).Returns(0);
        native.Setup(n => n.GetProcessList()).Returns([]);   // no trim candidates → trimmedBytes == 0
        return native;
    }

    private static readonly NativeMethods.MemoryListCommand SystemEmpty =
        NativeMethods.MemoryListCommand.MemoryEmptyWorkingSets;
    private static readonly NativeMethods.MemoryListCommand StandbyPurge =
        NativeMethods.MemoryListCommand.MemoryPurgeStandbyList;
    private static readonly NativeMethods.MemoryListCommand LowPriorityPurge =
        NativeMethods.MemoryListCommand.MemoryPurgeLowPriorityStandbyList;

    [Fact]
    public void OptimizeAll_TargetZero_AggressiveRunsDeepSteps_BalancedDoesNot_AndFreesMore()
    {
        const long total = 100_000_000;
        const long perCommand = 10_000_000;   // 10 MB reclaim per system command

        var balancedOps = new FakeSystemOps(initialAvailable: 1_000_000, reclaimPerCommand: perCommand);
        using var balanced = new MemoryOptimizer(MemoryInfo(total).Object, EmptyProcessBridge().Object, balancedOps);
        var balancedResult = balanced.OptimizeAll(
            level: OptimizationLevel.Balanced, targetThresholdPercent: 0,
            accessedBitsDelayMs: 0, effectivenessTrackingEnabled: false);

        var aggressiveOps = new FakeSystemOps(initialAvailable: 1_000_000, reclaimPerCommand: perCommand);
        using var aggressive = new MemoryOptimizer(MemoryInfo(total).Object, EmptyProcessBridge().Object, aggressiveOps);
        var aggressiveResult = aggressive.OptimizeAll(
            level: OptimizationLevel.Aggressive, targetThresholdPercent: 0,
            accessedBitsDelayMs: 0, effectivenessTrackingEnabled: false);

        // The deep system steps run ONLY for Aggressive.
        Assert.DoesNotContain(SystemEmpty, balancedOps.Commands);
        Assert.DoesNotContain(StandbyPurge, balancedOps.Commands);
        Assert.Contains(SystemEmpty, aggressiveOps.Commands);
        Assert.Contains(StandbyPurge, aggressiveOps.Commands);

        Assert.Equal(OptimizationLevel.Aggressive, aggressiveResult.ActualLevelUsed);
        Assert.Equal(OptimizationLevel.Balanced, balancedResult.ActualLevelUsed);

        // FreedBytes reflects the whole-pass available increase, so the deeper pass frees at least
        // as much (here strictly more, since it runs the two extra reclaim commands).
        Assert.True(aggressiveResult.FreedBytes >= balancedResult.FreedBytes);
        Assert.True(aggressiveResult.FreedBytes > balancedResult.FreedBytes);
    }

    [Fact]
    public void OptimizeAll_WithTarget_WhenTrimClearsThreshold_EarlyExitsAtConservative()
    {
        const long total = 100_000_000;
        // Available 80 MB of 100 MB → 20% usage, below the 75% target: the post-trim early-exit fires.
        var ops = new FakeSystemOps(initialAvailable: 80_000_000, reclaimPerCommand: 10_000_000);
        using var optimizer = new MemoryOptimizer(MemoryInfo(total).Object, EmptyProcessBridge().Object, ops);

        var result = optimizer.OptimizeAll(
            level: OptimizationLevel.Aggressive,   // requested Max, but pressure already cleared
            targetThresholdPercent: 75,
            accessedBitsDelayMs: 0, effectivenessTrackingEnabled: false);

        Assert.Equal(OptimizationLevel.Conservative, result.ActualLevelUsed);
        Assert.Empty(ops.Commands);   // no Balanced/Aggressive steps ran — pure trim-only early exit
        Assert.DoesNotContain(SystemEmpty, ops.Commands);
        Assert.DoesNotContain(StandbyPurge, ops.Commands);
    }

    [Fact]
    public void OptimizeAll_HighCompression_DowngradesAggressiveToBalanced()
    {
        const long total = 100_000_000;
        const long compressed = 20_000_000;   // 20% compressed > 0.15 cap → don't purge (would re-fault)
        var ops = new FakeSystemOps(initialAvailable: 1_000_000, reclaimPerCommand: 10_000_000);
        using var optimizer = new MemoryOptimizer(
            MemoryInfo(total, compressed).Object, EmptyProcessBridge().Object, ops);

        var result = optimizer.OptimizeAll(
            level: OptimizationLevel.Aggressive, targetThresholdPercent: 0,
            accessedBitsDelayMs: 0, effectivenessTrackingEnabled: false);

        Assert.Equal(OptimizationLevel.Balanced, result.ActualLevelUsed);
        Assert.Contains("Level capped (high compression)", result.MethodsUsed);
        Assert.Contains(LowPriorityPurge, ops.Commands);   // Balanced tier still runs
        Assert.DoesNotContain(SystemEmpty, ops.Commands);  // Aggressive deep steps suppressed
        Assert.DoesNotContain(StandbyPurge, ops.Commands);
    }

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
    public void IdentifyHotPids_FlagsOnlyProcessesBurningDuringTheDwell()
    {
        // pid 1 burns 50ms across the dwell (25% of a core) -> hot. pid 2 burns 1ms -> cold.
        // pid 3 is unreadable -> never hot (the gate must not act on a guess).
        var calls = new Dictionary<int, int>();
        TimeSpan? CpuOf(int pid)
        {
            var n = calls[pid] = calls.GetValueOrDefault(pid) + 1;
            return pid switch
            {
                1 => TimeSpan.FromMilliseconds(n == 1 ? 0 : 50),
                2 => TimeSpan.FromMilliseconds(n == 1 ? 0 : 1),
                _ => null,
            };
        }

        var dwelled = TimeSpan.Zero;
        var hot = MemoryOptimizer.IdentifyHotPids([1, 2, 3], CpuOf, d => dwelled = d);

        Assert.Contains(1, hot);
        Assert.DoesNotContain(2, hot);
        Assert.DoesNotContain(3, hot);
        Assert.True(dwelled > TimeSpan.Zero);   // the dwell really separates the two samples
    }

    [Fact]
    public void TrimProcessWorkingSets_SkipsHotProcesses_AndTrimsColdOnes()
    {
        // Trimming a process that is actively executing is counterproductive — it re-faults its
        // working set straight back. The heat gate skips it; the idle one is trimmed as before.
        var memoryInfo = new Mock<IMemoryInfoService>();
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetForegroundProcessId()).Returns(7);
        native.Setup(n => n.GetProcessList()).Returns([
            new NativeProcessInfo { ProcessId = 42, ProcessName = "cold", WorkingSetBytes = 64L * 1024 * 1024 },
            new NativeProcessInfo { ProcessId = 44, ProcessName = "hot", WorkingSetBytes = 64L * 1024 * 1024 },
        ]);
        native.SetupSequence(n => n.GetProcessCpuTime(42))
            .Returns(TimeSpan.Zero).Returns(TimeSpan.Zero);                      // idle through the dwell
        native.SetupSequence(n => n.GetProcessCpuTime(44))
            .Returns(TimeSpan.Zero).Returns(TimeSpan.FromMilliseconds(40));     // 20% of a core
        native.Setup(n => n.TrimProcessWorkingSet(42)).Returns(50L * 1024 * 1024);

        using var optimizer = new MemoryOptimizer(memoryInfo.Object, native.Object)
        {
            ExcludedProcesses = [],
        };

        var result = optimizer.TrimProcessWorkingSets();

        Assert.Equal(1, result.trimmed);
        native.Verify(n => n.TrimProcessWorkingSet(42), Times.Once);
        native.Verify(n => n.TrimProcessWorkingSet(44), Times.Never);
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

    [Fact]
    public void RestoreBackgroundMemoryPriority_ConcurrentWithItself_DoesNotThrow()
    {
        // The lowered-priority map is written by the threadpool watcher and snapshot+cleared by
        // revert/dispose. Concurrent access must not throw "Collection was modified": the lock makes
        // the snapshot+clear atomic, so two simultaneous restores both complete cleanly.
        var memoryInfo = new Mock<IMemoryInfoService>();
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetProcessList()).Returns(
            Enumerable.Range(1, 200)
                .Select(i => new NativeProcessInfo { ProcessId = i, ProcessName = "OneDrive" })
                .ToArray());
        native.Setup(n => n.GetProcessMemoryPriority(It.IsAny<int>())).Returns(5);
        native.Setup(n => n.SetProcessMemoryPriority(It.IsAny<int>(), It.IsAny<uint>())).Returns(true);

        using var optimizer = new MemoryOptimizer(memoryInfo.Object, native.Object);
        optimizer.HintBackgroundMemoryPriority();

        var ex = Record.Exception(() =>
            Parallel.Invoke(
                () => optimizer.RestoreBackgroundMemoryPriority(),
                () => optimizer.RestoreBackgroundMemoryPriority()));

        Assert.Null(ex);
    }
}
