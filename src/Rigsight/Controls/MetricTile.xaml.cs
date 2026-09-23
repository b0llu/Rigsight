using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rigsight.Models;

namespace Rigsight.Controls;

/// <summary>Small card showing one sensor: label, current value, min/max and a sparkline.</summary>
public partial class MetricTile : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(MetricTile), new PropertyMetadata(""));

    public static readonly DependencyProperty SensorProperty = DependencyProperty.Register(
        nameof(Sensor), typeof(SensorItem), typeof(MetricTile), new PropertyMetadata(null));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(MetricTile), new PropertyMetadata(Brushes.DeepSkyBlue));

    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush), typeof(Brush), typeof(MetricTile), new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF4))));

    public static readonly DependencyProperty SparkMinimumProperty = DependencyProperty.Register(
        nameof(SparkMinimum), typeof(double?), typeof(MetricTile), new PropertyMetadata(null));

    public static readonly DependencyProperty SparkMaximumProperty = DependencyProperty.Register(
        nameof(SparkMaximum), typeof(double?), typeof(MetricTile), new PropertyMetadata(null));

    public MetricTile() => InitializeComponent();

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public SensorItem? Sensor { get => (SensorItem?)GetValue(SensorProperty); set => SetValue(SensorProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush ValueBrush { get => (Brush)GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    public double? SparkMinimum { get => (double?)GetValue(SparkMinimumProperty); set => SetValue(SparkMinimumProperty, value); }
    public double? SparkMaximum { get => (double?)GetValue(SparkMaximumProperty); set => SetValue(SparkMaximumProperty, value); }
}
