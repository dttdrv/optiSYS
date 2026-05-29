using OptiSYS.Core.Domains.Battery;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using MemDomain = OptiSYS.Core.Domains.Memory.MemoryOptimizerDomain;

namespace OptiSYS.Core.Services;

/// <summary>
/// Central orchestrator: manages the lifecycle of all optimization domains
/// across battery and memory categories.
/// </summary>
public sealed class UnifiedOptimizationEngine : IOptimizationEngine
{
    private readonly List<IOptimizationDomain> _domains;
    private readonly SnapshotStore _snapshotStore;
    private readonly Settings _settings;
    private int _optimizing; // Interlocked guard
    private bool _disposed;

    public event Action<EngineEvent>? EventOccurred;

    public bool IsOptimizing => Interlocked.CompareExchange(ref _optimizing, 0, 0) != 0;
    public bool IsActive => _domains.Any(d => d.IsActive);
    public IReadOnlyList<IOptimizationDomain> Domains => _domains.AsReadOnly();

    public UnifiedOptimizationEngine(
        Settings settings,
        SnapshotStore snapshotStore,
        IEnumerable<IOptimizationDomain>? domains = null,
        INativeBridge? native = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));

        var orderedDomains = domains?.ToList();
        _domains = orderedDomains is { Count: > 0 }
            ? orderedDomains
            : BuildDefaultDomains(settings, native ?? NativeBridgeFactory.Create());
    }

    public UnifiedOptimizationEngine(Settings settings, SnapshotStore snapshotStore)
        : this(settings, snapshotStore, null)
    {
    }

    /// <summary>Activate all enabled and supported domains for the given category.</summary>
    public EngineResult ActivateCategory(string category)
    {
        if (Interlocked.Exchange(ref _optimizing, 1) != 0)
            return new EngineResult { Message = "Optimization already in progress" };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<ApplyResult>();

        try
        {
            foreach (var domain in _domains.Where(d =>
                d.Category.Equals(category, StringComparison.OrdinalIgnoreCase)))
            {
                if (!IsDomainEnabled(domain.Id) || !domain.IsSupported || domain.IsActive)
                    continue;

                try
                {
                    Emit($"Capturing baseline for {domain.DisplayName}...");
                    var snapshot = domain.CaptureBaseline();
                    _snapshotStore.Store(snapshot);

                    Emit($"Applying {domain.DisplayName}...");
                    var result = domain.Apply(snapshot);
                    results.Add(result);

                    if (result.Success)
                        Emit($"{domain.DisplayName}: {result.Message}");
                    else
                    {
                        Emit($"{domain.DisplayName} failed: {result.Message}");
                        _snapshotStore.Remove(domain.Id);
                    }
                }
                catch (Exception ex)
                {
                    results.Add(ApplyResult.Fail(domain.Id, ex.Message));
                    Emit($"{domain.DisplayName} error: {ex.Message}");
                    _snapshotStore.Remove(domain.Id);
                }
            }

            sw.Stop();
            return new EngineResult
            {
                Success = results.Any(r => r.Success),
                Results = results,
                Duration = sw.Elapsed,
                Message = $"Activated {results.Count(r => r.Success)}/{results.Count} {category} domains"
            };
        }
        finally
        {
            Interlocked.Exchange(ref _optimizing, 0);
        }
    }

    /// <summary>Activate a specific domain by ID.</summary>
    public EngineResult ActivateDomain(string domainId)
    {
        var domain = _domains.FirstOrDefault(d => d.Id == domainId);
        if (domain == null)
            return new EngineResult { Message = $"Domain '{domainId}' not found" };

        if (!IsDomainEnabled(domain.Id) || !domain.IsSupported || domain.IsActive)
            return new EngineResult { Message = $"Domain '{domainId}' not applicable" };

        try
        {
            var snapshot = domain.CaptureBaseline();
            _snapshotStore.Store(snapshot);
            var result = domain.Apply(snapshot);

            if (!result.Success)
                _snapshotStore.Remove(domain.Id);

            return new EngineResult
            {
                Success = result.Success,
                Results = [result],
                Message = result.Message
            };
        }
        catch (Exception ex)
        {
            _snapshotStore.Remove(domain.Id);
            return new EngineResult { Success = false, Message = ex.Message };
        }
    }

    /// <summary>Revert all active domains.</summary>
    public EngineResult RevertAll()
    {
        if (Interlocked.Exchange(ref _optimizing, 1) != 0)
            return new EngineResult { Message = "Operation already in progress" };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reverted = 0;
        var failed = 0;

        try
        {
            foreach (var domain in _domains.AsEnumerable().Reverse())
            {
                if (!domain.IsActive) continue;

                var snapshot = _snapshotStore.Get(domain.Id);
                if (snapshot == null)
                {
                    Emit($"No snapshot for {domain.DisplayName}, skipping revert");
                    continue;
                }

                try
                {
                    Emit($"Reverting {domain.DisplayName}...");
                    domain.Revert(snapshot);
                    _snapshotStore.Remove(domain.Id);
                    reverted++;
                    Emit($"{domain.DisplayName} reverted successfully");
                }
                catch (Exception ex)
                {
                    failed++;
                    Emit($"{domain.DisplayName} revert failed: {ex.Message}");
                }
            }

            sw.Stop();
            return new EngineResult
            {
                Success = failed == 0,
                Duration = sw.Elapsed,
                Message = $"Reverted {reverted} domains ({failed} failed)"
            };
        }
        finally
        {
            Interlocked.Exchange(ref _optimizing, 0);
        }
    }

    /// <summary>Revert a specific domain.</summary>
    public EngineResult RevertDomain(string domainId)
    {
        var domain = _domains.FirstOrDefault(d => d.Id == domainId);
        if (domain == null || !domain.IsActive)
            return new EngineResult { Message = $"Domain '{domainId}' not active" };

        var snapshot = _snapshotStore.Get(domain.Id);
        if (snapshot == null)
            return new EngineResult { Message = $"No snapshot for domain '{domainId}'" };

        try
        {
            domain.Revert(snapshot);
            _snapshotStore.Remove(domain.Id);
            return new EngineResult { Success = true, Message = $"{domain.DisplayName} reverted" };
        }
        catch (Exception ex)
        {
            return new EngineResult { Success = false, Message = ex.Message };
        }
    }

    /// <summary>Attempt crash recovery.</summary>
    public void TryCrashRecovery()
    {
        if (!_snapshotStore.HasSnapshots) return;

        Emit("Detected snapshots from previous session — attempting recovery...");
        var toRemove = new List<string>();

        foreach (var snapshot in _snapshotStore.GetAll())
        {
            var domain = _domains.FirstOrDefault(d => d.Id == snapshot.DomainId);
            if (domain == null)
            {
                toRemove.Add(snapshot.DomainId);
                continue;
            }

            try
            {
                domain.Revert(snapshot);
                Emit($"Recovered {domain.DisplayName}");
            }
            catch (Exception ex)
            {
                Emit($"Recovery failed for {domain.DisplayName}: {ex.Message}");
            }

            toRemove.Add(snapshot.DomainId);
        }

        if (toRemove.Count > 0)
            _snapshotStore.RemoveRange(toRemove);
    }

    /// <summary>Get live status for all domains.</summary>
    public List<DomainStatus> GetAllStatuses()
    {
        return _domains.Select(d =>
        {
            try { return d.GetStatus(); }
            catch { return new DomainStatus { DomainId = d.Id, DisplayName = d.DisplayName, Category = d.Category, Summary = "Error" }; }
        }).ToList();
    }

    private bool IsDomainEnabled(string domainId) => domainId switch
    {
        "ecoqos" => _settings.EcoQosEnabled,
        "timer-resolution" => _settings.TimerResolutionEnabled,
        "background-services" => _settings.BackgroundServicesEnabled,
        "usb-suspend" => _settings.UsbSuspendEnabled,
        "network-power" => _settings.NetworkPowerEnabled,
        "gpu-power" => _settings.GpuPowerEnabled,
        "cpu-parking" => _settings.CpuParkingEnabled,
        "disk-coalescing" => _settings.DiskCoalescingEnabled,
        "wifi-optimizer" => _settings.WiFiOptimizerEnabled,
        "memory-optimize" => _settings.AutoOptimizeMemoryEnabled,
        _ => false
    };

    private void Emit(string message)
    {
        EventOccurred?.Invoke(new EngineEvent { Message = message });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var domain in _domains)
        {
            try { domain.Dispose(); } catch { }
        }
    }

    private static List<IOptimizationDomain> BuildDefaultDomains(Settings settings, INativeBridge native) =>
    [
        new EcoQosDomain(settings, native),
        new TimerResolutionDomain(settings, native),
        new BackgroundServiceDomain(settings),
        new UsbSuspendDomain(),
        new NetworkPowerDomain(),
        new GpuPowerDomain(),
        new CpuParkingDomain(settings),
        new DiskIoCoalescingDomain(settings),
        BuildDefaultMemoryDomain(settings, native),
    ];

    private static IOptimizationDomain BuildDefaultMemoryDomain(Settings settings, INativeBridge native)
    {
        var memoryInfo = new MemoryInfoService(native);
        return new MemDomain(settings, new MemoryOptimizer(memoryInfo, native), memoryInfo, ownsDependencies: true);
    }
}
