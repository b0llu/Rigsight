using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Rigsight.Core;
using Rigsight.Core.Games;
using Rigsight.Core.Rewind;
using Rigsight.Services;

namespace Rigsight.Rewind;

/// <summary>
/// How the pictures look in Share. The cards carry RigSight's own colours, a world for each card; an accent picked
/// there recolours every card in that one colour instead ("mono" keeps RigSight's).
/// </summary>
public sealed record RewindLook(string Accent)
{
    /// <summary>The app's own accent ("Same as Windows" read as it is now).</summary>
    public static RewindLook App(Core.Settings.RigsightSettings s) => new(Accents.Normalize(s.Accent));

    public Color? AccentColor => Accents.ColorFor(Accent, false);

    internal static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

/// <summary>
/// Draws a rewind card as approved on 2026-10-19 (the redesign mock-up): a tall story card with its own colour world,
/// big figures, and a picture made of the period itself (the games' art, the month as a calendar, the day as a clock).
/// Every length is a share of the card's width, so the card is the same in the viewer, in Share and in a 1080 × 1920
/// picture. Post and Wide pictures hold the same tall card on its own colour.
/// </summary>
public static class RewindPainter
{
    /// <summary>The name of the part of a card under its logo row and above its foot (what has to fit the card).</summary>
    public const string BodyName = "RewindBody";

    public static readonly IReadOnlyDictionary<string, (int W, int H)> Sizes = new Dictionary<string, (int, int)>
    {
        ["story"] = (1080, 1920), ["post"] = (1080, 1350), ["wide"] = (1600, 900),
    };

    private static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static ImageSource? _logo;
    private static ImageSource? Logo => _logo ??= Load("pack://application:,,,/Assets/Rigsight.png");

    private static ImageSource? Load(string uri)
    {
        try { var b = new BitmapImage(new Uri(uri)); b.Freeze(); return b; }
        catch (Exception) { return null; }
    }

    // ── Rising in: which pieces come in, and in what order, when a card is shown in the viewer ──

    public static readonly DependencyProperty RiseProperty = DependencyProperty.RegisterAttached("Rise", typeof(int), typeof(RewindPainter), new PropertyMetadata(-1));
    private static T Rise<T>(T e, int step) where T : UIElement { e.SetValue(RiseProperty, step); return e; }

