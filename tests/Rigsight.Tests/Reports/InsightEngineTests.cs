using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Reports;

/// <summary>
/// Every observation the insight engine can make, with its exact wording (these sentences are what users read), when
/// it's made and when it isn't. Runs in the UI collection because temperatures format through the process-wide
/// <see cref="Units.Fahrenheit"/>, which the app's settings (UI tests) switch.
/// </summary>
[Collection("UI")]
public sealed class InsightEngineTests : IDisposable
{
    private static readonly DateTime Wed = new(2025, 3, 12); // a Wednesday long past
    private static readonly DateTime At1504 = Wed.AddHours(15).AddMinutes(4);
    private readonly IDisposable _culture = Make.Culture();

    public InsightEngineTests() => Units.Fahrenheit = false;

    public void Dispose()
    {
        Units.Fahrenheit = false;
        _culture.Dispose();
    }

    private static Report Past(ReportRange range = ReportRange.Day, double active = 3 * 3600, double on = 4 * 3600)
    {
        var (from, to) = ReportBuilder.Bounds(range, Wed);
        if (range == ReportRange.All) to = Wed.AddDays(1);
        return new Report { Range = range, From = from, To = to, HasData = true, ActiveSec = active, OnSec = on };
    }

    private static Report Now(ReportRange range = ReportRange.Day, double active = 3 * 3600, double on = 4 * 3600)
    {
        var (from, to) = ReportBuilder.Bounds(range, DateTime.Now);
        return new Report { Range = range, From = from, To = to, HasData = true, ActiveSec = active, OnSec = on };
    }

    /// <summary>The 7 days before: <paramref name="days"/> of them with use, adding up to these totals.</summary>
    private static Report Usual(int days, double active, double gaming = 0)
    {
        var r = new Report { Range = ReportRange.Week, From = Wed.AddDays(-7), To = Wed, HasData = days > 0, ActiveSec = active, GamingSec = gaming };
        for (int i = 0; i < 7; i++) r.Days.Add(new DayBucket { Day = Wed.AddDays(i - 7), OnSec = i < days ? 3600 : 0 });
        return r;
    }

    private static AppStat App(string name, AppCategory category, double active, string? exe = null) =>
        new() { Id = name.GetHashCode(), Name = name, Exe = exe ?? name.ToLowerInvariant().Replace(" ", "") + ".exe", Category = category, ActiveSec = active };

    private static List<Insight> Gen(Report r, Report? previous = null, Report? usual = null, AlertSettings? alerts = null, InsightContext? context = null) =>
        InsightEngine.Generate(r, previous, usual, alerts ?? new AlertSettings(), context);

    private static string Line(List<Insight> list, string key) => Assert.Single(list, i => i.Key == key).Text;

    private static void None(List<Insight> list, string key) => Assert.DoesNotContain(list, i => i.Key == key);

    // ── Nothing to say ──

    [Fact]
    public void A_report_without_data_says_nothing_even_with_crashes_and_peaks()
    {
        var r = Past();
        r.HasData = false;
        r.Crashes.Add(new CrashEvent { Ts = TimeUtil.ToUnix(At1504), Kind = CrashKind.SystemCrash, Code = "0x124" });
        r.CpuOverLimitMin = 5;
        r.CpuTempPeak = new Peak(99, At1504, null);
        Assert.Empty(Gen(r, Past(), Usual(7, 7 * 3600)));
    }

    [Theory]
    [InlineData(ReportRange.Day, "A quiet day: only 10m of use, so there's not much to point out.")]
    [InlineData(ReportRange.Week, "A quiet week: only 10m of use, so there's not much to point out.")]
    [InlineData(ReportRange.Month, "A quiet month: only 10m of use, so there's not much to point out.")]
    [InlineData(ReportRange.Year, "A quiet year: only 10m of use, so there's not much to point out.")]
    [InlineData(ReportRange.All, "A quiet stretch: only 10m of use, so there's not much to point out.")]
    public void A_period_with_little_use_is_called_quiet_and_nothing_else_is_said(ReportRange range, string text)
    {
        var r = Past(range, active: 600, on: 900);
        r.Apps.Add(App("Chrome", AppCategory.Browser, 600));
        r.CpuTempPeak = new Peak(60, At1504, null);
        var list = Gen(r, Past(range, 7200), Usual(7, 7 * 7200));
        var only = Assert.Single(list);
        Assert.Equal(text, only.Text);
        Assert.Equal(("early", InsightTone.Neutral, 60), (only.Key, only.Tone, only.Priority));
    }

    [Fact]
    public void A_period_in_progress_with_little_use_is_too_early_to_say()
    {
        Assert.Equal("Too early for highlights: 14m of use so far.", Assert.Single(Gen(Now(active: 14 * 60 + 59))).Text);
        Assert.Equal("Too early for highlights: 45s of use so far.", Assert.Single(Gen(Now(ReportRange.Month, active: 45))).Text);
    }

    [Fact]
    public void Fifteen_minutes_of_use_is_enough_to_say_more()
    {
        var list = Gen(Past(active: 15 * 60, on: 20 * 60));
        None(list, "early");
        Assert.Equal("You actively used your PC for 15m (on for 20m).", Line(list, "screen"));
    }

    // ── Warnings ──

    [Fact]
    public void The_latest_crash_is_explained_with_its_time()
    {
        var r = Past();
        r.Apps.Add(App("Dota 2", AppCategory.Game, 3600, "dota2.exe"));
        r.Crashes.Add(new CrashEvent { Ts = TimeUtil.ToUnix(At1504), Kind = CrashKind.AppHang, AppExe = "DOTA2.exe" });
        var crash = Assert.Single(Gen(r), i => i.Key == "crash");
        Assert.Equal("Dota 2 stopped responding at 3:04 PM.", crash.Text);
        Assert.Equal((InsightTone.Warn, 95), (crash.Tone, crash.Priority));
    }

    [Fact]
    public void Several_crashes_point_to_the_crashes_page_and_a_long_period_gives_the_date()
    {
        var r = Past(ReportRange.Week);
        r.Crashes.Add(new CrashEvent { Ts = TimeUtil.ToUnix(At1504), Kind = CrashKind.SystemCrash, Code = "0x00000124" });
        r.Crashes.Add(new CrashEvent { Ts = TimeUtil.ToUnix(At1504.AddDays(-1)), Kind = CrashKind.GpuDriverReset });
        r.Crashes.Add(new CrashEvent { Ts = TimeUtil.ToUnix(At1504.AddDays(-2)), Kind = CrashKind.UnexpectedShutdown });
        Assert.Equal("Windows crashed (blue screen) at Wed 12 Mar, 3:04 PM: WHEA_UNCORRECTABLE_ERROR. (3 crashes in total — see the Crashes page)",
            Line(Gen(r), "crash"));
    }

    [Theory]
    [InlineData(CrashKind.GpuDriverReset, "", null, null, "Graphics driver reset at 3:04 PM.")]
    [InlineData(CrashKind.UnexpectedShutdown, "", null, null, "PC shut off unexpectedly at 3:04 PM: a power cut or a hard freeze.")]
    [InlineData(CrashKind.AppHang, "unknown.exe", null, null, "unknown stopped responding at 3:04 PM.")]
    [InlineData(CrashKind.AppCrash, "game.exe", "nvwgf2umx.dll", "c0000005", "game crashed at 3:04 PM: NVIDIA driver.")]
    [InlineData(CrashKind.AppCrash, "game.exe", "game.exe", "c0000005", "game crashed at 3:04 PM, in its own code.")]
    [InlineData(CrashKind.AppCrash, "game.exe", "SomeMod.dll", null, "game crashed at 3:04 PM: SomeMod.dll.")]
    [InlineData(CrashKind.AppCrash, "game.exe", "", null, "game crashed at 3:04 PM.")]
    [InlineData(CrashKind.AppCrash, "game.exe", "unknown", null, "game crashed at 3:04 PM.")] // what Windows writes when it couldn't tell
    [InlineData(CrashKind.SystemCrash, "", null, "0x00000124", "Windows crashed (blue screen) at 3:04 PM: WHEA_UNCORRECTABLE_ERROR.")]
    [InlineData(CrashKind.SystemCrash, "", null, "0x0000ABCD", "Windows crashed (blue screen) at 3:04 PM: 0x0000ABCD.")]
    public void Each_kind_of_crash_reads_naturally_and_names_a_cause_only_where_it_adds_one(CrashKind kind, string exe, string? module, string? code, string text)
    {
        var r = Past();
        r.Crashes.Add(new CrashEvent { Ts = TimeUtil.ToUnix(At1504), Kind = kind, AppExe = exe, Module = module, Code = code });
        Assert.Equal(text, Line(Gen(r), "crash"));
    }

