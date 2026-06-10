using Xunit;

namespace OptiSYS.Tests;

/// <summary>
/// The sliding nav pill's Y is computed from live layout (row top + centring) instead of
/// hardcoded offsets, so spacing/height edits in the sidebar XAML can't desync the indicator.
/// The first two cases pin equivalence with the legacy constants (11 / 51) for the current
/// 36px rows with 0,1 margins and 2px spacing.
/// </summary>
public sealed class NavGeometryTests
{
    [Fact]
    public void CentersThePillInTheFirstRow_MatchingTheLegacyOffset()
    {
        // Dashboard row: top margin 1, row height 36, pill 16 -> 1 + (36-16)/2 = 11.
        Assert.Equal(11, MainWindow.NavGeometry.CenterInRow(rowTop: 1, rowHeight: 36, indicatorHeight: 16));
    }

    [Fact]
    public void CentersThePillInTheSecondRow_MatchingTheLegacyOffset()
    {
        // Settings row top: 1 + 36 + 1 + 2 + 1 = 41 -> 41 + 10 = 51.
        Assert.Equal(51, MainWindow.NavGeometry.CenterInRow(rowTop: 41, rowHeight: 36, indicatorHeight: 16));
    }

    [Fact]
    public void NeverRisesAboveTheRowTop_WhenThePillIsTallerThanTheRow()
    {
        Assert.Equal(5, MainWindow.NavGeometry.CenterInRow(rowTop: 5, rowHeight: 10, indicatorHeight: 16));
    }
}
