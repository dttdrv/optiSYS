using OptiSYS.Core.Models;
using OptiSYS.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

/// <summary>
/// The presenter is the pure half of the dashboard refresh: telemetry + automation state in,
/// the exact strings/values the shell binds out. These tests pin the formatting so the
/// MainWindow extraction stays behavior-preserving and future tweaks are observable here.
/// </summary>
public sealed class DashboardPresenterTests
{
    private const long GiB = 1L << 30;

    private static MemoryInfo SixteenGigsTwelveUsed() => new()
    {
        TotalPhysicalBytes = 16 * GiB,
        AvailablePhysicalBytes = 4 * GiB,    // 12 GiB used -> 75%
        StandbyCacheBytes = 2 * GiB,
        ProcessCount = 243,
    };

    private static DashboardState State(
        MemoryInfo? memory = null,
        BatteryInfo? battery = null,
        bool paused = false,
        DateTimeOffset? lastActivityAt = null) => new()
    {
        Memory = memory,
        Battery = battery,
        AutomationPaused = paused,
        MinimizeToTray = true,
        AutoOptimizeMemory = true,
        AutoOptimizeOnBattery = false,
        LastActivity = "Safe cleanup finished",
        LastActivityAt = lastActivityAt,
        TotalFreedBytes = 512L << 20,        // 512 MiB -> "512 MB"
        Now = new DateTime(2026, 6, 10, 14, 30, 5),
    };

    [Fact]
    public void Paused_ShowsResumeAffordances_AndPausedMode()
    {
        var view = DashboardPresenter.Present(State(paused: true));

        Assert.True(view.ShowPausedIndicators);
        Assert.Equal(0xE768, char.ConvertToUtf32(view.PauseGlyph, 0));   // Play glyph = resume affordance
        Assert.Equal("Resume optimization", view.PauseLabel);
        Assert.False(view.AutomationOn);
        Assert.Contains("mode              paused", view.StatusText);
    }

    [Fact]
    public void Running_ShowsPauseAffordances_AndSafeMode()
    {
        var view = DashboardPresenter.Present(State(paused: false));

        Assert.False(view.ShowPausedIndicators);
        Assert.Equal(0xE769, char.ConvertToUtf32(view.PauseGlyph, 0));   // Pause glyph while running
        Assert.Equal("Pause optimization", view.PauseLabel);
        Assert.True(view.AutomationOn);
        Assert.Contains("mode              safe optimization", view.StatusText);
    }

    [Fact]
    public void MemoryCard_WithTelemetry_FormatsTheFluentCardValues()
    {
        var view = DashboardPresenter.Present(State(memory: SixteenGigsTwelveUsed()));

        Assert.Equal("75%", view.Memory.PercentText);
        Assert.Equal("12.0 GB used of 16.0 GB", view.Memory.DetailText);
        Assert.Equal(75.0, view.Memory.ProgressValue);
        Assert.Equal("2.0 GB", view.Memory.CachedText);
        Assert.Equal("243", view.Memory.ProcessesText);
        Assert.Equal("512 MB", view.Memory.ClearedText);
        Assert.Equal(75.0, view.Memory.HistorySample);
    }

    [Fact]
    public void MemoryCard_WarmingUp_ShowsPlaceholders_AndNoChartSample()
    {
        var view = DashboardPresenter.Present(State(memory: null));

        Assert.Equal("--%", view.Memory.PercentText);
        Assert.Equal("Warming up memory telemetry...", view.Memory.DetailText);
        Assert.Equal(0, view.Memory.ProgressValue);
        Assert.Equal("-- GB", view.Memory.CachedText);
        Assert.Equal("--", view.Memory.ProcessesText);
        Assert.Null(view.Memory.ClearedText);                    // leave the freed counter as-is
        Assert.Null(view.Memory.HistorySample);                  // never chart a placeholder
    }