    [Fact]
    public void No_crash_sentence_repeats_its_title_or_lowercases_a_name()
    {
        // Every kind the explainer knows, with and without a module and a code: the sentence is the title, the time, and
        // a cause only when it isn't already in the title, written as the explainer wrote it.
        foreach (var kind in Enum.GetValues<CrashKind>())
            foreach (var module in new[] { null, "", "nvlddmkm.sys", "d3d11.dll", "gameoverlayrenderer64.dll", "unityplayer.dll", "coreclr.dll", "game.exe", "ntdll.dll", "weird.dll" })
                foreach (var code in new[] { null, "c0000005", "0x00000124", "0x0000ABCD" })
                {
                    var e = new CrashEvent { Ts = TimeUtil.ToUnix(At1504), Kind = kind, AppExe = "game.exe", Module = module, Code = code };
                    var ex = CrashExplainer.Explain(e, "Game");
                    var r = Past();
                    r.Apps.Add(App("Game", AppCategory.Game, 3600, "game.exe"));
                    r.Crashes.Add(e);
                    string line = Line(Gen(r), "crash");
                    if (ex.Cause is null) Assert.Equal($"{ex.Title} at 3:04 PM.", line);
                    else
                    {
                        // "…: NVIDIA driver." for a name; "…, in its own code." for where it happened.
                        Assert.Equal(ex.Cause.StartsWith("in ", StringComparison.Ordinal) ? $"{ex.Title} at 3:04 PM, {ex.Cause}." : $"{ex.Title} at 3:04 PM: {ex.Cause}.", line);
                        Assert.DoesNotContain(ex.Cause, ex.Title, StringComparison.OrdinalIgnoreCase);
                    }
                }
    }

    [Theory]
    [InlineData(1, "1 minute")]
    [InlineData(5, "5 minutes")]
    [InlineData(59, "59 minutes")]
    [InlineData(60, "1h 00m")]
    [InlineData(90, "1h 30m")]
    public void Time_over_the_alert_limit_is_a_hot_warning(int minutes, string during)
    {
        var r = Past();
        r.CpuOverLimitMin = minutes;
        r.GpuOverLimitMin = minutes;
        var list = Gen(r, alerts: new AlertSettings { CpuLimit = 80, GpuLimit = 83 });
        var over = list.Where(i => i.Key == "over-limit").ToList();
        Assert.Equal([$"Your CPU reached your 80° alert limit for {during}.", $"Your GPU reached your 83° alert limit for {during}."], over.Select(i => i.Text));
        Assert.All(over, i => Assert.Equal((InsightTone.Hot, 90), (i.Tone, i.Priority)));
    }

    [Theory]
    [InlineData(84.9, null)]
    [InlineData(85, InsightTone.Warn)]
    [InlineData(91.9, InsightTone.Warn)]
    [InlineData(92, InsightTone.Hot)]
    public void A_cpu_that_held_the_heat_is_mentioned_with_the_app_doing_the_work(double held, InsightTone? tone)
    {
        var r = Past();
        r.Apps.Add(App("Dota 2", AppCategory.Game, 3600));
        r.CpuTempPeak = new Peak(held + 3, At1504, "Dota 2");
        r.CpuTempHeld = held;
        var list = Gen(r, alerts: new AlertSettings { CpuLimit = 99 });
        if (tone is null) { None(list, "peak"); return; }
        var peak = Assert.Single(list, i => i.Key == "peak");
        Assert.Equal($"CPU peaked at {held + 3:0}° at 3:04 PM, with Dota 2 working it hardest.", peak.Text);
        Assert.Equal((tone.Value, 85), (peak.Tone, peak.Priority));
    }

    [Fact]
    public void A_spike_is_not_a_peak_worth_a_line()
    {
        // X3D and other chips jump to the 80s for a moment on a light load (an app opening): the minute's average stays low.
        var r = Past();
        r.CpuTempPeak = new Peak(89, At1504, "Chrome");
        r.CpuTempHeld = 61;
        r.GpuTempPeak = new Peak(86, At1504, null);
        r.GpuTempHeld = 70;
        None(Gen(r, alerts: new AlertSettings { CpuLimit = 99, GpuLimit = 99 }), "peak");
    }

    [Theory]
    [InlineData(82.9, null)]
    [InlineData(83, InsightTone.Warn)]
    [InlineData(88, InsightTone.Hot)]
    public void A_gpu_that_held_the_heat_is_mentioned(double held, InsightTone? tone)
    {
        var r = Past(ReportRange.Month);
        r.Apps.Add(App("Chrome", AppCategory.Browser, 3600));
        r.GpuTempPeak = new Peak(held + 1, At1504, "Chrome");
        r.GpuTempHeld = held;
        var list = Gen(r, alerts: new AlertSettings { GpuLimit = 99 });
        if (tone is null) { None(list, "peak"); return; }
        Assert.Equal($"GPU peaked at {held + 1:0}° at Wed 12 Mar, 3:04 PM, with Chrome working it hardest.", Line(list, "peak"));
    }

    [Fact]
    public void A_peak_without_an_app_just_says_when()
    {
        var r = Past();
        r.CpuTempPeak = new Peak(90, At1504, null);
        r.CpuTempHeld = 88;
        r.GpuTempPeak = new Peak(86, At1504, "Some Tool"); // not in the apps list: working in the background
        r.GpuTempHeld = 85;
        var peaks = Gen(r, alerts: new AlertSettings { CpuLimit = 99, GpuLimit = 99 }).Where(i => i.Key == "peak").Select(i => i.Text);
        Assert.Equal(["CPU peaked at 90° at 3:04 PM.", "GPU peaked at 86° at 3:04 PM, with Some Tool working it hardest."], peaks);
    }

    [Fact]
    public void A_peak_is_not_repeated_when_the_limit_warning_already_covers_it()
    {
        var r = Past();
        r.CpuTempPeak = new Peak(95, At1504, null);
        r.CpuTempHeld = 93;
        r.GpuTempPeak = new Peak(90, At1504, null);
        r.GpuTempHeld = 89;
        r.CpuOverLimitMin = 3;
        var peaks = Gen(r, alerts: new AlertSettings { CpuLimit = 85, GpuLimit = 99 }).Where(i => i.Key == "peak").ToList();
        Assert.Equal("GPU peaked at 90° at 3:04 PM.", Assert.Single(peaks).Text);
    }

    [Fact]
    public void A_record_peak_is_said_in_the_peak_line_not_twice()
    {
        var r = Past();
        r.GpuTempPeak = new Peak(89, At1504, "Cyberpunk 2077");
        r.GpuTempHeld = 88;
        r.Records.Add(new RecordNote(RecordKind.HottestGpu, "89°", null, 90));
        var list = Gen(r, alerts: new AlertSettings { GpuLimit = 99 });
        Assert.Equal("GPU peaked at 89° at 3:04 PM, the hottest in three months, with Cyberpunk 2077 working it hardest.", Line(list, "peak"));
        None(list, "record");
    }

    [Fact]
    public void A_wide_hot_spot_gap_says_what_it_can_mean()
    {
        var r = Past();
        r.HotSpotGap = new LoadTemps(null, 31.4, 20);
        var gap = Assert.Single(Gen(r), i => i.Key == "hotspot");
        Assert.Equal("In games, your GPU hot spot runs 31° above the core. A gap past 30° can mean the thermal paste or cooler contact has worn.", gap.Text);
        Assert.Equal((InsightTone.Warn, 70), (gap.Tone, gap.Priority));
    }

    [Fact]
    public void The_hot_spot_gap_reads_right_in_fahrenheit()
    {
        // Both numbers are differences: 31.4° C apart is 57° F apart, and the 30° line is 54° F.
        Units.Fahrenheit = true;
        var r = Past();
        r.HotSpotGap = new LoadTemps(null, 31.4, 20);
        Assert.Equal("In games, your GPU hot spot runs 57° above the core. A gap past 54° can mean the thermal paste or cooler contact has worn.", Line(Gen(r), "hotspot"));
    }

