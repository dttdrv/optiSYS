using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using OptiSYS.Views;
using Xunit;

namespace OptiSYS.Tests.Views;

/// <summary>
/// Compile-level smoke tests for the five navigation pages. Like
/// <see cref="MainWindowSmokeTests"/>, these do NOT instantiate the pages — WinUI's
/// <see cref="Page"/> type needs a live XAML dispatcher that xUnit cannot provide
/// without a full WinUI test host.
///
/// <para>
/// What we ARE checking is stronger than "the types exist": for every Page the XAML
/// compiler emits a generated partial class (in <c>obj/.../XxxPage.g.i.cs</c>) that
/// defines <c>InitializeComponent()</c> and the <c>_contentLoaded</c> guard field.
/// Those symbols only land in the assembly when the XAML parses cleanly AND every
/// resource key / converter name resolves. A missing converter key or a malformed
/// binding surface fails the build, not this test — but if someone later swaps one
/// of these files to a "code-only" page without re-adding <c>InitializeComponent</c>,
/// this test catches it.
/// </para>
///
/// <para>
/// We check via reflection against <see cref="BindingFlags.NonPublic"/> because
/// <c>InitializeComponent</c> is marked <c>private</c> in the generated code.
/// </para>
/// </summary>
public class PageResolutionTests
{
    [Theory]
    [InlineData(typeof(DashboardPage))]
    [InlineData(typeof(BatteryPage))]
    [InlineData(typeof(MemoryPage))]
    [InlineData(typeof(ProcessesPage))]
    [InlineData(typeof(SettingsPage))]
    public void Page_DerivesFromXamlPage(Type pageType)
    {
        Assert.True(typeof(Page).IsAssignableFrom(pageType),
            $"{pageType.FullName} must extend Microsoft.UI.Xaml.Controls.Page.");
    }

    [Theory]
    [InlineData(typeof(DashboardPage))]
    [InlineData(typeof(BatteryPage))]
    [InlineData(typeof(MemoryPage))]
    [InlineData(typeof(ProcessesPage))]
    [InlineData(typeof(SettingsPage))]
    public void Page_HasCompiledXamlInitializer(Type pageType)
    {
        // Presence of this method proves the XAML compiler emitted a generated partial
        // for this Page — which in turn means the XAML parsed cleanly end-to-end.
        var init = pageType.GetMethod(
            "InitializeComponent",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(init);
    }

    [Theory]
    [InlineData(typeof(DashboardPage))]
    [InlineData(typeof(BatteryPage))]
    [InlineData(typeof(MemoryPage))]
    [InlineData(typeof(ProcessesPage))]
    [InlineData(typeof(SettingsPage))]
    public void Page_HasParameterlessConstructor(Type pageType)
    {
        // Frame.Navigate only invokes the parameterless ctor — if someone accidentally
        // switches a page to constructor injection this test fires before runtime.
        var ctor = pageType.GetConstructor(Type.EmptyTypes);
        Assert.NotNull(ctor);
    }
}
