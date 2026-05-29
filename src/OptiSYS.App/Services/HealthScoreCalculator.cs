using OptiSYS.Core.Models;
using OptiSYS.Models;

namespace OptiSYS.Services;

/// <summary>
/// Computes the 0-100 tray optimization score from memory + battery telemetry.
///
/// <para>
/// Inputs (each normalised 0-100, higher = healthier):
/// <list type="bullet">
///   <item><c>MemHeadroom    = 100 - usedPercent</c></item>
///   <item><c>MemSavings     = clamp(0,100, freedMB / targetMB * 100)</c>, target ≈ 10% of physical RAM</item>
///   <item><c>BattEfficiency = </c> how efficiently the battery is being used (drain rate vs the
///         rolling baseline): <b>neutral 50 at baseline</b>, higher when the current drain is lighter
///         than usual, lower when heavier; 50 when there is no usable drain sample yet (fresh boot).</item>
/// </list>
/// The score deliberately ignores battery <em>charge %</em> — a near-empty battery that is being
/// sipped efficiently is "healthy" from an optimizer's point of view.
/// </para>
///
/// <para>
/// On battery (DC): <c>0.50·MemHeadroom + 0.20·MemSavings + 0.30·BattEfficiency</c>.
/// Plugged in / no battery (AC): <c>0.60·MemHeadroom + 0.40·MemSavings</c> (the battery term drops).
/// </para>
///
/// <para>
/// The instance keeps a small rolling drain-rate baseline (an exponential moving average of
/// observed DC drain in milliwatts) so <c>BattEfficiency</c> can reward a lighter-than-usual drain.
/// The static <see cref="Compute"/> overload stays pure for unit testing the formula.
/// </para>
/// </summary>
public sealed class HealthScoreCalculator
{
    // Smoothing factor for the rolling drain-rate baseline. Small => slow-moving, stable baseline.
    private const double BaselineSmoothing = 0.1;

    // Neutral efficiency: the drain matches the baseline, or no sample is available yet.
    private const double NeutralEfficiency = 50.0;

    private double? _drainBaselineMilliwatts;

    /// <summary>
    /// Computes the score from the latest telemetry, updating the rolling drain-rate baseline
    /// as a side effect. Returns a value clamped to [0, 100].
    /// </summary>
    public int Evaluate(MemoryInfo? memory, BatteryInfo? battery, long totalFreedBytes)
    {
        var onBattery = battery?.IsOnBattery == true;
        var battEfficiency = onBattery
            ? UpdateBaselineAndComputeBattEfficiency(battery!)
            : NeutralEfficiency;

        return Compute(memory, battery, totalFreedBytes, battEfficiency);
    }

    /// <summary>
    /// Pure formula. <paramref name="battEfficiency"/> is the already-derived 0-100 battery
    /// efficiency term (50 = neutral / at baseline). Battery terms apply only on battery (DC);
    /// on AC the value is ignored.
    /// </summary>
    public static int Compute(MemoryInfo? memory, BatteryInfo? battery, long totalFreedBytes, double battEfficiency)
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
            // Battery contribution reflects how *efficiently* the battery is used (drain rate),
            // not how full it is. Charge % is intentionally excluded.
            var clampedEfficiency = Clamp(battEfficiency);
            score = 0.50 * memHeadroom + 0.20 * memSavings + 0.30 * clampedEfficiency;
        }
        else
        {
            // Plugged in or no battery: memory only.
            score = 0.60 * memHeadroom + 0.40 * memSavings;
        }

        return (int)Math.Round(Clamp(score), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Derives the 0-100 battery efficiency term from the current drain vs the rolling baseline:
    /// <c>50 + (baseline - current) / baseline * 50</c> (clamped). Neutral 50 means "as expected";
    /// a lighter-than-baseline drain scores above 50, a heavier one below. Returns the neutral
    /// value when no usable drain sample exists yet so a fresh boot is not punished.
    /// </summary>
    private double UpdateBaselineAndComputeBattEfficiency(BatteryInfo battery)
    {
        // DrainRateMilliwatts is negative while discharging on some adapters; use magnitude.
        var currentDrain = Math.Abs(battery.DrainRateMilliwatts);

        // No usable drain sample yet (e.g. just plugged in / telemetry warming up): stay neutral.
        if (currentDrain <= 0)
        {
            return NeutralEfficiency;
        }

        // First reliable sample seeds the baseline; by definition it sits at the baseline => neutral.
        if (_drainBaselineMilliwatts is not { } baseline || baseline <= 0)
        {
            _drainBaselineMilliwatts = currentDrain;
            return NeutralEfficiency;
        }

        // Efficiency rises above 50 when the current drain is lighter than the baseline, and
        // falls below 50 when it is heavier; +/-50 maps a full baseline-sized swing to 0..100.
        var efficiency = NeutralEfficiency + (baseline - currentDrain) / baseline * 50.0;

        // Update baseline as an EMA so it tracks the typical drain over time.
        _drainBaselineMilliwatts = baseline + BaselineSmoothing * (currentDrain - baseline);

        return Clamp(efficiency);
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 100);
}
