using System.Text.Json;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class SnapshotStoreTests
{
    [Fact]
    public void Store_ReplacesExistingDomainSnapshot()
    {
        var path = NewSnapshotPath();
        var store = new SnapshotStore(path);
        var older = new DomainSnapshot { DomainId = "ecoqos", CapturedAtUtc = DateTime.UtcNow.AddMinutes(-1) };
        older.Set("value", 1);
        var newer = new DomainSnapshot { DomainId = "ecoqos", CapturedAtUtc = DateTime.UtcNow };
        newer.Set("value", 2);

        store.Store(older);
        store.Store(newer);

        var loaded = store.Get("ecoqos");
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Get<int>("value"));
        Assert.Single(store.GetAll());
    }

    [Fact]
    public void Load_IgnoresMalformedOrBlankDomainSnapshots()
    {
        var path = NewSnapshotPath();
        var snapshots = new[]
        {
            new DomainSnapshot { DomainId = "" },
            new DomainSnapshot { DomainId = "   " },
            new DomainSnapshot { DomainId = "ecoqos" },
            new DomainSnapshot { DomainId = "future", CapturedAtUtc = DateTime.UtcNow.AddHours(1) },
        };
        WriteSnapshots(path, snapshots);

        var store = new SnapshotStore(path);

        var snapshot = Assert.Single(store.GetAll());
        Assert.Equal("ecoqos", snapshot.DomainId);
    }

    [Fact]
    public void Load_DeduplicatesByDomainKeepingNewest()
    {
        var path = NewSnapshotPath();
        var older = new DomainSnapshot { DomainId = "ecoqos", CapturedAtUtc = DateTime.UtcNow.AddMinutes(-5) };
        older.Set("value", 1);
        var newer = new DomainSnapshot { DomainId = "ECOQOS", CapturedAtUtc = DateTime.UtcNow };
        newer.Set("value", 2);
        WriteSnapshots(path, [older, newer]);

        var store = new SnapshotStore(path);

        var snapshot = Assert.Single(store.GetAll());
        Assert.Equal("ECOQOS", snapshot.DomainId);
        Assert.Equal(2, snapshot.Get<int>("value"));
    }

    [Fact]
    public void Load_CorruptJsonStartsEmpty()
    {
        var path = NewSnapshotPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var store = new SnapshotStore(path);

        Assert.False(store.HasSnapshots);
    }

    private static string NewSnapshotPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "optiSYS-tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(dir, "snapshots.json");
    }

    private static void WriteSnapshots(string path, IEnumerable<DomainSnapshot> snapshots)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshots));
    }
}
