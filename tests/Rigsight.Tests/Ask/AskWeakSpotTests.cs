using Rigsight.Core.Ask;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Services;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Ask;

/// <summary>Nicknames and taught names, parts of a month, habits, a carried-over time said out loud, and each crash verdict.</summary>
public sealed class AskWeakSpotTests
{
    // A Monday afternoon.
    private static readonly DateTime Now = new(2026, 10, 5, 14, 30, 0);
    private static readonly AskApp[] Apps =
    [
        new(1, "Visual Studio Code", "Code.exe", AppCategory.Development, 90_000), new(2, "Dota 2", "dota2.exe", AppCategory.Game, 60_000),
        new(3, "Google Chrome", "chrome.exe", AppCategory.Browser, 400_000), new(4, "Counter-Strike 2", "cs2.exe", AppCategory.Game, 30_000),
    ];
    private static long U(DateTime t) => TimeUtil.ToUnix(t);

    [Fact]
    public void An_app_is_known_by_its_nickname_and_by_the_name_the_user_gave_it()
    {
        var router = new AskRouter(new AskEmbedder());
        Assert.Equal("Visual Studio Code", router.Read("time in vs code", Now, Apps).App);
        Assert.Equal("Visual Studio Code", router.Read("how long did i use vscode this week", Now, Apps).App);
        Assert.Equal("Counter-Strike 2", router.Read("how long did i play csgo", Now, Apps).App);
        // Not on this PC: unknown, until the user says what they mean by it.
        Assert.Equal("the moba", router.Read("how long did i play the moba", Now, Apps).UnknownApp is { } name ? "the moba".EndsWith(name) ? "the moba" : name : null);
        router.SetAliases(new Dictionary<string, string> { ["moba"] = "dota2.exe" });
        var taught = router.Read("how long did i play the moba yesterday", Now, Apps);
        Assert.Equal(("Dota 2", null, "yesterday"), (taught.App, taught.UnknownApp, taught.Period!.Label));
        // Two letters are a nickname only where an app is named, not anywhere in a sentence.
        Assert.Null(router.Read("is my cpu ok vs last week", Now, Apps).App);
    }

    [Theory]
    [InlineData("how long was i on in the first week of october", "in the first week of October", 10, 1, 10, 8)]
    [InlineData("how hot was it at the end of september", "at the end of September", 9, 21, 10, 1)]
    [InlineData("what changed in the last week of september", "in the last week of September", 9, 24, 10, 1)]
    public void Parts_of_a_month_are_read(string text, string label, int m1, int d1, int m2, int d2)
    {
        var p = AskTime.Parse(text, Now).Period!;
        Assert.Equal((label, new DateTime(2026, m1, d1), new DateTime(2026, m2, d2)), (p.Label, p.From, p.To));
    }

    [Fact]
    public void A_part_of_the_day_with_no_day_is_the_last_one_there_was()
    {
        Assert.Equal("last night", AskTime.Parse("how hot did it get at night", Now).Period!.Label);
        Assert.Equal("yesterday evening", AskTime.Parse("what did i do in the evening", Now).Period!.Label); // 2:30 PM: this evening hasn't come
        Assert.Equal("this morning", AskTime.Parse("what did i do in the morning", Now).Period!.Label);
    }

    /// <summary>Three weeks of evenings: a game from 7 to 9 PM every day, an hour at a desk app from 4 PM.</summary>
    private static TestDb Pc(int days = 21, Action<RigsightDb, long>? more = null)
    {
        var t = new TestDb("ask");
        long game = t.Db.UpsertApp("game.exe", "Star Racer", @"C:\Games\Star Racer\game.exe", AppCategory.Game);
        long desk = t.Db.UpsertApp("notes.exe", "Notes", @"C:\Apps\notes.exe", AppCategory.Productivity);
        for (var day = Now.Date.AddDays(-days); day < Now.Date; day = day.AddDays(1))
        {
            for (int m = 0; m < 60; m++)
                t.Db.WriteMinute(new SystemMinute { Ts = U(day.AddHours(16).AddMinutes(m)), GpuTemp = 40, GpuTempMax = 41, CpuTemp = 42, CpuTempMax = 43, GpuLoad = 3, CpuLoad = 5, RamUsed = 6, FgApp = desk, ActiveSec = 60 });
            for (int m = 0; m < 120; m++)
                t.Db.WriteMinute(new SystemMinute { Ts = U(day.AddHours(19).AddMinutes(m)), GpuTemp = 70, GpuTempMax = 71, CpuTemp = 60, CpuTempMax = 62, GpuLoad = 98, CpuLoad = 40, RamUsed = 12, FgApp = game, GpuApp = game, ActiveSec = 60 });
            t.Db.AddAppHour(new AppHour { Ts = U(day.AddHours(16)), AppId = desk, FgSec = 3600 });
            t.Db.AddAppHour(new AppHour { Ts = U(day.AddHours(19)), AppId = game, FgSec = 3600 });
            t.Db.AddAppHour(new AppHour { Ts = U(day.AddHours(20)), AppId = game, FgSec = 3600 });
        }
        more?.Invoke(t.Db, game);
        return t;
    }

