using System.Globalization;
using Rigsight.Core.Ai;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Tests.Data;
using Rigsight.Tests.Support;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.App;

/// <summary>
/// get_game answers a before-and-after question (2026-11-16): asked why Phantom Blade Zero "ran 6° hotter on the CPU in
/// the 9 days after the changes on 3 Nov", it listed only the newest plays, and the model said the plays before were
/// missing and the line was wrong. Older plays are now summed in a line, from and to list a period's plays in full, and
/// a finding about the game comes with the figures it was worked out from.
/// </summary>
[Collection("Disks")]
public sealed class AiGamePlaysTests
{
    /// <summary>The driver came in on this day; three plays before it, fifteen after, at 60 °C on the CPU then 68 °C.</summary>
    private static readonly DateTime Changed = DateTime.Today.AddDays(-20);

    private static string Said(DateTime d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A game's own settings looked for in empty folders, never this PC's.</summary>
    private static AiTools Tools(string db)
    {
        string root = TestEnvironment.NewFolder("ai-game-places");
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

    /// <summary>
    /// 18 plays of Kickoff, one an evening: 40 minutes at full graphics load, then 40 at rest. The driver on
    /// <see cref="Changed"/>; the CPU 60 °C before it and 68 °C after, the GPU the same throughout.
    /// </summary>
    private static TestDb History()
    {
        var t = new TestDb("ai-game-plays");
        long game = t.Db.UpsertApp("kickoff.exe", "Kickoff", @"C:\RigsightTestGames\Kickoff\kickoff.exe", AppCategory.Game);
        var days = new[] { -5, -4, -3 }.Concat(Enumerable.Range(1, 15)).Select(d => Changed.AddDays(d).AddHours(20));
        foreach (var start in days)
        {
            long s = U(start);
            double cpu = start < Changed ? 60 : 68;
            t.Db.InsertSession(Session(game, s, s + 40 * 60, game: true));
            for (int m = 0; m < 40; m++)
                t.Db.WriteMinute(Minute(s + m * 60, cpu: cpu, gpu: 70, cpuLoad: 50, gpuLoad: 97, app: game, gpuApp: game, gpuPower: 200));
            for (int m = 40; m < 80; m++)
                t.Db.WriteMinute(Minute(s + m * 60, cpu: 40, gpu: 40, cpuLoad: 2, gpuLoad: 2));
        }
        t.Db.InsertChanges([new SystemChange(Changed.AddHours(12), ChangeKind.Driver, "NVIDIA graphics driver 616.92") { Subject = "gpudriver:x", Was = "610.47", Now = "616.92" }]);
        return t;
    }

    private static int PlayLines(string said) => said.Split('\n').Count(l => l.StartsWith("  ", StringComparison.Ordinal));

    [Fact]
    public void Without_dates_the_older_plays_are_summed_never_left_out()
    {
        using var t = History();
        string said = Tools(t.Path).Run("get_game", """{"name":"Kickoff"}""");
        Assert.StartsWith("Kickoff: 12h 00m in 18 plays.", said);
        Assert.Contains($"the newest {15} of 18 one by one, the 3 older ones summed after them", said);
        Assert.Equal(15, PlayLines(said));
        // The three before the driver, together: their dates, and their temperatures, which differ from the newer plays'.
        Assert.Contains($"The 3 older plays, not listed one by one, summed together ({Said(Changed.AddDays(-5))} to {Said(Changed.AddDays(-3))}, 2h 00m in front): ", said);
        string older = said.Split('\n').Single(l => l.StartsWith("The 3 older plays", StringComparison.Ordinal));
        Assert.Contains("CPU temp average 60 °C", older);
        Assert.Contains("GPU temp average 70 °C", older);
        Assert.Contains("ask get_game again with from and to", older);
    }

    [Fact]
    public void A_game_with_few_plays_lists_every_one_and_sums_nothing()
    {
        using var t = new TestDb("ai-game-few");
        long game = t.Db.UpsertApp("kickoff.exe", "Kickoff", @"C:\RigsightTestGames\Kickoff\kickoff.exe", AppCategory.Game);
        long a = U(DateTime.Today.AddDays(-2).AddHours(20));
        t.Db.InsertSession(Session(game, a, a + 600, game: true));
        string said = Tools(t.Path).Run("get_game", """{"name":"Kickoff"}""");
        Assert.Contains("Plays, newest first, every one:", said);
        Assert.DoesNotContain("older plays, not listed", said);
    }

    [Fact]
    public void With_from_and_to_every_play_in_the_period_is_listed_with_them_together()
    {
        using var t = History();
        var tools = Tools(t.Path);
        string before = tools.Run("get_game", $$"""{"name":"Kickoff","from":"{{Day(Changed.AddDays(-14))}}","to":"{{Day(Changed.AddDays(-1))}}"}""");
        Assert.Contains($"Every play from {Said(Changed.AddDays(-14))} to {Said(Changed.AddDays(-1))}, newest first (3 of its 18)", before);
        Assert.Equal(3, PlayLines(before));
        Assert.Contains("These 3 plays together: GPU temp average 70 °C", before);
        Assert.Contains("CPU temp average 60 °C", before.Split('\n').Single(l => l.StartsWith("These 3", StringComparison.Ordinal)));
        Assert.DoesNotContain("older plays, not listed", before);

        // Every one in a longer period too, past the 15 listed without dates.
        string all = tools.Run("get_game", $$"""{"name":"Kickoff","from":"{{Day(Changed.AddDays(-30))}}","to":"{{Day(DateTime.Today)}}"}""");
        Assert.Equal(18, PlayLines(all));
        // One date alone is that day; a day with no plays says where its plays are.
        Assert.Equal(1, PlayLines(tools.Run("get_game", $$"""{"name":"Kickoff","from":"{{Day(Changed.AddDays(1))}}"}""")));
        Assert.StartsWith($"No plays on {Said(Changed)}.", tools.Run("get_game", $$"""{"name":"Kickoff","from":"{{Day(Changed)}}","to":"{{Day(Changed)}}"}""").Split('\n').First(l => l.StartsWith("No plays", StringComparison.Ordinal)));
        Assert.StartsWith("Error: 'to' must be a date", tools.Run("get_game", """{"name":"Kickoff","from":"2026-11-01","to":"soon"}"""));
    }

    [Fact]
    public void A_finding_about_the_game_comes_with_the_figures_behind_it()
    {
        using var t = History();
        string said = Tools(t.Path).Run("get_game", """{"name":"Kickoff"}""");
        string finding = said.Split('\n').SingleOrDefault(l => l.StartsWith("RigSight's finding", StringComparison.Ordinal)) ?? "";
        Assert.True(finding.Length > 0, said);
        Assert.Contains("Kickoff ran 8° hotter on the CPU in the 14 days after driver 616.92", finding);
        Assert.Contains($"about NVIDIA graphics driver 616.92 on {Said(Changed)}", finding);
        // The two sides it compared, with their days, and each figure before and after.
        string Short(DateTime d) => d.ToString("d MMM", CultureInfo.InvariantCulture);
        Assert.Contains($"14 days before, {Short(Changed.AddDays(-14))} to {Short(Changed.AddDays(-1))}", finding);
        Assert.Contains($"14 days after, {Short(Changed.AddDays(1))} to {Short(Changed.AddDays(14))}", finding);
        Assert.Contains("CPU in Kickoff 60° before, 68° after", finding);
        Assert.Contains("GPU in Kickoff 70° before, 70° after", finding);
        Assert.Contains("ask get_game with from and to", finding);
    }

    [Fact]
    public void Without_temperatures_shared_no_finding_is_given()
    {
        using var t = History();
        var settings = Settings();
        settings.Ai.SetSends(AiPages.Temperatures, false);
        string said = new AiTools(settings, t.Path).Run("get_game", """{"name":"Kickoff"}""");
        Assert.DoesNotContain("RigSight's finding", said);
    }

    [Fact]
    public void The_look_up_offers_from_and_to_without_needing_them()
    {
        var def = new AiTools(Settings(), Path.Combine(Path.GetTempPath(), "rigsight-no-such.db")).Definitions()
            .Single(d => (string?)d?["function"]?["name"] == "get_game")!["function"]!;
        var props = def["parameters"]!["properties"]!.AsObject();
        Assert.True(props.ContainsKey("from") && props.ContainsKey("to"));
        Assert.Equal(["name"], def["parameters"]!["required"]!.AsArray().Select(n => (string?)n));
        Assert.Contains("with from and to it lists every play in that period", (string?)def["description"]);
    }
}
