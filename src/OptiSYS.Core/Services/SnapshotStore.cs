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

    private string BackupFile => _snapshotFile + ".bak";

    private void Load()
    {
        // Try the main file; if it is missing or torn (crash mid-write), fall back to the last-good
        // .bak so a modified system is never stranded with no recovery snapshot (Finding 2).
        if (TryLoadFrom(_snapshotFile)) return;
        TryLoadFrom(BackupFile);
    }

    private bool TryLoadFrom(string file)
    {
        try
        {
            if (!File.Exists(file)) return false;
            var json = File.ReadAllText(file);
            var loaded = JsonSerializer.Deserialize<List<DomainSnapshot>>(json, JsonOptions);
            if (loaded == null) return false;

            _snapshots.AddRange(NormalizeSnapshots(loaded));
            return true;   // parsed successfully (even if it normalized to zero entries)
        }
        catch
        {
            return false;  // corrupt — let the caller try the backup
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_snapshotFile)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_snapshots, JsonOptions);

            // Atomic write: serialize to a temp file, then replace. A crash can leave the temp file
            // or the intact previous file, but never a half-written _snapshotFile. Preserve the prior
            // good file as .bak so Load can fall back if the replace is interrupted.
            var tmp = _snapshotFile + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(_snapshotFile))
                File.Replace(tmp, _snapshotFile, BackupFile);
            else
                File.Move(tmp, _snapshotFile);
        }
        catch
        {
            // Snapshot persistence failure is non-critical; a stale .bak still aids recovery.
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
