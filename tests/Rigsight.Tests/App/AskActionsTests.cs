using System.Text.Json.Nodes;
using Rigsight.Core.Ai;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The buttons under an answer (the 2026-10-22 audit): only from the fixed list, each checked against what the answer
/// looked at and what is really there; Close asks first and never prompts for admin; Check again compares like with like
/// from the records.
/// </summary>
public sealed class AskActionsTests
{
    private static readonly AiLookUp RunningLook = new("get_running_now", new JsonObject(), 900);
    private static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static RunningSnapshot Running() => new(DateTime.Now,
    [
        new("python.exe", "Python", @"C:\Python312\python.exe", 3, 13_200, 4, 0, false, false, AppCategory.Development),
        new("chrome.exe", "Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", 31, 2_900, 3, 1, true, true, AppCategory.Browser),
        new("cod.exe", "Call of Duty", @"D:\Games\cod.exe", 1, 6_600, 30, 92, true, false, AppCategory.Game),
        new("svchost.exe", "Service Host", Win + @"\System32\svchost.exe", 80, 900, 2, 0, false, false, AppCategory.System),
        new("llama-server.exe", "Llama Server", @"C:\AI\llama-server.exe", 1, 7_500, 12, 56, false, false, AppCategory.Development),
    ]);

    private static readonly InstalledGame Dota = new() { Key = "steam:570", Launcher = Launcher.Steam, Name = "Dota 2", InstallDir = @"D:\Steam\dota 2 beta" };
    private static readonly InstalledGame Fortnite = new() { Key = "epic:Fortnite", Launcher = Launcher.Epic, Name = "Fortnite", InstallDir = @"D:\Epic\Fortnite" };

    private static AiActionContext Context(bool looked = true, bool agent = true) =>
        new(looked ? [RunningLook] : [], Running(), () => [Dota, Fortnite], agent);

    private static IReadOnlyList<AiAction> Resolve(bool looked = true, bool agent = true, params string[] markers) => AiActions.Resolve(markers, Context(looked, agent));

    [Fact]
    public void Close_is_offered_only_for_an_ordinary_app_this_answer_saw_running()
    {
        var close = Assert.Single(Resolve(true, true, "close python.exe"));
        Assert.Equal(AiActionKind.CloseApp, close.Kind);
        Assert.Equal("python.exe", close.Target);
        Assert.Equal("Close Python", close.Label);
        Assert.Equal(13_200, close.MemMb);
        // By its name too, as models sometimes write it.
        Assert.Single(Resolve(true, true, "close Llama Server"));
    }

    [Theory]
    [InlineData("close notepad.exe")]        // not running: a made-up or planted name runs nothing
    [InlineData("close svchost.exe")]        // part of Windows
    [InlineData("close cod.exe")]            // a game: maybe the one being played
    [InlineData("close chrome.exe")]         // the app in front
    [InlineData("close Rigsight.exe")]
    [InlineData("rm -rf C:\\")]              // not on the list at all
    [InlineData("run powershell.exe -c whatever")]
    public void Nothing_off_the_list_or_unsafe_becomes_a_button(string marker) => Assert.Empty(Resolve(true, true, marker));

    [Fact]
    public void Close_needs_this_answer_to_have_looked_at_what_is_running()
    {
        Assert.Empty(Resolve(looked: false, markers: "close python.exe"));
    }

    [Theory]
    [InlineData("check 10 fans", 10, CheckWhat.Fans)]
    [InlineData("check 12 temps", 10, CheckWhat.Temps)]
    [InlineData("check 2 gpu", 5, CheckWhat.Gpu)]
    [InlineData("check 500 memory", 60, CheckWhat.Memory)]
    [InlineData("check fans", 10, CheckWhat.Fans)]
    public void Check_again_keeps_to_5_to_60_minutes_in_fives(string marker, int minutes, CheckWhat what)
    {
        var check = Assert.Single(Resolve(true, true, marker));
        Assert.Equal((AiActionKind.CheckAgain, minutes, what), (check.Kind, check.Minutes, check.What));
        Assert.Equal($"Check again in {minutes} min", check.Label);
    }

