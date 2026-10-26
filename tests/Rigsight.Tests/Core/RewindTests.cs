using Rigsight.Core.Data;
using Rigsight.Core.Rewind;
using Rigsight.Tests.Data;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Core;

/// <summary>Rewind: which periods are made and when, and the cards a period's facts make.</summary>
[Collection("UI")] // writes rewinds into the shared test data folder
public class RewindTests
{
    // The user's October 1-18 figures, from the mock-up the user approved.
    private static RewindFacts October() => new()
    {
        Id = "2026-10-month", Kind = RewindKind.Month, From = new(2026, 10, 1), To = new(2026, 11, 1), Made = new(2026, 11, 1, 9, 0, 0),
        Time = true, Games = true, Internet = true,
        OnSec = 211 * 3600 + 38 * 60, ActiveSec = 188 * 3600 + 25 * 60, DaysOn = 17, Days = 31,
        BusiestDay = new(2026, 10, 10), BusiestDaySec = 14 * 3600 + 55 * 60,
        GamingSec = 62.5 * 3600, GameCount = 17, Plays = 111,
        TopGames =
        [
            new("Dota 2", "dota2.exe", null, 17.8 * 3600, 30), new("REMATCH", "rematch.exe", null, 10.1 * 3600, 34),
            new("No Man's Sky", "nms.exe", null, 9.3 * 3600, 7),
        ],
        LongestGame = new("No Man's Sky", "nms.exe", null, 2 * 3600 + 40 * 60), LongestStart = new(2026, 10, 7, 17, 47, 0),
        NightShare = 0.3, Down = 502L * 1024 * 1024 * 1024, Up = 21L * 1024 * 1024 * 1024,
        TopInternet = [new("Steam", "steam.exe", null, 266L * 1024 * 1024 * 1024)],
        AppCrashes = 12, ShutOffs = 10,
    };

    // ── Periods ──

    [Theory]
    [InlineData(RewindKind.Month, "2026-10-19", "2026-10-01", "2026-11-01")]
    [InlineData(RewindKind.Quarter, "2026-10-19", "2026-10-01", "2027-01-01")]
    [InlineData(RewindKind.Quarter, "2026-03-31", "2026-01-01", "2026-04-01")]
    [InlineData(RewindKind.Half, "2026-10-19", "2026-07-01", "2027-01-01")]
    [InlineData(RewindKind.Year, "2026-10-19", "2026-01-01", "2027-01-01")]
    public void A_period_is_its_calendar_month_quarter_half_or_year(RewindKind kind, string day, string from, string to)
    {
        var (f, t) = RewindStore.PeriodOf(kind, DateTime.Parse(day));
        Assert.Equal((DateTime.Parse(from), DateTime.Parse(to)), (f, t));
    }

    [Theory]
    [InlineData(0, true, true, true, true)]     // kept for ever
    [InlineData(90, true, true, false, false)]  // 3 months: month and 3-month rewinds only (the user's ruling)
    [InlineData(365, true, true, true, true)]
    [InlineData(730, true, true, true, true)]
    public void History_kept_decides_which_kinds_can_be_made(int keep, bool month, bool quarter, bool half, bool year)
    {
        Assert.Equal(month, RewindStore.HistoryAllows(RewindKind.Month, keep));
        Assert.Equal(quarter, RewindStore.HistoryAllows(RewindKind.Quarter, keep));
        Assert.Equal(half, RewindStore.HistoryAllows(RewindKind.Half, keep));
        Assert.Equal(year, RewindStore.HistoryAllows(RewindKind.Year, keep));
    }

    [Fact]
    public void On_the_first_of_July_the_half_year_quarter_and_month_are_due_biggest_first()
    {
        // (2027: nothing ending before 1 November 2026 is ever made.)
        using var t = new TestDb();
        t.OpenWriter();
        // Used every day of the first half of the year.
        for (var d = new DateTime(2027, 1, 1); d < new DateTime(2027, 7, 1); d = d.AddDays(1))
            t.Exec("INSERT INTO system_day(day, minutes, active_sec, idle_sec, cpu_temp_sum, cpu_temp_n, gpu_temp_sum, gpu_temp_n, cpu_load_sum, cpu_load_n, gpu_load_sum, gpu_load_n) " +
                   "VALUES($d, 60, 3600, 0, 0, 0, 0, 0, 0, 0, 0, 0)", ("$d", U(d)));
        var settings = Settings();
        settings.Tracking.KeepHistoryDays = 0;
        foreach (var f in RewindStore.All().Where(f => f.From.Year == 2027 && f.From.Month <= 6)) RewindStore.Remove(f.Id);
        var due = RewindStore.Due(t.Db, settings, new DateTime(2027, 7, 1, 9, 0, 0));
        Assert.Equal([RewindKind.Half, RewindKind.Quarter, RewindKind.Month], due.Select(d => d.Kind));
        Assert.Equal((new DateTime(2027, 1, 1), new DateTime(2027, 7, 1)), (due[0].From, due[0].To));
        Assert.Equal((new DateTime(2027, 4, 1), new DateTime(2027, 7, 1)), (due[1].From, due[1].To));
        Assert.Equal((new DateTime(2027, 6, 1), new DateTime(2027, 7, 1)), (due[2].From, due[2].To));

        // Switched off, or history kept for 3 months: not made.
        settings.Rewind.Half = false;
        settings.Tracking.KeepHistoryDays = 90;
        Assert.Equal([RewindKind.Quarter, RewindKind.Month], RewindStore.Due(t.Db, settings, new DateTime(2027, 7, 1, 9, 0, 0)).Select(d => d.Kind));
    }

