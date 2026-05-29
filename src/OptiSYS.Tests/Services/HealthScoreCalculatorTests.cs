using FluentAssertions;
using OptiSYS.Core.Models;
using OptiSYS.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public sealed class HealthScoreCalculatorTests
{
    private const long EightGb = 8L * 1024 * 1024 * 1024;

    [Fact]
    public void Compute_OnAc_UsesMemoryOnlyFormula()
    {
        // 8 GB total, 40% used -> MemHeadroom = 60. Target reclaim ≈ 10% of 8 GB ≈ 819 MB.
        // Freed 819 MB -> MemSavings = 100. Expected = 0.60*60 + 0.40*100 = 76.
        var memory = MemoryAt(EightGb, usedPercent: 40);
        var battery = new BatteryInfo { PowerSource = PowerSource.Ac, HasBattery = true, ChargePercent = 90 };
        var freedBytes = (long)(EightGb * 0.10); // exactly the target

        var score = HealthScoreCalculator.Compute(memory, battery, freedBytes, battSavings: 999);

        score.Should().Be(76, "battery terms drop on AC and battSavings is ignored");
    }

    [Fact]
    public void Compute_OnBattery_BlendsMemoryAndBatteryTerms()
    {
        // MemHeadroom = 60, MemSavings = 100, BattLevel = 50, BattSavings = 20.
        // Expected = 0.30*60 + 0.20*100 + 0.30*50 + 0.20*20 = 18 + 20 + 15 + 4 = 57.
        var memory = MemoryAt(EightGb, usedPercent: 40);
        var battery = new BatteryInfo { PowerSource = PowerSource.Battery, HasBattery = true, ChargePercent = 50 };
        var freedBytes = (long)(EightGb * 0.10);

        var score = HealthScoreCalculator.Compute(memory, battery, freedBytes, battSavings: 20);

        score.Should().Be(57);
    }

    [Fact]
    public void Compute_MemSavings_ClampsAtTarget()
    {
        // Freeing far more than the 10% target still caps MemSavings at 100.
        var memory = MemoryAt(EightGb, usedPercent: 50); // MemHeadroom = 50
        var battery = new BatteryInfo { PowerSource = PowerSource.Ac, HasBattery = false };
        var freedBytes = EightGb; // way over target

        var score = HealthScoreCalculator.Compute(memory, battery, freedBytes, battSavings: 0);

        // 0.60*50 + 0.40*100 = 70
        score.Should().Be(70);
    }

    [Fact]
    public void Compute_ResultIsClampedToZeroHundred()
    {
        var memory = MemoryAt(EightGb, usedPercent: 100); // MemHeadroom = 0
        var battery = new BatteryInfo { PowerSource = PowerSource.Ac, HasBattery = false };

        var score = HealthScoreCalculator.Compute(memory, battery, totalFreedBytes: 0, battSavings: 0);

        score.Should().BeInRange(0, 100);
        score.Should().Be(0);
    }

    [Fact]
    public void Evaluate_FirstBatterySample_ProducesZeroBattSavings()
    {
        // No prior baseline -> BattSavings = 0, so battery contributes only via BattLevel.
        // MemHeadroom = 60, MemSavings = 0 (no freed), BattLevel = 80, BattSavings = 0.
        // Expected = 0.30*60 + 0.20*0 + 0.30*80 + 0.20*0 = 18 + 24 = 42.
        var calc = new HealthScoreCalculator();
        var memory = MemoryAt(EightGb, usedPercent: 40);
        var battery = new BatteryInfo
        {
            PowerSource = PowerSource.Battery,
            HasBattery = true,
            ChargePercent = 80,
            DrainRateMilliwatts = -12000,
        };

        var score = calc.Evaluate(memory, battery, totalFreedBytes: 0);

        score.Should().Be(42);
    }

    [Fact]
    public void Evaluate_LighterDrainThanBaseline_RewardsBattSavings()
    {
        var calc = new HealthScoreCalculator();
        var memory = MemoryAt(EightGb, usedPercent: 40);

        // First sample seeds the baseline at 20 W.
        var seed = new BatteryInfo
        {
            PowerSource = PowerSource.Battery,
            HasBattery = true,
            ChargePercent = 80,
            DrainRateMilliwatts = -20000,
        };
        var first = calc.Evaluate(memory, seed, totalFreedBytes: 0);

        // Second sample drains lighter (10 W) -> positive improvement vs baseline.
        var lighter = new BatteryInfo
        {
            PowerSource = PowerSource.Battery,
            HasBattery = true,
            ChargePercent = 80,
            DrainRateMilliwatts = -10000,
        };
        var second = calc.Evaluate(memory, lighter, totalFreedBytes: 0);

        second.Should().BeGreaterThan(first, "a lighter-than-baseline drain should raise the score");
    }

    private static MemoryInfo MemoryAt(long totalBytes, double usedPercent)
    {
        var available = (long)(totalBytes * (1 - usedPercent / 100.0));
        return new MemoryInfo
        {
            TotalPhysicalBytes = totalBytes,
            AvailablePhysicalBytes = available,
        };
    }
}
