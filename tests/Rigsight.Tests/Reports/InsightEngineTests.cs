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

    private static List<Insight> Gen(Report r, Report? previous = null, Report? usual = null, AlertSettings? alerts = null) =>
        InsightEngine.Generate(r, previous, usual, alerts ?? new AlertSettings());

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
        Assert.Equal("Dota 2 stopped responding at 3:04 PM: not responding.", crash.Text);
        Assert.Equal((InsightTone.Warn, 95), (crash.Tone, crash.Priority));
    }

    [Fact]
    public void Several_crashes_point_to_the_crashes_page_and_a_long_period_gives_the_date()
    {
        var r = Past(ReportRange.Week);
        r.Crashes.Add(new CrashEvent { Ts = TimeUtil.ToUnix(At1504), Kind = CrashKind.SystemCrash, Code = "0x00000124" });
        r.Crashes.Add(new CrashEvent { Ts = TimeUtil.ToUnix(At1504.AddDays(-1)), Kind = CrashKind.GpuDriverReset });
        r.Crashes.Add(new CrashEvent { Ts = TimeUtil.ToUnix(At1504.AddDays(-2)), Kind = CrashKind.UnexpectedShutdown });
        Assert.Equal("Windows crashed (blue screen) at Wed 12 Mar, 3:04 PM: whea_uncorrectable_error. (3 crashes in total — see the Crashes page)",
            Line(Gen(r), "crash"));
    }

    [Theory]
    [InlineData(CrashKind.GpuDriverReset, "", "Graphics driver reset at 3:04 PM: gpu driver.")]
    [InlineData(CrashKind.UnexpectedShutdown, "", "PC shut off unexpectedly at 3:04 PM: power / hard freeze.")]
    [InlineData(CrashKind.AppHang, "unknown.exe", "unknown stopped responding at 3:04 PM: not responding.")]
    public void Each_kind_of_crash_reads_naturally(CrashKind kind, string exe, string text)
    {
        var r = Past();
        r.Crashes.Add(new CrashEvent { Ts = TimeUtil.ToUnix(At1504), Kind = kind, AppExe = exe });
        Assert.Equal(text, Line(Gen(r), "crash"));
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
        Assert.Equal([$"Your CPU reached your 80° alert limit during {during}.", $"Your GPU reached your 83° alert limit during {during}."], over.Select(i => i.Text));
        Assert.All(over, i => Assert.Equal((InsightTone.Hot, 90), (i.Tone, i.Priority)));
    }

    [Theory]
    [InlineData(74.9, null)]
    [InlineData(75, InsightTone.Warn)]
    [InlineData(87.9, InsightTone.Warn)]
    [InlineData(88, InsightTone.Hot)]
    public void A_high_cpu_peak_is_mentioned_with_what_was_running(double value, InsightTone? tone)
    {
        var r = Past();
        r.Apps.Add(App("Dota 2", AppCategory.Game, 3600));
        r.CpuTempPeak = new Peak(value, At1504, "Dota 2");
        var list = Gen(r);
        if (tone is null) { None(list, "peak"); return; }
        var peak = Assert.Single(list, i => i.Key == "peak");
        Assert.Equal($"CPU peaked at {value:0}° at 3:04 PM while playing Dota 2.", peak.Text);
        Assert.Equal((tone.Value, 85), (peak.Tone, peak.Priority));
    }

    [Theory]
    [InlineData(74.9, null)]
    [InlineData(75, InsightTone.Warn)]
    [InlineData(85, InsightTone.Hot)]
    public void A_high_gpu_peak_is_mentioned_with_its_hot_spot(double value, InsightTone? tone)
    {
        var r = Past(ReportRange.Month);
        r.Apps.Add(App("Chrome", AppCategory.Browser, 3600));
        r.GpuTempPeak = new Peak(value, At1504, "Chrome");
        r.GpuHotPeak = new Peak(95.4, At1504, "Chrome");
        var list = Gen(r);
        if (tone is null) { None(list, "peak"); return; }
        Assert.Equal($"GPU peaked at {value:0}° (hot spot 95°) at Wed 12 Mar, 3:04 PM while browsing in Chrome.", Line(list, "peak"));
    }

    [Fact]
    public void A_peak_without_an_app_or_hot_spot_just_says_when()
    {
        var r = Past();
        r.CpuTempPeak = new Peak(80, At1504, null);
        r.GpuTempPeak = new Peak(80, At1504, "Some Tool"); // not in the apps list: an app of no known kind
        var peaks = Gen(r).Where(i => i.Key == "peak").Select(i => i.Text);
        Assert.Equal(["CPU peaked at 80° at 3:04 PM.", "GPU peaked at 80° at 3:04 PM while using Some Tool."], peaks);
    }

    [Theory]
    [InlineData(AppCategory.Media, "while Spotify was playing")]
    [InlineData(AppCategory.Communication, "while on Spotify")]
    [InlineData(AppCategory.Development, "while working in Spotify")]
    [InlineData(AppCategory.Productivity, "while working in Spotify")]
    [InlineData(AppCategory.Launcher, "while in Spotify")]
    [InlineData(AppCategory.System, "while in Spotify")]
    [InlineData(AppCategory.Other, "while using Spotify")]
    public void The_activity_words_fit_the_kind_of_app(AppCategory category, string words)
    {
        var r = Past();
        r.Apps.Add(App("Spotify", category, 3600));
        r.CpuTempPeak = new Peak(80, At1504, "Spotify");
        Assert.Equal($"CPU peaked at 80° at 3:04 PM {words}.", Line(Gen(r), "peak"));
    }

    [Fact]
    public void A_peak_is_not_repeated_when_the_limit_warning_already_covers_it()
    {
        var r = Past();
        r.CpuTempPeak = new Peak(95, At1504, null);
        r.GpuTempPeak = new Peak(90, At1504, null);
        r.CpuOverLimitMin = 3;
        var peaks = Gen(r).Where(i => i.Key == "peak").ToList();
        Assert.Equal("GPU peaked at 90° at 3:04 PM.", Assert.Single(peaks).Text);
    }

    [Fact]
    public void A_wide_hot_spot_gap_suggests_a_repaste()
    {
        var r = Past();
        r.HotSpotGap = new LoadTemps(null, 27.4, 10);
        var gap = Assert.Single(Gen(r), i => i.Key == "hotspot");
        Assert.Equal("Under load, your GPU hot spot ran 27° above the core temperature. A gap over about 25° usually means the thermal paste or cooler contact has worn, and a repaste would help.", gap.Text);
        Assert.Equal((InsightTone.Warn, 80), (gap.Tone, gap.Priority));
    }

    [Theory]
    [InlineData(ReportRange.Day, 22, ", up from 22° over the previous 7 days")]
    [InlineData(ReportRange.Week, 22, ", up from 22° in the 7 days before")]
    [InlineData(ReportRange.Day, 25, "")] // under 3° wider: no trend
    public void A_widening_hot_spot_gap_says_how_much_it_grew(ReportRange range, double before, string trend)
    {
        var r = Past(range);
        r.HotSpotGap = new LoadTemps(null, 27.6, 30);
        var usual = Usual(3, 3 * 3600);
        usual.HotSpotGap = new LoadTemps(null, before, 10);
        Assert.Equal($"Under load, your GPU hot spot ran 28° above the core temperature{trend}. A gap over about 25° usually means the thermal paste or cooler contact has worn, and a repaste would help.",
            Line(Gen(r, usual: usual), "hotspot"));
    }

    [Theory]
    [InlineData(24.9, 30)]
    [InlineData(30, 9)]
    public void A_narrow_or_brief_hot_spot_gap_is_not_mentioned(double gap, int minutes)
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
    [InlineData(ReportRange.Month, "the month before")]
    [InlineData(ReportRange.Year, "the year before")]
    public void A_finished_period_is_compared_with_the_one_before(ReportRange range, string than)
    {
        Assert.Equal($"That's 2h 30m more screen time than {than}.", Line(Gen(Past(range, active: 5 * 3600), Past(range, active: 2.5 * 3600)), "screen-compare"));
        Assert.Equal($"That's 2h 30m less screen time than {than}.", Line(Gen(Past(range, active: 1 * 3600), Past(range, active: 3.5 * 3600)), "screen-compare"));
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
        r.Sessions.Add(new SessionInfo { Name = "Dota 2", Start = At1504, ActiveSec = 19 * 60, IsGame = true });
        Assert.Equal("You gamed for 1h 10m (2 games).", Line(Gen(r), "gaming"));
        r.Sessions.Add(new SessionInfo { Name = "Tetris", Start = At1504, ActiveSec = 20 * 60, IsGame = true });
        Assert.Equal("You gamed for 1h 10m (2 games). Longest session: Tetris, 20m starting Wed 12 Mar, 3:04 PM.", Line(Gen(r), "gaming"));
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

    [Theory]
    [InlineData(71.9, InsightTone.Neutral)]
    [InlineData(72, InsightTone.Warn)]
    [InlineData(82, InsightTone.Hot)]
    public void The_app_that_ran_the_gpu_and_cpu_hottest_is_named(double gpuAvg, InsightTone tone)
    {
        var r = Past();
        var game = App("Dota 2", AppCategory.Game, 3600);
        game.GpuTempAvg = gpuAvg;
        game.CpuTempAvg = 60;
        var browser = App("Chrome", AppCategory.Browser, 3600);
        browser.GpuTempAvg = 45;
        browser.CpuTempAvg = 65.4;
        r.Apps.AddRange([game, browser]);
        var hottest = Gen(r).Where(i => i.Key == "hottest-app").ToList();
        Assert.Equal([$"Dota 2 ran your GPU the hottest, averaging {gpuAvg:0}°.", "Chrome ran your CPU the hottest, averaging 65°."], hottest.Select(i => i.Text));
        Assert.Equal(tone, hottest[0].Tone);
        Assert.Equal(InsightTone.Neutral, hottest[1].Tone);
    }

    [Fact]
    public void One_app_hottest_for_both_is_named_once_and_a_lone_or_brief_app_not_at_all()
    {
        var r = Past();
        var game = App("Dota 2", AppCategory.Game, 3600);
        game.GpuTempAvg = 70;
        game.CpuTempAvg = 70;
        var browser = App("Chrome", AppCategory.Browser, 3600);
        browser.GpuTempAvg = 40;
        browser.CpuTempAvg = 40;
        r.Apps.AddRange([game, browser]);
        Assert.Equal(["Dota 2 ran your GPU the hottest, averaging 70°."], Gen(r).Where(i => i.Key == "hottest-app").Select(i => i.Text));

        browser.ActiveSec = 9 * 60; // under ten minutes: not ranked, which leaves one app to compare
        None(Gen(r), "hottest-app");
    }

    [Fact]
    public void Temperatures_are_compared_at_the_same_load_with_your_usual()
    {
        var r = Past();
        r.GpuLoadTemps = new LoadTemps(70, 75.2, 30);
        r.IdleTemps = new LoadTemps(40, 35, 60);
        var usual = Usual(5, 5 * 3600);
        usual.GpuLoadTemps = new LoadTemps(66, 70, 40);
        usual.IdleTemps = new LoadTemps(45, 35, 60);
        var lines = Gen(r, usual: usual).Where(i => i.Key == "temp-compare").ToList();
        Assert.Equal(["Under heavy load, your GPU ran 5° hotter than over the previous 7 days (75° vs 70°).",
            "At idle, your CPU ran 5° cooler than over the previous 7 days (40° vs 45°)."], lines.Select(i => i.Text));
        Assert.Equal([(InsightTone.Warn, 70), (InsightTone.Good, 45)], lines.Select(i => (i.Tone, i.Priority)));
    }

    [Fact]
    public void A_warmer_idle_suggests_the_room_or_dust_and_at_most_two_comparisons_are_made()
    {
        var r = Past(ReportRange.Week);
        r.GpuLoadTemps = new LoadTemps(80, 80, 30);
        r.CpuLoadTemps = new LoadTemps(80, 80, 30);
        r.IdleTemps = new LoadTemps(50, 50, 60);
        var usual = Usual(5, 5 * 3600);
        usual.GpuLoadTemps = new LoadTemps(70, 70, 40);
        usual.CpuLoadTemps = new LoadTemps(70, 70, 40);
        usual.IdleTemps = new LoadTemps(40, 40, 60);
        Assert.Equal(2, Gen(r, usual: usual).Count(i => i.Key == "temp-compare"));

        r.GpuLoadTemps = r.CpuLoadTemps = null;
        var idle = Gen(r, usual: usual).Where(i => i.Key == "temp-compare").Select(i => i.Text).ToList();
        Assert.Equal(["At idle, your GPU ran 10° hotter than in the 7 days before (50° vs 40°). A warmer room or dust build-up are the usual causes.",
            "At idle, your CPU ran 10° hotter than in the 7 days before (50° vs 40°). A warmer room or dust build-up are the usual causes."], idle);
    }

    [Fact]
    public void Temperatures_are_not_compared_on_too_few_minutes_small_differences_or_without_history()
    {
        var r = Past();
        var usual = Usual(5, 5 * 3600);
        r.GpuLoadTemps = new LoadTemps(80, 80, 19); // under 20 minutes of load
        usual.GpuLoadTemps = new LoadTemps(70, 70, 40);
        r.IdleTemps = new LoadTemps(42.9, 40, 60);  // under 3° apart
        usual.IdleTemps = new LoadTemps(40, 40, 60);
        None(Gen(r, usual: usual), "temp-compare");
        r.GpuLoadTemps = new LoadTemps(80, 80, 30);
        None(Gen(r, usual: Usual(2, 2 * 3600)), "temp-compare");
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
    public void A_long_unattended_day_suggests_sleep()
    {
        var r = Past();
        r.AwaySec = 2 * 3600;
        var away = Assert.Single(Gen(r), i => i.Key == "away");
        Assert.Equal("Your PC sat unattended for 2h 00m this day. Letting it sleep sooner would save power.", away.Text);
        Assert.Equal(InsightTone.Warn, away.Tone);
        var today = Now();
        today.AwaySec = 3600;
        Assert.Equal("Your PC sat unattended for 1h 00m today. Letting it sleep sooner would save power.", Line(Gen(today), "away"));
        r.AwaySec = 3599;
        None(Gen(r), "away");
        var week = Past(ReportRange.Week);
        week.AwaySec = 10 * 3600;
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
        r.CpuTempPeak = new Peak(80, At1504, null);
        r.GpuOverLimitMin = 2;
        r.HotSpotGap = new LoadTemps(null, 30, 10);
        var list = Gen(r);
        Assert.Equal("CPU peaked at 176° at 3:04 PM.", Line(list, "peak"));
        Assert.Equal("Your GPU reached your 181° alert limit during 2 minutes.", Line(list, "over-limit"));
        Assert.StartsWith("Under load, your GPU hot spot ran 54° above the core temperature.", Line(list, "hotspot"));
    }

    [Fact]
    public void A_built_report_words_its_warnings_with_the_users_alert_limits()
    {
        using var t = new TestDb();
        long ts = TimeUtil.ToUnix(Wed.AddHours(20));
        for (int i = 0; i < 30; i++) t.Db.WriteMinute(Make.Minute(ts + i * 60, cpuMax: i < 2 ? 81 : 60, gpuMax: 50));
        var settings = Make.Settings();
        settings.Alerts.CpuLimit = 80;
        var r = ReportBuilder.Build(t.Db, ReportRange.Day, Wed, settings);
        Assert.Equal("Your CPU reached your 80° alert limit during 2 minutes.", Line(r.Insights, "over-limit"));
        settings.Alerts.CpuLimit = 82;
        r = ReportBuilder.Build(t.Db, ReportRange.Day, Wed, settings);
        None(r.Insights, "over-limit");
        Assert.Equal("CPU peaked at 81° at 8:00 PM.", Line(r.Insights, "peak"));
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
        r.GpuTempPeak = new Peak(80, At1504, "Dota 2");
        r.HotSpotGap = new LoadTemps(null, 26, 20);
        r.DayStart = Wed.AddHours(9); r.LastActive = Wed.AddHours(23);
        r.LongestStretch = new Stretch(Wed.AddHours(13), Wed.AddHours(16), "Dota 2");
        r.AwaySec = 3600;
        r.Sessions.Add(new SessionInfo { Name = "Dota 2", Start = At1504, ActiveSec = 3 * 3600, IsGame = true });
        r.GpuLoadTemps = new LoadTemps(70, 80, 60);
        var usual = Usual(7, 7 * 3 * 3600, 7 * 3600);
        usual.GpuLoadTemps = new LoadTemps(70, 70, 60);
        var list = Gen(r, Past(active: 3600), usual);
        Assert.Equal(["screen", "crash", "over-limit", "peak", "hotspot", "temp-compare", "screen-compare", "gaming", "stretch", "span",
            "top-app", "hottest-app", "hottest-app", "away", "idle-app", "memory"], list.Select(i => i.Key));
        Assert.Equal(list.OrderByDescending(i => i.Priority).Select(i => i.Priority), list.Select(i => i.Priority));
        Assert.All(list, i => Assert.False(string.IsNullOrEmpty(i.Icon)));
    }
}
