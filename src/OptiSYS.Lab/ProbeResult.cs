namespace OptiSYS.Lab;

/// <summary>
/// A probe's findings: named sections, each an ordered list of label/value pairs.
/// Kept deliberately flat and string-valued so both the text and JSON formatters are trivial
/// and a probe author never has to think about presentation.
/// </summary>
public sealed class ProbeResult
{
    public string ProbeName { get; init; } = string.Empty;
    public bool Ok { get; init; } = true;

    /// <summary>Set when the probe could not produce data (e.g. needs admin, no adapter).</summary>
    public string? Note { get; init; }

    public List<ProbeSection> Sections { get; init; } = [];

    public static ProbeResult Skipped(string probeName, string note) =>
        new() { ProbeName = probeName, Ok = false, Note = note };
}

/// <summary>A titled group of label/value rows.</summary>
public sealed class ProbeSection
{
    public string Title { get; init; } = string.Empty;
    public List<KeyValuePair<string, string>> Rows { get; init; } = [];

    public ProbeSection Add(string label, string value)
    {
        Rows.Add(new(label, value));
        return this;
    }
}