    [Theory]
    [InlineData(29.9, 30)] // many cards run a 25–30° gap by design
    [InlineData(35, 19)]
    public void A_normal_or_brief_hot_spot_gap_is_not_mentioned(double gap, int minutes)
    {
        var r = Past();
        r.HotSpotGap = new LoadTemps(null, gap, minutes);
        None(Gen(r), "hotspot");
    }

    // ── Screen time ──

    [Fact]
    public void Screen_time_is_the_top_line()
    {
        var list = Gen(Past(active: 3 * 3600 + 5 * 60, on: 4 * 3600 + 59));
        Assert.Equal("You actively used your PC for 3h 05m (on for 4h 00m).", list[0].Text);
        Assert.Equal(("screen", 99), (list[0].Key, list[0].Priority));
    }

    [Theory]
    [InlineData(3 * 3600, 2 * 3600, "That's 1h 00m more than your daily average of 2h 00m.")]
    [InlineData(1 * 3600, 2 * 3600, "That's 1h 00m less than your daily average of 2h 00m.")]
    [InlineData(2 * 3600 + 15 * 60, 2 * 3600, "That's 15m more than your daily average of 2h 00m.")]
    public void A_past_day_is_compared_with_the_daily_average(double active, double avg, string text)
    {
        Assert.Equal(text, Line(Gen(Past(active: active, on: active + 60), Past(active: 100), Usual(4, avg * 4)), "screen-compare"));
    }

    [Fact]
    public void A_day_close_to_the_average_falls_back_to_the_day_before()
    {
        var list = Gen(Past(active: 2 * 3600 + 10 * 60), Past(active: 1 * 3600), Usual(4, 4 * 2 * 3600));
        Assert.Equal("That's 1h 10m more screen time than the day before.", Line(list, "screen-compare"));
    }

    [Fact]
    public void Without_three_days_of_history_the_average_is_not_used()
    {
        var list = Gen(Past(active: 3 * 3600), null, Usual(2, 2 * 3600));
        None(list, "screen-compare");
    }

    [Fact]
    public void A_day_in_progress_is_only_compared_once_past_the_average()
    {
        Assert.Equal("You're already 1h 00m past your daily average of 2h 00m.", Line(Gen(Now(active: 3 * 3600), null, Usual(4, 8 * 3600)), "screen-compare"));
        None(Gen(Now(active: 1 * 3600), null, Usual(4, 8 * 3600)), "screen-compare");
    }

    [Theory]
    [InlineData(ReportRange.Day, "the day before")]
    [InlineData(ReportRange.Week, "the week before")]
    public void A_finished_period_is_compared_with_the_one_before(ReportRange range, string than)
    {
        Assert.Equal($"That's 2h 30m more screen time than {than}.", Line(Gen(Past(range, active: 5 * 3600), Past(range, active: 2.5 * 3600)), "screen-compare"));
        Assert.Equal($"That's 2h 30m less screen time than {than}.", Line(Gen(Past(range, active: 1 * 3600), Past(range, active: 3.5 * 3600)), "screen-compare"));
    }

    [Fact]
    public void A_month_or_year_is_compared_with_the_one_before_a_day_at_a_time()
    {
        // March (31 days) with 62h against February (28 days) with 28h: 2h a day against 1h.
        Report Month(int month, int days, double hours) =>
            new() { Range = ReportRange.Month, From = new(2025, month, 1), To = new DateTime(2025, month, 1).AddDays(days), HasData = true, ActiveSec = hours * 3600, OnSec = hours * 3600 };
        Assert.Equal("That's 1h 00m a day more than the month before.", Line(Gen(Month(3, 31, 62), Month(2, 28, 28)), "screen-compare"));
        // More in total, but only for being longer: 31 × 2h against 28 × 2h 12m is less a day.
        Assert.Equal("That's 12m a day less than the month before.", Line(Gen(Month(3, 31, 62), Month(2, 28, 28 * 2.2)), "screen-compare"));
        None(Gen(Month(3, 31, 62), Month(2, 28, 28 * 2.1)), "screen-compare"); // six minutes a day apart: not worth a line

        var year = new Report { Range = ReportRange.Year, From = new(2025, 1, 1), To = new(2026, 1, 1), HasData = true, ActiveSec = 730 * 3600, OnSec = 730 * 3600 };
        var before = new Report { Range = ReportRange.Year, From = new(2024, 1, 1), To = new(2025, 1, 1), HasData = true, ActiveSec = 366 * 3600, OnSec = 366 * 3600 };
        Assert.Equal("That's 1h 00m a day more than the year before.", Line(Gen(year, before), "screen-compare"));
    }

    [Theory]
    [InlineData(ReportRange.Day, "yesterday by this time")]
    [InlineData(ReportRange.Week, "last week by this point")]
    [InlineData(ReportRange.Month, "last month by this point")]
    [InlineData(ReportRange.Year, "last year by this point")]
    public void A_period_in_progress_is_compared_with_the_same_point_of_the_one_before(ReportRange range, string than)
    {
        Assert.Equal($"That's 1h 00m less screen time than {than}.", Line(Gen(Now(range, active: 2 * 3600), Now(range, active: 3 * 3600)), "screen-compare"));
    }

    [Fact]
    public void A_small_difference_or_an_empty_period_before_is_not_compared()
    {
        None(Gen(Past(ReportRange.Week, active: 3 * 3600), Past(ReportRange.Week, active: 3 * 3600 - 14 * 60)), "screen-compare");
        var empty = Past(ReportRange.Week, active: 0);
        empty.HasData = false;
        None(Gen(Past(ReportRange.Week), empty), "screen-compare");
        None(Gen(Past(ReportRange.Week), Past(ReportRange.Week, active: 0)), "screen-compare");
    }

    // ── The day's shape ──

    [Fact]
    public void A_finished_day_says_when_it_started_and_ended()
    {
        var r = Past();
        r.DayStart = Wed.AddHours(8).AddMinutes(5);
        r.LastActive = Wed.AddHours(23).AddMinutes(10);
        Assert.Equal("Your day ran from 8:05 AM to 11:10 PM.", Line(Gen(r), "span"));
    }

    [Fact]
    public void A_late_night_before_is_mentioned_first()
    {
        var r = Past();
        r.LateUntil = Wed.AddHours(1).AddMinutes(30);
        r.DayStart = Wed.AddHours(9);
        r.LastActive = Wed.AddHours(18);
        Assert.Equal("The night before ran late: you were on until 1:30 AM. Your day ran from 9:00 AM to 6:00 PM.", Line(Gen(r), "span"));
        r.DayStart = null;
        Assert.Equal("The night before ran late: you were on until 1:30 AM.", Line(Gen(r), "span"));
    }

    [Fact]
    public void A_day_that_ran_on_past_midnight_says_so_not_twelve()
    {
        var r = Past();
        r.DayStart = Wed.AddHours(8);
        r.LastActive = Wed.AddDays(1); // its last minute ended at midnight: the day went on into the next
        Assert.Equal("Your day ran from 8:00 AM until past midnight.", Line(Gen(r), "span"));
        r.LongestStretch = new Stretch(Wed.AddHours(21), Wed.AddDays(1), "Dota 2");
        Assert.Equal("Your longest stretch without a break was 3h 00m (9:00 PM – past midnight), mostly Dota 2.", Line(Gen(r), "stretch"));
    }

    [Fact]
    public void A_day_in_progress_only_says_when_it_started()
    {
        var r = Now();
        r.DayStart = DateTime.Today.AddHours(7).AddMinutes(45);
        r.LastActive = DateTime.Now;
        Assert.Equal("Your day started at 7:45 AM.", Line(Gen(r), "span"));
    }

    [Fact]
    public void Longer_periods_have_no_day_span()
    {
        var r = Past(ReportRange.Week);
        r.DayStart = Wed.AddHours(9);
        None(Gen(r), "span");
        None(Gen(Past()), "span");
    }

    // ── Apps ──

