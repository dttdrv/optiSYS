namespace OptiSYS.Lab;

/// <summary>
/// One diagnostic/measurement unit in the workbench. Adding a feature to prototype =
/// adding one probe. Probes are pure readers: they observe system state and return a
/// <see cref="ProbeResult"/>; they never mutate anything.
/// </summary>
public interface ILabProbe
{
    /// <summary>Short stable id used on the command line (e.g. "system", "wlan").</summary>
    string Name { get; }

    /// <summary>One-line description shown in the probe list.</summary>
    string Description { get; }

    /// <summary>True if the probe needs an elevated process to return complete data.</summary>
    bool RequiresElevation { get; }

    /// <summary>Run the probe and return its findings as ordered key/value sections.</summary>
    ProbeResult Run();
}
