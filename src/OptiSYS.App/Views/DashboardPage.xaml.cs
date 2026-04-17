using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using OptiSYS.ViewModels;

namespace OptiSYS.Views;

/// <summary>
/// Dashboard page — resolves <see cref="DashboardViewModel"/> from the DI container
/// and binds it as the page's <c>DataContext</c>. The VM owns all state; this class is
/// intentionally a thin shim with no business logic.
/// </summary>
public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        // Frame.Navigate uses the parameterless ctor, so DI happens here rather than at
        // construction-injection — AppHost.Services is a static kept alive for the
        // app's lifetime by App.xaml.cs's OnLaunched.
        DataContext = AppHost.Services.GetRequiredService<DashboardViewModel>();
    }
}
