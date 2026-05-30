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

    /// <summary>Tooltip efficiency word matching the dot color.</summary>
    public static string EfficiencyLabel(OverallHealthState state) => DotFor(state) switch
    {
        TrayDot.Green => "Good",
        TrayDot.Yellow => "Normal",
        _ => "Bad",
    };

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