    [Fact]
    public void Check_again_on_something_RigSight_doesnt_record_is_dropped() => Assert.Empty(Resolve(true, true, "check 10 noise"));

    [Fact]
    public void Pages_open_at_the_right_spot()
    {
        var all = Resolve(true, true, "open storage I", "open games dota", "open processes memory");
        Assert.Equal(("storage", "I", "Open Storage on the I drive"), (all[0].Target, all[0].Focus, all[0].Label));
        Assert.Equal(("games", "steam:570", "Open Dota 2"), (all[1].Target, all[1].Focus, all[1].Label));
        Assert.Equal(("processes", "memory"), (all[2].Target, all[2].Focus));
        Assert.Empty(Resolve(true, true, "open registry"));
    }

    [Fact]
    public void Windows_screens_are_only_the_listed_ones_and_only_open()
    {
        var sense = Assert.Single(Resolve(true, true, "windows storage-sense"));
        Assert.Equal(("ms-settings:storagesense", "Open Storage Sense"), (sense.Target, sense.Label));
        Assert.Equal("ms-settings:windowsupdate-history", Assert.Single(Resolve(true, true, "windows update history")).Target);
        Assert.Empty(Resolve(true, true, "windows regedit"));
    }

    [Fact]
    public void Verify_files_is_for_installed_Steam_games_only()
    {
        var verify = Assert.Single(Resolve(true, true, "verify Dota 2"));
        Assert.Equal(("steam://validate/570", "Verify Dota 2's files"), (verify.Target, verify.Label));
        Assert.Empty(Resolve(true, true, "verify Fortnite"));
        Assert.Empty(Resolve(true, true, "verify Half-Life 3"));
    }

    [Fact]
    public void Start_the_agent_only_when_it_isnt_running()
    {
        Assert.Empty(Resolve(true, agent: true, markers: "start-agent"));
        Assert.Equal(AiActionKind.StartAgent, Assert.Single(Resolve(true, agent: false, markers: "start-agent")).Kind);
    }

    [Fact]
    public void At_most_three_and_never_the_same_twice()
    {
        var all = Resolve(true, true, "close python.exe", "close Python", "check 10 memory", "open memory", "windows startup-apps");
        Assert.Equal([AiActionKind.CloseApp, AiActionKind.CheckAgain, AiActionKind.OpenPage], all.Select(a => a.Kind));
    }

    // ── Reading the answer ───────────────────────────────────────────────
    [Fact]
    public void Do_lines_become_buttons_and_never_show_as_words()
    {
        var seen = new List<string>();
        var answer = AskViewModel.ToAnswer("Python is holding most of your memory.\nIt has 13 GB right now. [do: check 10 memory]\nNext step: close Python, then check again.\n[do: close python.exe]\nAsk next: Why does Python use so much?",
            [RunningLook], actions: m => { seen.AddRange(m); return AiActions.Resolve(m, Context()); });
        Assert.Equal(["check 10 memory", "close python.exe"], seen);
        Assert.DoesNotContain(answer.Parts, p => p is AskLine l && l.Text.Contains("[do", StringComparison.Ordinal));
        // In the Next step box: the step, then its buttons, the first the main one.
        var next = Assert.IsType<AskNext>(Assert.Single(answer.Parts, p => p is AskNext));
        // Close first (the card's own button), whatever order the model wrote them in.
        Assert.Equal(["Close Python", "Check again in 10 min"], next.Actions.Select(a => a.Label));
        Assert.Equal("Holds 12.9 GB with no window open", next.CardText);
        Assert.Equal([AskActionRole.Main, AskActionRole.Second], next.Actions.Select(a => a.Role));
        Assert.False(next.CanOpen);
        Assert.Equal(["Why does Python use so much?"], answer.Suggestions);
    }

