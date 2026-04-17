using Microsoft.UI.Xaml;
using OptiSYS.Converters;
using OptiSYS.Core.Models;
using Xunit;

namespace OptiSYS.Tests.Converters;

public class BoolToVisibilityConverterTests
{
    private static readonly BoolToVisibilityConverter Sut = new();

    [Theory]
    [InlineData(true,  Visibility.Visible)]
    [InlineData(false, Visibility.Collapsed)]
    public void Convert_MapsBoolToVisibility(bool input, Visibility expected)
    {
        var result = Sut.Convert(input, typeof(Visibility), parameter: null, language: "");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_NonBoolInput_DefaultsToCollapsed()
    {
        // Defensive: if binding source produces a non-bool, we collapse rather than throw.
        var result = Sut.Convert(null, typeof(Visibility), null, "");
        Assert.Equal(Visibility.Collapsed, result);
    }

    [Theory]
    [InlineData(Visibility.Visible,   true)]
    [InlineData(Visibility.Collapsed, false)]
    public void ConvertBack_MapsVisibilityToBool(Visibility input, bool expected)
    {
        var result = Sut.ConvertBack(input, typeof(bool), null, "");
        Assert.Equal(expected, result);
    }
}

public class InverseBoolToVisibilityConverterTests
{
    private static readonly InverseBoolToVisibilityConverter Sut = new();

    [Theory]
    [InlineData(true,  Visibility.Collapsed)]
    [InlineData(false, Visibility.Visible)]
    public void Convert_InvertsBool(bool input, Visibility expected)
    {
        var result = Sut.Convert(input, typeof(Visibility), null, "");
        Assert.Equal(expected, result);
    }
}

public class BytesToHumanReadableConverterTests
{
    private static readonly BytesToHumanReadableConverter Sut = new();

    [Theory]
    [InlineData(0L,                     "0 B")]
    [InlineData(512L,                   "512 B")]
    [InlineData(1024L,                  "1.0 KB")]
    [InlineData(1024L * 1024,           "1.0 MB")]
    [InlineData(1073741824L,            "1.0 GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1.0 TB")]
    public void Convert_FormatsBinaryUnits(long bytes, string expected)
    {
        var result = Sut.Convert(bytes, typeof(string), null, "");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Convert_AcceptsDoubleInput()
    {
        var result = Sut.Convert(1024.0, typeof(string), null, "");
        Assert.Equal("1.0 KB", result);
    }
}

public class PercentToStringConverterTests
{
    private static readonly PercentToStringConverter Sut = new();

    [Theory]
    [InlineData(0.0,   "0%")]
    [InlineData(0.85,  "85%")]   // Treated as a fraction (≤ 1.0) — multiplied by 100.
    [InlineData(1.0,   "100%")]
    [InlineData(85.0,  "85%")]   // Treated as an already-expressed percent.
    [InlineData(100.0, "100%")]
    public void Convert_RoundsAndSuffixes(double input, string expected)
    {
        var result = Sut.Convert(input, typeof(string), null, "");
        Assert.Equal(expected, result);
    }
}

public class PressureLevelToBrushConverterTests
{
    // The converter's Convert() returns a SolidColorBrush, which requires the WinUI
    // dispatcher to construct. We test the pure-function core `GetColorArgb()` instead.
    // Colors chosen to match Windows 11 Fluent 2 system accent palette (green / amber / orange / red).

    [Theory]
    [InlineData(PressureLevel.Normal,   0x10, 0x7C, 0x10)] // green
    [InlineData(PressureLevel.Elevated, 0xE8, 0xA3, 0x00)] // amber
    [InlineData(PressureLevel.High,     0xF7, 0x63, 0x0C)] // orange
    [InlineData(PressureLevel.Critical, 0xC4, 0x2B, 0x1C)] // red
    public void GetColorArgb_MapsEachLevel(PressureLevel level, byte r, byte g, byte b)
    {
        var (a, actualR, actualG, actualB) = PressureLevelToBrushConverter.GetColorArgb(level);

        Assert.Equal(byte.MaxValue, a);
        Assert.Equal(r, actualR);
        Assert.Equal(g, actualG);
        Assert.Equal(b, actualB);
    }

    [Fact]
    public void GetColorArgb_UnknownLevel_FallsBackToGray()
    {
        var (_, r, g, b) = PressureLevelToBrushConverter.GetColorArgb((PressureLevel)99);
        Assert.Equal(0x80, r);
        Assert.Equal(0x80, g);
        Assert.Equal(0x80, b);
    }
}