    private static readonly AskEngine Engine = new(new AskRouter(new AskEmbedder()));

    private static AskAnswer Ask(TestDb t, string text, AskContext? context = null)
    {
        using var reader = t.Reader();
        return Engine.Ask(reader, new RigsightSettings(), text, context, Now);
    }

    [Fact]
    public void A_habit_is_said_with_its_count_and_not_at_all_under_ten_days()
    {
        using (var t = Pc())
        {
            Assert.Equal("Mostly between 7 PM and 9 PM: 100% of the time you played in the last 21 days falls in those hours.", Ask(t, "when do i usually play").Lead.Replace("**", ""));
            Assert.StartsWith("You usually start around 4:00 PM: on 21 of the last 21 days you used the PC", Ask(t, "what time do i usually start").Lead.Replace("**", ""));
            Assert.StartsWith("On a usual day you play for about 2h 00m: on 21 of the last 21 days you did", Ask(t, "how long do i usually play").Lead.Replace("**", ""));
            Assert.StartsWith("On a usual day you use the PC for about 3h 00m", Ask(t, "how long am i usually on my pc").Lead.Replace("**", ""));
        }
        using (var few = Pc(days: 6))
        {
            var a = Ask(few, "when do i usually play");
            Assert.Equal("Not enough days yet to call it a habit: you played on 6 of the last 21 days, and I'd want 10.", a.Lead);
            Assert.False(a.Understood);
        }
    }

    [Fact]
    public void A_time_carried_over_from_earlier_is_said_above_the_answer()
    {
        using var t = Pc();
        var first = Ask(t, "how hot did my gpu get on friday");
        Assert.Null(first.Carried); // it named its own time
        var next = Ask(t, "how long did i play", first.Context);
        Assert.Equal(("Still about Fri 2 Oct", "How long was I on my PC today?"), (next.Carried, next.CarriedInstead));
        Assert.Contains("2h 00m", next.Lead);
        // How the PC is doing asks about now unless told; and nothing is said when the time carried is today.
        Assert.Null(Ask(t, "is my pc ok", first.Context).Carried);
        var today = Ask(t, "how hot did it get today");
        Assert.Null(Ask(t, "any crashes", today.Context).Carried);
    }

    [Fact]
    public void An_unknown_name_offers_the_nearest_apps_to_pick_from()
    {
        using var t = Pc();
        var a = Ask(t, "how long did i play starracer");
        Assert.StartsWith("I can't find an app or game called", a.Lead);
        Assert.Equal(["Star Racer"], a.AppChoices.Select(c => c.Name));
        Assert.Equal("game.exe", a.AppChoices[0].Exe);
        Assert.NotNull(a.Query?.UnknownApp);
    }

    [Fact]
    public void A_follow_up_that_could_have_stood_alone_offers_its_other_reading()
    {
        using var t = Pc();
        var crashes = Ask(t, "any crashes last week");
        var next = Ask(t, "and star racer", crashes.Context);
        Assert.Equal(AskIntent.Crashes, next.Query!.Intent); // "and…": about crashes still
        var alone = Ask(t, "and how long did i play it", crashes.Context);
        Assert.True(alone.Query!.Intent == AskIntent.Usage || alone.FollowUps.Any(f => f.StartsWith("No, I meant:", StringComparison.Ordinal)));
    }

    // ── Each verdict a crash answer can give ────────────────────────────

    private static AskAnswer Why(CrashEvent crash, Action<RigsightDb, long>? more = null, string question = "why did my pc crash yesterday")
    {
        using var t = Pc(more: (db, game) =>
        {
            db.SetMeta("changes_scanned", U(Now).ToString());
            more?.Invoke(db, game);
            db.InsertCrashes([crash]);
        });
        return Ask(t, question);
    }

    private static readonly DateTime At = Now.Date.AddDays(-1).AddHours(20).AddMinutes(14);

