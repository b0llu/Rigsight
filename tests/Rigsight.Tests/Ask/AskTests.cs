using Rigsight.Core.Ask;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Services;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Ask;

/// <summary>The time a question names: found by rule, taken out of the question, and labelled as a sentence would say it.</summary>
public sealed class AskTimeTests
{
    // A Monday afternoon.
    private static readonly DateTime Now = new(2026, 10, 5, 14, 30, 0);
    private static DateTime D(int month, int day, int hour = 0) => new(2026, month, day, hour, 0, 0);

    [Theory]
    [InlineData("why did my pc crash yesterday", "yesterday", 10, 4, 10, 5, "why did my pc crash")]
    [InlineData("how hot was it today?", "today", 10, 5, 10, 6, "how hot was it ?")]
    [InlineData("crashes on friday", "on Fri 2 Oct", 10, 2, 10, 3, "crashes")]
    [InlineData("what did i play last saturday", "on Sat 3 Oct", 10, 3, 10, 4, "what did i play")]
    [InlineData("anything on monday", "today", 10, 5, 10, 6, "anything")]
    [InlineData("anything last monday", "on Mon 28 Sep", 9, 28, 9, 29, "anything")]
    [InlineData("the day before yesterday", "on Sat 3 Oct", 10, 3, 10, 4, "")]
    [InlineData("what changed 3 days ago", "on Fri 2 Oct", 10, 2, 10, 3, "what changed")]
    [InlineData("usage this week", "this week", 10, 5, 10, 12, "usage")]
    [InlineData("usage last week", "last week", 9, 28, 10, 5, "usage")]
    [InlineData("data this month", "this month", 10, 1, 11, 1, "data")]
    [InlineData("data in september", "last month", 9, 1, 10, 1, "data")]
    [InlineData("data in august", "in August", 8, 1, 9, 1, "data")]
    [InlineData("what happened on 1 oct", "on Thu 1 Oct", 10, 1, 10, 2, "what happened")]
    [InlineData("what happened on oct 1st", "on Thu 1 Oct", 10, 1, 10, 2, "what happened")]
    [InlineData("what happened on the 3rd", "on Sat 3 Oct", 10, 3, 10, 4, "what happened")]
    [InlineData("installed in the last 7 days", "in the last 7 days", 9, 28, 10, 6, "installed")]
    [InlineData("installed in the past two weeks", "in the last 14 days", 9, 21, 10, 6, "installed")]
    [InlineData("changes since friday", "since Fri 2 Oct", 10, 2, 10, 6, "changes")]
    [InlineData("crashes lately", "in the last 7 days", 9, 28, 10, 6, "crashes")]
    public void A_time_in_a_question_is_found_and_taken_out(string text, string label, int m1, int d1, int m2, int d2, string rest)
    {
        var (period, left) = AskTime.Parse(text, Now);
        Assert.NotNull(period);
        Assert.Equal((label, D(m1, d1), D(m2, d2)), (period.Label, period.From, period.To));
        Assert.Equal(rest, left);
    }

    [Fact]
    public void Parts_of_a_day_are_whole_hours_and_a_night_runs_into_the_morning()
    {
        var night = AskTime.Parse("did my internet drop last night", Now).Period!;
        Assert.Equal(("last night", D(10, 4, 18), D(10, 5, 6), ReportRange.Custom), (night.Label, night.From, night.To, night.Range));
        var morning = AskTime.Parse("what was heavy this morning", Now).Period!;
        Assert.Equal(("this morning", D(10, 5, 5), D(10, 5, 12)), (morning.Label, morning.From, morning.To));
        var hours = AskTime.Parse("in the last 3 hours", Now).Period!;
        Assert.Equal((Now.AddHours(-3), Now), (hours.From, hours.To));
    }

    [Theory]
    [InlineData("why is my pc slow")]
    [InlineData("what gpu do i have")]
    [InlineData("it may be the driver")] // "may" alone is a verb
    [InlineData("how do i update my bios")]
    public void A_question_without_a_time_has_none(string text)
    {
        var (period, left) = AskTime.Parse(text, Now);
        Assert.Null(period);
        Assert.Equal(text, left);
    }

