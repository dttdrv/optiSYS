using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptiSYS.Lab;

/// <summary>
/// Renders <see cref="ProbeResult"/>s either as human-readable text (the default) or as
/// machine-parseable JSON (--json), so before/after runs can be diffed or scripted.
/// </summary>
public static class OutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ToText(IReadOnlyList<ProbeResult> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.Append("== ").Append(r.ProbeName).AppendLine(" ==");
            if (r.Note is not null)
                sb.Append("  (").Append(r.Note).AppendLine(")");

            foreach (var section in r.Sections)
            {
                sb.Append("  [").Append(section.Title).AppendLine("]");
                int width = section.Rows.Count == 0 ? 0 : section.Rows.Max(row => row.Key.Length);
                foreach (var (label, value) in section.Rows)
                    sb.Append("    ").Append(label.PadRight(width)).Append("  ").AppendLine(value);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<ProbeResult> results) =>
        JsonSerializer.Serialize(results, JsonOptions);
}
