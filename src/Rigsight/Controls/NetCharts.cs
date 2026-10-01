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

/// <summary>
/// The period's internet use in bars: per hour (a day), day (a week or month) or month (a year). Each bar is the
/// download, the part downloaded in the background a darker part of it, and the upload a slim bar beside it. Hover a
/// bar for its numbers.
/// </summary>
public sealed class NetBarsChart : NetChartBase
{
    public static readonly DependencyProperty BinsProperty = DependencyProperty.Register(
        nameof(Bins), typeof(IReadOnlyList<NetBin>), typeof(NetBarsChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(ReportRange), typeof(NetBarsChart), new FrameworkPropertyMetadata(ReportRange.Day, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<NetBin>? Bins { get => (IReadOnlyList<NetBin>?)GetValue(BinsProperty); set => SetValue(BinsProperty, value); }
    public ReportRange Unit { get => (ReportRange)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var bins = Bins ?? [];
        double top = NiceTop(bins.Select(b => (double)Math.Max(b.Down, b.Up)).DefaultIfEmpty(0).Max());
        var plot = Grid(dc, top, v => Units.Data(v));
        if (bins.Count == 0) return;

        double slot = plot.Width / bins.Count, bar = Math.Min(slot * 0.7, 42);
        var bg = Faded(DownBrush, 0.45);
        double Y(double v) => plot.Bottom - plot.Height * Math.Min(v, top) / top;
        int? hover = HoverX is double hx && hx >= plot.Left && hx < plot.Right ? (int)((hx - plot.Left) / slot) : null;
        if (hover is int h && h < bins.Count)
            dc.DrawRoundedRectangle(Faded(MutedBrush, 0.12), null, new Rect(plot.Left + h * slot, plot.Top, slot, plot.Height), 4, 4);

        for (int i = 0; i < bins.Count; i++)
        {
            var b = bins[i];
            double x = plot.Left + i * slot + (slot - bar) / 2, dw = bar * 0.66, uw = bar * 0.28;
            if (b.Down > 0)
            {
                // The background part at the bottom in a paler shade, the part in front on top of it in full.
                long behind = Math.Min(b.BgDown, b.Down);
                var bar0 = new Rect(x, Y(b.Down), dw, Math.Max(1, plot.Bottom - Y(b.Down)));
                var clip = new RectangleGeometry(bar0, 2, 2);
                clip.Freeze();
                dc.PushClip(clip);
                if (behind > 0) dc.DrawRectangle(bg, null, new Rect(x, Y(behind), dw, plot.Bottom - Y(behind)));
                if (b.Down > behind) dc.DrawRectangle(DownBrush, null, new Rect(x, Y(b.Down), dw, Math.Max(1, Y(behind) - Y(b.Down))));
                dc.Pop();
            }
            if (b.Up > 0) dc.DrawRoundedRectangle(UpBrush, null, new Rect(x + dw + bar * 0.06, Y(b.Up), uw, Math.Max(1, plot.Bottom - Y(b.Up))), 1.5, 1.5);
            if (AxisLabel(b.Start, i) is { } text) Label(dc, text, new Point(plot.Left + i * slot + slot / 2, plot.Bottom + AxisHeight / 2 + 2), LabelBrush, center: true);
        }

        if (hover is int k && k < bins.Count)
        {
            var b = bins[k];
            var lines = b.Down + b.Up == 0 ? new[] { "Nothing" } : new[]
            {
                $"Downloaded {Units.Data(b.Down)}", $"In the background {Units.Data(b.BgDown)}", $"Uploaded {Units.Data(b.Up)}",
            };
            Hover(dc, plot, plot.Left + k * slot + slot / 2, Title(b.Start), lines);
        }
    }

    private string? AxisLabel(DateTime t, int i) => Unit switch
    {
        ReportRange.Day => i % 6 == 0 ? t.ToString("h tt") : null,
        ReportRange.Week => t.ToString("ddd"),
        ReportRange.Year => t.ToString("MMM"),
        _ => i % 5 == 0 ? t.ToString("d MMM") : null,
    };

    private string Title(DateTime t) => Unit switch
    {
        ReportRange.Day => $"{t:h tt} – {t.AddHours(1):h tt}",
        ReportRange.Year => t.ToString("MMMM yyyy"),
        _ => t.ToString("dddd, d MMMM"),
    };
}

/// <summary>The connection's speed over the last minute: download as a filled line, upload as a line.</summary>
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
