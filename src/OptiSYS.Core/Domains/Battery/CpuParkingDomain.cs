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
public sealed class CpuParkingDomain : IOptimizationDomain
{
    private readonly Settings _settings;
    private bool _isActive;
    private Guid _activeScheme;

    public string Id => "cpu-parking";
    public string DisplayName => "CPU Core Parking";
    public string Category => "Battery";
    public bool IsSupported => true;
    public bool IsActive => _isActive;

    public CpuParkingDomain(Settings settings) { _settings = settings; }

    public DomainSnapshot CaptureBaseline()
    {
        var snapshot = new DomainSnapshot { DomainId = Id };
        _activeScheme = NativeMethods.GetActiveScheme();

        if (_activeScheme == Guid.Empty)
        {
            snapshot.Set("schemeValid", false);
            return snapshot;
        }

        snapshot.Set("schemeValid", true);
        snapshot.Set("schemeGuid", _activeScheme.ToString());

        var minProc = NativeMethods.ReadDCValue(_activeScheme,
            NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
            NativeMethods.GUID_PROCESSOR_THROTTLE_MINIMUM);

        var maxProc = NativeMethods.ReadDCValue(_activeScheme,
            NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
            NativeMethods.GUID_PROCESSOR_THROTTLE_MAXIMUM);

        var coreParking = NativeMethods.ReadDCValue(_activeScheme,
            NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
            NativeMethods.GUID_PROCESSOR_PARKING_CORE_THRESHOLD);

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

        if (NativeMethods.WriteDCValue(scheme,
            NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
            NativeMethods.GUID_PROCESSOR_THROTTLE_MINIMUM,
            (uint)_settings.CpuParkingMinProcessorDC))
            applied++;
        else
            failed++;

        if (NativeMethods.WriteDCValue(scheme,
            NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
            NativeMethods.GUID_PROCESSOR_PARKING_CORE_THRESHOLD,
            100u))
            applied++;
        else
            failed++;

        NativeMethods.PowerSetActiveScheme(IntPtr.Zero, scheme);

        _isActive = applied > 0;
        sw.Stop();

        return ApplyResult.Ok(Id,
            $"CPU optimized: min state {_settings.CpuParkingMinProcessorDC}%, max parking",
            applied, failed, duration: sw.Elapsed);
    }

    public void Revert(DomainSnapshot baseline)
    {
        var schemeStr = baseline.Get<string>("schemeGuid");
        if (string.IsNullOrEmpty(schemeStr) || !Guid.TryParse(schemeStr, out var scheme))
        {
            _isActive = false;
            return;
        }

        var origMin = baseline.Get<uint>("minProcessorState");
        var origParking = baseline.Get<uint>("coreParkingThreshold");

        NativeMethods.WriteDCValue(scheme,
            NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
            NativeMethods.GUID_PROCESSOR_THROTTLE_MINIMUM, origMin);

        NativeMethods.WriteDCValue(scheme,
            NativeMethods.GUID_PROCESSOR_SETTINGS_SUBGROUP,
            NativeMethods.GUID_PROCESSOR_PARKING_CORE_THRESHOLD, origParking);

        NativeMethods.PowerSetActiveScheme(IntPtr.Zero, scheme);
        _isActive = false;
    }

    public DomainStatus GetStatus()
    {
        var scheme = NativeMethods.GetActiveScheme();
        var minProc = scheme != Guid.Empty
            ? NativeMethods.ReadDCValue(scheme,
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