    [Fact]
    public void A_thin_month_has_no_rewind_of_its_own()
    {
        using var t = new TestDb();
        t.OpenWriter();
        // Three days of use in June.
        foreach (int day in new[] { 3, 9, 20 })
            t.Exec("INSERT INTO system_day(day, minutes, active_sec, idle_sec, cpu_temp_sum, cpu_temp_n, gpu_temp_sum, gpu_temp_n, cpu_load_sum, cpu_load_n, gpu_load_sum, gpu_load_n) " +
                   "VALUES($d, 60, 3600, 0, 0, 0, 0, 0, 0, 0, 0, 0)", ("$d", U(2027, 6, day)));
        var settings = Settings();
        settings.Rewind.Quarter = settings.Rewind.Half = settings.Rewind.Year = false;
        Assert.Empty(RewindStore.Due(t.Db, settings, new DateTime(2027, 7, 1, 9, 0, 0)));
    }

    [Fact]
    public void Nothing_before_October_2026_is_made_the_first_is_October_seen_on_1_November()
    {
        using var t = new TestDb();
        t.OpenWriter();
        for (var d = new DateTime(2026, 9, 1); d < new DateTime(2026, 11, 1); d = d.AddDays(1))
            t.Exec("INSERT INTO system_day(day, minutes, active_sec, idle_sec, cpu_temp_sum, cpu_temp_n, gpu_temp_sum, gpu_temp_n, cpu_load_sum, cpu_load_n, gpu_load_sum, gpu_load_n) " +
                   "VALUES($d, 60, 3600, 0, 0, 0, 0, 0, 0, 0, 0, 0)", ("$d", U(d)));
        var settings = Settings();
        RewindStore.Remove("2026-10-month");
        Assert.Empty(RewindStore.Due(t.Db, settings, new DateTime(2026, 10, 19, 15, 0, 0))); // no September on update day
        var due = RewindStore.Due(t.Db, settings, new DateTime(2026, 11, 1, 9, 0, 0));
        Assert.Equal((RewindKind.Month, new DateTime(2026, 10, 1)), (due.Single().Kind, due.Single().From));
    }

    // ── Cards ──

    [Fact]
    public void The_default_topics_give_eight_cards()
    {
        var cards = RewindCards.Of(October());
        Assert.Equal(["cover", "time", "games", "top", "longest", "internet", "type", "summary"], cards.Select(c => c.Id));
    }

    [Fact]
    public void Each_label_and_figure_read_as_one_sentence_and_the_line_under_never_repeats_the_figure()
    {
        var cards = RewindCards.Of(October());
        var time = cards.Single(c => c.Id == "time");
        Assert.Equal(("Your PC was on for", "212", "hours"), (time.Label, time.Big, time.Unit));
        Assert.Equal("On 17 of 31 days. Your biggest day was Saturday 10 October, with 15 hours on.", time.Sub);
        var games = cards.Single(c => c.Id == "games");
        Assert.Equal(("Time in games", "62h", "30m"), (games.Label, games.Big, games.Unit));
        foreach (var c in cards.Where(c => c.Big.Length > 0 && c.Sub is not null))
            Assert.DoesNotContain(c.Big, c.Sub!);
        Assert.Equal("212 hours on your PC. 62 of them in 17 games.", cards[0].Sub);
    }

    [Fact]
    public void Topics_switched_off_leave_their_cards_out_and_thin_cards_are_dropped_not_shown_as_zero()
    {
        var f = October();
        f.Games = false;
        f.Heat = true; // no readings: no card
        f.Problems = true;
        var ids = RewindCards.Of(f).Select(c => c.Id).ToList();
        Assert.DoesNotContain("games", ids);
        Assert.DoesNotContain("top", ids);
        Assert.DoesNotContain("type", ids);
        Assert.DoesNotContain("heat", ids);
        Assert.Contains("problems", ids);

        f.Internet = false;
        f.Time = false;
        // Nothing left to sum up: no summary card with one figure.
        Assert.DoesNotContain("summary", RewindCards.Of(f).Select(c => c.Id));
    }

