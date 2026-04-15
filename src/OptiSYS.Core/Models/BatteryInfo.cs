namespace OptiSYS.Core.Models;

/// <summary>
/// Real-time battery and power state information.
/// </summary>
public sealed class BatteryInfo
{
    public PowerSource PowerSource { get; init; }
    public bool HasBattery { get; init; }
    public byte ChargePercent { get; init; }
    public int DrainRateMilliwatts { get; init; }
    public int EstimatedTimeRemainingSeconds { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public bool IsOnBattery => PowerSource == PowerSource.Battery;
    public bool IsOnAc => PowerSource == PowerSource.Ac;

    public string DrainRateDisplay => DrainRateMilliwatts switch
    {
        0 => "N/A",
        _ => $"{Math.Abs(DrainRateMilliwatts) / 1000.0:F1} W"
    };

    public string TimeRemainingDisplay => EstimatedTimeRemainingSeconds switch
    {
        <= 0 => "N/A",
        >= 3600 => $"{EstimatedTimeRemainingSeconds / 3600}h {EstimatedTimeRemainingSeconds % 3600 / 60}m",
        _ => $"{EstimatedTimeRemainingSeconds / 60}m"
    };
}

public enum PowerSource { Unknown, Ac, Battery }