    [Fact]
    public void An_answer_opened_again_shows_no_buttons()
    {
        var answer = AskViewModel.ToAnswer("Python is holding most of your memory.\nIt has 13 GB.\n[do: close python.exe]", [RunningLook]);
        Assert.DoesNotContain(answer.Parts, p => p is AskNext { HasActions: true });
        Assert.DoesNotContain(answer.Parts, p => p is AskLine l && l.Text.Contains("[do", StringComparison.Ordinal));
    }

    // ── Closing ──────────────────────────────────────────────────────────
    [Fact]
    public void Close_asks_the_app_first_and_ends_it_only_if_it_stays()
    {
        var running = new List<int> { 11, 12 };
        var asked = new List<int>();
        var polite = new AppCloser { Grace = TimeSpan.FromMilliseconds(600), PidsOf = _ => [.. running], AskToClose = pid => { asked.Add(pid); running.Remove(pid); return true; } };
        Assert.Equal(CloseOutcome.Closed, polite.Close("python.exe"));
        Assert.Equal([11, 12], asked);

        // Asked and still open (a "Save changes?" box, maybe): left for the person, never ended by force.
        var stubborn = new List<int> { 21 };
        bool killed = false;
        var ender = new TaskEnder { PidsOf = _ => [.. stubborn], Kill = (pid, _) => { killed = true; stubborn.Remove(pid); return KillResult.Ended; }, IsRunning = (pid, _) => stubborn.Contains(pid) };
        var asking = new AppCloser { Grace = TimeSpan.FromMilliseconds(300), PidsOf = _ => [.. stubborn], AskToClose = _ => true, Ender = ender };
        Assert.Equal(CloseOutcome.StillOpen, asking.Close("notepad.exe"));
        Assert.False(killed);

        // No window to ask: ended, as End task does.
        var windowless = new AppCloser { Grace = TimeSpan.FromMilliseconds(300), PidsOf = _ => [.. stubborn], AskToClose = _ => false, Ender = ender };
        Assert.Equal(CloseOutcome.Ended, windowless.Close("datacruncher.exe"));
        Assert.True(killed);
    }

    [Fact]
    public void Close_never_brings_up_an_admin_prompt()
    {
        bool prompted = false;
        var pids = new List<int> { 31 };
        var ender = new TaskEnder { PidsOf = _ => [.. pids], Kill = (_, _) => KillResult.Denied, IsRunning = (_, _) => true, Elevate = _ => { prompted = true; return Elevation.Cancelled; } };
        var closer = new AppCloser { Grace = TimeSpan.FromMilliseconds(100), PidsOf = _ => [.. pids], AskToClose = _ => false };
        // The closer's own ender (no elevation) is what answers use; this one only proves the default path never prompts.
        Assert.Equal(CloseOutcome.NeedsAdmin, new AppCloser { Grace = closer.Grace, PidsOf = closer.PidsOf, AskToClose = closer.AskToClose, Ender = new TaskEnder { PidsOf = ender.PidsOf, Kill = ender.Kill, IsRunning = ender.IsRunning, Elevate = _ => Elevation.Cancelled } }.Close("admin.exe"));
        Assert.False(prompted);
        Assert.Equal(Elevation.Cancelled, new AppCloser().Ender.Elevate([(1, "x.exe")]));
    }

