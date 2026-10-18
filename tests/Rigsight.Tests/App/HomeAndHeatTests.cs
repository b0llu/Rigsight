using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;
using Rigsight.Models;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// Home's recap card that fills its height and its three small cards, and the foot of the Temperatures page: the
/// week by hour and the chips at rest as lines (its drives and board cards: <see cref="TemperaturePartCardsTests"/>).
/// </summary>
[Collection("UI")]
public sealed class HomeAndHeatTests
{
    // ── The recap card's list ──

    [Fact]
    public void The_list_keeps_whole_rows_lowest_ranks_first()
    {
        // Three rows of 30, ranks in the order they stand: as many as fit, never part of one.
        (double, int)[] rows = [(30, 0), (30, 1), (30, 2)];
        Assert.Equal([true, true, true], FitStack.Fit(rows, 90));
        Assert.Equal([true, true, false], FitStack.Fit(rows, 89));
        Assert.Equal([false, false, false], FitStack.Fit(rows, 10));
        // Ranks out of order: the lower rank stays, wherever it stands.
        Assert.Equal([false, true, true], FitStack.Fit([(30, 5), (30, 0), (30, 1)], 60));
        // A tall row that doesn't fit ends it: a later, shorter one doesn't jump the queue.
        Assert.Equal([true, false, false], FitStack.Fit([(30, 0), (50, 1), (10, 2)], 70));
        Assert.Empty(FitStack.Fit([], 100));
    }

    [Fact]
    public void The_recap_shows_three_apps_and_two_lines_at_the_least_then_more_by_turns()
    {
        var apps = Enumerable.Range(0, 6).Select(i => new AppStat { Name = $"App {i}", ActiveSec = 600 - i }).ToList();
        var lines = Enumerable.Range(0, 5).Select(i => new Insight("", $"Line {i}")).ToList();
        var rows = HomeViewModel.RowsOf(apps, lines);
        // On the card: the apps, then the lines.
        Assert.Equal(["App 0", "App 1", "App 2", "App 3", "App 4", "App 5", "Line 0", "Line 1", "Line 2", "Line 3", "Line 4"],
            rows.Select(r => r.App?.Name ?? r.Line!.Text));
        Assert.Equal(["App 0", "App 1", "App 2", "Line 0", "Line 1"], rows.Where(r => r.Essential).Select(r => r.App?.Name ?? r.Line!.Text));
        // As the card grows: a line, an app, a line, an app…
        Assert.Equal(["App 0", "App 1", "App 2", "Line 0", "Line 1", "Line 2", "App 3", "Line 3", "App 4", "Line 4", "App 5"],
            rows.OrderBy(r => r.Rank).Select(r => r.App?.Name ?? r.Line!.Text));
        Assert.Equal(rows.Count, rows.Select(r => r.Rank).Distinct().Count());
        Assert.Equal("Line 0", rows.Single(r => r.StartsLines).Line!.Text);
        Assert.All(rows, r => Assert.Equal(r.App is not null, r.IsApp));
        Assert.Empty(HomeViewModel.RowsOf([], []));
    }

    // ── The small cards ──

    [Fact]
    public void What_changed_lately_is_the_latest_of_the_timeline_with_its_day()
    {
        var today = new DateTime(2026, 10, 10);
        SystemChange Change(int daysAgo, ChangeKind kind, string title) => new(today.AddDays(-daysAgo).AddHours(9), kind, title);
        var lines = HomeViewModel.HomeChangeLines(
        [
            Change(9, ChangeKind.WindowsUpdate, "Windows update installed"), Change(2, ChangeKind.AppUpdated, "Discord updated"),
            Change(0, ChangeKind.Storage, "63 GB added to the C drive"), Change(0, ChangeKind.AppInstalled, "Grounded 2 installed"),
            Change(1, ChangeKind.Driver, "Graphics driver updated"), Change(20, ChangeKind.Setting, "Fast Startup turned off"),
        ], today, new Dictionary<DateTime, ChangeResult>());
        Assert.Equal(HomeViewModel.CardLines, lines.Count);
        Assert.Equal(["Today", "Today", "Yesterday", "Thursday"], lines.Select(l => l.When));
        Assert.Contains("63 GB added to the C drive", lines.Select(l => l.Text));
        Assert.Equal("Graphics driver updated", lines[2].Text);
        Assert.Equal(("PurpleBrush", "GpuBrush", "CpuBrush"), (lines.Single(l => l.Text.StartsWith("63")).Brush, lines[2].Brush, lines[3].Brush));
        Assert.All(lines, l => Assert.False(string.IsNullOrEmpty(l.Icon)));
        Assert.Empty(HomeViewModel.HomeChangeLines([], today, new Dictionary<DateTime, ChangeResult>()));

        Assert.Equal("Today", HomeViewModel.DayWord(today, today));
        Assert.Equal("Yesterday", HomeViewModel.DayWord(today.AddDays(-1), today));
        Assert.Equal("Sunday", HomeViewModel.DayWord(today.AddDays(-6), today));
        Assert.Equal("3 Oct", HomeViewModel.DayWord(today.AddDays(-7), today));
    }

