using System.Diagnostics;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;
using OptiSYS.Core.Services;

namespace OptiSYS.Core.Domains.Services;

/// <summary>
/// Flips a tight, curated set of genuinely non-essential services from Automatic → Manual start.
/// It NEVER stops a running service — each still starts on demand, so the change is unnoticeable.
/// Admin-gated (SCM ChangeServiceConfig needs elevation); a clean skip when unelevated. Each
/// service's original start type is snapshotted for exact revert, and a hard block-list guards
/// anything critical.
/// </summary>
public sealed class ServiceManualDomain : IOptimizationDomain
{
    private readonly IServiceConfigStore _store;
    private readonly Func<bool> _isElevated;
    private bool _isActive;
    private int _changed;

    public string Id => "services-manual";
    public string DisplayName => "Background Service Tune-up";
    public string Category => "System";
    public bool IsSupported => _isElevated();
    public bool IsActive => _isActive;

    public bool IsEnabled(Settings settings) => settings.ServicesManualEnabled;

    public ServiceManualDomain(IServiceConfigStore store)
        : this(store, PrivilegeManager.IsProcessElevated)
    {
    }

    // Test seam: forces the elevation check. Internal so MS.DI never sees it and always picks
    // the public ctor (which uses the real PrivilegeManager check).
    internal ServiceManualDomain(IServiceConfigStore store, Func<bool> isElevated)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _isElevated = isElevated ?? throw new ArgumentNullException(nameof(isElevated));
    }

    public DomainSnapshot CaptureBaseline()
    {
        var snapshot = new DomainSnapshot { DomainId = Id };
        var startTypes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in Settings.ServicesToSetManual)
        {
            if (IsBlocked(name)) continue;
            var startType = _store.GetStartType(name);
            if (startType.HasValue)
                startTypes[name] = startType.Value;
        }

        snapshot.Set("startTypes", startTypes);
        return snapshot;
    }

    public ApplyResult Apply(DomainSnapshot baseline)
    {
        var sw = Stopwatch.StartNew();

        if (!IsSupported)
            return ApplyResult.Ok(Id, "Needs admin — skipped", skipped: 1, duration: sw.Elapsed);

        int changed = 0, skipped = 0;
        foreach (var name in Settings.ServicesToSetManual)
        {
            if (IsBlocked(name)) { skipped++; continue; }

            var startType = _store.GetStartType(name);
            if (startType is null) { skipped++; continue; }                       // not present on this machine
            if (startType.Value != NativeMethods.SERVICE_AUTO_START) { skipped++; continue; } // only touch Automatic; leave Manual/Disabled/trigger as-is

            if (_store.SetStartType(name, NativeMethods.SERVICE_DEMAND_START))
                changed++;
            else
                skipped++;
        }

        _changed = changed;
        _isActive = changed > 0;
        sw.Stop();

        return ApplyResult.Ok(Id,
            $"Set {changed} service(s) to manual start (skipped {skipped})",
            optimized: changed, skipped: skipped, duration: sw.Elapsed);
    }

    public void Revert(DomainSnapshot baseline)
    {
        var startTypes = baseline.Get<Dictionary<string, uint>>("startTypes")
                         ?? new Dictionary<string, uint>();

        foreach (var (name, startType) in startTypes)
        {
            if (IsBlocked(name)) continue;
            if (startType is NativeMethods.SERVICE_AUTO_START
                or NativeMethods.SERVICE_DEMAND_START
                or NativeMethods.SERVICE_DISABLED)
            {
                _store.SetStartType(name, startType);
            }
        }

        _isActive = false;
        _changed = 0;
    }

    public DomainStatus GetStatus() => new()
    {
        DomainId = Id,
        DisplayName = DisplayName,
        Category = Category,
        IsSupported = IsSupported,
        IsActive = _isActive,
        Summary = _isActive
            ? $"{_changed} service(s) on manual start"
            : IsSupported ? "Inactive" : "Needs admin",
    };

    public void Dispose() { }

    private static bool IsBlocked(string serviceName) =>
        Settings.ServicesNeverManual.Contains(serviceName, StringComparer.OrdinalIgnoreCase);
}
