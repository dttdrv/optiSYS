using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace OptiSYS.Controls;

/// <summary>
/// A self-contained memory-usage sparkline. Owns a rolling sample buffer and redraws a
/// gradient-filled line plot that scales with its canvas size. Callers push values via
/// <see cref="AddSample"/>; all geometry stays inside this control.
/// </summary>
public sealed partial class HistoryChartControl : UserControl
{
    private const int MaxSamples = 50;
    private readonly List<double> _samples = new();

    public HistoryChartControl()
    {
        InitializeComponent();
    }

    /// <summary>Append a value (0-100), trim to the most recent <see cref="MaxSamples"/>, and redraw.</summary>
    public void AddSample(double value)
    {
        _samples.Add(value);
        if (_samples.Count > MaxSamples)
        {
            _samples.RemoveAt(0);
        }

        Redraw();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        if (ChartCanvas is null)
        {
            return;
        }

        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_samples.Count == 0)
        {
            return;
        }

        var line = new PointCollection();
        var area = new PointCollection();

        // Start the area polygon at the bottom-left corner.
        area.Add(new Point(0, height));

        var divisor = Math.Max(1, _samples.Count - 1);
        for (int i = 0; i < _samples.Count; i++)
        {
            double x = i * (width / divisor);
            double y = height - (_samples[i] / 100.0 * height);
            var p = new Point(x, y);
            line.Add(p);
            area.Add(p);
        }

        // Close the area polygon at the bottom-right corner.
        double lastX = (_samples.Count - 1) * (width / divisor);
        area.Add(new Point(lastX, height));

        LinePolyline.Points = line;
        AreaPolygon.Points = area;
    }
}
