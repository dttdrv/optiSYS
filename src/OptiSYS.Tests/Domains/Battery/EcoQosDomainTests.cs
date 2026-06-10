using Moq;
using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using Xunit;

namespace OptiSYS.Tests.Domains.Battery;

/// <summary>
/// Covers the DRAIN-AWARE (targeted) EcoQoS reconcile: only background processes with measured,
/// sustained CPU burn (the BackgroundDrainTracker's enter/exit hysteresis over real samples) are
/// throttled — never the foreground, shell, protected, audible, or quiet ones — plus dynamic
/// revert (no persisted PID snapshot — the live tracked set is authoritative). All process
/// enumeration, CPU sampling, and audio-session reads route through INativeBridge so the
/// reconcile is hermetic; the clock is injected so burn percentages are deterministic.
/// </summary>
public class EcoQosDomainTests
{
    private static readonly DateTime T0 = new(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);

    private DateTime _now = T0;
    private readonly Dictionary<int, double> _cpuSeconds = [];
    private readonly HashSet<int> _audible = [];

    private static NativeProcessInfo Proc(int pid, string name) =>
        new() { ProcessId = pid, ProcessName = name };

    private Mock<INativeBridge> Bridge(int foreground, params NativeProcessInfo[] processes)
    {
        var native = new Mock<INativeBridge>();
        native.Setup(n => n.GetForegroundProcessId()).Returns(foreground);
        native.Setup(n => n.GetProcessList()).Returns(processes);
        native.Setup(n => n.SetEcoQos(It.IsAny<bool>(), It.IsAny<int>())).Returns(true);
        native.Setup(n => n.GetProcessCpuTime(It.IsAny<int>()))
            .Returns<int>(pid => _cpuSeconds.TryGetValue(pid, out var s) ? TimeSpan.FromSeconds(s) : null);
        native.Setup(n => n.GetAudibleProcessIds()).Returns(() => _audible.ToList());

        // Every known process starts with a readable zero CPU time, so the first sweep records a
        // baseline sample; tests that need an unreadable process remove its entry explicitly.
        foreach (var p in processes)
            _cpuSeconds.TryAdd(p.ProcessId, 0);

        return native;
    }

    private EcoQosDomain Domain(Mock<INativeBridge> native, Settings? settings = null) =>
        new(settings ?? new Settings(), native.Object, () => _now);

    /// <summary>Advance the clock and reconcile — one adaptive sweep.</summary>
    private void Sweep(EcoQosDomain domain, double secondsLater = 10, bool widened = false)
    {
        _now = _now.AddSeconds(secondsLater);
        domain.Reconcile(widenToAllCandidates: widened);
    }

    /// <summary>Make a pid look like a sustained burner: 5% of one core per elapsed second.</summary>
    private void Burn(int pid, double seconds) =>
        _cpuSeconds[pid] = _cpuSeconds.GetValueOrDefault(pid) + seconds;

