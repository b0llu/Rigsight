using System.Text.Json.Nodes;
using Rigsight.Core;
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
/// The chat buttons of 2026-10-24 (the approved artifact): apps on the PC opened by their Start menu id, the new Windows
/// screens, no Scan for a page the plan or Settings › AI keeps from the answers, the drive scan as one card that changes in
/// place under the answer that stays, Check again's before side at once.
/// </summary>
// With the Storage and drive scan tests: the drive scans are kept in one place for the whole app (a scan here leaked into theirs).
[Collection("UI")]
public sealed class AskButtonsTests
{
    private static readonly StartApp[] Start =
    [
        new("NVIDIA App", "com.nvidia.nvapp"), new("Steam", @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Steam\Steam.exe"),
        new("Uninstall CPU-Z", @"{6D809377-6AF0-444B-8957-A3773F02200E}\CPUID\CPU-Z\unins000.exe"),
        new("Reset settings", @"{6D809377-6AF0-444B-8957-A3773F02200E}\Cheat Engine\ceregreset.exe"),
        new("Logitech G HUB", @"{6D809377-6AF0-444B-8957-A3773F02200E}\LGHUB\lghub.exe"),
    ];
    private static readonly InstalledGame Dota = new() { Key = "steam:570", Launcher = Launcher.Steam, Name = "Dota 2", InstallDir = @"D:\Steam\dota 2 beta" };

    private static AiActionContext Context(Func<string, bool>? canRead = null) =>
        new([], null, () => [Dota], true, canRead, () => OpenableApps.Installed(Start));

    [Fact]
    public void Only_apps_on_the_fixed_list_count_as_installed_and_never_an_uninstaller_beside_them()
    {
        var found = OpenableApps.Installed(Start);
        Assert.Equal(["nvidia-app", "steam", "g-hub"], found.Keys.OrderBy(k => k == "nvidia-app" ? 0 : k == "steam" ? 1 : 2));
        Assert.False(found.ContainsKey("cpu-z"));
        Assert.Equal("com.nvidia.nvapp", found["nvidia-app"].AppId);
    }

    [Fact]
    public void An_installed_app_opens_by_its_start_menu_id_and_one_that_isnt_offers_its_makers_page()
    {
        var open = Assert.Single(AiActions.Resolve(["app nvidia-app"], Context()));
        Assert.Equal((AiActionKind.OpenApp, "Open NVIDIA App", @"shell:AppsFolder\com.nvidia.nvapp"), (open.Kind, open.Label, open.Target));

        var get = Assert.Single(AiActions.Resolve(["app amd-software"], Context()));
        Assert.Equal((AiActionKind.OpenWeb, "Get AMD Software"), (get.Kind, get.Label));
        Assert.StartsWith("https://www.amd.com/", get.Target);

        // Not installed and no download page on the list: nothing. Off the list, or a path: nothing.
        Assert.Empty(AiActions.Resolve(["app msi-center"], Context()));
        Assert.Empty(AiActions.Resolve(["app cmd.exe", @"app C:\Windows\System32\cmd.exe", "app uninstall-cpu-z"], Context()));
    }

    // The person's call (2026-10-25): any other tool by its Start menu name; the list stays for its download links.
    private static readonly StartApp[] More =
    [
        .. Start, new("MSI Center", @"{6D809377-6AF0-444B-8957-A3773F02200E}\MSI\One Dragon Center\MSI_Central.exe"),
        new("Uninstall MSI Center", @"{6D809377-6AF0-444B-8957-A3773F02200E}\MSI\One Dragon Center\unins000.exe"),
        new("Gigabyte Control Center", @"{6D809377-6AF0-444B-8957-A3773F02200E}\GIGABYTE\Control Center\GCC.exe"),
        new("Gigabyte Control Center Help", @"{6D809377-6AF0-444B-8957-A3773F02200E}\GIGABYTE\Control Center\help.chm"),
        new("Command Prompt", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\cmd.exe"),
        new("Registry Editor", @"C:\Windows\regedit.exe"),
        new("Fan Control", @"{6D809377-6AF0-444B-8957-A3773F02200E}\FanControl\FanControl.exe"),
        new("Fan Control Settings", @"{6D809377-6AF0-444B-8957-A3773F02200E}\FanControl\config.ini"),
    ];

    [Theory]
    [InlineData("MSI Center", "MSI Center")]
    [InlineData("msi center", "MSI Center")]
    [InlineData("Gigabyte Control Center", "Gigabyte Control Center")]
    [InlineData("Fan Control", "Fan Control")]
    public void Another_tool_opens_by_its_start_menu_name(string named, string opens)
    {
        var ctx = Context() with { StartMenu = () => More };
        var b = Assert.Single(AiActions.Resolve([$"app {named}"], ctx));
        Assert.Equal((AiActionKind.OpenApp, $"Open {opens}"), (b.Kind, b.Label));
        Assert.StartsWith(@"shell:AppsFolder\", b.Target);
    }

    [Theory]
    [InlineData("Uninstall MSI Center")]
    [InlineData("Gigabyte Control Center Help")]
    [InlineData("Command Prompt")]
    [InlineData("Registry Editor")]
    [InlineData("Fan Control Settings")]
    [InlineData("Some Tool Not Here")]
    [InlineData("Ce")]
    public void Never_an_uninstaller_a_help_file_a_windows_tool_or_something_not_here(string named) =>
        Assert.Empty(AiActions.Resolve([$"app {named}"], Context() with { StartMenu = () => More }));

    // "They should be dynamic" (2026-10-25): other PC tools on this PC reach the AI by name; the rest of the Start menu never does.
    [Fact]
    public void Other_pc_tools_on_this_pc_are_listed_by_name_and_nothing_else()
    {
        StartApp[] start = [.. More, new("NVIDIA Broadcast", @"{6D809377-6AF0-444B-8957-A3773F02200E}\NVIDIA Corporation\NVIDIA Broadcast\NVIDIA Broadcast.exe"),
            new("Spotify", @"SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"), new("Cheat Engine", @"{6D809377-6AF0-444B-8957-A3773F02200E}\Cheat Engine\cheatengine.exe")];
        var others = OpenableApps.OtherTools(start).Select(o => o.Name).ToList();
        Assert.Contains("NVIDIA Broadcast", others);
        Assert.DoesNotContain("MSI Center", others); // on the list too (msi-center)
        Assert.Contains("Gigabyte Control Center", others);
        Assert.DoesNotContain("Spotify", others);
        Assert.DoesNotContain("Cheat Engine", others);
        Assert.DoesNotContain("Uninstall MSI Center", others);
        Assert.DoesNotContain("NVIDIA App", others); // on the list: reported there, not twice
        var tools = new AiTools(new RigsightSettings(), Path.Combine(Path.GetTempPath(), "rigsight-no-such.db"))
            { OpenableAppsLookUp = true, OpenableOnPc = () => OpenableApps.Installed(start), OtherOnPc = () => OpenableApps.OtherTools(start) };
        string text = tools.Run(AiTools.AppsLookUp, "{}");
        Assert.Contains("NVIDIA Broadcast ([do: app NVIDIA Broadcast])", text);
        Assert.DoesNotContain("Spotify", text);
    }

    // A person kept pressing "Open RivaTuner Statistics Server" and nothing showed (2026-10-27): it starts hidden in the tray.
    [Theory]
    [InlineData("RivaTuner Statistics Server")]
    [InlineData("rivatuner")]
    [InlineData("RTSS")]
    public void RivaTuner_opens_the_overlay_page_and_is_never_listed_as_a_tool(string named)
    {
        StartApp[] start = [.. More, new("RivaTuner Statistics Server", @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\RivaTuner Statistics Server\RTSS.exe")];
        var b = Assert.Single(AiActions.Resolve([$"app {named}"], Context() with { StartMenu = () => start }));
        Assert.Equal((AiActionKind.OpenPage, "Open Overlay", "overlay"), (b.Kind, b.Label, b.Target));
        Assert.DoesNotContain(OpenableApps.OtherTools(start), s => s.Name.Contains("RivaTuner"));
    }

    [Fact]
    public void A_name_that_fits_two_entries_opens_neither()
    {
        StartApp[] two = [new("Armory Tool", @"{6D809377-6AF0-444B-8957-A3773F02200E}\A\a.exe"), new("Armory Tool", @"{6D809377-6AF0-444B-8957-A3773F02200E}\B\b.exe")];
        Assert.Empty(AiActions.Resolve(["app Armory Tool"], Context() with { StartMenu = () => two }));
    }

    [Fact]
    public void A_steam_game_opens_at_its_own_page_in_steam()
    {
        var game = Assert.Single(AiActions.Resolve(["app steam Dota 2"], Context()));
        Assert.Equal(("Open Dota 2 in Steam", "steam://nav/games/details/570"), (game.Label, game.Target));
    }

    [Theory]
    [InlineData("windows windows-update", "ms-settings:windowsupdate")]
    [InlineData("windows optional-updates", "ms-settings:windowsupdate-optionalupdates")]
    [InlineData("windows device-manager", "devmgmt.msc")]
    [InlineData("windows graphics-settings", "ms-settings:display-advancedgraphics")]
    [InlineData("windows game-mode", "ms-settings:gaming-gamemode")]
    [InlineData("windows task-manager", "taskmgr.exe")]
    public void The_new_windows_screens_open_and_change_nothing(string marker, string target) =>
        Assert.Equal(target, Assert.Single(AiActions.Resolve([marker], Context())).Target);

    // The person's I drive on Free (2026-10-24): "Scan the I drive" under "Storage isn't in your plan".
    [Fact]
    public void No_scan_when_the_answers_cant_read_storage_but_opening_the_page_still_works()
    {
        var old = AiActions.DriveReady;
        AiActions.DriveReady = _ => true;
        try
        {
            Assert.Single(AiActions.Resolve(["scan E"], Context(_ => true)));
            var locked = AiActions.Resolve(["scan E", "open storage E"], Context(page => page != AiPages.Storage));
            var only = Assert.Single(locked);
            Assert.Equal((AiActionKind.OpenPage, "Open Storage on the E drive"), (only.Kind, only.Label));
        }
        finally { AiActions.DriveReady = old; }
    }

    [Fact]
    public void A_drive_that_isnt_there_gets_no_scan()
    {
        var old = AiActions.DriveReady;
        AiActions.DriveReady = letter => letter != "I";
        try { Assert.Empty(AiActions.Resolve(["scan I"], Context(_ => true))); }
        finally { AiActions.DriveReady = old; }
    }

    [Fact]
    public void The_apps_look_up_lists_whats_here_and_follows_the_apps_switch()
    {
        var settings = new RigsightSettings();
        var tools = new AiTools(settings, Path.Combine(Path.GetTempPath(), "rigsight-no-such.db")) { OpenableAppsLookUp = true, OpenableOnPc = () => OpenableApps.Installed(Start) };
        Assert.Contains(tools.Definitions(), d => (string?)d?["function"]?["name"] == AiTools.AppsLookUp);
        string text = tools.Run(AiTools.AppsLookUp, "{}");
        Assert.Contains("NVIDIA App ([do: app nvidia-app])", text);
        Assert.Contains("Logitech G HUB ([do: app g-hub])", text);
        Assert.DoesNotContain("CPU-Z", text.Split("On the list but not")[0]);

        settings.Ai.PagesOff = [AiPages.Apps];
        Assert.StartsWith("Not shared", tools.Run(AiTools.AppsLookUp, "{}"));

        // Ollama and own key: not offered (their prompts stay as they are until the person says).
        var plain = new AiTools(new RigsightSettings());
        Assert.DoesNotContain(plain.Definitions(), d => (string?)d?["function"]?["name"] == AiTools.AppsLookUp);
    }

    [Fact]
    public void Check_again_shows_the_before_side_at_once()
    {
        var at = new DateTime(2026, 10, 24, 21, 41, 0);
        string path = Path.Combine(Path.GetTempPath(), $"check-before-{Guid.NewGuid():N}.db");
        using (var w = RigsightDb.OpenWriter(path))
            for (int i = -10; i < 0; i++) w.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(at) + i * 60, RamUsed = 26.8, CommitPct = 74 });
        using var db = RigsightDb.OpenReader(path)!;
        var rows = ActionCheck.Before(db, at, 10, CheckWhat.Memory);
        Assert.Equal([("Memory in use", "26.8 GB", ""), ("Closest to running out", "74%", "")], rows.Select(r => (r.Label, r.Before, r.After)));
        // Too little before: nothing yet.
        Assert.Empty(ActionCheck.Before(db, at.AddHours(-3), 10, CheckWhat.Memory));
    }

    /// <summary>Own key: no plan limits the pages (on Free, Scan rightly isn't started: Storage isn't in it).</summary>
    [Fact]
    public void A_question_waits_for_the_plan_and_isnt_sent_when_the_plan_cant_be_read()
    {
        // 2026-10-26: a button pressed seconds after start-up went out before the plan came, guessed as Free on a Complete PC.
        try
        {
            Ui.Run(() =>
            {
                Relay.TestRoot.Value = "http://127.0.0.1:9";
                var s = new RigsightSettings();
                s.Ai.Mode = AiMode.RigSight;
                s.Ai.FirstQuestionSeen = [AiMode.RigSight];
                var window = new AskViewModel(Kit.OfflineSettings(s));
                Assert.Null(window.Standing);
                window.AskNew("How did my PC do yesterday, and is anything worth a look?", AiPages.Reports);
                var talk = window.Current!;
                // Shown at once, the question and the thinking line, while the plan is asked for.
                Assert.Single(talk.Messages.OfType<AskQuestion>());
                Assert.Single(talk.Messages.OfType<AskThinking>());
                Assert.True(window.IsAnswering);
                for (int i = 0; i < 300 && window.IsAnswering; i++) Ui.Pump(50);
                Assert.False(window.IsAnswering);
                // The relay never told the plan: nothing was sent, and it says so.
                Assert.Empty(talk.Messages.OfType<AskThinking>());
                Assert.Empty(talk.Messages.OfType<AskAnswer>());
                Assert.Contains("wasn't sent", Assert.Single(talk.Messages.OfType<AskProblem>()).Text);
            });
        }
        finally { Relay.TestRoot.Value = null; }
    }

    private static RigsightSettings NoPlan()
    {
        var s = new RigsightSettings();
        s.Ai.Mode = AiMode.OwnKey;
        return s;
    }

    // Live test, 2026-10-25: "Puzzling it out…" showed under the scan card instead of what it was about to read.
    [Fact]
    public void A_pinned_step_stays_until_a_real_one_replaces_it()
    {
        var thinking = new AskThinking();
        thinking.Pin("Reading the scan of D:…");
        thinking.Show("");
        Assert.Equal("Reading the scan of D:…", thinking.Step);
        Assert.True(thinking.Slow);
        thinking.Show("Reading the scan of D: in full…");
        Assert.Equal("Reading the scan of D: in full…", thinking.Step);
        thinking.Show("");
        Assert.NotEqual("Reading the scan of D: in full…", thinking.Step);
    }

    private static ScanResult Result(long size) => new()
    {
        Root = new FolderNode
        {
            Name = "E:", Path = @"E:\", Size = size, Files = 58_083,
            Children = [new() { Name = "SteamLibrary", Path = @"E:\SteamLibrary", Size = size * 68 / 100 }, new() { Name = "Videos", Path = @"E:\Videos", Size = size * 13 / 100 },
                new() { Name = "Downloads", Path = @"E:\Downloads", Size = size * 8 / 100 }, new() { Name = "Games", Path = @"E:\Games", Size = size * 11 / 100 }],
        },
        LargestFiles = [], FileCount = 58_083, Elapsed = TimeSpan.FromSeconds(1.5),
    };

    /// <summary>
    /// The person's complaint (2026-10-24): "after pressing it, the block is just gone, then a loader starts". The card now
    /// shows the scan in place, then its result, and the first answer stays: the next answer comes under it.
    /// </summary>
    [Fact]
    public void A_scan_changes_its_card_in_place_and_the_first_answer_stays()
    {
        string oldFolder = DriveScans.Folder;
        DriveScans.Folder = Path.Combine(Path.GetTempPath(), $"rigsight-scans-{Guid.NewGuid():N}");
        var oldReady = AiActions.DriveReady;
        AiActions.DriveReady = _ => true;
        var oldMin = AskViewModel.MinScanShown;
        AskViewModel.MinScanShown = TimeSpan.FromMilliseconds(200);
        try
        {
            Ui.Run(() =>
            {
                Relay.TestRoot.Value = "http://127.0.0.1:9";
                var window = new AskViewModel(Kit.OfflineSettings(NoPlan()));
                var talk = new AskConversation { Title = "t" };
                window.Conversations.Add(talk);
                window.Current = talk;
                if (window.ShowFirstQuestion) window.StartAskingCommand.Execute(null);
                var release = new TaskCompletionSource();
                window.ScanDrive = async (root, progress, ct, folder) =>
                {
                    progress.Report((21_400, 212L << 30));
                    folder?.Report(@"E:\SteamLibrary\steamapps\common");
                    await release.Task.WaitAsync(ct);
                    return Result(677L << 30);
                };
                var answer = AskViewModel.ToAnswer("E is 73% full.\nNext step: see which folders take the space.\n[do: scan E]", [],
                    actions: m => AiActions.Resolve(m, new AiActionContext([], null, () => [], true)));
                talk.Messages.Add(new AskQuestion("why is my E drive full?"));
                talk.Messages.Add(answer);
                var next = answer.Parts.OfType<AskNext>().Single();
                Assert.True(next.IsScan);
                var scan = next.ScanAction!;
                Assert.Equal("Scan the E drive", scan.Scan!.Title);

                _ = window.ActCommand.ExecuteAsync(scan);
                Ui.Pump(100);
                Assert.True(scan.IsWorking);
                Assert.Equal(21_400, scan.Scan.Files);
                Assert.Equal(@"E:\SteamLibrary\steamapps\common", scan.Scan.Folder);

                release.SetResult();
                Ui.Pump(600);
                Assert.True(scan.IsDone);
                Assert.Equal(["SteamLibrary", "Videos", "Games"], scan.Scan.Top.Select(t => t.Name));
                // The first answer is still there and no second question bubble was added.
                Assert.Contains(answer, talk.Messages);
                Assert.Single(talk.Messages.OfType<AskQuestion>());
                Assert.True(talk.Messages.IndexOf(answer) < talk.Messages.Count - 1, "the answer that reads the scan comes under the first one");
            });
        }
        finally
        {
            Relay.TestRoot.Value = null;
            AskViewModel.MinScanShown = oldMin;
            AiActions.DriveReady = oldReady;
            DriveScans.Folder = oldFolder;
            DriveScans.Clear();
        }
    }

    [Fact]
    public void Stop_puts_the_card_back_and_a_failed_scan_says_why_and_can_be_tried_again()
    {
        var oldReady = AiActions.DriveReady;
        AiActions.DriveReady = _ => true;
        try
        {
            Ui.Run(() =>
            {
                var window = new AskViewModel(Kit.OfflineSettings(NoPlan()));
                var talk = new AskConversation { Title = "t" };
                window.Conversations.Add(talk);
                window.Current = talk;
                int tries = 0;
                window.ScanDrive = async (root, progress, ct, folder) =>
                {
                    tries++;
                    progress.Report((12_400, 1L << 30));
                    if (tries == 1) { await Task.Delay(Timeout.Infinite, ct); }
                    throw new IOException("The device is not ready.");
                };
                var answer = AskViewModel.ToAnswer("E is full.\n[do: scan E]", [], actions: m => AiActions.Resolve(m, new AiActionContext([], null, () => [], true)));
                talk.Messages.Add(answer);
                var scan = answer.Parts.OfType<AskNext>().Single().ScanAction!;

                _ = window.ActCommand.ExecuteAsync(scan);
                Ui.Pump(100);
                window.StopScanCommand.Execute(scan);
                Ui.Pump(200);
                Assert.True(scan.IsReady);

                _ = window.ActCommand.ExecuteAsync(scan);
                Ui.Pump(300);
                Assert.True(scan.IsFailed);
                Assert.Contains("unplugged or gone to sleep", scan.Scan!.Failed);
                Assert.True(scan.CanRetry);
            });
        }
        finally { AiActions.DriveReady = oldReady; }
    }
}
