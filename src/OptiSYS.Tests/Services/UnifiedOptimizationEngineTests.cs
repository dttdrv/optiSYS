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
            _dc[NativeMethods.GUID_PROCESSOR_PARKING_CORE_THRESHOLD] = parking;
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

    [Fact]
    public void ActivateDomain_WiFiOptimizer_AppliesWhenExplicitlyEnabled()
    {
        // The Wi-Fi optimizer is OFF by default now (it net-degraded the connection on real
        // hardware), so it only applies when the user explicitly opts in. Isolated temp-file store
        // so HasSnapshots doesn't observe the shared on-disk file.
        var snapshotStore = new SnapshotStore(
            Path.Combine(Path.GetTempPath(), "optiSYS-tests", Guid.NewGuid().ToString("N"), "snapshots.json"));
        using var wifi = new RecordingDomain("wifi-optimizer", "Network");

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
        using var wifi = new RecordingDomain("wifi-optimizer", "Network");

        using var engine = new UnifiedOptimizationEngine(
            new Settings { WiFiOptimizerEnabled = false }, snapshotStore, [wifi]);

        var result = engine.ActivateDomain("wifi-optimizer");

        Assert.False(wifi.IsActive);
        Assert.False(snapshotStore.HasSnapshots);
        Assert.Contains("not applicable", result.Message);
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

        public DomainSnapshot CaptureBaseline() => new() { DomainId = Id };

        public ApplyResult Apply(DomainSnapshot baseline)
        {
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
