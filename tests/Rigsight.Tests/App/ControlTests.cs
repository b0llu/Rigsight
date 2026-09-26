using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Data;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>Lays out and draws a control off screen, the way a window would.</summary>
internal static class Draw
{
    public static RenderTargetBitmap Render(FrameworkElement e, double w, double h)
    {
        e.Measure(new Size(w, h));
        e.Arrange(new Rect(0, 0, w, h));
        e.UpdateLayout();
        var bmp = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(w)), Math.Max(1, (int)Math.Ceiling(h)), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(e);
        return bmp;
    }

    /// <summary>Redraws after a change (the control only asked to be redrawn).</summary>
    public static RenderTargetBitmap Again(FrameworkElement e) => Render(e, e.ActualWidth, e.ActualHeight);

    /// <summary>Pixels with anything drawn on them (the controls' backgrounds are transparent).</summary>
    public static int Inked(BitmapSource bmp)
    {
        var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);
        int n = 0;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] > 0) n++;
        return n;
    }

    public static void Set(object target, string field, object? value) =>
        target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    public static T Get<T>(object target, string field) =>
        (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    public static HistoryBuffer Buffer(int count, long start, long stepMs, Func<int, double> value, int capacity = 0)
    {
        var b = new HistoryBuffer(Math.Max(capacity, count));
        for (int i = 0; i < count; i++) b.Add(start + i * stepMs, value(i));
        return b;
    }
}

/// <summary>The custom-drawn charts and the grid of tiles, with no data, one point, lots of data, and NaNs.</summary>
[Collection("UI")]
public sealed class ControlTests
{
    private static readonly long Now = new DateTimeOffset(DateTime.Now).ToUnixTimeMilliseconds();

    // ── Chart geometry ───────────────────────────────────────────────────

    private static readonly Rect Plot = new(40, 6, 800, 200);

    private static (StreamGeometry Line, StreamGeometry Fill)? Build(HistoryBuffer b, long from, long to, double min = 0, double max = 100,
        long until = long.MaxValue, (long, double)? joinTo = null) => ChartGeometry.Build(b, from, to, Plot, min, max, v => v, until, joinTo);

    private static int Figures(Geometry g) => PathGeometry.CreateFromGeometry(g).Figures.Count;

    private static int Points(Geometry g) => PathGeometry.CreateFromGeometry(g).Figures.Sum(f => 1 + f.Segments.Sum(s => s switch
    {
        PolyLineSegment p => p.Points.Count,
        LineSegment => 1,
        _ => 0,
    }));

    [Fact]
    public void Geometry_needs_data_and_a_range()
    {
        Ui.Run(() =>
        {
            var b = Draw.Buffer(10, Now, 1000, i => i);
            Assert.Null(Build(new HistoryBuffer(5), Now, Now + 1000));
            Assert.Null(Build(b, Now, Now));          // no time span
            Assert.Null(Build(b, Now + 5, Now));
            Assert.Null(Build(b, Now, Now + 10_000, 50, 50)); // no value span
            Assert.Null(Build(Draw.Buffer(5, Now, 1000, _ => double.NaN), Now, Now + 5000));
        });
    }

    [Fact]
    public void One_point_is_a_line_with_nothing_to_fill()
    {
        Ui.Run(() =>
        {
            var g = Build(Draw.Buffer(1, Now, 1000, _ => 50), Now - 1000, Now + 1000)!.Value;
            Assert.Equal(1, Figures(g.Line));
            Assert.Equal(0, Figures(g.Fill));
            Assert.True(g.Line.IsFrozen && g.Fill.IsFrozen);
        });
    }

    [Fact]
    public void Gaps_break_the_line()
    {
        Ui.Run(() =>
        {
            var b = Draw.Buffer(30, Now, 1000, i => i is 10 or 20 ? double.NaN : 50);
            var g = Build(b, Now, Now + 30_000)!.Value;
            Assert.Equal(3, Figures(g.Line));
            Assert.Equal(3, Figures(g.Fill));
        });
    }

    [Fact]
    public void Everything_drawn_stays_inside_the_plot()
    {
        Ui.Run(() =>
        {
            var b = Draw.Buffer(500, Now, 1000, i => i % 7 == 0 ? 1e6 : i % 5 == 0 ? -1e6 : 50);
            var g = Build(b, Now, Now + 500_000)!.Value;
            var bounds = g.Line.Bounds;
            Assert.InRange(bounds.Top, Plot.Top - 0.01, Plot.Bottom);
            Assert.InRange(bounds.Bottom, Plot.Top, Plot.Bottom + 0.01);
            Assert.InRange(bounds.Left, Plot.Left - 0.01, Plot.Right + 0.01);
        });
    }

    [Fact]
    public void Samples_in_the_same_pixel_are_averaged()
    {
        Ui.Run(() =>
        {
            var b = Draw.Buffer(100_000, Now, 10, i => i % 100);
            var g = Build(b, Now, Now + 1_000_000)!.Value;
            // One point per pixel across 800 pixels, not 100 000.
            Assert.InRange(Points(g.Line), 700, 900);
        });
    }

    [Fact]
    public void Samples_past_until_are_left_to_the_next_series_and_joined_to_it()
    {
        Ui.Run(() =>
        {
            var minutes = Draw.Buffer(60, Now - 3600_000, 60_000, _ => 40);
            long handover = Now - 1800_000;
            var cut = Build(minutes, Now - 3600_000, Now, until: handover)!.Value;
            Assert.True(cut.Line.Bounds.Right < Plot.Left + Plot.Width / 2 + 2);
            var joined = Build(minutes, Now - 3600_000, Now, until: handover, joinTo: (handover + 60_000, 45))!.Value;
            Assert.True(joined.Line.Bounds.Right > cut.Line.Bounds.Right);
            // Not across a real gap (the PC off in between).
            var far = Build(minutes, Now - 3600_000, Now, until: handover, joinTo: (handover + 600_000, 45))!.Value;
            Assert.Equal(cut.Line.Bounds.Right, far.Line.Bounds.Right, 3);
            var nan = Build(minutes, Now - 3600_000, Now, until: handover, joinTo: (handover + 60_000, double.NaN))!.Value;
            Assert.Equal(cut.Line.Bounds.Right, nan.Line.Bounds.Right, 3);
        });
    }

    [Fact]
    public void Range_of_values_in_a_window()
    {
        var b = Draw.Buffer(10, Now, 1000, i => i == 3 ? double.NaN : i * 10);
        Assert.Equal((0.0, 90.0), ChartGeometry.Range(b, Now, v => v));
        Assert.Equal((50.0, 90.0), ChartGeometry.Range(b, Now + 5000, v => v));
        Assert.Equal((20.0, 40.0), ChartGeometry.Range(b, Now + 2000, v => v, Now + 4000));
        Assert.Equal((32.0, 194.0), ChartGeometry.Range(b, Now, v => v * 9 / 5 + 32));
        Assert.Null(ChartGeometry.Range(b, Now + 100_000, v => v));
        Assert.Null(ChartGeometry.Range(new HistoryBuffer(4), Now, v => v));
        Assert.Null(ChartGeometry.Range(Draw.Buffer(3, Now, 1, _ => double.NaN), Now, v => v));
    }

