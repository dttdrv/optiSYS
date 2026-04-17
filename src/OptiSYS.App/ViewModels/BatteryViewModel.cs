using OptiSYS.Commands;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.ViewModels;

/// <summary>
/// View model for the Battery page. Surfaces the 8 battery-optimization domains as
/// individually bindable toggles plus aggregate Activate-All / Revert-All actions.
///
/// <para>
/// Domain-ID strings ("ecoqos", "timer-resolution", etc.) are repeated in property names
/// and in the toggle lambdas below. This redundancy is intentional — named commands keep
/// XAML bindings clean (<c>Command="{Binding ToggleEcoqosCommand}"</c>) and the string
/// appears exactly once per toggle (not at the call site). Changing a domain id is a
/// one-file edit.
/// </para>
/// </summary>
public sealed class BatteryViewModel : ViewModelBase, IDisposable
{
    // Domain-id constants — kept as private consts rather than magic strings scattered
    // through the file so a rename is a single-line change.
    private const string IdEcoqos              = "ecoqos";
    private const string IdTimerResolution     = "timer-resolution";
    private const string IdBackgroundServices  = "background-services";
    private const string IdUsbSuspend          = "usb-suspend";
    private const string IdNetworkPower        = "network-power";
    private const string IdGpuPower            = "gpu-power";
    private const string IdCpuParking          = "cpu-parking";
    private const string IdDiskCoalescing      = "disk-coalescing";
    private const string BatteryCategory       = "battery";   // engine compares OrdinalIgnoreCase

    private readonly IOptimizationEngine _engine;
    private readonly IBatteryInfoService _battery;
    private readonly Settings _settings;

    private BatteryInfo? _batteryInfo;
    private bool _isEcoqosActive;
    private bool _isTimerResolutionActive;
    private bool _isBackgroundServicesActive;
    private bool _isUsbSuspendActive;
    private bool _isNetworkPowerActive;
    private bool _isGpuPowerActive;
    private bool _isCpuParkingActive;
    private bool _isDiskCoalescingActive;

    public BatteryInfo? BatteryInfo                { get => _batteryInfo;                set => SetField(ref _batteryInfo, value); }
    public bool         IsEcoqosActive             { get => _isEcoqosActive;             set => SetField(ref _isEcoqosActive, value); }
    public bool         IsTimerResolutionActive    { get => _isTimerResolutionActive;    set => SetField(ref _isTimerResolutionActive, value); }
    public bool         IsBackgroundServicesActive { get => _isBackgroundServicesActive; set => SetField(ref _isBackgroundServicesActive, value); }
    public bool         IsUsbSuspendActive         { get => _isUsbSuspendActive;         set => SetField(ref _isUsbSuspendActive, value); }
    public bool         IsNetworkPowerActive       { get => _isNetworkPowerActive;       set => SetField(ref _isNetworkPowerActive, value); }
    public bool         IsGpuPowerActive           { get => _isGpuPowerActive;           set => SetField(ref _isGpuPowerActive, value); }
    public bool         IsCpuParkingActive         { get => _isCpuParkingActive;         set => SetField(ref _isCpuParkingActive, value); }
    public bool         IsDiskCoalescingActive     { get => _isDiskCoalescingActive;     set => SetField(ref _isDiskCoalescingActive, value); }

    public RelayCommand ToggleEcoqosCommand             { get; }
    public RelayCommand ToggleTimerResolutionCommand    { get; }
    public RelayCommand ToggleBackgroundServicesCommand { get; }
    public RelayCommand ToggleUsbSuspendCommand         { get; }
    public RelayCommand ToggleNetworkPowerCommand       { get; }
    public RelayCommand ToggleGpuPowerCommand           { get; }
    public RelayCommand ToggleCpuParkingCommand         { get; }
    public RelayCommand ToggleDiskCoalescingCommand     { get; }

    public RelayCommand ActivateAllCommand { get; }
    public RelayCommand RevertAllCommand   { get; }
    public RelayCommand RefreshCommand     { get; }

