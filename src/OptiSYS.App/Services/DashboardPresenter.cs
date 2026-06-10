using System.Text;
using OptiSYS.Core.Models;

namespace OptiSYS.Services;

/// <summary>Inputs the dashboard derives its display from — telemetry plus automation state.</summary>
internal sealed record DashboardState
{
    public MemoryInfo? Memory { get; init; }
    public BatteryInfo? Battery { get; init; }
    public bool AutomationPaused { get; init; }
    public bool MinimizeToTray { get; init; }
    public bool AutoOptimizeMemory { get; init; }
    public bool AutoOptimizeOnBattery { get; init; }
    public string LastActivity { get; init; } = string.Empty;
    public DateTimeOffset? LastActivityAt { get; init; }
    public long TotalFreedBytes { get; init; }
    public DateTime Now { get; init; }
}

/// <summary>
/// Memory card values. <see cref="ClearedText"/> and <see cref="HistorySample"/> are null while
/// telemetry is warming up: the freed counter keeps its last value and no placeholder is charted.
/// </summary>
internal sealed record MemoryCardView(
    string PercentText,
    string DetailText,
    double ProgressValue,
    string CachedText,
    string ProcessesText,
    string? ClearedText,
    double? HistorySample);

/// <summary>
/// Battery card values. <see cref="PowerGlyph"/> is null while telemetry is warming up so the
/// shell keeps whichever power icon it last showed instead of swapping it blindly.
/// </summary>
internal sealed record BatteryCardView(
    string PercentText,
    string SourceText,
    double ProgressValue,
    string? PowerGlyph,
    string DrainText,
    string RemainingText);

internal sealed record DashboardView(
    string StatusText,
    string FooterText,
    bool ShowPausedIndicators,
    string PauseGlyph,
    string PauseLabel,
    bool AutomationOn,
    MemoryCardView Memory,
    BatteryCardView Battery);

/// <summary>
/// The pure half of the dashboard refresh: turns <see cref="DashboardState"/> into the exact
/// strings and values the shell binds to its controls. No XAML types, fully unit-testable —
/// MainWindow keeps only the control assignments (see <c>ApplyDashboardView</c>).
/// </summary>
internal static class DashboardPresenter
{
    private const string PlayGlyph = "";        // shown while paused: the resume affordance
    private const string PauseGlyph = "";       // shown while running
    private const string BatteryGlyph = "";
    private const string PluggedInGlyph = "";

    public static DashboardView Present(DashboardState state)
    {
        var paused = state.AutomationPaused;
        var pauseLabel = paused ? "Resume optimization" : "Pause optimization";

        return new DashboardView(
            StatusText: BuildStatusText(state),
            FooterText: $"last sample {state.Now:HH:mm:ss} // safe runtime optimization only",
            ShowPausedIndicators: paused,
            PauseGlyph: paused ? PlayGlyph : PauseGlyph,
            PauseLabel: pauseLabel,
            AutomationOn: !paused,
            Memory: PresentMemory(state.Memory, state.TotalFreedBytes),
            Battery: PresentBattery(state.Battery));
    }

    private static MemoryCardView PresentMemory(MemoryInfo? memory, long totalFreedBytes)
    {
        if (memory is null || memory.TotalPhysicalBytes <= 0)
        {
            return new MemoryCardView(
                PercentText: "--%",
                DetailText: "Warming up memory telemetry...",
                ProgressValue: 0,
                CachedText: "-- GB",
                ProcessesText: "--",
                ClearedText: null,
                HistorySample: null);
        }

        return new MemoryCardView(
            PercentText: $"{memory.UsagePercent:0}%",
            DetailText: $"{memory.UsedGB:F1} GB used of {memory.TotalGB:F1} GB",
            ProgressValue: memory.UsagePercent,
            CachedText: $"{memory.StandbyGB:F1} GB",
            ProcessesText: $"{memory.ProcessCount:N0}",
            ClearedText: OptimizationResult.FormatBytesStatic(totalFreedBytes),
            HistorySample: memory.UsagePercent);
    }

