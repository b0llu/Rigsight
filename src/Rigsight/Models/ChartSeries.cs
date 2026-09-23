using System.Windows.Media;

namespace Rigsight.Models;

/// <summary>One line on a history chart.</summary>
public sealed class ChartSeries
{
    public ChartSeries(string label, SensorItem sensor, Color color)
    {
        Label = label;
        Sensor = sensor;
        Color = color;
        Brush = new SolidColorBrush(color);
        Brush.Freeze();
    }

    public string Label { get; }
    public SensorItem Sensor { get; }
    public Color Color { get; }
    public SolidColorBrush Brush { get; }
}