    [Fact]
    public void Close_asks_first_then_says_what_happened_and_puts_it_in_the_conversation()
    {
        Ui.Run(() =>
        {
            var window = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
            var talk = new AskConversation { Title = "t" };
            window.Conversations.Add(talk);
            window.Current = talk;
            var sent = new List<(string, string?)>();
            window.SendAgent = (c, a) => sent.Add((c, a));
            var gone = new List<int> { 5 };
            window.Closer = new AppCloser { Grace = TimeSpan.FromMilliseconds(300), PidsOf = _ => [.. gone], AskToClose = pid => gone.Remove(pid) };
            var button = new AskAction(Resolve(true, true, "close python.exe")[0]);

            window.ActCommand.Execute(button);
            Assert.True(button.IsConfirming);
            window.CancelActCommand.Execute(button);
            Assert.True(button.IsReady);

            window.ActCommand.Execute(button);
            window.ConfirmActCommand.ExecuteAsync(button).GetAwaiter();
            Ui.Pump(1200);
            Assert.True(button.IsDone, button.Said);
            Assert.StartsWith("Closed Python at ", button.Said);
            Assert.Contains("12.9 GB freed", button.Said);
            Assert.IsType<AskDone>(talk.Messages[^1]);
            Assert.Equal("task-ended", Assert.Single(sent).Item1);
        });
    }

    // ── Check again ──────────────────────────────────────────────────────
    private static string HistoryWith(Func<int, double> rpm, Func<int, bool>? gameAt = null, int from = -10, int to = 10)
    {
        string path = Path.Combine(Path.GetTempPath(), $"check-{Guid.NewGuid():N}.db");
        using var db = RigsightDb.OpenWriter(path);
        long fan = db.FanId("/lpc/fan2", "Fan #2", "Motherboard");
        long game = db.UpsertApp("cod.exe", "Call of Duty", null, AppCategory.Game), editor = db.UpsertApp("code.exe", "VS Code", null, AppCategory.Development);
        long at = TimeUtil.ToUnix(Pressed);
        for (int i = from; i < to; i++)
        {
            long ts = at + i * 60;
            db.WriteMinute(new SystemMinute { Ts = ts, CpuTemp = 50, GpuTemp = i < 0 ? 62 : 55, GpuLoad = i < 0 ? 56 : 4, RamUsed = i < 0 ? 28 : 16, FgApp = gameAt?.Invoke(i) == true ? game : editor });
            db.WriteFanMinutes(ts, [(fan, (int)rpm(i), (int)rpm(i))]);
        }
        return path;
    }

    private static readonly DateTime Pressed = new(2026, 10, 22, 21, 0, 0);

    [Fact]
    public void Check_again_compares_the_minutes_either_side()
    {
        using var db = RigsightDb.OpenReader(HistoryWith(i => i < 0 ? 1650 : 1100))!;
        Assert.Null(ActionCheck.Compare(db, Pressed, 10, CheckWhat.Fans, now: Pressed.AddMinutes(9)));
        var fans = ActionCheck.Compare(db, Pressed, 10, CheckWhat.Fans, now: Pressed.AddMinutes(10))!;
        var row = Assert.Single(fans.Rows);
        Assert.Equal(("Fan #2", "1,650 rpm", "1,100 rpm", "550 rpm lower", (bool?)true), (row.Label, row.Before, row.After, row.Change, row.Better));
        Assert.True(fans.Fair);

        var gpu = ActionCheck.Compare(db, Pressed, 10, CheckWhat.Gpu, now: Pressed.AddHours(1))!;
        Assert.Equal(["Graphics card busy", "GPU temperature"], gpu.Rows.Select(r => r.Label));
        Assert.Equal(("56%", "4%"), (gpu.Rows[0].Before, gpu.Rows[0].After));
        var memory = ActionCheck.Compare(db, Pressed, 10, CheckWhat.Memory, now: Pressed.AddHours(1))!;
        Assert.Equal(("28.0 GB", "16.0 GB", "12.0 GB lower"), (memory.Rows[0].Before, memory.Rows[0].After, memory.Rows[0].Change));
    }

    [Fact]
    public void A_small_wobble_is_about_the_same()
    {
        using var db = RigsightDb.OpenReader(HistoryWith(i => i < 0 ? 1200 : 1180))!;
        var row = Assert.Single(ActionCheck.Compare(db, Pressed, 10, CheckWhat.Fans, now: Pressed.AddHours(1))!.Rows);
        Assert.Equal(("about the same", (bool?)null), (row.Change, row.Better));
    }

