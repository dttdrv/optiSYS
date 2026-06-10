using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private readonly IEffectivePowerModeProvider _effectivePowerMode;

    private bool _allowExit;
    private bool _initializing = true;
    private bool _suppressAutoToggle;
    private OptiSYS.Core.Services.OnboardingState? _onboarding;
    // App icon, loaded from the shipped PNG. The custom title bar no longer shows it (Windows apps
    // don't put their icon in the window's top-left content), but onboarding still reuses it.
    private Microsoft.UI.Xaml.Media.ImageSource? _appIcon;

    public MainWindow()
    {
        _settings = AppHost.Services.GetRequiredService<Settings>();
        _memory = AppHost.Services.GetRequiredService<IMemoryInfoService>();
        _battery = AppHost.Services.GetRequiredService<IBatteryInfoService>();
        _automation = AppHost.Services.GetRequiredService<IQuietAutomationService>();
        _tray = AppHost.Services.GetRequiredService<ITrayIconService>();
        _startup = AppHost.Services.GetRequiredService<IStartupRegistrationService>();
        _effectivePowerMode = AppHost.Services.GetRequiredService<IEffectivePowerModeProvider>();

        InitializeComponent();
        AppVersionText.Text = GetDisplayVersion();

        _theme = new ThemeManager(this, ShellRoot, GetAppWindow, _settings, _effectivePowerMode);

        ConfigureTitleBar();
        _theme.ApplyThemeMode();
        // Re-apply caption-button colours AFTER the theme is resolved, so dark mode gets light
        // buttons (ConfigureTitleBar's earlier call ran before RequestedTheme was set, which left the
        // min/max/close glyphs dark-on-dark).
        _theme.UpdateTitleBarButtonsColors();
        _theme.ApplyBackdrop();
        _theme.ApplyAccentColor();

        // Re-apply the backdrop when the effective power mode changes so it follows Windows: the
        // translucent backdrop is dropped in battery saver and restored when leaving it. The provider
        // raises Changed off the UI thread, so the handler marshals back.
        _effectivePowerMode.Changed += OnEffectivePowerModeChanged;
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

        MaybeStartOnboarding();
    }

    // ── First-run onboarding ────────────────────────────────────────────
    // Stepped, in-shell overlay shown once (HasCompletedOnboarding). Uses the same Visibility
    // pattern as the page tabs — no ContentDialog/NavigationView — per the crash-risk gate.

    /// <summary>
    /// The display version. Uses AssemblyInformationalVersion (carries the SemVer pre-release
    /// suffix, e.g. "0.4.0-alpha") since Assembly.Version is numeric-only and drops it. Strips any
    /// "+githash" build-metadata tail.
    /// </summary>
    private static string GetDisplayVersion()
    {
        var info = typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute attr, ..]
            ? attr.InformationalVersion
            : null;

        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        return typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.4.0-alpha";
    }

    private void MaybeStartOnboarding()
    {
        if (_settings.HasCompletedOnboarding) return;

        _onboarding = new OptiSYS.Core.Services.OnboardingState();
        OnboardingIcon.Source = _appIcon; // reuse the already-loaded app icon
        BuildOnboardingDots();
        RenderOnboardingStep();
        OnboardingOverlay.Visibility = Visibility.Visible;

        // Gentle entrance: fade the whole overlay in, then ease the first step up into place.
        FadeIn(OnboardingOverlay, durationMs: 220);
        SlideStepIn(forward: true);
    }

    private void BuildOnboardingDots()
    {
        OnboardingDots.Children.Clear();
        // 5 steps: Welcome, WiFi, Battery, Memory, Done.
        for (int i = 0; i < 5; i++)
        {
            OnboardingDots.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorDisabledBrush"],
            });
        }
    }

    private void RenderOnboardingStep()
    {
        if (_onboarding is null) return;
        var step = _onboarding.Current;

        StepWelcome.Visibility = step == OptiSYS.Core.Services.OnboardingStep.Welcome ? Visibility.Visible : Visibility.Collapsed;
        StepWiFi.Visibility = step == OptiSYS.Core.Services.OnboardingStep.WiFi ? Visibility.Visible : Visibility.Collapsed;
        StepBattery.Visibility = step == OptiSYS.Core.Services.OnboardingStep.Battery ? Visibility.Visible : Visibility.Collapsed;
        StepMemory.Visibility = step == OptiSYS.Core.Services.OnboardingStep.Memory ? Visibility.Visible : Visibility.Collapsed;
        StepDone.Visibility = step == OptiSYS.Core.Services.OnboardingStep.Done ? Visibility.Visible : Visibility.Collapsed;

        OnboardBackButton.Visibility = _onboarding.IsFirstStep ? Visibility.Collapsed : Visibility.Visible;
        OnboardNextButton.Content = _onboarding.IsLastStep ? "Get started" : "Next";

        // Highlight the active step dot.
        var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemAccentColorBrush"];
        var dim = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorDisabledBrush"];
        int active = (int)step;
        for (int i = 0; i < OnboardingDots.Children.Count; i++)
            if (OnboardingDots.Children[i] is Microsoft.UI.Xaml.Shapes.Ellipse dot)
                dot.Fill = i == active ? accent : dim;
    }

    private bool _onboardingAnimating;

    private void OnOnboardingBack(object sender, RoutedEventArgs e)
    {
        if (_onboarding is null || _onboardingAnimating) return;
        CaptureOnboardingChoices();
        TransitionStep(() => _onboarding.Back(), forward: false);
    }

    private void OnOnboardingNext(object sender, RoutedEventArgs e)
    {
        if (_onboarding is null || _onboardingAnimating) return;
        CaptureOnboardingChoices();

        if (_onboarding.IsLastStep)
        {
            FinishOnboarding();
            return;
        }

        TransitionStep(() => _onboarding.Next(), forward: true);
    }

    // ── Onboarding motion ───────────────────────────────────────────────
    // Fade + horizontal slide between steps (Fluent "connected" feel). Opacity and
    // TranslateTransform.X are independent animations (GPU-composited), so no new control
    // types and no dependent-animation cost — keeps clear of the XAML fail-fast class.

    private void TransitionStep(Action changeState, bool forward)
    {
        var xform = (Microsoft.UI.Xaml.Media.TranslateTransform)OnboardingStepHost.RenderTransform;
        _onboardingAnimating = true;

        // Phase 1: ease the current step out toward the travel direction.
        var outBoard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        AddDouble(outBoard, OnboardingStepHost, "Opacity", OnboardingStepHost.Opacity, 0, 120);
        AddDouble(outBoard, xform, "X", xform.X, forward ? -28 : 28, 120,
            new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn });

        outBoard.Completed += (_, _) =>
        {
            changeState();
            RenderOnboardingStep();
            SlideStepIn(forward);
        };
        outBoard.Begin();
    }

    /// <summary>Phase 2: bring the (now-updated) step in from the opposite side.</summary>
    private void SlideStepIn(bool forward)
    {
        var xform = (Microsoft.UI.Xaml.Media.TranslateTransform)OnboardingStepHost.RenderTransform;
        xform.X = forward ? 28 : -28;
        OnboardingStepHost.Opacity = 0;

        var inBoard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        AddDouble(inBoard, OnboardingStepHost, "Opacity", 0, 1, 200);
        AddDouble(inBoard, xform, "X", xform.X, 0, 220,
            new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut });
        inBoard.Completed += (_, _) => _onboardingAnimating = false;
        inBoard.Begin();
    }

    private static void FadeIn(UIElement target, int durationMs)
    {
        target.Opacity = 0;
        var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        AddDouble(board, target, "Opacity", 0, 1, durationMs);
        board.Begin();
    }

    private static void AddDouble(
        Microsoft.UI.Xaml.Media.Animation.Storyboard board,
        Microsoft.UI.Xaml.DependencyObject target,
        string property, double from, double to, int durationMs,
        Microsoft.UI.Xaml.Media.Animation.EasingFunctionBase? easing = null)
    {
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Microsoft.UI.Xaml.Duration(TimeSpan.FromMilliseconds(durationMs)),
            EnableDependentAnimation = false,
            EasingFunction = easing,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, target);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, property);
        board.Children.Add(anim);
    }

    /// <summary>Pull the current panel's toggle/level values into the state before navigating.</summary>
    private void CaptureOnboardingChoices()
    {
        if (_onboarding is null) return;
        _onboarding.WiFiEnabled = OnboardWiFiToggle.IsOn;
        _onboarding.BatteryEnabled = OnboardBatteryToggle.IsOn;
        _onboarding.MemoryEnabled = OnboardMemoryToggle.IsOn;
        _onboarding.MemoryLevel = OnboardMemoryLevel.SelectedIndex == 1
            ? OptimizationLevel.Aggressive
            : OptimizationLevel.Balanced;
    }

    private void FinishOnboarding()
    {
        if (_onboarding is null) return;
        _onboarding.ApplyTo(_settings);
        _settings.Save();
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        _onboarding = null;

        // Re-sync the dashboard controls to the freshly-chosen settings, then (re)start automation
        // so the chosen features take effect this session.
        _suppressAutoToggle = true;
        try { InitializeControlValues(); }
        finally { _suppressAutoToggle = false; }
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

                // Load the app icon from the file (NOT ms-appx): this build strips resources.pri, so
                // ms-appx:///Assets/... fails to resolve. The custom title bar no longer displays it;
                // it's kept only as the onboarding overlay's icon source.
                var pngPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
                if (System.IO.File.Exists(pngPath))
                {
                    _appIcon = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(pngPath));
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
        // Selection is expressed as a VisualState (not a code-set Background): the template's
        // SelectionStates group paints the persistent subtle fill for the "Selected" item.
        Microsoft.UI.Xaml.VisualStateManager.GoToState(DashboardBtn, dashboardSelected ? "Selected" : "Unselected", true);
        Microsoft.UI.Xaml.VisualStateManager.GoToState(SettingsBtn, dashboardSelected ? "Unselected" : "Selected", true);

        // Slide the single shared accent pill to the selected row (Fluent selection indicator).
        SlideNavIndicator(NavIndicatorTargetY(dashboardSelected ? DashboardBtn : SettingsBtn));

        // Persist the active page so it is restored next launch (skip during init / no-op changes).
        if (!_initializing && _settings.SelectedNavItem != tag)
        {
            _settings.SelectedNavItem = tag;
            _settings.SaveDebounced();
        }
    }

    /// <summary>
    /// Keep the Dashboard scroll body's top inset exactly equal to the fixed header's height, so the
    /// first card rests flush under the header at any font scale / DPI. The XAML margin is only the
    /// pre-measure estimate; this corrects it on first layout (and any later header resize).
    /// </summary>
    private void OnDashboardHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DashboardBody is not null)
            DashboardBody.Margin = new Thickness(0, e.NewSize.Height, 0, DashboardBody.Margin.Bottom);
    }

    private void OnEffectivePowerModeChanged()
    {
        // The provider raises this off the UI thread; marshal back before touching SystemBackdrop.
        DispatcherQueue.TryEnqueue(() => _theme.ApplyBackdrop());
    }

    // Pre-layout fallback offsets only: until the tree is arranged (ActualHeight is 0) the live
    // geometry isn't available, so the first placement uses the values the current XAML produces
    // (StackPanel Spacing=2, 36px rows with Margin 0,1, 16px pill -> 11 / 51). Every placement
    // after layout is computed from the buttons' real positions, so sidebar edits can't desync it.
    private const double NavIndicatorRow0Y = 11;   // Dashboard
    private const double NavIndicatorRow1Y = 51;   // Settings

    private bool _navIndicatorPlaced;

    /// <summary>
    /// Target Y for the nav pill, in the nav container's coordinate space: the selected button's
    /// live layout position with the pill centred in its row. Falls back to the XAML-derived
    /// constants before the first arrange pass.
    /// </summary>
    private double NavIndicatorTargetY(Microsoft.UI.Xaml.Controls.Button target)
    {
        if (target.ActualHeight <= 0 || NavIndicator.ActualHeight <= 0 ||
            NavIndicator.Parent is not UIElement host)
        {
            return target == SettingsBtn ? NavIndicatorRow1Y : NavIndicatorRow0Y;
        }

        var rowTop = target.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        return NavGeometry.CenterInRow(rowTop, target.ActualHeight, NavIndicator.ActualHeight);
    }

    /// <summary>Pure geometry for the sliding nav indicator (unit-tested; no XAML types).</summary>
    internal static class NavGeometry
    {
        /// <summary>Centre the pill in a row; never above the row top when the pill is taller.</summary>
        public static double CenterInRow(double rowTop, double rowHeight, double indicatorHeight) =>
            rowTop + Math.Max(0, (rowHeight - indicatorHeight) / 2);
    }

    /// <summary>Slide the shared accent pill to the target row via TranslateTransform.Y
    /// (167ms CubicEase EaseOut). The first placement snaps so the restored page paints correctly;
    /// subsequent selection changes animate.</summary>
    private void SlideNavIndicator(double targetY)
    {
        if (!_navIndicatorPlaced)
        {
            _navIndicatorPlaced = true;
            NavIndicatorTransform.Y = targetY;
            return;
        }

        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = targetY,
            Duration = new Duration(TimeSpan.FromMilliseconds(167)),
            EnableDependentAnimation = true,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, NavIndicatorTransform);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Y");
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

        // All derivation/formatting lives in DashboardPresenter (pure, unit-tested); this method
        // only gathers the inputs and assigns the resulting view to controls. Battery efficiency
        // profile is fully automatic (recommended on AC <-> saver on battery, driven by
        // AppRuntimeCoordinator); there is no in-app Profile control to resync.
        var view = DashboardPresenter.Present(new DashboardState
        {
            Memory = memory,
            Battery = _battery.CurrentInfo,
            AutomationPaused = _settings.AutomationPaused,
            MinimizeToTray = _settings.MinimizeToTray,
            AutoOptimizeMemory = _settings.AutoOptimizeMemoryEnabled,
            AutoOptimizeOnBattery = _settings.AutoOptimizeOnBattery,
            LastActivity = _automation.LastActivity,
            LastActivityAt = _automation.LastActivityAt,
            TotalFreedBytes = _automation.TotalFreedBytes,
            Now = DateTime.Now,
        });

        ApplyDashboardView(view);
    }

    private void ApplyDashboardView(DashboardView view)
    {
        StatusText.Text = view.StatusText;
        FooterText.Text = view.FooterText;

        // Paused indicators + pause/resume button affordance.
        var pausedVisibility = view.ShowPausedIndicators ? Visibility.Visible : Visibility.Collapsed;
        MemoryPausedIndicator.Visibility = pausedVisibility;
        EfficiencyPausedIndicator.Visibility = pausedVisibility;
        PauseToggleIcon.Glyph = view.PauseGlyph;
        ToolTipService.SetToolTip(PauseToggleButton, view.PauseLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PauseToggleButton, view.PauseLabel);

        // Keep the Settings master switch in step with the pause button (guard the programmatic
        // assignment so it doesn't re-enter the Toggled handler).
        _suppressAutoToggle = true;
        AutomaticOptimizationToggle.IsOn = view.AutomationOn;
        _suppressAutoToggle = false;

        var memory = view.Memory;
        MemoryPercentText.Text = memory.PercentText;
        MemoryGBText.Text = memory.DetailText;
        MemoryProgressBar.Value = memory.ProgressValue;
        MemoryCachedText.Text = memory.CachedText;
        MemoryProcessesText.Text = memory.ProcessesText;
        if (memory.ClearedText is { } cleared)
        {
            MemoryClearedText.Text = cleared;
        }
        if (memory.HistorySample is { } sample)
        {
            MemoryHistoryChart.AddSample(sample);
        }

        var battery = view.Battery;
        BatteryPercentText.Text = battery.PercentText;
        BatterySourceText.Text = battery.SourceText;
        BatteryProgressBar.Value = battery.ProgressValue;
        if (battery.PowerGlyph is { } glyph)
        {
            PowerIcon.Glyph = glyph;
        }
        BatteryDrainText.Text = battery.DrainText;
        BatteryRemainingText.Text = battery.RemainingText;
    }

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

    private void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null) return;

        _settings.OptimizationLevel = OptimizationLevelComboBox.SelectedIndex == 1
            ? OptimizationLevel.Aggressive
            : OptimizationLevel.Balanced;
        _settings.MinimizeToTray = MinimizeToTrayToggle.IsOn;

        _settings.SaveDebounced();
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
        // Self-quieting: nobody can see the dashboard, so stop paying for its 1s refresh
        // (formatting + control updates burned ~1-2% of a core while hidden, measured by the
        // Lab drain probe). The tray icon stays live through the runtime coordinator's own
        // update path, which does not ride this timer.
        _refreshTimer.Stop();
    }

    private void RestoreFromTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_RESTORE);
        EnsureWindowOnScreen();
        SetForegroundWindow(hwnd);
        Activate();
        // Catch up instantly (the timer's first tick is a second away), then resume the cadence.
        RefreshPresentation(forceMemoryPoll: true);
        _refreshTimer.Start();
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
        // The power-mode provider is a DI singleton that outlives this window; unsubscribe so the
        // closed window isn't pinned and no backdrop refresh runs against a torn-down dispatcher.
        _effectivePowerMode.Changed -= OnEffectivePowerModeChanged;
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
