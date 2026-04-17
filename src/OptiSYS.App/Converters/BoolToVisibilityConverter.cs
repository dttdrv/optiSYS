using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace OptiSYS.Converters;

/// <summary>
/// <c>true</c> → <see cref="Visibility.Visible"/>, <c>false</c> → <see cref="Visibility.Collapsed"/>.
/// Non-bool / null inputs collapse defensively rather than throwing, so a binding source that
/// produces a malformed value doesn't crash the UI.
/// </summary>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}