    [Fact]
    public void Whole_calendar_units_keep_their_range_so_the_same_report_is_built_as_their_page_shows()
    {
        Assert.Equal(ReportRange.Day, AskTime.Parse("yesterday", Now).Period!.Range);
        Assert.Equal(ReportRange.Week, AskTime.Parse("last week", Now).Period!.Range);
        Assert.Equal(ReportRange.Month, AskTime.Parse("in september", Now).Period!.Range);
        Assert.Equal(ReportRange.Year, AskTime.Parse("this year", Now).Period!.Range);
        Assert.Equal(ReportRange.All, AskTime.Parse("ever", Now).Period!.Range);
        Assert.Equal(ReportRange.Custom, AskTime.Parse("in the last 7 days", Now).Period!.Range);
    }
}

/// <summary>How a question is read: its topic by meaning (the real model), its app and part by rule, follow-ups, and what it's taught.</summary>
public sealed class AskRouterTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 14, 30, 0);
    private static readonly AskApp[] Apps =
    [
        new(1, "Cyberpunk 2077", "Cyberpunk2077.exe", AppCategory.Game, 90_000), new(2, "Google Chrome", "chrome.exe", AppCategory.Browser, 400_000),
        new(3, "REMATCH", "RuntimeClient-WinGDK-Shipping.exe", AppCategory.Game, 30_000), new(4, "Settings", "SystemSettings.exe", AppCategory.System, 500),
        new(5, "Steam", "steam.exe", AppCategory.Launcher, 9_000), new(6, "Steam Client WebHelper", "steamwebhelper.exe", AppCategory.Launcher, 20_000),
    ];

    // One model for the whole class: loading it takes a fifth of a second.
    private static readonly Lazy<AskRouter> Model = new(() => new AskRouter(new AskEmbedder()));
    private static AskQuery Read(string text, AskContext? context = null) => Model.Value.Read(text, Now, Apps, context);

    [Fact]
    public void The_model_ships_with_the_app() => Assert.True(AskEmbedder.Available, "ask\\model.onnx and ask\\vocab.txt should be next to the app");

    [Theory]
    // None of these wordings is among the examples.
    [InlineData("whats behind the bsod i got last night", AskIntent.Crashes)]
    [InlineData("my computer just turned off in the middle of a match", AskIntent.Crashes)]
    [InlineData("has my computer crashed in the last month", AskIntent.Crashes)]
    [InlineData("what's new on my computer since monday", AskIntent.Changes)]
    [InlineData("when did nvidia driver get updated", AskIntent.Changes)]
    [InlineData("max gpu temp last week", AskIntent.Temps)]
    [InlineData("did my pc run hot yesterday", AskIntent.Temps)]
    [InlineData("how many hours on the pc this week", AskIntent.Usage)]
    [InlineData("when was the last time I opened chrome", AskIntent.Usage)]
    [InlineData("which apps took most of my time last week", AskIntent.TopApps)]
    [InlineData("pc was super laggy last night whats up", AskIntent.Slow)]
    [InlineData("game ran like garbage today", AskIntent.Slow)]
    [InlineData("whats eating all my memory", AskIntent.Memory)]
    [InlineData("how many gb did I download this month", AskIntent.Network)]
    [InlineData("whats hogging the internet", AskIntent.Network)]
    [InlineData("how many gigs free on d", AskIntent.Storage)]
    [InlineData("is my cpu fan dead", AskIntent.Fans)]
    [InlineData("everything alright with my rig?", AskIntent.Health)]
    [InlineData("sum up yesterday for me", AskIntent.Health)]
    [InlineData("whats my graphics card", AskIntent.Specs)]
    [InlineData("what sort of things can I ask", AskIntent.Help)]
    [InlineData("heyy", AskIntent.Hello)]
    [InlineData("awesome thanks", AskIntent.Thanks)]
    [InlineData("does this upload my data anywhere", AskIntent.Who)]
    [InlineData("whats the weather in mumbai", AskIntent.Other)]
    [InlineData("can you write a python script", AskIntent.Other)]
    public void A_question_is_read_by_its_meaning(string text, AskIntent intent) => Assert.Equal(intent, Read(text).Intent);

    [Theory]
    [InlineData("zxcv qwer asdf")]
    [InlineData("purple monkey dishwasher")]
    public void What_means_nothing_here_is_not_guessed_at(string text) => Assert.Contains(Read(text).Intent, new[] { AskIntent.None, AskIntent.Other });

    [Fact]
    public void Why_asks_for_a_cause_and_counting_words_for_a_list()
    {
        Assert.True(Read("why did my pc crash yesterday").Why);
        Assert.True(Read("what caused the blue screen").Why);
        Assert.False(Read("did my pc crash this week").Why);
        Assert.False(Read("how many times did it crash").Why);
        Assert.True(Read("how do i update my bios").HowTo);
        Assert.True(Read("when did i last play cyberpunk").Last);
        Assert.True(Read("how much did i play this week").Games);
    }

    [Fact]
    public void An_app_is_found_by_its_name_its_exe_or_a_telling_word_of_its_name()
    {
        Assert.Equal("Cyberpunk 2077", Read("how hot does cyberpunk 2077 make my pc").App);
        Assert.Equal("Cyberpunk 2077", Read("cyberpunk keeps crashing").App);
        Assert.Equal("Google Chrome", Read("how much data does chrome use").App);
        Assert.Equal("REMATCH", Read("did rematch crash lately").App);
        Assert.Equal("Steam", Read("how much did steam download").App); // the full name beats one word of a longer one
        // An app called Settings isn't meant by a question about settings, nor Google Chrome by "google".
        Assert.Null(Read("which settings changed this week").App);
        Assert.Null(Read("why is my pc slow").App);
    }

    [Fact]
    public void The_app_is_swapped_out_of_what_gets_remembered()
    {
        Assert.Equal("how hot does this game make my pc", Read("How hot does Cyberpunk make my PC yesterday").Generic);
        Assert.Equal("how much data does this app use", Read("how much data does chrome use").Generic);
    }

    [Fact]
    public void Parts_and_drive_letters_are_found()
    {
        Assert.Equal(AskPart.Gpu, Read("how hot did my graphics card get").Part);
        Assert.Equal(AskPart.Cpu, Read("processor temperature today").Part);
        Assert.Equal(AskPart.Memory, Read("how much ram is used").Part);
        Assert.Equal(("C", AskPart.Drive), (Read("how full is my c drive").Drive, Read("how full is my c drive").Part));
        Assert.Equal("D", Read("space left on d:").Drive);
    }

    [Fact]
    public void A_short_follow_up_leans_on_the_question_before()
    {
        var first = Read("how hot did my gpu get on saturday");
        var context = new AskContext(first.Intent, first.Period, first.AppId, first.App, first.Part);

        var cpu = Read("and the cpu?", context);
        Assert.Equal((AskIntent.Temps, AskPart.Cpu, "on Sat 3 Oct", true), (cpu.Intent, cpu.Part, cpu.Period!.Label, cpu.FollowsUp));

        var before = Read("and the day before?", context);
        Assert.Equal((AskIntent.Temps, "on Fri 2 Oct"), (before.Intent, before.Period!.Label));

        var week = Read("what about last week", context);
        Assert.Equal((AskIntent.Temps, "last week", AskPart.Gpu), (week.Intent, week.Period!.Label, week.Part));

        // A whole new question is a new question.
        var other = Read("what changed on my pc this week", context);
        Assert.Equal((AskIntent.Changes, false), (other.Intent, other.FollowsUp));
        // "…at the time" brings the time along, not the topic.
        var then = Read("how much memory was in use at the time", context);
        Assert.Equal((AskIntent.Memory, "on Sat 3 Oct"), (then.Intent, then.Period!.Label));
        // Thanks is thanks, not a follow-up.
        Assert.Equal(AskIntent.Thanks, Read("thanks", context).Intent);

        var crashes = new AskContext(AskIntent.Crashes, AskTime.Week(Now.AddDays(-7), Now), null, null, AskPart.None);
        var why = Read("why?", crashes);
        Assert.Equal((AskIntent.Crashes, true, "last week"), (why.Intent, why.Why, why.Period!.Label));
    }

    [Fact]
    public void A_wording_it_is_taught_is_understood_from_then_on_for_any_app_and_day()
    {
        var router = new AskRouter(new AskEmbedder());
        const string odd = "is cyberpunk being a potato today";
        var before = router.Read(odd, Now, Apps);
        Assert.NotEqual(AskIntent.Slow, before.Intent);

        var learned = router.Learn(before, AskIntent.Slow, why: false);
        Assert.Equal(new AskLearned("is this game being a potato", AskIntent.Slow, false), learned);
        Assert.Equal((AskIntent.Slow, 1.0), (router.Read(odd, Now, Apps).Intent, router.Read(odd, Now, Apps).Score));
        // Another game, another day: the same wording.
        var again = router.Read("is rematch being a potato yesterday", Now, Apps);
        Assert.Equal((AskIntent.Slow, "REMATCH", "yesterday"), (again.Intent, again.App, again.Period!.Label));
        // What it knew before, it still knows.
        Assert.Equal(AskIntent.Crashes, router.Read("why did my pc crash", Now, Apps).Intent);

        // Taught again as something else: corrected, not doubled. And it survives being saved and loaded.
        router.Learn(before, AskIntent.Temps, why: false);
        Assert.Single(router.Learned);
        var fresh = new AskRouter(new AskEmbedder());
        fresh.SetLearned(router.Learned);
        Assert.Equal(AskIntent.Temps, fresh.Read(odd, Now, Apps).Intent);
        fresh.Forget();
        Assert.Empty(fresh.Learned);
    }

    [Fact]
    public void Without_the_model_telling_words_still_carry_a_question()
    {
        var plain = new AskRouter(null);
        Assert.Equal(AskIntent.Crashes, plain.Read("why did my pc crash yesterday", Now, Apps).Intent);
        Assert.Equal(AskIntent.Network, plain.Read("how much internet did i use", Now, Apps).Intent);
        Assert.Equal(AskIntent.None, plain.Read("tell me something", Now, Apps).Intent);
        // And a taught wording, asked again word for word.
        var odd = plain.Read("potato mode", Now, Apps);
        plain.Learn(odd, AskIntent.Slow, false);
        Assert.Equal(AskIntent.Slow, plain.Read("potato mode", Now, Apps).Intent);
    }

    [Fact]
    public void The_memory_file_keeps_what_was_taught_and_what_went_unanswered()
    {
        string path = Path.Combine(Support.TestEnvironment.NewFolder("ask"), "ask.json");
        var memory = new AskMemory { Learned = [new("potato mode", AskIntent.Slow, false)] };
        memory.AddUnanswered("what is my battery wear");
        memory.AddUnanswered("What is my battery wear"); // the same one once
        memory.Save(path);
        var read = AskMemory.Load(path);
        Assert.Equal([new AskLearned("potato mode", AskIntent.Slow, false)], read.Learned);
        Assert.Equal(["What is my battery wear"], read.Unanswered);
        Assert.Empty(AskMemory.Load(Path.Combine(Path.GetDirectoryName(path)!, "none.json")).Learned);
    }
}