    [Fact]
    public void The_most_used_app_is_named_with_its_share()
    {
        var r = Past(active: 3 * 3600);
        r.Apps.Add(App("Chrome", AppCategory.Browser, 2 * 3600));
        r.Apps.Add(App("Code", AppCategory.Development, 3600));
        var top = Assert.Single(Gen(r), i => i.Key == "top-app");
        Assert.Equal("Chrome took most of your time: 2h 00m (67% of active time).", top.Text);
        Assert.Equal(50, top.Priority);
    }

    [Fact]
    public void Windows_parts_are_skipped_and_a_short_top_app_is_not_named()
    {
        var r = Past(active: 3 * 3600);
        r.Apps.Add(App("File Explorer", AppCategory.System, 2 * 3600));
        r.Apps.Add(App("Code", AppCategory.Development, 30 * 60));
        Assert.Equal("Code took most of your time: 30m (17% of active time).", Line(Gen(r), "top-app"));
        r.Apps[1].ActiveSec = 19 * 60;
        None(Gen(r), "top-app");
    }

    [Fact]
    public void Gaming_names_the_game_and_the_longest_session()
    {
        var r = Past();
        r.Apps.Add(App("Dota 2", AppCategory.Game, 3600));
        r.Apps.Add(App("Tetris", AppCategory.Game, 59)); // under a minute: not counted
        r.Sessions.Add(new SessionInfo { Name = "Dota 2", Start = At1504, End = At1504.AddMinutes(50), ActiveSec = 45 * 60, IsGame = true });
        r.Sessions.Add(new SessionInfo { Name = "Chrome", Start = At1504, End = At1504.AddHours(5), ActiveSec = 4 * 3600, IsGame = false });
        var gaming = Assert.Single(Gen(r), i => i.Key == "gaming");
        Assert.Equal("You gamed for 1h 00m (Dota 2). Longest session: Dota 2, 45m starting 3:04 PM.", gaming.Text);
        Assert.Equal(64, gaming.Priority);
    }

    [Fact]
    public void Several_games_are_counted_and_a_short_session_is_not_named()
    {
        var r = Past(ReportRange.Month);
        r.Apps.Add(App("Dota 2", AppCategory.Game, 3600));
        r.Apps.Add(App("Tetris", AppCategory.Game, 600));
        r.Sessions.Add(new SessionInfo { Name = "Dota 2", Start = At1504, End = At1504.AddMinutes(19), ActiveSec = 19 * 60, IsGame = true });
        Assert.Equal("You gamed for 1h 10m (2 games).", Line(Gen(r), "gaming"));
        r.Sessions.Add(new SessionInfo { Name = "Tetris", Start = At1504, End = At1504.AddMinutes(20), ActiveSec = 20 * 60, IsGame = true });
        Assert.Equal("You gamed for 1h 10m (2 games). Longest session: Tetris, 20m starting Wed 12 Mar, 3:04 PM.", Line(Gen(r), "gaming"));
    }

    [Fact]
    public void A_session_from_the_night_before_counts_only_its_part_in_the_day()
    {
        // 11 PM to 1:30 AM: an hour and a half of it today. Never "3h starting 11:00 PM" under a day with less gaming.
        var r = Past();
        r.Apps.Add(App("Dota 2", AppCategory.Game, 3 * 3600));
        r.Sessions.Add(new SessionInfo { Name = "Dota 2", Start = Wed.AddHours(-1), End = Wed.AddHours(1.5), ActiveSec = 2.5 * 3600, IsGame = true });
        Assert.Equal("You gamed for 3h 00m (Dota 2). Longest session: Dota 2, 1h 30m, carried on from the night before.", Line(Gen(r), "gaming"));
    }

    [Fact]
    public void A_session_that_was_all_the_gaming_or_is_still_going_isnt_named()
    {
        var r = Past();
        r.Apps.Add(App("REMATCH", AppCategory.Game, 34 * 60));
        r.Sessions.Add(new SessionInfo { Name = "REMATCH", Start = Wed.AddHours(-1), End = Wed.AddMinutes(35), ActiveSec = 70 * 60, IsGame = true });
        Assert.Equal("You gamed for 34m (REMATCH).", Line(Gen(r), "gaming")); // not "Longest session: REMATCH, 35m"
        r.Apps.Add(App("Dota 2", AppCategory.Game, 5 * 60)); // two games: which one the time went to is worth saying
        Assert.Equal("You gamed for 39m (2 games). Longest session: REMATCH, 35m, carried on from the night before.", Line(Gen(r), "gaming"));

        // Still playing: the session that matters isn't over (nor written) yet, so an earlier short one isn't "the longest".
        var today = Now();
        today.Apps.Add(App("Elden Ring", AppCategory.Game, 3 * 3600));
        today.Sessions.Add(new SessionInfo { Name = "Elden Ring", Start = DateTime.Today.AddMinutes(1), End = DateTime.Today.AddMinutes(26), ActiveSec = 25 * 60, IsGame = true });
        today.GameOngoing = "Elden Ring";
        Assert.Equal("You gamed for 3h 00m (Elden Ring).", Line(Gen(today), "gaming"));
    }

    [Fact]
    public void A_few_hours_picked_out_arent_compared_with_a_whole_days_gaming()
    {
        var r = new Report { Range = ReportRange.Custom, From = Wed.AddHours(18), To = Wed.AddHours(21), HasData = true, ActiveSec = 3 * 3600, OnSec = 3 * 3600 };
        r.Apps.Add(App("Dota 2", AppCategory.Game, 2 * 3600));
        Assert.Equal("You gamed for 2h 00m (Dota 2).", Line(Gen(r, usual: Usual(4, 4 * 3600, 4 * 3600)), "gaming"));
    }

    [Theory]
    [InlineData(3600, 1800, " That's 30m more than your daily average (30m).")]
    [InlineData(1800, 3600, " That's 30m less than your daily average (1h 00m).")]
    [InlineData(3600, 3000, "")] // 10 minutes apart: not worth saying
    public void A_days_gaming_is_compared_with_the_daily_average(double gamed, double average, string compare)
    {
        var r = Past();
        r.Apps.Add(App("Dota 2", AppCategory.Game, gamed));
        Assert.Equal($"You gamed for {Units.Duration(gamed)} (Dota 2).{compare}", Line(Gen(r, usual: Usual(4, 4 * 3600, 4 * average)), "gaming"));
    }

    [Fact]
    public void Gaming_today_is_only_compared_once_past_the_average()
    {
        var r = Now();
        r.Apps.Add(App("Dota 2", AppCategory.Game, 1800));
        Assert.Equal("You gamed for 30m (Dota 2).", Line(Gen(r, usual: Usual(4, 4 * 3600, 4 * 3600)), "gaming"));
        r.Apps[0].ActiveSec = 2 * 3600;
        Assert.Equal("You gamed for 2h 00m (Dota 2). That's 1h 00m more than your daily average (1h 00m).", Line(Gen(r, usual: Usual(4, 4 * 3600, 4 * 3600)), "gaming"));
    }

    [Fact]
    public void The_longest_stretch_without_a_break_is_mentioned_from_ninety_minutes()
    {
        var r = Past();
        r.LongestStretch = new Stretch(Wed.AddHours(13), Wed.AddHours(15), "Dota 2");
        Assert.Equal("Your longest stretch without a break was 2h 00m (1:00 PM – 3:00 PM), mostly Dota 2.", Line(Gen(r), "stretch"));
        r.LongestStretch = new Stretch(Wed.AddHours(13), Wed.AddHours(14).AddMinutes(30), null);
        Assert.Equal("Your longest stretch without a break was 1h 30m (1:00 PM – 2:30 PM).", Line(Gen(r), "stretch"));
        r.LongestStretch = new Stretch(Wed.AddHours(13), Wed.AddHours(14).AddMinutes(29), "Dota 2");
        None(Gen(r), "stretch");
    }

    [Fact]
    public void A_long_periods_stretch_gives_its_date()
    {
        var r = Past(ReportRange.Week);
        r.LongestStretch = new Stretch(Wed.AddHours(13), Wed.AddHours(16), "Dota 2");
        Assert.Equal("Your longest stretch without a break was 3h 00m (Wed 12 Mar, 1:00 PM), mostly Dota 2.", Line(Gen(r), "stretch"));
    }

    // ── Who made the heat ──

