using System.Text.Json.Nodes;
using Rigsight.Core.Ai;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The chat ideas the person approved on 2026-10-23: the folded "Looked at" line, what a look-up says while it runs, sources
/// opening at their days, and Helpful / Not helpful (RigSight AI only).
/// </summary>
public sealed class AskIdeasTests
{
    private static AiLookUp Look(string tool, string? from = null, string? to = null)
    {
        var args = new JsonObject();
        if (from is not null) { args["from"] = from; args["to"] = to; }
        return new AiLookUp(tool, args, 500);
    }

    [Fact]
    public void The_folded_line_names_the_pages_read_and_how_long_it_took()
    {
        var answer = AskViewModel.ToAnswer("Fine.", [Look("get_crashes", "2026-10-23", "2026-10-23"), Look("get_temperatures", "2026-10-16", "2026-10-23")])
            with { Took = TimeSpan.FromSeconds(16.6) };
        Assert.Equal("Looked at Crashes and Temperatures · 17 s", answer.LookedLine);
        Assert.Equal(["Crashes, 23 Oct", "Temperatures, 16 Oct – 23 Oct"], answer.Looked.Select(l => l.Text));
        // An answer opened from History has no time: the line says only what it read.
        Assert.Equal("Looked at Crashes", AskViewModel.ToAnswer("Fine.", [Look("get_crashes", "2026-10-23", "2026-10-23")]).LookedLine);
    }

    [Fact]
    public void A_look_up_says_what_it_reads_and_for_which_days()
    {
        Assert.Equal("Reading your crashes for 16 Oct – 22 Oct…", Ollama.Reviewing("get_crashes", new JsonObject { ["from"] = "2026-10-16", ["to"] = "2026-10-22" }));
        Assert.Equal("Reading your drives…", Ollama.Reviewing("get_storage", []));
        Assert.Equal("Searching the web for \"rtx 3080 ti driver\"…", Ollama.Reviewing("search_web", new JsonObject { ["query"] = "rtx 3080 ti driver" }));
        // A long search in full, one "…" at the end: the line wraps if the window is narrow (it was cut with room to spare).
        const string longQuery = "RTX 3080 Ti hot spot temperature normal limit site:nvidia.com";
        Assert.Equal($"Searching the web for \"{longQuery}\"…", Ollama.Reviewing("search_web", new JsonObject { ["query"] = longQuery }));
    }

    [Theory]
    // Lines of an answer, or a paragraph of one, after something was looked up: kept, even when the model also asks to look
    // something up again (the answer that showed, vanished and came again, 2026-10-24).
    [InlineData("Your CPU peaked at 78° this week, well under AMD's 89° limit.\nYour GPU's hot spot reached 90°, which NVIDIA allows.", 3, true)]
    [InlineData("Your temperatures this week are normal for your parts: the Ryzen 7 5700X3D peaked at 78°, well under its 89° limit, and the RTX 3080 Ti stayed in its usual range.", 2, true)]
    // A lead-in before the look-ups: comes off the screen, as before, even on two lines or ending as a lead-in does.
    [InlineData("Let me check your temperatures.", 2, false)]
    [InlineData("Let me check your temperatures and crashes.\nOne moment.", 2, false)]
    [InlineData("I'll look at a few things first: your temperatures, your crashes this week and what changed before them, then the web for your exact parts:", 2, false)]
    [InlineData("Checking your drives and what filled them this week, then what Windows says about the space it keeps for itself on each one…", 1, false)]
    [InlineData("", 3, false)]
    // A first reply's words, before anything was looked up, are a lead-in however long.
    [InlineData("Your temperatures this week are normal for your parts: the Ryzen 7 5700X3D peaked at 78°, well under its 89° limit, and the RTX 3080 Ti stayed in its usual range.", 0, false)]
    public void Words_written_before_a_look_up_are_kept_when_they_are_an_answer(string text, int lookedUp, bool answer) => Assert.Equal(answer, OwnKey.IsAnswer(text, lookedUp));

    [Fact]
    public void A_source_carries_its_days_for_the_page_to_open_at()
    {
        var source = AiSources.Record(Look("get_temperatures", "2026-10-22", "2026-10-16"))!;
        Assert.Equal("2026-10-16..2026-10-22", source.Focus);
        Assert.Equal((new DateTime(2026, 10, 16), new DateTime(2026, 10, 22)), AiSources.ParseDays(source.Focus));
        Assert.Null(AiSources.Record(Look("get_storage"))!.Focus);
        Assert.Null(AiSources.ParseDays("23 Oct"));
    }

    [Fact]
    public void Only_an_answer_the_relay_knows_can_be_rated_and_Not_helpful_asks_why_first()
    {
        Assert.False(new AskFeedback(null).CanRate);
        var feedback = new AskFeedback("q-123");
        feedback.NotHelpfulCommand.Execute(null);
        Assert.True(feedback.Asking);
        Assert.True(feedback.IsNotHelpful);
        feedback.PickCommand.Execute("too_long");
        Assert.True(feedback.ReasonButtons.Single(b => b.Id == "too_long").IsPicked);
        // Pressed again, the reason goes: it's optional.
        feedback.PickCommand.Execute("too_long");
        Assert.Null(feedback.Reason);
    }

