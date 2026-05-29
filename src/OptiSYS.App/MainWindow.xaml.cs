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
    private readonly ThemeManager _theme;

    private bool _allowExit;
    private bool _initializing = true;
    private bool _suppressAutoToggle;

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

        _theme = new ThemeManager(this, ShellRoot, GetAppWindow, _settings);

        ConfigureTitleBar();
        _theme.ApplyThemeMode();
        _theme.ApplyBackdrop();
        _theme.ApplyAccentColor();
        ConfigureAppWindow();
        HookTray();
        HookRuntimeEvents();

        if (GetAppWindow() is { } appWindow)
        {
            appWindow.Closing += OnAppWindowClosing;
        }

        InitializeControlValues();
        
        _initializing = false;

        // Restore the last-viewed page. Only Dashboard / Settings remain; any legacy value
        // (e.g. the removed "ProtectedApps") falls through to Dashboard.
        var restoreTag = _settings.SelectedNavItem switch
        {
            "Settings" => "Settings",
            _ => "Dashboard"
        };
        SwitchToPage(restoreTag);

        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(1);   // fast live telemetry
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

                // Force the window icon from the shipped .ico so the taskbar / alt-tab refresh
                // past Windows' cached exe icon (the embedded resource alone can read stale).
                var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (System.IO.File.Exists(icoPath))
                {
                    appWindow.SetIcon(icoPath);
                }

                // Load the in-window title-bar icon from the file (NOT ms-appx): this build strips
                // resources.pri, so ms-appx:///Assets/... fails to resolve and the icon stays blank.
                var pngPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
                if (System.IO.File.Exists(pngPath))
                {
                    TitleBarIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(pngPath));
                }

                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    var titleBar = appWindow.TitleBar;
                    titleBar.ExtendsContentIntoTitleBar = true;
                    _theme.UpdateTitleBarButtonsColors();
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

            var width = double.IsFinite(_settings.WindowWidth) ? (int)_settings.WindowWidth : MinWindowWidth;
            var height = double.IsFinite(_settings.WindowHeight) ? (int)_settings.WindowHeight : MinWindowHeight;

            // Work area (physical px, same units as AppWindow) of the monitor this window opens on.
            var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;

            // Never below the usable minimum, never larger than the monitor.
            width = Math.Clamp(width, MinWindowWidth, Math.Max(MinWindowWidth, work.Width));
            height = Math.Clamp(height, MinWindowHeight, Math.Max(MinWindowHeight, work.Height));
            appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

            // Enforce a minimum size. WindowsAppSDK 1.6 has no OverlappedPresenter min-size API,
            // so clamp via the AppWindow.Changed event instead.
            appWindow.Changed -= OnAppWindowChanged;
            appWindow.Changed += OnAppWindowChanged;

            // Place the window on-screen. A saved position is honored only when fully visible;
            // otherwise center. Without this, a stale off-screen position renders a taskbar button
            // and thumbnail but never appears on the desktop.
            var pos = ResolveOnScreenPosition(work, width, height);
            appWindow.Move(pos);
            StartupLog.Write($"ConfigureAppWindow: req={_settings.WindowWidth}x{_settings.WindowHeight} work={work.Width}x{work.Height}@({work.X},{work.Y}) -> {width}x{height} @({pos.X},{pos.Y})");
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ConfigureAppWindow failure", ex);
        }
    }

    /// <summary>
    /// Returns a top-left point that keeps the whole window inside <paramref name="work"/>: the
    /// saved position when it is set and fully on-screen, otherwise the centered position.
    /// </summary>
    private Windows.Graphics.PointInt32 ResolveOnScreenPosition(Windows.Graphics.RectInt32 work, int width, int height)
    {
        if (double.IsFinite(_settings.WindowLeft) && double.IsFinite(_settings.WindowTop))
        {
            var x = (int)_settings.WindowLeft;
            var y = (int)_settings.WindowTop;
            var fullyVisible = x >= work.X && y >= work.Y
                && x + width <= work.X + work.Width
                && y + height <= work.Y + work.Height;
            if (fullyVisible)
            {
                return new Windows.Graphics.PointInt32(x, y);
            }
        }

        var cx = work.X + Math.Max(0, (work.Width - width) / 2);
        var cy = work.Y + Math.Max(0, (work.Height - height) / 2);
        return new Windows.Graphics.PointInt32(cx, cy);
    }

    private const int MinWindowWidth = 800;
    private const int MinWindowHeight = 560;

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }

        // Only clamp in the normal (restored) state. Resizing while minimized or maximized fights
        // the OS and — for minimize — corrupts the restore placement, leaving the window off-screen.
        if ((sender.Presenter as OverlappedPresenter)?.State is { } state and not OverlappedPresenterState.Restored)
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

    private void SidebarBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag)
        {
            SwitchToPage(tag);
        }
    }

    private void SwitchToPage(string tag)
    {
        DashboardGrid.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        SettingsGrid.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        var dashboardSelected = tag == "Dashboard";
        // Win11 nav: fade the accent pill on the selected item, and give it a subtle persistent
        // fill. Skip the fade during init so the first paint is instant.
        FadeNavPill(DashboardAccentBorder, dashboardSelected);
        FadeNavPill(SettingsAccentBorder, !dashboardSelected);
        DashboardBtn.Background = dashboardSelected ? _navSelectedFill : _navTransparent;
        SettingsBtn.Background = dashboardSelected ? _navTransparent : _navSelectedFill;

        // Persist the active page so it is restored next launch (skip during init / no-op changes).
        if (!_initializing && _settings.SelectedNavItem != tag)
        {
            _settings.SelectedNavItem = tag;
            _settings.SaveDebounced();
        }
    }

    private static readonly Brush _navTransparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private readonly Brush _navSelectedFill = ResolveThemeBrush("SubtleFillColorSecondaryBrush");

    private static Brush ResolveThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>Win11-style selection indicator: quick opacity ease in/out (no stretch).</summary>
    private void FadeNavPill(Border pill, bool selected)
    {
        var target = selected ? 1.0 : 0.0;
        if (_initializing)
        {
            pill.Opacity = target;
            return;
        }

        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = target,
            Duration = new Duration(TimeSpan.FromMilliseconds(167)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, pill);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void OnToggleProtectionClick(object sender, RoutedEventArgs e)
    {
        _automation.SetAutomationPaused(!_settings.AutomationPaused);
        RefreshPresentation();
    }

    // The single Settings master switch — the canonical on/off for all automatic optimization.
    // Stays in sync with the dashboard pause button (both drive AutomationPaused).
    private void OnAutomaticOptimizationToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _suppressAutoToggle || _settings is null) return;
        _automation.SetAutomationPaused(!AutomaticOptimizationToggle.IsOn);
        RefreshPresentation();
    }

    private void InitializeControlValues()
    {
        // Master AIO switch: ON when automation is running (not paused).
        AutomaticOptimizationToggle.IsOn = !_settings.AutomationPaused;
        // Two modes: index 0 = Balanced, index 1 = Max (Aggressive).
        OptimizationLevelComboBox.SelectedIndex = _settings.OptimizationLevel == OptimizationLevel.Aggressive ? 1 : 0;
        BatteryPresetComboBox.SelectedIndex = (int)_settings.BatteryPreset;

        StartWithWindowsToggle.IsOn = _settings.StartWithWindows;
        MinimizeToTrayToggle.IsOn = _settings.MinimizeToTray;
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

        // 2. Reflect the (possibly auto-switched) efficiency profile in the Profile dropdown.
        //    The auto-switch on AC/DC mutates _settings.BatteryPreset off-UI; this is the
        //    UI-thread refresh path (StateChanged → RefreshPresentation), so it is the safe
        //    place to resync. Only assign when the index actually differs: setting the same
        //    SelectedIndex does not re-fire SelectionChanged, so there is no feedback loop, and
        //    OptimizationLevel is never touched here.
        SyncBatteryPresetSelection();

        // 3. Update Fluent UI dashboard telemetry
        UpdateDashboardUI(memory, battery);
    }

    private void UpdateDashboardUI(MemoryInfo? memory, BatteryInfo? battery)
    {
        // Update paused indicators + pause/resume button affordance
        var paused = _settings.AutomationPaused;
        var pausedVisibility = paused ? Visibility.Visible : Visibility.Collapsed;
        MemoryPausedIndicator.Visibility = pausedVisibility;
        EfficiencyPausedIndicator.Visibility = pausedVisibility;
        PauseToggleIcon.Glyph = paused ? "" : ""; // Play (resume) when paused, Pause when running
        var pauseTooltip = paused ? "Resume optimization" : "Pause optimization";
        ToolTipService.SetToolTip(PauseToggleButton, pauseTooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PauseToggleButton, pauseTooltip);

        // Keep the Settings master switch in step with the pause button (guard the programmatic
        // assignment so it doesn't re-enter the Toggled handler).
        _suppressAutoToggle = true;
        AutomaticOptimizationToggle.IsOn = !paused;
        _suppressAutoToggle = false;

        // Update Memory Card
        if (memory is not null && memory.TotalPhysicalBytes > 0)
        {
            MemoryPercentText.Text = $"{memory.UsagePercent:0}%";
            MemoryGBText.Text = $"{memory.UsedGB:F1} GB used of {memory.TotalGB:F1} GB";
            MemoryProgressBar.Value = memory.UsagePercent;
            MemoryCachedText.Text = $"{memory.StandbyGB:F1} GB";
            MemoryProcessesText.Text = $"{memory.ProcessCount:N0}";
            MemoryClearedText.Text = OptiSYS.Core.Models.OptimizationResult.FormatBytesStatic(_automation.TotalFreedBytes);

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
        // Keep the icon-only button's glyph intact — only toggle enabled state for feedback.
        // (Setting Content to a string destroyed the FontIcon and left cut-off "Optimize" text.)
        ManualTrimButton.IsEnabled = false;
        try
        {
            await _automation.RunMemoryCleanupAsync();
            RefreshPresentation(forceMemoryPoll: true);
        }
        finally
        {
            ManualTrimButton.IsEnabled = true;
        }
    }

    private async void OnDeepCleanClick(object sender, RoutedEventArgs e)
    {
        DeepCleanButton.IsEnabled = false;
        DeepCleanButton.Content = "Cleaning...";

        await _automation.RunDeepCleanAsync();

        RefreshPresentation(forceMemoryPoll: true);
        DeepCleanButton.Content = "Deep clean";
        DeepCleanButton.IsEnabled = true;
    }

    private void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null) return;

        _settings.OptimizationLevel = OptimizationLevelComboBox.SelectedIndex == 1
            ? OptimizationLevel.Aggressive
            : OptimizationLevel.Balanced;
        _settings.MinimizeToTray = MinimizeToTrayToggle.IsOn;

        _settings.SaveDebounced();
    }

    private void OnBatteryPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _automation.SetBatteryPreset((BatteryPreset)BatteryPresetComboBox.SelectedIndex);
    }

    /// <summary>
    /// Keeps the Profile dropdown in step with <see cref="Settings.BatteryPreset"/> after an
    /// automatic AC/DC switch (driven from the runtime coordinator). Assigning an unchanged
    /// SelectedIndex is a WinUI no-op that does not raise SelectionChanged, so this never loops
    /// back into <see cref="OnBatteryPresetChanged"/>; even if it did, re-applying the same
    /// preset is idempotent and leaves OptimizationLevel untouched.
    /// </summary>
    private void SyncBatteryPresetSelection()
    {
        if (_initializing || _settings is null) return;
        var desired = (int)_settings.BatteryPreset;
        if (BatteryPresetComboBox.SelectedIndex != desired)
        {
            BatteryPresetComboBox.SelectedIndex = desired;
        }
    }

    private void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null) return;
        _settings.StartWithWindows = StartWithWindowsToggle.IsOn;
        _startup.Apply(_settings.StartWithWindows);
        _settings.SaveDebounced();
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
        EnsureWindowOnScreen();
        SetForegroundWindow(hwnd);
        Activate();
    }

    /// <summary>Recenter the window if it has drifted off the visible work area, so a restore
    /// always brings it back into view (defends against a stale/parked off-screen position).</summary>
    private void EnsureWindowOnScreen()
    {
        if (GetAppWindow() is not { } appWindow)
        {
            return;
        }

        var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var pos = appWindow.Position;
        var size = appWindow.Size;
        var fullyVisible = pos.X >= work.X && pos.Y >= work.Y
            && pos.X + size.Width <= work.X + work.Width
            && pos.Y + size.Height <= work.Y + work.Height;
        if (!fullyVisible)
        {
            appWindow.Move(ResolveOnScreenPosition(work, size.Width, size.Height));
        }
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
            // Only persist a real, restored placement. Saving while minimized/maximized would
            // capture the -32000 parked coordinate and the min-clamp size, which then re-applies
            // as garbage geometry on the next launch.
            if (appWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
            {
                return;
            }

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