    [Fact]
    public void BatteryCard_OnBattery_ShowsDrainAndRemaining()
    {
        var battery = new BatteryInfo { PowerSource = PowerSource.Battery, HasBattery = true, ChargePercent = 63 };

        var view = DashboardPresenter.Present(State(battery: battery));

        Assert.Equal("63%", view.Battery.PercentText);
        Assert.Equal("Running on battery power", view.Battery.SourceText);
        Assert.Equal(63.0, view.Battery.ProgressValue);
        Assert.Equal(0xE83F, char.ConvertToUtf32(view.Battery.PowerGlyph!, 0));   // battery glyph
        Assert.Equal(battery.DrainRateDisplay, view.Battery.DrainText);
        Assert.Equal(battery.TimeRemainingDisplay, view.Battery.RemainingText);
    }

    [Fact]
    public void BatteryCard_PluggedIn_ShowsChargingPlaceholders()
    {
        var battery = new BatteryInfo { PowerSource = PowerSource.Ac, HasBattery = true, ChargePercent = 80 };

        var view = DashboardPresenter.Present(State(battery: battery));

        Assert.Equal("80%", view.Battery.PercentText);
        Assert.Equal("Connected to power", view.Battery.SourceText);
        Assert.Equal(80.0, view.Battery.ProgressValue);
        Assert.Equal(0xE72F, char.ConvertToUtf32(view.Battery.PowerGlyph!, 0));   // plugged-in glyph
        Assert.Equal("N/A (charging)", view.Battery.DrainText);
        Assert.Equal("N/A (plugged in)", view.Battery.RemainingText);
    }

    [Fact]
    public void BatteryCard_DesktopWithoutBattery_ShowsAcAtFullBar()
    {
        var battery = new BatteryInfo { PowerSource = PowerSource.Ac, HasBattery = false };

        var view = DashboardPresenter.Present(State(battery: battery));

        Assert.Equal("AC", view.Battery.PercentText);
        Assert.Equal(100.0, view.Battery.ProgressValue);
    }

    [Fact]
    public void BatteryCard_WarmingUp_ShowsPlaceholders_AndKeepsTheCurrentGlyph()
    {
        var view = DashboardPresenter.Present(State(battery: null));

        Assert.Equal("--%", view.Battery.PercentText);
        Assert.Equal("Warming up battery telemetry...", view.Battery.SourceText);
        Assert.Equal(0, view.Battery.ProgressValue);
        Assert.Null(view.Battery.PowerGlyph);                    // don't swap the icon blindly
        Assert.Equal("--", view.Battery.DrainText);
        Assert.Equal("--", view.Battery.RemainingText);
    }

    [Fact]
    public void StatusText_CarriesTheCanonicalObserverSections()
    {
        var battery = new BatteryInfo { PowerSource = PowerSource.Battery, HasBattery = true, ChargePercent = 63 };
        var at = new DateTimeOffset(2026, 6, 10, 14, 29, 0, TimeSpan.FromHours(2));

        var view = DashboardPresenter.Present(
            State(memory: SixteenGigsTwelveUsed(), battery: battery, lastActivityAt: at));

        Assert.Contains("time              2026-06-10 14:30:05", view.StatusText);
        Assert.Contains("background        tray enabled", view.StatusText);
        Assert.Contains("memory_auto       on", view.StatusText);
        Assert.Contains("battery_auto      off", view.StatusText);
        Assert.Contains("[memory]", view.StatusText);
        Assert.Contains("usage             75%", view.StatusText);
        Assert.Contains("[power]", view.StatusText);
        Assert.Contains("source            on battery", view.StatusText);
        Assert.Contains("activity          Safe cleanup finished", view.StatusText);
        Assert.Contains("activity_time     ", view.StatusText);
    }

    [Fact]
    public void StatusText_OmitsActivityTime_WhenNoActivityYet()
    {
        var view = DashboardPresenter.Present(State(lastActivityAt: null));

        Assert.DoesNotContain("activity_time", view.StatusText);
    }

    [Fact]
    public void FooterText_StampsTheSampleTime()
    {
        var view = DashboardPresenter.Present(State());

        Assert.Equal("last sample 14:30:05 // safe runtime optimization only", view.FooterText);
    }
}
