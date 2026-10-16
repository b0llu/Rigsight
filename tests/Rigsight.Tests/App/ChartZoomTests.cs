using System.Windows;
using System.Windows.Media.Imaging;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Data;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.App;

/// <summary>The maths of drag-to-zoom: the stretch a drag picks, its 15-point least, and staying inside the whole.</summary>
public sealed class ChartZoomMathTests
{
    private static readonly DateTime Day = new(2026, 10, 15);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1), Hour = TimeSpan.FromHours(1), OneDay = TimeSpan.FromDays(1);
    private static DateTime At(int h, int m, int s = 0) => Day.AddHours(h).AddMinutes(m).AddSeconds(s);

    [Fact]
    public void A_drag_zooms_to_the_whole_minutes_it_crossed_either_way_round()
    {
        var expected = (At(9, 30), At(11, 15));
        Assert.Equal(expected, ChartZoom.FromDrag(At(9, 30, 20), At(11, 14, 40), Day, Day.AddDays(1), Minute));
        Assert.Equal(expected, ChartZoom.FromDrag(At(11, 14, 40), At(9, 30, 20), Day, Day.AddDays(1), Minute));
    }

    [Fact]
    public void A_short_drag_is_widened_to_15_minutes_around_its_middle()
    {
        // 10:00 to 10:05: its middle is 10:02:30, so 9:55 to 10:10.
        Assert.Equal((At(9, 55), At(10, 10)), ChartZoom.FromDrag(At(10, 0), At(10, 5), Day, Day.AddDays(1), Minute));
        // Exactly 15 minutes stays as it is.
        Assert.Equal((At(10, 0), At(10, 15)), ChartZoom.FromDrag(At(10, 0), At(10, 15), Day, Day.AddDays(1), Minute));
    }

    [Fact]
    public void A_short_drag_at_either_end_is_moved_back_inside_the_day()
    {
        Assert.Equal((Day, At(0, 15)), ChartZoom.FromDrag(At(0, 1), At(0, 3), Day, Day.AddDays(1), Minute));
        Assert.Equal((At(23, 45), Day.AddDays(1)), ChartZoom.FromDrag(At(23, 58), At(23, 59, 30), Day, Day.AddDays(1), Minute));
    }

    [Fact]
    public void A_drag_past_the_ends_is_cut_at_them_and_one_across_everything_zooms_nothing()
    {
        Assert.Equal((Day, At(2, 0)), ChartZoom.FromDrag(Day.AddHours(-3), At(2, 0), Day, Day.AddDays(1), Minute));
        Assert.Null(ChartZoom.FromDrag(Day.AddHours(-1), Day.AddDays(2), Day, Day.AddDays(1), Minute));
        // A whole too short to hold 15 points has nothing to zoom into.
        Assert.Null(ChartZoom.FromDrag(At(10, 0), At(10, 5), At(10, 0), At(10, 10), Minute));
    }

    [Fact]
    public void A_window_up_to_now_is_not_left_by_rounding_to_whole_minutes()
    {
        // The 24 hours up to 10:23:41: a drag from its very start begins there, not at 10:23 the day before.
        var from = At(10, 23, 41).AddDays(-1);
        var zoom = ChartZoom.FromDrag(from, from.AddMinutes(30), from, At(10, 23, 41), Minute);
        Assert.Equal((from, At(10, 54).AddDays(-1)), zoom);
    }

    [Fact]
    public void On_a_week_the_points_stay_hours_and_the_least_is_15_of_them()
    {
        var monday = new DateTime(2026, 10, 12);
        // Two hours dragged across Wednesday noon: 15 hours around it, on whole hours.
        var zoom = ChartZoom.FromDrag(monday.AddDays(2).AddHours(11).AddMinutes(10), monday.AddDays(2).AddHours(12).AddMinutes(50), monday, monday.AddDays(7), Hour)!.Value;
        Assert.Equal(TimeSpan.FromHours(15), zoom.To - zoom.From);
        Assert.Equal(0, zoom.From.Minute);
        Assert.Equal(TimeSpan.FromHours(15), ChartZoom.MinSpan(Hour));
        // A year's points are days: 15 of them at least, from midnight.
        var year = ChartZoom.FromDrag(new DateTime(2026, 3, 3, 14, 0, 0), new DateTime(2026, 3, 5), new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), OneDay)!.Value;
        Assert.Equal(TimeSpan.FromDays(15), year.To - year.From);
        Assert.Equal(TimeSpan.Zero, year.From.TimeOfDay);
    }

    [Fact]
    public void The_range_is_said_in_the_points_own_words()
    {
        Assert.Equal($"{At(9, 30):h:mm tt} – {At(11, 15):h:mm tt}", ChartZoom.Text(At(9, 30), At(11, 15), Minute));
        Assert.Equal($"{At(9, 0):h tt} – {At(11, 15):h:mm tt}", ChartZoom.Text(At(9, 0), At(11, 15), Minute));
        // Hours with their day; days by their date, the last one shown (not the midnight after it).
        Assert.StartsWith(At(15, 0).ToString("ddd d, "), ChartZoom.Text(At(15, 0), At(23, 0), Hour));
        Assert.Equal($"{Day:d MMM} – {Day.AddDays(14):d MMM}", ChartZoom.Text(Day, Day.AddDays(15), OneDay));
    }

    [Fact]
    public void A_press_that_hardly_moves_zooms_nothing_and_a_drag_does()
    {
        var zoom = new ChartZoom();
        DateTime TimeAt(double x) => Day.AddMinutes(x); // a pixel a minute
        zoom.Press(100);
        zoom.Move(105, 0, 1440);
        Assert.False(zoom.Dragging);
        Assert.False(zoom.Release(TimeAt, Day, Day.AddDays(1), Minute));
        Assert.False(zoom.IsZoomed);

        zoom.Press(600);
        zoom.Move(2000, 0, 1440); // past the plot's edge: kept at it
        Assert.Equal((600.0, 1440.0), zoom.Drag);
        Assert.True(zoom.Dragging);
        Assert.True(zoom.Release(TimeAt, Day, Day.AddDays(1), Minute));
        Assert.Equal((At(10, 0), Day.AddDays(1)), zoom.Range);
        Assert.Null(zoom.Drag);

        // A drag given up keeps the zoom; a reset goes back to the whole.
        zoom.Press(700);
        Assert.True(zoom.CancelDrag());
        Assert.True(zoom.IsZoomed);
        Assert.True(zoom.Reset());
        Assert.False(zoom.IsZoomed);
        Assert.False(zoom.Reset());
    }

    [Fact]
    public void A_zoom_left_behind_by_a_window_that_moves_with_the_clock_is_let_go()
    {
        var zoom = new ChartZoom();
        zoom.Press(0);
        zoom.Move(30, 0, 1440);
        zoom.Release(x => At(9, 0).AddMinutes(x), Day, At(12, 0), Minute);
        Assert.Equal((At(9, 0), At(9, 30)), zoom.Shown(Day, At(12, 0), Minute));
        // The window moved on: 9:20 to 9:30 is all that's left of it, under 15 minutes.
        Assert.Equal((At(9, 20), At(12, 20)), zoom.Shown(At(9, 20), At(12, 20), Minute));
        Assert.False(zoom.IsZoomed);
    }

    [Fact]
    public void Ticks_fall_on_round_clock_times_and_as_many_as_fit()
    {
        var (ticks, step) = ChartZoom.Ticks(At(9, 30), At(11, 15), 800, 72);
        Assert.Equal(TimeSpan.FromMinutes(10), step);
        Assert.Equal(At(9, 30), ticks[0]);
        Assert.All(ticks, t => Assert.Equal(0, t.Minute % 10));
        var (week, every) = ChartZoom.Ticks(Day, Day.AddDays(7), 800, 72);
        Assert.Equal(OneDay, every);
        Assert.Equal(8, week.Count);
        Assert.Equal(Day.ToString("ddd d"), ChartZoom.TickLabel(Day, every));
    }

    [Fact]
    public void The_button_back_says_the_period_picked()
    {
        Assert.Equal("Whole day", LineChart.ResetTextOf(0));
        Assert.Equal("Back to 24 hours", LineChart.ResetTextOf(86400));
        Assert.Equal("Back to 6 hours", LineChart.ResetTextOf(21600));
        Assert.Equal("Whole week", LineChart.ResetTextOf(604800));
        Assert.Equal("Whole month", LineChart.ResetTextOf(2592000));
        Assert.Equal("Whole year", LineChart.ResetTextOf(31536000));
    }
}

