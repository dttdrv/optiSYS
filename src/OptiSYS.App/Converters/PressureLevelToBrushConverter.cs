using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using OptiSYS.Core.Models;
using Windows.UI;

namespace OptiSYS.Converters;

/// <summary>
/// Maps a <see cref="PressureLevel"/> to a <see cref="SolidColorBrush"/> for status indicators
/// (progress-bar fill, badge backgrounds, etc). Colors follow a green→amber→orange→red gradient
/// drawn from the Windows 11 Fluent 2 system accent palette.
///
/// <see cref="GetColorArgb"/> exists as a pure function so the mapping logic can be unit-tested
/// without needing a running WinUI dispatcher (a live <see cref="SolidColorBrush"/> can't be
/// constructed off-thread).
/// </summary>
public sealed partial class PressureLevelToBrushConverter : IValueConverter
{
    /// <summary>Pure color mapping — safe to call from any thread.</summary>
    public static (byte A, byte R, byte G, byte B) GetColorArgb(PressureLevel level) => level switch
    {
        PressureLevel.Normal   => (0xFF, 0x10, 0x7C, 0x10), // #107C10 green
        PressureLevel.Elevated => (0xFF, 0xE8, 0xA3, 0x00), // #E8A300 amber
        PressureLevel.High     => (0xFF, 0xF7, 0x63, 0x0C), // #F7630C orange
        PressureLevel.Critical => (0xFF, 0xC4, 0x2B, 0x1C), // #C42B1C red
        _                      => (0xFF, 0x80, 0x80, 0x80), // #808080 gray fallback
    };

    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        var level = value is PressureLevel l ? l : PressureLevel.Normal;
        var (a, r, g, b) = GetColorArgb(level);
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, string language)
        => throw new NotSupportedException();
}
