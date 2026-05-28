namespace OptiSYS.Models;

public enum OverallHealthState
{
    Bad = 1,
    NotGood = 2,
    Normal = 3,
    Good = 4,
    Great = 5,
}

public sealed class TraySnapshot
{
    public OverallHealthState HealthState { get; init; } = OverallHealthState.Normal;
    public string Tooltip { get; init; } = "optiSYS";
    public string BatteryPresetLabel { get; init; } = "Recommended";
    public bool AutomationPaused { get; init; }
}
