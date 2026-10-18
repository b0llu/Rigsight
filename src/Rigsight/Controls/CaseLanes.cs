using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Models;

namespace Rigsight.Controls;

/// <summary>
/// The minutes before a crash, everything recorded about the PC in them on one time line: a lane for each kind of
/// reading (temperature, load, power, memory, fans, drives, network), strips for the app in front and the apps working
/// the CPU and the GPU hardest, a mark across all of them for what happened (an app opened, the crash), and the time
/// along the foot. The lanes share out whatever height the control is given, so they end level with the box beside
/// them. Pointing at a minute shows its readings in that box (<see cref="CrashCase.Readout"/>), not in a box over the
/// lines. Minutes with nothing recorded are a break in every line, never a line drawn across.
/// </summary>
public sealed class CaseLanes : FrameworkElement
{
    // Top: two rows of words over the lanes, the crash's, then the apps that opened (see the marks below).
    private const double LabelWidth = 112, LabelGap = 10, Top = 32, StripHeight = 26, StripGap = 12, AxisHeight = 22, MinLane = 54;
    private const double CrashRow = 1, AppRow = 16;

    public static readonly DependencyProperty ViewProperty = DependencyProperty.Register(
        nameof(View), typeof(CaseView), typeof(CaseLanes), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, _) => ((CaseLanes)d)._pointed = -1));

    /// <summary>What to draw: the minutes around the crash, or its close-up.</summary>
    public CaseView? View { get => (CaseView?)GetValue(ViewProperty); set => SetValue(ViewProperty, value); }

    private int _pointed = -1;

    /// <summary>Never shorter than every lane at its least: taller when the box beside it is.</summary>
    protected override Size MeasureOverride(Size available)
    {
        if (View is not { HasChart: true } c) return new Size(0, 0);
        return new Size(0, Top + c.Lanes.Count * MinLane + c.Strips.Count * (StripHeight + StripGap) + AxisHeight);
    }

    private Rect Plot => new(LabelWidth + LabelGap, Top, Math.Max(1, ActualWidth - LabelWidth - LabelGap - EndGap), Math.Max(1, ActualHeight - Top));

    /// <summary>Room at the right for the crash's line with the word centred over it.</summary>
    private const double EndGap = 18;

    /// <summary>Where the time line ends: the view's own end (the crash itself, where nothing comes after it).</summary>
    private static DateTime End(CaseView c) => c.ChartTo;

    private int IndexAt(double x)
    {
        if (View is not { HasChart: true } c || c.Minutes.Count == 0) return -1;
        var plot = Plot;
        if (x < plot.Left - 8 || x > plot.Right + 8) return -1;
        double span = Math.Max(60, (End(c) - c.ChartFrom).TotalSeconds);
        var at = c.ChartFrom.AddSeconds(Math.Clamp((x - plot.Left) / plot.Width, 0, 1) * span);
        int best = -1;
        double nearest = double.MaxValue;
        for (int i = 0; i < c.Minutes.Count; i++)
        {
            double d = Math.Abs((c.Minutes[i].Time - at).TotalSeconds);
            if (d < nearest) (nearest, best) = (d, i);
        }
        return nearest <= c.BreakSec ? best : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = IndexAt(e.GetPosition(this).X);
        if (i == _pointed) return;
        _pointed = i;
        View?.Readout?.Show(i < 0 ? null : i);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_pointed < 0) return;
        _pointed = -1;
        View?.Readout?.Show(null);
        InvalidateVisual();
    }

    private Color ColorOf(string key) => TryFindResource(key) is SolidColorBrush b ? b.Color : ChartPaint.Cpu;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // the whole area answers the pointer
        if (View is not { HasChart: true } c || w < 260) return;

        double plotLeft = LabelWidth + LabelGap, plotRight = w - EndGap, plotWidth = plotRight - plotLeft;
        double span = Math.Max(60, (End(c) - c.ChartFrom).TotalSeconds);
        double X(DateTime t) => plotLeft + plotWidth * Math.Clamp((t - c.ChartFrom).TotalSeconds / span, 0, 1);

        double stripsHeight = c.Strips.Count * (StripHeight + StripGap);
        double lanesHeight = Math.Max(c.Lanes.Count * MinLane, h - Top - stripsHeight - AxisHeight);
        double laneHeight = c.Lanes.Count == 0 ? 0 : lanesHeight / c.Lanes.Count;

        // ── Lanes ──
        double y = Top;
        for (int li = 0; li < c.Lanes.Count; li++)
        {
            var lane = c.Lanes[li];
            if (li > 0) dc.DrawRectangle(ChartPaint.Grid, null, new Rect(0, Math.Round(y), w, 1));
            // The label: the lane's name, then a dot and a word for each line.
            double ly = y + 8;
            var name = ChartPaint.Format(this, lane.Name, 12.5, ChartPaint.TextBrush, bold: true);
            dc.DrawText(name, new Point(0, ly));
            ly += name.Height + 1;
            foreach (var s in lane.Series)
            {
                var label = ChartPaint.Format(this, s.Label, 11.5, ChartPaint.Muted);
                if (ly + label.Height > y + laneHeight - 2) break;
                dc.DrawRoundedRectangle(ChartPaint.Brush(ColorOf(s.Brush)), null, new Rect(0, ly + label.Height / 2 - 4, 8, 8), 2, 2);
                dc.DrawText(label, new Point(14, ly));
                ly += label.Height;
            }

            var area = new Rect(plotLeft, y + 6, plotWidth, Math.Max(10, laneHeight - 12));
            double Y(double v) => area.Bottom - area.Height * Math.Clamp(v / Math.Max(1e-9, lane.Max), 0, 1);
            foreach (var s in lane.Series)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    DateTime? last = null;
                    for (int i = 0; i < c.Minutes.Count; i++)
                    {
                        if (s.Values[i] is not double v) { last = null; continue; }
                        var p = new Point(X(c.Minutes[i].Time), Y(v));
                        if (last is { } before && (c.Minutes[i].Time - before).TotalSeconds <= c.BreakSec) g.LineTo(p, true, true);
                        else g.BeginFigure(p, false, false);
                        last = c.Minutes[i].Time;
                    }
                }
                geometry.Freeze();
                var pen = new Pen(ChartPaint.Brush(ColorOf(s.Brush)), 1.8) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
            // The scale, at the right: the top and the foot.
            var top = ChartPaint.Format(this, $"{Number(lane.Max)}{lane.Unit}", 10.5, ChartPaint.Label);
            var foot = ChartPaint.Format(this, $"0{lane.Unit}", 10.5, ChartPaint.Label);
            Backed(dc, top, new Point(plotLeft + 3, area.Top));
            Backed(dc, foot, new Point(plotLeft + 3, area.Bottom - foot.Height));

            // Stretches with nothing recorded are left empty, with why in words in the first lane. (A thin strip at
            // each lane's foot read as readings at zero: "0°", "0 W" for half an hour.)
            foreach (var gap in c.Gaps)
            {
                double a = X(gap.From), b = X(gap.To);
                if (b - a < 3 || li != 0) continue;
                var why = ChartPaint.Format(this, gap.Why, 11.5, ChartPaint.Muted);
                if (why.Width + 12 <= b - a) dc.DrawText(why, new Point((a + b) / 2 - why.Width / 2, area.Top + area.Height / 2 - why.Height / 2));
            }
            y += laneHeight;
        }

        // ── Strips ──
        foreach (var strip in c.Strips)
        {
            dc.DrawRectangle(ChartPaint.Grid, null, new Rect(0, Math.Round(y), w, 1));
            double sy = y + StripGap / 2;
            var label = ChartPaint.Format(this, strip.Name, 11.5, ChartPaint.Muted);
            dc.DrawText(label, new Point(0, sy + (StripHeight - label.Height) / 2));
            foreach (var band in strip.Bands)
            {
                double a = X(band.From), b = X(band.To);
                var rect = new Rect(a, sy, Math.Max(0, b - a - 2), StripHeight);
                if (rect.Width < 2) continue;
                Brush fill = band.Color is { } hex ? ChartPaint.Brush((Color)ColorConverter.ConvertFromString(hex)) : ChartPaint.Track;
                dc.DrawRoundedRectangle(fill, null, rect, 6, 6);
                var text = ChartPaint.Format(this, band.App ?? "None clearly", 11.5,
                    band.Color is { } c2 ? OnColor((Color)ColorConverter.ConvertFromString(c2)) : ChartPaint.Label, bold: band.Color is not null);
                if (text.Width + 14 > rect.Width) continue;
                dc.PushClip(new RectangleGeometry(rect));
                dc.DrawText(text, new Point(rect.Left + 7, sy + (StripHeight - text.Height) / 2));
                dc.Pop();
            }
            y += StripHeight + StripGap;
        }

        // ── The time along the foot: every so often (ten minutes, or one up close), and the crash's own in the warning colour ──
        dc.DrawRectangle(ChartPaint.Grid, null, new Rect(0, Math.Round(y), w, 1));
        var crash = c.Crash ?? c.ChartTo;
        var ticks = new List<DateTime>();
        var near = TimeSpan.FromTicks(c.TickEvery.Ticks * 7 / 10);
        for (var t = c.ChartFrom; t <= End(c) - near / 2; t += c.TickEvery)
            if ((t - crash).Duration() >= near) ticks.Add(t); // none crowding the crash's own time
        foreach (var t in ticks)
        {
            var label = ChartPaint.Format(this, t.ToString(c.IsCloseUp ? "h:mm:ss" : "h:mm tt", CultureInfo.CurrentCulture), 11, ChartPaint.Label);
            double x = t == c.ChartFrom ? plotLeft : X(t) - label.Width / 2;
            dc.DrawText(label, new Point(Math.Max(plotLeft, x), y + 5));
        }
        var end = ChartPaint.Format(this, crash.ToString(c.IsCloseUp ? "h:mm:ss tt" : "h:mm tt", CultureInfo.CurrentCulture), 11, ChartPaint.Hot, bold: true);
        dc.DrawText(end, new Point(Math.Min(plotRight - end.Width, X(crash) - end.Width / 2), y + 5));

        // ── Marks, across every lane and strip ──
        double markBottom = y;
        // The crash: its line, and its word on the top row; what came after it, lightly shaded, with "After" beside it.
        foreach (var mark in c.Marks.Where(m => m.IsCrash))
        {
            double x = X(mark.At);
            var label = ChartPaint.Format(this, mark.Label, 10.5, ChartPaint.Hot);
            double lx = Math.Max(plotLeft, Math.Min(x - label.Width / 2, w - label.Width)); // Math.Clamp throws where the word is wider than the room
            if ((End(c) - mark.At).TotalSeconds > c.BreakSec && plotRight - x > 40)
            {
                dc.DrawRectangle(ChartPaint.After, null, new Rect(x, Top, plotRight - x, markBottom - Top));
                var after = ChartPaint.Format(this, "After", 10.5, ChartPaint.Muted);
                if (lx + label.Width + 10 + after.Width <= w) dc.DrawText(after, new Point(lx + label.Width + 10, CrashRow));
            }
            dc.DrawRectangle(ChartPaint.Hot, null, new Rect(x - 1, Top, 2, markBottom - Top));
            dc.DrawText(label, new Point(lx, CrashRow));
        }
        // Apps that opened: a dashed line each, and their words on a row of their own under the crash's. Lines close
        // together share one label ("Steam, Discord and Epic Games opened", or "3 apps opened" where the names don't
        // fit), so no line is ever left without saying what it is (the user: "white lines but no explanation").
        var dashed = new Pen(ChartPaint.Label, 1) { DashStyle = new DashStyle([3, 3], 0) };
        dashed.Freeze();
        var apps = c.Marks.Where(m => !m.IsCrash).OrderBy(m => m.At).ToList();
        foreach (var m in apps)
        {
            double x = Math.Round(X(m.At)) + 0.5;
            dc.DrawLine(dashed, new Point(x, Top), new Point(x, markBottom));
        }
        var groups = new List<List<CaseMark>>();
        foreach (var m in apps)
            if (groups.Count > 0 && X(m.At) - X(groups[^1][0].At) < 90) groups[^1].Add(m);
            else groups.Add([m]);
        double free = plotLeft;
        foreach (var g in groups)
        {
            double mid = (X(g[0].At) + X(g[^1].At)) / 2;
            var names = g.Select(m => m.Label).Distinct().ToList();
            string full = Core.Wording.JoinAnd(names) + " opened";
            var label = ChartPaint.Format(this, full, 10.5, ChartPaint.Muted);
            // Not Math.Clamp: the room left after the label before can be narrower than this one, and that throws.
            double lx = Math.Max(free, Math.Min(mid - label.Width / 2, w - label.Width));
            if (label.Width > 220 || lx + label.Width > w)
            {
                label = ChartPaint.Format(this, names.Count == 1 ? "An app opened" : $"{names.Count} apps opened", 10.5, ChartPaint.Muted);
                lx = Math.Max(free, Math.Min(mid - label.Width / 2, w - label.Width));
                if (lx + label.Width > w) continue; // no room left at all on the right
            }
            dc.DrawText(label, new Point(lx, AppRow));
            free = lx + label.Width + 10;
        }

        // ── The minute pointed at ──
        if (_pointed >= 0 && _pointed < c.Minutes.Count)
        {
            double x = X(c.Minutes[_pointed].Time);
            dc.DrawRectangle(ChartPaint.Cursor, null, new Rect(Math.Round(x), Top, 1, markBottom - Top));
        }
    }

    /// <summary>A scale's figure on a patch of the card's own colour, so a line under it doesn't run through it.</summary>
    private static void Backed(DrawingContext dc, FormattedText text, Point at)
    {
        dc.DrawRoundedRectangle(ChartPaint.Backdrop, null, new Rect(at.X - 3, at.Y, text.Width + 6, text.Height), 3, 3);
        dc.DrawText(text, at);
    }

    private static string Number(double v) => v >= 1000 ? v.ToString("N0", CultureInfo.CurrentCulture) : v.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>Black on a light colour, white on a deep one.</summary>
    private static Brush OnColor(Color c) => Services.Accents.OnAccent(c) == Colors.Black ? Brushes.Black : Brushes.White;
}
