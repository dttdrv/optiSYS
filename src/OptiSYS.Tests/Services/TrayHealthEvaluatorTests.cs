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
    [InlineData(616L, "Memory: 62%")]  // 1000 total, 616 used -> 61.6% -> rounds to 62
    [InlineData(0L, "Memory: 0%")]
    [InlineData(994L, "Memory: 99%")]  // 99.4% -> rounds to 99
    public void MemoryTooltip_FormatsRoundedUsagePercent(long usedBytes, string expected)
    {
        var memory = MemoryAtUsedBytes(usedBytes);

        Assert.Equal(expected, TrayHealthEvaluator.MemoryTooltip(memory));
    }

    [Fact]
    public void MemoryTooltip_NullMemory_ReadsZeroPercent()
    {
        Assert.Equal("Memory: 0%", TrayHealthEvaluator.MemoryTooltip(null));
    }

    private static MemoryInfo MemoryAtUsedBytes(long usedBytes)
    {
        const long total = 1000;
        return new MemoryInfo { TotalPhysicalBytes = total, AvailablePhysicalBytes = total - usedBytes };
    }
}
