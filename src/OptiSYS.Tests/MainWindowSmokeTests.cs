using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xunit;

namespace OptiSYS.Tests;

/// <summary>
/// Smoke checks for <see cref="OptiSYS.MainWindow"/> that use reflection only. We do not
/// instantiate the window because WinUI's <c>Window</c> class requires a running dispatcher.
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

    [Fact]
    public void MainWindow_HasCompiledShellRootField()
    {
        // x:Name in XAML generates a private instance field of the matching control type
        // via the partial class that ships alongside in obj/.../MainWindow.g.cs.
        var field = typeof(OptiSYS.MainWindow).GetField(
            "ShellRoot", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(Grid), field!.FieldType);
    }

    [Theory]
    [InlineData("StatusText")]
    [InlineData("FooterText")]
    public void MainWindow_HasReadOnlyObserverFields(string fieldName)
    {
        var field = typeof(OptiSYS.MainWindow).GetField(
            fieldName, BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(TextBlock), field!.FieldType);
    }

    [Theory]
    [InlineData("DashboardGrid")]
    [InlineData("MemoryGrid")]
    [InlineData("PowerGrid")]
    [InlineData("ProtectedAppsGrid")]
    [InlineData("SettingsGrid")]
    public void MainWindow_HasPageContainers(string fieldName)
    {
        var field = typeof(OptiSYS.MainWindow).GetField(
            fieldName, BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(ScrollViewer), field!.FieldType);
    }
}
