using System.Security.Principal;
using OptiSYS.Lab;
using OptiSYS.Lab.Probes;

// OptiSYS.Lab — dev-only workbench. Usage:
//   OptiSYS.Lab                 list available probes
//   OptiSYS.Lab all [--json]    run every probe
//   OptiSYS.Lab <name> [--json] run one probe (e.g. system, wlan)

bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
bool load = args.Any(a => a.Equals("--load", StringComparison.OrdinalIgnoreCase));

var probes = new List<ILabProbe>
{
    new SystemSnapshotProbe(),
    new WlanProbe(),
    new CpuPerfProbe(load),
};

var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
string target = positional.Length > 0 ? positional[0] : "";

if (target.Length == 0)
{
    PrintProbeList(probes);
    return 0;
}

List<ILabProbe> toRun;
if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
{
    toRun = probes;
}
else
{
    var match = probes.FirstOrDefault(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
    if (match is null)
    {
        Console.Error.WriteLine($"Unknown probe '{target}'.");
        PrintProbeList(probes);
        return 1;
    }
    toRun = [match];
}

bool elevated = IsElevated();
if (!json)
{
    Console.WriteLine($"OptiSYS.Lab — elevated: {(elevated ? "yes" : "no")}");
    Console.WriteLine();
}

var results = new List<ProbeResult>();
foreach (var probe in toRun)
{
    if (probe.RequiresElevation && !elevated)
    {
        results.Add(ProbeResult.Skipped(probe.Name, "needs an elevated terminal — re-run as admin"));
        continue;
    }

    try
    {
        results.Add(probe.Run());
    }
    catch (Exception ex)
    {
        results.Add(ProbeResult.Skipped(probe.Name, $"probe threw: {ex.Message}"));
    }
}

Console.WriteLine(json ? OutputFormatter.ToJson(results) : OutputFormatter.ToText(results));
return 0;

static void PrintProbeList(IReadOnlyList<ILabProbe> probes)
{
    Console.WriteLine("OptiSYS.Lab — available probes:");
    foreach (var p in probes)
    {
        var adminTag = p.RequiresElevation ? " [admin]" : "";
        Console.WriteLine($"  {p.Name,-10}{adminTag}  {p.Description}");
    }
    Console.WriteLine();
    Console.WriteLine("Run:  OptiSYS.Lab <name|all> [--json]");
}

static bool IsElevated()
{
    try
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    catch
    {
        return false;
    }
}
