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
    [InlineData(CrashKind.AppCrash, "game.exe", "game.exe", "c0000005", "game crashed at 3:04 PM: its own code.")]
    [InlineData(CrashKind.AppCrash, "game.exe", "SomeMod.dll", null, "game crashed at 3:04 PM: SomeMod.dll.")]
    [InlineData(CrashKind.AppCrash, "game.exe", "", null, "game crashed at 3:04 PM.")]
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
                        Assert.Equal($"{ex.Title} at 3:04 PM: {ex.Cause}.", line);
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
        Assert.Equal([$"Your CPU reached your 80° alert limit during {during}.", $"Your GPU reached your 83° alert limit during {during}."], over.Select(i => i.Text));
        Assert.All(over, i => Assert.Equal((InsightTone.Hot, 90), (i.Tone, i.Priority)));
    }

    [Theory]
    [InlineData(74.9, null)]
    [InlineData(75, InsightTone.Warn)]
    [InlineData(87.9, InsightTone.Warn)]
    [InlineData(88, InsightTone.Hot)]
    public void A_high_cpu_peak_is_mentioned_with_the_app_doing_the_work(double value, InsightTone? tone)
    {
        var r = Past();
        r.Apps.Add(App("Dota 2", AppCategory.Game, 3600));
        r.CpuTempPeak = new Peak(value, At1504, "Dota 2");
        var list = Gen(r);
        if (tone is null) { None(list, "peak"); return; }
        var peak = Assert.Single(list, i => i.Key == "peak");
        Assert.Equal($"CPU peaked at {value:0}° at 3:04 PM, with Dota 2 working it hardest.", peak.Text);
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
        Assert.Equal($"GPU peaked at {value:0}° (hot spot 95°) at Wed 12 Mar, 3:04 PM, with Chrome working it hardest.", Line(list, "peak"));
    }

    [Fact]
    public void A_peak_without_an_app_or_hot_spot_just_says_when()
    {
        var r = Past();
        r.CpuTempPeak = new Peak(80, At1504, null);
        r.GpuTempPeak = new Peak(80, At1504, "Some Tool"); // not in the apps list: working in the background
        var peaks = Gen(r).Where(i => i.Key == "peak").Select(i => i.Text);
        Assert.Equal(["CPU peaked at 80° at 3:04 PM.", "GPU peaked at 80° at 3:04 PM, with Some Tool working it hardest."], peaks);
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

        r.GpuHotMinutes = 90; // an hour and a half: worth a warning, and shared
        r.GpuHotByApp = [new HotShare("Rematch", 50), new HotShare("Dota 2", 30)];
        var gpu = Gen(r).First(i => i.Key == "hot-app");
        Assert.Equal("Your GPU spent 1h 30m over 75°, mostly on Rematch.", gpu.Text);
        Assert.Equal(InsightTone.Warn, gpu.Tone);

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
        Assert.Equal(InsightTone.Warn, line.Tone);
    }

    [Fact]
    public void A_game_hotter_than_the_others_is_named_against_their_average()
    {
        var r = Past();
        var a = App("Rematch", AppCategory.Game, 3600);
        a.GpuTempAvg = 74;
        var b = App("Dota 2", AppCategory.Game, 3600);
        b.GpuTempAvg = 66;
        var c = App("Hades", AppCategory.Game, 3600);
        c.GpuTempAvg = 66;
        r.Apps.AddRange([a, b, c]);
        var line = Assert.Single(Gen(r), i => i.Key == "game-heat");
        Assert.Equal("Rematch runs your GPU about 8° hotter than your other games (74° vs 66°).", line.Text);
        Assert.Equal((InsightTone.Warn, "Rematch 74°, Dota 2 66° and Hades 66°"), (line.Tone, line.Detail));

        a.GpuTempAvg = 70; // 4° isn't a difference worth a line
        None(Gen(r), "game-heat");
        a.GpuTempAvg = 74;
        b.ActiveSec = 5 * 60; // too brief to count
        Assert.Equal(InsightTone.Warn, Assert.Single(Gen(r), i => i.Key == "game-heat").Tone); // Hades alone is the others
        c.ActiveSec = 5 * 60;
        None(Gen(r), "game-heat"); // one game: nothing to compare with
    }

    // ── Cooling, over months ──

    private static ThenTemps Then() => new(new LoadTemps(40, 42, 500), new LoadTemps(75, 70, 400), Wed.AddDays(-120), Wed.AddDays(-30), 30);

    [Fact]
    public void Idle_temperatures_are_compared_with_months_ago_and_dust_is_named()
    {
        var r = Past();
        r.IdleTemps = new LoadTemps(43, 48, 60);
        var lines = Gen(r, context: new InsightContext(Then: Then())).Where(i => i.Key == "drift").ToList();
        Assert.Equal(["At idle, your GPU runs 6° hotter than it did in December 2024 (48° vs 42°). Dust building up is the usual cause.",
            "At idle, your CPU runs 3° hotter than it did in December 2024 (43° vs 40°). Dust building up is the usual cause."], lines.Select(i => i.Text));
        Assert.Equal([72, 58], lines.Select(i => i.Priority));
        Assert.All(lines, i => Assert.Equal(InsightTone.Warn, i.Tone));
        Assert.Equal("48° over 60 minutes at idle now, 42° over 30 days in December 2024", lines[0].Detail);
    }

    [Fact]
    public void The_week_before_isnt_compared_where_months_ago_already_was()
    {
        var r = Past();
        r.IdleTemps = new LoadTemps(43, 48, 60);
        var usual = Usual(7, 7 * 3 * 3600);
        usual.IdleTemps = new LoadTemps(43, 41, 200); // the GPU 7° over last week too: said once, the longer view
        var list = Gen(r, usual: usual, context: new InsightContext(Then: Then()));
        Assert.Equal(2, list.Count(i => i.Key == "drift"));
        None(list, "temp-compare");
        Assert.Contains("temp-compare", Gen(r, usual: usual).Select(i => i.Key)); // without months ago, the week is compared
    }

    [Fact]
    public void Running_cooler_than_months_ago_is_good_news_and_needs_enough_minutes()
    {
        var r = Past();
        r.GpuLoadTemps = new LoadTemps(70, 65, 30);
        var line = Assert.Single(Gen(r, context: new InsightContext(Then: Then())), i => i.Key == "drift");
        Assert.Equal("Under heavy load, your GPU runs 5° cooler than it did in December 2024 (65° vs 70°).", line.Text);
        Assert.Equal((InsightTone.Good, 40), (line.Tone, line.Priority));

        r.GpuLoadTemps = new LoadTemps(70, 65, 10); // ten minutes under load say nothing
        None(Gen(r, context: new InsightContext(Then: Then())), "drift");
        r.IdleTemps = new LoadTemps(41, 44, 60); // a degree or two is within the noise
        None(Gen(r, context: new InsightContext(Then: Then())), "drift");
    }

    [Fact]
    public void A_fan_faster_at_the_same_temperature_means_a_clogging_cooler()
    {
        var r = Past();
        r.Fans = [new FanStat("GPU Fan", "NVIDIA GeForce RTX 3080 Ti", Gpu: true, 800, 60, 1650, 40)];
        var usual = Usual(7, 7 * 3 * 3600);
        usual.Fans = [new FanStat("GPU Fan", "NVIDIA GeForce RTX 3080 Ti", Gpu: true, 800, 100, 1350, 60)];
        var line = Assert.Single(Gen(r, usual: usual), i => i.Key == "fan");
        Assert.Equal("Your GPU fan runs about 300 rpm faster than usual at the same temperature (1,650 vs 1,350 rpm). That's what a clogging cooler looks like.", line.Text);
        Assert.Equal((InsightTone.Warn, 68), (line.Tone, line.Priority));
        Assert.Equal("40 minutes with the GPU at 60–75° now, 60 over the previous 7 days", line.Detail);

        r.Fans = [new FanStat("GPU Fan", "NVIDIA GeForce RTX 3080 Ti", Gpu: true, 800, 60, 1450, 40)]; // 100 rpm, 7%: not yet
        None(Gen(r, usual: usual), "fan");
        r.Fans = [new FanStat("GPU Fan", "NVIDIA GeForce RTX 3080 Ti", Gpu: true, 800, 60, 1650, 15)]; // too few warm minutes
        None(Gen(r, usual: usual), "fan");
    }

    [Fact]
    public void A_fan_faster_at_idle_is_noted_by_its_name_and_board()
    {
        var r = Past();
        r.Fans = [new FanStat("Fan #2", "Gigabyte B650", Gpu: false, 920, 60, null, 0)];
        var usual = Usual(7, 7 * 3 * 3600);
        usual.Fans = [new FanStat("Fan #2", "Gigabyte B650", Gpu: false, 780, 200, null, 0)];
        var line = Assert.Single(Gen(r, usual: usual), i => i.Key == "fan");
        Assert.Equal("Fan #2 on your Gigabyte B650 runs about 18% faster at idle than usual (920 vs 780 rpm).", line.Text);
        Assert.Equal(InsightTone.Neutral, line.Tone);
        usual.Fans = [new FanStat("Fan #1", "Gigabyte B650", Gpu: false, 780, 200, null, 0)]; // another fan: no comparison
        None(Gen(r, usual: usual), "fan");
    }

    [Fact]
    public void A_chip_slowing_itself_down_is_the_top_warning()
    {
        var r = Past();
        r.GpuThrottle = new Throttling(14, 15.2, 84);
        r.CpuThrottle = new Throttling(3, 9.6, 86);
        var lines = Gen(r).Where(i => i.Key == "throttle").ToList();
        Assert.Equal(["Your GPU slowed itself for 14 minutes to stay cool: clocks fell about 15% once it passed 84°.",
            "Your CPU slowed itself for 3 minutes to stay cool: clocks fell about 10% once it passed 86°."], lines.Select(i => i.Text));
        Assert.All(lines, i => Assert.Equal(InsightTone.Hot, i.Tone));
        Assert.Equal(lines[0], Gen(r)[1]); // right after the screen-time line, which heads every list
    }

    [Fact]
    public void Cooling_down_slower_than_usual_says_so_and_quicker_is_good_news()
    {
        var r = Past();
        r.CooldownMinutes = 9;
        r.CooldownCount = 2;
        var usual = Usual(7, 7 * 3 * 3600);
        usual.CooldownMinutes = 5;
        usual.CooldownCount = 3;
        var line = Assert.Single(Gen(r, usual: usual), i => i.Key == "cooldown");
        Assert.Equal("After heavy load, your GPU took 9 minutes to cool below 50°, against 5 minutes usually. Poor airflow keeps heat in the case.", line.Text);
        Assert.Equal((InsightTone.Warn, "Averaged over 2 times heavy load ended"), (line.Tone, line.Detail));

        r.CooldownMinutes = 2;
        line = Assert.Single(Gen(r, usual: usual), i => i.Key == "cooldown");
        Assert.Equal("After heavy load, your GPU took 2 minutes to cool below 50°, quicker than the 5 minutes usual.", line.Text);
        Assert.Equal(InsightTone.Good, line.Tone);

        r.CooldownMinutes = 6; // about the same
        None(Gen(r, usual: usual), "cooldown");
        r.CooldownMinutes = 25; // nothing to compare with, but long
        line = Assert.Single(Gen(r), i => i.Key == "cooldown");
        Assert.Equal("After heavy load, your GPU took 25 minutes to cool below 50°. Poor airflow keeps heat in the case.", line.Text);
        r.CooldownMinutes = 12;
        None(Gen(r), "cooldown");
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
        Assert.Equal([InsightTone.Good, InsightTone.Warn, InsightTone.Warn, InsightTone.Neutral], lines.Select(i => i.Tone));
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
        var line = Line(Gen(r), "crash");
        Assert.EndsWith(" It came minutes after the GPU passed 86°.", line);
        Assert.Equal("12 Mar 3:04 PM: CPU 70°, GPU 86° just before", Assert.Single(Gen(r), i => i.Key == "crash").Detail);
        r.CrashContexts[7] = new CrashContext(90, 60, null, null);
        Assert.EndsWith(" It came minutes after the CPU passed 90°.", Line(Gen(r), "crash"));
        r.CrashContexts[7] = new CrashContext(60, 60, null, null);
        Assert.EndsWith(".", Line(Gen(r), "crash"));
        Assert.DoesNotContain("minutes after", Line(Gen(r), "crash"));
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
            "top-app", "away", "idle-app", "memory"], list.Select(i => i.Key));
        Assert.Equal(list.OrderByDescending(i => i.Priority).Select(i => i.Priority), list.Select(i => i.Priority));
        Assert.All(list, i => Assert.False(string.IsNullOrEmpty(i.Icon)));
    }
}
