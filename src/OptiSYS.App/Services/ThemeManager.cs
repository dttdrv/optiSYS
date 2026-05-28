using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using OptiSYS.Core.Models;

namespace OptiSYS.Services;

/// <summary>
/// Owns theme, backdrop, accent-colour and title-bar-button-colour application for the shell
/// window. Extracted from MainWindow so the window's code-behind stays focused on layout and
/// data. Operates on the window, its root element, an AppWindow accessor and the settings;
/// behaviour is identical to the previous inline implementation.
/// </summary>
internal sealed class ThemeManager
{
    private readonly Window _window;
    private readonly FrameworkElement _root;
    private readonly Func<AppWindow?> _appWindow;
    private readonly Settings _settings;

    public ThemeManager(Window window, FrameworkElement root, Func<AppWindow?> appWindow, Settings settings)
    {
        _window = window;
        _root = root;
        _appWindow = appWindow;
        _settings = settings;
    }

    public void ApplyThemeMode()
    {
        try
        {
            _root.RequestedTheme = _settings.ThemeMode switch
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

    public void ApplyBackdrop()
    {
        try
        {
            _window.SystemBackdrop = _settings.BackdropType switch
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
            _window.SystemBackdrop = null;
        }
    }

    public void ApplyAccentColor()
    {
        try
        {
            // In high-contrast mode, respect the system palette — never override the accent.
            if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            {
                return;
            }

            // Default: follow the OS accent. WinUI's SystemAccentColor* resources already track the
            // system accent live, so we override nothing and let them flow through.
            if (_settings.UseWindowsAccentColor)
            {
                return;
            }

            // Opt-in custom brand accent: override the accent resources with OptiSYS green.
            var color = Windows.UI.Color.FromArgb(255, 92, 184, 101); // #5CB865

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

            if (_root != null)
            {
                var currentTheme = _root.RequestedTheme;
                _root.RequestedTheme = ElementTheme.Default;
                _root.RequestedTheme = currentTheme;
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("ApplyAccentColor failure", ex);
        }
    }

    public void UpdateTitleBarButtonsColors()
    {
        try
        {
            var appWindow = _appWindow();
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

    public bool IsDarkThemeActive()
    {
        var theme = _root?.RequestedTheme ?? ElementTheme.Default;
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
}
