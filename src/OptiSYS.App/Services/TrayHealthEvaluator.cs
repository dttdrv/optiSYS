using OptiSYS.Core.Models;
using OptiSYS.Models;

namespace OptiSYS.Services;

public static class TrayHealthEvaluator
{
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
