using OptiSYS.Commands;
using OptiSYS.Core.Models;

namespace OptiSYS.ViewModels;

/// <summary>
/// View model for the Settings page. Presents a curated subset of <see cref="Settings"/>
/// as bindable properties that write through to the underlying model so changes survive
/// VM disposal, and tracks an <see cref="IsDirty"/> flag that <see cref="SaveCommand"/>
/// / <see cref="ResetCommand"/> reset.
///
/// <para>
/// <b>Write-through, not clone:</b> because <see cref="Settings"/> is a DI singleton, every
/// other ViewModel and engine reads from the same instance. Cloning into a local copy
/// would let the UI drift from the rest of the app. So every setter mutates the shared
/// instance directly.
/// </para>
///
/// <para>
/// <b>Reset preserves identity:</b> rather than swapping in a fresh <see cref="Settings"/>
/// (which would leave other singleton consumers holding the old reference), we copy the
/// type defaults field-by-field back into the existing instance, then fire a blanket
/// PropertyChanged so every binding re-reads.
/// </para>
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private bool _isDirty;

    public RelayCommand SaveCommand  { get; }
    public RelayCommand ResetCommand { get; }

    public SettingsViewModel(Settings settings)
    {
        _settings    = settings ?? throw new ArgumentNullException(nameof(settings));
        SaveCommand  = new RelayCommand(Save);
        ResetCommand = new RelayCommand(Reset);
    }

    /// <summary>
    /// True whenever any exposed property has been mutated since the last Save/Reset.
    /// A Save button can bind <c>IsEnabled="{Binding IsDirty}"</c> for the familiar
    /// "only offer save if something changed" affordance.
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetField(ref _isDirty, value);
    }

    // ── General / UI ──────────────────────────────────────────────────────────

    public string ThemeMode
    {
        get => _settings.ThemeMode;
        set => WriteThrough(_settings.ThemeMode, value, v => _settings.ThemeMode = v);
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set => WriteThrough(_settings.StartWithWindows, value, v => _settings.StartWithWindows = v);
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set => WriteThrough(_settings.MinimizeToTray, value, v => _settings.MinimizeToTray = v);
    }

    // ── Auto-optimization master switches ────────────────────────────────────

    public bool AutoOptimizeOnBattery
    {
        get => _settings.AutoOptimizeOnBattery;
        set => WriteThrough(_settings.AutoOptimizeOnBattery, value, v => _settings.AutoOptimizeOnBattery = v);
    }

    public bool AutoOptimizeMemoryEnabled
    {
        get => _settings.AutoOptimizeMemoryEnabled;
        set => WriteThrough(_settings.AutoOptimizeMemoryEnabled, value, v => _settings.AutoOptimizeMemoryEnabled = v);
    }

    // ── Battery domain toggles ───────────────────────────────────────────────

    public bool EcoQosEnabled
    {
        get => _settings.EcoQosEnabled;
        set => WriteThrough(_settings.EcoQosEnabled, value, v => _settings.EcoQosEnabled = v);
    }

    public bool TimerResolutionEnabled
    {
        get => _settings.TimerResolutionEnabled;
        set => WriteThrough(_settings.TimerResolutionEnabled, value, v => _settings.TimerResolutionEnabled = v);
    }

    public bool BackgroundServicesEnabled
    {
        get => _settings.BackgroundServicesEnabled;
        set => WriteThrough(_settings.BackgroundServicesEnabled, value, v => _settings.BackgroundServicesEnabled = v);
    }

    public bool UsbSuspendEnabled
    {
        get => _settings.UsbSuspendEnabled;
        set => WriteThrough(_settings.UsbSuspendEnabled, value, v => _settings.UsbSuspendEnabled = v);
    }

    public bool NetworkPowerEnabled
    {
        get => _settings.NetworkPowerEnabled;
        set => WriteThrough(_settings.NetworkPowerEnabled, value, v => _settings.NetworkPowerEnabled = v);
    }

    public bool GpuPowerEnabled
    {
        get => _settings.GpuPowerEnabled;
        set => WriteThrough(_settings.GpuPowerEnabled, value, v => _settings.GpuPowerEnabled = v);
    }

    public bool CpuParkingEnabled
    {
        get => _settings.CpuParkingEnabled;
        set => WriteThrough(_settings.CpuParkingEnabled, value, v => _settings.CpuParkingEnabled = v);
    }

    public bool DiskCoalescingEnabled
    {
        get => _settings.DiskCoalescingEnabled;
        set => WriteThrough(_settings.DiskCoalescingEnabled, value, v => _settings.DiskCoalescingEnabled = v);
    }

    // ── Memory thresholds ────────────────────────────────────────────────────

    public int MemoryThresholdPercent
    {
        get => _settings.MemoryThresholdPercent;
        set => WriteThrough(_settings.MemoryThresholdPercent, value, v => _settings.MemoryThresholdPercent = v);
    }

    public int MemoryCooldownSeconds
    {
        get => _settings.MemoryCooldownSeconds;
        set => WriteThrough(_settings.MemoryCooldownSeconds, value, v => _settings.MemoryCooldownSeconds = v);
    }

    public OptimizationLevel OptimizationLevel
    {
        get => _settings.OptimizationLevel;
        set => WriteThrough(_settings.OptimizationLevel, value, v => _settings.OptimizationLevel = v);
    }

    /// <summary>
    /// Shared setter body — compares old vs new, writes through when different, raises
    /// PropertyChanged for the caller's property, and flags IsDirty. Kept as a private
    /// helper because every setter looks exactly the same otherwise and the repetition
    /// would obscure the actual property list above.
    /// </summary>
    private void WriteThrough<T>(T current, T value, Action<T> assign,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        OnPropertyChanged(propertyName);
        IsDirty = true;
    }

    private void Save()
    {
        _settings.Save();     // best-effort: Settings.Save swallows IO failures internally.
        IsDirty = false;
    }

    private void Reset()
    {
        // Capture the type defaults from a throwaway instance, then overwrite every exposed
        // field on the singleton. The individual setter helpers would each flip IsDirty, so
        // we bypass them (direct assignment) and raise a blanket PropertyChanged at the end.
        var d = new Settings();

        _settings.ThemeMode                 = d.ThemeMode;
        _settings.StartWithWindows          = d.StartWithWindows;
        _settings.MinimizeToTray            = d.MinimizeToTray;
        _settings.AutoOptimizeOnBattery     = d.AutoOptimizeOnBattery;
        _settings.AutoOptimizeMemoryEnabled = d.AutoOptimizeMemoryEnabled;
        _settings.EcoQosEnabled             = d.EcoQosEnabled;
        _settings.TimerResolutionEnabled    = d.TimerResolutionEnabled;
        _settings.BackgroundServicesEnabled = d.BackgroundServicesEnabled;
        _settings.UsbSuspendEnabled         = d.UsbSuspendEnabled;
        _settings.NetworkPowerEnabled       = d.NetworkPowerEnabled;
        _settings.GpuPowerEnabled           = d.GpuPowerEnabled;
        _settings.CpuParkingEnabled         = d.CpuParkingEnabled;
        _settings.DiskCoalescingEnabled     = d.DiskCoalescingEnabled;
        _settings.MemoryThresholdPercent    = d.MemoryThresholdPercent;
        _settings.MemoryCooldownSeconds     = d.MemoryCooldownSeconds;
        _settings.OptimizationLevel         = d.OptimizationLevel;

        // Empty string per INotifyPropertyChanged convention: "all properties changed."
        // Every binding on the SettingsPage will re-query.
        OnPropertyChanged(string.Empty);
        IsDirty = false;
    }
}
