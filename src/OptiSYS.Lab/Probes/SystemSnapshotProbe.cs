using OptiSYS.Core.Models;
using OptiSYS.Core.Services;

namespace OptiSYS.Lab.Probes;

/// <summary>
/// Live battery + memory snapshot, reusing the same Core services the shipping app uses.
/// This is the reference probe: it proves the host → probe → output pipeline end-to-end with
/// no new Core logic.
/// </summary>
public sealed class SystemSnapshotProbe : ILabProbe
{
    public string Name => "system";
    public string Description => "Live battery + memory snapshot (reuses OptiSYS.Core services)";
    public bool RequiresElevation => false;

    public ProbeResult Run()
    {
        var native = NativeBridgeFactory.Create();

        using var battery = new BatteryInfoService(native);
        using var memory = new MemoryInfoService(native);

        var b = battery.CurrentInfoOrRefresh();
        var m = memory.GetCurrentMemoryInfo();

        var result = new ProbeResult { ProbeName = Name };

        var batterySection = new ProbeSection { Title = "Battery" };
        if (b is { HasBattery: true })
        {
            batterySection
                .Add("Power source", b.PowerSource.ToString())
                .Add("Charge", $"{b.ChargePercent}%")
                .Add("Drain rate", b.DrainRateDisplay)
                .Add("Time remaining", b.TimeRemainingDisplay);
        }
        else
        {
            batterySection.Add("Battery", "none detected (desktop or no battery device)");
        }
        result.Sections.Add(batterySection);

        result.Sections.Add(new ProbeSection { Title = "Memory" }
            .Add("Total", m.TotalDisplay)
            .Add("Used", $"{m.UsedDisplay} ({m.UsagePercent:F0}%)")
            .Add("Available", m.AvailableDisplay)
            .Add("Standby cache", $"{m.StandbyGB:F1} GB")
            .Add("Committed", $"{m.CommitPercent:F0}% of limit")
            .Add("Processes", m.ProcessCount.ToString()));

        return result;
    }
}

internal static class BatteryServiceLabExtensions
{
    /// <summary>
    /// The service populates <c>CurrentInfo</c> on its polling timer; in a one-shot console run we
    /// briefly Start it so a single sample is taken, then Stop. Avoids exposing a public one-shot
    /// read on the shipping interface just for the workbench.
    /// </summary>
    public static BatteryInfo? CurrentInfoOrRefresh(this BatteryInfoService service)
    {
        if (service.CurrentInfo is not null)
            return service.CurrentInfo;

        service.Start(intervalSeconds: 1); // Start() takes one sample synchronously before returning
        service.Stop();
        return service.CurrentInfo;
    }
}
