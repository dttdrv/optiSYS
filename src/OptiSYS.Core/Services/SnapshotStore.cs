using System.IO;
using System.Text.Json;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Services;

/// <summary>
/// Persists optimization snapshots to disk for crash recovery.
/// Uses a single lock and batch write for removal operations.
/// </summary>
public sealed class SnapshotStore
{
    private static readonly string DefaultSnapshotFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "optiSYS", "snapshots.json");
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    private const int MaxSnapshotStateEntries = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly List<DomainSnapshot> _snapshots = [];
    private readonly object _lock = new();
    private readonly string _snapshotFile;

    public bool HasSnapshots
    {
        get
        {
            lock (_lock) { return _snapshots.Count > 0; }
        }
    }

    public SnapshotStore()
        : this(DefaultSnapshotFile)
    {
    }

    internal SnapshotStore(string snapshotFile)
    {
        _snapshotFile = snapshotFile ?? throw new ArgumentNullException(nameof(snapshotFile));
        Load();
    }

    public void Store(DomainSnapshot snapshot)
    {
        lock (_lock)
        {
            _snapshots.RemoveAll(s => s.DomainId == snapshot.DomainId);
            _snapshots.Add(snapshot);
            Save();
        }
    }

    public DomainSnapshot? Get(string domainId)
    {
        lock (_lock)
        {
            return _snapshots.FirstOrDefault(s => s.DomainId == domainId);
        }
    }

    public void Remove(string domainId)
    {
        lock (_lock)
        {
            _snapshots.RemoveAll(s => s.DomainId == domainId);
            Save();
        }
    }

    public void RemoveRange(List<string> domainIds)
    {
        lock (_lock)
        {
            _snapshots.RemoveAll(s => domainIds.Contains(s.DomainId));
            Save();
        }
    }

    public List<DomainSnapshot> GetAll()
    {
        lock (_lock)
        {
            return [.. _snapshots];
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_snapshotFile)) return;
            var json = File.ReadAllText(_snapshotFile);
            var loaded = JsonSerializer.Deserialize<List<DomainSnapshot>>(json, JsonOptions);
            if (loaded == null)
                return;

            _snapshots.AddRange(NormalizeSnapshots(loaded));
        }
        catch
        {
            // Corrupted snapshots file — start fresh
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_snapshotFile)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_snapshots, JsonOptions);
            File.WriteAllText(_snapshotFile, json);
        }
        catch
        {
            // Snapshot persistence failure is non-critical
        }
    }

    private static List<DomainSnapshot> NormalizeSnapshots(IEnumerable<DomainSnapshot> snapshots)
    {
        var now = DateTime.UtcNow;
        var byDomain = new Dictionary<string, DomainSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in snapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot.DomainId))
                continue;

            if (snapshot.CapturedAtUtc > now + MaxFutureSkew)
                continue;

            if (snapshot.State.Count > MaxSnapshotStateEntries)
                continue;

            var domainId = snapshot.DomainId.Trim();
            var normalized = new DomainSnapshot
            {
                DomainId = domainId,
                CapturedAtUtc = snapshot.CapturedAtUtc,
                State = snapshot.State,
            };

            if (!byDomain.TryGetValue(domainId, out var existing) ||
                existing.CapturedAtUtc <= normalized.CapturedAtUtc)
            {
                byDomain[domainId] = normalized;
            }
        }

        return byDomain.Values.ToList();
    }
}
