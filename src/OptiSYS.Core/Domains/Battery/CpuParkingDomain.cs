using System.Diagnostics;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Domains.Battery;

/// <summary>
/// Optimizes CPU core parking and processor state for battery.
/// Lowers the minimum processor state on DC (battery) power
/// and increases core parking aggressiveness, allowing Windows
/// to park more cores when idle without affecting active workloads.
/// </summary>
public sealed class CpuParkingDomain : IOptimizationDomain, IVerifiableRevert
{
    private readonly Settings _settings;
    private readonly IPowerSchemeController _power;
    private bool _isActive;
    private Guid _activeScheme;

    public string Id => "cpu-parking";
    public string DisplayName => "CPU Core Parking";
    public string Category => "Battery";
    public bool IsSupported => true;
    public bool IsActive => _isActive;

    public CpuParkingDomain(Settings settings, IPowerSchemeController? power = null)
    {
        _settings = settings;
        _power = power ?? new PowerSchemeController();
    }

    public DomainSnapshot CaptureBaseline()
    {
        var snapshot = new DomainSnapshot { DomainId = Id };
        _activeScheme = _power.GetActiveScheme();

        if (_activeScheme == Guid.Empty)
        {
            snapshot.Set("schemeValid", false);
            return snapshot;
        }

        snapshot.Set("schemeValid", true);
        snapshot.Set("schemeGuid", _activeScheme.ToString());

        var minProc = _power.ReadDcValue(_activeScheme,
            NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
            NativeMethods.GUID_PROCESSOR_THROTTLE_MINIMUM);

        var maxProc = _power.ReadDcValue(_activeScheme,
            NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
            NativeMethods.GUID_PROCESSOR_THROTTLE_MAXIMUM);

        var coreParking = _power.ReadDcValue(_activeScheme,
            NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
            NativeMethods.GUID_PROCESSOR_CORE_PARKING_MIN_CORES);

        snapshot.Set("minProcessorState", minProc ?? 5u);
        snapshot.Set("maxProcessorState", maxProc ?? 100u);
        snapshot.Set("coreParkingThreshold", coreParking ?? 50u);

        return snapshot;
    }

    public ApplyResult Apply(DomainSnapshot baseline)
    {
        var sw = Stopwatch.StartNew();

        if (!baseline.Get<bool>("schemeValid"))
            return ApplyResult.Fail(Id, "Could not read active power scheme", sw.Elapsed);

        var schemeStr = baseline.Get<string>("schemeGuid");
        if (string.IsNullOrEmpty(schemeStr) || !Guid.TryParse(schemeStr, out var scheme))
            return ApplyResult.Fail(Id, "Invalid power scheme GUID", sw.Elapsed);

        int applied = 0, failed = 0;

        if (_power.WriteDcValue(scheme,
            NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
            NativeMethods.GUID_PROCESSOR_THROTTLE_MINIMUM,
            (uint)_settings.CpuParkingMinProcessorDC))
            applied++;
        else
            failed++;

        // CPMINCORES floor on battery: 0 = aggressive idle-core parking (the parking engine still
        // unparks under load); 100 (the old value) disabled core parking entirely, inverting intent.
        if (_power.WriteDcValue(scheme,
            NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
            NativeMethods.GUID_PROCESSOR_CORE_PARKING_MIN_CORES,
            0u))
            applied++;
        else
            failed++;

        _power.SetActiveScheme(scheme);

        _isActive = applied > 0;
        sw.Stop();

        return ApplyResult.Ok(Id,
            $"CPU optimized: min state {_settings.CpuParkingMinProcessorDC}%, max parking",
            applied, failed, duration: sw.Elapsed);
    }

    public void Revert(DomainSnapshot baseline) => TryRevert(baseline);

    /// <summary>
    /// Restore the captured DC processor values. Returns false if the scheme is unreadable or any
    /// restore write fails — callers (engine / crash recovery) must then RETAIN the snapshot so the
    /// only copy of the user's originals is not lost. Each value is restored only if its key was
    /// actually captured: a missing key (legacy/truncated snapshot) is skipped, never written as 0
    /// (which would pin the CPU to its floor).
    /// </summary>
    public bool TryRevert(DomainSnapshot baseline)
    {
        var schemeStr = baseline.Get<string>("schemeGuid");
        if (string.IsNullOrEmpty(schemeStr) || !Guid.TryParse(schemeStr, out var scheme))
        {
            _isActive = false;
            return false;
        }

        bool ok = true;
        ok &= RestoreIfPresent(baseline, "minProcessorState", scheme, NativeMethods.GUID_PROCESSOR_THROTTLE_MINIMUM);
        ok &= RestoreIfPresent(baseline, "maxProcessorState", scheme, NativeMethods.GUID_PROCESSOR_THROTTLE_MAXIMUM);
        ok &= RestoreIfPresent(baseline, "coreParkingThreshold", scheme, NativeMethods.GUID_PROCESSOR_CORE_PARKING_MIN_CORES);

        _power.SetActiveScheme(scheme);
        _isActive = false;
        return ok;
    }

    /// <summary>Restore one DC value only if the snapshot actually carried it. Returns false on write failure.</summary>
    private bool RestoreIfPresent(DomainSnapshot baseline, string key, Guid scheme, Guid setting)
    {
        if (!baseline.Has(key))
            return true; // nothing captured for this key → leave the live value untouched
        var value = baseline.Get<uint>(key);
        return _power.WriteDcValue(scheme, NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP, setting, value);
    }

    public DomainStatus GetStatus()
    {
        var scheme = _power.GetActiveScheme();
        var minProc = scheme != Guid.Empty
            ? _power.ReadDcValue(scheme,
                NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
                NativeMethods.GUID_PROCESSOR_THROTTLE_MINIMUM)
            : null;

        return new DomainStatus
        {
            DomainId = Id,
            DisplayName = DisplayName,
            Category = Category,
            IsSupported = IsSupported,
            IsActive = _isActive,
            Summary = _isActive
                ? $"Min state: {_settings.CpuParkingMinProcessorDC}%, parking: aggressive"
                : minProc.HasValue ? $"Min state: {minProc}%" : "Inactive",
        };
    }

    public void Dispose() { }
}
