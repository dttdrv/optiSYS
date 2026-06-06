using System.Drawing;
using FluentAssertions;
using OptiSYS.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public sealed class TrayIconServiceTests
{
    [Theory]
    [InlineData(0x0202u)]
    [InlineData(0x0205u)]
    [InlineData(0x007Bu)]
    [InlineData(0x0400u)]
    [InlineData(0x0401u)]
    public void ExtractTrayEventId_ReturnsLowWord_WhenNotifyIconVersion4PacksIconId(uint eventId)
    {
        var lParam = PackNotifyIconLParam(iconId: 1, eventId);

        TrayIconService.ExtractTrayEventId(lParam).Should().Be(eventId);
    }

    [Theory]
    [InlineData(true, 29, 33, 36)]
    [InlineData(false, 238, 244, 239)]
    public void SelectStrokeColor_InvertsIconStrokeForWindowsTheme(
        bool isLightTheme,
        int expectedRed,
        int expectedGreen,
        int expectedBlue)
    {
        var color = TrayIconService.SelectStrokeColor(isLightTheme);

        color.R.Should().Be((byte)expectedRed);
        color.G.Should().Be((byte)expectedGreen);
        color.B.Should().Be((byte)expectedBlue);
        color.A.Should().Be(255);
    }

    [Theory]
    [InlineData(20, TrayDot.Green, false, 20, TrayDot.Green, false, false)]  // nothing changed -> no re-render
    [InlineData(20, TrayDot.Green, false, 21, TrayDot.Green, false, true)]   // number changed -> re-render
    [InlineData(20, TrayDot.Green, false, 20, TrayDot.Yellow, false, true)]  // colour changed -> re-render
    [InlineData(20, TrayDot.Green, false, 21, TrayDot.Red, false, true)]     // both changed -> re-render
    [InlineData(20, TrayDot.Green, false, 20, TrayDot.Green, true, true)]    // theme flipped -> re-render
    public void ShouldRerender_IsTrue_OnlyWhenNumberDotOrThemeChanges(
        int prevNumber,
        TrayDot prevDot,
        bool prevIsLight,
        int newNumber,
        TrayDot newDot,
        bool newIsLight,
        bool expected)
    {
        TrayIconService.ShouldRerender(prevNumber, prevDot, prevIsLight, newNumber, newDot, newIsLight)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(7, TrayDot.Green, true)]
    [InlineData(42, TrayDot.Yellow, false)]
    [InlineData(0, TrayDot.Red, true)]
    [InlineData(99, TrayDot.Green, false)]
    public void DotIconRenderer_Render_ReturnsNonNull32x32Icon(int number, TrayDot dot, bool isLightTheme)
    {
        using var icon = TrayIconService.DotIconRenderer.Render(number, dot, isLightTheme, out var handle);

        icon.Should().NotBeNull();
        icon.Width.Should().Be(32);
        icon.Height.Should().Be(32);
        handle.Should().NotBe(nint.Zero);
    }

    private static nint PackNotifyIconLParam(uint iconId, uint eventId) =>
        unchecked((nint)(((ulong)iconId << 16) | eventId));
}