/// <summary>Each minute's highest and lowest on Reports' minute by minute: from the history, drawn as picked, shared with Temperatures.</summary>
public sealed class DayTimelinePlotTests
{
    [Fact]
    public void A_days_temperature_curve_keeps_each_minutes_highest_and_lowest()
    {
        using var t = new TestDb();
        long start = DayStart(U(2026, 3, 10, 12)) + 10 * 3600;
        var kept = Minute(start, cpu: 50, gpu: 45, cpuMax: 61, gpuMax: 52);
        kept.CpuTempMin = 44;
        kept.GpuTempMin = 41;
        t.Db.WriteMinute(kept);
        t.Db.WriteMinute(Minute(start + 60, cpu: 55, gpu: 47, cpuMax: 70, gpuMax: 58)); // no lowest kept for it
        var report = ReportBuilder.Build(t.Db, ReportRange.Day, L(start), new RigsightSettings());

        Assert.Equal(2, report.Temps.Count);
        var first = report.Temps[0];
        Assert.Equal((50.0, 45.0, 61.0, 52.0, 44.0, 41.0), (first.Cpu, first.Gpu, first.CpuHigh, first.GpuHigh, first.CpuLow, first.GpuLow));
        Assert.Equal((61.0, 52.0), (first.CpuFor("High"), first.GpuFor("High")));
        Assert.Equal((44.0, 41.0), (first.CpuFor("Low"), first.GpuFor("Low")));
        Assert.Equal((50.0, 45.0), (first.CpuFor("Avg"), first.GpuFor("Avg")));
        // Without a lowest the minute has none: the line stops, it doesn't pass its average off as one.
        Assert.Null(report.Temps[1].CpuFor("Low"));
        Assert.Equal(70.0, report.Temps[1].CpuFor("High"));
    }
}

