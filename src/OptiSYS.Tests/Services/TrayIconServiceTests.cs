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

    [Fact]
    public void DotIconRenderer_TwoDigitNumbers_RenderBothDigits()
    {
        // Regression guard (introduced in 7bec12b): GDI+ DrawString into a layout RECTANGLE
        // character-trims text that doesn't fit the layout width — NoClip does not prevent
        // layout-time trimming — so "60" rendered as just "6". A two-digit render's ink must be
        // meaningfully wider than a one-digit render's.
        var oneDigitInk = NumberInkWidth(9);
        var twoDigitInk = NumberInkWidth(99);

        twoDigitInk.Should().BeGreaterThanOrEqualTo((int)(oneDigitInk * 1.5),
            $"'99' (ink {twoDigitInk}px) must render both digits, not get trimmed to one (ink {oneDigitInk}px)");
    }

    // Horizontal extent of the number's stroke pixels on the dark-theme icon: the stroke is
    // near-white (R 238), while the green efficiency dot (R 46) is excluded by the red gate.
    private static int NumberInkWidth(int number)
    {
        using var icon = TrayIconService.DotIconRenderer.Render(number, TrayDot.Green, isLightTheme: false, out _);
        using var bmp = icon.ToBitmap();

        int min = bmp.Width, max = -1;
        for (var x = 0; x < bmp.Width; x++)
        {
            for (var y = 0; y < bmp.Height; y++)
            {
                var pixel = bmp.GetPixel(x, y);
                if (pixel.A > 128 && pixel.R > 200)
                {
                    if (x < min) min = x;
                    if (x > max) max = x;
                }
            }
        }

        return Math.Max(0, max - min + 1);
    }

    [Theory]
    [InlineData(0x0016u, 1ul, true)]   // WM_ENDSESSION, end committed -> record clean exit
    [InlineData(0x0016u, 0ul, false)]  // WM_ENDSESSION, end cancelled -> session continues
    [InlineData(0x0011u, 1ul, false)]  // WM_QUERYENDSESSION is only the question, not the commit
    [InlineData(0x0202u, 1ul, false)]  // unrelated message -> ignore
    public void IsSessionEndCommit_IsTrue_OnlyWhenEndSessionCommitsTheShutdown(
        uint msg,
        ulong wParam,
        bool expected)
    {
        TrayIconService.IsSessionEndCommit(msg, (nuint)wParam).Should().Be(expected);
    }

    private static nint PackNotifyIconLParam(uint iconId, uint eventId) =>
        unchecked((nint)(((ulong)iconId << 16) | eventId));
}