/// <summary>Answers, from a database with known contents: every number in them is one that was put in.</summary>
public sealed class AskEngineTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 14, 30, 0);
    private static readonly AskEngine Engine = new(new AskRouter(new AskEmbedder()));
    private static long U(DateTime t) => TimeUtil.ToUnix(t);

    /// <summary>A PC that played a game every evening for three weeks, with a GPU reading each minute.</summary>
    private static TestDb Pc(Action<RigsightDb, long>? more = null)
    {
        var t = new TestDb("ask");
        long game = t.Db.UpsertApp("game.exe", "Star Racer", @"C:\Games\Star Racer\game.exe", AppCategory.Game);
        for (var day = Now.Date.AddDays(-21); day <= Now.Date; day = day.AddDays(1))
        {
            var start = day.AddHours(19);
            for (int m = 0; m < 120 && start.AddMinutes(m) < Now; m++)
                t.Db.WriteMinute(new SystemMinute
                {
                    Ts = U(start.AddMinutes(m)), GpuTemp = 68, GpuTempMax = 70, CpuTemp = 60, CpuTempMax = 62, GpuLoad = 98, CpuLoad = 35, RamUsed = 12,
                    GpuPower = 250, FgApp = game, GpuApp = game, ActiveSec = 60,
                });
            if (start >= Now) continue;
            foreach (int hour in new[] { 0, 1 })
                t.Db.AddAppHour(new AppHour { Ts = U(start.AddHours(hour)), AppId = game, FgSec = 3600, GpuTempSum = 68 * 60, GpuTempN = 60, GpuTempMax = 70, CpuTempSum = 60 * 60, CpuTempN = 60, CpuTempMax = 62 });
            t.Db.InsertSession(new SessionRow { AppId = game, Start = U(start), End = U(start.AddHours(2)), ActiveSec = 7200, GpuTempMax = 70, CpuTempMax = 62, IsGame = true });
        }
        more?.Invoke(t.Db, game);
        return t;
    }

    private static AskAnswer Ask(TestDb t, string text, AskContext? context = null)
    {
        using var reader = t.Reader();
        return Engine.Ask(reader, new RigsightSettings(), text, context, Now);
    }

    [Fact]
    public void A_driver_reset_after_a_new_driver_is_put_down_to_the_driver_and_heat_is_ruled_out()
    {
        var crash = Now.Date.AddDays(-1).AddHours(20).AddMinutes(14);
        using var t = Pc((db, _) =>
        {
            db.InsertChanges([new SystemChange(crash.AddDays(-2), ChangeKind.Driver, "NVIDIA graphics driver 616.92") { Subject = "nvidia-graphics", Was = "610.47", Now = "616.92" }]);
            db.SetMeta("changes_scanned", U(Now).ToString());
            db.InsertCrashes([new CrashEvent { Ts = U(crash), Kind = CrashKind.GpuDriverReset }, new CrashEvent { Ts = U(crash.AddHours(-1)), Kind = CrashKind.GpuDriverReset }]);
        });
        var a = Ask(t, "Why did my PC crash yesterday?");
        string text = a.PlainText();
        Assert.StartsWith("Most likely the graphics driver. It changed 2 days before, and the problem only began after that.", text);
        Assert.Contains("Graphics driver reset at 8:14 PM yesterday, with Star Racer in front.", text);
        Assert.Contains("Heat: GPU 70° and CPU 62° at most in the 5 minutes before, under your limits.", text);
        Assert.Contains("Load: GPU at 98% and CPU at 35% in the last minute recorded before it.", text);
        Assert.Contains("What changed: NVIDIA graphics driver 616.92 (2 days before).", text);
        Assert.Contains("the 2nd graphics driver reset in the 30 days up to it, all of them after NVIDIA graphics driver 616.92; none in the", text);
        Assert.Contains("go back to 610.47, the version before", a.Advice);
        Assert.Equal("Why it crashed · yesterday", a.Read);

        // The evidence is sorted by what it says about the cause: the driver and the pattern point to it, heat is ruled out.
        Assert.Equal(["Points to", "Points to", "Ruled out", "Also noted"], a.Points.Select(p => p.Group));
        Assert.StartsWith("**What changed**", a.Points[0].Text);
        Assert.StartsWith("**Heat**", a.Points[2].Text);
        // And what led up to it, in order: the game in front, the GPU flat out, the first reset, this one.
        Assert.Equal("Leading up to it", a.TrailTitle);
        Assert.Equal(["Star Racer in front (since before this)", "GPU load at 90% or more from here on", "GPU at its hottest, 70°", "Graphics driver reset"],
            a.Trail.Select(s => s.Text));
        Assert.Equal(("7:44 PM", "8:14 PM", AskTone.Hot), (a.Trail[0].When, a.Trail[^1].When, a.Trail[^1].Tone));

        // "That" in the next question is the driver resets.
        var history = Ask(t, "when did that start happening?", a.Context);
        Assert.True(history.Query!.History);
        Assert.StartsWith("2 graphics driver resets on record. All of them yesterday.", history.PlainText());
        Assert.Contains("In the week before the first one: NVIDIA graphics driver 616.92 (2 days before).", history.Paragraphs);
        Assert.Equal(("crashes", crash.Date), (a.Links[0].Page, a.Links[0].Day));
        Assert.Contains(a.Links, l => l.Page == "timeline");

        // The follow-up keeps the day.
        var heat = Ask(t, "how hot did it get then?", a.Context);
        Assert.Contains("yesterday", heat.Read);
        Assert.Contains("70°", heat.PlainText());
    }

    [Fact]
    public void Heat_over_the_limit_before_a_crash_is_the_verdict()
    {
        var crash = Now.Date.AddDays(-1).AddHours(20).AddMinutes(30);
        using var t = Pc((db, game) =>
        {
            db.WriteMinute(new SystemMinute { Ts = U(crash.AddMinutes(-1)), GpuTemp = 90, GpuTempMax = 93, CpuTemp = 70, CpuTempMax = 72, GpuLoad = 99, FgApp = game, ActiveSec = 60 });
            db.InsertCrashes([new CrashEvent { Ts = U(crash), Kind = CrashKind.SystemCrash, Code = "0x116" }]);
        });
        var a = Ask(t, "what caused the blue screen yesterday");
        Assert.StartsWith("Heat is the likely cause.", a.PlainText());
        Assert.Contains(a.Points, p => p.Tone == AskTone.Hot && p.Text.Contains("the GPU reached 93° (your limit is 83°)"));
    }

    [Fact]
    public void No_crash_is_said_plainly_with_the_last_one_before()
    {
        using var t = Pc((db, _) => db.InsertCrashes([new CrashEvent { Ts = U(Now.Date.AddDays(-9).AddHours(21)), Kind = CrashKind.AppCrash, AppExe = "game.exe", Module = "game.exe" }]));
        var a = Ask(t, "did my pc crash this week?");
        Assert.Equal("No crashes or sudden shutdowns this week.", a.Lead);
        Assert.Contains("Star Racer crashed", a.Paragraphs[0]);
        Assert.Equal("No blue screens this month.", Ask(t, "any blue screens this month").Lead);
        Assert.Equal("Yes, once in the last 30 days: Star Racer crashed Sat 26 Sep, 9:00 PM.", Ask(t, "has star racer crashed").Lead);
    }

    [Fact]
    public void Temperatures_time_and_most_used_come_from_the_readings()
    {
        using var t = Pc();
        var temps = Ask(t, "how hot did my gpu get yesterday");
        Assert.StartsWith("Yesterday, the GPU peaked at 70°", temps.Lead.Replace("**", ""));
        Assert.Contains("That is under your 83° limit.", temps.Paragraphs);
        Assert.Equal("Temperatures look fine yesterday.", Ask(t, "was my pc overheating yesterday").Lead.Replace("**", ""));

        var use = Ask(t, "how long was i on my pc yesterday");
        Assert.Equal("You were on your PC for 2h 00m yesterday.", use.Lead.Replace("**", ""));
        var last = Ask(t, "when did i last play star racer");
        Assert.StartsWith("You last played Star Racer yesterday, from 7:00 PM to", last.Lead.Replace("**", ""));
        var top = Ask(t, "what did i use most last week");
        Assert.Contains("Star Racer", top.Lead);
    }

    [Fact]
    public void A_time_with_nothing_recorded_says_so_and_never_makes_numbers_up()
    {
        using var t = Pc();
        var early = Ask(t, "how hot did it get in january");
        Assert.StartsWith("Nothing was recorded in January: the records start on", early.Lead);
        Assert.Empty(early.Facts);
        var today = Ask(t, "how hot did it get today"); // on from 7 PM only: nothing yet at 2:30
        Assert.StartsWith("Nothing was recorded today", today.Lead);
    }

    [Fact]
    public void What_it_cannot_answer_it_says_it_cannot()
    {
        using var t = Pc();
        Assert.False(Ask(t, "whats the weather in mumbai").Understood);
        var how = Ask(t, "how do i update my bios");
        Assert.StartsWith("I can't walk you through that", how.Lead);
        Assert.Equal(["What is my BIOS version?"], how.FollowUps);
        var odd = Ask(t, "zxcv qwer asdf");
        Assert.False(odd.Understood);
        Assert.NotEmpty(odd.FollowUps);
        // Said what it meant: answered as that.
        using var reader = t.Reader();
        var taught = Engine.Ask(reader, new RigsightSettings(), "zxcv qwer asdf", null, Now, AskIntent.Usage);
        Assert.Equal(AskIntent.Usage, taught.Query!.Intent);
        Assert.True(taught.Understood);
    }

    [Fact]
    public void Starters_are_questions_about_this_pc()
    {
        using var t = Pc((db, _) => db.InsertCrashes([new CrashEvent { Ts = U(Now.Date.AddDays(-2).AddHours(20)), Kind = CrashKind.SystemCrash, Code = "0x124" }]));
        using var reader = t.Reader();
        var starters = Engine.Starters(reader, new RigsightSettings(), Now);
        Assert.Equal("Why did my PC crash on Sat 3 Oct?", starters[0]);
        Assert.Contains("How hot does Star Racer make my PC?", starters);
        // And each of them is understood.
        Assert.All(starters, s => Assert.True(Engine.Ask(reader, new RigsightSettings(), s, null, Now).Understood, s));
    }
}