    [Fact]
    public void Hot_minutes_are_put_down_to_the_app_that_was_working_the_part()
    {
        var r = Past();
        r.GpuHotMinutes = 48;
        r.GpuHotByApp = [new HotShare("Rematch", 46), new HotShare("Chrome", 2)];
        r.CpuHotMinutes = 12;
        r.CpuHotByApp = [new HotShare("Blender", 12)];
        var lines = Gen(r).Where(i => i.Key == "hot-app").ToList();
        Assert.Equal(["Your GPU spent 48 minutes over 75°, nearly all of it on Rematch.", "Your CPU spent 12 minutes over 75°, nearly all of it on Blender."],
            lines.Select(i => i.Text));
        Assert.All(lines, i => Assert.Equal(InsightTone.Neutral, i.Tone));
        Assert.Equal("Rematch 46 min · Chrome 2 min", lines[0].Detail);

        r.GpuHotMinutes = 90; // an evening's gaming: where the heat went, not a warning (a GPU runs over 75° in most games)
        r.GpuHotByApp = [new HotShare("Rematch", 50), new HotShare("Dota 2", 30)];
        var gpu = Gen(r).First(i => i.Key == "hot-app");
        Assert.Equal("Your GPU spent 1h 30m over 75°, mostly on Rematch.", gpu.Text);
        Assert.Equal(InsightTone.Neutral, gpu.Tone);

        r.GpuHotByApp = [new HotShare("Rematch", 30), new HotShare("Dota 2", 30), new HotShare("Blender", 30)];
        Assert.Equal("Your GPU spent 1h 30m over 75°, spread across Rematch, Dota 2 and Blender.", Gen(r).First(i => i.Key == "hot-app").Text);
    }

    [Fact]
    public void Hot_minutes_nobody_clearly_made_or_too_few_of_them_go_unsaid()
    {
        var r = Past();
        r.GpuHotMinutes = 40;
        r.GpuHotByApp = [new HotShare("Chrome", 10)]; // most of the hot minutes are nobody's
        None(Gen(r), "hot-app");
        r.GpuHotMinutes = 9;
        r.GpuHotByApp = [new HotShare("Rematch", 9)];
        None(Gen(r), "hot-app");
    }

    [Fact]
    public void Heavy_work_behind_the_app_in_front_is_named_with_when_and_where_you_were()
    {
        var r = Past();
        r.BackgroundWork = new BackgroundWork("Windows Update", "Chrome", AppCategory.Browser, At1504, 12, Gpu: false);
        var line = Assert.Single(Gen(r), i => i.Key == "background-work");
        Assert.Equal("Windows Update ran your CPU hard for 12 minutes from 3:04 PM while you were in Chrome.", line.Text);
        Assert.Equal((InsightTone.Neutral, "3:04 PM – 3:16 PM"), (line.Tone, line.Detail));

        r.BackgroundWork = new BackgroundWork("Steam shader pre-caching", "Discord", AppCategory.Communication, At1504, 35, Gpu: true);
        line = Assert.Single(Gen(r), i => i.Key == "background-work");
        Assert.Equal("Steam shader pre-caching ran your GPU hard for 35 minutes from 3:04 PM while you were in Discord.", line.Text);
        Assert.Equal(InsightTone.Neutral, line.Tone); // a render or a download started on purpose isn't a worry
    }

    private static SteadyLoad Steady(AppStat app, int minutes, double gpu, double? cpu = null, double? power = 300, int days = 5) =>
        new(app.Id, app.Name, minutes, days, gpu, cpu, power);

    [Fact]
    public void The_game_heaviest_on_the_gpu_is_named_from_steady_play_against_the_others()
    {
        var r = Past();
        var a = App("Rematch", AppCategory.Game, 3600);
        var b = App("Dota 2", AppCategory.Game, 3600);
        var c = App("Hades", AppCategory.Game, 3600);
        r.Apps.AddRange([a, b, c]);
        r.Steady = [Steady(a, 60, 74), Steady(b, 90, 66), Steady(c, 30, 60)];
        var line = Assert.Single(Gen(r), i => i.Key == "game-heat");
        // The others weighted by their minutes: (66×90 + 60×30) / 120 = 64.5.
        Assert.Equal("Rematch works your GPU hardest: 74° in steady play, against 65° in your other games.", line.Text);
        Assert.Equal((InsightTone.Neutral, "Rematch 74°, Dota 2 66° and Hades 60°"), (line.Tone, line.Detail));

        r.Steady = [Steady(a, 60, 69), Steady(b, 90, 66)]; // 3° is no difference worth a line
        None(Gen(r), "game-heat");
        r.Steady = [Steady(a, 60, 74), Steady(b, 29, 60)]; // under half an hour of steady play: a warm-up and a menu
        None(Gen(r), "game-heat");
        var blender = App("Blender", AppCategory.Productivity, 3600);
        r.Apps.Add(blender);
        r.Steady = [Steady(a, 60, 74), Steady(blender, 90, 60)]; // a render isn't one of "your other games"
        None(Gen(r), "game-heat");
    }

    // ── Cooling, over months ──

    private static AppStat Cyberpunk => App("Cyberpunk 2077", AppCategory.Game, 3600);

    /// <summary>June: Cyberpunk at 70° GPU and 65° CPU in steady play, 40° GPU and 42° CPU at rest.</summary>
    private static ThenHeat Then(int minutes = 300, int days = 8, double? power = 300) =>
        new([Steady(Cyberpunk, minutes, 70, 65, power, days)], new LoadTemps(42, 40, 400), Wed.AddDays(-120), Wed.AddDays(-30));

    private static Report Playing(double gpu, double cpu, double restGpu, double restCpu, int minutes = 90, double? power = 300, ReportRange range = ReportRange.Week)
    {
        var r = Past(range);
        r.Apps.Add(Cyberpunk);
        r.Steady = [Steady(Cyberpunk, minutes, gpu, cpu, power)];
        r.RestTemps = new LoadTemps(restCpu, restGpu, 120);
        return r;
    }

    [Fact]
    public void The_same_game_running_hotter_than_months_ago_beyond_the_room_names_dust_and_paste()
    {
        var list = Gen(Playing(gpu: 77, cpu: 66, restGpu: 41, restCpu: 42), context: new InsightContext(Then: Then()));
        var line = Assert.Single(list, i => i.Key == "drift");
        Assert.Equal("In Cyberpunk 2077, your GPU ran 7° hotter than in December 2024 (77° vs 70°), and not because of the room. Dust or old thermal paste are the usual causes.", line.Text);
        Assert.Equal((InsightTone.Warn, 72), (line.Tone, line.Priority));
        Assert.Equal("90 minutes of steady play now, 300 in December 2024; at rest 41° now, 40° then", line.Detail);
    }

    [Fact]
    public void A_warmer_room_is_not_drift()
    {
        // Summer: the room 6° warmer lifts everything by as much. Rest and play both up 6°: the PC is as it was.
        None(Gen(Playing(gpu: 76, cpu: 71, restGpu: 46, restCpu: 48), context: new InsightContext(Then: Then())), "drift");
        // 8° hotter in play but 6° of it is the room: 2° left is within the noise.
        None(Gen(Playing(gpu: 78, cpu: 65, restGpu: 46, restCpu: 42), context: new InsightContext(Then: Then())), "drift");
        // 9° hotter, 5° of it the room: 4° is more than the room explains, and is said.
        Assert.Contains(Gen(Playing(gpu: 79, cpu: 65, restGpu: 45, restCpu: 42), context: new InsightContext(Then: Then())), i => i.Key == "drift");
    }

    [Fact]
    public void Running_cooler_than_months_ago_is_good_news()
    {
        var line = Assert.Single(Gen(Playing(gpu: 64, cpu: 64, restGpu: 40, restCpu: 42), context: new InsightContext(Then: Then())), i => i.Key == "drift");
        Assert.Equal("In Cyberpunk 2077, your GPU ran 6° cooler than in December 2024 (64° vs 70°).", line.Text);
        Assert.Equal((InsightTone.Good, 40), (line.Tone, line.Priority));
    }