/// <summary>Drawing the picked curve and zooming the charts, on the window's thread.</summary>
[Collection("UI")]
public sealed class ChartZoomUiTests
{
    private static List<TempPoint> Temps(DateTime day, bool lows = true) =>
        [.. Enumerable.Range(0, 1440).Select(m => new TempPoint(day.AddMinutes(m), 45 + m % 10, 50 + m % 7,
            CpuHigh: 70 + m % 15, GpuHigh: 72 + m % 9, CpuLow: lows ? 35 + m % 5 : null, GpuLow: lows ? 38 + m % 4 : null))];

    private static byte[] Pixels(BitmapSource bmp)
    {
        var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);
        return pixels;
    }

    [Fact]
    public void The_switch_changes_what_the_timeline_draws()
    {
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var day = DateTime.Today.AddDays(-1);
            var t = new DayTimeline { Day = day, Temps = Temps(day, lows: false), RecordedUntil = DateTime.Now };
            var average = Pixels(Draw.Render(t, 1000, 240));
            t.Plot = "High";
            var highest = Pixels(Draw.Render(t, 1000, 240));
            Assert.False(average.AsSpan().SequenceEqual(highest), "the highest drawn as the average");
            // No lowest kept for these minutes: no line at all, rather than the average under another name.
            t.Plot = "Low";
            int lowest = Draw.Inked(Draw.Render(t, 1000, 240));
            t.Plot = "Avg";
            Assert.True(lowest < Draw.Inked(Draw.Render(t, 1000, 240)) - 500, "a line drawn for lowest readings that weren't kept");
            // Hovering says the picked minute's values.
            t.Plot = "High";
            Draw.Set(t, "_hoverX", 500.0);
            Draw.Render(t, 1000, 240);
        });
        Ui.AssertNoProblems("timeline plot switch");
    }

    [Fact]
    public void The_reports_pick_is_the_temperatures_pick()
    {
        var settings = Kit.OfflineSettings();
        var live = Kit.Live(settings);
        Ui.Run(() =>
        {
            var vm = new ReportsViewModel(new ReportService(settings)) { Live = live };
            Assert.Equal("Avg", vm.ChartPlot);
            vm.ChartPlot = "High";
            Assert.Equal(("High", "High"), (live.ChartPlot, settings.Current.ChartPlot));
            // Picked on Temperatures: Reports hears of it.
            var changed = Kit.Changes(vm, () => live.ChartPlot = "Low");
            Assert.Contains(nameof(ReportsViewModel.ChartPlot), changed);
            Assert.Equal("Low", vm.ChartPlot);
            live.ChartPlot = "Avg";
        });
    }

    /// <summary>A drag from <paramref name="x0"/> to <paramref name="x1"/> on a chart, as the pointer would make it.</summary>
    private static void Drag(FrameworkElement chart, double x0, double x1)
    {
        var zoom = Draw.Get<ChartZoom>(chart, "_zoom");
        zoom.Press(x0);
        zoom.Move(x1, 0, chart.ActualWidth);
        chart.GetType().GetMethod("FinishDrag", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(chart, null);
    }

    [Fact]
    public void The_timeline_zooms_to_a_drag_keeps_its_hover_and_starts_whole_on_another_day()
    {
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var day = DateTime.Today.AddDays(-1);
            var segments = Enumerable.Range(0, 144).Select(i => new TimelineSegment
            {
                Start = day.AddMinutes(i * 10), End = day.AddMinutes(i * 10 + 10), AppId = i % 4, App = $"App {i % 4}", Category = (AppCategory)(i % 9),
            }).ToList();
            var t = new DayTimeline { Day = day, Temps = Temps(day), Segments = segments, RecordedUntil = DateTime.Now, CanZoom = true };
            var whole = Pixels(Draw.Render(t, 1000, 240));
            // 40 px of axis on the left, 952 px for the day: from about 6 AM to noon.
            Drag(t, 40 + 952 / 4.0, 40 + 952 / 2.0);
            Assert.True(t.IsZoomed);
            Assert.Contains(" – ", t.ZoomText);
            Assert.Equal("Whole day", t.ResetText);
            var zoomed = Pixels(Draw.Render(t, 1000, 240));
            Assert.False(whole.AsSpan().SequenceEqual(zoomed), "the zoomed timeline drawn as the whole day");
            // Hover still works while zoomed.
            Draw.Set(t, "_hoverX", 500.0);
            Draw.Render(t, 1000, 240);

            // The button back, then a double-click's worth: whole again.
            t.ResetZoomCommand.Execute(null);
            Assert.False(t.IsZoomed);
            Assert.Equal("", t.ZoomText);

            // Another day starts whole.
            Drag(t, 300, 600);
            Assert.True(t.IsZoomed);
            t.Day = day.AddDays(-1);
            Assert.False(t.IsZoomed);

            // Without CanZoom (a timeline with no button back) a press does nothing.
            var still = new DayTimeline { Day = day, Temps = Temps(day) };
            Draw.Render(still, 1000, 240);
            Assert.False(still.CanZoom);
        });
        Ui.AssertNoProblems("timeline zoom");
    }

    [Fact]
    public void On_reports_the_switch_and_the_button_back_move_nothing()
    {
        var settings = Kit.OfflineSettings();
        var live = Kit.Live(settings);
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var day = DateTime.Today.AddDays(-1);
            var vm = new ReportsViewModel(new ReportService(settings)) { Live = live };
            vm.Report = new Report { Range = ReportRange.Day, From = day, To = day.AddDays(1), HasData = true, Temps = Temps(day) };
            vm.Ready = true;
            var view = new Views.ReportsView { DataContext = vm };
            var window = new Window { Content = view, Left = -32000, Top = -32000, Width = 1440, Height = 900, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                Ui.Pump(150);
                var timeline = (DayTimeline)view.FindName("Timeline");
                Assert.True(timeline.ActualWidth > 500, "the timeline isn't shown");
                Point Where() => timeline.TranslatePoint(new Point(0, 0), view);
                var before = (Where(), timeline.ActualWidth);
                var button = Visuals.Descendants<System.Windows.Controls.Button>(view).Single(b => b.Command == timeline.ResetZoomCommand);
                Assert.Equal(Visibility.Hidden, button.Visibility);

                Drag(timeline, 300, 600);
                Ui.Pump(150);
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.Equal(before, (Where(), timeline.ActualWidth));
                // The switch picks what the timeline draws, and Temperatures' pick with it.
                vm.ChartPlot = "High";
                Assert.Equal(("High", "High"), (timeline.Plot, live.ChartPlot));
                button.Command.Execute(null);
                Ui.Pump(150);
                Assert.Equal(Visibility.Hidden, button.Visibility);
                Assert.Equal(before, (Where(), timeline.ActualWidth));
                live.ChartPlot = "Avg";
            }
            finally { window.Close(); }
        });
        Ui.AssertNoProblems("reports minute by minute");
    }

    [Fact]
    public void The_temperatures_history_card_loads_with_its_zoom()
    {
        var (_, live) = Kit.Greeted();
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var view = new Views.TemperaturesView { DataContext = live };
            var window = new Window { Content = view, Left = -32000, Top = -32000, Width = 1440, Height = 900, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            try
            {
                Ui.Pump(150);
                var chart = (LineChart)view.FindName("HistoryChart");
                Assert.True(chart.CanZoom);
                var button = Visuals.Descendants<System.Windows.Controls.Button>(view).Single(b => b.Command == chart.ResetZoomCommand);
                Assert.Equal(Visibility.Hidden, button.Visibility);
                Assert.Equal(LineChart.ResetTextOf(live.ChartWindowSeconds), chart.ResetText);
            }
            finally { window.Close(); }
        });
        Ui.AssertNoProblems("temperatures history");
    }

    private static ChartSeries Minutes(string label, DateTime day)
    {
        var sensor = new SensorItem(new SensorMeta { Id = "/t/" + label, Name = label, Kind = SensorKind.Temperature }, "CPU", "Cpu");
        var series = new ChartSeries(label, sensor, "CpuColor", m => m.CpuTemp, m => (m.CpuTemp, m.CpuTempMax, m.CpuTempMin));
        long start = TimeUtilStart(day);
        series.LoadMinutes(Enumerable.Range(0, 1440).Select(i => new Rigsight.Core.Data.SystemMinute { Ts = start + i * 60L, CpuTemp = 40 + i % 30, CpuTempMax = 75 + i % 5, CpuTempMin = 35 }));
        return series;
    }

    private static long TimeUtilStart(DateTime day) => new DateTimeOffset(day).ToUnixTimeSeconds();

    [Fact]
    public void The_history_chart_zooms_into_its_minutes_and_a_new_period_starts_whole()
    {
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var day = DateTime.Today.AddDays(-2);
            var chart = new LineChart { WindowSeconds = 0, Day = day, Series = [Minutes("CPU", day)], CanZoom = true };
            var whole = Pixels(Draw.Render(chart, 900, 300));
            Assert.Equal("Whole day", chart.ResetText);
            // 42 px of axis, 852 px of plot: a drag of about 20 pixels is half an hour.
            Drag(chart, 400, 420);
            Assert.True(chart.IsZoomed);
            var zoomed = Pixels(Draw.Render(chart, 900, 300));
            Assert.False(whole.AsSpan().SequenceEqual(zoomed), "the zoomed chart drawn as the whole day");
            var shown = Draw.Get<(DateTime From, DateTime To)>(chart, "_shown");
            Assert.True(shown.To - shown.From >= TimeSpan.FromMinutes(15) && shown.To - shown.From < TimeSpan.FromHours(1), $"zoomed to {shown}");
            Draw.Set(chart, "_hoverX", 450.0);
            Draw.Render(chart, 900, 300);

            // A tiny drag zooms to 15 minutes, no less (on a wide chart, where 10 pixels are under 8 minutes).
            chart.ResetZoom();
            Draw.Render(chart, 2000, 300);
            Drag(chart, 1000, 1010);
            Draw.Render(chart, 2000, 300);
            shown = Draw.Get<(DateTime From, DateTime To)>(chart, "_shown");
            Assert.Equal(TimeSpan.FromMinutes(15), shown.To - shown.From);

            // Another period picked: whole again, and the button says that period.
            chart.WindowSeconds = 86400;
            Assert.False(chart.IsZoomed);
            Assert.Equal("Back to 24 hours", chart.ResetText);
            Drag(chart, 300, 600);
            chart.Day = day.AddDays(-1);
            Assert.False(chart.IsZoomed);

            // The five minutes draw every reading: nothing to zoom into there.
            chart.WindowSeconds = 300;
            Draw.Render(chart, 900, 300);
            Drag(chart, 300, 600);
            Assert.False(chart.IsZoomed);
        });
        Ui.AssertNoProblems("history chart zoom");
    }
}