    [Fact]
    public void A_game_on_one_side_only_isnt_like_for_like()
    {
        using var db = RigsightDb.OpenReader(HistoryWith(_ => 1500, gameAt: i => i < 0))!;
        var result = ActionCheck.Compare(db, Pressed, 10, CheckWhat.Fans, now: Pressed.AddHours(1))!;
        Assert.False(result.Fair);
        Assert.Contains("like-for-like", result.Note);
    }

    [Fact]
    public void Too_little_recorded_says_so_instead_of_guessing()
    {
        using var db = RigsightDb.OpenReader(HistoryWith(_ => 1500, to: 2))!;
        var result = ActionCheck.Compare(db, Pressed, 10, CheckWhat.Fans, now: Pressed.AddHours(1))!;
        Assert.False(result.HasRows);
        Assert.Contains("Not enough was recorded", result.Note);
    }

    [Fact]
    public void A_check_is_saved_and_compared_when_the_conversation_is_opened_again()
    {
        var check = new AskCheck(Pressed, 10, CheckWhat.Fans);
        var back = AskCheck.FromSaved(check.Saved)!;
        Assert.Equal((Pressed, 10, CheckWhat.Fans), (back.At, back.Minutes, back.What));
        Assert.Null(AskCheck.FromSaved("nonsense"));

        Ui.Run(() =>
        {
            var window = new AskViewModel(Kit.OfflineSettings(new RigsightSettings())) { CheckDb = HistoryWith(i => i < 0 ? 1650 : 1100) };
            window.CompareAsync(back).GetAwaiter();
            Ui.Pump(1500);
            Assert.True(back.HasResult);
            Assert.Equal("1,100 rpm", back.Rows[0].After);
        });
    }

    // A side agent's catch (2026-10-23): pressed after Close, Check again compared two stretches both after the close
    // ("about the same"). It measures from the close now, whichever button came first.
    private static (AskViewModel Window, AskConversation Talk, AskAction Close, AskAction Check) CloseAndCheck()
    {
        var window = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
        var talk = new AskConversation { Title = "t" };
        window.Conversations.Add(talk);
        window.Current = talk;
        var gone = new List<int> { 5 };
        window.Closer = new AppCloser { Grace = TimeSpan.FromMilliseconds(300), PidsOf = _ => [.. gone], AskToClose = pid => gone.Remove(pid) };
        var answer = AskViewModel.ToAnswer("Memory is full.\nDataCruncher holds 13 GB.\nNext step: close it.\n[do: close python.exe]\n[do: check 10 memory]", [RunningLook],
            actions: m => AiActions.Resolve(m, Context()));
        talk.Messages.Add(answer);
        var buttons = answer.Parts.OfType<AskNext>().Single().Actions;
        return (window, talk, buttons[0], buttons[1]);
    }

    [Fact]
    public void Check_again_pressed_after_Close_measures_from_the_close()
    {
        Ui.Run(() =>
        {
            var (window, talk, close, check) = CloseAndCheck();
            window.ActCommand.Execute(close);
            window.ConfirmActCommand.ExecuteAsync(close).GetAwaiter();
            Ui.Pump(1200);
            var closedAt = close.ClosedAt!.Value;
            close.ClosedAt = closedAt.AddMinutes(-7); // pressed 7 minutes after the close
            window.ActCommand.Execute(check);
            var started = talk.Messages.OfType<AskCheck>().Single();
            Assert.Equal(closedAt.AddMinutes(-7), started.At);
            Assert.Equal("Python", started.Closed);
            Assert.StartsWith("10 minutes before Python closed at ", started.Detail);
        });
    }

