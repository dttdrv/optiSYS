using OptiSYS.Core.Models;
using OptiSYS.Models;

namespace OptiSYS.Services;

/// <summary>
/// Computes the 0-100 tray optimization score from memory + battery telemetry.
///
/// <para>
/// Inputs (each normalised 0-100, higher = healthier):
/// <list type="bullet">
///   <item><c>MemHeadroom = 100 - usedPercent</c></item>
///   <item><c>MemSavings  = clamp(0,100, freedMB / targetMB * 100)</c>, target ≈ 10% of physical RAM</item>
///   <item><c>BattLevel   = </c> charge %</item>
///   <item><c>BattSavings = </c> drain-rate improvement vs a rolling baseline, or 0 when not reliable</item>
/// </list>
/// </para>
///
/// <para>
/// On battery (DC): <c>0.30·MemHeadroom + 0.20·MemSavings + 0.30·BattLevel + 0.20·BattSavings</c>.
/// Plugged in / no battery (AC): <c>0.60·MemHeadroom + 0.40·MemSavings</c> (battery terms drop).
/// </para>
///
/// <para>
/// The instance keeps a small rolling drain-rate baseline (an exponential moving average of
/// observed DC drain in milliwatts) so <c>BattSavings</c> can reward a lighter-than-usual drain.
/// The static <see cref="Compute"/> overload stays pure for unit testing the formula.
/// </para>
/// </summary>
public sealed class HealthScoreCalculator
{
    // Smoothing factor for the rolling drain-rate baseline. Small => slow-moving, stable baseline.
    private const double BaselineSmoothing = 0.1;

    private double? _drainBaselineMilliwatts;

    /// <summary>
    /// Computes the score from the latest telemetry, updating the rolling drain-rate baseline
    /// as a side effect. Returns a value clamped to [0, 100].
    /// </summary>
    public int Evaluate(MemoryInfo? memory, BatteryInfo? battery, long totalFreedBytes)
    {
        var onBattery = battery?.IsOnBattery == true;
        var battSavings = onBattery ? UpdateBaselineAndComputeBattSavings(battery!) : 0.0;

        return Compute(memory, battery, totalFreedBytes, battSavings);
    }

    /// <summary>
    /// Pure formula. <paramref name="battSavings"/> is the already-derived 0-100 drain-rate
    /// improvement (0 when no reliable baseline). Battery terms apply only on battery (DC).
    /// </summary>
    public static int Compute(MemoryInfo? memory, BatteryInfo? battery, long totalFreedBytes, double battSavings)
    {
        var totalBytes = memory?.TotalPhysicalBytes ?? 0;
        var usedPercent = memory?.UsagePercent ?? 0;

        var memHeadroom = Clamp(100 - usedPercent);

        // Target reclaim ≈ 10% of physical RAM. Guard against a zero target before telemetry warms up.
        var targetMb = totalBytes > 0 ? totalBytes * 0.10 / (1024.0 * 1024.0) : 0;
        var freedMb = totalFreedBytes / (1024.0 * 1024.0);
        var memSavings = targetMb > 0 ? Clamp(freedMb / targetMb * 100) : 0;

        var onBattery = battery?.IsOnBattery == true;

        double score;
        if (onBattery)
        {
            var battLevel = Clamp(battery!.ChargePercent);
            var clampedBattSavings = Clamp(battSavings);
            score = 0.30 * memHeadroom + 0.20 * memSavings + 0.30 * battLevel + 0.20 * clampedBattSavings;
        }
        else
        {
            // Plugged in or no battery: memory only.
            score = 0.60 * memHeadroom + 0.40 * memSavings;
        }

        return (int)Math.Round(Clamp(score), MidpointRounding.AwayFromZero);
    }

    private double UpdateBaselineAndComputeBattSavings(BatteryInfo battery)
    {
        // DrainRateMilliwatts is negative while discharging on some adapters; use magnitude.
        var currentDrain = Math.Abs(battery.DrainRateMilliwatts);

        // No usable drain sample yet (e.g. just plugged in / telemetry warming up).
        if (currentDrain <= 0)
        {
            return 0.0;
        }

        // First reliable sample seeds the baseline; no improvement claimable yet.
        if (_drainBaselineMilliwatts is not { } baseline || baseline <= 0)
        {
            _drainBaselineMilliwatts = currentDrain;
            return 0.0;
        }

        // Improvement = how much lighter the current drain is vs the rolling baseline.
        var improvement = (baseline - currentDrain) / baseline * 100.0;

        // Update baseline as an EMA so it tracks the typical drain over time.
        _drainBaselineMilliwatts = baseline + BaselineSmoothing * (currentDrain - baseline);

        return Clamp(improvement);
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 100);
}
