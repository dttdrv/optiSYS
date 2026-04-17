using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using OptiSYS.ViewModels;

namespace OptiSYS.Views;

public sealed partial class MemoryPage : Page
{
    public MemoryPage()
    {
        InitializeComponent();
        // DI resolve at page construction — Frame.Navigate only invokes the parameterless ctor.
        DataContext = AppHost.Services.GetRequiredService<MemoryViewModel>();
    }
}
