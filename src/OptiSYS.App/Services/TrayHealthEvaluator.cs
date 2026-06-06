using OptiSYS.Core.Models;
using OptiSYS.Models;

namespace OptiSYS.Services;

/// <summary>Three-state tray indicator color, collapsed from the 5-level OverallHealthState.</summary>
public enum TrayDot { Green, Yellow, Red }

public static class TrayHealthEvaluator
{
    /// <summary>Green = Great/Good, Yellow = Normal, Red = NotGood/Bad (and paused, which evaluates to NotGood).</summary>
    public static TrayDot DotFor(OverallHealthState state) => state switch
    {
        OverallHealthState.Great or OverallHealthState.Good => TrayDot.Green,
        OverallHealthState.Normal => TrayDot.Yellow,
        _ => TrayDot.Red,
    };

    /// <summary>
    /// The integer watts of battery discharge shown as the tray number: <c>round(|rate| / 1000)</c>
    /// on battery, <c>0</c> on AC (the SYSTEM_BATTERY_STATE rate is positive while charging, so it is
    /// gated on the power source to never read a charge rate as load). Clamped to two digits
    /// (>= 100 shows 99) so the number stays legible at icon size.
    /// </summary>
    public static int DischargeWatts(BatteryInfo? battery)
    {
        if (battery?.IsOnBattery != true)
        {
            return 0;
        }

        var watts = (int)Math.Round(Math.Abs(battery.DrainRateMilliwatts) / 1000.0, MidpointRounding.AwayFromZero);
        return Math.Min(watts, 99);
    }

    /// <summary>
    /// The tray's main number: memory usage percent, rounded and clamped to two digits (>= 100 shows
    /// 99) so it stays legible at icon size.
    /// </summary>
    public static int MemoryDisplayNumber(MemoryInfo? memory)
    {
        var percent = (int)Math.Round(memory?.UsagePercent ?? 0, MidpointRounding.AwayFromZero);
        return Math.Clamp(percent, 0, 99);
    }

    /// <summary>Short, human efficiency label for the tooltip, e.g. <c>"Good"</c> / <c>"Poor"</c>.</summary>
    public static string EfficiencyLabel(OverallHealthState state) => state switch
    {
        OverallHealthState.Great => "Great",
        OverallHealthState.Good => "Good",
        OverallHealthState.Normal => "Normal",
        _ => "Poor",
    };

    /// <summary>
    /// Tray tooltip (the number is memory now): battery draw + efficiency, e.g.
    /// <c>"12 W draw • Good"</c> on battery, <c>"On AC • Good"</c> on AC.
    /// </summary>
    public static string BatteryTooltip(BatteryInfo? battery, OverallHealthState state)
    {
        var label = EfficiencyLabel(state);
        return battery?.IsOnBattery == true
            ? $"{DischargeWatts(battery)} W draw • {label}"
            : $"On AC • {label}";
    }

    public static OverallHealthState Evaluate(
        MemoryInfo? memoryInfo,
        BatteryInfo? batteryInfo,
        bool batteryPresetActive,
        bool automationPaused)
    {
        if (automationPaused)
        {
            return OverallHealthState.NotGood;
        }

        var usage = memoryInfo?.UsagePercent ?? 0;
        if (usage >= 92)
        {
            return OverallHealthState.Bad;
        }

        if (batteryInfo?.IsOnBattery == true)
        {
            if (batteryInfo.ChargePercent <= 15 && !batteryPresetActive)
            {
                return OverallHealthState.Bad;
            }

            if (batteryInfo.ChargePercent <= 25 || usage >= 82)
            {
                return OverallHealthState.NotGood;
            }

            if (batteryPresetActive && batteryInfo.ChargePercent >= 65 && usage < 55)
            {
                return OverallHealthState.Great;
            }

            if (batteryPresetActive && usage < 72)
            {
                return OverallHealthState.Good;
            }

            return OverallHealthState.Normal;
        }

        if (usage < 52)
        {
            return OverallHealthState.Great;
        }

        if (usage < 65)
        {
            return OverallHealthState.Good;
        }

        if (usage < 78)
        {
            return OverallHealthState.Normal;
        }

        return OverallHealthState.NotGood;
    }
}
