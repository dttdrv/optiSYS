using OptiSYS.Core.Models;
using OptiSYS.Models;
using OptiSYS.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class TrayHealthEvaluatorTests
{
    [Theory]
    [InlineData(OverallHealthState.Great, TrayDot.Green)]
    [InlineData(OverallHealthState.Good, TrayDot.Green)]
    [InlineData(OverallHealthState.Normal, TrayDot.Yellow)]
    [InlineData(OverallHealthState.NotGood, TrayDot.Red)]
    [InlineData(OverallHealthState.Bad, TrayDot.Red)]
    public void Maps_HealthState_To_Dot(OverallHealthState state, TrayDot dot)
    {
        Assert.Equal(dot, TrayHealthEvaluator.DotFor(state));
    }

    [Theory]
    [InlineData(616L, 62)]  // 1000 total, 616 used -> 61.6% -> rounds to 62
    [InlineData(0L, 0)]
    [InlineData(994L, 99)]  // 99.4% -> rounds to 99
    [InlineData(1000L, 99)] // 100% -> clamped to two digits (99)
    public void MemoryDisplayNumber_RoundsAndClampsUsagePercent(long usedBytes, int expected)
    {
        var memory = MemoryAtUsedBytes(usedBytes);

        Assert.Equal(expected, TrayHealthEvaluator.MemoryDisplayNumber(memory));
    }

    [Fact]
    public void MemoryDisplayNumber_NullMemory_IsZero()
    {
        Assert.Equal(0, TrayHealthEvaluator.MemoryDisplayNumber(null));
    }

    [Fact]
    public void DischargeWatts_OnBattery_RoundsMagnitudeOfSignedMilliwatts()
    {
        // Discharging: Rate is negative mW. 15500 mW -> 16 W (round), magnitude of the sign.
        var battery = new BatteryInfo { PowerSource = PowerSource.Battery, DrainRateMilliwatts = -15500 };

        Assert.Equal(16, TrayHealthEvaluator.DischargeWatts(battery));
    }

    [Fact]
    public void DischargeWatts_OnAc_IsZero_EvenWhenChargeRateIsPositive()
    {
        // Charging: Rate is positive mW. On AC the charge rate must never read as a load -> 0.
        var battery = new BatteryInfo { PowerSource = PowerSource.Ac, DrainRateMilliwatts = 42000 };

        Assert.Equal(0, TrayHealthEvaluator.DischargeWatts(battery));
    }

    [Fact]
    public void DischargeWatts_ClampsToTwoDigits()
    {
        // A value rounding to >= 100 W is clamped to 99 so the number stays legible at icon size.
        var battery = new BatteryInfo { PowerSource = PowerSource.Battery, DrainRateMilliwatts = -150000 };

        Assert.Equal(99, TrayHealthEvaluator.DischargeWatts(battery));
    }

    [Fact]
    public void DischargeWatts_NullBattery_IsZero()
    {
        Assert.Equal(0, TrayHealthEvaluator.DischargeWatts(null));
    }

    [Theory]
    [InlineData(OverallHealthState.Great, "Great")]
    [InlineData(OverallHealthState.Good, "Good")]
    [InlineData(OverallHealthState.Normal, "Normal")]
    [InlineData(OverallHealthState.NotGood, "Poor")]
    [InlineData(OverallHealthState.Bad, "Poor")]
    public void EfficiencyLabel_MapsHealthStateToShortLabel(OverallHealthState state, string expected)
    {
        Assert.Equal(expected, TrayHealthEvaluator.EfficiencyLabel(state));
    }

    [Fact]
    public void BatteryTooltip_OnBattery_ShowsDrawWattsAndEfficiency()
    {
        var battery = new BatteryInfo { PowerSource = PowerSource.Battery, DrainRateMilliwatts = -12000 };

        Assert.Equal("12 W draw • Good", TrayHealthEvaluator.BatteryTooltip(battery, OverallHealthState.Good));
    }

    [Fact]
    public void BatteryTooltip_OnAc_ShowsOnAcAndEfficiency()
    {
        var battery = new BatteryInfo { PowerSource = PowerSource.Ac, DrainRateMilliwatts = 42000 };

        Assert.Equal("On AC • Great", TrayHealthEvaluator.BatteryTooltip(battery, OverallHealthState.Great));
    }

    [Fact]
    public void BatteryTooltip_NullBattery_ReadsAsAc()
    {
        Assert.Equal("On AC • Normal", TrayHealthEvaluator.BatteryTooltip(null, OverallHealthState.Normal));
    }

    private static MemoryInfo MemoryAtUsedBytes(long usedBytes)
    {
        const long total = 1000;
        return new MemoryInfo { TotalPhysicalBytes = total, AvailablePhysicalBytes = total - usedBytes };
    }
}
