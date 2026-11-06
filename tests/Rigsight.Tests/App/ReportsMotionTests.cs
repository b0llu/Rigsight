using Rigsight.Controls;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>
/// How Reports' figures move from one period to the next (issue #11, mock-up ReK33EVbdKbsoarY7jwYXQ; 1.6.1, after 1.6.0's
/// motion was over before the first new frame showed it): figures count when they read alike, and the totals over a
/// month's bars shorten rather than run into each other.
/// </summary>
[Collection("UI")]
public sealed class ReportsMotionTests
{
    [Theory]
    [InlineData("36h 42m", "78h 06m", 0.5, "57h 24m")]
    [InlineData("38m", "12h 45m", 1, "12h 45m")]
    [InlineData("53 GB", "512 GB", 0, "53 GB")]
    [InlineData("5.8 kWh", "30.6 kWh", 0.5, "18.2 kWh")]
    [InlineData("34h 45m in use · 4h 48m away", "241h 10m in use · 20h 00m away", 1, "241h 10m in use · 20h 00m away")]
    public void A_figure_counts_from_the_old_one_when_the_two_read_alike(string from, string to, double t, string expected) =>
        Assert.Equal(expected, CountUp.Between(from, to, t));

    [Theory]
    [InlineData("947 GB", "1.1 TB")]
    [InlineData("None", "2")]
    [InlineData("—", "10h 19m")]
    [InlineData("12 GB", "12 GB used")]
    public void A_figure_that_reads_differently_just_changes(string from, string to) => Assert.Null(CountUp.Between(from, to, 0.5));

    [Fact]
    public void The_motion_eases_out_as_the_mock_up_does()
    {
        Assert.Equal(0, ChartTween.Ease(0));
        Assert.Equal(1, ChartTween.Ease(1));
        Assert.Equal(1 - Math.Pow(0.5, 4), ChartTween.Ease(0.5), 9);
        Assert.Equal(250, ChartTween.Duration.TotalMilliseconds);
        // Two frames pass before it starts: the one that lays the new figures out, and the first that shows them.
        Assert.Equal(2, Frames.Settle);
    }

    [Fact]
    public void The_same_days_read_again_are_not_another_period()
    {
        // Coming back to the page or the minute's refresh brings the same days with new figures: nothing moves
        // (2026-11-06, the user: coming back "should not animate"). Another week, or hours to days, does.
        var monday = new DateTime(2026, 11, 2);
        List<Rigsight.Core.Reports.DayBucket> Week(DateTime first, double sec) =>
            [.. Enumerable.Range(0, 7).Select(i => new Rigsight.Core.Reports.DayBucket { Day = first.AddDays(i), ActiveSec = sec })];
        Assert.True(ChartTween.SameDays(Week(monday, 3600), Week(monday, 7200)));
        Assert.False(ChartTween.SameDays(Week(monday, 3600), Week(monday.AddDays(7), 3600)));
        Assert.False(ChartTween.SameDays(Week(monday, 3600), Week(monday, 3600).Take(6).ToList()));
        Assert.False(ChartTween.SameDays(null, Week(monday, 3600)));
        Assert.False(ChartTween.SameDays([], []));
    }

    [Fact]
    public void Figures_read_again_just_show_and_another_periods_count()
    {
        // Pumped from the test's thread between steps, so the window draws its frames (the motion runs on them).
        var (window, area, figure, bar) = Ui.Run(() =>
        {
            var figure = new System.Windows.Controls.TextBlock();
            var bar = new System.Windows.Controls.ProgressBar { Minimum = 0, Maximum = 1 };
            SmoothBar.SetKey(bar, "Games (motion test)");
            var area = new System.Windows.Controls.StackPanel { Children = { figure, bar } };
            var window = new System.Windows.Window { Content = area, Width = 300, Height = 200, Left = -3000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            return (window, area, figure, bar);
        });
        try
        {
            Ui.Pump(100);
            Ui.Run(() => { CountUp.SetText(figure, "1h 00m"); SmoothBar.SetValue(bar, 0.2); });
            Ui.Pump(600);
            // The same period read again: the new figures show at once.
            Ui.Run(() =>
            {
                Motion.SetMoves(area, false);
                CountUp.SetText(figure, "9h 00m");
                SmoothBar.SetValue(bar, 0.9);
                Assert.Equal("9h 00m", figure.Text);
                Assert.Equal(0.9, bar.Value, 6);
            });
            if (!Ui.Run(() => System.Windows.SystemParameters.ClientAreaAnimation)) return;
            // Another period: they count and slide there, from where they were.
            Ui.Run(() =>
            {
                Motion.SetMoves(area, true);
                CountUp.SetText(figure, "3h 00m");
                SmoothBar.SetValue(bar, 0.4);
                Assert.Equal("9h 00m", figure.Text);
            });
            var seen = new List<string>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 900)
            {
                Ui.Pump(10);
                seen.Add(Ui.Run(() => figure.Text));
            }
            Assert.Contains(seen, t => t is not ("9h 00m" or "3h 00m")); // on its way
            Ui.Run(() =>
            {
                Assert.Equal("3h 00m", figure.Text);
                Assert.Equal(0.4, bar.Value, 6);
            });
        }
        finally { Ui.Run(window.Close); }
    }

