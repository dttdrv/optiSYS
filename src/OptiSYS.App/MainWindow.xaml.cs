using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace OptiSYS;

public sealed partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly IMemoryInfoService _memory;
    private readonly IBatteryInfoService _battery;
    private readonly IQuietAutomationService _automation;
    private readonly ITrayIconService _tray;
    private readonly IStartupRegistrationService _startup;
    private readonly DispatcherQueueTimer _refreshTimer;
    
    private readonly ObservableCollection<string> _memoryExclusions = new();
    private readonly ObservableCollection<string> _timerExclusions = new();
    private readonly ObservableCollection<string> _protectedApps = new();
    
    private bool _allowExit;
    private bool _initializing = true;

    public MainWindow()
    {
        _settings = AppHost.Services.GetRequiredService<Settings>();
        _memory = AppHost.Services.GetRequiredService<IMemoryInfoService>();
        _battery = AppHost.Services.GetRequiredService<IBatteryInfoService>();
        _automation = AppHost.Services.GetRequiredService<IQuietAutomationService>();
        _tray = AppHost.Services.GetRequiredService<ITrayIconService>();
        _startup = AppHost.Services.GetRequiredService<IStartupRegistrationService>();

        InitializeComponent();
        AppVersionText.Text = $"Version {typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.3"} // Active System Protection";

        ConfigureTitleBar();
        ApplyThemeMode();
        ApplyBackdrop();
        ApplyAccentColor();
        ConfigureAppWindow();
        HookTray();
        HookRuntimeEvents();

        if (GetAppWindow() is { } appWindow)
        {
            appWindow.Closing += OnAppWindowClosing;
        }

        // Setup Exclusions and Lists Data Binding
        MemoryExclusionsListView.ItemsSource = _memoryExclusions;
        TimerExclusionsListView.ItemsSource = _timerExclusions;
        ProtectedAppsListView.ItemsSource = _protectedApps;

        LoadExclusionsFromSettings();
        InitializeControlValues();
        
        _initializing = false;

        RestoreSelectedPage();

        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(5);
        _refreshTimer.Tick += (_, _) => RefreshPresentation();
        _refreshTimer.Start();

        Closed += OnClosed;

        RefreshPresentation(forceMemoryPoll: true);
    }

    internal void LaunchInBackground() => DispatcherQueue.TryEnqueue(HideToTray);

    private void ConfigureTitleBar()
    {
        try
        {
            var appWindow = GetAppWindow();
            if (appWindow != null)
            {
                appWindow.Title = "optiSYS";

                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    var titleBar = appWindow.TitleBar;
                    titleBar.ExtendsContentIntoTitleBar = true;
                    UpdateTitleBarButtonsColors();
                    // Designate the title strip as the draggable region. Without this,
                    // extending content into the title bar leaves the window undraggable.
                    SetTitleBar(AppTitleBar);
                }
                else
                {
                    SetTitleBar(AppTitleBar);
                }
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ConfigureTitleBar failure", ex);
        }
    }

    private void ConfigureAppWindow()
    {
        try
        {
            var appWindow = GetAppWindow();
            if (appWindow is null)
            {
                return;
            }

            var width = double.IsFinite(_settings.WindowWidth) ? (int)_settings.WindowWidth : 1100;
            var height = double.IsFinite(_settings.WindowHeight) ? (int)_settings.WindowHeight : 720;
            
            // Windows 11 system utility standard layout limits
            appWindow.Resize(new Windows.Graphics.SizeInt32(Math.Max(width, 800), Math.Max(height, 560)));

            // Enforce a minimum size. WindowsAppSDK 1.6 has no OverlappedPresenter min-size API,
            // so clamp via the AppWindow.Changed event instead.
            appWindow.Changed -= OnAppWindowChanged;
            appWindow.Changed += OnAppWindowChanged;

            if (double.IsFinite(_settings.WindowLeft) && double.IsFinite(_settings.WindowTop))
            {
                appWindow.Move(new Windows.Graphics.PointInt32((int)_settings.WindowLeft, (int)_settings.WindowTop));
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ConfigureAppWindow failure", ex);
        }
    }

    private const int MinWindowWidth = 800;
    private const int MinWindowHeight = 560;

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }

        var size = sender.Size;
        var width = Math.Max(size.Width, MinWindowWidth);
        var height = Math.Max(size.Height, MinWindowHeight);
        if (width != size.Width || height != size.Height)
        {
            sender.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
    }

    private void HookTray()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _tray.Initialize(hwnd);
            _tray.OpenRequested += RestoreFromTray;
            _tray.RunCleanupRequested += OnTrayRunCleanupRequested;
            _tray.ToggleAutomationRequested += OnTrayToggleAutomationRequested;
            _tray.ExitRequested += OnTrayExitRequested;
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("HookTray failure", ex);
        }
    }

    private void HookRuntimeEvents()
    {
        try
        {
            _battery.Updated += OnBatteryUpdated;
            _automation.StateChanged += OnAutomationStateChanged;
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("HookRuntimeEvents failure", ex);
        }
    }

    private void OnBatteryUpdated(BatteryInfo info) =>
        DispatcherQueue.TryEnqueue(() => RefreshPresentation());

    private void OnAutomationStateChanged() =>
        DispatcherQueue.TryEnqueue(() => RefreshPresentation());

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            SwitchToPage(tag);
        }
    }

    private void SwitchToPage(string tag)
    {
        // Content elements may not exist yet if NavigationView raises SelectionChanged during load.
        if (DashboardGrid is null)
        {
            return;
        }

        DashboardGrid.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        MemoryGrid.Visibility = tag == "Memory" ? Visibility.Visible : Visibility.Collapsed;
        PowerGrid.Visibility = tag == "Power" ? Visibility.Visible : Visibility.Collapsed;
        ProtectedAppsGrid.Visibility = tag == "ProtectedApps" ? Visibility.Visible : Visibility.Collapsed;
        SettingsGrid.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        // Persist the active page so it is restored next launch (skip during init / no-op changes).
        if (!_initializing && _settings.SelectedNavItem != tag)
        {
            _settings.SelectedNavItem = tag;
            _settings.SaveDebounced();
        }
    }

    private void RestoreSelectedPage()
    {
        var tag = string.IsNullOrWhiteSpace(_settings.SelectedNavItem) ? "Dashboard" : _settings.SelectedNavItem;

        // Set page content immediately — this only flips the page Grids' Visibility, which is
        // safe during construction.
        SwitchToPage(tag);

        // Defer the NavigationView selection: assigning SelectedItem before the control's
        // template is realized throws COMException 0x80070490 (ERROR_NOT_FOUND).
        if (NavView.IsLoaded)
        {
            SelectNavItem(tag);
        }
        else
        {
            void OnNavLoaded(object sender, RoutedEventArgs e)
            {
                NavView.Loaded -= OnNavLoaded;
                SelectNavItem(tag);
            }

            NavView.Loaded += OnNavLoaded;
        }
    }

    private void SelectNavItem(string tag)
    {
        foreach (var entry in NavView.MenuItems)
        {
            if (entry is NavigationViewItem item && item.Tag is string itemTag && itemTag == tag)
            {
                NavView.SelectedItem = item;
                return;
            }
        }

        if (NavView.MenuItems.Count > 0)
        {
            NavView.SelectedItem = NavView.MenuItems[0];
        }
    }

    private void OnToggleProtectionClick(object sender, RoutedEventArgs e)
    {
        _automation.SetAutomationPaused(!_settings.AutomationPaused);
        RefreshPresentation();
    }

    private void LoadExclusionsFromSettings()
    {
        _memoryExclusions.Clear();
        foreach (var app in _settings.MemoryExcludedProcesses)
        {
            _memoryExclusions.Add(app);
        }

        _timerExclusions.Clear();
        foreach (var app in _settings.TimerResolutionExcludedProcesses)
        {
            _timerExclusions.Add(app);
        }

        _protectedApps.Clear();
        foreach (var app in _settings.ProtectedApplications)
        {
            _protectedApps.Add(app);
        }
    }

    private void InitializeControlValues()
    {
        AutoOptimizeMemoryToggle.IsOn = _settings.AutoOptimizeMemoryEnabled;
        MemoryThresholdSlider.Value = _settings.MemoryThresholdPercent;
        MemoryThresholdValueLabel.Text = $"{_settings.MemoryThresholdPercent}%";
        MemoryCooldownSlider.Value = _settings.MemoryCooldownSeconds;
        MemoryCooldownValueLabel.Text = $"{_settings.MemoryCooldownSeconds} seconds";
        OptimizationLevelComboBox.SelectedIndex = (int)_settings.OptimizationLevel;
        EffectivenessTrackingToggle.IsOn = _settings.EffectivenessTrackingEnabled;
        SelfCapSlider.Value = _settings.SelfWorkingSetCapMB;
        SelfCapValueLabel.Text = $"{_settings.SelfWorkingSetCapMB} MB";

        AutoOptimizeOnBatteryToggle.IsOn = _settings.AutoOptimizeOnBattery;
        BatteryPresetComboBox.SelectedIndex = (int)_settings.BatteryPreset;

        StartWithWindowsToggle.IsOn = _settings.StartWithWindows;
        MinimizeToTrayToggle.IsOn = _settings.MinimizeToTray;
        ThemeModeComboBox.SelectedIndex = _settings.ThemeMode switch
        {
            "System" => 0,
            "Light" => 1,
            "Dark" => 2,
            _ => 2
        };

        UseWindowsAccentColorToggle.IsOn = _settings.UseWindowsAccentColor;
        BackdropTypeComboBox.SelectedIndex = _settings.BackdropType switch
        {
            "MicaAlt" => 0,
            "Mica" => 1,
            "Acrylic" => 2,
            "None" => 3,
            _ => 0
        };
    }

    private void RefreshPresentation(bool forceMemoryPoll = false)
    {
        MemoryInfo? memory = null;
        try
        {
            memory = forceMemoryPoll || _memory.CurrentInfo is null
                ? _memory.GetCurrentMemoryInfo()
                : _memory.CurrentInfo;
        }
        catch
        {
            memory = _memory.CurrentInfo;
        }

        var battery = _battery.CurrentInfo;
        var now = DateTime.Now;

        // 1. Update text-based logs for tests compatibility
        var text = new StringBuilder();
        text.AppendLine($"time              {now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine(_settings.AutomationPaused ? "mode              paused" : "mode              safe optimization");
        text.AppendLine("policy            memory trim + runtime throttles; no service, registry, device, or power-plan edits");
        text.AppendLine($"background        {(_settings.MinimizeToTray ? "tray enabled" : "window only")}");
        text.AppendLine($"memory_auto       {FormatBool(_settings.AutoOptimizeMemoryEnabled)}");
        text.AppendLine($"battery_auto      {FormatBool(_settings.AutoOptimizeOnBattery)}");
        text.AppendLine();
        AppendMemoryText(text, memory);
        text.AppendLine();
        AppendBatteryText(text, battery);
        text.AppendLine();
        text.AppendLine($"activity          {_automation.LastActivity}");
        if (_automation.LastActivityAt is { } at)
        {
            text.AppendLine($"activity_time     {at:yyyy-MM-dd HH:mm:ss zzz}");
        }
        StatusText.Text = text.ToString();
        FooterText.Text = $"last sample {now:HH:mm:ss} // safe runtime optimization only";

        // 2. Update Fluent UI dashboard telemetry
        UpdateDashboardUI(memory, battery);
    }

    private SolidColorBrush? _accentStatusFallback;
    private SolidColorBrush? _cautionStatusBrush;

    // Status colours resolved from theme resources so they track the accent/theme and are
    // allocated at most once, instead of constructing fresh brushes on every timer tick.
    private Brush AccentStatusBrush =>
        Application.Current.Resources.TryGetValue("SystemAccentColorBrush", out var value) && value is Brush brush
            ? brush
            : _accentStatusFallback ??= new SolidColorBrush(Windows.UI.Color.FromArgb(255, 92, 184, 101));

    private Brush CautionStatusBrush =>
        _cautionStatusBrush ??= new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 165, 0));

    private void UpdateDashboardUI(MemoryInfo? memory, BatteryInfo? battery)
    {
        var accentBrush = AccentStatusBrush;
        var cautionBrush = CautionStatusBrush;

        // Update overall health alert
        if (_settings.AutomationPaused)
        {
            HealthStatusIcon.Glyph = "\uE71A"; // Pause
            HealthStatusIcon.Foreground = cautionBrush;
            HealthStatusTitle.Text = "Safe optimization is paused";
            HealthStatusMessage.Text = "Background checks and auto-cleanup rules are currently suspended.";
            
            SidebarStatusLabel.Text = "Optimization Paused";
            SidebarStatusLabel.Foreground = cautionBrush;
            ToggleProtectionBtn.Content = "Resume Optimization";
        }
        else if (_automation.IsCleanupRunning)
        {
            HealthStatusIcon.Glyph = "\uE72C"; // Sync
            HealthStatusIcon.Foreground = accentBrush;
            HealthStatusTitle.Text = "Trimming memory working sets...";
            HealthStatusMessage.Text = _automation.LastActivity;
            
            SidebarStatusLabel.Text = "Optimizing...";
            SidebarStatusLabel.Foreground = accentBrush;
            ToggleProtectionBtn.Content = "Pause Optimization";
        }
        else if (battery is not null && battery.IsOnBattery)
        {
            HealthStatusIcon.Glyph = "\uE83F"; // Battery
            HealthStatusIcon.Foreground = accentBrush;
            HealthStatusTitle.Text = $"Running on Battery (Preset: {_settings.BatteryPreset})";
            HealthStatusMessage.Text = _automation.LastActivity;
            
            SidebarStatusLabel.Text = "On Battery (Protected)";
            SidebarStatusLabel.Foreground = accentBrush;
            ToggleProtectionBtn.Content = "Pause Optimization";
        }
        else
        {
            HealthStatusIcon.Glyph = "\uE73E"; // Shield
            HealthStatusIcon.Foreground = accentBrush;
            HealthStatusTitle.Text = "System is protected and optimized";
            HealthStatusMessage.Text = _automation.LastActivity;
            
            SidebarStatusLabel.Text = "Protection Active";
            SidebarStatusLabel.Foreground = accentBrush;
            ToggleProtectionBtn.Content = "Pause Optimization";
        }

        // Update Memory Card
        if (memory is not null && memory.TotalPhysicalBytes > 0)
        {
            MemoryPercentText.Text = $"{memory.UsagePercent:0}%";
            MemoryGBText.Text = $"{memory.UsedGB:F1} GB used of {memory.TotalGB:F1} GB";
            MemoryProgressBar.Value = memory.UsagePercent;
            MemoryCachedText.Text = $"{memory.StandbyGB:F1} GB";
            MemoryProcessesText.Text = $"{memory.ProcessCount:N0}";

            MemoryHistoryChart.AddSample(memory.UsagePercent);
        }
        else
        {
            MemoryPercentText.Text = "--%";
            MemoryGBText.Text = "Warming up memory telemetry...";
            MemoryProgressBar.Value = 0;
            MemoryCachedText.Text = "-- GB";
            MemoryProcessesText.Text = "--";
        }

        if (TotalFreedBytesText != null)
        {
            TotalFreedBytesText.Text = FormatFreedBytes(_automation.TotalFreedBytes);
        }

        // Update Battery Card
        if (battery is not null)
        {
            if (battery.IsOnBattery)
            {
                BatteryPercentText.Text = $"{battery.ChargePercent}%";
                BatterySourceText.Text = "Running on battery power";
                BatteryProgressBar.Value = battery.ChargePercent;
                PowerIcon.Glyph = "\uE83F"; // Battery
                BatteryDrainText.Text = battery.DrainRateDisplay;
                BatteryRemainingText.Text = battery.TimeRemainingDisplay;
            }
            else
            {
                BatteryPercentText.Text = battery.HasBattery ? $"{battery.ChargePercent}%" : "AC";
                BatterySourceText.Text = "Connected to power";
                BatteryProgressBar.Value = battery.HasBattery ? battery.ChargePercent : 100;
                PowerIcon.Glyph = "\uE72F"; // Plugged In / Power
                BatteryDrainText.Text = "N/A (charging)";
                BatteryRemainingText.Text = "N/A (plugged in)";
            }
        }
        else
        {
            BatteryPercentText.Text = "--%";
            BatterySourceText.Text = "Warming up battery telemetry...";
            BatteryProgressBar.Value = 0;
            BatteryDrainText.Text = "--";
            BatteryRemainingText.Text = "--";
        }
    }

