using Microsoft.UI.Xaml.Data;
using System.Globalization;

namespace OptiSYS.Converters;

/// <summary>
/// Formats a byte count as a human-readable binary-unit string. 1024 → "1.0 KB", 1 GiB → "1.0 GB", etc.
/// Uses binary prefixes (powers of 1024) to match how Windows Task Manager & most dev-tooling
/// surfaces memory figures, even though SI naming ("KB", "MB") is technically ambiguous.
///
/// Accepts <see cref="long"/>, <see cref="double"/>, or anything <see cref="IConvertible"/>
/// — real bindings tend to produce one of those depending on the property type.
/// </summary>
public sealed partial class BytesToHumanReadableConverter : IValueConverter
{
    private const long KB = 1L << 10;
    private const long MB = 1L << 20;
    private const long GB = 1L << 30;
    private const long TB = 1L << 40;

    public object Convert(object? value, Type targetType, object? parameter, string language)
        => value is null ? "" : Format(ToDouble(value));

    public object ConvertBack(object? value, Type targetType, object? parameter, string language)
        => throw new NotSupportedException();

    private static double ToDouble(object value) => value switch
    {
        long l         => l,
        int i          => i,
        double d       => d,
        float f        => f,
        IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
        _              => 0.0,
    };

    private static string Format(double bytes) => bytes switch
    {
        >= TB => $"{bytes / TB:F1} TB",
        >= GB => $"{bytes / GB:F1} GB",
        >= MB => $"{bytes / MB:F1} MB",
        >= KB => $"{bytes / KB:F1} KB",
        _     => $"{bytes:F0} B",
    };
}