    [Fact]
    public void Drift_needs_the_same_game_at_the_same_power_with_enough_play_on_both_sides()
    {
        var hot = Playing(gpu: 78, cpu: 65, restGpu: 40, restCpu: 42);
        Assert.Contains(Gen(hot, context: new InsightContext(Then: Then())), i => i.Key == "drift");
        None(Gen(Playing(gpu: 78, cpu: 65, restGpu: 40, restCpu: 42, minutes: 59), context: new InsightContext(Then: Then())), "drift");
        None(Gen(hot, context: new InsightContext(Then: Then(minutes: 119))), "drift");
        None(Gen(hot, context: new InsightContext(Then: Then(days: 2))), "drift"); // one or two evenings then aren't a baseline
        // 20% more power: new settings, a new card or a raised limit; not the same load.
        None(Gen(Playing(gpu: 78, cpu: 65, restGpu: 40, restCpu: 42, power: 360), context: new InsightContext(Then: Then())), "drift");
        // Another game then: nothing to compare.
        var other = new ThenHeat([new SteadyLoad(42, "Dota 2", 300, 8, 60, 55, 200)], new LoadTemps(42, 40, 400), Wed.AddDays(-120), Wed.AddDays(-30));
        None(Gen(hot, context: new InsightContext(Then: other)), "drift");
        // No time at rest to measure the room by: the room can't be ruled out.
        hot.RestTemps = null;
        None(Gen(hot, context: new InsightContext(Then: Then())), "drift");
    }

    [Fact]
    public void A_day_counts_with_the_week_before_it_for_drift()
    {
        // Twenty minutes today are too little alone; with the week before they're a fair measure.
        var r = Playing(gpu: 78, cpu: 65, restGpu: 40, restCpu: 42, minutes: 20, range: ReportRange.Day);
        None(Gen(r, context: new InsightContext(Then: Then())), "drift");
        var usual = Usual(7, 7 * 3 * 3600);
        usual.Steady = [Steady(Cyberpunk, 200, 78, 65)];
        usual.RestTemps = new LoadTemps(42, 40, 600);
        Assert.StartsWith("In Cyberpunk 2077, your GPU ran 8° hotter", Line(Gen(r, usual: usual, context: new InsightContext(Then: Then())), "drift"));
    }

    [Fact]
    public void A_drift_line_adds_up_as_shown()
    {
        // 75.4 vs 69.6 is 5.8° apart, shown as 75° vs 70°: the line says 5°, what the two shown numbers make, not 6°.
        var then = new ThenHeat([Steady(Cyberpunk, 300, 69.6, 65, 300, 8)], new LoadTemps(42, 40, 400), Wed.AddDays(-120), Wed.AddDays(-30));
        var line = Line(Gen(Playing(gpu: 75.4, cpu: 65, restGpu: 40, restCpu: 42), context: new InsightContext(Then: then)), "drift");
        Assert.Equal("In Cyberpunk 2077, your GPU ran 5° hotter than in December 2024 (75° vs 70°), and not because of the room. Dust or old thermal paste are the usual causes.", line);
    }

    [Fact]
    public void A_gpu_fan_stopped_while_the_gpu_was_hot_is_a_warning()
    {
        var r = Past();
        r.Fans = [new FanStat("GPU Fan 1", "NVIDIA GeForce RTX 3080 Ti", Gpu: true, 90, 8, 8, At1504, 81.4),
            new FanStat("GPU Fan 2", "NVIDIA GeForce RTX 3080 Ti", Gpu: true, 90, 8, 8, At1504, 81.4)];
        var usual = Usual(7, 7 * 3 * 3600);
        usual.Fans = [new FanStat("GPU Fan 1", "NVIDIA GeForce RTX 3080 Ti", Gpu: true, 400, 0, 0, null, null),
            new FanStat("GPU Fan 2", "NVIDIA GeForce RTX 3080 Ti", Gpu: true, 400, 0, 0, null, null)];
        var line = Assert.Single(Gen(r, usual: usual), i => i.Key == "fan-stopped"); // both fans: one line
        Assert.Equal("Your GPU fan stopped for 8 minutes from 3:04 PM with the GPU at 81°, when it always turns at that heat. Check it isn't blocked or unplugged.", line.Text);
        Assert.Equal((InsightTone.Hot, 88), (line.Tone, line.Priority));
    }

    [Fact]
    public void A_case_fan_that_stopped_is_named_by_its_header()
    {
        var r = Past();
        r.Fans = [new FanStat("Fan #2", "ITE IT8686E", Gpu: false, 300, 45, 45, At1504, null)];
        var usual = Usual(7, 7 * 3 * 3600);
        usual.Fans = [new FanStat("Fan #2", "ITE IT8686E", Gpu: false, 2000, 0, 0, null, null)];
        var line = Assert.Single(Gen(r, usual: usual), i => i.Key == "fan-stopped");
        Assert.Equal("Fan #2 on your motherboard stopped for 45 minutes from 3:04 PM, when it always turns. Check it isn't blocked or unplugged.", line.Text);
        Assert.Equal(InsightTone.Warn, line.Tone);
    }

    [Fact]
    public void Fans_that_stop_on_purpose_or_briefly_say_nothing()
    {
        var r = Past();
        var usual = Usual(7, 7 * 3 * 3600);
        // A header with nothing on it, or a fan that stops at idle by design: it read 0 much of the week before too.
        r.Fans = [new FanStat("Fan #4", "ITE IT8686E", Gpu: false, 300, 300, 300, At1504, null)];
        usual.Fans = [new FanStat("Fan #4", "ITE IT8686E", Gpu: false, 2000, 2000, 900, null, null)];
        None(Gen(r, usual: usual), "fan-stopped");
        usual.Fans = [new FanStat("Fan #4", "ITE IT8686E", Gpu: false, 2000, 60, 30, null, null)]; // 3% of the time: not "always"
        None(Gen(r, usual: usual), "fan-stopped");
        // A blip: a GPU fan two minutes at 0 (starting up), a case fan five.
        r.Fans = [new FanStat("GPU Fan 1", "NVIDIA", Gpu: true, 90, 2, 2, At1504, 72), new FanStat("Fan #2", "ITE IT8686E", Gpu: false, 300, 9, 9, At1504, null)];
        usual.Fans = [new FanStat("GPU Fan 1", "NVIDIA", Gpu: true, 400, 0, 0, null, null), new FanStat("Fan #2", "ITE IT8686E", Gpu: false, 2000, 0, 0, null, null)];
        None(Gen(r, usual: usual), "fan-stopped");
        // Nothing known about it the week before (the first week, or fan history just started).
        r.Fans = [new FanStat("GPU Fan 1", "NVIDIA", Gpu: true, 90, 8, 8, At1504, 81)];
        usual.Fans = [new FanStat("GPU Fan 1", "NVIDIA", Gpu: true, 59, 0, 0, null, null)];
        None(Gen(r, usual: usual), "fan-stopped");
        None(Gen(r), "fan-stopped");
    }

    [Fact]
    public void The_gpu_slowing_itself_down_is_the_top_warning()
    {
        var r = Past();
        r.GpuThrottle = new Throttling(14, 15.2, 84);
        var line = Assert.Single(Gen(r), i => i.Key == "throttle");
        Assert.Equal("Your GPU slowed itself for 14 minutes to stay cool: clocks fell about 15% once it reached 84°.", line.Text);
        Assert.Equal(InsightTone.Hot, line.Tone);
        Assert.Equal(line, Gen(r)[1]); // right after the screen-time line, which heads every list
    }

    // ── Records, streaks, habits ──

    [Fact]
    public void Records_say_how_far_back_they_hold()
    {
        var r = Past();
        r.Records = [new RecordNote(RecordKind.LongestGameSession, "4h 12m", "Dota 2", 90), new RecordNote(RecordKind.HottestGpu, "84°", null, 30),
            new RecordNote(RecordKind.HottestCpu, "88°", null, 30), new RecordNote(RecordKind.MostScreenTime, "11h 20m", null, 365)];
        var lines = Gen(r).Where(i => i.Key == "record").ToList();
        Assert.Equal(["Longest gaming session in three months: Dota 2, 4h 12m.", "Hottest GPU peak in a month: 84°.", "Hottest CPU peak in a month: 88°.",
            "Most screen time in a year: 11h 20m."], lines.Select(i => i.Text));
        Assert.Equal([InsightTone.Good, InsightTone.Neutral, InsightTone.Neutral, InsightTone.Neutral], lines.Select(i => i.Tone));
        Assert.Equal("Beats every day of the 90 before", lines[0].Detail);
    }

