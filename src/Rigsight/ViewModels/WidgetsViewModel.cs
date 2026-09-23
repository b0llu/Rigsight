using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>Settings card for one widget style.</summary>
public sealed partial class WidgetCard(WidgetStyle style, SettingsModel settings) : ObservableObject
{
    public WidgetStyle Style { get; } = style;

    private WidgetConfig Config => settings.Current.Widgets.First(w => w.Style == Style);

    private void Change(Action<WidgetConfig> change, string property)
    {
        settings.Update(s => change(s.Widgets.First(w => w.Style == Style)));
        OnPropertyChanged(property);
    }

    public string Title => Style switch
    {
        WidgetStyle.Compact => "Compact",
        WidgetStyle.Pill => "Slim bar",
        WidgetStyle.Gauges => "Gauges",
        WidgetStyle.NowPlaying => "Now playing",
        WidgetStyle.Today => "Today",
        _ => "Temperature graph",
    };

    public string Description => Style switch
    {
        WidgetStyle.Compact => "CPU and GPU temperature with load, power and a mini chart.",
        WidgetStyle.Pill => "One slim line — CPU, GPU and RAM. Great along the top of the screen.",
        WidgetStyle.Gauges => "Two ring gauges for CPU and GPU temperature.",
        WidgetStyle.NowPlaying => "The app or game you're using, for how long, and its peak temperatures.",
        WidgetStyle.Today => "Screen time so far today, your most-used app and today's peaks.",
        _ => "The last five minutes of CPU and GPU temperature.",
    };

    public bool Enabled { get => Config.Enabled; set => Change(c => c.Enabled = value, nameof(Enabled)); }
    public string Theme { get => Config.Theme.ToString(); set => Change(c => c.Theme = Enum.Parse<WidgetTheme>(value), nameof(Theme)); }
    public string Visibility { get => Config.Visibility.ToString(); set => Change(c => c.Visibility = Enum.Parse<WidgetVisibility>(value), nameof(Visibility)); }
    public bool Locked { get => Config.Locked; set => Change(c => c.Locked = value, nameof(Locked)); }
    public double OpacityPercent { get => Math.Round(Config.Opacity * 100); set => Change(c => c.Opacity = value / 100, nameof(OpacityPercent)); }
    public string Scale { get => Config.Scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); set => Change(c => c.Scale = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture), nameof(Scale)); }

    [ObservableProperty] private ImageSource? _preview;

    public void LoadPreview() => Preview = PreviewImages.Load(Style.ToString()) ?? Preview;

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>Pictures of widgets and the overlay, drawn by the agent into the data folder.</summary>
public static class PreviewImages
{
    public static ImageSource? Load(string name)
    {
        var file = Path.Combine(RigsightPaths.DataDir, "previews", $"{name}.png");
        if (!File.Exists(file)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // Being rewritten by the agent right now; the next refresh will pick it up.
            return null;
        }
    }
}

public sealed class WidgetsViewModel
{
    private readonly AgentClient _client;

    public WidgetsViewModel(SettingsModel settings, AgentClient client)
    {
        _client = client;
        foreach (var style in Enum.GetValues<WidgetStyle>())
            Cards.Add(new WidgetCard(style, settings));
    }

    public ObservableCollection<WidgetCard> Cards { get; } = [];

    /// <summary>Asks the agent to render fresh preview images of every widget.</summary>
    public void RequestPreviews() => _client.SendCommand("render-previews");

    public void OnPreviewsReady()
    {
        foreach (var c in Cards) c.LoadPreview();
    }

    public void Refresh()
    {
        foreach (var c in Cards) c.Refresh();
    }
}
