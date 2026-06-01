namespace OptiSYS.Lab.Probes;

/// <summary>One connected WLAN interface as parsed from <c>netsh wlan show interfaces</c>.</summary>
public sealed class WlanInterfaceReading
{
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string Ssid { get; init; } = "";
    public string Band { get; init; } = "";
    public string Channel { get; init; } = "";
    public string RadioType { get; init; } = "";
    public string SignalPercent { get; init; } = "";
    public string Rssi { get; init; } = "";
    public string ReceiveRateMbps { get; init; } = "";
    public string TransmitRateMbps { get; init; } = "";
}

/// <summary>
/// Pure parser for <c>netsh wlan show interfaces</c> output. Split out from the probe so it is
/// unit-testable against captured sample text with no machine/Wi-Fi dependency. The values
/// are surfaced as-is (strings) — the workbench's job is to show what Windows reports, not to
/// reinterpret it.
/// </summary>
public static class WlanInterfaceParser
{
    /// <summary>
    /// Parse zero or more interface blocks. netsh prints "    Key  : Value" lines and separates
    /// interfaces with a blank line after the first "Name" of each block. We split on the "Name"
    /// key, which begins every interface block.
    /// </summary>
    public static IReadOnlyList<WlanInterfaceReading> Parse(string netshOutput)
    {
        if (string.IsNullOrWhiteSpace(netshOutput))
            return [];

        var readings = new List<WlanInterfaceReading>();
        Dictionary<string, string>? current = null;

        foreach (var rawLine in netshOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length == 0)
                continue;

            // "Name" starts a new interface block.
            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null)
                    readings.Add(Build(current));
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            if (current is not null)
                current[key] = value;
        }

        if (current is not null)
            readings.Add(Build(current));

        return readings;
    }

    private static WlanInterfaceReading Build(Dictionary<string, string> f) => new()
    {
        Name = Get(f, "Name"),
        State = Get(f, "State"),
        Ssid = Get(f, "SSID"),
        Band = Get(f, "Band"),
        Channel = Get(f, "Channel"),
        RadioType = Get(f, "Radio type"),
        SignalPercent = Get(f, "Signal"),
        Rssi = Get(f, "Rssi"),
        ReceiveRateMbps = Get(f, "Receive rate (Mbps)"),
        TransmitRateMbps = Get(f, "Transmit rate (Mbps)"),
    };

    private static string Get(Dictionary<string, string> f, string key) =>
        f.TryGetValue(key, out var v) ? v : "";
}
