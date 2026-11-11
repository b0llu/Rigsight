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
}
