using Microsoft.UI.Xaml.Data;
using System.Globalization;

namespace OptiSYS.Converters;

/// <summary>
/// Formats a numeric value as "{n}%". Accepts both fraction form (0.85 → "85%") and
/// already-expressed percent form (85 → "85%"). The heuristic: values ≤ 1.0 are treated
/// as fractions and multiplied by 100; larger values are rounded as-is.
///
/// This dual-mode behavior lets a single resource be bound against both:
/// <list type="bullet">
///   <item><see cref="OptiSYS.Core.Models.MemoryInfo.UsagePercent"/> (already 0–100)</item>
///   <item>raw ratios coming from <c>double</c> properties in the 0.0–1.0 range</item>
/// </list>
/// The narrow boundary at exactly 1.0 resolves to "100%" either way, so no ambiguity.
/// </summary>
public sealed partial class PercentToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        var d = ToDouble(value);
        var percent = d <= 1.0 && d >= 0.0 ? d * 100 : d;
        return $"{Math.Round(percent).ToString("F0", CultureInfo.InvariantCulture)}%";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, string language)
        => throw new NotSupportedException();

    private static double ToDouble(object? value) => value switch
    {
        double d       => d,
        float f        => f,
        int i          => i,
        long l         => l,
        IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
        _              => 0.0,
    };
}