    [Fact]
    public void Fade_fill_goes_from_the_colour_to_clear()
    {
        Ui.Run(() =>
        {
            var fill = (LinearGradientBrush)ChartGeometry.FadeFill(Colors.Red, 0.5);
            Assert.True(fill.IsFrozen);
            Assert.Equal(127, fill.GradientStops[0].Color.A);
            Assert.Equal(0, fill.GradientStops[1].Color.A);
            Assert.Equal(Colors.Red.R, fill.GradientStops[1].Color.R);
        });
    }

    [Fact]
    public void Chart_paint_follows_the_theme_and_falls_back_to_grey()
    {
        try
        {
            Ui.Run(() =>
            {
                ThemeManager.Apply(ThemeManager.Light);
                Assert.Equal((Color)Application.Current.FindResource("HotColor"), ChartPaint.Hot.Color);
                Assert.Equal((Color)Application.Current.FindResource("CpuColor"), ChartPaint.Cpu);
                Assert.Equal(Color.FromRgb(7, 7, 7), ChartPaint.Res("NoSuchColor", 7));
                Assert.Equal(Color.FromRgb(9, 9, 9), ChartPaint.Res("HotBrush", 9)); // a brush isn't a colour
                Assert.True(ChartPaint.Grid.IsFrozen && ChartPaint.TooltipBorder.IsFrozen && ChartPaint.Cursor.IsFrozen);
                Assert.Equal(0.4, ChartPaint.Cursor.Opacity, 6);
            });
        }
        finally
        {
            Ui.Run(() => ThemeManager.Apply(ThemeManager.Dark));
        }
        Ui.Run(() => Assert.Equal((Color)Application.Current.FindResource("HotColor"), ChartPaint.Hot.Color));
    }

