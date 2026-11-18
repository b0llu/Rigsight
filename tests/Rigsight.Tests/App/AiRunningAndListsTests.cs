using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Core.Data;
using Rigsight.Tests.Data;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.App;

/// <summary>
/// RivaTuner named where it supplies the frame rates, and lists cut to their top said with how many there are (audit,
/// 2026-11-17): "RTSS isn't in the list of running apps" to a person whose game had frame rates from it (Q125, Q128), and
/// "no history" for an app that a 30-day list of 25 had simply left out (Q094).
/// </summary>
public sealed class AiRunningAndListsTests
{
    private static RunningApp App(string exe, double mem, double cpu = 0) =>
        new(exe, Path.GetFileNameWithoutExtension(exe), $@"C:\Apps\{exe}", 1, mem, cpu, 0, true, false, AppCategory.Other);

    private static AiTools Tools(string db, RunningSnapshot snapshot, string? rtss)
        => new(Settings(), db) { SampleRunning = () => snapshot, RivaTuner = () => rtss };

    [Fact]
    public void RivaTuner_running_but_too_light_to_list_is_named_as_running()
    {
        using var t = new TestDb("ai-rtss");
        var snapshot = new RunningSnapshot(DateTime.Now, [App("chrome.exe", 900), App("RTSS.exe", 12)]);

        string said = Tools(t.Path, snapshot, null).Run("get_running_now", "{}");

        Assert.Contains("RivaTuner Statistics Server (RTSS.exe) is running now: we record every game's frame rate, lows and stutters from it.", said);
        Assert.Contains("It isn't in the list above only because it uses little memory and CPU.", said);
    }

    [Fact]
    public void RivaTuner_with_a_frame_rate_this_minute_is_said_to_supply_it()
    {
        using var t = new TestDb("ai-rtss-fps");
        long game = t.Db.UpsertApp("RainbowSix.exe", "Rainbow Six", @"C:\Games\RainbowSix.exe", AppCategory.Game);
        long now = U(DateTime.Now) / 60 * 60 - 60;
        var minute = Minute(now, app: game);
        minute.Fps = 357;
        t.Db.WriteMinute(minute);

        string said = Tools(t.Path, new RunningSnapshot(DateTime.Now, [App("chrome.exe", 900)]), "running").Run("get_running_now", "{}");

        Assert.Contains("is running now", said);
        Assert.Contains("It is supplying frame rates now: it gave Rainbow Six 357 fps in the minute at", said);
    }

    [Fact]
    public void Old_frame_rates_never_make_RivaTuner_running_now()
    {
        using var t = new TestDb("ai-rtss-old");
        var minute = Minute(U(DateTime.Now.AddHours(-2)));
        minute.Fps = 140;
        t.Db.WriteMinute(minute);

        string said = Tools(t.Path, new RunningSnapshot(DateTime.Now, [App("chrome.exe", 900)]), "stopped").Run("get_running_now", "{}");

        Assert.DoesNotContain("is running now", said);
        Assert.Contains("RivaTuner Statistics Server isn't running now, so no frame rates are being recorded", said);
    }

    [Fact]
    public void A_game_with_frame_rates_says_RivaTuner_ran_for_those_plays()
    {
        using var t = new TestDb("ai-rtss-game");
        long game = t.Db.UpsertApp("kickoff.exe", "Kickoff", @"C:\RigsightTestGames\Kickoff\kickoff.exe", AppCategory.Game);
        long s = U(DateTime.Today.AddDays(-1).AddHours(20));
        t.Db.InsertSession(Session(game, s, s + 30 * 60, game: true));
        for (int m = 0; m < 30; m++)
        {
            var minute = Minute(s + m * 60, app: game, gpuApp: game, gpuLoad: 97);
            minute.Fps = 144;
            t.Db.WriteMinute(minute);
        }

        string said = new AiTools(Settings(), t.Path).Run("get_game", """{"name":"Kickoff"}""");

        Assert.Contains("FPS average 144", said);
        Assert.Contains("Every play with an FPS figure had RivaTuner Statistics Server (RTSS) running during it", said);
    }

    [Fact]
    public void A_temperature_peak_names_the_app_in_front_apart_from_the_one_working_the_part()
    {
        using var t = new TestDb("ai-peak-front");
        long chrome = t.Db.UpsertApp("chrome.exe", "Google Chrome", @"C:\Apps\chrome.exe", AppCategory.Browser);
        long steam = t.Db.UpsertApp("steam.exe", "Steam", @"C:\Apps\steam.exe", AppCategory.Launcher);
        long s = U(DateTime.Today.AddDays(-1).AddHours(15));
        t.Db.WriteMinute(Minute(s, cpu: 60, app: chrome, cpuApp: chrome, gpuApp: chrome));
        t.Db.WriteMinute(Minute(s + 60, cpu: 90, cpuMax: 95, gpu: 40, gpuMax: 41, app: chrome, cpuApp: steam, gpuApp: chrome));
        string day = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");

        string said = new AiTools(Settings(), t.Path).Run("get_temperatures", $$"""{"from":"{{day}}","to":"{{day}}"}""");

        Assert.Contains("CPU peak: 95 °C at", said);
        Assert.Contains(", with Steam working it hardest; Google Chrome was the app in front.", said);
        Assert.Contains(", with Google Chrome working it hardest (also the app in front).", said);
    }

    [Fact]
    public void The_running_list_says_how_many_it_leaves_out()
    {
        using var t = new TestDb("ai-running-count");
        var apps = Enumerable.Range(1, 22).Select(i => App($"busy{i}.exe", 500)).Concat(Enumerable.Range(1, 9).Select(i => App($"quiet{i}.exe", 20))).ToList();

        string said = Tools(t.Path, new RunningSnapshot(DateTime.Now, apps), null).Run("get_running_now", "{}");

        Assert.Contains("Showing 20 of the 31 apps running now", said);
        Assert.Contains("the other 11 aren't listed. An app missing from this list may still be running", said);
    }

    [Fact]
    public void A_long_app_list_says_its_top_of_how_many_and_to_ask_a_shorter_period()
    {
        using var t = new TestDb("ai-apps-count");
        long hour = U(DateTime.Today.AddDays(-1).AddHours(12));
        for (int i = 1; i <= 30; i++)
        {
            long app = t.Db.UpsertApp($"app{i}.exe", $"App {i}", $@"C:\Apps\app{i}.exe", AppCategory.Other);
            t.Db.AddAppHour(Hour(hour, app, fg: 60 * i));
        }
        string day = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");

        string said = new AiTools(Settings(), t.Path).Run("get_apps", $$"""{"from":"{{day}}","to":"{{day}}"}""");

        Assert.Contains("(showing the top 25 of 30 apps):", said);
        Assert.Contains("Showing the top 25 of 30 apps; the other 5 had less time in front", said);
        Assert.Contains("ask get_apps again for a shorter period", said);
        Assert.DoesNotContain("App 5:", said);
    }

    [Fact]
    public void A_short_app_list_says_it_is_all_of_them()
    {
        using var t = new TestDb("ai-apps-all");
        long hour = U(DateTime.Today.AddDays(-1).AddHours(12));
        long app = t.Db.UpsertApp("app.exe", "App", @"C:\Apps\app.exe", AppCategory.Other);
        t.Db.AddAppHour(Hour(hour, app, fg: 600));
        string day = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");

        string said = new AiTools(Settings(), t.Path).Run("get_apps", $$"""{"from":"{{day}}","to":"{{day}}"}""");

        Assert.Contains("(all 1):", said);
        Assert.DoesNotContain("Showing the top", said);
    }
}