    [Fact]
    public void A_left_out_game_never_appears_but_the_total_keeps_its_time()
    {
        // Left out at making: the facts hold only what may be shown; the game total is the report's.
        var f = October();
        f.TopGames.RemoveAt(0);
        var top = RewindCards.Of(f).Single(c => c.Id == "top");
        Assert.Equal("REMATCH", top.Big);
        Assert.DoesNotContain(top.Rows, r => r.Name == "Dota 2");
        Assert.Equal(("62h", "30m"), (RewindCards.Of(f).Single(c => c.Id == "games").Big, RewindCards.Of(f).Single(c => c.Id == "games").Unit));
    }

    // ── Not the happy path ──

    private static RewindFacts WithApps(RewindFacts f)
    {
        f.TopApps = [new("Google Chrome", "chrome.exe", null, 64 * 3600), new("Visual Studio Code", "code.exe", null, 41 * 3600)];
        f.UseHours = [0, 0, 0, 0, 0, 0, 0, .4, 3.2, 9.8, 10.4, 8.6, 4.1, 6.2, 7.4, 6.8, 5.9, 4.4, 2.1, 1.6, 1.2, .6, .2, 0];
        return f;
    }

    [Fact]
    public void A_PC_that_never_plays_games_gets_its_apps_its_hours_and_what_kind_of_user()
    {
        var f = WithApps(October());
        f.GamingSec = 0; f.GameCount = 0; f.Plays = 0; f.TopGames = []; f.LongestGame = null;
        var ids = RewindCards.Of(f).Select(c => c.Id).ToList();
        Assert.Contains("apps", ids);
        Assert.Contains("hours", ids);
        Assert.DoesNotContain("games", ids);
        Assert.DoesNotContain("top", ids);
        var type = RewindCards.Of(f).Single(c => c.Id == "type");
        Assert.Equal(("What kind of PC user you were", "Early bird"), (type.Label, type.Big));
        Assert.Equal("Google Chrome", RewindCards.Of(f).Single(c => c.Id == "summary").Rows[0].Name);
    }

    [Fact]
    public void A_little_play_keeps_the_games_cards_but_the_kind_comes_from_the_whole_PC()
    {
        var f = WithApps(October());
        f.TopGames = [.. f.TopGames.Take(2)]; f.GameCount = 2; f.Plays = 5; f.GamingSec = 3.7 * 3600;
        var cards = RewindCards.Of(f);
        Assert.Contains(cards, c => c.Id == "games");
        Assert.Contains(cards, c => c.Id == "hours");
        Assert.Equal("What kind of PC user you were", cards.Single(c => c.Id == "type").Label);
    }

    [Fact]
    public void Nothing_going_wrong_is_a_card_too()
    {
        var f = October();
        f.Problems = true; f.AppCrashes = 0; f.ShutOffs = 0;
        var card = RewindCards.Of(f).Single(c => c.Id == "problems");
        Assert.Equal(("What went wrong", "Nothing"), (card.Label, card.Big));
        Assert.Equal("Not one app or game crashed, and your PC never shut down without warning.", card.Sub);
    }

    [Fact]
    public void A_cool_month_says_it_stayed_cool()
    {
        var f = October();
        f.Heat = true;
        f.GpuHot = new RewindHeat(61, new DateTime(2026, 10, 7, 20, 0, 0), "Dota 2", true);
        Assert.EndsWith("It stayed cool all along.", RewindCards.Of(f).Single(c => c.Id == "heat").Sub);
    }

    [Theory]
    [InlineData(0.7, 0.1, 60, 17, "One-game player")]
    [InlineData(0.3, 0.6, 60, 17, "Night player")]
    [InlineData(0.3, 0.1, 120, 3, "Marathon player")]
    [InlineData(0.3, 0.1, 60, 17, "Variety player")]
    [InlineData(0.3, 0.1, 60, 4, "Steady player")]
    public void The_kind_of_player_comes_from_the_period_alone(double topShare, double night, double minutesPerPlay, int games, string expected)
    {
        var f = October();
        f.Plays = 40;
        f.GamingSec = f.Plays * minutesPerPlay * 60;
        f.TopGames[0] = f.TopGames[0] with { Value = f.GamingSec * topShare };
        f.NightShare = night;
        f.GameCount = games;
        Assert.Equal(expected, RewindCards.Player(f)!.Value.Name);
    }

    [Fact]
    public void Titles_name_the_period_plainly()
    {
        var f = October();
        Assert.Equal(("October", "October 2026", "Your October rewind is ready"), (RewindCards.Title(f), RewindCards.TitleWithYear(f), RewindCards.Ready(f)));
        f.Kind = RewindKind.Quarter;
        (f.From, f.To) = (new DateTime(2027, 1, 1), new DateTime(2027, 4, 1));
        Assert.Equal(("January to March", "Jan – Mar"), (RewindCards.Title(f), RewindCards.CoverWord(f)));
        f.Kind = RewindKind.Year;
        (f.From, f.To) = (new DateTime(2026, 1, 1), new DateTime(2027, 1, 1));
        Assert.Equal("Your 2026 rewind is ready", RewindCards.Ready(f));
        Assert.Equal("You played games for 62 hours, Dota 2 the most. Click to watch it.", RewindCards.Teaser(October()));
    }
}