    private static BatteryCardView PresentBattery(BatteryInfo? battery)
    {
        if (battery is null)
        {
            return new BatteryCardView(
                PercentText: "--%",
                SourceText: "Warming up battery telemetry...",
                ProgressValue: 0,
                PowerGlyph: null,
                DrainText: "--",
                RemainingText: "--");
        }

        if (battery.IsOnBattery)
        {
            return new BatteryCardView(
                PercentText: $"{battery.ChargePercent}%",
                SourceText: "Running on battery power",
                ProgressValue: battery.ChargePercent,
                PowerGlyph: BatteryGlyph,
                DrainText: battery.DrainRateDisplay,
                RemainingText: battery.TimeRemainingDisplay);
        }

        return new BatteryCardView(
            PercentText: battery.HasBattery ? $"{battery.ChargePercent}%" : "AC",
            SourceText: "Connected to power",
            ProgressValue: battery.HasBattery ? battery.ChargePercent : 100,
            PowerGlyph: PluggedInGlyph,
            DrainText: "N/A (charging)",
            RemainingText: "N/A (plugged in)");
    }

    private static string BuildStatusText(DashboardState state)
    {
        var text = new StringBuilder();
        text.AppendLine($"time              {state.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine(state.AutomationPaused ? "mode              paused" : "mode              safe optimization");
        text.AppendLine("policy            memory trim + runtime throttles; no service, registry, device, or power-plan edits");
        text.AppendLine($"background        {(state.MinimizeToTray ? "tray enabled" : "window only")}");
        text.AppendLine($"memory_auto       {FormatBool(state.AutoOptimizeMemory)}");
        text.AppendLine($"battery_auto      {FormatBool(state.AutoOptimizeOnBattery)}");
        text.AppendLine();
        AppendMemoryText(text, state.Memory);
        text.AppendLine();
        AppendBatteryText(text, state.Battery);
        text.AppendLine();
        text.AppendLine($"activity          {state.LastActivity}");
        if (state.LastActivityAt is { } at)
        {
            text.AppendLine($"activity_time     {at:yyyy-MM-dd HH:mm:ss zzz}");
        }

        return text.ToString();
    }

    private static void AppendMemoryText(StringBuilder text, MemoryInfo? memory)
    {
        text.AppendLine("[memory]");
        if (memory is null || memory.TotalPhysicalBytes <= 0)
        {
            text.AppendLine("state             warming up");
            return;
        }

        text.AppendLine($"usage             {memory.UsagePercent:0}%");
        text.AppendLine($"installed         {memory.TotalDisplay}");
        text.AppendLine($"used              {memory.UsedDisplay}");
        text.AppendLine($"available         {memory.AvailableDisplay}");
        text.AppendLine($"standby_cache     {memory.StandbyGB:0.0} GB");
        text.AppendLine($"processes         {memory.ProcessCount:N0}");
    }

    private static void AppendBatteryText(StringBuilder text, BatteryInfo? battery)
    {
        text.AppendLine("[power]");
        if (battery is null)
        {
            text.AppendLine("state             warming up");
            return;
        }

        text.AppendLine($"source            {FormatPowerSource(battery.PowerSource)}");
        text.AppendLine($"charge            {(battery.HasBattery ? $"{battery.ChargePercent}%" : "AC")}");
        text.AppendLine($"remaining         {battery.TimeRemainingDisplay}");
        text.AppendLine($"drain             {battery.DrainRateDisplay}");
    }

    private static string FormatPowerSource(PowerSource source) => source switch
    {
        PowerSource.Ac => "plugged in",
        PowerSource.Battery => "on battery",
        _ => "unknown",
    };

    private static string FormatBool(bool value) => value ? "on" : "off";
}
