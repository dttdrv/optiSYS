using System.Diagnostics;

namespace OptiSYS.Lab.Probes;

/// <summary>
/// Surfaces the current WLAN link(s) as Windows reports them, via <c>netsh wlan show interfaces</c>.
/// Read-only. The parsing is delegated to the unit-tested <see cref="WlanInterfaceParser"/>; this
/// class only handles invoking netsh and shaping the result.
/// </summary>
public sealed class WlanProbe : ILabProbe
{
    public string Name => "wlan";
    public string Description => "Current Wi-Fi link(s): SSID, band, channel, signal, RSSI, rates";
    public bool RequiresElevation => false;

    public ProbeResult Run()
    {
        string output;
        try
        {
            output = RunNetsh();
        }
        catch (Exception ex)
        {
            return ProbeResult.Skipped(Name, $"could not run netsh: {ex.Message}");
        }

        var interfaces = WlanInterfaceParser.Parse(output);
        if (interfaces.Count == 0)
            return ProbeResult.Skipped(Name, "no WLAN interfaces reported");

        var result = new ProbeResult { ProbeName = Name };
        foreach (var iface in interfaces)
        {
            var section = new ProbeSection { Title = $"{iface.Name} ({iface.State})" };
            if (iface.Ssid.Length > 0) section.Add("SSID", iface.Ssid);
            if (iface.Band.Length > 0) section.Add("Band", iface.Band);
            if (iface.Channel.Length > 0) section.Add("Channel", iface.Channel);
            if (iface.RadioType.Length > 0) section.Add("Radio type", iface.RadioType);
            if (iface.SignalPercent.Length > 0) section.Add("Signal", iface.SignalPercent);
            if (iface.Rssi.Length > 0) section.Add("RSSI", $"{iface.Rssi} dBm");
            if (iface.ReceiveRateMbps.Length > 0) section.Add("Rx rate", $"{iface.ReceiveRateMbps} Mbps");
            if (iface.TransmitRateMbps.Length > 0) section.Add("Tx rate", $"{iface.TransmitRateMbps} Mbps");
            result.Sections.Add(section);
        }
        return result;
    }

    private static string RunNetsh()
    {
        var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("netsh did not start");
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10_000);
        return output;
    }
}
