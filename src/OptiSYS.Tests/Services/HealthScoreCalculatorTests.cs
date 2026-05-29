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

        var score = HealthScoreCalculator.Compute(memory, battery, freedBytes, battEfficiency: 999);

        score.Should().Be(76, "battery terms drop on AC and battEfficiency is ignored");
    }

    [Fact]
    public void Compute_OnBattery_BlendsMemoryAndBatteryEfficiency()
    {
        // MemHeadroom = 60, MemSavings = 100, BattEfficiency = 50 (neutral / at baseline).
        // Expected = 0.50*60 + 0.20*100 + 0.30*50 = 30 + 20 + 15 = 65. Charge % is irrelevant.
        var memory = MemoryAt(EightGb, usedPercent: 40);
        var battery = new BatteryInfo { PowerSource = PowerSource.Battery, HasBattery = true, ChargePercent = 50 };
        var freedBytes = (long)(EightGb * 0.10);

        var score = HealthScoreCalculator.Compute(memory, battery, freedBytes, battEfficiency: 50);

        score.Should().Be(65);
    }

    [Fact]
    public void Compute_OnBattery_IgnoresChargePercent()
    {
        // Two batteries differing ONLY in charge % must yield the same score: the formula
        // no longer factors in how full the battery is, only how efficiently it drains.
        var memory = MemoryAt(EightGb, usedPercent: 40);
        var freedBytes = (long)(EightGb * 0.10);
        var nearlyEmpty = new BatteryInfo { PowerSource = PowerSource.Battery, HasBattery = true, ChargePercent = 5 };
        var nearlyFull = new BatteryInfo { PowerSource = PowerSource.Battery, HasBattery = true, ChargePercent = 95 };

        var lowScore = HealthScoreCalculator.Compute(memory, nearlyEmpty, freedBytes, battEfficiency: 50);
        var highScore = HealthScoreCalculator.Compute(memory, nearlyFull, freedBytes, battEfficiency: 50);

        lowScore.Should().Be(highScore, "charge % must not influence the score");
    }

    [Fact]
    public void Compute_MemSavings_ClampsAtTarget()
    {
        // Freeing far more than the 10% target still caps MemSavings at 100.
        var memory = MemoryAt(EightGb, usedPercent: 50); // MemHeadroom = 50
        var battery = new BatteryInfo { PowerSource = PowerSource.Ac, HasBattery = false };
        var freedBytes = EightGb; // way over target

        var score = HealthScoreCalculator.Compute(memory, battery, freedBytes, battEfficiency: 0);

        // 0.60*50 + 0.40*100 = 70
        score.Should().Be(70);
    }

    [Fact]
    public void Compute_ResultIsClampedToZeroHundred()
    {
        var memory = MemoryAt(EightGb, usedPercent: 100); // MemHeadroom = 0
        var battery = new BatteryInfo { PowerSource = PowerSource.Ac, HasBattery = false };

        var score = HealthScoreCalculator.Compute(memory, battery, totalFreedBytes: 0, battEfficiency: 0);

        score.Should().BeInRange(0, 100);
        score.Should().Be(0);
    }

    [Fact]
    public void Evaluate_FirstBatterySample_ProducesNeutralBattEfficiency()
    {
        // No prior baseline -> the first reliable drain sample seeds the baseline and is, by
        // definition, "at baseline" => BattEfficiency = 50 (neutral). Charge % is ignored.
        // MemHeadroom = 60, MemSavings = 0 (no freed), BattEfficiency = 50.
        // Expected = 0.50*60 + 0.20*0 + 0.30*50 = 30 + 15 = 45.
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

        score.Should().Be(45);
    }

    [Fact]
    public void Evaluate_NoDrainSample_StaysNeutral_NotPunished()
    {
        // Fresh boot on battery with no usable drain reading yet -> neutral 50, not 0.
        // MemHeadroom = 60, MemSavings = 0, BattEfficiency = 50 -> 0.50*60 + 0.30*50 = 45.
        var calc = new HealthScoreCalculator();
        var memory = MemoryAt(EightGb, usedPercent: 40);
        var battery = new BatteryInfo
        {
            PowerSource = PowerSource.Battery,
            HasBattery = true,
            ChargePercent = 80,
            DrainRateMilliwatts = 0,
        };

        var score = calc.Evaluate(memory, battery, totalFreedBytes: 0);

        score.Should().Be(45, "a fresh boot with no drain sample is neutral, not punished");
    }

    [Fact]
    public void Evaluate_LighterDrainThanBaseline_RaisesScore()
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
