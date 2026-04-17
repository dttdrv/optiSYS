using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OptiSYS.Core.Models;

namespace OptiSYS;

/// <summary>
/// Shell window hosting the NavigationView sidebar + content frame. XAML supplies the visual
/// tree (auto-generated partial fills in <c>NavView</c> and <c>ContentFrame</c> fields); this
/// file only wires runtime behavior — navigation, window bounds persistence.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Settings _settings;

    public MainWindow()
    {
        // Pull the SAME singleton that SettingsViewModel mutates so window-close persistence
        // doesn't clobber user-saved preference edits. Loading a fresh copy here was the prior
        // approach and caused lost writes: user edits propagate through the DI singleton, but
        // OnClosed would serialize this window's stale local instance over them.
        _settings = AppHost.Services.GetRequiredService<Settings>();

        InitializeComponent();

        ConfigureAppWindow();

        NavView.Loaded += (_, _) =>
        {
            // Default-select the first item (Dashboard). SelectionChanged then navigates.
            NavView.SelectedItem = NavView.MenuItems[0];
        };
        NavView.SelectionChanged += OnSelectionChanged;
        NavView.ItemInvoked += OnItemInvoked;
        Closed += OnClosed;
    }

    /// <summary>Sets initial size + position from persisted settings via the AppWindow API.</summary>
    private void ConfigureAppWindow()
    {
        var appWindow = GetAppWindow();
        if (appWindow is null) return;

        var width  = double.IsFinite(_settings.WindowWidth)  ? (int)_settings.WindowWidth  : 1100;
        var height = double.IsFinite(_settings.WindowHeight) ? (int)_settings.WindowHeight : 720;
        appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

        if (double.IsFinite(_settings.WindowLeft) && double.IsFinite(_settings.WindowTop))
        {
            appWindow.Move(new Windows.Graphics.PointInt32(
                (int)_settings.WindowLeft,
                (int)_settings.WindowTop));
        }
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // The built-in settings gear is special — it doesn't flow through SelectedItem.
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(Views.SettingsPage));
            return;
        }

        var tag = (args.SelectedItemContainer?.Tag as string) ?? "Dashboard";
        var pageType = ResolvePageType(tag);
        ContentFrame.Navigate(pageType);
    }

    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        // Tapping the settings gear raises ItemInvoked (and NOT SelectionChanged for the gear),
        // so we route it here as a backup in case the selection didn't fire.
        if (args.IsSettingsInvoked)
        {
            ContentFrame.Navigate(typeof(Views.SettingsPage));
        }
    }

    private static Type ResolvePageType(string tag) => tag switch
    {
        "Dashboard" => typeof(Views.DashboardPage),
        "Battery"   => typeof(Views.BatteryPage),
        "Memory"    => typeof(Views.MemoryPage),
        "Processes" => typeof(Views.ProcessesPage),
        _           => typeof(Views.DashboardPage),
    };

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Persist window geometry on exit so the next launch restores it.
        // Failures here are non-critical — the app is going away anyway, so swallow and move on.
        try
        {
            var appWindow = GetAppWindow();
            if (appWindow is not null)
            {
                _settings.WindowLeft   = appWindow.Position.X;
                _settings.WindowTop    = appWindow.Position.Y;
                _settings.WindowWidth  = appWindow.Size.Width;
                _settings.WindowHeight = appWindow.Size.Height;
            }
            _settings.Save();
        }
        catch { /* shutdown path — never throw */ }
    }

    private AppWindow? GetAppWindow()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            return AppWindow.GetFromWindowId(windowId);
        }
        catch
        {
            return null;
        }
    }
}
