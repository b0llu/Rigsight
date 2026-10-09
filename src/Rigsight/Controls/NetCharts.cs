using System.Windows;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;

namespace Rigsight.Controls;

/// <summary>What the network charts share: download and upload colours, and a data axis from 0 in round steps.</summary>
public abstract class NetChartBase : FanChartBase
{
    public static readonly DependencyProperty DownBrushProperty = DependencyProperty.Register(
        nameof(DownBrush), typeof(Brush), typeof(NetChartBase), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpBrushProperty = DependencyProperty.Register(
        nameof(UpBrush), typeof(Brush), typeof(NetChartBase), new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    protected NetChartBase()
    {
        SetResourceReference(DownBrushProperty, "CoolBrush");
        SetResourceReference(UpBrushProperty, "PurpleBrush");
    }

    public Brush DownBrush { get => (Brush)GetValue(DownBrushProperty); set => SetValue(DownBrushProperty, value); }
    public Brush UpBrush { get => (Brush)GetValue(UpBrushProperty); set => SetValue(UpBrushProperty, value); }

    /// <summary>A round top for the axis (four steps of 1, 2 or 5 times a power of ten, in Windows' MB / GB).</summary>
    protected static double NiceTop(double max)
    {
        if (max <= 0) return 4 << 20;
        double step = max / 4;
        double unit = step >= 1L << 30 ? 1L << 30 : step >= 1L << 20 ? 1L << 20 : 1L << 10;
        double v = step / unit;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double m = v / p;
        double nice = (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
        return Math.Max(nice * unit, 1) * 4;
    }

    /// <summary>The plot area, with a dashed grid and labels made by <paramref name="label"/>.</summary>
    protected Rect Grid(DrawingContext dc, double top, Func<double, string> label)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var plot = new Rect(AxisWidth + 12, Top, Math.Max(1, ActualWidth - AxisWidth - 12 - 2), Math.Max(1, ActualHeight - AxisHeight - Top));
        var pen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([3, 4], 0) };
        pen.Freeze();
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(plot.Bottom - plot.Height * i / 4) + 0.5;
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(dc, i == 0 ? "0" : label(top * i / 4), new Point(plot.Left - 8, y), LabelBrush, right: true);
        }
        return plot;
    }
}

/// <summary>How the bars of the usage chart are split.</summary>
public enum NetBarsMode
{
    /// <summary>By app: the few with a colour of their own, then every other app together.</summary>
    App,
    /// <summary>The app in front, and what moved in the background (or with nobody at the PC).</summary>
    Background,
}

