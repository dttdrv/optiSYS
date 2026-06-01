using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class UnifiedOptimizationEngineTests
{
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
    public void ActivateDomain_WiFiOptimizer_AppliesByDefault()
    {
        // The Wi-Fi optimizer is ON by default now (background scan must always be off while
        // connected), so activating it with default settings applies and stores a baseline.
        // Isolated temp-file store so HasSnapshots doesn't observe the shared on-disk file.
        var snapshotStore = new SnapshotStore(
            Path.Combine(Path.GetTempPath(), "optiSYS-tests", Guid.NewGuid().ToString("N"), "snapshots.json"));
        using var wifi = new RecordingDomain("wifi-optimizer", "Network");

        using var engine = new UnifiedOptimizationEngine(new Settings(), snapshotStore, [wifi]);

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
