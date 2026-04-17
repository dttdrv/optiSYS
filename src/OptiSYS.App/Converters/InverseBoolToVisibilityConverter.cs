using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace OptiSYS.Converters;

/// <summary>Logical inverse of <see cref="BoolToVisibilityConverter"/>.</summary>
public sealed partial class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, string language)
        => value is Visibility v && v == Visibility.Collapsed;
}
