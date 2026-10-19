using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rigsight.Core;
using Rigsight.Core.Games;
using Rigsight.Core.Rewind;
using Rigsight.Services;

namespace Rigsight.Rewind;

/// <summary>How the pictures look: a theme's background and text, and an accent that recolours the glows (or none).</summary>
public sealed record RewindLook(string Theme, string Accent)
{
    public sealed record ThemeColors(string Key, string Name, Color Bg, Color Surface, Color Text);

    public static readonly IReadOnlyList<ThemeColors> Themes =
    [
        new(ThemeManager.Dark, Lang.Key("Dark"), C("#000000"), C("#0A0A0A"), Colors.White),
        new(ThemeManager.Grey, Lang.Key("Grey"), C("#1E1E1E"), C("#262626"), Colors.White),
        new(ThemeManager.Midnight, Lang.Key("Midnight"), C("#0A0E1C"), C("#19213D"), C("#EEF2FF")),
        new(ThemeManager.Forest, Lang.Key("Forest"), C("#08120D"), C("#16291F"), C("#EFFAF4")),
        new(ThemeManager.Ember, Lang.Key("Ember"), C("#140E0A"), C("#2A1E15"), C("#FFF6EE")),
        new(ThemeManager.Light, Lang.Key("Light"), C("#FFFFFF"), C("#F3F3F3"), Colors.Black),
    ];

    /// <summary>The app's own look: its theme ("Same as Windows" read as what Windows uses) and accent.</summary>
    public static RewindLook App(Core.Settings.RigsightSettings s)
    {
        string theme = s.Theme == ThemeManager.System ? (ThemeManager.WindowsUsesLight() ? ThemeManager.Light : ThemeManager.Dark) : s.Theme;
        return new(Themes.Any(t => t.Key == theme) ? theme : ThemeManager.Dark, Accents.Normalize(s.Accent));
    }

    public ThemeColors Colors_ => Themes.FirstOrDefault(t => t.Key == Theme) ?? Themes[0];
    public bool IsLight => Theme == ThemeManager.Light;
    public Color? AccentColor => Accents.ColorFor(Accent, IsLight);

    internal static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

/// <summary>
/// Draws a rewind card at any size: every length is a share of the picture's shorter side, so the same card is the
/// same in the app's viewer, in Share's preview and in a 1080 × 1920 picture. "wide" puts the sentence beside its
/// rows; "story" and "post" stack them.
/// </summary>
public static class RewindPainter
{
    public static readonly IReadOnlyDictionary<string, (int W, int H)> Sizes = new Dictionary<string, (int, int)>
    {
        ["story"] = (1080, 1920), ["post"] = (1080, 1350), ["wide"] = (1600, 900),
    };

    private static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");
    private static ImageSource? _logo;

    private static ImageSource? Logo => _logo ??= Load("pack://application:,,,/Assets/Rigsight.png");

    private static ImageSource? Load(string uri)
    {
        try { var b = new BitmapImage(new Uri(uri)); b.Freeze(); return b; }
        catch (Exception) { return null; }
    }