    [Fact]
    public void Info_boxes_stay_inside_their_bounds()
    {
        Ui.Run(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var lines = new List<(string, Brush, bool)> { ("A long first line of text", ChartPaint.TextBrush, true), ("second", ChartPaint.Muted, false) };
                ChartPaint.InfoBox(dc, visual, lines, new Point(390, 290), new Rect(0, 0, 400, 300));
                ChartPaint.Text(dc, visual, "x", new Point(0, 0), 11, ChartPaint.Label, ChartPaint.Align.Right);
                ChartPaint.Text(dc, visual, "y", new Point(0, 0), 11, ChartPaint.Label, ChartPaint.Align.Center, bold: true, middle: false);
            }
            var bounds = visual.ContentBounds;
            Assert.InRange(bounds.Right, 0, 401);
            Assert.InRange(bounds.Bottom, 0, 301);
        });
    }

    // ── Sparkline ────────────────────────────────────────────────────────

    [Fact]
    public void Sparkline_draws_only_with_two_readings_or_more()
    {
        Ui.Run(() =>
        {
            var s = new Sparkline { Stroke = Brushes.Orange, WindowSeconds = 60 };
            Assert.Equal(0, Draw.Inked(Draw.Render(s, 120, 30)));
            s.Source = Draw.Buffer(1, Now, 1000, _ => 50);
            Assert.Equal(0, Draw.Inked(Draw.Again(s)));
            s.Source = Draw.Buffer(2, Now, 1000, i => 50 + i);
            Assert.True(Draw.Inked(Draw.Again(s)) > 0);
            s.Source = Draw.Buffer(30, Now, 1000, _ => double.NaN);
            Assert.Equal(0, Draw.Inked(Draw.Again(s)));
            // Too small to draw in.
            s.Source = Draw.Buffer(30, Now, 1000, i => i);
            Assert.Equal(0, Draw.Inked(Draw.Render(s, 3, 3)));
        });
    }

    [Fact]
    public void Sparkline_handles_flat_lines_fixed_ranges_and_short_history()
    {
        Ui.Run(() =>
        {
            var s = new Sparkline { Source = Draw.Buffer(60, Now, 1000, _ => 42), WindowSeconds = 60 };
            Assert.True(Draw.Inked(Draw.Render(s, 120, 30)) > 0);
            s.Minimum = 0;
            s.Maximum = 100;
            s.Source = Draw.Buffer(60, Now, 1000, i => i * 3 - 50); // beyond both ends
            Assert.True(Draw.Inked(Draw.Again(s)) > 0);
            s.Minimum = s.Maximum = null;
            s.FitToData = true;
            s.WindowSeconds = 6 * 3600;
            s.Source = Draw.Buffer(5, Now, 120_000, i => 30 + i);
            Assert.True(Draw.Inked(Draw.Again(s)) > 0);
            s.Stroke = new LinearGradientBrush(Colors.Red, Colors.Blue, 0); // not a plain colour: drawn in the default
            Assert.True(Draw.Inked(Draw.Again(s)) > 0);
        });
    }

    // ── Line chart ───────────────────────────────────────────────────────

    private static ChartSeries Series(string label, int live, int minutes, Func<int, double> value, string color = "CpuColor")
    {
        var sensor = new SensorItem(new SensorMeta { Id = "/t/" + label, Name = label, Kind = SensorKind.Temperature }, "CPU", "Cpu");
        for (int i = 0; i < live; i++) sensor.Push(Now - (live - i) * 1000L, (float)value(i));
        var series = new ChartSeries(label, sensor, color, m => m.CpuTemp);
        long start = Now / 1000 - 24 * 3600;
        series.LoadMinutes(Enumerable.Range(0, minutes).Select(i => new Rigsight.Core.Data.SystemMinute { Ts = start + i * 60L, CpuTemp = value(i) }));
        return series;
    }

    [Theory]
    [InlineData(300)]
    [InlineData(3600)]
    [InlineData(21600)]
    [InlineData(86400)]
    [InlineData(0)]
    public void Line_chart_draws_every_window(int window)
    {
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var chart = new LineChart { WindowSeconds = window, Series = [Series("CPU", 600, 1440, i => 40 + i % 30), Series("GPU", 600, 1440, i => 50 + i % 20, "GpuColor")] };
            Assert.True(Draw.Inked(Draw.Render(chart, 900, 300)) > 1000);
        });
        Ui.AssertNoProblems($"line chart, {window} s");
    }

    [Fact]
    public void Line_chart_with_nothing_says_it_is_collecting()
    {
        Ui.Run(() =>
        {
            var chart = new LineChart { WindowSeconds = 300 };
            int text = Draw.Inked(Draw.Render(chart, 600, 200)); // "Collecting data…"
            Assert.True(text > 0);
            chart.Series = [Series("CPU", 0, 0, _ => 0)];
            Assert.Equal(text, Draw.Inked(Draw.Again(chart)));
            chart.Series = [Series("CPU", 30, 0, _ => double.NaN)];
            Assert.Equal(text, Draw.Inked(Draw.Again(chart)));
            // Smaller than its axes: nothing at all.
            Assert.Equal(0, Draw.Inked(Draw.Render(chart, 50, 30)));
        });
    }

    [Fact]
    public void Line_chart_with_one_reading()
    {
        Ui.Run(() =>
        {
            var chart = new LineChart { WindowSeconds = 300, Series = [Series("CPU", 1, 0, _ => 55)] };
            Assert.True(Draw.Inked(Draw.Render(chart, 600, 200)) > 0);
        });
    }

    [Fact]
    public void Line_chart_of_an_earlier_day_uses_minute_history_only()
    {
        Ui.Run(() =>
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var chart = new LineChart { WindowSeconds = 0, Day = yesterday, Series = [Series("CPU", 60, 1440, i => 45)] };
            Assert.True(Draw.Inked(Draw.Render(chart, 900, 300)) > 0);
            // A day with nothing recorded says so.
            chart.Day = DateTime.Today.AddDays(-30);
            Assert.True(Draw.Inked(Draw.Again(chart)) > 0);
            Assert.True(Draw.Get<bool>(chart, "_pastDay"));
        });
    }

    [Fact]
    public void Line_chart_hover_box_in_every_corner()
    {
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var chart = new LineChart { WindowSeconds = 300, Series = [Series("CPU", 300, 0, i => 40 + i % 10), Series("GPU", 300, 0, i => double.NaN, "GpuColor")] };
            int plain = Draw.Inked(Draw.Render(chart, 700, 250));
            foreach (var x in new[] { 45.0, 350, 690, -5, 5000 })
            {
                Draw.Set(chart, "_hoverX", x);
                chart.InvalidateVisual();
                var inked = Draw.Inked(Draw.Again(chart));
                if (x is > 42 and < 694) Assert.True(inked > plain, $"no hover box at {x}");
            }
            Draw.Set(chart, "_hoverX", null);
        });
        Ui.AssertNoProblems("line chart hover");
    }

    [Fact]
    public void Line_chart_in_fahrenheit()
    {
        try
        {
            Ui.Run(() =>
            {
                Units.Fahrenheit = true;
                var chart = new LineChart { WindowSeconds = 3600, Series = [Series("CPU", 600, 120, i => 40 + i % 30)] };
                Assert.True(Draw.Inked(Draw.Render(chart, 900, 300)) > 0);
            });
        }
        finally
        {
            Ui.Run(() => Units.Fahrenheit = false);
        }
    }

    // ── Gauge ────────────────────────────────────────────────────────────

    private static double Fraction(ArcGauge g) =>
        (double)g.GetValue((DependencyProperty)typeof(ArcGauge).GetField("FractionProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!);

    [Theory]
    [InlineData(50.0, 0.5)]
    [InlineData(0.0, 0.0)]
    [InlineData(-20.0, 0.0)]
    [InlineData(150.0, 1.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(null, 0.0)]
    public void Gauge_fraction(double? value, double fraction)
    {
        Ui.Run(() =>
        {
            var g = new ArcGauge { Value = 10 };
            g.Value = value;
            Assert.Equal(fraction, Fraction(g), 6); // not on screen: set straight away, no animation
            Draw.Render(g, 120, 120);
        });
    }

    [Fact]
    public void Gauge_range_and_tiny_changes()
    {
        Ui.Run(() =>
        {
            var g = new ArcGauge { Minimum = 20, Maximum = 120, Value = 70 };
            Assert.Equal(0.5, Fraction(g), 6);
            g.Value = 70.2; // under a degree of arc: not worth redrawing
            Assert.Equal(0.5, Fraction(g), 6);
            g.Maximum = 20; // no range
            Assert.Equal(0, Fraction(g), 6);
            g.Maximum = 120;
            Assert.Equal(0.502, Fraction(g), 3);
        });
    }

    [Fact]
    public void Gauge_draws_its_track_and_arc_at_any_size()
    {
        Ui.Run(() =>
        {
            var g = new ArcGauge { Value = 0 };
            int track = Draw.Inked(Draw.Render(g, 160, 160));
            Assert.True(track > 0);
            g.Value = 90;
            Assert.True(Draw.Inked(Draw.Again(g)) > track);
            g.Thickness = 60; // thicker than the gauge is wide
            Draw.Render(g, 40, 40);
            Assert.Equal(0, Draw.Inked(Draw.Render(g, 0, 0)));
            g.Brush = new LinearGradientBrush(Colors.Red, Colors.Blue, 0);
            Draw.Render(g, 100, 60);
        });
    }

    // ── Daily bars ───────────────────────────────────────────────────────

    private static List<DayBucket> Days(int n, Func<DateTime, DateTime> step, DateTime first, Func<int, double> active)
    {
        var list = new List<DayBucket>();
        var d = first;
        for (int i = 0; i < n; i++, d = step(d))
        {
            var b = new DayBucket { Day = d, ActiveSec = active(i), TopApp = "App", CpuTempMax = 70, GpuTempMax = 60 };
            b.ActiveByCategory[AppCategory.Game] = active(i) * 0.6;
            b.ActiveByCategory[AppCategory.Browser] = active(i) * 0.4;
            list.Add(b);
        }
        return list;
    }

    public static TheoryData<string> BarShapes => ["hourly", "week", "month", "year", "years", "one", "empty", "zero", "huge", "nan", "negative"];

    [Theory]
    [MemberData(nameof(BarShapes))]
    public void Daily_bars_for_every_shape_of_period(string shape)
    {
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var today = DateTime.Today;
            List<DayBucket>? days = shape switch
            {
                "hourly" => Days(24, d => d.AddHours(1), today, i => i * 100),
                "week" => Days(7, d => d.AddDays(1), today.AddDays(-6), i => i * 3000),
                "month" => Days(31, d => d.AddDays(1), today.AddDays(-30), i => i * 1000),
                "year" => Days(12, d => d.AddMonths(1), new DateTime(today.Year, 1, 1), i => i * 100_000),
                "years" => Days(30, d => d.AddMonths(1), new DateTime(today.Year - 2, 1, 1), i => i * 100_000),
                "one" => Days(1, d => d, today, _ => 1800),
                "empty" => [],
                "zero" => Days(7, d => d.AddDays(1), today.AddDays(-6), _ => 0),
                "huge" => Days(3650, d => d.AddDays(1), today.AddDays(-3649), i => 86_400),
                "nan" => Days(7, d => d.AddDays(1), today.AddDays(-6), i => i == 3 ? double.NaN : 3600),
                "negative" => Days(7, d => d.AddDays(1), today.AddDays(-6), i => -3600),
                _ => null,
            };
            var bars = new DailyBars { Days = days };
            var drawn = Draw.Inked(Draw.Render(bars, 800, 220));
            if (shape is "hourly" or "week" or "month" or "year" or "one") Assert.True(drawn > 0);
            if (shape == "empty") Assert.Equal(0, drawn);
            // Hovering a bar shows its day.
            if (days is { Count: > 0 })
            {
                Draw.Set(bars, "_hover", days.Count / 2);
                bars.InvalidateVisual();
                Assert.True(Draw.Inked(Draw.Again(bars)) >= drawn);
            }
            Assert.Equal(0, Draw.Inked(Draw.Render(bars, 40, 40))); // too small
        });
        Ui.AssertNoProblems($"daily bars, {shape}");
    }

    // ── Day timeline ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("today")]
    [InlineData("yesterday")]
    [InlineData("empty")]
    [InlineData("busy")]
    [InlineData("nan")]
    [InlineData("hovered")]
    public void Day_timeline(string shape)
    {
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var day = shape == "yesterday" ? DateTime.Today.AddDays(-1) : DateTime.Today;
            var segments = new List<TimelineSegment>();
            var temps = new List<TempPoint>();
            if (shape != "empty")
            {
                int n = shape == "busy" ? 5000 : 40;
                for (int i = 0; i < n; i++)
                {
                    var start = day.AddMinutes(i * 1440.0 / n);
                    segments.Add(new TimelineSegment { Start = start, End = start.AddMinutes(1440.0 / n), AppId = i % 5 == 0 ? null : i,
                        App = $"App {i % 7}", Category = (AppCategory)(i % 9), Away = i % 3 == 0 });
                }
                for (int m = 0; m < 1440; m += shape == "busy" ? 1 : 10)
                    temps.Add(new TempPoint(day.AddMinutes(m), shape == "nan" && m % 20 == 0 ? double.NaN : 40 + m % 30, m % 50 == 0 ? null : 50));
            }
            var t = new DayTimeline { Day = day, Segments = segments, Temps = temps, RecordedUntil = DateTime.Now };
            int drawn = Draw.Inked(Draw.Render(t, 1000, 200));
            Assert.True(drawn > 0); // the band, the grid and the hours at least
            if (shape == "hovered")
            {
                foreach (var x in new[] { 45.0, 500, 990, 1100 })
                {
                    Draw.Set(t, "_hoverX", x);
                    t.InvalidateVisual();
                    Draw.Again(t);
                }
            }
            Assert.Equal(0, Draw.Inked(Draw.Render(t, 60, 60)));
        });
        Ui.AssertNoProblems($"day timeline, {shape}");
    }

    [Fact]
    public void Day_timeline_over_a_range_past_midnight()
    {
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var from = DateTime.Today.AddDays(-2).AddHours(8);
            var to = from.AddHours(19); // 3 AM
            var segments = Enumerable.Range(0, 19 * 6).Select(i => new TimelineSegment
            {
                Start = from.AddMinutes(i * 10), End = from.AddMinutes(i * 10 + 10), AppId = i % 4, App = $"App {i % 4}", Category = (AppCategory)(i % 9),
            }).ToList();
            var temps = Enumerable.Range(0, 19 * 60).Select(m => new TempPoint(from.AddMinutes(m), 40 + m % 30, 50)).ToList();
            var t = new DayTimeline { Day = from, End = to, Segments = segments, Temps = temps, RecordedUntil = DateTime.Now };
            Assert.True(Draw.Inked(Draw.Render(t, 1000, 200)) > 0);
            foreach (var x in new[] { 10.0, 500, 990 })
            {
                Draw.Set(t, "_hoverX", x);
                t.InvalidateVisual();
                Draw.Again(t);
            }
            // A range still going on.
            t.Day = DateTime.Today.AddDays(-1);
            t.End = ReportBuilder.HourEnd(DateTime.Now);
            Assert.True(Draw.Inked(Draw.Render(t, 1000, 200)) > 0);
        });
        Ui.AssertNoProblems("day timeline over a range");
    }

    // ── Crash strip ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(365)]
    [InlineData(730)]
    [InlineData(3650)]
    public void Crash_strip_labels_and_bars_for_any_length(int days)
    {
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var from = DateTime.Today.AddDays(1 - days);
            var rows = Enumerable.Range(0, Math.Min(days, 200)).Select(i => Crashes.App(from.AddDays(i * days / Math.Min(days, 200)).AddHours(12), $"a{i % 5}.exe"));
            var changes = new[] { new SystemChange(from.AddHours(3), ChangeKind.Driver, "NVIDIA graphics driver") };
            var list = CrashStrip.BuildDays(from, DateTime.Today.AddDays(1), CrashGroup.Build(rows), changes);
            Assert.Equal(days, list.Count);
            var strip = new CrashStrip { Days = list };
            Assert.True(Draw.Inked(Draw.Render(strip, 900, 90)) > 0);
            Draw.Set(strip, "_hover", list.Count - 1);
            strip.InvalidateVisual();
            Draw.Again(strip);
            Assert.Equal(0, Draw.Inked(Draw.Render(strip, 70, 40)));
        });
        Ui.AssertNoProblems($"crash strip, {days} days");
    }

    [Fact]
    public void Crash_strip_with_no_days()
    {
        Ui.Run(() =>
        {
            Assert.Equal(0, Draw.Inked(Draw.Render(new CrashStrip(), 900, 90)));
            Assert.Equal(0, Draw.Inked(Draw.Render(new CrashStrip { Days = [] }, 900, 90)));
        });
    }

    [Fact]
    public void Crash_days_count_by_severity_and_note_changes()
    {
        var from = DateTime.Today.AddDays(-4);
        var t = from.AddDays(1).AddHours(12);
        var groups = CrashGroup.Build(
        [
            Crashes.App(t), Crashes.Bsod(t.AddHours(1)), Crashes.Power(t.AddHours(2), sleep: true), Crashes.Reset(t.AddDays(1)),
            Crashes.Hang(t.AddDays(2), "a.exe"), Crashes.Hang(t.AddDays(2).AddMinutes(1), "b.exe"), Crashes.Hang(t.AddDays(2).AddMinutes(2), "c.exe"),
            Crashes.App(from.AddDays(-3)), // before the period
        ]);
        var changes = new[] { new SystemChange(t, ChangeKind.WindowsUpdate, "Update"), new SystemChange(from.AddDays(-2), ChangeKind.Driver, "Old") };
        var days = CrashStrip.BuildDays(from, DateTime.Today.AddDays(1), groups, changes);
        Assert.Equal(5, days.Count);
        Assert.Equal(from, days[0].Day);
        Assert.Equal(DateTime.Today, days[^1].Day);
        Assert.Equal(0, days[0].Total);
        Assert.Equal([1, 1, 0, 1], days[1].Counts); // info (asleep), minor (app), serious, critical (blue screen)
        Assert.Equal(3, days[1].Problems.Count);
        Assert.StartsWith(t.ToString("h:mm tt"), days[1].Problems[0]);
        Assert.Equal("Update", Assert.Single(days[1].Changes).Title);
        Assert.Equal([0, 0, 1, 0], days[2].Counts);
        Assert.Equal([0, 0, 3, 0], days[3].Counts); // a freeze of the PC counts as serious, all three
        Assert.Equal(7, days.Sum(d => d.Total));
    }

    [Fact]
    public void Crash_days_never_run_past_today()
    {
        var days = CrashStrip.BuildDays(DateTime.Today.AddDays(-2), DateTime.Today.AddDays(30), [], []);
        Assert.Equal(3, days.Count);
        Assert.Empty(CrashStrip.BuildDays(DateTime.Today.AddDays(1), DateTime.Today.AddDays(5), [], []));
        Assert.Equal(31, CrashStrip.BuildDays(new DateTime(2025, 1, 1), new DateTime(2025, 2, 1), [], []).Count);
    }

    // ── Treemap ──────────────────────────────────────────────────────────

    private static List<FolderNode> Nodes(params long[] sizes) =>
        [.. sizes.Select((s, i) => new FolderNode { Name = $"n{i}", Path = $@"C:\n{i}", Size = s, IsBucket = i == 1 })];

    private static List<(Rect Rect, FolderNode Node, int Color)> Layout(Treemap map) =>
        Draw.Get<List<(Rect, FolderNode, int)>>(map, "_layout");

    [Theory]
    [InlineData(new long[] { 100 })]
    [InlineData(new long[] { 50, 50 })]
    [InlineData(new long[] { 600, 300, 100 })]
    [InlineData(new long[] { 1_000_000_000_000, 1, 1, 1, 5_000_000 })]
    [InlineData(new long[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 })]
    public void Treemap_areas_are_proportional_fill_the_box_and_never_overlap(long[] sizes)
    {
        Ui.Run(() =>
        {
            var map = new Treemap { Items = Nodes(sizes) };
            Draw.Render(map, 600, 400);
            var layout = Layout(map);
            Assert.Equal(sizes.Length, layout.Count);
            double total = sizes.Sum(s => (double)s);
            foreach (var (rect, node, _) in layout)
            {
                Assert.InRange(rect.Left, -0.001, 600.001);
                Assert.InRange(rect.Right, -0.001, 600.001);
                Assert.InRange(rect.Top, -0.001, 400.001);
                Assert.InRange(rect.Bottom, -0.001, 400.001);
                Assert.Equal(node.Size / total * 600 * 400, rect.Width * rect.Height, 0.5);
            }
            Assert.Equal(600 * 400, layout.Sum(l => l.Rect.Width * l.Rect.Height), 1);
            for (int i = 0; i < layout.Count; i++)
                for (int j = i + 1; j < layout.Count; j++)
                {
                    var overlap = Rect.Intersect(layout[i].Rect, layout[j].Rect);
                    Assert.True(overlap.IsEmpty || overlap.Width * overlap.Height < 1e-6, $"{layout[i].Node.Name} overlaps {layout[j].Node.Name}");
                }
            // Biggest first, coloured in turn.
            Assert.Equal(layout.OrderByDescending(l => l.Node.Size).Select(l => l.Node), layout.Select(l => l.Node));
        });
    }

    [Fact]
    public void Treemap_skips_empty_and_negative_items_and_tiny_boxes()
    {
        Ui.Run(() =>
        {
            var map = new Treemap { Items = Nodes(0, -5, 100, 0) };
            Draw.Render(map, 300, 200);
            Assert.Single(Layout(map));
            map.Items = Nodes(0, 0);
            Draw.Again(map);
            Assert.Empty(Layout(map));
            map.Items = null;
            Draw.Again(map);
            Assert.Empty(Layout(map));
            map.Items = Nodes(10, 20);
            Draw.Render(map, 9, 200);
            Assert.Empty(Layout(map));
        });
    }

    [Fact]
    public void Treemap_hover_shows_details()
    {
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var map = new Treemap { Items = Nodes(600, 300, 100) };
            map.Items[0].Children.Add(new FolderNode { Name = "child", Path = "c", Size = 1 });
            int plain = Draw.Inked(Draw.Render(map, 600, 400));
            Draw.Set(map, "_hover", 0);
            map.InvalidateVisual();
            Draw.Again(map);
            Draw.Set(map, "_hover", 99); // stale index after the items changed
            map.InvalidateVisual();
            Draw.Again(map);
            Assert.True(plain > 0);
        });
        Ui.AssertNoProblems("treemap hover");
    }

    // ── Tile grid ────────────────────────────────────────────────────────

    private static (TileGrid Grid, List<TileViewModel> Tiles) Grid(params (int X, int Y, int W, int H)[] cells)
    {
        SharedData.EnsureSeeded();
        var settings = Kit.OfflineSettings();
        var live = new LiveData(settings);
        var reports = new ReportService(settings);
        var page = new CustomPageViewModel(new CustomPageConfig(), settings, live, new HomeViewModel(reports, live), new CrashesViewModel(reports, settings), _ => { }, _ => { });
        var grid = new TileGrid();
        var tiles = new List<TileViewModel>();
        foreach (var (x, y, w, h) in cells)
        {
            var t = new TileViewModel(new TileConfig { Kind = "fans", X = x, Y = y, W = w, H = h }, page);
            tiles.Add(t);
            grid.Children.Add(new Border { DataContext = t });
        }
        return (grid, tiles);
    }

    [Fact]
    public void Tile_grid_places_tiles_on_its_cells()
    {
        Ui.Run(() =>
        {
            var (grid, _) = Grid((0, 0, 3, 4), (3, 0, 9, 2), (0, 4, 12, 4));
            grid.Children.Add(new Border()); // not a tile: laid out at nothing
            Draw.Render(grid, 1224, 1000);
            double cell = (1224 - 16 * 11) / 12.0;
            Assert.Equal(cell, grid.CellWidth, 6);
            Assert.Equal(8 * (56 + 16) - 16, grid.DesiredSize.Height, 6);
            var slots = grid.Children.OfType<Border>().Select(b => LayoutInformation.GetLayoutSlot(b)).ToList();
            Assert.Equal(new Rect(0, 0, 3 * cell + 2 * 16, 4 * 56 + 3 * 16), slots[0]);
            Assert.Equal(new Rect(3 * (cell + 16), 0, 9 * cell + 8 * 16, 2 * 56 + 16), slots[1]);
            Assert.Equal(new Rect(0, 4 * (56 + 16), 1224, 4 * 56 + 3 * 16), slots[2]);
            Assert.Equal(0, slots[3].Width);
        });
    }

    [Fact]
    public void Tile_grid_converts_between_pixels_and_cells()
    {
        Ui.Run(() =>
        {
            var (grid, _) = Grid((0, 0, 3, 4));
            Draw.Render(grid, 1224, 600);
            double step = grid.CellWidth + 16;
            Assert.Equal((2, 3), grid.CellAt(new Point(2 * step + 10, 3 * 72 - 10)));
            Assert.Equal((0, 0), grid.CellAt(new Point(-20, -20)));
            // Dragging the right-bottom edge of a tile at (1,1) to the end of the fourth cell and third row.
            Assert.Equal((3, 2), grid.SpanTo(1, 1, new Point(4 * step - 16, 3 * 72 - 16)));
        });
    }

    [Fact]
    public void Tile_grid_draws_the_dragged_tile_where_the_mouse_is_and_ignores_it_for_height()
    {
        Ui.Run(() =>
        {
            var (grid, tiles) = Grid((0, 0, 3, 4), (0, 10, 3, 4));
            tiles[1].IsDragging = true;
            tiles[1].DragPosition = new Point(123, 45);
            Draw.Render(grid, 1224, 600);
            Assert.Equal(4 * 72 - 16, grid.DesiredSize.Height, 6); // the dragged one floats
            var slot = LayoutInformation.GetLayoutSlot((FrameworkElement)grid.Children[1]);
            Assert.Equal(new Point(123, 45), slot.TopLeft);
            Assert.Equal(10, Panel.GetZIndex(grid.Children[1]));
            Assert.Equal(0, Panel.GetZIndex(grid.Children[0]));
        });
    }

    [Fact]
    public void Tile_grid_with_no_tiles_or_infinite_width()
    {
        Ui.Run(() =>
        {
            var (grid, _) = Grid();
            grid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.Equal(new Size(900, 0), grid.DesiredSize);
            var (tiny, _) = Grid((0, 0, 12, 1));
            Draw.Render(tiny, 50, 50);
            Assert.Equal(10, tiny.CellWidth); // never narrower than 10 px
        });
    }

    [Fact]
    public void Tile_grid_slides_moved_tiles_but_not_on_a_resize_of_the_window()
    {
        Ui.Run(() =>
        {
            var (grid, tiles) = Grid((0, 0, 3, 4), (3, 0, 3, 4));
            Draw.Render(grid, 1224, 600);
            tiles[1].X = 6;
            grid.InvalidateArrange();
            Draw.Render(grid, 1224, 600);
            var move = (TranslateTransform)grid.Children[1].RenderTransform;
            Assert.True(move.HasAnimatedProperties);
            // A different width: everything jumps, no slide for the unmoved one.
            Draw.Render(grid, 1000, 600);
            Assert.False(((TranslateTransform)grid.Children[0].RenderTransform).HasAnimatedProperties);
        });
    }

    // ── Period picker ────────────────────────────────────────────────────

    [Theory]
    [InlineData(ReportRange.Day, 0, "Today")]
    [InlineData(ReportRange.Day, -1, "Yesterday")]
    [InlineData(ReportRange.Week, 0, "This week")]
    [InlineData(ReportRange.Week, -7, "Last week")]
    [InlineData(ReportRange.Month, 0, "This month")]
    [InlineData(ReportRange.Year, 0, "This year")]
    [InlineData(ReportRange.Year, -366, "Last year")]
    [InlineData(ReportRange.All, -500, "All time")]
    public void Periods_in_words(ReportRange unit, int daysAgo, string text) =>
        Assert.Equal(text, PeriodPicker.Text(unit, DateTime.Today.AddDays(daysAgo)));

    [Fact]
    public void Older_periods_in_words()
    {
        var today = DateTime.Today;
        var old = today.AddYears(-2);
        Assert.Equal(old.ToString("d MMM yyyy"), PeriodPicker.Text(ReportRange.Day, old));
        Assert.Equal(old.ToString("MMMM yyyy"), PeriodPicker.Text(ReportRange.Month, old));
        Assert.Equal(old.Year.ToString(), PeriodPicker.Text(ReportRange.Year, old));
        Assert.Equal("Last month", PeriodPicker.Text(ReportRange.Month, today.AddMonths(-1)));
        // Weeks run Monday to Sunday: within one month, and across two.
        Assert.Equal("6–12 Jan 2020", PeriodPicker.Text(ReportRange.Week, new DateTime(2020, 1, 8)));
        Assert.Equal("28 Sep – 4 Oct 2020", PeriodPicker.Text(ReportRange.Week, new DateTime(2020, 9, 30)));
        Assert.Equal("28 Sep – 4 Oct 2020", PeriodPicker.Text(ReportRange.Week, new DateTime(2020, 10, 4)));
    }

    [Fact]
    public void Period_spans()
    {
        var today = DateTime.Today;
        Assert.Equal(today.ToString("dddd, d MMMM yyyy"), PeriodPicker.Span(ReportRange.Day, today));
        Assert.Equal("1 Jan – 31 Dec 2025", PeriodPicker.Span(ReportRange.Year, new DateTime(2025, 6, 1)));
        Assert.Equal("1 Dec – 31 Dec 2024", PeriodPicker.Span(ReportRange.Month, new DateTime(2024, 12, 5)));
        Assert.Equal("28 Dec 2020 – 3 Jan 2021", PeriodPicker.Span(ReportRange.Week, new DateTime(2021, 1, 1)));
        Assert.EndsWith(" – " + today.ToString("d MMM yyyy"), PeriodPicker.Span(ReportRange.Month, today)); // in progress: up to today
        var all = PeriodPicker.Span(ReportRange.All, today, today.AddDays(-9));
        Assert.StartsWith(today.AddDays(-9).ToString("d MMM"), all);
        Assert.EndsWith(" (10 days)", all);
        Assert.Equal($"{today:d MMM} – {today:d MMM yyyy}", PeriodPicker.Span(ReportRange.All, today)); // nothing recorded yet
    }

    private static PeriodPicker Picker(ReportRange unit, DateTime anchor, DateTime? min = null, bool all = true)
    {
        var p = new PeriodPicker { AllowAll = all, MinDate = min, Unit = unit, Anchor = anchor };
        Draw.Render(p, 700, 60);
        return p;
    }

    [Fact]
    public void Picker_steps_back_and_forth_between_first_day_and_today()
    {
        Ui.Run(() =>
        {
            var today = DateTime.Today;
            var p = Picker(ReportRange.Day, today, min: today.AddDays(-2));
            Assert.Equal("Today", p.Pager.Label);
            Assert.False(p.Pager.CanGoNext);
            Assert.True(p.Pager.CanGoPrevious);
            p.Pager.NextCommand!.Execute(null);
            Assert.Equal(today, p.Anchor);
            p.Pager.PreviousCommand!.Execute(null);
            Assert.Equal(today.AddDays(-1), p.Anchor);
            Assert.Equal("Yesterday", p.Pager.Label);
            Assert.True(p.Pager.CanGoNext);
            p.Pager.PreviousCommand.Execute(null);
            Assert.Equal(today.AddDays(-2), p.Anchor);
            Assert.False(p.Pager.CanGoPrevious);
            p.Pager.PreviousCommand.Execute(null); // before the first day: no
            Assert.Equal(today.AddDays(-2), p.Anchor);
        });
    }

    [Fact]
    public void Picker_never_lands_after_today_when_stepping_months()
    {
        Ui.Run(() =>
        {
            var today = DateTime.Today;
            var lastMonth = today.AddMonths(-1);
            // The 31st of last month, stepping to this month, lands on today at the latest.
            var p = Picker(ReportRange.Month, new DateTime(lastMonth.Year, lastMonth.Month, DateTime.DaysInMonth(lastMonth.Year, lastMonth.Month)));
            p.Pager.NextCommand!.Execute(null);
            Assert.True(p.Anchor <= today);
            Assert.Equal("This month", p.Pager.Label);
            Assert.True(p.Pager.PickMonth);
            Assert.False(p.Pager.PickYear);
            p.Pager.NextCommand.Execute(null);
            Assert.Equal("This month", p.Pager.Label);
        });
    }

    [Fact]
    public void Picker_units_and_all_time()
    {
        Ui.Run(() =>
        {
            var p = Picker(ReportRange.Year, DateTime.Today);
            Assert.True(p.Pager.PickYear);
            Assert.Equal(Visibility.Visible, p.AllButton.Visibility);
            p.Unit = ReportRange.All;
            Assert.Equal(Visibility.Collapsed, p.Pager.Visibility);
            var radios = p.UnitButtons.Children.OfType<RadioButton>().ToList();
            Assert.True(radios.Single(r => (string)r.Tag == "All").IsChecked);
            // Clicking "Week".
            radios.Single(r => (string)r.Tag == "Week").IsChecked = true;
            Assert.Equal(ReportRange.Week, p.Unit);
            Assert.Equal(Visibility.Visible, p.Pager.Visibility);
            Assert.Equal(1, radios.Count(r => r.IsChecked == true));
            var reports = Picker(ReportRange.Day, DateTime.Today, all: false);
            Assert.Equal(Visibility.Collapsed, reports.AllButton.Visibility);
        });
    }

    // ── Custom ranges ────────────────────────────────────────────────────

    [Fact]
    public void Custom_ranges_are_read_from_dates_and_hours()
    {
        var now = new DateTime(2026, 6, 12, 14, 30, 0);
        var day = new DateTime(2026, 6, 10);
        Assert.Equal((day.AddHours(8), day.AddDays(1).AddHours(1), (string?)null), PeriodPicker.ReadRange(day, 8, day.AddDays(1), 1, now));
        Assert.Equal("Pick a date and hour for both ends.", PeriodPicker.ReadRange(null, 8, day, 9, now).Error);
        Assert.Equal("Pick a date and hour for both ends.", PeriodPicker.ReadRange(day, -1, day, 9, now).Error);
        Assert.Equal("Pick a date and hour for both ends.", PeriodPicker.ReadRange(day, 8, day, -1, now).Error);
        Assert.Equal("The end has to be after the start.", PeriodPicker.ReadRange(day, 9, day, 9, now).Error);
        Assert.Equal("The end has to be after the start.", PeriodPicker.ReadRange(day.AddDays(1), 1, day, 23, now).Error);
        Assert.Equal("That's still to come.", PeriodPicker.ReadRange(now.Date, 15, now.Date, 18, now).Error);
        Assert.Null(PeriodPicker.ReadRange(now.Date, 14, now.Date.AddDays(1), 0, now).Error); // the hour now, into the future: fine
    }

    [Theory]
    [InlineData(0.2, "1 hour")]
    [InlineData(1, "1 hour")]
    [InlineData(17, "17 hours")]
    [InlineData(24, "1 day")]
    [InlineData(48, "2 days")]
    [InlineData(25, "1 day and 1 hour")]
    [InlineData(76, "3 days and 4 hours")]
    public void Custom_range_lengths_in_words(double hours, string text) =>
        Assert.Equal(text, PeriodPicker.Duration(TimeSpan.FromHours(hours)));

    [Theory]
    [InlineData(0, "12 AM")]
    [InlineData(1, "1 AM")]
    [InlineData(11, "11 AM")]
    [InlineData(12, "12 PM")]
    [InlineData(23, "11 PM")]
    public void Hours_in_the_range_editor(int hour, string text) => Assert.Equal(text, PeriodPicker.HourText(hour));

    [Fact]
    public void Custom_bounds_words_and_spans()
    {
        using var c = Make.Culture();
        var (from, to) = (new DateTime(2020, 3, 4, 8, 0, 0), new DateTime(2020, 3, 5, 1, 0, 0));
        Assert.Equal((from, to), PeriodPicker.Bounds(ReportRange.Custom, DateTime.Today, from, to));
        Assert.Equal("Wed 4 Mar 2020, 8 AM – Thu 5 Mar 2020, 1 AM", PeriodPicker.Text(ReportRange.Custom, DateTime.Today, from, to));
        Assert.Equal("17 hours", PeriodPicker.Span(ReportRange.Custom, DateTime.Today, null, from, to)); // the dates are on the picker
        Assert.Equal("", PeriodPicker.Span(ReportRange.Custom, DateTime.Today, null, default, default));
        // Not picked yet: the day around the anchor, and "Custom".
        Assert.Equal(ReportBuilder.Bounds(ReportRange.Custom, from), PeriodPicker.Bounds(ReportRange.Custom, from, default, default));
        Assert.Equal("Custom", PeriodPicker.Text(ReportRange.Custom, from, default, default));
        // Other units ignore the custom ends.
        Assert.Equal(ReportBuilder.Bounds(ReportRange.Week, from), PeriodPicker.Bounds(ReportRange.Week, from, from, to));
        Assert.Equal(PeriodPicker.Text(ReportRange.Week, from), PeriodPicker.Text(ReportRange.Week, from, from, to));
    }

    // Asked to open: a popup only really opens in a window on screen.
    private static bool EditorOpen(PeriodPicker p) => p.RangePopup.ReadLocalValue(Popup.IsOpenProperty) is true;

    [Fact]
    public void Picking_custom_starts_from_the_period_shown_and_opens_the_editor()
    {
        Ui.Run(() =>
        {
            var week = DateTime.Today.AddDays(-14);
            var p = Picker(ReportRange.Week, week);
            var radios = p.UnitButtons.Children.OfType<RadioButton>().ToList();
            radios.Single(r => (string)r.Tag == "Custom").IsChecked = true;
            Assert.Equal(ReportRange.Custom, p.Unit);
            Assert.Equal(ReportBuilder.Bounds(ReportRange.Week, week), (p.CustomFrom, p.CustomTo));
            Assert.True(EditorOpen(p));
            Assert.Equal(p.CustomFrom.Date, p.FromDate.SelectedDate);
            Assert.Equal(0, p.FromHour.SelectedIndex);
            Assert.Equal(p.CustomTo.Date, p.ToDate.SelectedDate);
            Assert.Equal(Report.CustomTitle(p.CustomFrom, p.CustomTo), p.Pager.Label);
            Assert.NotNull(p.Pager.LabelCommand);
            Assert.Equal(1, radios.Count(r => r.IsChecked == true));
            p.RangePopup.IsOpen = false;

            // Today: up to the end of this hour, not midnight to come.
            var today = Picker(ReportRange.Day, DateTime.Today);
            today.UnitButtons.Children.OfType<RadioButton>().Single(r => (string)r.Tag == "Custom").IsChecked = true;
            Assert.Equal((DateTime.Today, ReportBuilder.HourEnd(DateTime.Now)), (today.CustomFrom, today.CustomTo));
            today.RangePopup.IsOpen = false;

            // Back to a unit: the calendar label again.
            radios.Single(r => (string)r.Tag == "Day").IsChecked = true;
            Assert.Equal(ReportRange.Day, p.Unit);
            Assert.Null(p.Pager.LabelCommand);
        });
    }

    [Fact]
    public void The_range_editor_shows_what_is_wrong_and_applies_whole_hours()
    {
        Ui.Run(() =>
        {
            var day = DateTime.Today.AddDays(-5);
            var p = Picker(ReportRange.Custom, DateTime.Today);
            (p.CustomFrom, p.CustomTo) = (day.AddHours(8), day.AddHours(20));
            p.Pager.LabelCommand!.Execute(null);
            Assert.True(EditorOpen(p));
            Assert.Equal(8, p.FromHour.SelectedIndex);
            Assert.Equal(20, p.ToHour.SelectedIndex);

            void Apply() => typeof(PeriodPicker).GetMethod("RangeApply_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p, [null, null]);
            p.ToHour.SelectedIndex = 6;
            Apply();
            Assert.True(EditorOpen(p));
            Assert.Equal(Visibility.Visible, p.RangeError.Visibility);
            Assert.Equal("The end has to be after the start.", p.RangeError.Text);
            Assert.Equal(day.AddHours(20), p.CustomTo); // nothing changed

            p.ToDate.SelectedDate = day.AddDays(1);
            p.ToHour.SelectedIndex = 3;
            Apply();
            Assert.False(EditorOpen(p));
            Assert.Equal((day.AddHours(8), day.AddDays(1).AddHours(3)), (p.CustomFrom, p.CustomTo));

            // Opening again clears the old error.
            p.Pager.LabelCommand!.Execute(null);
            Assert.Equal(Visibility.Collapsed, p.RangeError.Visibility);
            p.RangePopup.IsOpen = false;
        });
    }

    [Fact]
    public void A_custom_range_steps_by_its_own_length()
    {
        Ui.Run(() =>
        {
            var day = DateTime.Today.AddDays(-3);
            var p = Picker(ReportRange.Custom, DateTime.Today, min: day.AddHours(2));
            (p.CustomFrom, p.CustomTo) = (day.AddHours(8), day.AddHours(14));
            Assert.True(p.Pager.CanGoNext);
            p.Pager.NextCommand!.Execute(null);
            Assert.Equal((day.AddHours(14), day.AddHours(20)), (p.CustomFrom, p.CustomTo));
            p.Pager.PreviousCommand!.Execute(null);
            p.Pager.PreviousCommand.Execute(null);
            Assert.Equal((day.AddHours(2), day.AddHours(8)), (p.CustomFrom, p.CustomTo));
            p.Pager.PreviousCommand.Execute(null); // would end before the first day: stays
            Assert.Equal((day.AddHours(2), day.AddHours(8)), (p.CustomFrom, p.CustomTo));
            Assert.Equal(ReportRange.Custom, p.Unit);

            // Up to now: nothing after it.
            (p.CustomFrom, p.CustomTo) = (ReportBuilder.HourStart(DateTime.Now), ReportBuilder.HourEnd(DateTime.Now.AddMinutes(1)));
            Assert.False(p.Pager.CanGoNext);
            var before = (p.CustomFrom, p.CustomTo);
            p.Pager.NextCommand.Execute(null);
            Assert.Equal(before, (p.CustomFrom, p.CustomTo));

            // Not picked yet: nowhere to step.
            var empty = Picker(ReportRange.Custom, DateTime.Today);
            Assert.Equal("Pick a range", empty.Pager.Label);
            Assert.False(empty.Pager.CanGoPrevious);
            Assert.False(empty.Pager.CanGoNext);
            empty.Pager.PreviousCommand!.Execute(null);
            Assert.Equal(default, empty.CustomFrom);
        });
    }

    [Fact]
    public void Date_pager_month_and_year_grids_offer_only_months_with_history()
    {
        Ui.Run(() =>
        {
            var today = DateTime.Today;
            var first = today.AddMonths(-14);
            var pager = new DatePager { MinDate = first, Date = today, PickMonth = true };
            Draw.Render(pager, 400, 50);
            Draw.Set(pager, "_min", first.Date);
            Draw.Set(pager, "_year", today.Year);
            typeof(DatePager).GetMethod("BuildMonths", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pager, null);
            var months = pager.MonthGrid.Children.OfType<Button>().ToList();
            Assert.Equal(12, months.Count);
            Assert.Equal(today.Month, months.Count(b => b.IsEnabled));
            Assert.Equal(today.Year.ToString(), pager.YearText.Text);
            Assert.False(pager.NextYear.IsEnabled);
            Assert.True(pager.PreviousYear.IsEnabled);

            typeof(DatePager).GetMethod("BuildYears", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pager, null);
            Assert.Equal(today.Year - first.Year + 1, pager.YearGrid.Children.Count);

            typeof(DatePager).GetMethod("PrepareCalendar", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pager, [today.AddDays(-3)]);
            Assert.Equal(new DateTime(first.Year, first.Month, 1), pager.Cal.DisplayDateStart);
            Assert.Equal(today.AddDays(-3), pager.Cal.SelectedDate);
            Assert.Equal(today, pager.Date); // preparing isn't picking

            typeof(DatePager).GetMethod("Pick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pager, [today.AddDays(-5).AddHours(7)]);
            Assert.Equal(today.AddDays(-5), pager.Date);
            Assert.False(pager.Picker.IsOpen);
        });
    }

    // ── Performance ──────────────────────────────────────────────────────

    /// <summary>Median of several timed runs (after one to warm up).</summary>
    private static double Median(Action draw, int runs = 5)
    {
        draw();
        var times = new List<double>();
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            draw();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        return times.Order().ElementAt(runs / 2);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Drawing_big_series_stays_fast()
    {
        var results = new List<string>();
        Ui.Run(() =>
        {
            // Temperatures move smoothly (a saw-tooth of 40° a minute would mostly measure WPF's software rasterizer).
            double Temp(int i, int j) => 45 + 15 * Math.Sin(j / 90.0 + i) + j % 5 * 0.3;
            var chart = new LineChart { WindowSeconds = 86400, Series = [.. Enumerable.Range(0, 4).Select(i => Series($"S{i}", 3600, 1500, j => Temp(i, j)))] };
            double line = Median(() => { chart.InvalidateVisual(); Draw.Render(chart, 1400, 400); });
            results.Add($"line chart 4×(3600+1500), drawn and rasterized: {line:0.0} ms");
            double lineDraw = Median(() => { chart.InvalidateVisual(); chart.UpdateLayout(); });
            results.Add($"line chart, drawing only: {lineDraw:0.0} ms");
            Draw.Set(chart, "_hoverX", 700.0);
            double hover = Median(() => { chart.InvalidateVisual(); chart.UpdateLayout(); });
            results.Add($"line chart hover: {hover:0.0} ms");

            var spark = new Sparkline { Source = Draw.Buffer(100_000, Now, 100, i => i % 97), WindowSeconds = 10_000 };
            double sparkMs = Median(() => { spark.Version++; Draw.Render(spark, 300, 60); });
            results.Add($"sparkline 100k: {sparkMs:0.0} ms");

            var buffer = Draw.Buffer(100_000, Now, 100, i => i % 97);
            double geometry = Median(() => ChartGeometry.Build(buffer, Now, Now + 10_000_000, Plot, 0, 100, v => v));
            results.Add($"geometry 100k: {geometry:0.0} ms");

            var bars = new DailyBars { Days = Days(3650, d => d.AddDays(1), DateTime.Today.AddDays(-3649), i => i % 50 * 600) };
            double barsMs = Median(() => { bars.InvalidateVisual(); Draw.Render(bars, 1400, 300); });
            results.Add($"daily bars 3650: {barsMs:0.0} ms");

            var strip = new CrashStrip
            {
                Days = CrashStrip.BuildDays(DateTime.Today.AddDays(-3649), DateTime.Today.AddDays(1),
                    CrashGroup.Build(Enumerable.Range(0, 3000).Select(i => Crashes.App(DateTime.Today.AddDays(-i).AddHours(9), $"a{i % 40}.exe"))), []),
            };
            double stripMs = Median(() => { strip.InvalidateVisual(); Draw.Render(strip, 1400, 90); });
            results.Add($"crash strip 3650: {stripMs:0.0} ms");

            var map = new Treemap { Items = Nodes([.. Enumerable.Range(1, 10_000).Select(i => (long)(1_000_000 / i + 1))]) };
            double mapMs = Median(() => { map.InvalidateVisual(); Draw.Render(map, 1400, 700); });
            results.Add($"treemap 10k: {mapMs:0.0} ms");

            var day = DateTime.Today.AddDays(-1);
            var timeline = new DayTimeline
            {
                Day = day,
                Segments = [.. Enumerable.Range(0, 1440).Select(i => new TimelineSegment { Start = day.AddMinutes(i), End = day.AddMinutes(i + 1), AppId = i, Category = (AppCategory)(i % 9) })],
                Temps = [.. Enumerable.Range(0, 1440).Select(i => new TempPoint(day.AddMinutes(i), 40 + i % 30, 50 + i % 20))],
            };
            double timelineMs = Median(() => { timeline.InvalidateVisual(); Draw.Render(timeline, 1400, 220); });
            results.Add($"day timeline 1440: {timelineMs:0.0} ms");

            // Budgets: about three times what they took on the development PC (Ryzen 7 5700X3D; in brackets), at least 25 ms.
            var over = new List<string>();
            void Budget(string what, double ms, double budget) { if (ms > budget) over.Add($"{what} {ms:0.0} ms > {budget} ms"); }
            Budget("line chart", line, 115);            // (37)
            Budget("line chart drawing", lineDraw, 25); // (4)
            Budget("line chart hover", hover, 25);      // (5)
            Budget("sparkline", sparkMs, 35);           // (11)
            Budget("geometry", geometry, 25);           // (6)
            Budget("daily bars", barsMs, 450);          // (145)
            Budget("crash strip", stripMs, 35);         // (11)
            Budget("treemap", mapMs, 400);              // (135)
            Budget("day timeline", timelineMs, 135);    // (45)
            File.WriteAllLines(Path.Combine(TestEnvironment.DataDir, "perf-controls.txt"), results);
            Assert.True(over.Count == 0, string.Join("; ", over) + " | measured: " + string.Join("; ", results));
        });
    }
}
