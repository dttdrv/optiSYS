using Moq;
using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using Xunit;

namespace OptiSYS.Tests.Domains.Battery;

/// <summary>
/// Covers the adaptive (reconcile-based) EcoQoS behavior: continuous foreground-following
/// throttling of background processes, and dynamic revert (no persisted PID snapshot — the
/// live tracked set is authoritative, mirroring MemoryOptimizerDomain's inherent reversibility).
/// All process enumeration is routed through INativeBridge so the reconcile is hermetic.
/// </summary>
public class EcoQosDomainTests
{
    private static NativeProcessInfo Proc(int pid, string name) =>
        new() { ProcessId = pid, ProcessName = name };

    private static Mock<INativeBridge> Bridge(int foreground, params NativeProcessInfo[] processes)
    {
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetForegroundProcessId()).Returns(foreground);
        native.Setup(n => n.GetProcessList()).Returns(processes);
        native.Setup(n => n.SetEcoQos(It.IsAny<bool>(), It.IsAny<int>())).Returns(true);
        return native;
    }

    [Fact]
    public void Ctor_NullBridge_Throws()
    {
        // The bridge is required: omitting it must fail loudly at construction rather than silently
        // falling through to real Win32 (which defeats mockability and hits the live system in tests).
        Assert.Throws<ArgumentNullException>(() => new EcoQosDomain(new Settings(), null!));
    }

    [Fact]
    public void Reconcile_ThrottlesBackgroundProcesses_AndSkipsForeground()
    {
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"), Proc(1002, "bgapp2"));
        var domain = new EcoQosDomain(new Settings(), native.Object);

        domain.Reconcile();

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);
        native.Verify(n => n.SetEcoQos(true, 1002), Times.Once);
        native.Verify(n => n.SetEcoQos(true, 1000), Times.Never);
        Assert.True(domain.IsActive);
    }

    [Fact]
    public void Reconcile_SkipsShellAndProtectedProcesses()
    {
        // "explorer" is a shell process; "chrome" is in the default ProtectedApplications list.
        var native = Bridge(1000,
            Proc(1000, "fg"), Proc(1001, "explorer"), Proc(1002, "chrome"), Proc(1003, "bgapp"));
        var domain = new EcoQosDomain(new Settings(), native.Object);

        domain.Reconcile();

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Never);
        native.Verify(n => n.SetEcoQos(true, 1002), Times.Never);
        native.Verify(n => n.SetEcoQos(true, 1003), Times.Once);
        native.Verify(n => n.SetEcoQos(true, 1000), Times.Never);
    }

    [Fact]
    public void Reconcile_ReleasesProcessThatBecameForeground()
    {
        var native = new Mock<INativeBridge>();
        native.SetupSequence(n => n.GetForegroundProcessId()).Returns(1000).Returns(1001);
        native.Setup(n => n.GetProcessList()).Returns(new[] { Proc(1000, "a"), Proc(1001, "b") });
        native.Setup(n => n.SetEcoQos(It.IsAny<bool>(), It.IsAny<int>())).Returns(true);
        var domain = new EcoQosDomain(new Settings(), native.Object);

        domain.Reconcile();   // foreground = 1000 -> throttle 1001
        domain.Reconcile();   // foreground = 1001 -> release 1001, throttle the now-background 1000

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);
        native.Verify(n => n.SetEcoQos(false, 1001), Times.Once);
        native.Verify(n => n.SetEcoQos(true, 1000), Times.Once);
    }

    [Fact]
    public void Reconcile_CatchesNewlySpawnedBackgroundProcess()
    {
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetForegroundProcessId()).Returns(1000);
        native.SetupSequence(n => n.GetProcessList())
            .Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp") })
            .Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp"), Proc(1002, "spawned") });
        native.Setup(n => n.SetEcoQos(It.IsAny<bool>(), It.IsAny<int>())).Returns(true);
        var domain = new EcoQosDomain(new Settings(), native.Object);

        domain.Reconcile();
        domain.Reconcile();

        native.Verify(n => n.SetEcoQos(true, 1002), Times.Once);  // newly spawned, caught
        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);  // already throttled, not re-applied
    }

    [Fact]
    public void Reconcile_ReleasesAndDropsExitedProcess()
    {
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetForegroundProcessId()).Returns(1000);
        native.SetupSequence(n => n.GetProcessList())
            .Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp"), Proc(1002, "leaving") })
            .Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp") });   // 1002 exited
        native.Setup(n => n.SetEcoQos(It.IsAny<bool>(), It.IsAny<int>())).Returns(true);
        var domain = new EcoQosDomain(new Settings(), native.Object);

        domain.Reconcile();
        domain.Reconcile();
        domain.Revert(new DomainSnapshot { DomainId = "ecoqos" });

        // 1002 released once when it left; Revert must NOT touch it again (dropped from tracked set).
        native.Verify(n => n.SetEcoQos(false, 1002), Times.Once);
        native.Verify(n => n.SetEcoQos(false, 1001), Times.Once);
    }

    [Fact]
    public void Revert_UnThrottlesLiveSet_IncludingProcessesAddedAfterApply()
    {
        // The "dynamic like memory" contract: revert reflects the live tracked set, not a snapshot
        // captured at apply time — so a process throttled by a later reconcile is still un-throttled.
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetForegroundProcessId()).Returns(1000);
        native.SetupSequence(n => n.GetProcessList())
            .Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp") })
            .Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp"), Proc(1002, "spawned") });
        native.Setup(n => n.SetEcoQos(It.IsAny<bool>(), It.IsAny<int>())).Returns(true);
        var domain = new EcoQosDomain(new Settings(), native.Object);

        var baseline = domain.CaptureBaseline();
        domain.Apply(baseline);   // throttles 1001
        domain.Reconcile();       // dynamically throttles 1002
        domain.Revert(baseline);

        native.Verify(n => n.SetEcoQos(false, 1001), Times.Once);
        native.Verify(n => n.SetEcoQos(false, 1002), Times.Once);
        Assert.False(domain.IsActive);
    }

    [Fact]
    public void Revert_OnFreshInstance_IsNoOp()
    {
        // No persisted PID list to replay: after a crash, a fresh instance reverts to a clean no-op.
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"));
        var domain = new EcoQosDomain(new Settings(), native.Object);

        domain.Revert(new DomainSnapshot { DomainId = "ecoqos" });

        native.Verify(n => n.SetEcoQos(false, It.IsAny<int>()), Times.Never);
        Assert.False(domain.IsActive);
    }

    [Fact]
    public void Apply_ThrottlesBackground_AndReportsActive()
    {
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"));
        var domain = new EcoQosDomain(new Settings(), native.Object);

        var result = domain.Apply(domain.CaptureBaseline());

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);
        Assert.True(result.Success);
        Assert.True(domain.IsActive);
    }

    // ── Readback-aware reconcile (item #25) ──────────────────────────

    [Fact]
    public void Reconcile_SkipsProcessAlreadyVerifiedThrottled_AndStillCountsItVerified()
    {
        // 1001 is already in EcoQoS (e.g. the OS classified it, or a prior session did): the bridge
        // reports it throttled, so we must NOT re-apply it, but it still counts toward verified.
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "already"));
        native.Setup(n => n.IsEcoQosThrottled(1001)).Returns(true);
        var domain = new EcoQosDomain(new Settings(), native.Object);

        var result = domain.Reconcile();

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Never);   // skip-if-already-throttled
        Assert.Contains("1 background processes verified", result.Message);
        Assert.True(domain.IsActive);
    }

    [Fact]
    public void Reconcile_AppliesToNotThrottledProcess_AndReportsVerifiedCount()
    {
        // 1001 reads back as not throttled before apply, throttled after apply -> verified.
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"));
        native.SetupSequence(n => n.IsEcoQosThrottled(1001))
            .Returns(false)   // pre-apply: not yet throttled
            .Returns(true);   // post-apply readback: confirmed
        var domain = new EcoQosDomain(new Settings(), native.Object);

        var result = domain.Reconcile();

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);    // applied (was not throttled)
        Assert.Contains("1 background processes verified", result.Message);
    }

    [Fact]
    public void Reconcile_SilentNoOp_AppliedButReadbackNotThrottled_NotCountedVerified()
    {
        // The write "succeeds" but readback shows the OS/driver ignored it: applied, NOT verified.
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"));
        native.Setup(n => n.IsEcoQosThrottled(1001)).Returns(false);   // never throttled, even post-apply
        var domain = new EcoQosDomain(new Settings(), native.Object);

        var result = domain.Reconcile();

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);       // attempted
        Assert.Contains("0 background processes verified", result.Message);
    }

    [Fact]
    public void Reconcile_NullReadback_FallsBackToAttempting_AndDoesNotThrow()
    {
        // Readback unavailable (access denied / exited) -> bridge returns null. Must fall back to
        // today's attempt-and-track behavior and never throw. (Bridge() leaves IsEcoQosThrottled
        // unmocked, so it returns null by default.)
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"));
        var domain = new EcoQosDomain(new Settings(), native.Object);

        var ex = Record.Exception(() => domain.Reconcile());

        Assert.Null(ex);
        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);       // attempted despite unknown state
        Assert.True(domain.IsActive);
    }
}
