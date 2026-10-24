using System.Windows;
using System.Windows.Controls;
using Rigsight.Core;

namespace Rigsight.Controls;

/// <summary>A drawing of a feature's page beside its What's new words (see the XAML); "storage" is the only one so far.</summary>
public partial class FeaturePicture : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(FeaturePicture),
        new PropertyMetadata(null, (d, _) => ((FeaturePicture)d).Show()));

    public string? Kind
    {
        get => (string?)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>A bar in the drawing: a drive's letter and how full, or a folder's name and its share (as star widths).</summary>
    public sealed record Bar(string Letter, int Percent)
    {
        public GridLength Used => new(Math.Max(Percent, 1), GridUnitType.Star);
        public GridLength Free => new(Math.Max(100 - Percent, 0.01), GridUnitType.Star);
    }

    // Example drives and folders, the same on every PC: a drawing of the page, not anyone's own.
    public IReadOnlyList<Bar> Drives { get; } = [new("E", 91), new("C", 73), new("D", 64), new("F", 51)];
    public IReadOnlyList<Bar> Folders { get; } = [new("SteamLibrary", 100), new("Videos", 58), new("Downloads", 41), new("Program Files", 30), new("Users", 22), new("Windows", 14)];

    public FeaturePicture()
    {
        InitializeComponent();
        Show();
    }

    private void Show()
    {
        bool storage = Kind == "storage";
        Storage.Visibility = storage ? Visibility.Visible : Visibility.Collapsed;
        Caption.Text = storage ? Lang.T("The new Storage page") : "";
        Caption.Visibility = storage ? Visibility.Visible : Visibility.Collapsed;
    }
}
