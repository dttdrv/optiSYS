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
    private static readonly string SnapshotFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "optiSYS", "snapshots.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly List<DomainSnapshot> _snapshots = [];
    private readonly object _lock = new();

    public bool HasSnapshots
    {
        get
        {
            lock (_lock) { return _snapshots.Count > 0; }
        }
    }

    public SnapshotStore()
    {
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
            if (!File.Exists(SnapshotFile)) return;
            var json = File.ReadAllText(SnapshotFile);
            var loaded = JsonSerializer.Deserialize<List<DomainSnapshot>>(json, JsonOptions);
            if (loaded != null)
                _snapshots.AddRange(loaded);
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
            var dir = Path.GetDirectoryName(SnapshotFile)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_snapshots, JsonOptions);
            File.WriteAllText(SnapshotFile, json);
        }
        catch
        {
            // Snapshot persistence failure is non-critical
        }
    }
}