    [Fact]
    public void The_report_look_up_says_the_energy_used_and_what_it_leaves_out()
    {
        // "How much electricity did my PC use this week?" was answered "no record": the report had it, its text didn't.
        var report = new Rigsight.Core.Reports.Report
        {
            EnergyWh = 4200, CpuEnergyWh = 1500, GpuEnergyWh = 2700, PoweredMinutes = 30 * 60,
            GpuPowerPeak = new(312, new DateTime(2026, 10, 21, 21, 5, 0), "Crimson Desert"),
        };
        string text = AiTools.Energy(report, new DateTime(2026, 10, 17), new DateTime(2026, 10, 23));
        Assert.Contains("4.20 kWh over 30 hours on, about 600 Wh a day; on average 140 W while on.", text);
        Assert.Contains("CPU 1.50 kWh, graphics card 2.70 kWh.", text);
        Assert.Contains("312 W", text);
        Assert.Contains("the PC at the wall uses more", text);
        // Nothing read: nothing said (no "0 kWh").
        Assert.Equal("", AiTools.Energy(new Rigsight.Core.Reports.Report(), new DateTime(2026, 10, 17), new DateTime(2026, 10, 23)));
    }

    [Fact]
    public void The_first_answer_names_the_conversation_and_its_title_line_never_shows()
    {
        var answer = AskViewModel.ToAnswer("It shut off because the power went.\nAsk next: Was it the same last week?\nTitle: Sudden shut-off at 4:44 PM", []);
        Assert.Equal("Sudden shut-off at 4:44 PM", answer.Title);
        Assert.DoesNotContain(answer.Parts, p => p is AskLine l && l.Text.Contains("Title", StringComparison.Ordinal));
        Assert.Null(AskViewModel.ToAnswer("Fine.", []).Title);
    }

    [Fact]
    public void A_next_step_written_inside_a_paragraph_gets_its_card()
    {
        // The shut-off answer of 2026-10-23: "…one shut-off isn't enough to blame it. Next step: check whether…".
        var answer = AskViewModel.ToAnswer("It was the mains.\nOne shut-off isn't enough to blame the power supply. Next step: check whether the router lost power too.", []);
        var next = Assert.Single(answer.Parts.OfType<AskNext>());
        Assert.Equal("check whether the router lost power too.", next.Text, ignoreCase: true);
        Assert.DoesNotContain(answer.Parts, p => p is AskLine l && l.Text.Contains("Next step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_scan_button_is_only_for_a_drive_that_is_there()
    {
        var was = AiActions.DriveReady;
        try
        {
            AiActions.DriveReady = letter => letter == "E";
            var ctx = new AiActionContext([], null, () => [], true);
            var scan = Assert.Single(AiActions.Resolve(["scan E"], ctx));
            Assert.Equal((AiActionKind.ScanDrive, "Scan the E drive", "E"), (scan.Kind, scan.Label, scan.Target));
            Assert.Empty(AiActions.Resolve(["scan Q"], ctx));
            Assert.Empty(AiActions.Resolve(["scan everything"], ctx));
        }
        finally { AiActions.DriveReady = was; }
    }

    [Fact]
    public void A_drive_scan_is_said_without_the_user_name_or_file_names()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = new Rigsight.Services.FolderNode { Name = "E:", Path = @"E:\", Size = 1000L << 30 };
        var games = new Rigsight.Services.FolderNode { Name = "Games", Path = @"E:\Games", Size = 600L << 30, Files = 900, Parent = root };
        games.Children.Add(new Rigsight.Services.FolderNode { Name = "SteamLibrary", Path = @"E:\Games\SteamLibrary", Size = 580L << 30, Parent = games });
        var mine = new Rigsight.Services.FolderNode { Name = "Downloads", Path = System.IO.Path.Combine(home, "Downloads"), Size = 200L << 30, Files = 40, Parent = root };
        root.Children.AddRange([games, mine]);
        var result = new Rigsight.Services.ScanResult
        {
            Root = root, FileCount = 12345, Elapsed = TimeSpan.FromSeconds(30),
            LargestFiles = [new(System.IO.Path.Combine(home, "Downloads", "my private video.mkv"), 40L << 30)],
        };
        string text = Rigsight.Services.DriveScans.Describe("E", result, DateTime.Now, DateTime.Now, [("Temporary files", "Leftovers.", 3L << 30)]);
        Assert.Contains("Scan of E: finished just now", text);
        Assert.Contains(@"E:\Games\SteamLibrary", text);
        Assert.Contains(@"your user folder\Downloads", text);
        Assert.Contains("a .mkv file in your user folder", text);
        Assert.Contains("Temporary files: 3", text);
        Assert.DoesNotContain("my private video", text);
        Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_drive_scan_is_read_again_each_time_never_from_an_earlier_look()
    {
        string? scan = null;
        var tools = new AiTools(new Rigsight.Core.Settings.RigsightSettings()) { PlanPages = null, DriveScan = _ => scan };
        Assert.Contains("hasn't been scanned", tools.Run("get_drive_scan", """{"drive":"E"}"""));
        scan = "Scan of E: finished just now";
        Assert.Equal("Scan of E: finished just now", tools.Run("get_drive_scan", """{"drive":"E"}"""));
    }
}