/// <summary>
/// The period's internet use in bars: per hour (a day), day (a week or month) or month (a year). Each bar is everything
/// that moved in it, download and upload together, split by app or by in front and background. Hover a bar for its
/// numbers; click it to pick it (the page then lists that bar's apps), click it again to let go.
/// </summary>
public sealed class NetBarsChart : NetChartBase
{
    public static readonly DependencyProperty BinsProperty = DependencyProperty.Register(
        nameof(Bins), typeof(IReadOnlyList<NetBin>), typeof(NetBarsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(ReportRange), typeof(NetBarsChart), new FrameworkPropertyMetadata(ReportRange.Day, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(NetBarsMode), typeof(NetBarsChart), new FrameworkPropertyMetadata(NetBarsMode.App, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorAppsProperty = DependencyProperty.Register(
        nameof(ColorApps), typeof(IReadOnlyList<long>), typeof(NetBarsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AppNamesProperty = DependencyProperty.Register(
        nameof(AppNames), typeof(IReadOnlyDictionary<long, string>), typeof(NetBarsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(int), typeof(NetBarsChart),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public NetBarsChart() => Cursor = System.Windows.Input.Cursors.Hand;

    public IReadOnlyList<NetBin>? Bins { get => (IReadOnlyList<NetBin>?)GetValue(BinsProperty); set => SetValue(BinsProperty, value); }
    public ReportRange Unit { get => (ReportRange)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public NetBarsMode Mode { get => (NetBarsMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    /// <summary>The apps with a colour of their own, in the order of <see cref="AppBrushKeys"/>.</summary>
    public IReadOnlyList<long>? ColorApps { get => (IReadOnlyList<long>?)GetValue(ColorAppsProperty); set => SetValue(ColorAppsProperty, value); }
    /// <summary>Each app's name, for the hover box.</summary>
    public IReadOnlyDictionary<long, string>? AppNames { get => (IReadOnlyDictionary<long, string>?)GetValue(AppNamesProperty); set => SetValue(AppNamesProperty, value); }
    /// <summary>The bar picked by a click (-1: none).</summary>
    public int Selected { get => (int)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    /// <summary>The colours apps are told apart by, in order: none of them one that says how a reading is doing.</summary>
    public static readonly string[] AppBrushKeys = ["CpuBrush", "PinkBrush", "GpuBrush", "PurpleBrush"];
    /// <summary>Every other app together.</summary>
    public const string OtherBrushKey = "FaintBrush";

    private Brush Res(string key) => TryFindResource(key) as Brush ?? MutedBrush;

    private Rect PlotArea => new(AxisWidth + 12, Top, Math.Max(1, ActualWidth - AxisWidth - 12 - 2), Math.Max(1, ActualHeight - AxisHeight - Top));

    /// <summary>The bar under <paramref name="x"/> (null outside the plot).</summary>
    internal int? BarAt(double x)
    {
        var plot = PlotArea;
        int count = Bins?.Count ?? 0;
        if (count == 0 || x < plot.Left || x >= plot.Right) return null;
        return Math.Min(count - 1, (int)((x - plot.Left) / (plot.Width / count)));
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (BarAt(e.GetPosition(this).X) is int i) Pick(i);
    }

    /// <summary>A click on bar <paramref name="i"/>: picks it, or lets go of it when it is the one picked. An empty bar
    /// has no apps to list, so clicking one lets go too.</summary>
    internal void Pick(int i)
    {
        if (Bins is not { } bins || i < 0 || i >= bins.Count) return;
        SetCurrentValue(SelectedProperty, i == Selected || bins[i].Total == 0 ? -1 : i);
    }

    /// <summary>A bar's pieces, bottom first, each with its colour: what <see cref="OnRender"/> stacks.</summary>
    internal IReadOnlyList<(Brush Brush, long Bytes)> Pieces(NetBin b)
    {
        if (Mode == NetBarsMode.Background)
        {
            long behind = Math.Min(b.Background, b.Total);
            return [(DownBrush, b.Total - behind), (Faded(DownBrush, 0.45), behind)];
        }
        var pieces = new List<(Brush, long)>();
        long named = 0;
        var colored = ColorApps ?? [];
        for (int i = 0; i < colored.Count && i < AppBrushKeys.Length; i++)
        {
            long bytes = Math.Min(b.Apps.FirstOrDefault(a => a.App == colored[i])?.Total ?? 0, b.Total - named);
            named += bytes;
            pieces.Add((Res(AppBrushKeys[i]), bytes));
        }
        pieces.Add((Res(OtherBrushKey), Math.Max(0, b.Total - named)));
        return pieces;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bins = Bins ?? [];
        double top = NiceTop(bins.Select(b => (double)b.Total).DefaultIfEmpty(0).Max());
        var plot = Grid(dc, top, v => Units.Data(v));
        if (bins.Count == 0) return;

        double slot = plot.Width / bins.Count, bar = Math.Min(slot * 0.72, 42);
        double Y(double v) => plot.Bottom - plot.Height * Math.Min(v, top) / top;
        int? hover = HoverX is double hx ? BarAt(hx) : null;
        int selected = Selected >= 0 && Selected < bins.Count ? Selected : -1;
        if (hover is int h)
            dc.DrawRoundedRectangle(Faded(MutedBrush, 0.12), null, new Rect(plot.Left + h * slot, plot.Top, slot, plot.Height), 4, 4);

        for (int i = 0; i < bins.Count; i++)
        {
            var b = bins[i];
            double x = plot.Left + i * slot + (slot - bar) / 2;
            if (b.Total > 0)
            {
                // One rounded bar, its pieces stacked inside it with a hairline of the card between them. With a bar
                // picked, the others step back.
                var whole = new Rect(x, Y(b.Total), bar, Math.Max(1, plot.Bottom - Y(b.Total)));
                var clip = new RectangleGeometry(whole, 2.5, 2.5);
                clip.Freeze();
                dc.PushClip(clip);
                bool back = selected >= 0 && i != selected;
                if (back) dc.PushOpacity(0.3);
                double from = 0;
                foreach (var (brush, bytes) in Pieces(b))
                {
                    if (bytes <= 0) continue;
                    double y1 = Y(from), y2 = Y(from + bytes), gap = from > 0 && y1 - y2 > 3 ? 1.5 : 0;
                    dc.DrawRectangle(brush, null, new Rect(x, y2, bar, Math.Max(0.5, y1 - y2 - gap)));
                    from += bytes;
                }
                if (back) dc.Pop();
                dc.Pop();
            }
            // Time that wasn't recorded: a grey line under the bar (what it shows is only part), and for a bar with
            // nothing at all a dashed outline in its place, so it isn't read as "nothing used".
            if (b.UnrecordedMinutes >= MinUnrecorded)
            {
                var grey = Faded(MutedBrush, 0.7);
                dc.DrawRoundedRectangle(grey, null, new Rect(x, plot.Bottom + 2, bar, 3), 1.5, 1.5);
                if (b.Total == 0)
                {
                    var dashed = new Pen(grey, 1) { DashStyle = new DashStyle([3, 3], 0) };
                    dashed.Freeze();
                    dc.DrawRoundedRectangle(null, dashed, new Rect(x + 0.5, plot.Bottom - 28.5, bar - 1, 28), 2.5, 2.5);
                }
            }
            if (AxisLabel(b.Start, i) is { } text) Label(dc, text, new Point(plot.Left + i * slot + slot / 2, plot.Bottom + AxisHeight / 2 + 2), LabelBrush, center: true);
        }

        if (hover is int k) Hover(dc, plot, plot.Left + k * slot + slot / 2, Title(bins[k].Start), HoverLines(bins[k]));
    }

    /// <summary>The fewest unrecorded minutes in a bar for it to be marked.</summary>
    public const int MinUnrecorded = 15;

    /// <summary>What hovering a bar says: its total, then its biggest apps or its in front / background split.</summary>
    internal IReadOnlyList<string> HoverLines(NetBin b)
    {
        string? missing = b.UnrecordedMinutes >= MinUnrecorded
            ? $"Not recorded for {(b.UnrecordedMinutes >= 60 ? $"{b.UnrecordedMinutes / 60} h {b.UnrecordedMinutes % 60} min" : $"{b.UnrecordedMinutes} min")}" : null;
        if (b.Total == 0) return [missing ?? "Nothing"];
        var lines = new List<string> { $"Total {Units.Data(b.Total)}" };
        if (missing is not null) lines.Add(missing);
        if (Mode == NetBarsMode.Background)
        {
            long behind = Math.Min(b.Background, b.Total), away = Math.Min(b.Away, behind);
            lines.Add($"In front {Units.Data(b.Total - behind)}");
            lines.Add($"In the background {Units.Data(behind - away)}");
            if (away > 0) lines.Add($"While you were away {Units.Data(away)}");
        }
        else
        {
            foreach (var a in b.Apps.Take(5))
                lines.Add($"{(AppNames is { } names && names.TryGetValue(a.App, out var name) ? name : "Unknown")} {Units.Data(a.Total)}");
        }
        return lines;
    }

    private string? AxisLabel(DateTime t, int i) => Unit switch
    {
        ReportRange.Day => i % 3 == 0 ? t.ToString("h tt") : null,
        ReportRange.Week => t.ToString("ddd"),
        ReportRange.Year => t.ToString("MMM"),
        _ => i % 5 == 0 ? t.ToString("d MMM") : null,
    };

    internal string Title(DateTime t) => Unit switch
    {
        ReportRange.Day => $"{t:h tt} – {t.AddHours(1):h tt}",
        ReportRange.Year => t.ToString("MMMM yyyy"),
        _ => t.ToString("dddd, d MMMM"),
    };
}

/// <summary>The connection's speed over the last minute: download as a filled line, upload as a line. Hover a moment for its speeds.</summary>
public sealed class NetLiveChart : NetChartBase
{
    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(IReadOnlyList<NetLive>), typeof(NetLiveChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(long), typeof(NetLiveChart), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<NetLive>? History { get => (IReadOnlyList<NetLive>?)GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    /// <summary>Bumped each second (the live tick) to redraw.</summary>
    public long Version { get => (long)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var points = History ?? [];
        double top = NiceTop(points.Select(p => Math.Max(p.Down, p.Up)).DefaultIfEmpty(0).Max());
        var plot = Grid(dc, top, v => Units.Speed(v));
        Label(dc, "60 seconds", new Point(plot.Left, plot.Bottom + AxisHeight / 2 + 2), LabelBrush);
        Label(dc, "now", new Point(plot.Right, plot.Bottom + AxisHeight / 2 + 2), LabelBrush, right: true);
        if (points.Count < 2) return;

        long last = points[^1].Time;
        Point At(NetLive p, double v) => new(plot.Right - plot.Width * Math.Clamp(last - p.Time, 0, 59) / 59.0, plot.Bottom - plot.Height * Math.Min(v, top) / top);
        StreamGeometry Line(Func<NetLive, double> value, bool fill)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                var first = At(points[0], value(points[0]));
                ctx.BeginFigure(fill ? new Point(first.X, plot.Bottom) : first, fill, fill);
                if (fill) ctx.LineTo(first, true, false);
                for (int i = 1; i < points.Count; i++) ctx.LineTo(At(points[i], value(points[i])), true, true);
                if (fill) ctx.LineTo(new Point(At(points[^1], 0).X, plot.Bottom), true, false);
            }
            g.Freeze();
            return g;
        }
        dc.DrawGeometry(Faded(DownBrush, 0.14), null, Line(p => p.Down, fill: true));
        dc.DrawGeometry(null, new Pen(DownBrush, 1.6), Line(p => p.Down, fill: false));
        dc.DrawGeometry(null, new Pen(UpBrush, 1.4), Line(p => p.Up, fill: false));

        // Hover: the second nearest the pointer, its speeds marked on both lines.
        if (HoverX is double hx && hx >= plot.Left - 6 && hx <= plot.Right + 6)
        {
            var near = points.MinBy(p => Math.Abs(At(p, 0).X - hx))!;
            var (down, up) = (At(near, near.Down), At(near, near.Up));
            dc.DrawEllipse(UpBrush, null, up, 3.5, 3.5);
            dc.DrawEllipse(DownBrush, null, down, 3.5, 3.5);
            long ago = last - near.Time;
            Hover(dc, plot, down.X, ago <= 0 ? "Now" : ago == 1 ? "1 second ago" : $"{ago} seconds ago",
                [$"Download {Units.Speed(near.Down)}", $"Upload {Units.Speed(near.Up)}"]);
        }
    }
}

/// <summary>An app row's bar: its download then its upload, each as a share (0–100) of the biggest app's.</summary>
public sealed class BarPair : FrameworkElement
{
    public static readonly DependencyProperty DownShareProperty = DependencyProperty.Register(
        nameof(DownShare), typeof(double), typeof(BarPair), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpShareProperty = DependencyProperty.Register(
        nameof(UpShare), typeof(double), typeof(BarPair), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DownBrushProperty = NetChartBase.DownBrushProperty.AddOwner(typeof(BarPair),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UpBrushProperty = NetChartBase.UpBrushProperty.AddOwner(typeof(BarPair),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public BarPair()
    {
        SetResourceReference(DownBrushProperty, "CoolBrush");
        SetResourceReference(UpBrushProperty, "PurpleBrush");
    }

    public double DownShare { get => (double)GetValue(DownShareProperty); set => SetValue(DownShareProperty, value); }
    public double UpShare { get => (double)GetValue(UpShareProperty); set => SetValue(UpShareProperty, value); }
    public Brush DownBrush { get => (Brush)GetValue(DownBrushProperty); set => SetValue(DownBrushProperty, value); }
    public Brush UpBrush { get => (Brush)GetValue(UpBrushProperty); set => SetValue(UpBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double down = w * Math.Clamp(DownShare, 0, 100) / 100, up = w * Math.Clamp(UpShare, 0, 100) / 100;
        if (down > 0) dc.DrawRoundedRectangle(DownBrush, null, new Rect(0, 0, Math.Max(down, 2), h), h / 2, h / 2);
        if (up > 0) dc.DrawRoundedRectangle(UpBrush, null, new Rect(Math.Min(down, w - 2), 0, Math.Max(up, 2), h), h / 2, h / 2);
    }
}

/// <summary>An app's use split three ways: in front, in the background, and while nobody was there (shares, 0–1).</summary>
public sealed class ShareBar : FrameworkElement
{
    public static readonly DependencyProperty FrontProperty = DependencyProperty.Register(
        nameof(Front), typeof(double), typeof(ShareBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Background2Property = DependencyProperty.Register(
        nameof(Background2), typeof(double), typeof(ShareBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AwayProperty = DependencyProperty.Register(
        nameof(Away), typeof(double), typeof(ShareBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(ShareBar), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public ShareBar() => SetResourceReference(BrushProperty, "CoolBrush");

    public double Front { get => (double)GetValue(FrontProperty); set => SetValue(FrontProperty, value); }
    /// <summary>The background share (named so it doesn't hide FrameworkElement's own properties).</summary>
    public double Background2 { get => (double)GetValue(Background2Property); set => SetValue(Background2Property, value); }
    public double Away { get => (double)GetValue(AwayProperty); set => SetValue(AwayProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, x = 0;
        var clip = new RectangleGeometry(new Rect(0, 0, w, h), h / 2, h / 2);
        clip.Freeze();
        dc.PushClip(clip);
        foreach (var (share, opacity) in new[] { (Front, 1.0), (Background2, 0.45), (Away, 0.22) })
        {
            double part = w * Math.Clamp(share, 0, 1);
            if (part <= 0) continue;
            var brush = Brush.Clone();
            brush.Opacity = opacity;
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(x, 0, part, h));
            x += part;
        }
        dc.Pop();
    }
}