    [Fact]
    public void Ctor_NullBridge_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EcoQosDomain(new Settings(), null!));
    }

    [Fact]
    public void Reconcile_FirstSweep_ObservesButThrottlesNothing()
    {
        // One sample is no delta: nothing can be classified as a drainer yet, so the first sweep
        // must not touch anything (throttling without evidence is the old blanket behavior).
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"));
        var domain = Domain(native);

        domain.Reconcile();

        native.Verify(n => n.SetEcoQos(true, It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void Reconcile_SustainedBurner_GetsThrottled_QuietBackgroundDoesNot()
    {
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "burner"), Proc(1002, "quiet"));
        var domain = Domain(native);

        domain.Reconcile();              // first samples
        Burn(1001, 0.5);                 // 0.5s CPU over the next 10s = 5% of a core
        Sweep(domain);                   // classifies + throttles

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);
        native.Verify(n => n.SetEcoQos(true, 1002), Times.Never);
        native.Verify(n => n.SetEcoQos(true, 1000), Times.Never);
    }

    [Fact]
    public void Reconcile_AudibleBurner_IsNeverThrottled()
    {
        // An explicit EcoQoS overrides the OS's audio-gets-full-QoS heuristic, so a process with
        // an active audio session is exempt no matter how much it burns.
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "player"));
        var domain = Domain(native);
        _audible.Add(1001);

        domain.Reconcile();
        Burn(1001, 0.5);
        Sweep(domain);

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Never);
    }

    [Fact]
    public void Reconcile_ThrottledDrainer_IsReleasedWhenItStartsPlaying()
    {
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "becomes-player"));
        var domain = Domain(native);

        domain.Reconcile();
        Burn(1001, 0.5);
        Sweep(domain);                   // throttled as a drainer
        _audible.Add(1001);              // starts playing audio
        Burn(1001, 0.5);
        Sweep(domain);

        native.Verify(n => n.SetEcoQos(false, 1001), Times.Once);
    }

    [Fact]
    public void Reconcile_ShellAndProtectedBurners_AreNeverThrottled()
    {
        // "explorer" is a shell process; "chrome" is in the default ProtectedApplications list.
        var native = Bridge(1000,
            Proc(1000, "fg"), Proc(1001, "explorer"), Proc(1002, "chrome"), Proc(1003, "bgapp"));
        var domain = Domain(native);

        domain.Reconcile();
        Burn(1001, 0.5);
        Burn(1002, 0.5);
        Burn(1003, 0.5);
        Sweep(domain);

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Never);
        native.Verify(n => n.SetEcoQos(true, 1002), Times.Never);
        native.Verify(n => n.SetEcoQos(true, 1003), Times.Once);
    }

    [Fact]
    public void Reconcile_ReleasesDrainerThatBecameForeground()
    {
        var native = Bridge(1000, Proc(1000, "a"), Proc(1001, "b"));
        native.SetupSequence(n => n.GetForegroundProcessId())
            .Returns(1000).Returns(1000).Returns(1001);
        var domain = Domain(native);

        domain.Reconcile();
        Burn(1001, 0.5);
        Sweep(domain);                   // 1001 throttled as a drainer
        Sweep(domain);                   // 1001 took focus -> released

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);
        native.Verify(n => n.SetEcoQos(false, 1001), Times.Once);
    }

    [Fact]
    public void Reconcile_CooledDrainer_IsReleasedOnceItsWindowDecays()
    {
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "burst"));
        var domain = Domain(native);

        domain.Reconcile();
        Burn(1001, 0.5);
        Sweep(domain);                   // throttled (5%)
        Sweep(domain);                   // idle from here: window average decays...
        Sweep(domain);
        Sweep(domain);
        Sweep(domain);
        Sweep(domain);                   // ...burst slid out -> released

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);
        native.Verify(n => n.SetEcoQos(false, 1001), Times.Once);
    }

    [Fact]
    public void Reconcile_ReleasesAndDropsExitedDrainer()
    {
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"), Proc(1002, "leaving"));
        native.SetupSequence(n => n.GetProcessList())
            .Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp"), Proc(1002, "leaving") })
            .Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp"), Proc(1002, "leaving") })
            .Returns(new[] { Proc(1000, "fg"), Proc(1001, "bgapp") });   // 1002 exited
        var domain = Domain(native);

        domain.Reconcile();
        Burn(1002, 0.5);
        Sweep(domain);                   // 1002 throttled
        Sweep(domain);                   // 1002 gone -> released and dropped
        domain.Revert(new DomainSnapshot { DomainId = "ecoqos" });

        // 1002 released once when it left; Revert must NOT touch it again (dropped from tracked set).
        native.Verify(n => n.SetEcoQos(false, 1002), Times.Once);
    }

    [Fact]
    public void Revert_UnThrottlesLiveSet_IncludingDrainersAddedAfterApply()
    {
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"));
        var domain = Domain(native);

        var baseline = domain.CaptureBaseline();
        domain.Apply(baseline);          // first samples; nothing classified yet
        Burn(1001, 0.5);
        Sweep(domain);                   // dynamically throttles the drainer
        domain.Revert(baseline);

        native.Verify(n => n.SetEcoQos(false, 1001), Times.Once);
        Assert.False(domain.IsActive);
    }

    [Fact]
    public void Suspend_ReleasesThrottles_ButStaysEngaged()
    {
        // The "follow, never fight" stand-down: release everything (reversible, back to
        // OS-managed) but remain engaged, so the maintenance loop can re-throttle once the
        // user leaves the high-performance mode — without waiting for an AC->DC replug.
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"));
        var domain = Domain(native);
        domain.Apply(domain.CaptureBaseline());
        Burn(1001, 0.5);
        Sweep(domain);                   // 1001 throttled

        domain.Suspend();

        native.Verify(n => n.SetEcoQos(false, 1001), Times.Once);
        Assert.True(domain.IsActive);
    }

    [Fact]
    public void Revert_OnFreshInstance_IsNoOp()
    {
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"));
        var domain = Domain(native);

        domain.Revert(new DomainSnapshot { DomainId = "ecoqos" });

        native.Verify(n => n.SetEcoQos(false, It.IsAny<int>()), Times.Never);
        Assert.False(domain.IsActive);
    }

    [Fact]
    public void Apply_ReportsActive_BeforeTheFirstDrainerIsEvenFound()
    {
        // IsActive means "engaged" (applied and not reverted), NOT "currently throttling > 0
        // pids" — the first sweeps legitimately throttle nothing while evidence accumulates, and
        // the adaptive controller's IsActive gate must keep maintaining through that phase.
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"));
        var domain = Domain(native);

        var result = domain.Apply(domain.CaptureBaseline());

        Assert.True(result.Success);
        Assert.True(domain.IsActive);
        native.Verify(n => n.SetEcoQos(true, It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void Reconcile_UnreadableCpu_IsNeverThrottled()
    {
        // GetProcessCpuTime returning null means "can't measure" — without evidence the process
        // must not be throttled, no matter how long it sits in the background.
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "opaque"));
        var domain = Domain(native);
        _cpuSeconds.Remove(1001);   // the bridge mock returns null for it: CPU time unreadable

        domain.Reconcile();
        Sweep(domain);
        Sweep(domain);

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Never);
    }

    // ── Idle deep-saver: widened sweeps ──────────────────────────────

    [Fact]
    public void Reconcile_Widened_ThrottlesQuietCandidatesToo()
    {
        // With the user away the evidence gate relaxes: every candidate gets the hint without
        // waiting for burn history — battery savings start on the first idle sweep.
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "quiet"));
        var domain = Domain(native);

        domain.Reconcile(widenToAllCandidates: true);

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);
        native.Verify(n => n.SetEcoQos(true, 1000), Times.Never);
    }

    [Fact]
    public void Reconcile_Widened_StillExemptsShellProtectedAndAudible()
    {
        // The static exemptions are about safety, not evidence — they hold in every mode.
        // Audible especially: music keeps playing while the user is away.
        var native = Bridge(1000,
            Proc(1000, "fg"), Proc(1001, "explorer"), Proc(1002, "chrome"),
            Proc(1003, "player"), Proc(1004, "bg"));
        _audible.Add(1003);
        var domain = Domain(native);

        domain.Reconcile(widenToAllCandidates: true);

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Never);
        native.Verify(n => n.SetEcoQos(true, 1002), Times.Never);
        native.Verify(n => n.SetEcoQos(true, 1003), Times.Never);
        native.Verify(n => n.SetEcoQos(true, 1004), Times.Once);
    }

    [Fact]
    public void Reconcile_BackToTargeted_ReleasesWidenedThrottles_ButKeepsRealDrainers()
    {
        // First input after idle: the next targeted sweep's desired-set diff releases everything
        // that was throttled only by the widened mode, while measured drainers stay throttled.
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "drainer"), Proc(1002, "quiet"));
        var domain = Domain(native);

        domain.Reconcile();                      // baseline samples
        Burn(1001, 0.5);
        Sweep(domain, widened: true);            // idle: throttles BOTH (drainer + quiet)
        Sweep(domain);                           // user back: targeted again

        native.Verify(n => n.SetEcoQos(true, 1002), Times.Once);
        native.Verify(n => n.SetEcoQos(false, 1002), Times.Once);   // widened-only -> released
        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);    // drainer stays throttled
        native.Verify(n => n.SetEcoQos(false, 1001), Times.Never);
    }

    // ── Readback-aware reconcile ──────────────────────────────────────

    [Fact]
    public void Reconcile_SkipsDrainerAlreadyVerifiedThrottled_AndStillCountsItVerified()
    {
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "already"));
        native.Setup(n => n.IsEcoQosThrottled(1001)).Returns(true);
        var domain = Domain(native);

        domain.Reconcile();
        Burn(1001, 0.5);
        _now = _now.AddSeconds(10);
        var result = domain.Reconcile();

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Never);   // skip-if-already-throttled
        Assert.Contains("1 background drainers verified", result.Message);
    }

    [Fact]
    public void Reconcile_AppliesToNotThrottledDrainer_AndReportsVerifiedCount()
    {
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"));
        native.SetupSequence(n => n.IsEcoQosThrottled(1001))
            .Returns(false)   // pre-apply: not yet throttled
            .Returns(true);   // post-apply readback: confirmed
        var domain = Domain(native);

        domain.Reconcile();
        Burn(1001, 0.5);
        _now = _now.AddSeconds(10);
        var result = domain.Reconcile();

        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);
        Assert.Contains("1 background drainers verified", result.Message);
    }

    [Fact]
    public void Reconcile_NullReadback_FallsBackToAttempting_AndDoesNotThrow()
    {
        var native = Bridge(1000, Proc(1000, "fg"), Proc(1001, "bgapp"));
        var domain = Domain(native);

        domain.Reconcile();
        Burn(1001, 0.5);
        var ex = Record.Exception(() => Sweep(domain));

        Assert.Null(ex);
        native.Verify(n => n.SetEcoQos(true, 1001), Times.Once);    // attempted despite unknown state
    }
}