    public BatteryViewModel(
        IOptimizationEngine engine,
        IBatteryInfoService battery,
        Settings settings)
    {
        _engine   = engine   ?? throw new ArgumentNullException(nameof(engine));
        _battery  = battery  ?? throw new ArgumentNullException(nameof(battery));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        ToggleEcoqosCommand             = new RelayCommand(() => ToggleDomain(IdEcoqos));
        ToggleTimerResolutionCommand    = new RelayCommand(() => ToggleDomain(IdTimerResolution));
        ToggleBackgroundServicesCommand = new RelayCommand(() => ToggleDomain(IdBackgroundServices));
        ToggleUsbSuspendCommand         = new RelayCommand(() => ToggleDomain(IdUsbSuspend));
        ToggleNetworkPowerCommand       = new RelayCommand(() => ToggleDomain(IdNetworkPower));
        ToggleGpuPowerCommand           = new RelayCommand(() => ToggleDomain(IdGpuPower));
        ToggleCpuParkingCommand         = new RelayCommand(() => ToggleDomain(IdCpuParking));
        ToggleDiskCoalescingCommand     = new RelayCommand(() => ToggleDomain(IdDiskCoalescing));

        ActivateAllCommand = new RelayCommand(ActivateAllBatteryDomains);
        RevertAllCommand   = new RelayCommand(RevertAllBatteryDomains);
        RefreshCommand     = new RelayCommand(RefreshStatusFlags);

        _battery.Updated += OnBatteryUpdated;

        RefreshStatusFlags();
    }

    private void OnBatteryUpdated(BatteryInfo info) => BatteryInfo = info;

    /// <summary>
    /// Core toggle logic: check the domain's current IsActive, dispatch to
    /// Activate/Revert, then resync flags so the UI reflects the new state.
    /// </summary>
    private void ToggleDomain(string domainId)
    {
        var isActive = _engine.GetAllStatuses()
            .FirstOrDefault(s => s.DomainId == domainId)?.IsActive ?? false;

        if (isActive)
            _engine.RevertDomain(domainId);
        else
            _engine.ActivateDomain(domainId);

        RefreshStatusFlags();
    }

    private void ActivateAllBatteryDomains()
    {
        _engine.ActivateCategory(BatteryCategory);
        RefreshStatusFlags();
    }

    private void RevertAllBatteryDomains()
    {
        // Snapshot first so any collection mutation inside RevertDomain doesn't affect iteration.
        var toRevert = _engine.GetAllStatuses()
            .Where(s => s.Category.Equals(BatteryCategory, StringComparison.OrdinalIgnoreCase)
                        && s.IsActive)
            .Select(s => s.DomainId)
            .ToArray();

        foreach (var id in toRevert)
            _engine.RevertDomain(id);

        RefreshStatusFlags();
    }

    /// <summary>
    /// Pull every status at once and mirror it into the 8 bindable flags. Cheaper than
    /// querying per-property and avoids partial-state glitches mid-refresh.
    /// </summary>
    private void RefreshStatusFlags()
    {
        var byId = _engine.GetAllStatuses().ToDictionary(s => s.DomainId, s => s.IsActive);

        IsEcoqosActive             = byId.GetValueOrDefault(IdEcoqos);
        IsTimerResolutionActive    = byId.GetValueOrDefault(IdTimerResolution);
        IsBackgroundServicesActive = byId.GetValueOrDefault(IdBackgroundServices);
        IsUsbSuspendActive         = byId.GetValueOrDefault(IdUsbSuspend);
        IsNetworkPowerActive       = byId.GetValueOrDefault(IdNetworkPower);
        IsGpuPowerActive           = byId.GetValueOrDefault(IdGpuPower);
        IsCpuParkingActive         = byId.GetValueOrDefault(IdCpuParking);
        IsDiskCoalescingActive     = byId.GetValueOrDefault(IdDiskCoalescing);
    }

    public void Dispose()
    {
        _battery.Updated -= OnBatteryUpdated;
    }
}
