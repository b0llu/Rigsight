using System.Collections.Concurrent;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Reports;

/// <summary>
/// Reports built from generated history, for every kind of period, each checked against what's in the database and
/// against itself: totals add up, lists are in order, peaks are the real highs, nothing is NaN or negative.
/// </summary>
public sealed class ReportInvariantTests
{
    /// <summary>"profile/range/days back": the anchor is that many days before the seed's "now".</summary>
    public static TheoryData<string> Cases
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var r in new[] { "Day", "Week", "Month" })
            {
                foreach (var back in new[] { 0, 1, 10 }) data.Add($"small/{r}/{back}");
                foreach (var back in new[] { 0, 1, 40, 200 }) data.Add($"typical/{r}/{back}");
            }
            data.Add("small/Year/0");
            data.Add("small/All/0");
            data.Add("typical/Year/0");
            data.Add("typical/Year/370");
            data.Add("typical/All/0");
            return data;
        }
    }

    private static readonly ConcurrentDictionary<string, Lazy<Report>> Built = new();

    private static (Report Report, string Profile) Get(string c)
    {
        var parts = c.Split('/');
        var report = Built.GetOrAdd(c, _ => new Lazy<Report>(() =>
        {
            using var db = Seeds.Open(parts[0]);
            var now = parts[0] == "small" ? Seeds.SmallNow : Seeds.TypicalNow;
            var range = Enum.Parse<ReportRange>(parts[1]);
            var anchor = now.AddDays(-int.Parse(parts[2]));
            // The PC isn't on every day: a day report goes back to the nearest day it was.
            while (range == ReportRange.Day && db.GetMinutes(TimeUtil.ToUnix(anchor.Date), TimeUtil.ToUnix(anchor.Date.AddDays(1))).Count == 0)
                anchor = anchor.AddDays(-1);
            return ReportBuilder.Build(db, range, anchor, Make.Settings());
        })).Value;
        return (report, parts[0]);
    }

    private static (long From, long To) Unix(Report r) => (TimeUtil.ToUnix(r.From), TimeUtil.ToUnix(r.To));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Has_data_and_no_NaN_infinite_or_negative_number_anywhere(string c)
    {
        var (r, _) = Get(c);
        Assert.True(r.HasData);
        ReportCheck.NoBadNumbers(r);
        Assert.False(string.IsNullOrWhiteSpace(r.Title));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Active_and_away_time_fit_inside_the_time_on(string c)
    {
        var (r, _) = Get(c);
        Assert.True(r.OnSec > 0);
        Assert.True(r.ActiveSec <= r.OnSec, $"active {r.ActiveSec} > on {r.OnSec}");
        Assert.True(r.ActiveSec + r.AwaySec <= r.OnSec + 1e-6, $"active {r.ActiveSec} + away {r.AwaySec} > on {r.OnSec}");
        Assert.True(r.OnSec <= (r.To - r.From).TotalSeconds);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Time_on_active_and_away_are_what_the_database_holds_for_the_period(string c)
    {
        var (r, p) = Get(c);
        using var t = TestDb.At(Seeds.PathOf(p));
        var (f, to) = Unix(r);
        var row = t.Rows($"SELECT count(*) AS n, total(active_sec) AS a, total(idle_sec) AS i FROM system_minute WHERE ts >= {f} AND ts < {to}")[0];
        Assert.Equal((long)row["n"]! * 60.0, r.OnSec);
        Assert.Equal((double)row["a"]!, r.ActiveSec, 6);
        Assert.Equal((double)row["i"]!, r.AwaySec, 6);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void App_times_add_up_to_the_reports_totals(string c)
    {
        var (r, _) = Get(c);
        Assert.Equal(r.ActiveSec, r.Apps.Sum(a => a.ActiveSec), 3);
        Assert.Equal(r.AwaySec, r.Apps.Sum(a => a.AwaySec), 3);
        Assert.Equal(r.Apps.Sum(a => a.ActiveSec), r.ActiveByCategory.Values.Sum(), 3);
        Assert.Equal(r.Apps.Where(a => a.Category == AppCategory.Game).Sum(a => a.ActiveSec), r.GamingSec, 3);
        foreach (var (cat, sec) in r.ActiveByCategory)
            Assert.Equal(r.Apps.Where(a => a.Category == cat).Sum(a => a.ActiveSec), sec, 3);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Apps_come_most_used_first_each_once_with_sane_numbers(string c)
    {
        var (r, _) = Get(c);
        Assert.NotEmpty(r.Apps);
        Assert.Equal(r.Apps.Count, r.Apps.Select(a => a.Id).Distinct().Count());
        for (int i = 1; i < r.Apps.Count; i++)
        {
            var (a, b) = (r.Apps[i - 1], r.Apps[i]);
            Assert.True(a.ActiveSec > b.ActiveSec || (a.ActiveSec == b.ActiveSec && a.OpenSec >= b.OpenSec), $"{a.Name} before {b.Name}");
        }
        foreach (var a in r.Apps)
        {
            Assert.False(string.IsNullOrEmpty(a.Name));
            Assert.Equal(a.ActiveSec + a.AwaySec + a.BackgroundSec + a.MinimizedSec, a.OpenSec, 6);
            Assert.True(a.CpuTempAvg is null || a.CpuTempAvg <= a.CpuTempMax + 1e-9, $"{a.Name}: avg {a.CpuTempAvg} > max {a.CpuTempMax}");
            Assert.True(a.GpuTempAvg is null || a.GpuTempAvg <= a.GpuTempMax + 1e-9, $"{a.Name}: gpu avg {a.GpuTempAvg} > max {a.GpuTempMax}");
            Assert.True(a.MemAvg is null || a.MemAvg <= a.MemMax + 1e-9, $"{a.Name}: mem avg {a.MemAvg} > max {a.MemMax}");
            Assert.True(a.SessionCount == 0 || a.LongestSessionSec >= ReportBuilder.MinSessionSec);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_peak_is_the_databases_highest_value_in_the_period(string c)
    {
        var (r, p) = Get(c);
        using var t = TestDb.At(Seeds.PathOf(p));
        var (f, to) = Unix(r);
        foreach (var (peak, column) in new[] { (r.CpuTempPeak, "cpu_temp_max"), (r.GpuTempPeak, "gpu_temp_max"), (r.GpuHotPeak, "gpu_hot_max"),
            (r.CpuVoltPeak, "cpu_volt_max"), (r.GpuVoltPeak, "gpu_volt_max"), (r.CpuPowerPeak, "cpu_power"), (r.GpuPowerPeak, "gpu_power") })
        {
            var max = Rollups.Num(t.Scalar($"SELECT max({column}) FROM system_minute WHERE ts >= {f} AND ts < {to}"));
            Assert.True(peak is not null, column);
            Assert.Equal(max, peak.Value);
            // And it happened then: the first minute of the period with that value.
            var first = (long)t.Scalar($"SELECT min(ts) FROM system_minute WHERE ts >= {f} AND ts < {to} AND {column} = $v", ("$v", max))!;
            Assert.Equal(TimeUtil.FromUnix(first), peak.Time);
            Assert.InRange(peak.Time, r.From, r.To.AddTicks(-1));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Averages_are_the_databases_averages(string c)
    {
        var (r, p) = Get(c);
        using var t = TestDb.At(Seeds.PathOf(p));
        var (f, to) = Unix(r);
        var row = t.Rows($"SELECT avg(cpu_temp) AS c, avg(gpu_temp) AS g, avg(cpu_load) AS cl, avg(gpu_load) AS gl FROM system_minute WHERE ts >= {f} AND ts < {to}")[0];
        Assert.Equal((double)row["c"]!, r.CpuTempAvg!.Value, 6);
        Assert.Equal((double)row["g"]!, r.GpuTempAvg!.Value, 6);
        Assert.Equal((double)row["cl"]!, r.CpuLoadAvg!.Value, 6);
        Assert.Equal((double)row["gl"]!, r.GpuLoadAvg!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Sessions_all_overlap_the_period_are_a_minute_or_longer_and_none_is_missing(string c)
    {
        var (r, p) = Get(c);
        using var t = TestDb.At(Seeds.PathOf(p));
        var (f, to) = Unix(r);
        long count = (long)t.Scalar($"SELECT count(*) FROM sessions WHERE start < {to} AND end > {f} AND active_sec >= 60")!;
        Assert.All(r.Sessions, s =>
        {
            Assert.True(s.Start < r.To && s.End > r.From, $"{s.Name} {s.Start}–{s.End}");
            Assert.True(s.ActiveSec >= ReportBuilder.MinSessionSec);
            Assert.True(s.End >= s.Start);
        });
        if (ReportBuilder.IsLong(r.Range))
        {
            Assert.Equal(Math.Min(200, count), r.Sessions.Count);
            Assert.Equal(r.Sessions.OrderByDescending(s => s.ActiveSec).Select(s => s.ActiveSec), r.Sessions.Select(s => s.ActiveSec));
            double longest = (double)(t.Scalar($"SELECT max(active_sec) FROM sessions WHERE start < {to} AND end > {f}") ?? 0.0);
            Assert.Equal(longest, r.Sessions[0].ActiveSec);
        }
        else
        {
            Assert.Equal(count, r.Sessions.Count);
            Assert.Equal(r.Sessions.OrderBy(s => s.Start).Select(s => s.Start), r.Sessions.Select(s => s.Start));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Session_counts_per_app_match_the_sessions_in_the_period(string c)
    {
        var (r, p) = Get(c);
        using var t = TestDb.At(Seeds.PathOf(p));
        var (f, to) = Unix(r);
        var expected = t.Rows($"SELECT app_id, count(*) AS n, max(active_sec) AS m FROM sessions WHERE start < {to} AND end > {f} AND active_sec >= 60 GROUP BY app_id")
            .ToDictionary(x => (long)x["app_id"]!, x => ((long)x["n"]!, (double)x["m"]!));
        foreach (var a in r.Apps)
            Assert.Equal(expected.GetValueOrDefault(a.Id), ((long)a.SessionCount, a.LongestSessionSec));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Day_buckets_cover_the_period_in_order_and_add_up(string c)
    {
        var (r, _) = Get(c);
        if (r.Range == ReportRange.Day)
        {
            Assert.Empty(r.Days);
            return;
        }
        Assert.Equal(r.Days.OrderBy(d => d.Day).Select(d => d.Day), r.Days.Select(d => d.Day));
        if (ReportBuilder.IsLong(r.Range))
            Assert.All(r.Days, d => Assert.Equal(1, d.Day.Day));
        else
            Assert.Equal((int)(r.To - r.From).TotalDays, r.Days.Count);
        Assert.Equal(r.OnSec, r.Days.Sum(d => d.OnSec), 6);
        Assert.Equal(r.ActiveSec, r.Days.Sum(d => d.ActiveSec), 3);
        Assert.InRange(r.DaysWithData, 1, r.Days.Count);
        foreach (var d in r.Days)
        {
            Assert.Equal(d.ActiveSec, d.ActiveByCategory.Values.Sum(), 3);
            Assert.True(d.OnSec > 0 || d.TopApp is null);
            Assert.True(d.CpuTempAvg is null || d.CpuTempAvg <= d.CpuTempMax, $"{d.Day:d}");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_timeline_and_temperature_curve_cover_every_minute_once(string c)
    {
        var (r, _) = Get(c);
        if (ReportBuilder.IsLong(r.Range))
        {
            Assert.Empty(r.Timeline);
            Assert.Empty(r.Temps);
            return;
        }
        Assert.Equal((int)(r.OnSec / 60), r.Temps.Count);
        Assert.Equal(r.Temps.OrderBy(x => x.Time).Select(x => x.Time), r.Temps.Select(x => x.Time));
        Assert.Equal(r.OnSec, r.Timeline.Sum(s => (s.End - s.Start).TotalSeconds), 3);
        for (int i = 0; i < r.Timeline.Count; i++)
        {
            Assert.True(r.Timeline[i].End > r.Timeline[i].Start);
            if (i > 0) Assert.True(r.Timeline[i].Start >= r.Timeline[i - 1].End, $"segment {i} overlaps the one before");
            Assert.True(r.Timeline[i].App is not null || r.Timeline[i].AppId is null);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Crashes_are_in_the_period_newest_first(string c)
    {
        var (r, p) = Get(c);
        using var t = TestDb.At(Seeds.PathOf(p));
        var (f, to) = Unix(r);
        Assert.Equal((long)t.Scalar($"SELECT count(*) FROM crashes WHERE ts >= {f} AND ts < {to}")!, r.Crashes.Count);
        Assert.Equal(r.Crashes.OrderByDescending(x => x.Ts).Select(x => x.Ts), r.Crashes.Select(x => x.Ts));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Minutes_over_the_alert_limits_are_counted_for_short_periods(string c)
    {
        var (r, p) = Get(c);
        if (ReportBuilder.IsLong(r.Range))
        {
            Assert.Equal(0, r.CpuOverLimitMin);
            return;
        }
        using var t = TestDb.At(Seeds.PathOf(p));
        var (f, to) = Unix(r);
        var alerts = new AlertSettings();
        Assert.Equal((long)t.Scalar($"SELECT count(*) FROM system_minute WHERE ts >= {f} AND ts < {to} AND cpu_temp_max >= {alerts.CpuLimit}")!, r.CpuOverLimitMin);
        Assert.Equal((long)t.Scalar($"SELECT count(*) FROM system_minute WHERE ts >= {f} AND ts < {to} AND gpu_temp_max >= {alerts.GpuLimit}")!, r.GpuOverLimitMin);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_day_shape_is_consistent(string c)
    {
        var (r, _) = Get(c);
        if (ReportBuilder.IsLong(r.Range)) return;
        Assert.NotNull(r.FirstActive);
        Assert.True(r.FirstActive <= r.LastActive);
        Assert.InRange(r.FirstActive!.Value, r.From, r.To);
        Assert.InRange(r.LastActive!.Value, r.From, r.To);
        Assert.True(r.DayStart is null || r.DayStart.Value.Hour >= 5);
        var s = Assert.IsType<Stretch>(r.LongestStretch);
        Assert.True(s.Seconds > 0);
        Assert.InRange(s.Start, r.From, r.To);
        Assert.InRange(s.End, s.Start, r.To);
        foreach (var lt in new[] { r.IdleTemps, r.GpuLoadTemps, r.CpuLoadTemps, r.HotSpotGap })
            Assert.True(lt is null || lt.Minutes > 0);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Insights_are_ranked_and_read_as_plain_sentences(string c)
    {
        var (r, _) = Get(c);
        Assert.NotEmpty(r.Insights);
        Assert.Equal(r.Insights.OrderByDescending(i => i.Priority).Select(i => i.Priority), r.Insights.Select(i => i.Priority));
        foreach (var i in r.Insights)
        {
            Assert.True(i.Text.EndsWith('.') || i.Text.EndsWith(')'), i.Text);
            Assert.DoesNotContain("NaN", i.Text);
            Assert.DoesNotContain("  ", i.Text);
            Assert.DoesNotContain("{", i.Text);
            Assert.DoesNotContain("—°", i.Text);
            Assert.False(string.IsNullOrEmpty(i.Key));
        }
        Assert.Equal(r.Insights.Count, r.Insights.Select(i => i.Text).Distinct().Count());
    }

    // ── Year and all-time come from the daily and monthly totals, but must say what adding up every minute says ──

    public static TheoryData<string, string> LongCases => new() { { "typical", "Year/0" }, { "typical", "Year/370" }, { "typical", "All/0" }, { "small", "All/0" }, { "small", "Year/0" } };

    [Theory]
    [MemberData(nameof(LongCases))]
    public void Long_reports_from_the_totals_equal_reports_from_every_minute(string profile, string c)
    {
        var (fromTotals, _) = Get($"{profile}/{c}");
        using var db = Seeds.Open(profile);
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        var raw = ReportBuilder.BuildRaw(db, fromTotals.Range, fromTotals.From, fromTotals.To, apps, Make.Settings());

        Assert.Equal(raw.HasData, fromTotals.HasData);
        Assert.Equal(raw.OnSec, fromTotals.OnSec);
        Assert.Equal(raw.ActiveSec, fromTotals.ActiveSec, 3);
        Assert.Equal(raw.AwaySec, fromTotals.AwaySec, 3);
        Assert.Equal(raw.GamingSec, fromTotals.GamingSec, 3);
        foreach (var (a, b) in new[] { (raw.CpuTempAvg, fromTotals.CpuTempAvg), (raw.GpuTempAvg, fromTotals.GpuTempAvg), (raw.CpuLoadAvg, fromTotals.CpuLoadAvg), (raw.GpuLoadAvg, fromTotals.GpuLoadAvg) })
            Assert.Equal(a!.Value, b!.Value, 6);
        Assert.Equal(raw.CpuTempPeak, fromTotals.CpuTempPeak);
        Assert.Equal(raw.GpuTempPeak, fromTotals.GpuTempPeak);
        Assert.Equal(raw.GpuHotPeak, fromTotals.GpuHotPeak);
        Assert.Equal(raw.CpuVoltPeak, fromTotals.CpuVoltPeak);
        Assert.Equal(raw.GpuVoltPeak, fromTotals.GpuVoltPeak);
        Assert.Equal(raw.CpuPowerPeak, fromTotals.CpuPowerPeak);
        Assert.Equal(raw.GpuPowerPeak, fromTotals.GpuPowerPeak);
        Assert.Equal(raw.Crashes.Select(x => x.Id), fromTotals.Crashes.Select(x => x.Id));

        var rawApps = raw.Apps.ToDictionary(a => a.Id);
        Assert.Equal(rawApps.Keys.Order(), fromTotals.Apps.Select(a => a.Id).Order());
        foreach (var a in fromTotals.Apps)
        {
            var b = rawApps[a.Id];
            Assert.Equal(b.ActiveSec, a.ActiveSec, 3);
            Assert.Equal(b.BackgroundSec, a.BackgroundSec, 3);
            Assert.Equal(b.SessionCount, a.SessionCount);
            Assert.Equal(b.LongestSessionSec, a.LongestSessionSec);
            Assert.Equal(b.CpuTempAvg ?? -1, a.CpuTempAvg ?? -1, 6);
            Assert.Equal(b.MemMax, a.MemMax);
            Assert.Equal(b.GpuHotMax, a.GpuHotMax);
        }

        // Monthly bars: the minutes of each month added up.
        foreach (var m in fromTotals.Days)
        {
            var inMonth = raw.Temps.Where(x => x.Time.Year == m.Day.Year && x.Time.Month == m.Day.Month).Count();
            Assert.Equal(inMonth * 60.0, m.OnSec);
        }
    }

    // ── The period before, for the comparison lines ──

    public static TheoryData<string, int> PastPeriods => new()
    {
        { "Week", 14 }, { "Week", 21 }, { "Month", 45 }, { "Month", 100 }, { "Year", 370 },
    };

    [Theory]
    [MemberData(nameof(PastPeriods))]
    public void The_comparison_with_the_period_before_uses_that_whole_period(string range, int back)
    {
        using var _ = Make.Culture();
        using var db = Seeds.Open("typical");
        var kind = Enum.Parse<ReportRange>(range);
        var anchor = Seeds.TypicalNow.AddDays(-back);
        var report = ReportBuilder.Build(db, kind, anchor, Make.Settings());
        var before = ReportBuilder.Build(db, kind, ReportBuilder.Previous(kind, anchor), Make.Settings());
        double diff = report.ActiveSec - before.ActiveSec;
        var line = report.Insights.SingleOrDefault(i => i.Key == "screen-compare");
        if (Math.Abs(diff) < 15 * 60 || !before.HasData || before.ActiveSec == 0)
        {
            Assert.Null(line);
            return;
        }
        string than = kind switch { ReportRange.Week => "the week before", ReportRange.Year => "the year before", _ => "the month before" };
        Assert.Equal($"That's {Rigsight.Core.Units.Duration(Math.Abs(diff))} {(diff > 0 ? "more" : "less")} screen time than {than}.", line?.Text);
    }

    public static TheoryData<string, string, string, string, string> PreviousPeriods => new()
    {
        // range, anchor = now, expected previous from, expected previous to
        { "Day", "2025-03-12 15:30", "2025-03-12 15:30", "2025-03-11 00:00", "2025-03-11 15:30" },
        { "Day", "2025-03-01 00:10", "2025-03-01 00:10", "2025-02-28 00:00", "2025-02-28 00:10" },
        { "Week", "2025-03-12 15:30", "2025-03-12 15:30", "2025-03-03 00:00", "2025-03-05 15:30" },
        { "Month", "2025-03-12 15:30", "2025-03-12 15:30", "2025-02-01 00:00", "2025-02-12 15:30" },
        // A month longer than the one before: the comparison stops at the end of February, not in March.
        { "Month", "2025-03-31 12:00", "2025-03-31 12:00", "2025-02-01 00:00", "2025-03-01 00:00" },
        { "Month", "2025-03-29 12:00", "2025-03-29 12:00", "2025-02-01 00:00", "2025-03-01 00:00" },
        { "Month", "2024-03-30 12:00", "2024-03-30 12:00", "2024-02-01 00:00", "2024-03-01 00:00" },
        { "Month", "2025-05-31 23:59", "2025-05-31 23:59", "2025-04-01 00:00", "2025-05-01 00:00" },
        { "Year", "2025-03-12 15:30", "2025-03-12 15:30", "2024-01-01 00:00", "2024-03-12 00:00" }, // as many days (2024 had 29 February)
        // The last day of a leap year: 366 days against 365.
        { "Year", "2024-12-31 18:00", "2024-12-31 18:00", "2023-01-01 00:00", "2024-01-01 00:00" },
        // Periods that are over compare with the whole period before.
        { "Day", "2025-03-10 12:00", "2025-03-12 15:30", "2025-03-09 00:00", "2025-03-10 00:00" },
        { "Week", "2025-03-01 12:00", "2025-03-12 15:30", "2025-02-17 00:00", "2025-02-24 00:00" },
        { "Month", "2025-02-10 12:00", "2025-03-12 15:30", "2025-01-01 00:00", "2025-02-01 00:00" },
        { "Year", "2024-06-01 12:00", "2025-03-12 15:30", "2023-01-01 00:00", "2024-01-01 00:00" },
    };

    [Theory]
    [MemberData(nameof(PreviousPeriods))]
    public void The_period_compared_with_is_the_same_part_of_the_one_before(string range, string anchor, string now, string from, string to)
    {
        static DateTime D(string s) => DateTime.ParseExact(s, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var p = ReportBuilder.PreviousPeriod(Enum.Parse<ReportRange>(range), D(anchor), D(now));
        Assert.Equal((D(from), D(to)), p);
    }

    [Fact]
    public void All_time_has_nothing_to_compare_with()
    {
        Assert.Null(ReportBuilder.PreviousPeriod(ReportRange.All, DateTime.Now, DateTime.Now));
    }
}
