using System.Globalization;
using System.Text.RegularExpressions;
using Rigsight.Core.Ai;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Tests.Data;
using Rigsight.Tests.Support;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.App;

/// <summary>
/// Two look-ups never give two numbers for one thing without saying why (the answers checked on the real PC, 26 Oct
/// 2026): Rewind's crash totals against get_crashes', where RigSight's records begin against where Windows' crash
/// records do, a game's plays in list_games against get_game, a game's time in a week against the report's, and which
/// days a report's usual covers.
/// </summary>
[Collection("Disks")]
public sealed class AiCountsAgreeTests
{
    private static string Range(DateTime from, DateTime to) =>
        $$"""{"from":"{{from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}}","to":"{{to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}}"}""";

    private static string Said(DateTime d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>A game's own settings looked for in empty folders, never this PC's.</summary>
    private static AiTools Tools(string db)
    {
        string root = TestEnvironment.NewFolder("ai-counts-places");
        string Dir(string name) { var d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }
        return new AiTools(Settings(), db)
        {
            SettingsPlaces = new GameSettings.Places
            {
                LocalAppData = Dir("Local"), RoamingAppData = Dir("Roaming"), LocalLow = Dir("LocalLow"), Documents = Dir("Documents"),
                SavedGames = Dir("Saved Games"), Steam = Dir("Steam"),
            },
        };
    }

    /// <summary>"App crashes: 72 in all, …" in get_crashes' totals, as a number (0 when the kind isn't there).</summary>
    private static int InAll(string crashes, string kind) =>
        Regex.Match(crashes, $@"^{kind}: (\d+) in all", RegexOptions.Multiline) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

    [Fact]
    public void Rewinds_crash_totals_say_which_kinds_they_join_and_add_up_from_get_crashes()
    {
        // Real PC, 26 Oct 2026: the year's Rewind said "App crashes 79, sudden shut-offs 55" and get_crashes 72 app crashes
        // and 58 sudden shut-offs, told as two counts of one thing. Two weeks of history up to mid-June, with a freeze, a
        // shutdown that didn't finish and a blue screen besides.
        string db = Path.Combine(TestEnvironment.NewFolder("ai-rewind-crashes"), "rigsight.db");
        SeedData.Generate(db, SeedProfile.Small, new DateTime(2026, 6, 15, 22, 0, 0));
        var day = new DateTime(2026, 6, 10, 21, 0, 0);
        using (var w = RigsightDb.OpenWriter(db))
            w.InsertCrashes([
                new CrashEvent { Ts = U(day), Kind = CrashKind.AppHang, AppExe = "dota2.exe" },
                new CrashEvent { Ts = U(day.AddMinutes(20)), Kind = CrashKind.UnexpectedShutdown, Moment = PowerMoment.ShuttingDown },
                new CrashEvent { Ts = U(day.AddMinutes(40)), Kind = CrashKind.SystemCrash, Code = "0x133" },
            ]);
        int unfinished;
        using (var r = RigsightDb.OpenReader(db)!)
            unfinished = r.GetCrashes(U(new DateTime(2026, 1, 1)), U(DateTime.Today.AddDays(1))).Count(c => c is { Kind: CrashKind.UnexpectedShutdown, Moment: PowerMoment.ShuttingDown });

        var tools = Tools(db);
        string rewind = tools.Run("get_rewind", """{"period":"year","when":"2026"}""");
        string crashes = tools.Run("get_crashes", Range(new DateTime(2026, 1, 1), DateTime.Today));

        Assert.True(unfinished > 0 && InAll(crashes, "App freezes") > 0 && InAll(crashes, "Blue screens") > 0, crashes);
        Assert.Contains($"App crashes and freezes together: {InAll(crashes, "App crashes") + InAll(crashes, "App freezes")}.", rewind);
        Assert.Contains($"Blue screens and sudden shut-offs together: {InAll(crashes, "Blue screens") + InAll(crashes, "Sudden shut-offs") - unfinished} (", rewind);
        Assert.Contains("get_crashes counts each kind apart", rewind);
        Assert.DoesNotContain("  App crashes:", rewind);
        // The year began long before these two weeks of records: said, with the crashes' own source.
        Assert.Contains("RECORDS: RigSight's own records (time on, apps, games, temperatures, internet) begin", rewind);
    }

    [Fact]
    public void Crashes_from_before_recording_began_say_whose_records_begin_when()
    {
        // Real PC, 26 Oct 2026: "the history starts on 23 Sep" beside crashes back to 26 Jun, both right (Windows' logs
        // reach further back than RigSight's minutes) and said as a mistake.
        using var t = new TestDb("ai-records");
        var began = DateTime.Today.AddDays(-2).AddHours(12);
        var old = DateTime.Today.AddDays(-60).AddHours(12);
        t.Db.WriteMinute(Minute(U(began)));
        t.Db.InsertCrashes([
            new CrashEvent { Ts = U(old), Kind = CrashKind.AppCrash, AppExe = "game.exe", Code = "c0000005" },
            new CrashEvent { Ts = U(DateTime.Today.AddDays(-1).AddHours(12)), Kind = CrashKind.AppCrash, AppExe = "game.exe", Code = "c0000005" },
        ]);
        var tools = Tools(t.Path);

        string back = tools.Run("get_crashes", Range(DateTime.Today.AddDays(-90), DateTime.Today));
        Assert.StartsWith("RECORDS: RigSight's own records", back);
        Assert.Contains($"begin {Said(began)}", back);
        Assert.Contains($"Windows' own logs, which on this PC go back to {Said(old)}", back);
        // Days the records cover: nothing to explain.
        string recent = tools.Run("get_crashes", Range(DateTime.Today.AddDays(-1), DateTime.Today));
        Assert.Contains("App crashes: 1 in all", recent);
        Assert.DoesNotContain("RECORDS:", recent);
    }

    [Fact]
    public void List_games_and_get_game_count_the_same_plays()
    {
        // Real PC, 26 Oct 2026: list_games said Dota 2 had 48 "sessions", get_game 32 plays. A match, five minutes in the
        // menu, another match; a third an hour later; then a launch of 30 seconds: four sessions, three plays.
        using var t = new TestDb("ai-plays");
        long game = t.Db.UpsertApp("kickoff.exe", "Kickoff", @"C:\RigsightTestGames\Kickoff\kickoff.exe", AppCategory.Game);
        long a = U(2026, 10, 17, 20);
        foreach (var (from, to) in new[] { (a, a + 600), (a + 900, a + 1500), (a + 3600, a + 4200), (a + 7200, a + 7230) })
            t.Db.InsertSession(Session(game, from, to, game: true));
        var tools = Tools(t.Path);

        Assert.Contains("Kickoff: 30m over 3 plays,", tools.Run("list_games", "{}"));
        Assert.StartsWith("Kickoff: 30m in 3 plays.", tools.Run("get_game", """{"name":"Kickoff"}"""));
    }

    [Fact]
    public void A_games_time_in_a_period_is_only_the_time_inside_it()
    {
        // Real PC, 18 to 24 Oct 2026: REMATCH had 1h 56m in list_games where the report said 1h 33m, because a play from
        // 11:34 PM the night before brought its whole time in. Here 25 minutes of a play are on the 17th, 34 on the 18th.
        using var t = new TestDb("ai-played-in");
        long game = t.Db.UpsertApp("kickoff.exe", "Kickoff", @"C:\RigsightTestGames\Kickoff\kickoff.exe", AppCategory.Game);
        long start = U(2026, 10, 17, 23, 35);
        t.Db.InsertSession(Session(game, start, start + 59 * 60, game: true));
        t.Db.AddAppHour(Hour(U(2026, 10, 17, 23), game, fg: 25 * 60));
        t.Db.AddAppHour(Hour(U(2026, 10, 18, 0), game, fg: 34 * 60));
        var tools = Tools(t.Path);

        Assert.Contains("Kickoff: 34m in 1 play", tools.Run("list_games", Range(new DateTime(2026, 10, 18), new DateTime(2026, 10, 24))));
        Assert.Contains("Kickoff: 25m in 1 play", tools.Run("list_games", Range(new DateTime(2026, 10, 17), new DateTime(2026, 10, 17))));
        Assert.Contains("Kickoff: 59m in 1 play", tools.Run("list_games", Range(new DateTime(2026, 10, 17), new DateTime(2026, 10, 18))));
    }
}