private static void AppendMemoryText(StringBuilder text, MemoryInfo? memory)
    {
        text.AppendLine("[memory]");
        if (memory is null || memory.TotalPhysicalBytes <= 0)
        {
            text.AppendLine("state             warming up");
            return;
        }

        text.AppendLine($"usage             {memory.UsagePercent:0}%");
        text.AppendLine($"installed         {memory.TotalDisplay}");
        text.AppendLine($"used              {memory.UsedDisplay}");
        text.AppendLine($"available         {memory.AvailableDisplay}");
        text.AppendLine($"standby_cache     {memory.StandbyGB:0.0} GB");
        text.AppendLine($"processes         {memory.ProcessCount:N0}");
    }

    private static void AppendBatteryText(StringBuilder text, BatteryInfo? battery)
    {
        text.AppendLine("[power]");
        if (battery is null)
        {
            text.AppendLine("state             warming up");
            return;
        }

        text.AppendLine($"source            {FormatPowerSource(battery.PowerSource)}");
        text.AppendLine($"charge            {(battery.HasBattery ? $"{battery.ChargePercent}%" : "AC")}");
        text.AppendLine($"remaining         {battery.TimeRemainingDisplay}");
        text.AppendLine($"drain             {battery.DrainRateDisplay}");
    }

    private static string FormatPowerSource(PowerSource source) => source switch
    {
        PowerSource.Ac => "plugged in",
        PowerSource.Battery => "on battery",
        _ => "unknown",
    };

    private static string FormatBool(bool value) => value ? "on" : "off";

    private async void OnManualTrimClick(object sender, RoutedEventArgs e)
    {
        ManualTrimButton.IsEnabled = false;
        ManualTrimButton.Content = "Optimizing...";
        
        await _automation.RunMemoryCleanupAsync();
        
        RefreshPresentation(forceMemoryPoll: true);
        ManualTrimButton.Content = "Run Cleanup";
        ManualTrimButton.IsEnabled = true;
    }

    private void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null) return;

        _settings.AutoOptimizeMemoryEnabled = AutoOptimizeMemoryToggle.IsOn;
        _settings.OptimizationLevel = (OptimizationLevel)OptimizationLevelComboBox.SelectedIndex;
        _settings.EffectivenessTrackingEnabled = EffectivenessTrackingToggle.IsOn;
        _settings.AutoOptimizeOnBattery = AutoOptimizeOnBatteryToggle.IsOn;
        _settings.MinimizeToTray = MinimizeToTrayToggle.IsOn;

        _settings.SaveDebounced();
    }

    private void OnMemoryThresholdChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _settings.MemoryThresholdPercent = (int)MemoryThresholdSlider.Value;
        MemoryThresholdValueLabel.Text = $"{_settings.MemoryThresholdPercent}%";
        _settings.SaveDebounced();
    }

    private void OnMemoryCooldownChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _settings.MemoryCooldownSeconds = (int)MemoryCooldownSlider.Value;
        MemoryCooldownValueLabel.Text = $"{_settings.MemoryCooldownSeconds} seconds";
        _settings.SaveDebounced();
    }

    private void OnSelfCapChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _settings.SelfWorkingSetCapMB = (int)SelfCapSlider.Value;
        SelfCapValueLabel.Text = $"{_settings.SelfWorkingSetCapMB} MB";
        _settings.SaveDebounced();
    }

    private void OnBatteryPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _automation.SetBatteryPreset((BatteryPreset)BatteryPresetComboBox.SelectedIndex);
    }

    private void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _settings.StartWithWindows = StartWithWindowsToggle.IsOn;
        _startup.Apply(_settings.StartWithWindows);
        _settings.SaveDebounced();
    }

    private void OnThemeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _settings.ThemeMode = ThemeModeComboBox.SelectedIndex switch
        {
            0 => "System",
            1 => "Light",
            2 => "Dark",
            _ => "Dark"
        };
        ApplyThemeMode();
        ApplyAccentColor();
        UpdateTitleBarButtonsColors();
        _settings.SaveDebounced();
    }

    private void OnResetSettingsClick(object sender, RoutedEventArgs e)
    {
        _settings.AutoOptimizeMemoryEnabled = true;
        _settings.MemoryThresholdPercent = 80;
        _settings.MemoryCooldownSeconds = 30;
        _settings.OptimizationLevel = OptimizationLevel.Conservative;
        _settings.EffectivenessTrackingEnabled = true;
        _settings.SelfWorkingSetCapMB = 100;
        _settings.AutoOptimizeOnBattery = true;
        _settings.BatteryPreset = BatteryPreset.Recommended;
        _settings.MinimizeToTray = true;
        _settings.StartWithWindows = true;
        _settings.ThemeMode = "Dark";
        _settings.UseWindowsAccentColor = false;
        _settings.BackdropType = "MicaAlt";

        _settings.MemoryExcludedProcesses = new List<string>(Settings.CriticalProcessExclusions);
        _settings.TimerResolutionExcludedProcesses = new List<string>(Settings.CriticalProcessExclusions) { "audiodg", "NVIDIA Display Container" };
        _settings.ProtectedApplications = new List<string>
        {
            "Code", "Cursor", "devenv", "rider64", "idea64", "clion64", "pycharm64", "webstorm64",
            "datagrip64", "dotnet", "msbuild", "node", "python", "pwsh", "powershell", "cmd", "wt",
            "chrome", "msedge", "firefox", "brave", "vivaldi", "obs64", "Teams", "ms-teams",
            "Zoom", "Discord", "slack"
        };

        _settings.Save();
        _startup.Apply(true);

        _initializing = true;
        LoadExclusionsFromSettings();
        InitializeControlValues();
        ApplyThemeMode();
        ApplyBackdrop();
        ApplyAccentColor();
        _initializing = false;

        RefreshPresentation(forceMemoryPoll: true);
    }

    // Shared add/remove for the three exclusion lists (memory, timer, protected apps).
    private void AddExclusion(string processName, IList<string> settingsList, ObservableCollection<string> uiList)
    {
        processName = processName.Trim();
        if (string.IsNullOrWhiteSpace(processName)) return;
        if (settingsList.Contains(processName, StringComparer.OrdinalIgnoreCase)) return;

        settingsList.Add(processName);
        uiList.Add(processName);
        _settings.SaveDebounced();
    }

    private void RemoveExclusion(string item, IList<string> settingsList, ObservableCollection<string> uiList)
    {
        if (settingsList.Remove(item))
        {
            uiList.Remove(item);
            _settings.SaveDebounced();
        }
    }

    private void AddMemoryExclusion(string processName) =>
        AddExclusion(processName, _settings.MemoryExcludedProcesses, _memoryExclusions);

    private void OnAddMemoryExclusionClick(object sender, RoutedEventArgs e)
    {
        AddMemoryExclusion(MemoryExclusionInput.Text);
        MemoryExclusionInput.Text = string.Empty;
    }

    private void OnMemoryExclusionInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            AddMemoryExclusion(MemoryExclusionInput.Text);
            MemoryExclusionInput.Text = string.Empty;
            e.Handled = true;
        }
    }

    private void OnRemoveMemoryExclusionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is string item)
        {
            RemoveExclusion(item, _settings.MemoryExcludedProcesses, _memoryExclusions);
        }
    }

    private void AddTimerExclusion(string processName) =>
        AddExclusion(processName, _settings.TimerResolutionExcludedProcesses, _timerExclusions);

    private void OnAddTimerExclusionClick(object sender, RoutedEventArgs e)
    {
        AddTimerExclusion(TimerExclusionInput.Text);
        TimerExclusionInput.Text = string.Empty;
    }

    private void OnTimerExclusionInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            AddTimerExclusion(TimerExclusionInput.Text);
            TimerExclusionInput.Text = string.Empty;
            e.Handled = true;
        }
    }

    private void OnRemoveTimerExclusionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is string item)
        {
            RemoveExclusion(item, _settings.TimerResolutionExcludedProcesses, _timerExclusions);
        }
    }

    private void AddProtectedApp(string processName) =>
        AddExclusion(processName, _settings.ProtectedApplications, _protectedApps);

    private void OnAddProtectedAppClick(object sender, RoutedEventArgs e)
    {
        AddProtectedApp(ProtectedAppInput.Text);
        ProtectedAppInput.Text = string.Empty;
    }

    private void OnProtectedAppInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            AddProtectedApp(ProtectedAppInput.Text);
            ProtectedAppInput.Text = string.Empty;
            e.Handled = true;
        }
    }

    private async void OnBrowseProtectedAppClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(".exe");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                var processName = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                AddProtectedApp(processName);
            }
        }
        catch
        {
            // Fail silently or fallback
        }
    }

    private void OnRemoveProtectedAppClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is string item)
        {
            RemoveExclusion(item, _settings.ProtectedApplications, _protectedApps);
        }
    }

    // Windows lifecycle & tray

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        PersistWindowPlacement(sender);

        if (_allowExit || !_settings.MinimizeToTray)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_HIDE);
    }

    private void RestoreFromTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
        Activate();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _refreshTimer.Stop();

        if (GetAppWindow() is { } appWindow)
        {
            PersistWindowPlacement(appWindow);
            appWindow.Closing -= OnAppWindowClosing;
            appWindow.Changed -= OnAppWindowChanged;
        }

        _battery.Updated -= OnBatteryUpdated;
        _automation.StateChanged -= OnAutomationStateChanged;
        _tray.OpenRequested -= RestoreFromTray;
        _tray.RunCleanupRequested -= OnTrayRunCleanupRequested;
        _tray.ToggleAutomationRequested -= OnTrayToggleAutomationRequested;
        _tray.ExitRequested -= OnTrayExitRequested;
        _tray.Dispose();
    }

    private void PersistWindowPlacement(AppWindow appWindow)
    {
        try
        {
            _settings.WindowLeft = appWindow.Position.X;
            _settings.WindowTop = appWindow.Position.Y;
            _settings.WindowWidth = appWindow.Size.Width;
            _settings.WindowHeight = appWindow.Size.Height;
            _settings.Save();
        }
        catch
        {
            // Best effort only.
        }
    }

    private AppWindow? GetAppWindow()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == nint.Zero)
            {
                return null;
            }
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("GetAppWindow interop failure", ex);
            return null;
        }
    }

    private void ApplyThemeMode()
    {
        try
        {
            ShellRoot.RequestedTheme = _settings.ThemeMode switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ApplyThemeMode failure", ex);
        }
    }

    private void ApplyBackdrop()
    {
        try
        {
            SystemBackdrop = _settings.BackdropType switch
            {
                "Mica" => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
                "MicaAlt" => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                "Acrylic" => new DesktopAcrylicBackdrop(),
                _ => null
            };
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ApplyBackdrop failure, falling back to None", ex);
            SystemBackdrop = null;
        }
    }

    private void ApplyAccentColor()
    {
        try
        {
            // In high-contrast mode, respect the system palette — never override the accent.
            if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            {
                return;
            }

            Windows.UI.Color color;
            if (_settings.UseWindowsAccentColor)
            {
                try
                {
                    var uiSettings = new Windows.UI.ViewManagement.UISettings();
                    color = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
                }
                catch
                {
                    color = Windows.UI.Color.FromArgb(255, 92, 184, 101); // fallback
                }
            }
            else
            {
                color = Windows.UI.Color.FromArgb(255, 92, 184, 101); // #5CB865
            }

            Application.Current.Resources["SystemAccentColor"] = color;

            foreach (var key in new[] { "Light", "Dark", "Default" })
            {
                if (Application.Current.Resources.ThemeDictionaries.TryGetValue(key, out var dictObj) &&
                    dictObj is ResourceDictionary dict)
                {
                    dict["SystemAccentColor"] = color;
                }
            }

            var accentBrush = new SolidColorBrush(color);
            Application.Current.Resources["SystemAccentColorBrush"] = accentBrush;
            Application.Current.Resources["SystemFillColorSuccessBrush"] = accentBrush;

            if (ShellRoot != null)
            {
                var currentTheme = ShellRoot.RequestedTheme;
                ShellRoot.RequestedTheme = ElementTheme.Default;
                ShellRoot.RequestedTheme = currentTheme;
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ApplyAccentColor failure", ex);
        }
    }

    private void UpdateTitleBarButtonsColors()
    {
        try
        {
            var appWindow = GetAppWindow();
            if (appWindow != null && AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = appWindow.TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

                var isDark = IsDarkThemeActive();
                if (isDark)
                {
                    titleBar.ButtonForegroundColor = Colors.White;
                    titleBar.ButtonHoverForegroundColor = Colors.White;
                    titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(20, 255, 255, 255);
                    titleBar.ButtonPressedForegroundColor = Colors.White;
                    titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 255, 255, 255);
                    titleBar.ButtonInactiveForegroundColor = Colors.Gray;
                }
                else
                {
                    titleBar.ButtonForegroundColor = Colors.Black;
                    titleBar.ButtonHoverForegroundColor = Colors.Black;
                    titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(20, 0, 0, 0);
                    titleBar.ButtonPressedForegroundColor = Colors.Black;
                    titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(40, 0, 0, 0);
                    titleBar.ButtonInactiveForegroundColor = Colors.LightGray;
                }
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("UpdateTitleBarButtonsColors failure", ex);
        }
    }

    private bool IsDarkThemeActive()
    {
        var theme = ShellRoot?.RequestedTheme ?? ElementTheme.Default;
        if (theme == ElementTheme.Default)
        {
            try
            {
                var uiSettings = new Windows.UI.ViewManagement.UISettings();
                var bgColor = uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                return bgColor.R < 128;
            }
            catch
            {
                return true;
            }
        }
        return theme == ElementTheme.Dark;
    }

    private void OnUseWindowsAccentColorToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _settings.UseWindowsAccentColor = UseWindowsAccentColorToggle.IsOn;
        ApplyAccentColor();
        UpdateTitleBarButtonsColors();
        _settings.SaveDebounced();
    }

    private void OnBackdropTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _settings.BackdropType = BackdropTypeComboBox.SelectedIndex switch
        {
            0 => "MicaAlt",
            1 => "Mica",
            2 => "Acrylic",
            3 => "None",
            _ => "MicaAlt"
        };
        ApplyBackdrop();
        _settings.SaveDebounced();
    }

    private static string FormatFreedBytes(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        return OptiSYS.Core.Models.OptimizationResult.FormatBytesStatic(bytes);
    }

    private void OnTrayRunCleanupRequested()
    {
        OnManualTrimClick(this, new RoutedEventArgs());
    }

    private void OnTrayToggleAutomationRequested()
    {
        _automation.SetAutomationPaused(!_settings.AutomationPaused);
        DispatcherQueue.TryEnqueue(() => RefreshPresentation());
    }

    private void OnTrayExitRequested()
    {
        _allowExit = true;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_RESTORE);
        Close();
    }

    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
