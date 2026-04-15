using Microsoft.UI.Xaml;

namespace OptiSYS;

/// <summary>
/// optiSYS - Unified Windows Optimization Suite
/// Entry point for the WinUI 3 application.
/// </summary>
public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