    /// <summary>The card drawn at <paramref name="w"/> × <paramref name="h"/>.</summary>
    public static FrameworkElement Card(RewindCard card, RewindFacts facts, int index, int count, string shape, RewindLook look, double w, double h)
    {
        double u = Math.Min(w, h) / 100;
        bool wide = shape == "wide";
        var theme = look.Colors_;
        var text = theme.Text;
        Color hue1 = look.AccentColor ?? RewindLook.C(card.Id == "cover" ? RewindCards.KindHue(facts.Kind) : card.Hue1);
        Color hue2 = look.AccentColor ?? RewindLook.C(card.Hue2);
        Brush Text(double alpha) => Frozen(new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), text.R, text.G, text.B)));

        var root = new Grid { Width = w, Height = h, ClipToBounds = true, Background = Frozen(new SolidColorBrush(theme.Bg)) };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.HighQuality);
        root.Children.Add(new Border { Background = Glow(hue1, 0.42, new Point(0.85, 0), 1.2, 0.7, 0.6) });
        root.Children.Add(new Border { Background = Glow(hue2, 0.32, new Point(0, 1), 0.9, 0.6, 0.65) });
        root.Children.Add(GridLines(text, u, w, h));

        var frame = new Grid { Margin = wide ? new Thickness(7 * u, 6.5 * u, 7 * u, 6.5 * u) : new Thickness(7.5 * u) };
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame.RowDefinitions.Add(new RowDefinition());
        frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(frame);

        // Top: the logo and name, the period on the right.
        var top = new DockPanel();
        top.Children.Add(new TextBlock { Text = RewindCards.TitleWithYear(facts), FontFamily = Ui, FontSize = 3.3 * u, Foreground = Text(0.6), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(top.Children[0], Dock.Right);
        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        if (Logo is { } logo) brand.Children.Add(new Image { Source = logo, Width = 5.6 * u, Height = 5.6 * u, Margin = new Thickness(0, 0, 2.2 * u, 0) });
        brand.Children.Add(new TextBlock { Text = "RigSight Rewind", FontFamily = Ui, FontSize = 3.3 * u, FontWeight = FontWeights.SemiBold, Foreground = Text(0.78), VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(brand);
        frame.Children.Add(top);

        // Foot: the site, and which card of how many.
        var foot = new DockPanel();
        var at = new TextBlock { Text = $"{index + 1} / {count}", FontFamily = Ui, FontSize = 3.1 * u, Foreground = Text(0.55) };
        DockPanel.SetDock(at, Dock.Right);
        foot.Children.Add(at);
        foot.Children.Add(new TextBlock { Text = "rigsight.app", FontFamily = Ui, FontSize = 3.1 * u, Foreground = Text(0.55) });
        Grid.SetRow(foot, 2);
        frame.Children.Add(foot);

        // The sentence: label, figure, the line under them.
        var a = new StackPanel();
        if (card.Id == "summary")
            a.Children.Add(Label(card.Label, hue1, u));
        else
        {
            a.Children.Add(Label(card.Label, hue1, u));
            double big = card.Id == "cover" ? (wide ? 17 : 21) : card.Wordy ? 10.5 : wide ? 13 : shape == "post" ? 12.5 : 14;
            a.Children.Add(new TextBlock
            {
                Text = card.Big, FontFamily = Display, FontSize = big * u, FontWeight = FontWeights.Bold, Foreground = Text(1),
                TextWrapping = TextWrapping.Wrap, LineHeight = big * u * 1.0, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(0, 2.4 * u, 0, 0),
            });
            if (card.Sub is { } sub)
                a.Children.Add(new TextBlock { Text = sub, FontFamily = Ui, FontSize = 4.3 * u, Foreground = Text(0.8), TextWrapping = TextWrapping.Wrap, LineHeight = 4.3 * u * 1.38, Margin = new Thickness(0, 2.4 * u, 0, 0) });
        }

        var b = Rows(card, text, hue1, u, wide, Text);
        var body = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4 * u, 0, 4 * u) };
        if (b is null) body.Children.Add(a);
        else if (wide)
        {
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7 * u) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            a.VerticalAlignment = b.VerticalAlignment = VerticalAlignment.Center;
            body.Children.Add(a);
            Grid.SetColumn(b, 2);
            body.Children.Add(b);
        }
        else
        {
            var stack = new StackPanel();
            stack.Children.Add(a);
            b.Margin = new Thickness(0, 5 * u, 0, 0);
            stack.Children.Add(b);
            body.Children.Add(stack);
        }
        Grid.SetRow(body, 1);
        frame.Children.Add(body);
        return root;
    }

    private static TextBlock Label(string text, Color hue, double u) => new()
    {
        Text = text.ToUpper(CultureInfo.CurrentCulture), FontFamily = Ui, FontSize = 3.6 * u, FontWeight = FontWeights.Bold,
        Foreground = Frozen(new SolidColorBrush(hue)), TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>The card's second half: ranked games with covers, days, readings, or the summary's tiles. Null when it has none.</summary>
    private static FrameworkElement? Rows(RewindCard card, Color text, Color hue, double u, bool wide, Func<double, Brush> Text)
    {
        double fs = (wide ? 4.2 : 4) * u;
        if (card.Tiles.Count > 0)
        {
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-1.6 * u) };
            foreach (var (value, label) in card.Tiles)
            {
                var tile = new Border
                {
                    Background = Text(0.06), BorderBrush = Text(0.08), BorderThickness = new Thickness(Math.Max(1, 0.2 * u)), CornerRadius = new CornerRadius(2.4 * u),
                    Padding = new Thickness(3.4 * u), Margin = new Thickness(1.6 * u),
                };
                var s = new StackPanel();
                s.Children.Add(new TextBlock { Text = value, FontFamily = Display, FontSize = 7.2 * u, FontWeight = FontWeights.Bold, Foreground = Text(1), TextTrimming = TextTrimming.CharacterEllipsis });
                s.Children.Add(new TextBlock { Text = label, FontFamily = Ui, FontSize = 3.2 * u, Foreground = Text(0.7), TextWrapping = TextWrapping.Wrap });
                tile.Child = s;
                grid.Children.Add(tile);
            }
            return grid;
        }
        if (card.Heat.Count == 0 && card.Rows.Count == 0) return null;
        var list = new StackPanel();
        foreach (var (deg, part, when, h) in card.Heat)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2.6 * u) };
            var d = new TextBlock { Text = deg, FontFamily = Display, FontSize = 9 * u, FontWeight = FontWeights.Bold, Width = 19 * u, VerticalAlignment = VerticalAlignment.Center, Foreground = Frozen(new SolidColorBrush(RewindLook.C(h))) };
            row.Children.Add(d);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3.2 * u, 0, 0, 0) };
            words.Children.Add(new TextBlock { Text = part, FontFamily = Ui, FontSize = 4.2 * u, FontWeight = FontWeights.SemiBold, Foreground = Text(1) });
            words.Children.Add(new TextBlock { Text = when, FontFamily = Ui, FontSize = 3.9 * u, Foreground = Text(0.82), TextWrapping = TextWrapping.Wrap });
            row.Children.Add(words);
            list.Children.Add(row);
        }
        foreach (var r in card.Rows)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2.6 * u), LastChildFill = true };
            if (r.Rank > 0)
            {
                var rank = new TextBlock { Text = r.Rank.ToString(CultureInfo.CurrentCulture), FontFamily = Ui, FontSize = fs, Width = 4 * u, Foreground = Text(0.5), VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(rank);
                row.Children.Add(Cover(r, u, Text));
            }
            else if (r.Path is not null && IconOf(r) is { } icon)
                row.Children.Add(new Image { Source = icon, Width = 6 * u, Height = 6 * u, Margin = new Thickness(0, 0, 3 * u, 0), VerticalAlignment = VerticalAlignment.Center });
            if (r.Value.Length > 0)
            {
                var v = new TextBlock { Text = r.Value, FontFamily = Ui, FontSize = fs, FontWeight = FontWeights.SemiBold, Foreground = Text(1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3 * u, 0, 0, 0) };
                DockPanel.SetDock(v, Dock.Right);
                row.Children.Add(v);
            }
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (r.Detail is { } detail)
            {
                // A day or a count named under its label: "Your busiest day" / "Saturday 10 October".
                col.Children.Add(new TextBlock { Text = r.Name, FontFamily = Ui, FontSize = fs * 0.9, Foreground = Text(0.6) });
                col.Children.Add(new TextBlock { Text = detail, FontFamily = Ui, FontSize = fs, FontWeight = FontWeights.SemiBold, Foreground = Text(1), TextWrapping = TextWrapping.Wrap });
            }
            else
                col.Children.Add(new TextBlock { Text = r.Name, FontFamily = Ui, FontSize = fs, Foreground = Text(1), TextTrimming = TextTrimming.CharacterEllipsis });
            if (r.Bar > 0)
                col.Children.Add(new Border
                {
                    Height = 0.9 * u, CornerRadius = new CornerRadius(u), Background = Frozen(new SolidColorBrush(hue)), HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, u, 0, 0), Tag = r.Bar,
                }.Also(bar => col.SizeChanged += (_, e) => bar.Width = Math.Max(u, e.NewSize.Width * r.Bar)));
            row.Children.Add(col);
            list.Children.Add(row);
        }
        return list;
    }

    private static T Also<T>(this T t, Action<T> act) { act(t); return t; }

    /// <summary>A ranked game's cover from its launcher, else its icon on a plain tile.</summary>
    private static FrameworkElement Cover(RewindRow r, double u, Func<double, Brush> Text)
    {
        var box = new Border
        {
            Width = 8 * u, Height = 10.5 * u, CornerRadius = new CornerRadius(1.2 * u), Margin = new Thickness(0, 0, 3 * u, 0), ClipToBounds = true,
            Background = Text(0.08), VerticalAlignment = VerticalAlignment.Center,
        };
        var installed = GameLibrary.Find(Core.Apps.AppCatalog.InstalledGames, r.Path);
        if (GamePictures.Picture(installed?.Art.Cover, 300) is { } cover)
            box.Background = Frozen(new ImageBrush(cover) { Stretch = Stretch.UniformToFill });
        else if (IconOf(r) is { } icon)
            box.Child = new Image { Source = icon, Width = 5 * u, Height = 5 * u };
        return box;
    }

    private static ImageSource? IconOf(RewindRow r) => GamePictures.Icon(r.Path) ?? (r.Path is null ? null : IconCache.Get(r.Path));

    private static Brush Glow(Color c, double alpha, Point center, double rx, double ry, double stop)
    {
        var g = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = rx, RadiusY = ry };
        g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), stop));
        return Frozen(g);
    }

    /// <summary>A faint square grid, strongest near the top right and fading out.</summary>
    private static FrameworkElement GridLines(Color text, double u, double w, double h)
    {
        double cell = 9 * u;
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(12, text.R, text.G, text.B)), Math.Max(1, 0.1 * u));
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(0, 0), new Point(cell, 0)));
        lines.Children.Add(new LineGeometry(new Point(0, 0), new Point(0, cell)));
        var tile = new DrawingBrush(new GeometryDrawing(null, pen, lines)) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, cell, cell), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, cell, cell), ViewboxUnits = BrushMappingMode.Absolute };
        var mask = new RadialGradientBrush { Center = new Point(0.7, 0.2), GradientOrigin = new Point(0.7, 0.2), RadiusX = 0.75 * Math.Max(1, h / w), RadiusY = 0.75 * Math.Max(1, w / h) };
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        return new Border { Background = Frozen(tile), OpacityMask = Frozen(mask) };
    }

    private static T Frozen<T>(T f) where T : Freezable { if (f.CanFreeze) f.Freeze(); return f; }

    /// <summary>The card as a picture of its shape's full size (1080 × 1920 for a story).</summary>
    public static BitmapSource Picture(RewindCard card, RewindFacts facts, int index, int count, string shape, RewindLook look)
    {
        var (w, h) = Sizes[shape];
        var element = Card(card, facts, index, count, shape, look, w, h);
        element.Measure(new Size(w, h));
        element.Arrange(new Rect(0, 0, w, h));
        element.UpdateLayout();
        // Bars take their width from the laid-out rows: lay out once more now that they have it.
        element.Measure(new Size(w, h));
        element.Arrange(new Rect(0, 0, w, h));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    public static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