    /// <summary>The card's pieces rise in one after another (the viewer only; pictures are still).</summary>
    public static void Animate(FrameworkElement card)
    {
        if (!Motion.Enabled) return;
        double u = card.ActualWidth > 0 ? card.ActualWidth / 100 : (card.Width > 0 ? card.Width / 100 : 10);
        // Called before the card is shown (so nothing is drawn in place first and then jumps): the pieces are found in
        // the tree as built, not the visual tree, which doesn't exist yet.
        foreach (var e in Rising(card))
        {
            int step = (int)e.GetValue(RiseProperty);
            var shift = new TranslateTransform(0, 5 * u);
            e.RenderTransform = shift;
            e.Opacity = 0;
            var begin = TimeSpan.FromMilliseconds(80 * step);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { BeginTime = begin });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5 * u, 0, TimeSpan.FromMilliseconds(550)) { BeginTime = begin, EasingFunction = ease });
        }
    }

    private static IEnumerable<UIElement> Rising(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is UIElement e && (int)e.GetValue(RiseProperty) >= 0) yield return e;
            foreach (var d in Rising(child)) yield return d;
        }
    }

    private static IEnumerable<UIElement> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement e) yield return e;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    // ── The card ─────────────────────────────────────────────────────────

    /// <summary>The card in a picture of <paramref name="w"/> × <paramref name="h"/>: a story fills it; Post and Wide hold the story card on its colour.</summary>
    public static FrameworkElement Card(RewindCard card, RewindFacts facts, int index, int count, string shape, RewindLook look, double w, double h)
    {
        if (shape == "story" || Math.Abs(w / h - 1080.0 / 1920) < 0.02) return Story(card, facts, index, count, look, w, h);
        // A taller story card would not fit: it sits whole in the middle, on its own colour, with room around it.
        double ch = h * 0.9, cw = ch * 1080 / 1920;
        var story = Story(card, facts, index, count, look, 1080, 1920);
        var holder = new Border
        {
            Width = cw, Height = ch, CornerRadius = new CornerRadius(cw * 0.05), ClipToBounds = true,
            Child = new Viewbox { Stretch = Stretch.Fill, Child = story },
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var root = new Grid { Width = w, Height = h, ClipToBounds = true, Background = Background(card.Id, look) };
        root.Children.Add(new Border { Background = Frozen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0))) });
        root.Children.Add(holder);
        return root;
    }

    private static FrameworkElement Story(RewindCard card, RewindFacts f, int index, int count, RewindLook look, double w, double h)
    {
        double u = w / 100;
        var root = new Grid { Width = w, Height = h, ClipToBounds = true, Background = Background(card.Id, look) };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.HighQuality);

        var frame = new DockPanel { Margin = new Thickness(7 * u), LastChildFill = true };
        root.Children.Add(frame);

        // Story progress across the top, the logo and the period under it, the site and the place at the foot.
        var bar = new UniformGrid { Rows = 1, Columns = Math.Max(1, count), Margin = new Thickness(-0.5 * u, 0, -0.5 * u, 4.5 * u) };
        for (int k = 0; k < count; k++)
            bar.Children.Add(new Border { Height = 0.7 * u, CornerRadius = new CornerRadius(u), Margin = new Thickness(0.5 * u, 0, 0.5 * u, 0), Background = White(k <= index ? 1 : 0.28) });
        DockPanel.SetDock(bar, Dock.Top);
        frame.Children.Add(bar);

        // The logo and both texts on one centre line: a text's line box is taller than its letters, so each is given a
        // line exactly its size and centred on the logo's middle.
        var brand = new DockPanel { Height = 6 * u };
        var period = Line(Text(RewindCards.TitleWithYear(f), 3.6 * u, FontWeights.Normal, White(0.75), Ui), 3.6 * u);
        DockPanel.SetDock(period, Dock.Right);
        brand.Children.Add(period);
        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (Logo is { } logo) name.Children.Add(new Image { Source = logo, Width = 6 * u, Height = 6 * u, Margin = new Thickness(0, 0, 2.4 * u, 0), VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(Line(Text("RigSight Rewind", 3.6 * u, FontWeights.SemiBold, White(0.85), Ui), 3.6 * u));
        brand.Children.Add(name);
        DockPanel.SetDock(brand, Dock.Top);
        frame.Children.Add(brand);

        var foot = new DockPanel { Margin = new Thickness(0, 3 * u, 0, 0) };
        var place = Text($"{index + 1} / {count}", 3.2 * u, FontWeights.Normal, White(0.6), Ui);
        DockPanel.SetDock(place, Dock.Right);
        foot.Children.Add(place);
        foot.Children.Add(Text("rigsight.app", 3.2 * u, FontWeights.Normal, White(0.6), Ui));
        DockPanel.SetDock(foot, Dock.Bottom);
        frame.Children.Add(foot);

        var accent = look.AccentColor;
        var body = card.Id switch
        {
            "cover" => Cover(card, f, u, accent),
            "time" => Time(card, f, u, accent),
            "games" => Games(card, f, u, accent),
            "months" => Months(card, u, accent),
            "apps" => Apps(card, u, accent),
            "hours" => UseClock(card, f, u, accent),
            "top" => Top(card, u, accent),
            "longest" => Longest(card, u, accent),
            "internet" => Internet(card, u, accent),
            "heat" => Heat(card, u, accent),
            "changes" => Changes(card, u, accent),
            "problems" => Problems(card, f, u, accent),
            "type" => Player(card, f, u, accent),
            _ => Summary(card, f, u, accent),
        };
        // Named so a test can tell when what the card says needs more room than the card has.
        body.Name = BodyName;
        frame.Children.Add(body);
        return root;
    }

    // ── Each card ────────────────────────────────────────────────────────

    private static FrameworkElement Cover(RewindCard card, RewindFacts f, double u, Color? accent)
    {
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        // The top five's covers fanned out, the most played in the middle.
        var covers = f.TopGames.Take(5).Select(g => Art(g.Path, banner: false, 400)).Where(a => a is not null).ToList();
        // No games: the most used apps' icons fanned out the same way, each on a tile.
        var icons = covers.Count > 0 ? [] : f.TopGames.Concat(f.TopApps).Take(5).Select(a => Icon(a.Path)).Where(i => i is not null).ToList();
        if (covers.Count == 0 && icons.Count > 0)
        {
            var fan = new Canvas { Height = 34 * u, Margin = new Thickness(0, 4 * u, 0, 6 * u) };
            int[] order = icons.Count switch { 1 => [0], 2 => [1, 0], 3 => [1, 0, 2], 4 => [2, 1, 0, 3], _ => [2, 1, 0, 3, 4] };
            int mid = order.Length / 2;
            double step = 16.5 * u, start = (86 * u - (order.Length - 1) * step - 22 * u) / 2;
            for (int k = 0; k < order.Length; k++)
            {
                var tile = new Border
                {
                    Width = 22 * u, Height = 22 * u, CornerRadius = new CornerRadius(5 * u), Background = Frozen(new SolidColorBrush(Color.FromArgb(235, 26, 22, 64))),
                    BorderBrush = White(0.18), BorderThickness = new Thickness(0.3 * u),
                    Child = new Image { Source = icons[order[k]], Width = 13 * u, Height = 13 * u },
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new TransformGroup { Children = { new RotateTransform((k - mid) * 4), new TranslateTransform(0, Math.Abs(k - mid) * 1.6 * u) } },
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 5 * u, ShadowDepth = 2 * u, Opacity = 0.5, Direction = 270 },
                };
                Canvas.SetLeft(tile, start + k * step);
                Panel.SetZIndex(tile, k == mid ? 10 : 5 - Math.Abs(k - mid));
                fan.Children.Add(tile);
            }
            body.Children.Add(Rise(fan, 0));
        }
        if (covers.Count > 0)
        {
            var fan = new Canvas { Height = 44 * u, Margin = new Thickness(0, 4 * u, 0, 4 * u) };
            int[] order = covers.Count switch { 1 => [0], 2 => [1, 0], 3 => [1, 0, 2], 4 => [2, 1, 0, 3], _ => [2, 1, 0, 3, 4] };
            int mid = order.Length / 2;
            // Centred in the card's 86 widths of room.
            // Smaller and closer than first drawn, so the turned outer covers stay well inside the card.
            double step = 12.5 * u, start = (86 * u - (order.Length - 1) * step - 24 * u) / 2;
            for (int k = 0; k < order.Length; k++)
            {
                var img = new Border
                {
                    Width = 24 * u, Height = 36 * u, CornerRadius = new CornerRadius(2 * u), ClipToBounds = true,
                    Background = Frozen(new ImageBrush(covers[order[k]]!) { Stretch = Stretch.UniformToFill }),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new TransformGroup { Children = { new RotateTransform((k - mid) * 6), new TranslateTransform(0, Math.Abs(k - mid) * 2.4 * u) } },
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6 * u, ShadowDepth = 3 * u, Opacity = 0.55, Direction = 270 },
                };
                Canvas.SetLeft(img, start + k * step);
                Panel.SetZIndex(img, k == mid ? 10 : 5 - Math.Abs(k - mid));
                fan.Children.Add(img);
            }
            body.Children.Add(Rise(fan, 0));
        }
        body.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C("#C9B6FF"), u), 1));
        body.Children.Add(Rise(Mega(card.Big, 22, u, top: 2, bottom: 4), 2));
        body.Children.Add(Rise(Sub(card.Sub, u), 3));
        return body;
    }

    private static FrameworkElement Time(RewindCard card, RewindFacts f, double u, Color? accent)
    {
        // The calendars take the room left under the words, made smaller where there is less (never cut off).
        var body = new DockPanel { Margin = new Thickness(0, 6 * u, 0, 0) };
        var words = new StackPanel();
        words.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C("#7DD3FC"), u), 0));
        words.Children.Add(Rise(BigWithUnit(card.Big, card.Unit, card.Big.Length > 4 ? 26 : 34, 8, u), 1));
        words.Children.Add(Rise(Sub(card.Sub, u), 2));
        DockPanel.SetDock(words, Dock.Top);
        body.Children.Add(words);
        var calendars = f.Days <= 31 ? Calendar(f, u, accent) : MonthCalendars(f, u, accent);
        calendars.Width = 86 * u;
        body.Children.Add(Rise(new Viewbox { Child = calendars, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1 * u, 0, 0) }, 3));
        return body;
    }

    private static FrameworkElement Games(RewindCard card, RewindFacts f, double u, Color? accent)
    {
        var words = new StackPanel();
        words.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C("#D8C7FF"), u), 0));
        words.Children.Add(Rise(BigWithUnit(card.Big, card.Unit, 30, 10, u), 1));
        words.Children.Add(Rise(Sub(card.Sub, u), 2));
        return WordsAndPicture(words, Clock(f.PlayHours, 68 * u, accent), u, 5);
    }

    /// <summary>The most used app, its icon large; the next four with theirs.</summary>
    private static FrameworkElement Apps(RewindCard card, double u, Color? accent)
    {
        var body = new StackPanel { Margin = new Thickness(0, 6 * u, 0, 0) };
        body.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C("#7DD3FC"), u), 0));
        var head = new DockPanel { Margin = new Thickness(0, 4 * u, 0, 0) };
        if (Icon(card.Tag) is { } icon)
            head.Children.Add(new Border { Width = 22 * u, Height = 22 * u, CornerRadius = new CornerRadius(5 * u), Background = White(0.1), Margin = new Thickness(0, 0, 4 * u, 0), Child = new Image { Source = icon, Width = 13 * u, Height = 13 * u } });
        head.Children.Add(Centered(Mega(card.Big, 13, u, wrap: true, width: 60))); // beside the 22-wide icon tile
        body.Children.Add(Rise(head, 1));
        body.Children.Add(Rise(Sub(card.Sub, u, top: 3, bottom: 6), 2));
        var rows = new StackPanel();
        foreach (var r in card.Rows) rows.Children.Add(GameRow(r, u, 9, 9, 4.2));
        body.Children.Add(Rise(rows, 3));
        return body;
    }

    /// <summary>When in the day the PC was used: the same clock as the games', for every app.</summary>
    private static FrameworkElement UseClock(RewindCard card, RewindFacts f, double u, Color? accent)
    {
        var words = new StackPanel();
        words.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C("#7DD3FC"), u), 0));
        words.Children.Add(Rise(Mega(card.Big, 15, u, top: 3, bottom: 3, wrap: true), 1));
        words.Children.Add(Rise(Sub(card.Sub, u), 2));
        return WordsAndPicture(words, Clock(f.UseHours, 68 * u, accent ?? RewindLook.C(RewindCards.Sky)), u, 5);
    }

    /// <summary>
    /// The game's own logo, large, on a soft glow of the card's colour (the banner art was dark and busy behind the
    /// words); its cover where the launcher has no logo.
    /// </summary>
    private static FrameworkElement GameMark(string? exePath, double u, Color? accent)
    {
        var holder = new Grid { Height = 40 * u, Margin = new Thickness(0, 6 * u, 0, 0) };
        var glow = Radial(new Point(0.5, 0.5), 0.6, 0.6, accent is { } a ? Color.FromArgb(110, a.R, a.G, a.B) : Color.FromArgb(110, 0xB1, 0x8C, 0xFF));
        holder.Children.Add(new Border { Background = Frozen(glow), Margin = new Thickness(-7 * u, -6 * u, -7 * u, -6 * u) });
        var game = exePath is null ? null : GameLibrary.Find(Core.Apps.AppCatalog.InstalledGames, exePath);
        if (GamePictures.Picture(game?.Art.Logo, 900) is { } logo)
            holder.Children.Add(new Image
            {
                Source = logo, Stretch = Stretch.Uniform, MaxWidth = 76 * u, MaxHeight = 34 * u, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4 * u, ShadowDepth = 1 * u, Opacity = 0.6, Direction = 270 },
            });
        else if (Art(exePath, banner: false, 500) is { } cover)
            holder.Children.Add(new Border
            {
                Width = 26 * u, Height = 39 * u, CornerRadius = new CornerRadius(2.4 * u), HorizontalAlignment = HorizontalAlignment.Center,
                Background = Frozen(new ImageBrush(cover) { Stretch = Stretch.UniformToFill }),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6 * u, ShadowDepth = 2 * u, Opacity = 0.6, Direction = 270 },
            });
        return holder;
    }

    private static FrameworkElement Top(RewindCard card, double u, Color? accent)
    {
        var all = new DockPanel { LastChildFill = false };
        var mark = GameMark(card.Tag, u, accent);
        DockPanel.SetDock(mark, Dock.Top);
        all.Children.Add(Rise(mark, 0));
        var body = new StackPanel { Margin = new Thickness(0, 0, 0, 3 * u) };
        DockPanel.SetDock(body, Dock.Bottom);
        all.Children.Add(body);
        body.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C("#FF9FD0"), u), 0));
        body.Children.Add(Rise(Mega(card.Big, 17, u, top: 1.6, bottom: 1.6, wrap: true), 1));
        body.Children.Add(Rise(Sub(card.Sub, u, bottom: 5), 2));
        var rows = new StackPanel();
        foreach (var r in card.Rows) rows.Children.Add(GameRow(r, u, 9, 12, 4.2));
        body.Children.Add(Rise(rows, 3));
        return all;
    }

    private static FrameworkElement Longest(RewindCard card, double u, Color? accent)
    {
        var all = new DockPanel { LastChildFill = false };
        var mark = GameMark(card.Tag, u, accent);
        DockPanel.SetDock(mark, Dock.Top);
        all.Children.Add(Rise(mark, 0));
        var body = new StackPanel { Margin = new Thickness(0, 0, 0, 4 * u) };
        DockPanel.SetDock(body, Dock.Bottom);
        all.Children.Add(body);
        body.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C("#FF9FD0"), u), 0));
        body.Children.Add(Rise(BigWithUnit(card.Big, card.Unit, 30, 13, u), 1));
        body.Children.Add(Rise(Sub(card.Sub, u), 2));
        if (card.Span is { } span)
        {
            // The day from midnight to midnight, and the sitting in it.
            var tl = new Grid { Height = 16 * u, Margin = new Thickness(0, 6 * u, 0, 0) };
            double width = 86 * u;
            tl.Children.Add(new Border { Height = 3 * u, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6 * u, 0, 0), CornerRadius = new CornerRadius(2 * u), Background = White(0.12) });
            var run = new LinearGradientBrush(accent ?? RewindLook.C(RewindCards.Pink), accent ?? RewindLook.C(RewindCards.Purple), 0);
            tl.Children.Add(new Border
            {
                Height = 3 * u, Width = Math.Max(2 * u, (span.End - span.Start) * width), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(span.Start * width, 6 * u, 0, 0), CornerRadius = new CornerRadius(2 * u), Background = Frozen(run),
            });
            var labels = new Canvas();
            void At(string text, double x, bool right = false)
            {
                var t = Text(text, 3.2 * u, FontWeights.Normal, White(0.75), Ui);
                Canvas.SetTop(t, 11 * u);
                if (right) Canvas.SetRight(t, 0); else Canvas.SetLeft(t, x);
                labels.Children.Add(t);
            }
            // From and to as one label under the sitting, kept inside the card.
            var both = Lang.T($"{span.From} to {span.To}");
            var probe = Text(both, 3.2 * u, FontWeights.Normal, White(0.75), Ui);
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            At(both, Math.Clamp((span.Start + span.End) / 2 * width - probe.DesiredSize.Width / 2, 0, width - probe.DesiredSize.Width));
            tl.Children.Add(labels);
            body.Children.Add(Rise(tl, 3));
        }
        return all;
    }

    private static FrameworkElement Internet(RewindCard card, double u, Color? accent)
    {
        var body = new StackPanel { Margin = new Thickness(0, 6 * u, 0, 0) };
        body.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C("#7DD3FC"), u), 0));
        body.Children.Add(Rise(BigWithUnit(card.Big, card.Unit, 34, 10, u), 1));
        body.Children.Add(Rise(Sub(card.Sub, u), 2));
        var rows = new StackPanel { Margin = new Thickness(0, 6 * u, 0, 0) };
        var fill = new LinearGradientBrush(accent ?? RewindLook.C(RewindCards.Sky), accent ?? RewindLook.C(RewindCards.Cyan), 0);
        foreach (var r in card.Rows)
        {
            var one = new StackPanel { Margin = new Thickness(0, 0, 0, 4 * u) };
            var line = new DockPanel();
            var v = Text(r.Value, 4.2 * u, FontWeights.Bold, White(1), Ui);
            DockPanel.SetDock(v, Dock.Right);
            line.Children.Add(v);
            line.Children.Add(Text(r.Name, 4.2 * u, FontWeights.Normal, White(1), Ui, trim: true));
            one.Children.Add(line);
            one.Children.Add(new Border { Height = 2.2 * u, Width = Math.Max(2 * u, r.Bar * 86 * u), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2 * u), Margin = new Thickness(0, 1.6 * u, 0, 0), Background = Frozen(fill.Clone()) });
            rows.Children.Add(one);
        }
        body.Children.Add(Rise(rows, 3));
        return body;
    }

    private static FrameworkElement Heat(RewindCard card, double u, Color? accent)
    {
        var body = new StackPanel { Margin = new Thickness(0, 6 * u, 0, 0) };
        body.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C("#D8C7FF"), u), 0));
        var big = Mega(card.Big, 44, u, top: 3, bottom: 1);
        var glow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        glow.GradientStops.Add(new GradientStop(Colors.White, 0));
        glow.GradientStops.Add(new GradientStop(accent is { } a ? Lighten(a, 0.5) : RewindLook.C("#D6C4FF"), 0.6));
        glow.GradientStops.Add(new GradientStop(accent ?? RewindLook.C(RewindCards.Purple), 1));
        big.Foreground = Frozen(glow);
        body.Children.Add(Rise(big, 1));
        body.Children.Add(Rise(Sub(card.Sub, u), 2));
        if (card.Tiles.Count > 0) body.Children.Add(Rise(Tiles(card.Tiles, u, top: 7, accent), 3));
        return body;
    }

    private static FrameworkElement Changes(RewindCard card, double u, Color? accent)
    {
        // The words on top, and the bars filling the rest of the card down to its foot.
        var body = new Grid { Margin = new Thickness(0, 6 * u, 0, 0) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var words = new StackPanel();
        words.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C("#67E8F9"), u), 0));
        words.Children.Add(Rise(Mega(card.Big, 15, u, top: 3, bottom: 3, wrap: true), 1));
        if (card.Sub is { } sub) words.Children.Add(Rise(Sub(sub, u), 2));
        if (card.Chips.Count > 0)
        {
            var chips = new WrapPanel();
            foreach (var c in card.Chips)
                chips.Children.Add(new Border { CornerRadius = new CornerRadius(1.4 * u), Background = Frozen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0))), Padding = new Thickness(2.4 * u, 1.2 * u, 2.4 * u, 1.4 * u), Margin = new Thickness(0, 0, 1.8 * u, 1.8 * u), Child = Line(Text(c, 3.4 * u, FontWeights.SemiBold, White(1), Ui), 3.4 * u) });
            words.Children.Add(Rise(chips, 2));
        }
        body.Children.Add(words);

        // Each column: room above, the number, the bar (its share of the room), the word. The tallest bar takes all the
        // room, so its number always sits inside the card.
        var bars = new UniformGrid { Rows = 1, Columns = card.Bars.Count, Margin = new Thickness(-2 * u, 6 * u, -2 * u, 0) };
        foreach (var b in card.Bars)
        {
            double share = Math.Clamp(b.Fraction, 0.04, 1);
            var col = new Grid { Margin = new Thickness(2 * u, 0, 2 * u, 0) };
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - share + 0.0001, GridUnitType.Star) });
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(share, GridUnitType.Star), MinHeight = 1.2 * u });
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var value = Text(b.Value, 9 * u, FontWeights.ExtraBold, White(1), Display, margin: new Thickness(0, 0, 0, 1.6 * u));
            value.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(value, 1);
            col.Children.Add(value);
            var fill = new Border { CornerRadius = new CornerRadius(2 * u, 2 * u, 0, 0), Background = Frozen(new SolidColorBrush(accent ?? RewindLook.C(b.Hue))) };
            Grid.SetRow(fill, 2);
            col.Children.Add(fill);
            var label = Text(b.Label, 3.6 * u, FontWeights.Normal, White(0.75), Ui, margin: new Thickness(0, 1.6 * u, 0, 0));
            label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(label, 3);
            col.Children.Add(label);
            bars.Children.Add(col);
        }
        Grid.SetRow(bars, 1);
        body.Children.Add(Rise(bars, 3));
        return body;
    }

    private static FrameworkElement Problems(RewindCard card, RewindFacts f, double u, Color? accent)
    {
        if (f.AppCrashes + f.ShutOffs == 0) return NothingWrong(card, u, accent);
        var body = new StackPanel { Margin = new Thickness(0, 5 * u, 0, 0) };
        body.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C(RewindCards.Slate), u), 0));
        // The two counts side by side, each with its colour as the key for the bars below.
        var pair = new UniformGrid { Rows = 1, Columns = 2, Margin = new Thickness(-1.5 * u, 4 * u, -1.5 * u, 0) };
        string[] figure = ["#D8C7FF", "#9DB8FF"];
        for (int k = 0; k < card.Tiles.Count && k < 2; k++)
        {
            var t = card.Tiles[k];
            var p = new StackPanel();
            p.Children.Add(Text(t.Value, 20 * u, FontWeights.ExtraBold, Frozen(new SolidColorBrush(accent ?? RewindLook.C(figure[k]))), Display));
            var label = new DockPanel { Margin = new Thickness(0, 2 * u, 0, 0) };
            var key = new Border { Width = 2.6 * u, Height = 2.6 * u, CornerRadius = new CornerRadius(0.6 * u), Background = Frozen(new SolidColorBrush(accent ?? RewindLook.C(t.Hue ?? RewindCards.Purple))), Margin = new Thickness(0, 0.9 * u, 1.4 * u, 0), VerticalAlignment = VerticalAlignment.Top };
            label.Children.Add(key);
            label.Children.Add(Text(t.Label, 3.6 * u, FontWeights.Normal, White(0.85), Ui, wrap: true));
            p.Children.Add(label);
            pair.Children.Add(new Border { CornerRadius = new CornerRadius(3.4 * u), Background = White(0.06), Padding = new Thickness(4 * u), Margin = new Thickness(1.5 * u, 0, 1.5 * u, 0), Child = p });
        }
        body.Children.Add(Rise(pair, 1));
        if (card.Sub is { } sub) body.Children.Add(Rise(Sub(sub, u, top: 5), 2));
        // A bar for each day: crashes as tall bars, shut-offs as a mark under them.
        int days = Math.Min(f.CrashesByDay.Count, f.ShutOffsByDay.Count);
        if (days > 0)
        {
            // A bar a day for a month; a bar a week over 3 months, a month over 6 or 12.
            var crashes = Buckets(f, f.CrashesByDay.Select(c => (double)c).ToList());
            var offs = Buckets(f, f.ShutOffsByDay.Select(c => (double)c).ToList());
            double most = Math.Max(1, crashes.Max(b => b.Sum));
            var grid = new UniformGrid { Rows = 1, Columns = crashes.Count, Height = 44 * u, Margin = new Thickness(0, 6 * u, 0, 0) };
            for (int d = 0; d < crashes.Count; d++)
            {
                var col = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0.5 * u, 0, 0.5 * u, 0) };
                if (crashes[d].Sum > 0) col.Children.Add(new Border { Height = 40 * u * crashes[d].Sum / most, CornerRadius = new CornerRadius(0.8 * u), Background = Frozen(new SolidColorBrush(accent ?? RewindLook.C(RewindCards.Purple))) });
                if (offs[d].Sum > 0) col.Children.Add(new Border { Height = 2.2 * u, CornerRadius = new CornerRadius(0.8 * u), Margin = new Thickness(0, 0.6 * u, 0, 0), Background = Frozen(new SolidColorBrush(accent is { } a ? Lighten(a, 0.4) : RewindLook.C(RewindCards.Blue))) });
                if (crashes[d].Sum == 0 && offs[d].Sum == 0) col.Children.Add(new Border { Height = 0.8 * u, CornerRadius = new CornerRadius(0.8 * u), Background = White(0.1) });
                grid.Children.Add(col);
            }
            body.Children.Add(Rise(grid, 3));
            body.Children.Add(Rise(BucketAxis(f, crashes, u), 3));
        }
        return body;
    }

    /// <summary>A period without a crash or a sudden shut-off: a big tick and the plain sentence.</summary>
    private static FrameworkElement NothingWrong(RewindCard card, double u, Color? accent)
    {
        var body = new StackPanel { Margin = new Thickness(0, 5 * u, 0, 0) };
        body.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C(RewindCards.Slate), u), 0));
        body.Children.Add(Rise(Mega(card.Big, 24, u, top: 3, bottom: 2), 1));
        body.Children.Add(Rise(Sub(card.Sub, u), 2));
        var hue = accent ?? RewindLook.C(RewindCards.Cyan);
        var ring = new Grid { Width = 56 * u, Height = 56 * u, Margin = new Thickness(0, 10 * u, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        ring.Children.Add(new Ellipse { Fill = Frozen(Radial(new Point(0.5, 0.5), 0.5, 0.5, Color.FromArgb(90, hue.R, hue.G, hue.B))) });
        ring.Children.Add(new Ellipse { Stroke = Frozen(new SolidColorBrush(hue)), StrokeThickness = 1.6 * u, Margin = new Thickness(8 * u) });
        ring.Children.Add(new TextBlock { Text = "\uE73E", FontFamily = Icons, FontSize = 18 * u, Foreground = Frozen(new SolidColorBrush(hue)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(Rise(ring, 3));
        return body;
    }

    private static FrameworkElement Player(RewindCard card, RewindFacts f, double u, Color? accent)
    {
        var outer = new DockPanel { Margin = new Thickness(0, 4 * u, 0, 3 * u) };
        var label = Label(card.Label, accent ?? RewindLook.C("#FF9FD0"), u);
        DockPanel.SetDock(label, Dock.Top);
        outer.Children.Add(Rise(label, 0));

        // A player card: the games' covers behind, the kind of player over them.
        var pc = Round(new Grid(), 4.5 * u);
        var covers = f.TopGames.Take(5).Select(g => Art(g.Path, banner: false, 400)).Where(a => a is not null).ToList();
        if (covers.Count > 0)
        {
            var mosaic = new UniformGrid { Rows = 1, Columns = covers.Count };
            foreach (var c in covers) mosaic.Children.Add(new Image { Source = c, Stretch = Stretch.UniformToFill });
            pc.Children.Add(mosaic);
        }
        else
        {
            // No games: the most used apps' icons, large and scattered, behind the name.
            var scatter = new UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(4 * u), VerticalAlignment = VerticalAlignment.Top, Height = 60 * u, Opacity = 0.55 };
            var appIcons = f.TopApps.Select(a => Icon(a.Path)).Where(i => i is not null).ToList();
            for (int k = 0; k < 9 && appIcons.Count > 0; k++)
                scatter.Children.Add(new Image { Source = appIcons[k % appIcons.Count], Width = 12 * u, Height = 12 * u, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform((k % 3 - 1) * 9) });
            pc.Children.Add(scatter);
        }
        var shade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        var deep = accent is { } a ? Darken(a, 0.85) : RewindLook.C("#140828");
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(38, deep.R, deep.G, deep.B), 0));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(140, deep.R, deep.G, deep.B), 0.38));
        shade.GradientStops.Add(new GradientStop(deep, 0.62));
        pc.Children.Add(new Border { Background = Frozen(shade) });
        var ct = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6 * u) };
        if (card.Chips.Count > 0) ct.Children.Add(Label(card.Chips[0], Color.FromArgb(205, 255, 255, 255), u, 3.4));
        ct.Children.Add(Mega(card.Big, 16, u, top: 1.6, bottom: 2.4, wrap: true, width: 72)); // inside the player card's own padding
        ct.Children.Add(Sub(card.Sub, u, size: 4.2));
        var stats = new UniformGrid { Rows = 1, Columns = Math.Max(1, card.Tiles.Count), Margin = new Thickness(-1 * u, 5 * u, -1 * u, 0) };
        foreach (var t in card.Tiles)
        {
            var s = new StackPanel();
            var v = Text(t.Value, 8 * u, FontWeights.ExtraBold, White(1), Display); v.HorizontalAlignment = HorizontalAlignment.Center;
            var l = Text(t.Label, 2.9 * u, FontWeights.Normal, White(0.75), Ui, trim: true); l.HorizontalAlignment = HorizontalAlignment.Center; l.Margin = new Thickness(0, 1.2 * u, 0, 0);
            s.Children.Add(v); s.Children.Add(l);
            stats.Children.Add(new Border { CornerRadius = new CornerRadius(2.6 * u), Background = White(0.08), Padding = new Thickness(2.4 * u, 3 * u, 2.4 * u, 3 * u), Margin = new Thickness(1 * u, 0, 1 * u, 0), Child = s });
        }
        ct.Children.Add(stats);
        pc.Children.Add(ct);
        var cardBorder = new Border
        {
            CornerRadius = new CornerRadius(5 * u), Child = pc, BorderBrush = White(0.22), BorderThickness = new Thickness(0.5 * u), Margin = new Thickness(0, 4 * u, 0, 0),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10 * u, ShadowDepth = 4 * u, Opacity = 0.5, Direction = 270 },
        };
        outer.Children.Add(Rise(cardBorder, 1));
        return outer;
    }

    private static FrameworkElement Summary(RewindCard card, RewindFacts f, double u, Color? accent)
    {
        var body = new DockPanel { Margin = new Thickness(0, 4 * u, 0, 0), LastChildFill = false };
        var label = Label(card.Label, accent ?? RewindLook.C("#9DB8FF"), u);
        DockPanel.SetDock(label, Dock.Top);
        body.Children.Add(Rise(label, 0));

        if (card.Rows.Count > 0)
        {
            // The most played's cover beside the top five.
            var top = new Grid { Margin = new Thickness(0, 4 * u, 0, 4 * u) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24 * u) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4 * u) });
            top.ColumnDefinitions.Add(new ColumnDefinition());
            if (Art(card.Rows[0].Path, banner: false, 400) is null && Icon(card.Rows[0].Path) is { } icon)
                top.Children.Add(new Border
                {
                    Width = 24 * u, Height = 36 * u, CornerRadius = new CornerRadius(2.6 * u), Background = White(0.08),
                    Child = new Image { Source = icon, Width = 16 * u, Height = 16 * u },
                });
            if (Art(card.Rows[0].Path, banner: false, 400) is { } one)
                top.Children.Add(new Border
                {
                    Width = 24 * u, Height = 36 * u, CornerRadius = new CornerRadius(2.6 * u), ClipToBounds = true, Background = Frozen(new ImageBrush(one) { Stretch = Stretch.UniformToFill }),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6 * u, ShadowDepth = 2 * u, Opacity = 0.6, Direction = 270 },
                });
            var list = new UniformGrid { Columns = 1, Rows = card.Rows.Count, Height = 36 * u };
            foreach (var r in card.Rows)
            {
                var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
                var rank = Centered(Text(r.Rank.ToString(CultureInfo.CurrentCulture), 3.8 * u, FontWeights.Normal, White(0.5), Ui)); rank.Width = 4 * u;
                DockPanel.SetDock(rank, Dock.Left);
                row.Children.Add(rank);
                var hrs = Centered(Text(r.Value, 3.8 * u, FontWeights.Bold, White(0.9), Ui, margin: new Thickness(2 * u, 0, 0, 0)));
                DockPanel.SetDock(hrs, Dock.Right);
                row.Children.Add(hrs);
                // The first a size up, smaller for a long name (an app's) so it isn't cut short.
                row.Children.Add(Centered(r.Rank == 1 ? Text(r.Name, r.Name.Length > 10 ? 4.4 * u : 5.6 * u, FontWeights.ExtraBold, White(1), Display, trim: true) : Text(r.Name, 3.8 * u, FontWeights.Normal, White(1), Ui, trim: true)));
                list.Children.Add(row);
            }
            Grid.SetColumn(list, 2);
            top.Children.Add(list);
            DockPanel.SetDock(top, Dock.Top);
            body.Children.Add(Rise(top, 1));
        }
        var tiles = Tiles(card.Tiles, u, top: card.Rows.Count > 0 ? 0 : 4, accent, size: 8);
        DockPanel.SetDock(tiles, Dock.Top);
        body.Children.Add(Rise(tiles, 2));

        // Every day of the period, at the foot.
        var strip = new StackPanel { Margin = new Thickness(0, 3 * u, 0, 1 * u) };
        var head = new DockPanel();
        var right = Text(Lang.T($"{f.DaysOn} of {f.Days} days on"), 3.2 * u, FontWeights.Normal, White(0.75), Ui);
        DockPanel.SetDock(right, Dock.Right);
        head.Children.Add(right);
        head.Children.Add(Text(f.Kind switch
        {
            RewindKind.Month => Lang.T($"Every day of {RewindCards.Title(f)}"),
            RewindKind.Quarter => Lang.T("Every week"),
            _ => Lang.T("Every month"),
        }, 3.2 * u, FontWeights.Normal, White(0.75), Ui));
        strip.Children.Add(head);
        strip.Children.Add(DayStrip(f, u, accent, 13, whole: true));
        DockPanel.SetDock(strip, Dock.Bottom);
        body.Children.Add(Rise(strip, 3));
        return body;
    }

    // ── Pictures made of the period ──────────────────────────────────────

    /// <summary>The month as a calendar, Monday first: each day shaded by its hours, the biggest ringed, the days to come dashed.</summary>
    private static FrameworkElement Calendar(RewindFacts f, double u, Color? accent)
    {
        // The weekday letters on a row of their own: in the days' grid they took a whole row's height.
        var names = CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames;
        var head = new UniformGrid { Columns = 7, Margin = new Thickness(-0.8 * u, 5 * u, -0.8 * u, 0.8 * u) };
        for (int k = 0; k < 7; k++)
        {
            var t = Text(names[(k + 1) % 7], 2.8 * u, FontWeights.Normal, White(0.55), Ui);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            head.Children.Add(t);
        }
        var grid = new UniformGrid { Columns = 7, Margin = new Thickness(-0.8 * u, 0, -0.8 * u, 0) };
        var first = new DateTime(f.From.Year, f.From.Month, 1);
        int offset = ((int)first.DayOfWeek + 6) % 7;
        for (int k = 0; k < offset; k++) grid.Children.Add(new Border());
        int inMonth = DateTime.DaysInMonth(first.Year, first.Month);
        double most = Math.Max(1, f.DayHours.DefaultIfEmpty(0).Max());
        var hue = accent ?? RewindLook.C(RewindCards.Sky);
        for (int d = 1; d <= inMonth; d++)
        {
            int at = d - 1;
            var cell = new Border { CornerRadius = new CornerRadius(1.6 * u), Margin = new Thickness(0.8 * u), Height = 9.4 * u };
            var label = Text(d.ToString(CultureInfo.CurrentCulture), 2.9 * u, FontWeights.Normal, White(1), Ui);
            label.HorizontalAlignment = HorizontalAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center;
            cell.Child = label;
            if (at >= f.DayHours.Count)
            {
                cell.BorderBrush = White(0.12); cell.BorderThickness = new Thickness(Math.Max(1, 0.15 * u)); label.Foreground = White(0.2);
            }
            else if (f.DayHours[at] < 0.05)
            {
                cell.Background = White(0.05); label.Foreground = White(0.35);
            }
            else
            {
                double a = 0.25 + 0.75 * f.DayHours[at] / most;
                cell.Background = Frozen(new SolidColorBrush(Color.FromArgb((byte)(a * 255), hue.R, hue.G, hue.B)));
                if (f.BusiestDay?.Date == first.AddDays(at).Date) { cell.BorderBrush = White(1); cell.BorderThickness = new Thickness(0.6 * u); }
            }
            grid.Children.Add(cell);
        }
        var both = new StackPanel();
        both.Children.Add(head);
        both.Children.Add(grid);
        return both;
    }

    /// <summary>A longer period cut into weeks (3 months) or months (6 or 12): each piece's first day and its sum.</summary>
    private static List<(DateTime Start, double Sum)> Buckets(RewindFacts f, IReadOnlyList<double> perDay)
    {
        var list = new List<(DateTime, double)>();
        if (f.Kind == RewindKind.Month)
        {
            for (int d = 0; d < perDay.Count; d++) list.Add((f.From.AddDays(d), perDay[d]));
            return list;
        }
        bool weeks = f.Kind == RewindKind.Quarter;
        for (var start = f.From; start < f.To; start = weeks ? start.AddDays(7) : start.AddMonths(1))
        {
            var end = weeks ? start.AddDays(7) : start.AddMonths(1);
            if (end > f.To) end = f.To;
            double sum = 0;
            for (var d = start; d < end; d = d.AddDays(1)) { int k = (int)(d - f.From).TotalDays; if (k < perDay.Count) sum += perDay[k]; }
            list.Add((start, sum));
        }
        return list;
    }

    /// <summary>Under bars by week or month: the first, middle and last piece, by month name.</summary>
    private static FrameworkElement BucketAxis(RewindFacts f, List<(DateTime Start, double Sum)> b, double u) =>
        f.Kind == RewindKind.Month ? Axis(b[0].Start, b[b.Count / 2].Start, b[^1].Start, u, "d MMM") : Axis(b[0].Start, b[b.Count / 2].Start, b[^1].Start, u, "MMM");

    /// <summary>Hours by week (3 months) or month (6 or 12), the biggest in white.</summary>
    private static FrameworkElement PeriodStrip(RewindFacts f, double u, Color? accent, double height)
    {
        var b = Buckets(f, f.DayHours);
        double most = Math.Max(1, b.Max(x => x.Sum));
        int best = b.FindIndex(x => x.Sum == b.Max(y => y.Sum));
        var fill = new LinearGradientBrush(accent is { } a ? Lighten(a, 0.4) : RewindLook.C("#9DB8FF"), accent ?? RewindLook.C(RewindCards.Blue), 90);
        var grid = new UniformGrid { Rows = 1, Columns = b.Count, Height = height * u, Margin = new Thickness(-0.4 * u, 2 * u, -0.4 * u, 0) };
        for (int k = 0; k < b.Count; k++)
            grid.Children.Add(new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0.6 * u, 0, 0.6 * u, 0), CornerRadius = new CornerRadius(0.8 * u, 0.8 * u, 0.3 * u, 0.3 * u),
                Height = Math.Max(height * u * 0.04, height * u * b[k].Sum / most), Background = k == best ? White(1) : Frozen(fill.Clone()),
            });
        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(BucketAxis(f, b, u));
        return stack;
    }

    /// <summary>
    /// Three months or more as small month calendars: two across for 3 months, three for 6, four for a year (the
    /// room under the figure holds three rows of four, not four of three).
    /// </summary>
    private static FrameworkElement MonthCalendars(RewindFacts f, double u, Color? accent)
    {
        int months = 0;
        for (var m = f.From; m < f.To; m = m.AddMonths(1)) months++;
        int cols = months > 6 ? 4 : months > 3 ? 3 : 2; // 3 months: two across, big enough to fill the room
        double gap = 3 * u, width = (86 * u - (cols - 1) * gap) / cols, cell = width / 7;
        var grid = new UniformGrid { Columns = cols, Margin = new Thickness(-gap / 2, 0, -gap / 2, 0) };
        double most = Math.Max(1, f.DayHours.DefaultIfEmpty(0).Max());
        var hue = accent ?? RewindLook.C(RewindCards.Sky);
        for (var m = f.From; m < f.To; m = m.AddMonths(1))
        {
            var one = new StackPanel { Margin = new Thickness(gap / 2, 0, gap / 2, gap) };
            one.Children.Add(Text(m.ToString(cols == 4 ? "MMM" : "MMMM", CultureInfo.CurrentCulture), 3 * u, FontWeights.Bold, White(0.8), Ui, margin: new Thickness(0, 0, 0, 1.2 * u)));
            var days = new UniformGrid { Columns = 7 };
            int offset = ((int)m.DayOfWeek + 6) % 7;
            for (int k = 0; k < offset; k++) days.Children.Add(new Border { Height = cell });
            int inMonth = DateTime.DaysInMonth(m.Year, m.Month);
            for (int d = 0; d < inMonth; d++)
            {
                int at = (int)(m.AddDays(d) - f.From).TotalDays;
                var c = new Border { Height = cell - 0.4 * u, Margin = new Thickness(0.2 * u), CornerRadius = new CornerRadius(cell * 0.22) };
                double v = at < f.DayHours.Count ? f.DayHours[at] : 0;
                c.Background = v < 0.05 ? White(0.06) : Frozen(new SolidColorBrush(Color.FromArgb((byte)((0.25 + 0.75 * v / most) * 255), hue.R, hue.G, hue.B)));
                if (f.BusiestDay?.Date == m.AddDays(d).Date) { c.BorderBrush = White(1); c.BorderThickness = new Thickness(0.35 * u); }
                days.Children.Add(c);
            }
            one.Children.Add(days);
            grid.Children.Add(one);
        }
        return grid;
    }

    /// <summary>Month by month: big columns for 3 months, rows for 6 or 12, each with that month's game (or app).</summary>
    private static FrameworkElement Months(RewindCard card, double u, Color? accent)
    {
        var body = new StackPanel { Margin = new Thickness(0, 5 * u, 0, 0) };
        body.Children.Add(Rise(Label(card.Label, accent ?? RewindLook.C("#9DB8FF"), u), 0));
        body.Children.Add(Rise(Mega(card.Big, 13, u, top: 2, bottom: 2, wrap: true), 1));
        body.Children.Add(Rise(Sub(card.Sub, u), 2));
        var fill = new LinearGradientBrush(accent is { } a ? Lighten(a, 0.4) : RewindLook.C("#9DB8FF"), accent ?? RewindLook.C(RewindCards.Blue), 90);
        FrameworkElement Pic(RewindRow r, double w, double h)
        {
            var box = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(Math.Min(w, h) * 0.12), Background = White(0.08) };
            if (Art(r.Path, banner: false, 300) is { } cover) box.Background = Frozen(new ImageBrush(cover) { Stretch = Stretch.UniformToFill });
            else if (Icon(r.Path) is { } icon) box.Child = new Image { Source = icon, Width = w * 0.6, Height = w * 0.6 };
            return box;
        }
        if (card.Rows.Count <= 4)
        {
            var cols = new UniformGrid { Rows = 1, Columns = card.Rows.Count, Height = 66 * u, Margin = new Thickness(-0.8 * u, 6 * u, -0.8 * u, 0) };
            foreach (var r in card.Rows)
            {
                var col = new DockPanel { Margin = new Thickness(0.8 * u, 0, 0.8 * u, 0), LastChildFill = false };
                var month = Text(r.Name, 3 * u, FontWeights.Normal, White(0.7), Ui); month.HorizontalAlignment = HorizontalAlignment.Center;
                DockPanel.SetDock(month, Dock.Bottom); col.Children.Add(month);
                double w = 86 * u / card.Rows.Count - 1.6 * u;
                var pic = Pic(r, w, Math.Min(w * 1.5, 26 * u)); pic.Margin = new Thickness(0, 1.2 * u, 0, 1.2 * u);
                DockPanel.SetDock(pic, Dock.Bottom); col.Children.Add(pic);
                var bar = new Border { Height = Math.Max(1.2 * u, r.Bar * 26 * u), CornerRadius = new CornerRadius(1.2 * u, 1.2 * u, 0.3 * u, 0.3 * u), Background = Frozen(fill.Clone()) };
                DockPanel.SetDock(bar, Dock.Bottom); col.Children.Add(bar);
                var hours = Text(r.Value, 3.4 * u, FontWeights.Bold, White(0.9), Ui, margin: new Thickness(0, 0, 0, 1.2 * u)); hours.HorizontalAlignment = HorizontalAlignment.Center;
                DockPanel.SetDock(hours, Dock.Bottom); col.Children.Add(hours);
                cols.Children.Add(col);
            }
            body.Children.Add(Rise(cols, 3));
        }
        else
        {
            var rows = new StackPanel { Margin = new Thickness(0, 3.5 * u, 0, 0) };
            foreach (var r in card.Rows)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 1.2 * u) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8 * u) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13 * u) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6.5 * u) });
                row.Children.Add(Centered(Text(r.Name, 3.2 * u, FontWeights.Normal, White(0.75), Ui)));
                var track = new Border { Height = 3 * u, CornerRadius = new CornerRadius(2 * u), Background = White(0.08), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2.4 * u, 0) };
                var run = new Border { Height = 3 * u, CornerRadius = new CornerRadius(2 * u), Background = Frozen(fill.Clone()), HorizontalAlignment = HorizontalAlignment.Left };
                track.Child = run;
                track.SizeChanged += (_, e) => run.Width = Math.Max(2 * u, e.NewSize.Width * r.Bar);
                Grid.SetColumn(track, 1); row.Children.Add(track);
                var hours = Centered(Text(r.Value, 3.4 * u, FontWeights.Bold, White(0.9), Ui)); hours.HorizontalAlignment = HorizontalAlignment.Right; hours.Margin = new Thickness(0, 0, 1.6 * u, 0);
                Grid.SetColumn(hours, 2); row.Children.Add(hours);
                var pic = Pic(r, 5 * u, 7 * u); Grid.SetColumn(pic, 3); row.Children.Add(pic);
                rows.Children.Add(row);
            }
            body.Children.Add(Rise(rows, 3));
        }
        return body;
    }

    /// <summary>A bar for each day of the period, as tall as its hours; the biggest in white; days to come as stubs.</summary>
    private static FrameworkElement DayStrip(RewindFacts f, double u, Color? accent, double height, bool whole = false)
    {
        if (f.Kind != RewindKind.Month) return PeriodStrip(f, u, accent, height);
        int total = whole && f.Kind == RewindKind.Month ? DateTime.DaysInMonth(f.From.Year, f.From.Month) : Math.Max(1, f.DayHours.Count);
        double most = Math.Max(1, f.DayHours.DefaultIfEmpty(0).Max());
        var fill = new LinearGradientBrush(accent is { } a ? Lighten(a, 0.4) : RewindLook.C("#9DB8FF"), accent ?? RewindLook.C(RewindCards.Blue), 90);
        var grid = new UniformGrid { Rows = 1, Columns = total, Height = height * u, Margin = new Thickness(-0.4 * u, 2 * u, -0.4 * u, 0) };
        for (int d = 0; d < total; d++)
        {
            var bar = new Border { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0.4 * u, 0, 0.4 * u, 0), CornerRadius = new CornerRadius(0.8 * u, 0.8 * u, 0.3 * u, 0.3 * u) };
            if (d >= f.DayHours.Count) { bar.Height = 1.2 * u; bar.Background = White(0.16); }
            else
            {
                bar.Height = Math.Max(height * u * 0.04, height * u * f.DayHours[d] / most);
                bar.Background = f.BusiestDay?.Date == f.From.AddDays(d).Date ? White(1) : Frozen(fill.Clone());
            }
            grid.Children.Add(bar);
        }
        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(Axis(f.From, f.From.AddDays(total / 2), f.From.AddDays(total - 1), u, total <= 31 ? "%d" : "d MMM"));
        return stack;
    }

    /// <summary>
    /// The words at the top and the picture in the room left under them, made smaller where there is less (a longer
    /// sentence, another language): never cut off by the card's foot.
    /// </summary>
    private static FrameworkElement WordsAndPicture(FrameworkElement words, FrameworkElement picture, double u, double top)
    {
        var body = new DockPanel { Margin = new Thickness(0, top * u, 0, 0) };
        DockPanel.SetDock(words, Dock.Top);
        body.Children.Add(words);
        body.Children.Add(Rise(new Viewbox { Child = picture, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, VerticalAlignment = VerticalAlignment.Top }, 3));
        return body;
    }

    /// <summary>The day as a clock: a wedge for each hour, as long as the play in it; the busiest hours in pink.</summary>
    private static FrameworkElement Clock(double[] hours, double size, Color? accent)
    {
        var canvas = new Canvas { Width = size, Height = size, Margin = new Thickness(0, size * 0.06, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        double max = Math.Max(0.01, hours.DefaultIfEmpty(0).Max()), s = size / 100, cx = 50 * s, cy = 50 * s;
        var hot = Frozen(new SolidColorBrush(accent ?? RewindLook.C(RewindCards.Pink)));
        var calm = Frozen(new SolidColorBrush(accent is { } a ? Color.FromArgb(150, a.R, a.G, a.B) : Color.FromArgb(180, 0xB1, 0x8C, 0xFF)));
        Point P(double r, double angle) => new(cx + r * s * Math.Cos(angle), cy + r * s * Math.Sin(angle));
        for (int h = 0; h < 24 && h < hours.Length; h++)
        {
            double a1 = h / 24.0 * 2 * Math.PI - Math.PI / 2, a2 = (h + 0.86) / 24.0 * 2 * Math.PI - Math.PI / 2;
            double r0 = 22, r1 = 22 + 24 * hours[h] / max + 1.5;
            var fig = new PathFigure { StartPoint = P(r0, a1), IsClosed = true };
            fig.Segments.Add(new LineSegment(P(r1, a1), true));
            fig.Segments.Add(new ArcSegment(P(r1, a2), new Size(r1 * s, r1 * s), 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(P(r0, a2), true));
            fig.Segments.Add(new ArcSegment(P(r0, a1), new Size(r0 * s, r0 * s), 0, false, SweepDirection.Counterclockwise, true));
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = new PathGeometry([fig]), Fill = hours[h] >= max * 0.7 ? hot : calm });
        }
        canvas.Children.Add(new Ellipse { Width = 41 * s, Height = 41 * s, Stroke = White(0.12), StrokeThickness = 0.4 * s, Margin = new Thickness(cx - 20.5 * s, cy - 20.5 * s, 0, 0) });
        foreach (int h in new[] { 0, 6, 12, 18 })
        {
            double angle = h / 24.0 * 2 * Math.PI - Math.PI / 2;
            var t = Text(DateTime.Today.AddHours(h).ToString("h tt", CultureInfo.CurrentCulture), 3.4 * s, FontWeights.Normal, White(0.6), Ui);
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var p = P(16, angle);
            Canvas.SetLeft(t, p.X - t.DesiredSize.Width / 2);
            Canvas.SetTop(t, p.Y - t.DesiredSize.Height / 2);
            canvas.Children.Add(t);
        }
        return canvas;
    }

    // ── Pieces ───────────────────────────────────────────────────────────

    private static FrameworkElement GameRow(RewindRow r, double u, double coverW, double coverH, double size)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2.6 * u) };
        if (r.Rank > 0) { var n = Text(r.Rank.ToString(CultureInfo.CurrentCulture), size * u, FontWeights.Normal, White(0.55), Ui); n.Width = 4 * u; n.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(n); }
        var box = new Border { Width = coverW * u, Height = coverH * u, CornerRadius = new CornerRadius(1.4 * u), ClipToBounds = true, Margin = new Thickness(0, 0, 3 * u, 0), Background = White(0.08) };
        if (Art(r.Path, banner: false, 300) is { } cover) box.Background = Frozen(new ImageBrush(cover) { Stretch = Stretch.UniformToFill });
        else if (Icon(r.Path) is { } icon) box.Child = new Image { Source = icon, Width = coverW * 0.6 * u, Height = coverW * 0.6 * u };
        row.Children.Add(box);
        var v = Text(r.Value, size * u, FontWeights.Bold, White(1), Ui); v.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(v, Dock.Right);
        row.Children.Add(v);
        var name = Text(r.Name, size * u, FontWeights.Normal, White(1), Ui, trim: true); name.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(name);
        return row;
    }

    /// <summary>Tiles two by two, the label above the figure so the two read as one sentence.</summary>
    private static FrameworkElement Tiles(IReadOnlyList<RewindTile> tiles, double u, double top, Color? accent, double size = 9.5)
    {
        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-1.3 * u, top * u, -1.3 * u, 0) };
        foreach (var t in tiles)
        {
            var s = new StackPanel();
            s.Children.Add(Text(t.Label, 3.2 * u, FontWeights.Normal, White(0.75), Ui, wrap: true));
            s.Children.Add(Text(t.Value, size * u, FontWeights.ExtraBold, t.Hue is { } h ? Frozen(new SolidColorBrush(accent ?? RewindLook.C(h))) : White(1), Display, margin: new Thickness(0, 1.4 * u, 0, 0)));
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(3 * u), Background = White(0.07), Padding = new Thickness(3.4 * u), Margin = new Thickness(1.3 * u), Child = s });
        }
        return grid;
    }

    private static FrameworkElement Axis(DateTime first, DateTime mid, DateTime last, double u, string format)
    {
        var axis = new DockPanel { Margin = new Thickness(0, 1.6 * u, 0, 0), LastChildFill = false };
        var a = Text(first.ToString(format, CultureInfo.CurrentCulture), 2.9 * u, FontWeights.Normal, White(0.6), Ui);
        var c = Text(last.ToString(format, CultureInfo.CurrentCulture), 2.9 * u, FontWeights.Normal, White(0.6), Ui);
        DockPanel.SetDock(a, Dock.Left); DockPanel.SetDock(c, Dock.Right);
        axis.Children.Add(a); axis.Children.Add(c);
        var grid = new Grid();
        grid.Children.Add(axis);
        var b = Text(mid.ToString(format, CultureInfo.CurrentCulture), 2.9 * u, FontWeights.Normal, White(0.6), Ui);
        b.HorizontalAlignment = HorizontalAlignment.Center; b.Margin = new Thickness(0, 1.6 * u, 0, 0);
        grid.Children.Add(b);
        return grid;
    }

    private static TextBlock Label(string text, Color color, double u, double size = 4.2) => new()
    {
        Text = text.ToUpper(CultureInfo.CurrentCulture), FontFamily = Ui, FontSize = size * u, FontWeight = FontWeights.Bold,
        Foreground = Frozen(new SolidColorBrush(color)), TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>
    /// A big figure or name. One that would run past the card's width (a long game's name) is set smaller, to fit in
    /// at most two lines (one where it may not wrap), and cut with an ellipsis beyond that: it never pushes the card's
    /// other parts off its foot.
    /// </summary>
    private static TextBlock Mega(string text, double size, double u, double top = 0, double bottom = 0, bool wrap = false, double width = 86)
    {
        // The biggest size up to the asked one at which the words really fit: one line, or two lines broken only between
        // words (counting letters alone once cut "A new graphics driver" to "A new graphics d…").
        var face = new Typeface(Display, FontStyles.Normal, FontWeights.ExtraBold, FontStretches.Normal);
        double s = size;
        for (; s > 7; s -= 0.25)
        {
            var shape = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, s * u, Brushes.White, 1.0);
            if (!wrap) { if (shape.WidthIncludingTrailingWhitespace <= width * u) break; continue; }
            shape.MaxTextWidth = width * u;
            shape.LineHeight = s * u * 0.92;
            if (shape.MinWidth <= width * u && shape.Height <= s * u * 0.92 * 2 + 1) break;
        }
        return new()
        {
            Text = text, FontFamily = Display, FontSize = s * u, FontWeight = FontWeights.ExtraBold, Foreground = White(1),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = s * u * 0.92, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = s * u * 0.92 * (wrap ? 2 : 1) + 1, Margin = new Thickness(-0.4 * u, top * u, 0, bottom * u),
        };
    }

    private static FrameworkElement BigWithUnit(string big, string? unit, double size, double unitSize, double u)
    {
        var row = new WrapPanel { Margin = new Thickness(-0.4 * u, 2 * u, 0, 1 * u) };
        row.Children.Add(new TextBlock { Text = big, FontFamily = Display, FontSize = size * u, FontWeight = FontWeights.ExtraBold, Foreground = White(1), VerticalAlignment = VerticalAlignment.Bottom });
        if (unit is not null)
            row.Children.Add(new TextBlock { Text = unit, FontFamily = Display, FontSize = unitSize * u, FontWeight = FontWeights.ExtraBold, Foreground = White(0.9), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2 * u, 0, 0, size * u * 0.14) });
        return row;
    }

    private static TextBlock Sub(string? text, double u, double top = 0, double bottom = 0, double size = 4.6) => new()
    {
        Text = text ?? "", FontFamily = Ui, FontSize = size * u, Foreground = White(0.92), TextWrapping = TextWrapping.Wrap,
        LineHeight = size * u * 1.35, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Margin = new Thickness(0, top * u, 0, bottom * u),
        Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible,
    };

    private static TextBlock Text(string text, double size, FontWeight weight, Brush brush, FontFamily family, bool trim = false, bool wrap = false, Thickness? margin = null) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, Foreground = brush, FontFamily = family,
        TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Margin = margin ?? new Thickness(0),
    };

    // ── Colours ──────────────────────────────────────────────────────────

    private static Brush White(double alpha) => Frozen(new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), 255, 255, 255)));

    /// <summary>Each card's colour world, from the app's palette (deep blues, violets, cyans); an accent recolours them all.</summary>
    private static Brush Background(string id, RewindLook look)
    {
        if (look.AccentColor is { } a)
            return Frozen(Linear(170, (Darken(a, 0.55), 0), (Darken(a, 0.82), 0.6), (Darken(a, 0.9), 1)));
        return id switch
        {
            "cover" => Layered(Radial(new Point(0.5, 0.3), 0.9, 0.6, RewindLook.C("#3B2AA8")), Linear(170, (RewindLook.C("#1B1060"), 0), (RewindLook.C("#09071F"), 0.6), (RewindLook.C("#2A0B3A"), 1))),
            "time" => Frozen(Linear(165, (RewindLook.C("#0D3A63"), 0), (RewindLook.C("#061727"), 0.55), (RewindLook.C("#03101C"), 1))),
            "months" => Frozen(Linear(170, (RewindLook.C("#121A3D"), 0), (RewindLook.C("#070912"), 1))),
            "apps" or "hours" => Frozen(Linear(165, (RewindLook.C("#0D3A63"), 0), (RewindLook.C("#061727"), 0.55), (RewindLook.C("#03101C"), 1))),
            "games" or "top" or "longest" or "type" => Frozen(Linear(160, (RewindLook.C("#3A1670"), 0), (RewindLook.C("#160733"), 0.6), (RewindLook.C("#0B041C"), 1))),
            "heat" => Layered(Radial(new Point(0.5, 0.34), 0.7, 0.45, Color.FromArgb(140, 0xB1, 0x8C, 0xFF)), Linear(170, (RewindLook.C("#250C3D"), 0), (RewindLook.C("#0D0518"), 1))),
            "changes" or "internet" => Frozen(Linear(160, (RewindLook.C("#08414A"), 0), (RewindLook.C("#03181D"), 0.6), (RewindLook.C("#020C0F"), 1))),
            "problems" => Frozen(Linear(165, (RewindLook.C("#1D2433"), 0), (RewindLook.C("#0B0E15"), 1))),
            _ => Frozen(Linear(170, (RewindLook.C("#121A3D"), 0), (RewindLook.C("#070912"), 1))),
        };
    }

    /// <summary>The colour the art at the top fades into: the card's own, a little below its top.</summary>
    private static Color FadeColor(string id, RewindLook look) => look.AccentColor is { } a ? Darken(a, 0.78) : id == "top" ? RewindLook.C("#1A0A2C") : RewindLook.C("#140826");

    private static LinearGradientBrush Linear(double angle, params (Color C, double At)[] stops)
    {
        double r = (angle - 90) * Math.PI / 180;
        var b = new LinearGradientBrush { StartPoint = new Point(0.5 - Math.Cos(r) / 2, 0.5 - Math.Sin(r) / 2), EndPoint = new Point(0.5 + Math.Cos(r) / 2, 0.5 + Math.Sin(r) / 2) };
        foreach (var (c, at) in stops) b.GradientStops.Add(new GradientStop(c, at));
        return b;
    }

    private static RadialGradientBrush Radial(Point center, double rx, double ry, Color c)
    {
        var g = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = rx, RadiusY = ry };
        g.GradientStops.Add(new GradientStop(c, 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0.7));
        return g;
    }

    private static Brush Layered(Brush top, Brush under)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(under, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        group.Children.Add(new GeometryDrawing(top, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        return Frozen(new DrawingBrush(group) { Stretch = Stretch.Fill });
    }

    private static Color Darken(Color c, double by) => Color.FromRgb((byte)(c.R * (1 - by)), (byte)(c.G * (1 - by)), (byte)(c.B * (1 - by)));
    private static Color Lighten(Color c, double by) => Color.FromRgb((byte)(c.R + (255 - c.R) * by), (byte)(c.G + (255 - c.G) * by), (byte)(c.B + (255 - c.B) * by));

    private static T Frozen<T>(T f) where T : Freezable { if (f.CanFreeze) f.Freeze(); return f; }

    private static T Centered<T>(T e) where T : FrameworkElement { e.VerticalAlignment = VerticalAlignment.Center; return e; }

    /// <summary>A one-line text whose box is its letters' height, centred: lines up with an icon beside it.</summary>
    private static TextBlock Line(TextBlock t, double size)
    {
        t.LineHeight = size * 1.2;
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    /// <summary>Clipped to a rounded rectangle: a border's corner radius doesn't clip what is inside it.</summary>
    public static T Round<T>(T e, double radius) where T : FrameworkElement
    {
        e.SizeChanged += (_, _) => e.Clip = new RectangleGeometry(new Rect(0, 0, e.ActualWidth, e.ActualHeight), radius, radius);
        return e;
    }

    // ── The games' art ───────────────────────────────────────────────────

    /// <summary>A game's cover (tall) or banner (wide) from its launcher's files, by its program's path; null when it has none.</summary>
    private static ImageSource? Art(string? exePath, bool banner, int width)
    {
        if (exePath is null) return null;
        var game = GameLibrary.Find(Core.Apps.AppCatalog.InstalledGames, exePath);
        var path = banner ? game?.Art.Banner ?? game?.Art.Cover : game?.Art.Cover;
        return GamePictures.Picture(path, width);
    }

    private static ImageSource? Icon(string? path) => GamePictures.Icon(path) ?? (path is null ? null : IconCache.Get(path));

    // ── Pictures ─────────────────────────────────────────────────────────

    /// <summary>The card as a picture of its shape's full size (1080 × 1920 for a story).</summary>
    public static BitmapSource Picture(RewindCard card, RewindFacts facts, int index, int count, string shape, RewindLook look)
    {
        var (w, h) = Sizes[shape];
        var element = Card(card, facts, index, count, shape, look, w, h);
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
