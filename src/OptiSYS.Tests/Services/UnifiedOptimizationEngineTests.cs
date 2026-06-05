using Microsoft.Extensions.DependencyInjection;
using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class UnifiedOptimizationEngineTests
{
    private static string NewStorePath() =>
        Path.Combine(Path.GetTempPath(), "optiSYS-tests", Guid.NewGuid().ToString("N"), "snapshots.json");

    /// <summary>
    /// The canonical apply order — defined in exactly one place (AppHost DI registration) and
    /// pinned here. Apply runs forward in this order; Revert runs the reverse. This is the
    /// PRODUCTION order from <see cref="OptiSYS.AppHost.ConfigureServices"/>.
    /// </summary>
    private static readonly string[] CanonicalDomainOrder =
    [
        "ecoqos",
        "timer-resolution",
        "memory-optimize",
        "background-services",
        "usb-suspend",
        "network-power",
        "gpu-power",
        "cpu-parking",
        "disk-coalescing",
        "wifi-optimizer",
        "services-manual",
    ];

    /// <summary>Builds the production domain set in the canonical DI order — the single source.</summary>
    private static List<IOptimizationDomain> BuildProductionDomains()
    {
        var sc = new ServiceCollection();
        OptiSYS.AppHost.ConfigureServices(sc);
        using var provider = sc.BuildServiceProvider(validateScopes: false);
        return provider.GetServices<IOptimizationDomain>().ToList();
    }

    /// <summary>
    /// ONE source of truth: the engine must expose the domains in exactly the canonical
    /// production order, with no duplicate Ids — including WiFi and ServicesManual, and with
    /// Memory in its production position (3rd), not last. This pins the order the app actually
    /// runs so the engine can never drift to a private/divergent ordering again.
    /// </summary>
    [Fact]
    public void EngineDomainOrder_EqualsCanonicalProductionOrder_WithUniqueIds()
    {
        var snapshotStore = new SnapshotStore(NewStorePath());
        using var engine = new UnifiedOptimizationEngine(
            new Settings(), snapshotStore, BuildProductionDomains());

        var ids = engine.Domains.Select(d => d.Id).ToArray();

        Assert.Equal(CanonicalDomainOrder, ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());   // no duplicate Ids
    }

    [Fact]
    public void CrashRecovery_RestoresOriginalCpuValues_FromPersistedSnapshot()
    {
        // Simulate: Apply on battery, then "crash" (new engine over the SAME on-disk snapshot),
        // and assert recovery restores the ORIGINAL min/max — never re-captures the modified ones.
        var path = NewStorePath();
        var scheme = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var power = new FakePowerScheme(scheme, min: 5, max: 100, parking: 50);
        var settings = new Settings { CpuParkingMinProcessorDC = 0 };

        // Session 1: apply, then abandon WITHOUT reverting (the crash).
        var store1 = new SnapshotStore(path);
        using (var engine1 = new UnifiedOptimizationEngine(settings, store1, [new CpuParkingDomain(settings, power)]))
        {
            engine1.ActivateDomain("cpu-parking");
            Assert.Equal(0u, power.GetDc(NativeMethods.GUID_PROCESSOR_THROTTLE_MINIMUM));   // applied (min only)
        }

        // Session 2: fresh engine + fresh domain over the SAME snapshot file → crash recovery.
        var store2 = new SnapshotStore(path);
        using var engine2 = new UnifiedOptimizationEngine(settings, store2, [new CpuParkingDomain(settings, power)]);
        engine2.TryCrashRecovery();

        Assert.Equal(5u, power.GetDc(NativeMethods.GUID_PROCESSOR_THROTTLE_MINIMUM));    // ORIGINAL restored
        Assert.Equal(100u, power.GetDc(NativeMethods.GUID_PROCESSOR_THROTTLE_MAXIMUM));
        Assert.False(store2.HasSnapshots);   // clean recovery removes it
    }

    [Fact]
    public void CrashRecovery_WhenRevertWriteFails_RetainsSnapshot()
    {
        // Findings 1+6: if the restore write fails, recovery must KEEP the snapshot for a retry,
        // not delete the only copy of the originals.
        var path = NewStorePath();
        var scheme = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var power = new FakePowerScheme(scheme, min: 5, max: 100, parking: 50);
        var settings = new Settings { CpuParkingMinProcessorDC = 0 };

        var store1 = new SnapshotStore(path);
        using (var engine1 = new UnifiedOptimizationEngine(settings, store1, [new CpuParkingDomain(settings, power)]))
            engine1.ActivateDomain("cpu-parking");

        power.FailWritesFor.Add(NativeMethods.GUID_PROCESSOR_THROTTLE_MAXIMUM); // restore will fail

        var store2 = new SnapshotStore(path);
        using var engine2 = new UnifiedOptimizationEngine(settings, store2, [new CpuParkingDomain(settings, power)]);
        engine2.TryCrashRecovery();

        Assert.True(store2.HasSnapshots);   // retained for retry
    }

    [Fact]
    public void CrashRecovery_RegistryDomainRevertWriteFails_RetainsSnapshot()
    {
        // A registry-backed battery domain (GpuPower) now reports revert failure too: a failed
        // restore write must KEEP the snapshot so the next launch can retry, never discard the
        // only copy of the user's original GPU preference.
        var path = NewStorePath();
        var settings = new Settings { GpuPowerEnabled = true };

        var store = new SnapshotStore(path);
        var snapshot = new DomainSnapshot { DomainId = "gpu-power" };
        snapshot.Set("globalPreference", 1);   // user's original
        store.Store(snapshot);

        var reg = new FailingRegistryWriter();
        var domain = new GpuPowerDomain(reg);

        using var engine = new UnifiedOptimizationEngine(settings, store, [domain]);
        engine.TryCrashRecovery();

        Assert.True(store.HasSnapshots);   // retained for retry
    }

    [Fact]
    public void ActivateCategory_WhenApplyFails_EmitsDiagnostic()
    {
        var snapshotStore = new SnapshotStore(NewStorePath());
        using var domain = new RecordingDomain("wifi-optimizer", "Battery")
            { FailApply = true, Enable = s => s.WiFiOptimizerEnabled };
        var diag = new RecordingDiagnosticLog();

        using var engine = new UnifiedOptimizationEngine(
            new Settings { WiFiOptimizerEnabled = true },
            snapshotStore, [domain], diagnostics: diag);

        engine.ActivateCategory("Battery");

        Assert.Contains(diag.Entries, e => e.Contains("wifi-optimizer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CrashRecovery_WhenRevertFails_EmitsDiagnostic()
    {
        var path = NewStorePath();
        var settings = new Settings { GpuPowerEnabled = true };

        var store = new SnapshotStore(path);
        var snapshot = new DomainSnapshot { DomainId = "gpu-power" };
        snapshot.Set("globalPreference", 1);
        store.Store(snapshot);

        var reg = new FailingRegistryWriter();
        var domain = new GpuPowerDomain(reg);
        var diag = new RecordingDiagnosticLog();

        using var engine = new UnifiedOptimizationEngine(
            settings, store, [domain], diagnostics: diag);
        engine.TryCrashRecovery();

        Assert.Contains(diag.Entries, e => e.Contains("gpu-power", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingDiagnosticLog : IDiagnosticLog
    {
        public List<string> Entries { get; } = [];
        public void Write(string level, string category, string message) =>
            Entries.Add($"{level}|{category}|{message}");
    }

    private sealed class FailingRegistryWriter : IRegistryRestoreWriter
    {
        public bool SetDword(RegistryRoot root, string subKey, string name, int value) => false;
        public bool SetString(RegistryRoot root, string subKey, string name, string value) => false;
        public bool DeleteValue(RegistryRoot root, string subKey, string name) => false;
    }

    private sealed class FakePowerScheme : IPowerSchemeController
    {
        private readonly Guid _scheme;
        private readonly Dictionary<Guid, uint> _dc = [];
        public HashSet<Guid> FailWritesFor { get; } = [];

        public FakePowerScheme(Guid scheme, uint min, uint max, uint parking)
        {
            _scheme = scheme;
            _dc[NativeMethods.GUID_PROCESSOR_THROTTLE_MINIMUM] = min;
            _dc[NativeMethods.GUID_PROCESSOR_THROTTLE_MAXIMUM] = max;
            _dc[NativeMethods.GUID_PROCESSOR_CORE_PARKING_MIN_CORES] = parking;
        }

        public uint GetDc(Guid setting) => _dc[setting];
        public Guid GetActiveScheme() => _scheme;
        public uint? ReadDcValue(Guid scheme, Guid subgroup, Guid setting) =>
            _dc.TryGetValue(setting, out var v) ? v : null;
        public bool WriteDcValue(Guid scheme, Guid subgroup, Guid setting, uint value)
        {
            if (FailWritesFor.Contains(setting)) return false;
            _dc[setting] = value;
            return true;
        }
        public void SetActiveScheme(Guid scheme) { }
    }
    [Fact]
    public void Domains_PreserveInjectedOrder()
    {
        var snapshotStore = new SnapshotStore();
        var firstId = $"first-{Guid.NewGuid():N}";
        var secondId = $"second-{Guid.NewGuid():N}";
        using var first = new RecordingDomain(firstId, "Battery");
        using var second = new RecordingDomain(secondId, "Memory");

        using var engine = new UnifiedOptimizationEngine(
            new Settings(),
            snapshotStore,
            [first, second]);

        Assert.Collection(engine.Domains,
            domain => Assert.Equal(firstId, domain.Id),
            domain => Assert.Equal(secondId, domain.Id));
    }

    [Fact]
    public void RevertAll_TraversesDomainsInReverseInjectedOrder()
    {
        var snapshotStore = new SnapshotStore();
        var log = new List<string>();
        var firstId = $"first-{Guid.NewGuid():N}";
        var secondId = $"second-{Guid.NewGuid():N}";
        using var first = new RecordingDomain(firstId, "Battery", isActive: true, log);
        using var second = new RecordingDomain(secondId, "Battery", isActive: true, log);

        snapshotStore.Store(new DomainSnapshot { DomainId = firstId });
        snapshotStore.Store(new DomainSnapshot { DomainId = secondId });

        using var engine = new UnifiedOptimizationEngine(
            new Settings(),
            snapshotStore,
            [first, second]);

        var result = engine.RevertAll();

        Assert.True(result.Success);
        Assert.Equal([secondId, firstId], log);
        snapshotStore.RemoveRange([firstId, secondId]);
    }

    /// <summary>
    /// The enable gate moved from the engine's stringly-typed switch onto each domain
    /// (<see cref="IOptimizationDomain.IsEnabled"/>). This pins the exact historical
    /// id → Settings-flag mapping the old switch used, so the per-domain predicates can
    /// never drift from the behavior they replaced. Each domain is evaluated against a
    /// Settings whose flag for that id is true and a Settings whose flag is false.
    /// </summary>
    [Fact]
    public void EachDomain_IsEnabled_MatchesHistoricalSwitchMapping()
    {
        // (id, set the historical flag) → (id, clear the historical flag)
        var enable = new (string id, Action<Settings> set)[]
        {
            ("ecoqos",              s => s.EcoQosEnabled = true),
            ("timer-resolution",    s => s.TimerResolutionEnabled = true),
            ("background-services", s => s.BackgroundServicesEnabled = true),
            ("usb-suspend",         s => s.UsbSuspendEnabled = true),
            ("network-power",       s => s.NetworkPowerEnabled = true),
            ("gpu-power",           s => s.GpuPowerEnabled = true),
            ("cpu-parking",         s => s.CpuParkingEnabled = true),
            ("disk-coalescing",     s => s.DiskCoalescingEnabled = true),
            ("wifi-optimizer",      s => s.WiFiOptimizerEnabled = true),
            ("services-manual",     s => s.ServicesManualEnabled = true),
            ("memory-optimize",     s => s.AutoOptimizeMemoryEnabled = true),
        };
        var disable = new (string id, Action<Settings> clear)[]
        {
            ("ecoqos",              s => s.EcoQosEnabled = false),
            ("timer-resolution",    s => s.TimerResolutionEnabled = false),
            ("background-services", s => s.BackgroundServicesEnabled = false),
            ("usb-suspend",         s => s.UsbSuspendEnabled = false),
            ("network-power",       s => s.NetworkPowerEnabled = false),
            ("gpu-power",           s => s.GpuPowerEnabled = false),
            ("cpu-parking",         s => s.CpuParkingEnabled = false),
            ("disk-coalescing",     s => s.DiskCoalescingEnabled = false),
            ("wifi-optimizer",      s => s.WiFiOptimizerEnabled = false),
            ("services-manual",     s => s.ServicesManualEnabled = false),
            ("memory-optimize",     s => s.AutoOptimizeMemoryEnabled = false),
        };

        var domains = BuildProductionDomains();
        foreach (var (id, set) in enable)
        {
            var s = new Settings();
            set(s);
            var domain = domains.Single(d => d.Id == id);
            Assert.True(domain.IsEnabled(s), $"{id} should be enabled when its historical flag is true");
        }
        foreach (var (id, clear) in disable)
        {
            var s = new Settings();
            clear(s);
            var domain = domains.Single(d => d.Id == id);
            Assert.False(domain.IsEnabled(s), $"{id} should be disabled when its historical flag is false");
        }
    }

    [Fact]
    public void ActivateDomain_WiFiOptimizer_AppliesWhenExplicitlyEnabled()
    {
        // The Wi-Fi optimizer is OFF by default now (it net-degraded the connection on real
        // hardware), so it only applies when the user explicitly opts in. Isolated temp-file store
        // so HasSnapshots doesn't observe the shared on-disk file.
        var snapshotStore = new SnapshotStore(
            Path.Combine(Path.GetTempPath(), "optiSYS-tests", Guid.NewGuid().ToString("N"), "snapshots.json"));
        using var wifi = new RecordingDomain("wifi-optimizer", "Network")
            { Enable = s => s.WiFiOptimizerEnabled };

        using var engine = new UnifiedOptimizationEngine(
            new Settings { WiFiOptimizerEnabled = true }, snapshotStore, [wifi]);

        var result = engine.ActivateDomain("wifi-optimizer");

        Assert.True(wifi.IsActive);                // applied
        Assert.True(snapshotStore.HasSnapshots);   // baseline stored
    }

    [Fact]
    public void ActivateDomain_WiFiOptimizer_GatedOff_WhenSettingDisabled()
    {
        // The enable-gate still works both ways: an explicit opt-out is a clean no-op.
        var snapshotStore = new SnapshotStore(
            Path.Combine(Path.GetTempPath(), "optiSYS-tests", Guid.NewGuid().ToString("N"), "snapshots.json"));
        using var wifi = new RecordingDomain("wifi-optimizer", "Network")
            { Enable = s => s.WiFiOptimizerEnabled };

        using var engine = new UnifiedOptimizationEngine(
            new Settings { WiFiOptimizerEnabled = false }, snapshotStore, [wifi]);

        var result = engine.ActivateDomain("wifi-optimizer");

        Assert.False(wifi.IsActive);
        Assert.False(snapshotStore.HasSnapshots);
        Assert.Contains("not applicable", result.Message);
    }

    [Fact]
    public void ConcurrentMutations_AreSerialized_CaptureApplyRevertNeverInterleave()
    {
        // Two mutation paths driven from different threads (an ActivateDomain apply and a
        // RevertDomain revert against two distinct domains) must NOT have their critical sections
        // overlap. The engine must serialize every capture->store->apply / revert path under one
        // interlock so a power-transition revert on a timer thread can never interleave a UI-thread
        // apply. Deterministic: thread 1 enters Apply and parks; thread 2 must block at the engine
        // gate (cannot enter Revert) until thread 1 is released.
        var snapshotStore = new SnapshotStore(NewStorePath());

        var inFlight = 0;
        var maxInFlight = 0;
        var concurrencyLock = new object();
        void EnterCritical()
        {
            lock (concurrencyLock)
            {
                inFlight++;
                if (inFlight > maxInFlight) maxInFlight = inFlight;
            }
        }
        void LeaveCritical()
        {
            lock (concurrencyLock) { inFlight--; }
        }

        var applyEntered = new ManualResetEventSlim(false);
        var releaseApply = new ManualResetEventSlim(false);
        var revertEntered = new ManualResetEventSlim(false);

        using var applyDomain = new BlockingDomain("apply-domain", "Battery")
        {
            OnApply = () => { EnterCritical(); applyEntered.Set(); releaseApply.Wait(5000); LeaveCritical(); },
        };
        using var revertDomain = new BlockingDomain("revert-domain", "Battery", isActive: true)
        {
            OnRevert = () => { EnterCritical(); revertEntered.Set(); LeaveCritical(); },
        };

        // revert-domain must already have a stored snapshot for RevertDomain to act.
        snapshotStore.Store(new DomainSnapshot { DomainId = "revert-domain" });

        using var engine = new UnifiedOptimizationEngine(
            new Settings(), snapshotStore, [applyDomain, revertDomain]);

        var t1 = new Thread(() => engine.ActivateDomain("apply-domain"));
        t1.Start();

        Assert.True(applyEntered.Wait(5000), "Apply critical section never entered");

        // Thread 1 now holds the engine interlock inside Apply. Thread 2's RevertDomain must block
        // at the gate — its Revert body must NOT run while Apply is in flight.
        var t2 = new Thread(() => engine.RevertDomain("revert-domain"));
        t2.Start();

        // With correct serialization, Revert cannot enter while Apply is parked.
        Assert.False(revertEntered.Wait(500), "Revert interleaved with Apply — mutations not serialized");

        releaseApply.Set();
        Assert.True(t1.Join(5000));
        Assert.True(t2.Join(5000));

        // After release, the revert ran exactly once and never overlapped the apply.
        Assert.True(revertEntered.IsSet);
        Assert.Equal(1, maxInFlight);
        snapshotStore.RemoveRange(["apply-domain", "revert-domain"]);
    }

    [Fact]
    public void ActivateDomain_ConcurrentDoubleActivate_AppliesOnce_StoresOneSnapshot()
    {
        // The lookup + IsActive guard now runs inside the mutation gate, so two concurrent
        // ActivateDomain calls for the same domain serialize: the first applies (flips IsActive),
        // the second observes IsActive and bails as "not applicable". Exactly one apply, one capture,
        // one stored snapshot — never capturing already-applied state as a corrupt baseline.
        var snapshotStore = new SnapshotStore(NewStorePath());

        var applyCount = 0;
        var captureCount = 0;
        var firstApplyEntered = new ManualResetEventSlim(false);
        var releaseFirstApply = new ManualResetEventSlim(false);

        using var domain = new BlockingDomain("dup-domain", "Battery")
        {
            OnCapture = () => Interlocked.Increment(ref captureCount),
            OnApply = () =>
            {
                // First entrant parks inside the gate; the second call is blocked at the gate and,
                // once released, sees IsActive==true and returns before ever reaching Apply.
                if (Interlocked.Increment(ref applyCount) == 1)
                {
                    firstApplyEntered.Set();
                    releaseFirstApply.Wait(5000);
                }
            },
        };

        using var engine = new UnifiedOptimizationEngine(
            new Settings(), snapshotStore, [domain]);

        var t1 = new Thread(() => engine.ActivateDomain("dup-domain"));
        t1.Start();
        Assert.True(firstApplyEntered.Wait(5000), "First Apply never entered the gate");

        var t2 = new Thread(() => engine.ActivateDomain("dup-domain"));
        t2.Start();
        // t2 must block at the gate while t1 is parked; give it a moment to prove it can't proceed.
        Assert.False(t2.Join(500), "Second ActivateDomain entered the gate while the first was parked");

        releaseFirstApply.Set();
        Assert.True(t1.Join(5000));
        Assert.True(t2.Join(5000));

        Assert.Equal(1, applyCount);     // applied exactly once
        Assert.Equal(1, captureCount);   // captured baseline exactly once
        Assert.True(domain.IsActive);
        Assert.Single(snapshotStore.GetAll());   // exactly one stored snapshot
        snapshotStore.RemoveRange(["dup-domain"]);
    }

    /// <summary>Fake whose Apply/Revert run injected callbacks so a test can block inside the
    /// engine's critical section and observe whether a second mutation interleaves.</summary>
    private sealed class BlockingDomain : IOptimizationDomain
    {
        public BlockingDomain(string id, string category, bool isActive = false)
        {
            Id = id;
            Category = category;
            IsActive = isActive;
        }

        public string Id { get; }
        public string DisplayName => Id;
        public string Category { get; }
        public bool IsSupported => true;
        public bool IsActive { get; private set; }

        public Action? OnApply { get; init; }
        public Action? OnRevert { get; init; }
        public Action? OnCapture { get; init; }

        public bool IsEnabled(Settings settings) => true;
        public DomainSnapshot CaptureBaseline()
        {
            OnCapture?.Invoke();
            return new() { DomainId = Id };
        }

        public ApplyResult Apply(DomainSnapshot baseline)
        {
            OnApply?.Invoke();
            IsActive = true;
            return ApplyResult.Ok(Id);
        }

        public void Revert(DomainSnapshot baseline)
        {
            OnRevert?.Invoke();
            IsActive = false;
        }

        public DomainStatus GetStatus() => new()
        {
            DomainId = Id,
            DisplayName = DisplayName,
            Category = Category,
            IsSupported = IsSupported,
            IsActive = IsActive,
        };

        public void Dispose() { }
    }

    private sealed class RecordingDomain : IOptimizationDomain
    {
        private readonly List<string>? _log;

        public RecordingDomain(string id, string category, bool isActive = false, List<string>? log = null)
        {
            Id = id;
            Category = category;
            IsActive = isActive;
            _log = log;
        }

        public string Id { get; }
        public string DisplayName => Id;
        public string Category { get; }
        public bool IsSupported => true;
        public bool IsActive { get; private set; }
        public bool FailApply { get; init; }

        /// <summary>
        /// Enable predicate this fake reports to the engine. Defaults to always-enabled (order/revert
        /// tests don't gate); the Wi-Fi gating tests inject a predicate reading the historical flag,
        /// mirroring the real domain's <c>IsEnabled(s) =&gt; s.WiFiOptimizerEnabled</c>.
        /// </summary>
        public Func<Settings, bool> Enable { get; init; } = _ => true;

        public bool IsEnabled(Settings settings) => Enable(settings);

        public DomainSnapshot CaptureBaseline() => new() { DomainId = Id };

        public ApplyResult Apply(DomainSnapshot baseline)
        {
            if (FailApply)
                return ApplyResult.Fail(Id, "forced apply failure");
            IsActive = true;
            return ApplyResult.Ok(Id);
        }

        public void Revert(DomainSnapshot baseline)
        {
            _log?.Add(Id);
            IsActive = false;
        }

        public DomainStatus GetStatus() => new()
        {
            DomainId = Id,
            DisplayName = DisplayName,
            Category = Category,
            IsSupported = IsSupported,
            IsActive = IsActive,
        };

        public void Dispose() { }
    }
}