    [Fact]
    public void Pc_health_says_each_part_against_the_usual_in_one_statement_and_only_what_needs_a_look_opens()
    {
        static CrashEvent Down(CrashKind kind) => new() { Kind = kind, Moment = PowerMoment.Running, Ts = 1 };
        var day = DateTime.Today;
        // Today's report as Home has it: its usual, and the lines worded from it (the same words as Reports).
        static Report Today(PcUsual? usual, params Insight[] more)
        {
            var r = new Report { Range = ReportRange.Day, From = DateTime.Today, To = DateTime.Today.AddDays(1), Usual = usual };
            var lines = new List<Insight>();
            if (usual is not null && UsualLines.Heat(usual, true, DateTime.Today) is { } heat) lines.Add(heat);
            if (usual?.Problems is { } pr && UsualLines.Problems(pr, UsualLines.ProblemsSpan(r, DateTime.Today)) is { } problems) lines.Add(problems);
            r.Insights = InsightLinks.Apply(lines.Concat(more));
            return r;
        }
        static UsualHeat Heat(double now, EffectTone tone) => new("Crimson Desert", "GPU", now, 68, 67, 69, 300, 735, 5, 48, 48, 294, 295, tone) { LastDay = DateTime.Today, LastTemp = now };

        var calm = new PcUsual
        {
            Parts = [Heat(70, EffectTone.Same)], Heat = Heat(70, EffectTone.Same),
            RestNow = new LoadTemps(49, 48, 300), RestUsual = new LoadTemps(49, 48, 3000),
            Fans = [new UsualFan("Fan #2", 1645, 1630, 1655, null)],
            Problems = new UsualProblems([], 9, 80, 190, 2.4, EffectTone.Same, 1, 12) { PowerLikelyNow = 1 },
        };
        var fine = HomeViewModel.HealthLines(Today(calm), [], 0, [], "the E drive is 73% full", null);
        Assert.Equal(["Temperatures as usual", "Fans at their usual speeds", "No PC problems in the last 7 days", "1 likely power cut in the last 7 days", "The E drive is 73% full"],
            fine.Select(l => l.Text));
        // Nothing in it needs a look: nothing is amber and nothing opens.
        Assert.All(fine, l => Assert.False(l.CanOpen));
        Assert.All(fine, l => Assert.Null(l.TextBrush));
        Assert.Equal("MutedBrush", fine.Single(l => l.Text.Contains("power cut")).Brush);

        var off = new PcUsual
        {
            Parts = [Heat(77, EffectTone.Worse)], Heat = Heat(77, EffectTone.Worse),
            Problems = new UsualProblems([Down(CrashKind.GpuDriverReset), Down(CrashKind.GpuDriverReset), Down(CrashKind.GpuDriverReset)], 0, 80, 190, 2.4, EffectTone.Worse, 0, 0),
        };
        var fan = new Insight("", "Fan #2 on your motherboard stopped for 12 minutes from 3:10 PM, when it always turns. Check it isn't blocked or unplugged.", InsightTone.Warn, "fan-stopped")
            { Short = "Fan #2 on your motherboard stopped for 12 minutes" };
        var worseDriver = new HomeLine("", "WarmBrush", "Driver 618.10: Crimson Desert runs 10° hotter on the GPU") { TextBrush = "WarmBrush", ChangeDay = day.AddDays(-3) };
        var bad = HomeViewModel.HealthLines(Today(off, fan), [], 0, [], null, worseDriver);
        // The same words as the insight list; the driver's own line isn't said here too (PC health already says the heat).
        Assert.Equal(["Crimson Desert runs 9° hotter than usual", "Fan #2 on your motherboard stopped for 12 minutes", "3 graphics driver resets in the last 7 days"], bad.Select(l => l.Text));
        Assert.All(bad, l => Assert.Equal("WarmBrush", l.TextBrush));
        Assert.Equal([InsightOpens.Usual, InsightOpens.Fans, InsightOpens.Crashes], bad.Select(l => l.Opens));
        // A worse driver with nothing else saying it: it's PC health's first line.
        Assert.Equal(worseDriver, HomeViewModel.HealthLines(Today(calm), [], 0, [], null, worseDriver)[0]);

        // Too little history for a usual: the last 7 days' problems as they are, and nothing else is claimed.
        var young = HomeViewModel.HealthLines(Today(null), [CrashKind.GpuDriverReset, CrashKind.SystemCrash, CrashKind.GpuDriverReset], 1, [], null, null);
        Assert.Equal(["2 graphics driver resets and 1 blue screen in the last 7 days", "1 likely power cut in the last 7 days"], young.Select(l => l.Text));
        Assert.Equal(InsightOpens.Crashes, young[0].Opens);
        Assert.Equal("No PC problems in the last 7 days", HomeViewModel.HealthLines(Today(null), [], 0, [], null, null).Single().Text);

        // Fewer problems than usual is good news, with the figures one click away.
        var better = HomeViewModel.HealthLines(Today(new PcUsual { Problems = new UsualProblems([], 30, 80, 190, 8.6, EffectTone.Better, 0, 0) }), [], 0, [], null, null);
        Assert.Equal(("Fewer PC problems than usual", "GoodBrush", InsightOpens.Usual), (better[0].Text, better[0].Brush, better[0].Opens));

        Assert.Equal("the C drive is 58% full", HomeViewModel.FullText(@"C:\", 272L << 30, 465L << 30));
    }

    [Fact]
    public void What_changed_lately_is_one_statement_a_change_with_when_at_the_right_and_a_result_on_top()
    {
        var today = new DateTime(2026, 10, 17);
        var driver = new SystemChange(today.AddDays(-7).AddHours(17), ChangeKind.Driver, "NVIDIA graphics driver 618.10") { Now = "618.10" };
        var chrome = new SystemChange(today.AddDays(-1).AddHours(9), ChangeKind.AppUpdated, "Google Chrome updated to 155.0.8059.80");
        var result = new ChangeResult { Day = driver.Time.Date, Changes = [driver], Tone = EffectTone.Worse, Plain = "Crimson Desert runs 10° hotter on the GPU" };
        var lines = HomeViewModel.HomeChangeLines([driver, chrome], today, new Dictionary<DateTime, ChangeResult> { [result.Day] = result });
        Assert.Equal(("Driver 618.10: Crimson Desert runs 10° hotter on the GPU", "WarmBrush", true), (lines[0].Text, lines[0].TextBrush, lines[0].CanOpen));
        Assert.Equal(("Google Chrome updated", "Yesterday", false), (lines[1].Text, lines[1].When, lines[1].CanOpen));
        Assert.All(lines, l => Assert.Null(l.Detail));
    }

    // ── The week by hour ──

    [Fact]
    public void An_hour_is_coloured_by_the_hotter_of_the_two_chips_and_says_what_it_held()
    {
        var hour = new DateTime(2026, 10, 8, 19, 0, 0);
        Assert.Equal(0, HeatGrid.Level(new HeatHour(hour, 40, 38, null)));
        Assert.Equal(1, HeatGrid.Level(new HeatHour(hour, 60, 44, null)));
        Assert.Equal(2, HeatGrid.Level(new HeatHour(hour, 60, 71, null))); // the GPU is warm, the CPU isn't
        Assert.Equal(3, HeatGrid.Level(new HeatHour(hour, 86, 60, null)));
        Assert.Equal(2, HeatGrid.Level(new HeatHour(hour, null, 80, null)));
        Assert.Null(HeatGrid.Level(new HeatHour(hour, null, null, null)));

        // The colour goes by the hour's average: a spike of a moment doesn't turn an hour of browsing amber.
        Assert.Equal(1, HeatGrid.Level(new HeatHour(hour, 52, 46, null) { CpuHigh = 78, GpuHigh = 50 }));

        Assert.Equal("Thu 7 PM · CPU 74°, GPU 68° · Apex Legends", HeatGrid.TipOf(hour, new HeatHour(hour, 74, 68, "Apex Legends")));
        // Under the pointer: the average, and the highest where it says more.
        Assert.Equal("Thu 7 PM · CPU 66° (up to 74°), GPU 68° · Apex Legends",
            HeatGrid.TipOf(hour, new HeatHour(hour, 66, 68, "Apex Legends") { CpuHigh = 74, GpuHigh = 68.2 }));
        Assert.Equal("Thu 7 PM · CPU 52°", HeatGrid.TipOf(hour, new HeatHour(hour, 52, null, null)));
        Assert.Equal("Thu 7 PM · Not recorded", HeatGrid.TipOf(hour, null));
        Assert.Equal("Thu 7 PM · No reading", HeatGrid.TipOf(hour, new HeatHour(hour, null, null, "Idle")));
    }

    // ── At rest, as lines ──

    [Fact]
    public void A_chip_at_rest_carries_its_last_month_day_by_day()
    {
        var (_, live) = Kit.Greeted();
        var today = DateTime.Today;
        HeatDay Rest(int daysAgo, double cpu, double gpu, int minutes = 60) => new(TimeUtil.ToUnix(today.AddDays(-daysAgo)), 0, minutes, gpu * minutes, minutes, cpu * minutes, minutes, 0, 0);
        Ui.Run(() =>
        {
            live.LoadRest([Rest(0, 45, 41), Rest(1, 44, 40), Rest(3, 43, 40), Rest(5, 99, 99, minutes: RestTemps.MinMinutes - 1)]);
            var cpu = live.Rest[0];
            // From the first day with any rest (three days back) to today.
            Assert.Equal(("CPU", "CpuBrush", 4), (cpu.Part, cpu.Brush, cpu.Days.Count));
            Assert.Equal([43.0, null, 44.0, 45.0], cpu.Days); // a day with too little rest has no point (and doesn't start the line)
            Assert.True(cpu.HasLine);
            Assert.Equal(cpu.Days.Count, cpu.Tips.Count);
            Assert.Equal($"{today:d MMM} · 45°", cpu.Tips[^1]);
            Assert.Equal($"{today.AddDays(-2):d MMM} · No rest recorded", cpu.Tips[^3]);
            Assert.Equal(41.0, live.Rest[1].Days[^1]);
            Assert.Equal("GpuBrush", live.Rest[1].Brush);

            // One day only: a figure, and nothing to draw a line through.
            live.LoadRest([Rest(0, 45, 41)]);
            Assert.False(live.Rest[0].HasLine);
        });
    }

    // ── Motherboard and drives ──

    [Fact]
    public void Tiles_take_as_many_columns_as_fit_and_keep_their_size_when_few()
    {
        var panel = Ui.Run(() => new TileWrap());
        Ui.Run(() =>
        {
            // 170 wide at the least, 10 apart: a 900-wide card takes five across.
            Assert.Equal(5, panel.Fit(900, 9).Columns);
            Assert.Equal((900 - 4 * 10) / 5.0, panel.Fit(900, 9).Width);
            // Two tiles in that card keep a quarter of it each, not half.
            Assert.Equal(4, panel.Fit(900, 2).Columns);
            // A narrow window: as many as fit, one at the least.
            Assert.Equal(2, panel.Fit(360, 9).Columns);
            Assert.Equal(1, panel.Fit(120, 9).Columns);
            // A very wide one stops at eight.
            Assert.Equal(TileWrap.MaxColumns, panel.Fit(4000, 20).Columns);
        });
    }

    [Fact]
    public void A_reading_knows_where_it_sits_in_todays_range()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            var sensor = live.AllSensors.First(x => x.Kind == SensorKind.Temperature);
            sensor.SetTodayRange(40, 60);
            long t = TimeUtil.NowUnixMs();
            var changes = Kit.Changes(sensor, () => sensor.Push(t, 55));
            Assert.Equal(75, sensor.RangeShare);
            Assert.Contains(nameof(SensorItem.RangeShare), changes);
            Assert.Contains(nameof(SensorItem.RangeText), changes);
            Assert.Equal("40° to 60° today", sensor.RangeText);
            sensor.Push(t + 2000, 70); // a new high: it is the top of the range
            Assert.Equal(100, sensor.RangeShare);
            // A range with no width yet: the middle, not an empty bar.
            sensor.SetTodayRange(50, 50);
            sensor.Push(t + 4000, 50);
            Assert.Equal(50, sensor.RangeShare);
        });
    }
}
