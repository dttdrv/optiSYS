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
    [InlineData(0, "0")]
    [InlineData(42, "42")]
    [InlineData(100, "100")]   // full health shows the number, never "OK"
    [InlineData(150, "100")]   // clamped
    [InlineData(-5, "0")]      // clamped
    public void FormatScore_AlwaysRendersTheClampedNumber(int score, string expected)
    {
        TrayIconService.ScoreIconRenderer.FormatScore(score).Should().Be(expected);
    }

    private static nint PackNotifyIconLParam(uint iconId, uint eventId) =>
        unchecked((nint)(((ulong)iconId << 16) | eventId));
}