    [Fact]
    public void The_dashed_lines_name_is_for_the_cards_heading()
    {
        // On the line it found no free spot when every day was near the average, and sat on a bar and its total
        // (2026-11-06): the heading says it instead, only while the graph draws the line.
        var today = new DateTime(2026, 11, 6);
        var now = today.AddHours(15);
        Rigsight.Core.Reports.DayBucket D(DateTime day, double hours) => new() { Day = day, ActiveSec = hours * 3600 };
        List<Rigsight.Core.Reports.DayBucket> days = [D(today.AddDays(-3), 12), D(today.AddDays(-2), 13), D(today.AddDays(-1), 13), D(today, 5)];
        Assert.Equal("Average day 12h 40m", DailyBars.KeyFor(days, null, null, null, now));
        // A kind picked hides the line; so does a year's months (they aren't days), and one used day is its own average.
        Assert.Null(DailyBars.KeyFor(days, [AppCategory.Game], null, null, now));
        Assert.Null(DailyBars.KeyFor([D(new DateTime(2026, 1, 1), 300), D(new DateTime(2026, 2, 1), 280), D(new DateTime(2026, 3, 1), 250)], null, null, null, now));
        Assert.Null(DailyBars.KeyFor([D(today.AddDays(-1), 4), D(today, 9)], null, null, null, now));
        Assert.Null(DailyBars.KeyFor(null, null, null, null, now));
        // A day's hours: the usual weekday's line, named when there is one.
        List<Rigsight.Core.Reports.DayBucket> hours = [.. Enumerable.Range(0, 24).Select(h => D(today.AddHours(h), 0.5))];
        Assert.Equal("Usual Friday", DailyBars.KeyFor(hours, null, [.. Enumerable.Repeat(600.0, 24)], "Usual Friday", now));
        Assert.Null(DailyBars.KeyFor(hours, null, null, "Usual Friday", now));
        Assert.Null(DailyBars.KeyFor(hours, null, [.. Enumerable.Repeat(600.0, 24)], null, now));
    }

    [Fact]
    public void Each_temperature_bar_runs_from_its_lowest_to_its_highest_on_a_scale_in_tens()
    {
        // Mock-up Gvq1nXbE8Ubq4W1Q2CHzQb, A (2026-11-06): the user's 14 October ran from 38° to 82° on the CPU.
        var day = new DateTime(2026, 10, 14);
        var full = new Rigsight.Core.Reports.DayBucket { Day = day, CpuTempMin = 38, CpuTempAvg = 55, CpuTempMax = 82, GpuTempMin = 37, GpuTempAvg = 55, GpuTempMax = 72 };
        Assert.Equal(new PeriodTemps.Spread(38, 55, 82), PeriodTemps.SpreadOf(full).Cpu);
        Assert.Equal(new PeriodTemps.Spread(37, 55, 72), PeriodTemps.SpreadOf(full).Gpu);
        // From older totals, with no lowest kept: from its average. A part never reported has nothing to draw.
        var old = new Rigsight.Core.Reports.DayBucket { Day = day, CpuTempAvg = 50, CpuTempMax = 70 };
        Assert.Equal(new PeriodTemps.Spread(50, 50, 70), PeriodTemps.SpreadOf(old).Cpu);
        Assert.False(PeriodTemps.SpreadOf(old).Gpu.Any);
        // The scale: lowest to highest in whole tens; a quiet period is still 30° tall, around its middle.
        Assert.Equal((30.0, 90.0), PeriodTemps.ScaleOf([34, 83, 38, 74]));
        Assert.Equal((60.0, 90.0), PeriodTemps.ScaleOf([74, 83]));
        Assert.Equal((30.0, 90.0), PeriodTemps.ScaleOf([]));
    }

    [Theory]
    [InlineData(11 * 3600 + 37 * 60, "12h")]
    [InlineData(2 * 3600 + 41 * 60, "3h")]
    [InlineData(38 * 60, "38m")]
    public void A_total_with_no_room_is_said_in_whole_hours(double sec, string expected) => Assert.Equal(expected, DailyBars.WholeHours(sec));
}