    [Fact]
    public void A_sudden_shutdown_has_no_recorded_cause_and_says_so()
    {
        var a = Why(new CrashEvent { Ts = U(At), Kind = CrashKind.UnexpectedShutdown });
        Assert.StartsWith("The cause isn't recorded.", a.PlainText());
        Assert.Contains(a.Points, p => p.Group == "Ruled out" && p.Text.StartsWith("**Heat**"));
        Assert.DoesNotContain("Most likely", a.PlainText());
        Assert.DoesNotContain("It has happened", a.Advice ?? ""); // once is not a pattern
    }

    [Fact]
    public void A_blue_screen_names_what_windows_reported_and_no_more()
    {
        var a = Why(new CrashEvent { Ts = U(At), Kind = CrashKind.SystemCrash, Code = "0x124" }, question: "what caused the blue screen yesterday");
        Assert.StartsWith("Windows hit an error it couldn't recover from: WHEA_UNCORRECTABLE_ERROR.", a.PlainText());
        Assert.Contains(a.Points, p => p.Group == "Ruled out" && p.Text.Contains("no driver, Windows update or hardware change"));
    }

    [Fact]
    public void A_driver_reset_with_no_change_before_it_is_not_put_down_to_a_new_driver()
    {
        var a = Why(new CrashEvent { Ts = U(At), Kind = CrashKind.GpuDriverReset });
        Assert.StartsWith("The graphics driver stopped responding, and Windows reset it.", a.PlainText());
        Assert.DoesNotContain("Most likely", a.PlainText());
        Assert.DoesNotContain("first suspect", a.PlainText());
    }

    [Fact]
    public void A_new_driver_with_no_pattern_behind_it_is_a_suspect_not_a_verdict()
    {
        // The driver came two days before, but resets had been happening before it too.
        var a = Why(new CrashEvent { Ts = U(At), Kind = CrashKind.GpuDriverReset }, (db, _) =>
        {
            db.InsertChanges([new SystemChange(At.AddDays(-2), ChangeKind.Driver, "NVIDIA graphics driver 616.92") { Subject = "nvidia-graphics", Was = "610.47", Now = "616.92" }]);
            db.InsertCrashes([new CrashEvent { Ts = U(At.AddDays(-9)), Kind = CrashKind.GpuDriverReset }]);
        });
        Assert.StartsWith("The graphics driver is the first suspect. It changed 2 days before; that alone doesn't prove it.", a.PlainText());
        Assert.DoesNotContain("only began after", a.PlainText());
    }

    [Fact]
    public void An_app_crashing_in_its_own_code_is_not_blamed_on_the_pc()
    {
        var a = Why(new CrashEvent { Ts = U(At), Kind = CrashKind.AppCrash, AppExe = "game.exe", Module = "game.exe", Code = "c0000005" }, question: "why did star racer crash yesterday");
        Assert.StartsWith("Star Racer crashed in its own code.", a.PlainText());
        Assert.DoesNotContain(a.Points, p => p.Text.StartsWith("**What changed**")); // a driver that week wouldn't explain a bug in the game
    }

    [Fact]
    public void A_freeze_says_windows_keeps_no_reason_for_it()
    {
        var a = Why(new CrashEvent { Ts = U(At), Kind = CrashKind.AppHang, AppExe = "game.exe" }, question: "why did star racer freeze yesterday");
        Assert.StartsWith("Star Racer froze and was closed. Windows doesn't record why an app stops responding.", a.PlainText());
    }

    [Fact]
    public void Memory_full_just_before_points_to_memory()
    {
        // Half past nine: after the evening's recorded minutes, so these are the only ones around it.
        var late = Now.Date.AddDays(-1).AddHours(21).AddMinutes(30);
        var a = Why(new CrashEvent { Ts = U(late), Kind = CrashKind.AppCrash, AppExe = "game.exe", Module = "game.exe" }, (db, game) =>
        {
            db.ApplyInventory(new() { [Inventory.Ram] = [new(Inventory.Ram, Inventory.Ram, "Memory", "16 GB")] }, "test", Now.AddDays(-30));
            for (int m = 10; m >= 1; m--)
                db.WriteMinute(new SystemMinute { Ts = U(late.AddMinutes(-m)), GpuTemp = 70, GpuTempMax = 71, CpuTemp = 60, CpuTempMax = 62, GpuLoad = 98, CpuLoad = 40, RamUsed = 15.6, FgApp = game, ActiveSec = 60 });
        }, "why did star racer crash yesterday");
        Assert.Contains(a.Points, p => p.Group == "Points to" && p.Text == "**Memory**: nearly full, 15.6 of 16 GB in use.");
        Assert.Contains(a.Trail, s => s.Text == "Memory passed 90% full");
    }
}