    [Theory]
    [InlineData(2, null)]
    [InlineData(3, "Third day in a row over your usual screen time.")]
    [InlineData(5, "Fifth day in a row over your usual screen time.")]
    [InlineData(7, "Seventh day in a row over your usual screen time.")]
    [InlineData(9, "9 days in a row over your usual screen time.")]
    public void A_streak_of_days_over_your_usual_is_counted(int days, string? text)
    {
        var r = Past();
        r.StreakDays = days;
        var line = Gen(r).SingleOrDefault(i => i.Key == "streak");
        Assert.Equal(text, line?.Text);
    }

    [Fact]
    public void A_day_is_compared_with_its_own_weekday_when_there_are_enough_of_them()
    {
        var r = Past(active: 5 * 3600);
        r.Apps.Add(App("Dota 2", AppCategory.Game, 2 * 3600));
        var weekday = new InsightContext(new WeekdayUsual(DayOfWeek.Wednesday, 4, 3 * 3600, 3600));
        var usual = Usual(7, 7 * 3 * 3600, 7 * 3600); // the 7-day average would say the same numbers, in other words
        var list = Gen(r, usual: usual, context: weekday);
        Assert.Equal("That's 2h 00m more than your usual Wednesday (3h 00m).", Line(list, "screen-compare"));
        Assert.Equal("You gamed for 2h 00m (Dota 2). That's 1h 00m more than your usual Wednesday (1h 00m).", Line(list, "gaming"));

        var today = Now(active: 5 * 3600);
        today.Apps.Add(App("Dota 2", AppCategory.Game, 2 * 3600));
        var inProgress = new InsightContext(new WeekdayUsual(DateTime.Today.DayOfWeek, 4, 3 * 3600, 3600));
        Assert.Equal($"You're already 2h 00m past your usual {DateTime.Today.DayOfWeek} of 3h 00m.", Line(Gen(today, usual: usual, context: inProgress), "screen-compare"));
        var under = Now(active: 2 * 3600);
        None(Gen(under, usual: usual, context: inProgress), "screen-compare"); // not past it yet: nothing to say
    }

    // ── Not the same thing every day ──

    [Fact]
    public void The_same_top_app_as_the_period_before_is_said_in_passing()
    {
        var r = Past();
        r.Apps.Add(App("Chrome", AppCategory.Browser, 3 * 3600));
        var before = Past();
        before.Apps.Add(App("Chrome", AppCategory.Browser, 2 * 3600));
        var line = Assert.Single(Gen(r, before), i => i.Key == "top-app");
        Assert.Equal(("Chrome took most of your time again: 3h 00m (100% of active time).", 25), (line.Text, line.Priority));
        before.Apps[0] = App("Code", AppCategory.Development, 2 * 3600);
        line = Assert.Single(Gen(r, before), i => i.Key == "top-app");
        Assert.Equal(("Chrome took most of your time: 3h 00m (100% of active time).", 50), (line.Text, line.Priority));
    }

    [Fact]
    public void The_same_background_hog_and_memory_hog_as_before_arent_repeated_and_comfort_ranks_lower()
    {
        var r = Past();
        var chat = App("Discord", AppCategory.Communication, 10 * 60);
        chat.BackgroundSec = 5 * 3600;
        var game = App("Dota 2", AppCategory.Game, 3600);
        game.MemMax = 8000;
        r.Apps.AddRange([chat, game]);
        r.CpuTempPeak = new Peak(60, At1504, null);
        var same = Past();
        var chatBefore = App("Discord", AppCategory.Communication, 10 * 60);
        chatBefore.BackgroundSec = 4 * 3600;
        var gameBefore = App("Dota 2", AppCategory.Game, 3600);
        gameBefore.MemMax = 7000;
        same.Apps.AddRange([chatBefore, gameBefore]);
        same.CpuTempPeak = new Peak(62, At1504, null);

        var fresh = Gen(r);
        Assert.Contains(fresh, i => i.Key == "idle-app");
        Assert.Contains(fresh, i => i.Key == "memory");
        Assert.Equal(30, Assert.Single(fresh, i => i.Key == "comfortable").Priority);

        var repeated = Gen(r, same);
        None(repeated, "idle-app");
        None(repeated, "memory");
        Assert.Equal(20, Assert.Single(repeated, i => i.Key == "comfortable").Priority);
    }

    // ── Crashes, with what came before them ──

    [Fact]
    public void A_crash_whose_title_names_the_culprit_doesnt_say_it_twice()
    {
        var r = Past();
        r.Crashes.Add(new CrashEvent { Id = 1, Ts = TimeUtil.ToUnix(At1504), Kind = CrashKind.UnexpectedShutdown, DuringSleep = true });
        Assert.Equal("PC lost power while asleep at 3:04 PM.", Line(Gen(r), "crash"));
        r.Crashes[0] = new CrashEvent { Id = 1, Ts = TimeUtil.ToUnix(At1504), Kind = CrashKind.UnexpectedShutdown };
        Assert.Equal("PC shut off unexpectedly at 3:04 PM: a power cut or a hard freeze.", Line(Gen(r), "crash"));
    }

    [Fact]
    public void A_crash_after_heat_says_so()
    {
        var r = Past();
        r.Crashes.Add(new CrashEvent { Id = 7, Ts = TimeUtil.ToUnix(At1504), Kind = CrashKind.GpuDriverReset });
        r.CrashContexts[7] = new CrashContext(70, 86, null, null);
        var line = Line(Gen(r), "crash"); // the GPU over its 83° alert limit
        Assert.EndsWith(" The GPU was at 86° just before.", line);
        Assert.Equal("12 Mar 3:04 PM: CPU 70°, GPU 86° just before", Assert.Single(Gen(r), i => i.Key == "crash").Detail);
        r.CrashContexts[7] = new CrashContext(90, 60, null, null);
        Assert.EndsWith(" The CPU was at 90° just before.", Line(Gen(r), "crash"));
        // 82° is where many GPUs sit in every game: under the alert limit, heat isn't blamed.
        r.CrashContexts[7] = new CrashContext(60, 82, null, null);
        Assert.Equal("Graphics driver reset at 3:04 PM.", Line(Gen(r), "crash"));
        Assert.EndsWith(" The GPU was at 82° just before.", Line(Gen(r, alerts: new AlertSettings { GpuLimit = 80 }), "crash")); // the user's own limit
    }

    [Fact]
    public void A_crash_detail_leaves_out_temperatures_it_doesnt_have()
    {
        var r = Past();
        r.Crashes.Add(new CrashEvent { Id = 7, Ts = TimeUtil.ToUnix(At1504), Kind = CrashKind.GpuDriverReset });
        r.CrashContexts[7] = new CrashContext(null, null, null, null);
        Assert.Equal("12 Mar 3:04 PM", Assert.Single(Gen(r), i => i.Key == "crash").Detail);
        r.CrashContexts[7] = new CrashContext(null, 70, null, null);
        Assert.Equal("12 Mar 3:04 PM: GPU 70° just before", Assert.Single(Gen(r), i => i.Key == "crash").Detail);
    }

    [Fact]
    public void Crashes_of_the_app_in_front_dont_say_it_was_in_front()
    {
        var r = Past();
        var game = App("Elden Ring", AppCategory.Game, 3600, "eldenring.exe");
        r.Apps.Add(game);
        r.Crashes.AddRange([new CrashEvent { Id = 1, Ts = TimeUtil.ToUnix(At1504), Kind = CrashKind.AppCrash, AppExe = "eldenring.exe" },
            new CrashEvent { Id = 2, Ts = TimeUtil.ToUnix(At1504.AddHours(-2)), Kind = CrashKind.AppCrash, AppExe = "eldenring.exe" }]);
        r.CrashContexts[1] = new CrashContext(60, 60, game.Id, null);
        r.CrashContexts[2] = new CrashContext(60, 60, game.Id, null);
        Assert.DoesNotContain("in front", Line(Gen(r), "crash"));
    }

