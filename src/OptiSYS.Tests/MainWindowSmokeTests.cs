using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xunit;

namespace OptiSYS.Tests;

/// <summary>
/// Smoke checks for <see cref="OptiSYS.MainWindow"/> that use reflection only — we don't
/// instantiate the window because WinUI's <c>Window</c> class requires a running dispatcher
/// (unavailable in the xUnit process). The goal is to catch XAML-refactor mistakes that
/// would silently rename or remove the x:Named fields the code-behind depends on.
/// </summary>
public class MainWindowSmokeTests
{
    [Fact]
    public void MainWindow_DerivesFromXamlWindow()
    {
        var type = typeof(OptiSYS.MainWindow);
        Assert.True(typeof(Window).IsAssignableFrom(type),
            "MainWindow must extend Microsoft.UI.Xaml.Window.");
    }

    [Theory]
    [InlineData("NavView", typeof(NavigationView))]
    [InlineData("ContentFrame", typeof(Frame))]
    public void MainWindow_HasCompiledXNameField(string fieldName, Type expectedType)
    {
        // x:Name in XAML generates a private instance field of the matching control type
        // via the partial class that ships alongside in obj/.../MainWindow.g.cs.
        var field = typeof(OptiSYS.MainWindow).GetField(
            fieldName, BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(expectedType, field!.FieldType);
    }
}