    [Fact]
    public void A_check_pressed_before_Close_moves_its_middle_to_the_close()
    {
        Ui.Run(() =>
        {
            var (window, talk, close, check) = CloseAndCheck();
            window.ActCommand.Execute(check);
            var early = talk.Messages.OfType<AskCheck>().Single();
            Assert.Null(early.Closed);
            window.ActCommand.Execute(close);
            window.ConfirmActCommand.ExecuteAsync(close).GetAwaiter();
            Ui.Pump(1200);
            var moved = talk.Messages.OfType<AskCheck>().Single();
            Assert.Equal(close.ClosedAt, moved.At);
            Assert.Equal("Python", moved.Closed);
            Assert.Same(moved, check.Started);
            // Saved and opened again with the app's name.
            Assert.Equal("Python", AskCheck.FromSaved(moved.Saved)!.Closed);
        });
    }

    // ── Running now ──────────────────────────────────────────────────────
    [Fact]
    public void Running_now_tells_the_model_what_may_be_closed()
    {
        string text = Running().Text();
        Assert.Contains("Python (python.exe, 3 processes): memory 12.9 GB", text);
        Assert.Contains("can be closed from an answer", text.Split('\n').Single(l => l.Contains("python.exe")));
        Assert.DoesNotContain("can be closed", text.Split('\n').Single(l => l.Contains("cod.exe")));
        Assert.Contains("part of Windows, never closed from here", text);
        Assert.Contains("the app in front", text);
    }

    /// <summary>
    /// Each app's usual beside its memory now, the Memory page's own figure (2026-10-30: the page said 356 MB, the AI's
    /// week average 414 MB, and the answer called the page's number missing from the records).
    /// </summary>
    [Fact]
    public void Running_now_gives_each_app_its_usual_as_the_Memory_page_does()
    {
        string text = Running().Text(new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["PYTHON.exe"] = 4_100 });
        Assert.Contains("Python (python.exe, 3 processes): memory 12.9 GB (usual 4.0 GB)", text);
        Assert.DoesNotContain("usual", text.Split('\n').Single(l => l.Contains("chrome.exe")));
        Assert.Contains("the figure the Memory page shows", text);
        Assert.DoesNotContain("the figure the Memory page shows", Running().Text());
    }

    [Fact]
    public void Running_now_is_looked_at_again_every_time()
    {
        int looks = 0;
        var tools = new AiTools(Kit.OfflineSettings(new RigsightSettings()).Current) { SampleRunning = () => { looks++; return Running(); } };
        tools.Run("get_running_now", "{}");
        tools.Run("get_running_now", "{}");
        Assert.Equal(2, looks);
        Assert.NotNull(tools.LastRunning);
    }

    [Fact]
    public void The_real_sampler_sees_this_test_running()
    {
        var snapshot = RunningNow.Sample(200);
        Assert.Contains(snapshot.Apps, a => a.Exe.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) || a.Exe.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) || a.Exe.StartsWith("Rigsight.Tests", StringComparison.OrdinalIgnoreCase));
        Assert.All(snapshot.Apps, a => Assert.InRange(a.Cpu, 0, 100));
    }

    [Fact]
    public void Own_key_and_Ollama_prompts_carry_the_button_rules()
    {
        Assert.Contains("[do: close <exe>]", AiPrompt.Actions);
        Assert.Contains("never offer to close the game being played", AiPrompt.Actions);
    }

    // 2026-10-24, the person's own I drive: Scan ran, then the answer said "hasn't been scanned". The new chat's starters
    // had made the look-ups before the shell set their hooks, so a drive's scan was never read.
    [Fact]
    public void Hooks_set_after_the_look_ups_were_made_still_reach_them()
    {
        Ui.Run(() =>
        {
            var window = new AskViewModel(Kit.OfflineSettings(new RigsightSettings()));
            var tools = (AiTools)typeof(AskViewModel).GetMethod("Tools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null)!;
            tools.PlanPages = null;
            window.ConfigureTools = t => t.DriveScan = letter => letter == "E" ? "Scan of E: finished just now." : null;
            Assert.StartsWith("Scan of E:", tools.Run("get_drive_scan", """{"drive":"E"}"""));
        });
    }
}