    [Fact]
    public void Crashes_with_something_in_common_have_it_pointed_out()
    {
        var r = Past();
        r.Crashes.AddRange([new CrashEvent { Id = 1, Ts = TimeUtil.ToUnix(At1504), Kind = CrashKind.GpuDriverReset },
            new CrashEvent { Id = 2, Ts = TimeUtil.ToUnix(At1504.AddHours(-2)), Kind = CrashKind.GpuDriverReset }]);
        r.CrashContexts[1] = new CrashContext(70, 86, null, null);
        r.CrashContexts[2] = new CrashContext(70, 84, null, null);
        Assert.EndsWith(" Both came minutes after the GPU was over 84°.", Line(Gen(r), "crash"));

        var discord = App("Discord", AppCategory.Communication, 3600);
        r.Apps.Add(discord);
        r.CrashContexts[1] = new CrashContext(70, 60, discord.Id, null);
        r.CrashContexts[2] = new CrashContext(70, 60, discord.Id, null);
        Assert.EndsWith(" Both happened while Discord was in front.", Line(Gen(r), "crash"));

        r.Crashes.Add(new CrashEvent { Id = 3, Ts = TimeUtil.ToUnix(At1504.AddHours(-4)), Kind = CrashKind.SystemCrash, Code = "0x124" });
        r.CrashContexts[3] = new CrashContext(70, 60, discord.Id, null);
        Assert.EndsWith(" All 3 happened while Discord was in front.", Line(Gen(r), "crash"));
        r.CrashContexts[3] = new CrashContext(70, 60, null, null); // one without: nothing in common
        Assert.DoesNotContain("in front", Line(Gen(r), "crash"));
    }

    [Fact]
    public void An_app_left_open_but_unused_is_pointed_out()
    {
        var r = Past();
        var discord = App("Discord", AppCategory.Communication, 10 * 60);
        discord.BackgroundSec = 1.5 * 3600;
        discord.MinimizedSec = 0.5 * 3600;
        r.Apps.Add(discord);
        Assert.Equal("Discord sat open in the background for 2h 00m but you only used it for 10m.", Line(Gen(r), "idle-app"));
        discord.ActiveSec = 30 * 60; // a quarter of the open time: it was used
        None(Gen(r), "idle-app");
    }

    [Fact]
    public void An_app_open_all_along_but_never_used_is_said_so_not_used_for_0s()
    {
        var r = Past();
        var discord = App("Discord", AppCategory.Communication, 0);
        discord.BackgroundSec = 7 * 3600;
        r.Apps.Add(discord);
        Assert.Equal("Discord sat open in the background for 7h 00m and you never opened it.", Line(Gen(r), "idle-app"));
    }

    [Fact]
    public void A_big_memory_user_is_named_from_four_gigabytes()
    {
        var r = Past();
        var game = App("Dota 2", AppCategory.Game, 3600);
        game.MemMax = 6144;
        r.Apps.Add(game);
        Assert.Equal("Dota 2 used the most memory, peaking at 6.0 GB.", Line(Gen(r), "memory"));
        game.MemMax = 4095;
        None(Gen(r), "memory");
    }

    [Fact]
    public void Long_stretches_left_on_with_nobody_there_are_noted_plainly()
    {
        var r = Past();
        r.LongAwaySec = 2 * 3600;
        var away = Assert.Single(Gen(r), i => i.Key == "away");
        Assert.Equal("Your PC sat on with nobody there for 2h 00m that day.", away.Text);
        Assert.Equal(InsightTone.Neutral, away.Tone);
        var today = Now();
        today.LongAwaySec = 3600;
        Assert.Equal("Your PC sat on with nobody there for 1h 00m today.", Line(Gen(today), "away"));
        r.LongAwaySec = 3599;
        r.AwaySec = 5 * 3600; // twelve coffee breaks aren't the PC left on: only the long stretches count
        None(Gen(r), "away");
        var week = Past(ReportRange.Week);
        week.LongAwaySec = 10 * 3600;
        None(Gen(week), "away");
    }

    [Fact]
    public void Cool_temperatures_are_reassuring()
    {
        var r = Past();
        r.CpuTempPeak = new Peak(69.9, At1504, null);
        var ok = Assert.Single(Gen(r), i => i.Key == "comfortable");
        Assert.Equal(("Temperatures stayed comfortable the whole time.", InsightTone.Good), (ok.Text, ok.Tone));
        var today = Now();
        today.CpuTempPeak = new Peak(50, DateTime.Now, null);
        today.GpuTempPeak = new Peak(50, DateTime.Now, null);
        Assert.Equal("Temperatures have stayed comfortable so far.", Line(Gen(today), "comfortable"));
        r.GpuTempPeak = new Peak(70, At1504, null);
        None(Gen(r), "comfortable");
        None(Gen(Past()), "comfortable"); // no readings at all: nothing to reassure about
    }

    [Fact]
    public void Temperatures_follow_the_fahrenheit_setting()
    {
        Units.Fahrenheit = true;
        var r = Past();
        r.CpuTempPeak = new Peak(90, At1504, null);
        r.CpuTempHeld = 88;
        r.GpuOverLimitMin = 2;
        var list = Gen(r);
        Assert.Equal("CPU peaked at 194° at 3:04 PM.", Line(list, "peak"));
        Assert.Equal("Your GPU reached your 181° alert limit for 2 minutes.", Line(list, "over-limit"));
    }

    [Fact]
    public void A_built_report_words_its_warnings_with_the_users_alert_limits()
    {
        using var t = new TestDb();
        long ts = TimeUtil.ToUnix(Wed.AddHours(20));
        // Two minutes held at 87° (the minute's average), a spike to 95° in another: the spike is no minute at the limit.
        for (int i = 0; i < 30; i++) t.Db.WriteMinute(Make.Minute(ts + i * 60, cpu: i < 2 ? 87 : 60, cpuMax: i < 2 ? 89 : i == 10 ? 95 : 62, gpuMax: 50));
        var settings = Make.Settings();
        settings.Alerts.CpuLimit = 86;
        var r = ReportBuilder.Build(t.Db, ReportRange.Day, Wed, settings);
        Assert.Equal("Your CPU reached your 86° alert limit for 2 minutes.", Line(r.Insights, "over-limit"));
        settings.Alerts.CpuLimit = 88;
        r = ReportBuilder.Build(t.Db, ReportRange.Day, Wed, settings);
        None(r.Insights, "over-limit");
        Assert.Equal("CPU peaked at 95° at 8:10 PM.", Line(r.Insights, "peak")); // held 87° is worth it; the peak is the peak
    }

    [Fact]
    public void Everything_at_once_comes_most_important_first()
    {
        var r = Past(active: 6 * 3600, on: 8 * 3600);
        var game = App("Dota 2", AppCategory.Game, 4 * 3600);
        game.GpuTempAvg = 75; game.CpuTempAvg = 70; game.MemMax = 8000;
        var chat = App("Discord", AppCategory.Communication, 10 * 60);
        chat.BackgroundSec = 5 * 3600; chat.CpuTempAvg = 72; chat.GpuTempAvg = 40;
        r.Apps.AddRange([game, chat]);
        r.Crashes.Add(new CrashEvent { Ts = TimeUtil.ToUnix(At1504), Kind = CrashKind.GpuDriverReset });
        r.CpuOverLimitMin = 2;
        r.GpuTempPeak = new Peak(86, At1504, "Dota 2");
        r.GpuTempHeld = 84;
        r.HotSpotGap = new LoadTemps(null, 32, 20);
        r.DayStart = Wed.AddHours(9); r.LastActive = Wed.AddHours(23);
        r.LongestStretch = new Stretch(Wed.AddHours(13), Wed.AddHours(16), "Dota 2");
        r.LongAwaySec = 3600;
        r.Sessions.Add(new SessionInfo { Name = "Dota 2", Start = At1504, End = At1504.AddHours(3), ActiveSec = 3 * 3600, IsGame = true });
        var usual = Usual(7, 7 * 3 * 3600, 7 * 3600);
        var list = Gen(r, Past(active: 3600), usual);
        Assert.Equal(["screen", "crash", "over-limit", "peak", "hotspot", "screen-compare", "gaming", "stretch", "span",
            "top-app", "away", "idle-app", "memory"], list.Select(i => i.Key));
        Assert.Equal(list.OrderByDescending(i => i.Priority).Select(i => i.Priority), list.Select(i => i.Priority));
        Assert.All(list, i => Assert.False(string.IsNullOrEmpty(i.Icon)));
    }
}
