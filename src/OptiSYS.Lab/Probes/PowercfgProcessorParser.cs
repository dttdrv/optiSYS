namespace OptiSYS.Lab.Probes;

/// <summary>
/// Pure parser for `powercfg /q SCHEME_CURRENT SUB_PROCESSOR &lt;setting&gt;` output. Extracts the
/// "Current AC/DC Power Setting Index" hex values. Split out so it is unit-testable against
/// captured sample text with no machine dependency.
/// </summary>
public static class PowercfgProcessorParser
{
    /// <summary>Returns (ac, dc) percentage values, or null for either if not found.</summary>
    public static (int? ac, int? dc) ParseAcDcIndex(string powercfgOutput)
    {
        int? ac = null, dc = null;
        if (string.IsNullOrWhiteSpace(powercfgOutput))
            return (ac, dc);

        foreach (var raw in powercfgOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Current AC", StringComparison.OrdinalIgnoreCase))
                ac = ParseIndex(line);
            else if (line.StartsWith("Current DC", StringComparison.OrdinalIgnoreCase))
                dc = ParseIndex(line);
        }
        return (ac, dc);
    }

    private static int? ParseIndex(string line)
    {
        var colon = line.LastIndexOf(':');
        if (colon < 0) return null;
        var token = line[(colon + 1)..].Trim();
        try
        {
            // Values print as hex ("0x00000055") or sometimes plain decimal.
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt32(token, 16);
            return int.TryParse(token, out var v) ? v : null;
        }
        catch
        {
            return null;
        }
    }
}
