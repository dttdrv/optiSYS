using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using OptiSYS.ViewModels;

namespace OptiSYS;

/// <summary>
/// Main application window with NavigationView sidebar.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly NavigationViewItem _dashboardItem;
    private readonly NavigationViewItem _batteryItem;
    private readonly NavigationViewItem _memoryItem;
    private readonly NavigationViewItem _processesItem;
    private readonly NavigationViewItem? _settingsItem;

    public MainWindow()
    {
        _settings = Settings.Load();

        Title = "optiSYS";
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // Set minimum size
        appWindow.Resize(new Windows.Graphics.SizeInt32(1100, 720));

        // Restore window position from settings
        if (double.IsFinite(_settings.WindowLeft) && double.IsFinite(_settings.WindowTop))
        {
            appWindow.Move(new Windows.Graphics.PointInt32(
                (int)_settings.WindowLeft,
                (int)_settings.WindowTop));
        }

        var rootGrid = new Grid();

        var navView = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsPaneToggleButtonVisible = true,
            OpenPaneLength = 260,
            CompactPaneLength = 56,
            IsSettingsVisible = true
        };

        // Header
        navView.PaneHeader = new TextBlock
        {
            Text = "optiSYS",
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            Margin = new Thickness(12, 8, 0, 0)
        };

        // Navigation items
        _dashboardItem = new NavigationViewItem
        {
            Content = "Dashboard",
            Icon = new SymbolIcon(Symbol.Home),
            Tag = "Dashboard"
        };

        _batteryItem = new NavigationViewItem
        {
            Content = "Battery",
            Icon = new FontIcon { Glyph = "\uE83F" }, // Battery icon
            Tag = "Battery"
        };

        _memoryItem = new NavigationViewItem
        {
            Content = "Memory",
            Icon = new FontIcon { Glyph = "\uE964" }, // RAM/memory icon
            Tag = "Memory"
        };

        _processesItem = new NavigationViewItem
        {
            Content = "Processes",
            Icon = new SymbolIcon(Symbol.AllApps),
            Tag = "Processes"
        };

        navView.MenuItems.Add(_dashboardItem);
        navView.MenuItems.Add(_batteryItem);
        navView.MenuItems.Add(_memoryItem);
        navView.MenuItems.Add(_processesItem);

        // Content frame
        var contentFrame = new Frame();
        navView.Content = contentFrame;

        navView.SelectionChanged += (s, e) =>
        {
            var selectedItem = navView.SelectedItem as NavigationViewItem;
            if (selectedItem?.Tag is string tag)
            {
                NavigateTo(tag);
            }
        };

        navView.Loaded += (s, e) =>
        {
            navView.SelectedItem = _dashboardItem;
            NavigateTo("Dashboard");
        };

        rootGrid.Children.Add(navView);

        Content = rootGrid;
    }

    private void NavigateTo(string page)
    {
        var frame = ((NavigationView)((Grid)Content).Children[0]).Content as Frame;
        if (frame == null) return;

        switch (page)
        {
            case "Dashboard":
                frame.Navigate(typeof(Views.DashboardPage));
                break;
            case "Battery":
                frame.Navigate(typeof(Views.BatteryPage));
                break;
            case "Memory":
                frame.Navigate(typeof(Views.MemoryPage));
                break;
            case "Processes":
                frame.Navigate(typeof(Views.ProcessesPage));
                break;
            case "Settings":
                frame.Navigate(typeof(Views.SettingsPage));
                break;
        }
    }
}
