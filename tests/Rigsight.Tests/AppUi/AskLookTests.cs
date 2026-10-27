using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using Rigsight.Core.Ai;
using Rigsight.Core.Apps;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;
using Rigsight.Views;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// The Ask window as drawn, with every part of the approved look of 2026-10-23 (buttons in the Next step card in each of
/// their states, the Check again card, source pills and their list, suggestion chips), saved as pictures in screens\ask-*.png
/// to look at, and checked for what shouldn't be there (an empty pill, a marker shown as words).
/// </summary>
[Collection("UI")]
public sealed class AskLookTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");
    private static readonly AiLookUp Running = new("get_running_now", new JsonObject(), 900);
    private static readonly AiLookUp Memory = new("get_memory", new JsonObject { ["from"] = "2026-10-16", ["to"] = "2026-10-22" }, 900);
    private static readonly AiLookUp Web = new("search_web", new JsonObject { ["query"] = "best gpu" }, 400, AiSources.ParseWeb("""
        [w1] GPU Benchmarks Hierarchy 2026 - Graphics Card Rankings (https://www.tomshardware.com/reviews/gpu-hierarchy), published 2025-04-17
        [w2] Best Graphics Cards for Gaming in India 2026 (https://gauravtiwari.org/best-gpus/), published 2026-08-24
        [w3] Fastest Gaming Graphics Cards (https://pcpartvs.com/gpus/)
        """));

    private static readonly RunningSnapshot Now = new(DateTime.Now,
        [new("DataCruncher.exe", "DataCruncher", @"C:\Tools\DataCruncher.exe", 1, 3100, 9, 0, false, false, AppCategory.Other)]);

    private const string Fast = """
        The biggest drag right now is memory: it's 84% full, and DataCruncher is holding 3.1 GB of it. [src: running memory]
        DataCruncher has no window and has been running for 2 hours. Chrome and VS Code are next, but you're using those. [src: running]
        For a new card, the RX 9060 XT 16GB is the best value at 1080p. [src: w1 w2 w3]
        Next step: close DataCruncher if you don't need it, then check whether memory drops.
        [do: close DataCruncher.exe]
        [do: check 10 memory]
        [do: open processes memory]
        Ask next: Why does DataCruncher use so much? | Is 32 GB enough for me? | What else runs in the background?
        """;

    private (AskViewModel Ask, AskConversation Talk, AskAnswer Answer) Setup(string text, IReadOnlyList<AiLookUp> looks)
    {
        var ask = host.Shell.Ask;
        var talk = new AskConversation { Title = "Look" };
        ask.Conversations.Insert(0, talk);
        ask.Current = talk;
        var answer = AskViewModel.ToAnswer(text, looks, _ => [new AiBars("APPS USING THE MOST MEMORY", [new("DataCruncher", 1, "3.1 GB", AiTone.Plain), new("Google Chrome", 0.9, "2.9 GB", AiTone.Plain)])],
            actions: m => AiActions.Resolve(m, new AiActionContext(looks, Now, () => [], true)));
        talk.Messages.Add(new AskQuestion("how do I make my PC fast?"));
        talk.Messages.Add(answer);
        ask.IsPoppedOut = false;
        ask.IsOpen = true;
        // Past the first-question window, as after Start asking.
        if (ask.ShowFirstQuestion) ask.StartAskingCommand.Execute(null);
        return (ask, talk, answer);
    }

    private FrameworkElement Panel() => (FrameworkElement)Visuals.Descendants<AskWindow>(host.Window).Single().FindName("Panel");

    /// <summary>The panel cut out of the whole window (drawn alone, it lost its place and came out blank).</summary>
    private void Shot(string name)
    {
        Ui.Pump(250);
        Ui.Run(() =>
        {
            var window = host.Window;
            var panel = Panel();
            var at = panel.TransformToAncestor(window).Transform(new Point(0, 0));
            int w = (int)Math.Ceiling(window.ActualWidth), h = (int)Math.Ceiling(window.ActualHeight);
            var all = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            all.Render(window);
            var cut = new System.Windows.Media.Imaging.CroppedBitmap(all, new Int32Rect((int)at.X, (int)at.Y, Math.Min(w - (int)at.X, (int)panel.ActualWidth), Math.Min(h - (int)at.Y, (int)panel.ActualHeight)));
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(cut));
            Directory.CreateDirectory(Shots);
            using var file = File.Create(Path.Combine(Shots, $"ask-{name}.png"));
            png.Save(file);
        });
    }

    /// <summary>The pills drawn, found through the lines' text (they sit inside it, where the visual tree doesn't reach).</summary>
    private static List<Button> Pills(FrameworkElement panel) =>
        [.. Visuals.Descendants<TextBlock>(panel).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.InlineUIContainer>())
            .Select(c => c.Child).OfType<ContentControl>().Where(c => c.IsVisible && c.Content is AskRefs)
            .SelectMany(c => Visuals.Descendants<Button>(c)).Where(b => b.Tag is AskRefs)];

    [Fact]
    public void Every_part_of_an_answer_as_drawn()
    {
        Ui.TakeProblems();
        var (ask, talk, answer) = Ui.Run(() => Setup(Fast, [Running, Memory, Web]));
        Ui.Pump(400);
        Shot("1-answer");

        Ui.Run(() =>
        {
            var panel = Panel();
            // No marker shows as words anywhere, and every pill drawn has its sources.
            var words = Visuals.Descendants<TextBlock>(panel).Select(t => t.Text).ToList();
            Assert.DoesNotContain(words, w => w.Contains("[src", StringComparison.Ordinal) || w.Contains("[do", StringComparison.Ordinal) || w.Contains("Ask next:", StringComparison.Ordinal));
            var pills = Pills(panel);
            Assert.Equal(3, pills.Count);
            Assert.All(pills, p => Assert.True(((AskRefs)p.Tag).List.Count > 0));
            // The buttons: one main, one second, one link, all inside the Next step card.
            var next = answer.Parts.OfType<AskNext>().Single();
            Assert.Equal([AskActionRole.Main, AskActionRole.Second, AskActionRole.Link], next.Actions.Select(a => a.Role));
            // A step that closes something is the card, its title the action, its own button "Close it".
            Assert.True(next.IsCard);
            Assert.Equal(("Close DataCruncher", "Close it"), (next.CardTitle, next.Actions[0].ButtonLabel));
        });

        // The sources list of the web paragraph, open.
        var webRefs = answer.Parts.OfType<AskLine>().Last().Refs!;
        Ui.Run(() => webRefs.IsOpen = true);
        Ui.Pump(400);
        Ui.Run(() =>
        {
            var popup = Visuals.Descendants<Popup>(Panel()).First(p => p.IsOpen);
            Ui.SavePng((FrameworkElement)popup.Child, Path.Combine(Shots, "ask-2-sources-web.png"));
            // Each source fills the list's width, so its hover covers the whole row (it covered half a row): all but the
            // card's 4 px inset each side (the hover is a rounded block inside the rounded card) and the shadow's room.
            var list = (FrameworkElement)popup.Child;
            Assert.All(Visuals.Descendants<Button>(list), b => Assert.True(b.ActualWidth >= list.ActualWidth - 20, $"{b.ActualWidth} of {list.ActualWidth}"));
            webRefs.IsOpen = false;
        });
        var appRefs = answer.Parts.OfType<AskLine>().First().Refs!;
        Ui.Run(() => (answer.VerdictRefs ?? appRefs).IsOpen = true);
        Ui.Pump(400);
        Ui.Run(() =>
        {
            var popup = Visuals.Descendants<Popup>(Panel()).First(p => p.IsOpen);
            Ui.SavePng((FrameworkElement)popup.Child, Path.Combine(Shots, "ask-3-sources-records.png"));
            (answer.VerdictRefs ?? appRefs).IsOpen = false;
        });

        // Close: asking first, at work, done.
        var close = answer.Parts.OfType<AskNext>().Single().Actions[0];
        Ui.Run(() => close.State = AskActionState.Confirming);
        Shot("4-close-asks");
        Ui.Run(() => close.State = AskActionState.Working);
        Shot("5-closing");
        Ui.Run(() => { close.Said = "Closed DataCruncher at 9:41 PM · 3.0 GB freed"; close.State = AskActionState.Done; });
        Shot("6-closed");

        // Check again: waiting, then the result.
        var check = new AskCheck(DateTime.Now, 10, CheckWhat.Memory, "DataCruncher");
        Ui.Run(() =>
        {
            var press = answer.Parts.OfType<AskNext>().Single().Actions[1];
            press.Said = "Checking at 9:51 PM";
            press.State = AskActionState.Done;
            talk.Messages.Add(new AskDone("Closed DataCruncher at 9:41 PM · 3.0 GB freed"));
            talk.Messages.Add(check);
        });
        Shot("7-check-waiting");
        Ui.Run(() => check.Result = new CheckResult(
            [new("Memory in use", "26.8 GB", "15.9 GB", "10.9 GB lower", true), new("Closest to running out", "74%", "50%", "24% lower", true)], true, null));
        Ui.Run(() => Visuals.Descendants<ScrollViewer>(Panel()).First(s => s.Name == "Chat").ScrollToEnd());
        Shot("8-check-result");

        // A step that only opens things: a line each, no card.
        Ui.Run(() =>
        {
            var (_, _, lines) = Setup("Your C drive is 94% full.\nIt grew 18 GB this week.\nNext step: see which folders take the space, then let Windows clear what it can.\n[do: open storage C]\n[do: windows storage-sense]", []);
            var step = lines.Parts.OfType<AskNext>().Single();
            Assert.True(step.IsLines);
            Assert.Equal("see which folders take the space, then let Windows clear what it can.", step.Actions[0].Why, ignoreCase: true);
            Assert.Null(step.Actions[1].Why);
        });
        Shot("11-lines");

        // Buttons with no step, and small talk with nothing.
        Ui.Run(() => Setup("I couldn't look at your temperatures: they're turned off in Settings › Ask RigSight.\nTurn them on there and ask again.\n[do: settings]", []));
        Shot("9-buttons-only");
        Ui.Run(() =>
        {
            Setup("Hey! Ask me anything about your PC.", []);
            Assert.Empty(Pills(Panel()));
        });
        Shot("10-small-talk");
        Ui.Run(() => { ask.IsOpen = false; ask.Conversations.Remove(talk); });
        Assert.Empty(Ui.TakeProblems().Crashes);
    }

    /// <summary>
    /// The chat buttons of 2026-10-24 as drawn, each state the approved artifact shows, for the side-by-side audit: the
    /// scan card ready, scanning, done and failed (under an answer that stays); the plan card for a page the plan doesn't
    /// open, and paying; "Open NVIDIA App" with its own icon and "Get" for one not here; Check again waiting with its bar and
    /// before figures; Close with its seconds. Saved as screens\ask-20..33.png.
    /// </summary>
    // Questions given from the admin site (approved mock-up, 2026-10-25): the card above the question box, with and
    // without a message, and on Free with none left, where it takes the out card's place.
    [Fact]
    public void Questions_given_as_drawn()
    {
        Ui.TakeProblems();
        var (ask, talk, _) = Ui.Run(() => Setup("Your CPU hits 78°C in short bursts when Chrome opens new tabs.", [Running]));
        var before = Ui.Run(() => ask.Standing);
        string Gift(long id, int n, string? note) => $$"""{"id":{{id}},"n":{{n}},"note":{{(note is null ? "null" : $"\"{note}\"")}}}""";
        try
        {
            Ui.Run(() =>
            {
                talk.Messages.Clear();
                ask.Show(Relay.ReadStatus(Rigsight.Tests.App.PlansTests.Status("complete", "subscription", used: 23)
                    .Replace("\"can_manage\"", "\"given\":20,\"can_manage\"")
                    .Replace("\"search\":true", $"\"search\":true,\"gifts\":[{Gift(41, 20, "Thanks for reporting the fan bug.")}]"))!);
            });
            Shot("40-given-extra");
            var card = Ui.Run(() => Visuals.Descendants<Button>(Panel()).Single(b => b.IsVisible && b.Command == ask.CloseGiftCommand));
            Assert.NotNull(card);

            Ui.Run(() => ask.Show(Relay.ReadStatus(Rigsight.Tests.App.PlansTests.Status("free", used: 10)
                .Replace("\"can_manage\"", "\"given\":5,\"can_manage\"")
                .Replace("\"search\":true", $"\"search\":true,\"gifts\":[{Gift(42, 5, null)}]"))!));
            Shot("41-given-free");
            Assert.False(Ui.Run(() => ask.IsOutOfQuestions));

            Ui.Run(() => ask.CloseGiftCommand.Execute(null));
            Shot("42-given-closed");
            Assert.False(Ui.Run(() => Visuals.Descendants<Button>(Panel()).Any(b => b.IsVisible && b.Command == ask.CloseGiftCommand)));
        }
        finally
        {
            Ui.Run(() => { ask.Gift = null; if (before is not null) ask.Show(before); else ask.Standing = null; });
        }
        Assert.Empty(Ui.TakeProblems().Crashes);
    }

    [Fact]
    public void The_new_buttons_in_each_state_as_drawn()
    {
        Ui.TakeProblems();
        var drive = new AiLookUp("get_storage", new JsonObject(), 600);
        var (ask, talk, answer) = Ui.Run(() => Setup("E is 73% full: 680.5 GB of 931.5 GB is used. It has barely changed this week.\nNext step: see which folders take the space.\n[do: scan E]", [drive]));
        talk.Messages[0] = new AskQuestion("why is my E drive full?");
        Shot("20-scan-ready");
        var scan = answer.Parts.OfType<AskNext>().Single().ScanAction!;
        Ui.Run(() => { scan.State = AskActionState.Working; scan.Scan!.Files = 31_250; scan.Scan.Found = 381L << 30; scan.Scan.Folder = @"E:\SteamLibrary\steamapps\common"; });
        Shot("21-scanning");
        Ui.Run(() =>
        {
            var root = new FolderNode { Name = "E:", Path = @"E:\", Size = 677L << 30, Files = 58_083 };
            root.Children = [new() { Name = "SteamLibrary", Path = @"E:\SteamLibrary", Size = 464L << 30 }, new() { Name = "Videos", Path = @"E:\Videos", Size = 90L << 30 },
                new() { Name = "Downloads", Path = @"E:\Downloads", Size = 57L << 30 }, new() { Name = "Games", Path = @"E:\Games", Size = 66L << 30 }];
            scan.Scan!.Result = new ScanResult { Root = root, LargestFiles = [], FileCount = 58_083, Elapsed = TimeSpan.FromSeconds(1.5) };
            scan.Scan.DoneAt = new DateTime(2026, 10, 24, 21, 7, 0);
            scan.State = AskActionState.Done;
            var thinking = new AskThinking();
            thinking.Show("Reading the scan of E:…");
            talk.Messages.Add(thinking);
        });
        Shot("22-scanned-reading");
        Ui.Run(() =>
        {
            talk.Messages.Remove(talk.Messages.OfType<AskThinking>().Single());
            scan.Scan!.Failed = "Windows stopped the scan after 12,400 files. The drive may have been unplugged or gone to sleep.";
            scan.State = AskActionState.Failed;
        });
        Shot("23-scan-failed");

        // A page the plan doesn't open: the card where the scan was, the page's own line under it, then paying.
        Ui.Run(() =>
        {
            var (_, t, locked) = Setup("Drive E is 73% full: 680.5 GB of 931.5 GB is used, so 251 GB is free.\nYour plan doesn't include Storage, so I can't see which folders fill it.\nNext step: scan it yourself on the Storage page.\n[do: open storage E]\nAsk next: How full is C? | Did E grow this week?\n[locked: storage | See what fills the E drive]", [new AiLookUp("get_today", new JsonObject(), 900)]);
            t.Messages[0] = new AskQuestion("why is my E drive full?");
            var withPlan = locked with { LockedPlanId = "essentials", LockedPlanName = "Essentials" };
            var parts = withPlan.Parts.ToList();
            parts.Insert(parts.FindIndex(x => x is AskNext), new AskLockedCard(withPlan));
            t.Messages[1] = withPlan with { Parts = parts };
        });
        Shot("24-locked-card");
        Ui.Run(() => ask.Current!.Messages.OfType<AskAnswer>().Single().Pay.Waiting = true);
        Shot("25-locked-paying");

        // Open an app that's here (its own icon, when Windows gives one) and Get one that isn't.
        Ui.Run(() =>
        {
            var (_, t, _) = Setup("No, your driver is behind: you have 617.14, and NVIDIA's newest Game Ready driver is 617.42.\nNext step: check for a driver update and install the newest Game Ready driver if it offers one.\n[do: app nvidia-app]\n[do: app amd-software]", []);
            t.Messages[0] = new AskQuestion("Is the driver for my RTX 3080 Ti up to date?");
            var text = "No, your driver is behind: you have 617.14, and NVIDIA's newest Game Ready driver is 617.42.\nNext step: check for a driver update and install the newest Game Ready driver if it offers one.\n[do: app nvidia-app]\n[do: app amd-software]";
            t.Messages[1] = AskViewModel.ToAnswer(text, [], actions: m => AiActions.Resolve(m, new AiActionContext([], null, () => [], true, null, () => OpenableApps.Installed(StartApps.List()))));
        });
        Shot("26-open-app");

        // Check again waiting: a bar to a known end, the before figures already in.
        Ui.Run(() =>
        {
            var (_, t, a) = Setup(Fast, [Running, Memory, Web]);
            var next = a.Parts.OfType<AskNext>().Single();
            var close = next.Actions[0];
            close.Said = "Closed DataCruncher at 9:41 PM · 3.0 GB freed";
            close.State = AskActionState.Done;
            var press = next.Actions[1];
            var check = new AskCheck(DateTime.Now.AddMinutes(-4), 10, CheckWhat.Memory, "DataCruncher") { BeforeRead = true };
            check.BeforeRows = [new("Memory in use", "26.8 GB", "", "", null), new("Closest to running out", "74%", "", "", null)];
            press.Started = check;
            press.Said = $"Checks at {check.Due:h:mm tt}";
            press.Pending = true;
            press.State = AskActionState.Done;
            t.Messages.Add(check);
        });
        Ui.Run(() => Visuals.Descendants<ScrollViewer>(Panel()).First(sv => sv.Name == "Chat").ScrollToEnd());
        Shot("27-check-waiting");

        // Close at work: why it takes a moment, its seconds as a bar.
        Ui.Run(() =>
        {
            var (_, _, a) = Setup(Fast, [Running, Memory, Web]);
            a.Parts.OfType<AskNext>().Single().Actions[0].State = AskActionState.Working;
        });
        Ui.Pump(500);
        Shot("28-closing-windowless");
        // An app with a window: it's asked, and given its seconds, as a bar.
        Ui.Run(() =>
        {
            var (_, t, _) = Setup("Hey! Ask me anything about your PC.", []);
            var a = AskViewModel.ToAnswer("Chrome holds 2.9 GB right now.\nNext step: close it if you don't need it.\n[do: close chrome.exe]", [],
                actions: _ => [new AiAction(AiActionKind.CloseApp, "Close Google Chrome", "", "chrome.exe", "Google Chrome", MemMb: 2900, Windowless: false)]);
            t.Messages[1] = a;
            a.Parts.OfType<AskNext>().Single().Actions[0].State = AskActionState.Working;
        });
        Ui.Pump(3000);
        Shot("28-closing");

        // A card at a limit, paying.
        Ui.Run(() =>
        {
            var (_, t, _) = Setup("Hey! Ask me anything about your PC.", []);
            t.Messages.Clear();
            t.Messages.Add(new AskQuestion("why does my PC crash in Cyberpunk?"));
            t.Messages.Add(new AskWall("That was your last free question this week.", "They're back on Monday. Essentials gives up to 100 a day for $5 a month, and sends this question the moment it's on.", "essentials", "Essentials", "why does my PC crash in Cyberpunk?"));
        });
        Shot("29-wall");
        Ui.Run(() => ask.Current!.Messages.OfType<AskWall>().Single().Pay.Waiting = true);
        Shot("30-wall-paying");

        Ui.Run(() => { ask.IsOpen = false; foreach (var c in ask.Conversations.Where(c => c.Title == "Look").ToList()) ask.Conversations.Remove(c); });
        Assert.Empty(Ui.TakeProblems().Crashes);
    }

    /// <summary>
    /// Board 7 (2026-10-26): the widget, overlay and taskbar cards under an answer, ready, done (with Undo) and with every
    /// reading already there, drawn for the side-by-side check with the mock-up. Saved as screens\ask-50..54.png.
    /// </summary>
    [Fact]
    public void The_widget_overlay_and_taskbar_cards_as_drawn()
    {
        Ui.TakeProblems();
        AiReading package = new("CPU Package", SensorId: "/look/cpu/0", Part: "AMD Ryzen 7 7800X3D");
        var context = new AiActionContext([], null, () => [], true, Sensors: [package], TrayOf: new Dictionary<OverlayMetric, AiReading> { [OverlayMetric.CpuTemp] = package });
        const string Text = "Your GPU hot spot reached 96°C in Cyberpunk, 18 degrees over its edge temperature.\nWatching it while you play shows when it climbs.\n"
            + "[do: widget GPU hot spot, GPU temperature, CPU temperature]\n[do: overlay GPU hot spot, Frame time, 1% low]\n[do: taskbar CPU temperature]";
        var (ask, talk, answer) = Ui.Run(() =>
        {
            var (a, t, _) = Setup("Hey! Ask me anything about your PC.", []);
            a.RivaTuner = () => "missing";
            var drawn = AskViewModel.ToAnswer(Text, [], actions: m => AiActions.Resolve(m, context));
            foreach (var card in drawn.Parts.OfType<AskNext>().SelectMany(n => n.SetupActions)) card.Setup!.Refresh(host.Shell.Settings.Current, "missing");
            t.Messages[0] = new AskQuestion("how do I keep an eye on my GPU while gaming?");
            t.Messages[1] = drawn;
            return (a, t, drawn);
        });
        Shot("50-setup-ready");
        var cards = answer.Parts.OfType<AskNext>().Single().SetupActions;
        Assert.Equal(3, cards.Count);
        Ui.Run(() =>
        {
            var labels = Visuals.Descendants<Button>(Panel()).Where(b => b.IsVisible).Select(b => AutomationProperties.GetName(b)).ToList();
            Assert.Contains("Create and turn on", labels);
            Assert.Contains("Customize", labels);
            Assert.Contains("Open Taskbar", labels);
            var words = Visuals.Descendants<TextBlock>(Panel()).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Contains("Frame time (needs RivaTuner)", words);
            Assert.Contains("As a slim bar, on your desktop, where you can drag it.", words);
        });
        Ui.Run(() => { foreach (var c in cards) { c.Setup!.DoneText = c.Kind == AiActionKind.CreateWidget ? "Created and turned on" : c.Kind == AiActionKind.AddOverlay ? "Added to the overlay" : "Added to the taskbar"; c.State = AskActionState.Done; } });
        Ui.Run(() => Visuals.Descendants<ScrollViewer>(Panel()).First(sv => sv.Name == "Chat").ScrollToEnd());
        Shot("51-setup-done");
        Ui.Run(() => Assert.Contains("Undo", Visuals.Descendants<Button>(Panel()).Where(b => b.IsVisible).Select(b => AutomationProperties.GetName(b))));
        // Everything there already: said, no button.
        Ui.Run(() =>
        {
            var s = host.Shell.Settings.Current.Clone();
            s.TraySensors = ["/look/cpu/0"];
            var taskbar = cards.Single(c => c.Kind == AiActionKind.AddTaskbar);
            taskbar.State = AskActionState.Ready;
            taskbar.Setup!.Refresh(s, "running");
        });
        Shot("52-setup-all-on");
        Ui.Run(() => Assert.Contains("All of these are in the taskbar already.", Visuals.Descendants<TextBlock>(Panel()).Where(t => t.IsVisible).Select(t => t.Text)));
        Ui.Run(() => { ask.IsOpen = false; foreach (var c in ask.Conversations.Where(c => c.Title == "Look").ToList()) ask.Conversations.Remove(c); });
        Assert.Empty(Ui.TakeProblems().Crashes);
    }

    /// <summary>
    /// A widget of theirs already showing the readings (made from an earlier answer and switched off, the user's own case of
    /// 2026-10-26): the card turns that one on instead of making a second; with it on, the card says so and has no button.
    /// </summary>
    [Fact]
    public void A_widget_that_already_shows_the_readings_is_turned_on_not_made_again()
    {
        Ui.TakeProblems();
        const string Text = "Keep the temperatures in view while you play.\n[do: widget CPU temperature, GPU temperature, RAM in use]";
        var (ask, answer) = Ui.Run(() =>
        {
            var (a, t, _) = Setup("Hey! Ask me anything about your PC.", []);
            var drawn = AskViewModel.ToAnswer(Text, [], actions: m => AiActions.Resolve(m, new AiActionContext([], null, () => [], true)));
            t.Messages[1] = drawn;
            return (a, drawn);
        });
        var card = answer.Parts.OfType<AskNext>().Single().SetupActions.Single();
        var keys = card.Setup!.Readings.Select(r => new WidgetItem { Id = r.Key }).ToList();
        var mine = new WidgetConfig { Style = WidgetStyle.Custom, Id = "w-test", Name = "My widget 1", Layout = WidgetLayout.Bar, Items = keys, Enabled = false };
        Ui.Run(() =>
        {
            var s = host.Shell.Settings.Current.Clone();
            s.Widgets.Add(mine);
            card.Setup.Refresh(s, "running");
        });
        Shot("53-setup-widget-off");
        Ui.Run(() =>
        {
            Assert.Equal("Turn on My widget 1", card.Setup.MainLabel);
            Assert.Contains("Turn on My widget 1", Visuals.Descendants<Button>(Panel()).Where(b => b.IsVisible).Select(b => AutomationProperties.GetName(b)));
            Assert.Contains("My widget 1 already shows these, but it's off.", Visuals.Descendants<TextBlock>(Panel()).Where(t => t.IsVisible).Select(t => t.Text));
            // The step's words aren't shown beside the card: they told them to do by hand what the button does.
            Assert.DoesNotContain(Visuals.Descendants<TextBlock>(Panel()).Where(t => t.IsVisible), t => t.Text.StartsWith("Keep the temperatures in view"));
        });
        Ui.Run(() =>
        {
            var s = host.Shell.Settings.Current.Clone();
            mine.Enabled = true;
            s.Widgets.Add(mine);
            card.Setup.Refresh(s, "running");
        });
        Shot("54-setup-widget-on");
        Ui.Run(() =>
        {
            Assert.True(card.Setup.AllOn);
            Assert.All(card.Setup.Chips, c => Assert.True(c.On));
            Assert.Contains("All of these are on My widget 1 already.", Visuals.Descendants<TextBlock>(Panel()).Where(t => t.IsVisible).Select(t => t.Text));
        });
        Ui.Run(() => { ask.IsOpen = false; foreach (var c in ask.Conversations.Where(c => c.Title == "Look").ToList()) ask.Conversations.Remove(c); });
        Assert.Empty(Ui.TakeProblems().Crashes);
    }

    /// <summary>
    /// Every icon on a line of words (a tick before "Created and turned on", the warning before a failure, the Check again
    /// clock) sits in the middle of the words' capitals, measured from what's drawn at 4x (the user, 2026-10-26: "alignment
    /// not proper with icon, audit these scenarios"). Every card in each of its states is on screen while it's measured.
    /// </summary>
    [Fact]
    public void Every_icon_sits_in_the_middle_of_its_words()
    {
        Ui.TakeProblems();
        AiReading package = new("CPU Package", SensorId: "/look/cpu/0", Part: "AMD Ryzen 7 7800X3D");
        var context = new AiActionContext([], null, () => [], true, Sensors: [package], TrayOf: new Dictionary<OverlayMetric, AiReading> { [OverlayMetric.CpuTemp] = package });
        const string Text = "Watching it while you play shows when it climbs.\n"
            + "[do: widget GPU hot spot, GPU temperature, CPU temperature]\n[do: overlay GPU hot spot, Frame time, 1% low]\n[do: taskbar CPU temperature]";
        var (ask, answer) = Ui.Run(() =>
        {
            var (a, t, _) = Setup("Hey! Ask me anything about your PC.", []);
            var drawn = AskViewModel.ToAnswer(Text, [], actions: m => AiActions.Resolve(m, context));
            t.Messages[1] = drawn;
            t.Messages.Add(new AskDone("Closed Python at 10:42 PM"));
            t.Messages.Add(new AskCheck(DateTime.Now, 10, CheckWhat.Memory));
            t.Messages.Add(new AskSummary { Replaced = 6 });
            return (a, drawn);
        });
        var cards = answer.Parts.OfType<AskNext>().Single().SetupActions;
        var problems = new List<string>();
        var seen = new List<string>();
        void Measure(string state)
        {
            Ui.Pump(250);
            Ui.Run(() =>
            {
                static bool IsIcon(FrameworkElement e) => e.IsVisible && e switch
                {
                    TextBlock t => t.FontFamily.Source.Contains("Icons") && t.Text.Length > 0,
                    Rigsight.Controls.InkCentered i => i.Font is { Length: > 0 } && i.Text.Length > 0,
                    _ => false,
                };
                foreach (var icon in Visuals.Descendants<FrameworkElement>(Panel()).Where(IsIcon).ToList())
                {
                    // In a square or a circle: centred on that, not on words.
                    if (System.Windows.Media.VisualTreeHelper.GetParent(icon) is not Panel row || row is Grid || row is StackPanel { Orientation: Orientation.Vertical }) continue;
                    // The words beside it, or the first line of the block of words beside it (Check again's heading).
                    var words = row.Children.OfType<FrameworkElement>().Where(e => e != icon && e.IsVisible)
                        .SelectMany(e => e is TextBlock t ? [t] : e is StackPanel { Orientation: Orientation.Vertical } sp ? sp.Children.OfType<TextBlock>().Take(1) : Enumerable.Empty<TextBlock>())
                        .FirstOrDefault(t => t.IsVisible && t.Text.Length > 0 && !t.FontFamily.Source.Contains("Icons"));
                    if (words is null) continue;
                    string name = $"{state}: {words.Text}";
                    double dy = IconOffset(row, icon, words);
                    seen.Add($"{name} {dy:+0.00;-0.00}");
                    // Half a pixel, closer than a screen shows. (A spinner isn't measured: caught mid-turn, it read anywhere from
                    // -0.6 to +1.4 px run to run; its place is checked on the picture, ask-60-summary-busy.)
                    if (Math.Abs(dy) > 0.5) problems.Add($"{name}: icon {dy:+0.00;-0.00} px from the middle of the capitals");
                }
            });
        }
        Ui.Run(() => { foreach (var c in cards) c.Setup!.Refresh(host.Shell.Settings.Current, "running"); });
        Measure("ready");
        Ui.Run(() =>
        {
            foreach (var c in cards) { c.Setup!.DoneText = "Created and turned on"; c.State = AskActionState.Done; }
            var line = ask.Current!.Messages.OfType<AskSummary>().Single();
            line.Text = "- x"; line.IsBusy = false;
        });
        Measure("done");
        Ui.Run(() =>
        {
            var s = host.Shell.Settings.Current.Clone();
            s.TraySensors = ["/look/cpu/0"];
            foreach (var c in cards) c.State = AskActionState.Ready;
            cards.Single(c => c.Kind == AiActionKind.AddTaskbar).Setup!.Refresh(s, "running");
            var widget = cards.Single(c => c.Kind == AiActionKind.CreateWidget);
            widget.Said = "You have 8 widgets of your own already, the most RigSight keeps. Delete one in Widgets first.";
            widget.State = AskActionState.Failed;
        });
        Measure("all on, failed");
        Directory.CreateDirectory(Shots);
        File.WriteAllLines(Path.Combine(Shots, "ask-icon-offsets.txt"), seen);
        Ui.Run(() => { ask.IsOpen = false; foreach (var c in ask.Conversations.Where(c => c.Title == "Look").ToList()) ask.Conversations.Remove(c); });
        Assert.NotEmpty(seen);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.Empty(Ui.TakeProblems().Crashes);
    }

    /// <summary>
    /// How far down the icon's ink middle is from the middle of the words' capitals on their first line, in pixels. The words
    /// are drawn as capitals for the moment ("HHH": no descender or accent to move the middle), then put back.
    /// </summary>
    private static double IconOffset(Panel row, FrameworkElement icon, TextBlock words)
    {
        const int k = 4;
        string text = words.Text;
        words.Text = "HHH";
        row.UpdateLayout();
        try
        {
            int w = (int)Math.Ceiling(row.ActualWidth) * k, h = (int)Math.Ceiling(row.ActualHeight) * k;
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(row) { Stretch = System.Windows.Media.Stretch.None, AlignmentX = System.Windows.Media.AlignmentX.Left, AlignmentY = System.Windows.Media.AlignmentY.Top },
                    null, new Rect(0, 0, row.ActualWidth, row.ActualHeight));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96 * k, 96 * k, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var px = new byte[w * h * 4];
            bitmap.CopyPixels(px, w * 4, 0);
            (double Top, double Bottom) Ink(FrameworkElement e)
            {
                var at = e.TransformToAncestor(row).Transform(new Point(0, 0));
                int x0 = Math.Max(0, (int)(at.X * k)), x1 = Math.Min(w, (int)((at.X + e.ActualWidth) * k));
                int y0 = Math.Max(0, (int)(at.Y * k)), y1 = Math.Min(h, (int)((at.Y + e.ActualHeight) * k));
                int top = -1, bottom = -1;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                        if (px[(y * w + x) * 4 + 3] >= 128) { if (top < 0) top = y; bottom = y; break; }
                return (top / (double)k, (bottom + 1) / (double)k);
            }
            var i = Ink(icon);
            var c = Ink(words);
            return (i.Top + i.Bottom) / 2 - (c.Top + c.Bottom) / 2;
        }
        finally
        {
            words.Text = text;
            row.UpdateLayout();
        }
    }

    /// <summary>
    /// A long chat's summary line and the question box as drawn (the approved mock-up "Long chats and big pastes",
    /// 2026-10-26): the spinner while it's made, a question asked meanwhile waiting for it, the line, the line opened, and
    /// the box near and past the most it holds. Saved as screens\ask-60..65 to compare with the mock-up; the spinner and
    /// the chevron are measured against their words.
    /// </summary>
    [Fact]
    public void A_long_chats_summary_and_the_big_question_box_as_drawn()
    {
        Ui.TakeProblems();
        var (ask, talk) = Ui.Run(() =>
        {
            var (a, t, _) = Setup("Hey! Ask me anything about your PC.", []);
            t.Title = "Black screens after the driver update";
            t.Messages.Clear();
            string[] qs = ["My screen went black twice yesterday, what happened?", "was it too hot?", "is the driver the problem?", "what should I try?", "ok I rolled back to 617.42, anything since?"];
            string[] ans = [
                "Your graphics driver stopped answering and Windows reset it, at **5:13 PM** and **5:25 PM** on 22 Oct.",
                "No. The graphics card was at 54 °C and 55 °C just before each reset, close to the 48 °C it sits at when idle.",
                "Possibly. Both resets came the day you installed NVIDIA driver 621.01, with none in the 14 days before.",
                "Go back to 617.42 with a clean install, then play as usual for a few days.",
                "Nothing since. No reset in the 19 hours on 617.42, including 2h 51m of Risk of Rain 2 last night."];
            for (int i = 0; i < qs.Length; i++)
            {
                t.Messages.Add(new AskQuestion(qs[i]));
                t.Messages.Add(AskViewModel.ToAnswer(ans[i], []));
            }
            return (a, t);
        });
        // While it's made: the line with the spinner, above the last two questions and their answers.
        var line = new AskSummary { Replaced = 6 };
        Ui.Run(() => talk.Messages.Insert(6, line));
        Ui.Run(() => Visuals.Descendants<ScrollViewer>(Panel()).First(sv => sv.Name == "Chat").ScrollToEnd());
        Shot("60-summary-busy");
        Ui.Run(() => Assert.Contains("Summarising earlier messages…", Visuals.Descendants<TextBlock>(Panel()).Where(t => t.IsVisible).Select(t => t.Text)));
        // A question asked meanwhile waits for it, saying so.
        AskThinking? waiting = null;
        Ui.Run(() =>
        {
            talk.Messages.Add(new AskQuestion("and the shut-offs?"));
            waiting = new AskThinking();
            talk.Messages.Add(waiting);
            waiting.Show("Summarising earlier messages…", "Your question goes as soon as it's done.");
            Visuals.Descendants<ScrollViewer>(Panel()).First(sv => sv.Name == "Chat").ScrollToEnd();
        });
        Shot("61-summary-asked");
        Ui.Run(() => Assert.Contains("Your question goes as soon as it's done.", Visuals.Descendants<TextBlock>(Panel()).Where(t => t.IsVisible).Select(t => t.Text)));
        // Done: the line, closed, then opened.
        Ui.Run(() =>
        {
            talk.Messages.Remove(waiting!);
            talk.Messages.RemoveAt(talk.Messages.Count - 1);
            line.Text = "- Two graphics driver resets on 22 Oct, at 5:13 PM and 5:25 PM.\n- Graphics card at 54 °C and 55 °C before them (idle is 48 °C), so not heat.\n- Suggested: go back to 617.42 with a clean install.";
            line.IsBusy = false;
            Visuals.Descendants<ScrollViewer>(Panel()).First(sv => sv.Name == "Chat").ScrollToEnd();
        });
        Shot("62-summary-done");
        Ui.Run(() => Assert.Contains("Earlier messages summarised to keep this chat going", Visuals.Descendants<Button>(Panel()).Where(b => b.IsVisible).Select(b => AutomationProperties.GetName(b))));
        Ui.Run(() =>
        {
            ask.ToggleSummaryCommand.Execute(line);
            Assert.True(line.IsOpen);
            Visuals.Descendants<ScrollViewer>(Panel()).First(sv => sv.Name == "Chat").ScrollToEnd();
        });
        Shot("63-summary-open");
        Ui.Run(() =>
        {
            var words = Visuals.Descendants<TextBlock>(Panel()).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Contains("What RigSight AI now reads instead of the 6 messages above:", words);
            Assert.Contains("Graphics card at 54 °C and 55 °C before them (idle is 48 °C), so not heat.", words);
        });
        // The box near the most it holds, then past it.
        Ui.Run(() => ask.Draft = new string('x', 92_000));
        Shot("64-box-near");
        Ui.Run(() =>
        {
            Assert.True(ask.ShowDraftCount);
            Assert.Contains("92,000 of 100,000 characters", Visuals.Descendants<TextBlock>(Panel()).Where(t => t.IsVisible).Select(t => t.Text));
            Assert.Equal(92_000, ((TextBox)Visuals.Descendants<AskWindow>(host.Window).Single().FindName("Question")).Text.Length);
        });
        Ui.Run(() => ask.Draft = new string('x', 130_000));
        Shot("65-box-over");
        Ui.Run(() =>
        {
            Assert.True(ask.IsTooLong);
            Assert.False(ask.SendCommand.CanExecute(null));
            Assert.Contains(Visuals.Descendants<TextBlock>(Panel()).Where(t => t.IsVisible), t => t.Text.StartsWith("That's more than RigSight AI can read at once"));
            // Nothing cut off: the box holds all of it.
            Assert.Equal(130_000, ((TextBox)Visuals.Descendants<AskWindow>(host.Window).Single().FindName("Question")).Text.Length);
        });
        Ui.Run(() => { ask.Draft = ""; ask.IsOpen = false; foreach (var c in ask.Conversations.Where(c => c.Title is "Look" or "Black screens after the driver update").ToList()) ask.Conversations.Remove(c); });
        Assert.Empty(Ui.TakeProblems().Crashes);
    }

    /// <summary>
    /// Every first letter a site can start with sits in the middle of its circle (the person saw an "N" off centre,
    /// 2026-10-23, and asked for every letter to be checked): A to Z, 0 to 9, accents, other scripts and a lowercase-only
    /// one, each drawn at 8x and its ink's middle measured against the circle's. Also saved as screens\ask-12-marks.png.
    /// </summary>
    [Fact]
    public void Every_first_letter_sits_in_the_middle_of_its_circle()
    {
        Ui.Run(() => Setup(Fast, [Running, Memory, Web]));
        // The Ask window is built once laid out: waited for, not assumed (run after other tests it wasn't there yet).
        Assert.True(Ui.WaitFor(() => Ui.Run(() => Visuals.Descendants<AskWindow>(host.Window).Any())), "the Ask window never opened");
        Ui.Pump(250);
        var starts = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".Select(c => c.ToString()).Concat(["Ö", "Ç", "Ł", "É", "Я", "Ω", "中", "ß"]).ToList();
        var problems = new List<string>();
        Ui.Run(() =>
        {
            var template = (DataTemplate)Visuals.Descendants<AskWindow>(host.Window).Single().FindResource("RefPill");
            var sheet = new WrapPanel { Width = 520, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = (System.Windows.Media.Brush)Application.Current.FindResource("SurfaceBrush") };
            var marks = new List<(string Start, FrameworkElement Mark, System.Windows.Media.Color Circle)>();
            foreach (var start in starts)
            {
                var refs = new AskRefs([new AiSource(false, start.ToLowerInvariant() + "site.com", "A page", null, null, "https://example.com")]);
                var pill = new ContentControl { Content = refs, ContentTemplate = template, Margin = new Thickness(4) };
                sheet.Children.Add(pill);
            }
            // RigSight's own records: each page's icon in the same circle (the fan and the network screen are drawn, not letters).
            foreach (var page in AiPages.All)
            {
                var refs = new AskRefs([new AiSource(true, page.Label, page.Label, null, page.Id is AiPages.Parts or AiPages.Rewind or AiPages.Dashboards ? null : page.Id, null)]);
                sheet.Children.Add(new ContentControl { Content = refs, ContentTemplate = template, Margin = new Thickness(4) });
            }
            // In the window, so its theme and the template's resources apply.
            var layer = (Grid)Visuals.Descendants<AskWindow>(host.Window).Single().FindName("Layer");
            layer.Children.Add(sheet);
            sheet.UpdateLayout();
            foreach (ContentControl pill in sheet.Children)
            {
                var dot = Visuals.Descendants<System.Windows.Shapes.Ellipse>(pill).First(e => e.Name == "Dot");
                var r = (AskRefs)pill.Content;
                marks.Add((r.IsApp ? r.First.Site : r.Mark, (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetParent(dot),
                    ((System.Windows.Media.SolidColorBrush)dot.Fill).Color));
            }
            foreach (var (start, mark, circle) in marks)
            {
                const int k = 8;
                int w = (int)Math.Ceiling(mark.ActualWidth) * k, h = (int)Math.Ceiling(mark.ActualHeight) * k;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96 * k, 96 * k, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(mark);
                var px = new byte[w * h * 4];
                bitmap.CopyPixels(px, w * 4, 0);
                // The circle: every pixel drawn at all. The letter: the ink inside it, dark or light, whichever its circle
                // takes (a picked accent makes some circles deeper, and their letters white).
                bool darkInk = Rigsight.Converters.InkOnConverter.ReadsDark(circle);
                int cx0 = w, cx1 = -1, cy0 = h, cy1 = -1, lx0 = w, lx1 = -1, ly0 = h, ly1 = -1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w + x) * 4;
                        byte b = px[i], gg = px[i + 1], r = px[i + 2], a = px[i + 3];
                        if (a < 128) continue;
                        (cx0, cx1, cy0, cy1) = (Math.Min(cx0, x), Math.Max(cx1, x), Math.Min(cy0, y), Math.Max(cy1, y));
                        if (darkInk ? r + gg + b < 150 : r + gg + b > 690) (lx0, lx1, ly0, ly1) = (Math.Min(lx0, x), Math.Max(lx1, x), Math.Min(ly0, y), Math.Max(ly1, y));
                    }
                if (lx1 < 0) { problems.Add($"{start}: no ink drawn on {circle} (ink {((System.Windows.Media.SolidColorBrush?)Visuals.Descendants<Rigsight.Controls.InkCentered>(mark).FirstOrDefault()?.Fill)?.Color})"); continue; }
                double dx = ((lx0 + lx1) - (cx0 + cx1)) / 2.0 / k, dy = ((ly0 + ly1) - (cy0 + cy1)) / 2.0 / k;
                // Within half a pixel either way: closer than a screen can show.
                if (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5) problems.Add($"{start}: {dx:+0.00;-0.00} px across, {dy:+0.00;-0.00} px down");
                // And inside the circle with room to spare (a wide letter or a tall accent mustn't touch its edge).
                if (lx0 <= cx0 + k || lx1 >= cx1 - k || ly0 <= cy0 + k || ly1 >= cy1 - k) problems.Add($"{start}: touches the circle's edge");
            }
            Ui.SavePng(sheet, Path.Combine(Shots, "ask-12-marks.png"));
            layer.Children.Remove(sheet);
        });
        Assert.True(problems.Count == 0, "Off the circle's middle:\n" + string.Join("\n", problems));
    }
    // The title bar's ask box and its drop (approved mock-up, 2026-10-27): drawn resting, open with its questions, typed
    // in, and showing the chat's name once Ask is open; the drop keeps what's typed when it closes.
    [Fact]
    public void The_title_bars_ask_box_opens_its_drop_keeps_a_draft_and_names_the_open_chat()
    {
        host.Show("home", 600);
        var ask = host.Shell.Ask;
        Ui.Run(() => { ask.IsOpen = false; ask.BarDraft = ""; });
        var box = Ui.Run(() => (System.Windows.Controls.Button)host.Window.FindName("AskBox"));
        var drop = Ui.Run(() => (System.Windows.Controls.Primitives.Popup)host.Window.FindName("AskDrop"));
        var root = Ui.Run(() => (FrameworkElement)host.Window.FindName("AskDropRoot"));
        Ui.Run(() =>
        {
            Assert.True(box.IsVisible);
            Assert.Equal("Ask about your PC…", ask.BarText);
            var bar = (FrameworkElement)host.Window.FindName("TitleBar");
            Ui.SavePng(bar, Path.Combine(Shots, "askbox-1-resting.png"));
            ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(box).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
        });
        Ui.Pump(500);
        Ui.Run(() =>
        {
            Assert.True(drop.IsOpen);
            Assert.True(ask.BarSuggests);
            Ui.SavePng(root, Path.Combine(Shots, "askbox-2-drop.png"));
            ask.BarDraft = "Why did my GPU hot spot reach 88 °C yesterday?\nAnd is that safe?";
        });
        Ui.Pump(300);
        Ui.Run(() =>
        {
            Assert.False(ask.BarSuggests);
            Assert.True(ask.CanAskFromBar);
            Ui.SavePng(root, Path.Combine(Shots, "askbox-3-typed.png"));
        });
        // Esc: back into the box, the draft kept and shown in it.
        Ui.Run(() => root.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(root)!, 0, System.Windows.Input.Key.Escape)
            { RoutedEvent = UIElement.PreviewKeyDownEvent }));
        Ui.Pump(500);
        Ui.Run(() =>
        {
            Assert.False(drop.IsOpen);
            Assert.StartsWith("Why did my GPU hot spot", ask.BarText);
            Assert.Contains("⏎", ask.BarText);
            Ui.SavePng((FrameworkElement)host.Window.FindName("TitleBar"), Path.Combine(Shots, "askbox-4-draft.png"));
            // Ask open on a chat: the box doesn't repeat its name (Ask's header says it); it starts a new chat.
            ask.BarDraft = "";
            var talk = new AskConversation { Title = "GPU hot spot at 88 °C" };
            ask.Conversations.Insert(0, talk);
            ask.Current = talk;
            ask.IsOpen = true;
        });
        Ui.Pump(300);
        Ui.Run(() =>
        {
            Assert.Equal("Ask about your PC…", ask.BarText);
            Assert.Equal("Start a new chat…", ask.BarPlaceholder);
            Ui.SavePng((FrameworkElement)host.Window.FindName("TitleBar"), Path.Combine(Shots, "askbox-5-open.png"));
            ask.IsOpen = false;
        });
        // Zoomed (2026-10-27: the drop hung to the right at 125%): the box, the drop and the window share one middle.
        double was = Ui.Run(() => host.Shell.Settings.Current.Zoom);
        try
        {
            Ui.Run(() => { host.Shell.Settings.Update(s => s.Zoom = 1.2); host.Window.Width += 1; });
            Ui.Pump(400);
            Ui.Run(() => ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(box).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke());
            Ui.Pump(600);
            Ui.Run(() =>
            {
                var bar = (FrameworkElement)host.Window.FindName("TitleBar");
                double Mid(FrameworkElement e) => e.PointToScreen(new Point(e.ActualWidth / 2, 0)).X;
                Assert.True(drop.IsOpen, "drop open");
                Assert.InRange(Mid(box) - Mid(bar), -1.5, 1.5);
                // Placed from the sizes as drawn: centred on the box at any zoom (the test window is off screen, where
                // Windows moves popups back on, so screen places can't be compared here).
                Assert.Equal(System.Windows.Controls.Primitives.PlacementMode.Custom, drop.Placement);
                var at = MainWindow.DropPlacement(new Size(600 * 1.2 + 28, 300), new Size(480 * 1.2, 36), default)[0].Point;
                Assert.Equal((480 * 1.2 - (600 * 1.2 + 28)) / 2, at.X, 3);
                Assert.Equal(0, at.Y);
                Assert.True(root.ActualWidth * ((System.Windows.Media.ScaleTransform)root.LayoutTransform).ScaleX > 640, "the drop is zoomed with the bar");
                root.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(root)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            });
            Ui.Pump(400);
        }
        finally
        {
            Ui.Run(() => { host.Shell.Settings.Update(s => s.Zoom = was); host.Window.Width -= 1; });
            Ui.Pump(300);
        }
    }
    // Before the first question (2026-10-27): the drop asks first, before anything is typed; Start asking turns it into
    // the box to type in. And the chat button beside the box opens Ask on its chats and closes it.
    [Fact]
    public void The_drop_asks_before_the_first_question_and_the_chat_button_opens_ask()
    {
        host.Show("home", 600);
        var ask = host.Shell.Ask;
        var seen = Ui.Run(() => host.Shell.Settings.Current.Ai.FirstQuestionSeen);
        var changed = typeof(AskViewModel).GetMethod("FirstQuestionChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var box = Ui.Run(() => (System.Windows.Controls.Button)host.Window.FindName("AskBox"));
        var chats = Ui.Run(() => (System.Windows.Controls.Button)host.Window.FindName("AskChats"));
        var drop = Ui.Run(() => (System.Windows.Controls.Primitives.Popup)host.Window.FindName("AskDrop"));
        var root = Ui.Run(() => (FrameworkElement)host.Window.FindName("AskDropRoot"));
        var text = Ui.Run(() => (FrameworkElement)host.Window.FindName("AskDropText"));
        static void Press(System.Windows.Controls.Button b) =>
            ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(b).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
        try
        {
            Ui.Run(() =>
            {
                ask.IsOpen = false;
                host.Shell.Settings.Update(s => s.Ai.FirstQuestionSeen = []);
                changed.Invoke(ask, null);
                Assert.True(ask.ShowFirstQuestion, "first-question step");
                Press(box);
            });
            Ui.Pump(500);
            Ui.Run(() =>
            {
                Assert.True(drop.IsOpen, "drop open");
                Assert.False(text.IsVisible, "nothing to type in before agreeing");
                var buttons = Visuals.Descendants<System.Windows.Controls.Button>(root).Where(b => b.IsVisible).Select(b => b.Content as string).ToList();
                Assert.Equal(["Start asking", "Not now", "Settings", "Privacy page"], buttons.Where(b => b is not null && !b.Contains('·')).Take(4).ToList());
                Ui.SavePng(root, Path.Combine(Shots, "askbox-6-first.png"));
                ask.StartAskingCommand.Execute(null);
            });
            Ui.Pump(400);
            Ui.Run(() =>
            {
                Assert.False(ask.ShowFirstQuestion);
                Assert.True(drop.IsOpen, "Start asking keeps the drop open, as the box to type in");
                Assert.True(text.IsVisible, "typing box shown");
                root.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(root)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            });
            Ui.Pump(400);
            Ui.Run(() =>
            {
                Assert.True(chats.IsVisible, "chat button shown");
                Ui.SavePng((FrameworkElement)host.Window.FindName("TitleBar"), Path.Combine(Shots, "askbox-7-chats.png"));
                Press(chats);
            });
            Ui.Pump(300);
            Ui.Run(() =>
            {
                Assert.True(ask.IsOpen, "Ask opened");
                Ui.SavePng((FrameworkElement)host.Window.FindName("TitleBar"), Path.Combine(Shots, "askbox-8-chats-lit.png"));
                Press(chats);
            });
            Ui.Pump(300);
            Assert.False(Ui.Run(() => ask.IsOpen), "Ask closed again");
        }
        finally
        {
            Ui.Run(() => { host.Shell.Settings.Update(s => s.Ai.FirstQuestionSeen = seen); changed.Invoke(ask, null); ask.IsOpen = false; });
            Ui.Pump(200);
        }
    }
    // A drive scan's file named by its label: drawn as a chip with the real name, in line with the words (2026-10-27).
    [Fact]
    public void A_file_named_by_its_label_is_drawn_as_a_chip_in_the_line()
    {
        string old = Rigsight.Services.DriveScans.Folder, folder = Path.Combine(Path.GetTempPath(), $"rigsight-scans-{Guid.NewGuid():N}");
        Rigsight.Services.DriveScans.Folder = folder;
        Directory.CreateDirectory(folder);
        string big = Path.Combine(folder, "docker_data.vhdx");
        File.WriteAllText(big, "x");
        try
        {
            var root = new Rigsight.Services.FolderNode { Name = "C:", Path = @"C:\", Size = 1000, Files = 1 };
            var scanned = DateTime.Now;
            Rigsight.Services.DriveScans.Keep(@"C:\", new Rigsight.Services.ScanResult { Root = root, FileCount = 1, Elapsed = TimeSpan.FromSeconds(1), LargestFiles = [new(big, 44_668_000_000)] }, scanned)
                .GetAwaiter().GetResult();
            host.Show("home", 400);
            Ui.Run(() => Setup($"The biggest single thing is [file C1 {Rigsight.Services.DriveScans.ScanCode(scanned)}], 41.6 GB in **C:\\ProgramData\\DockerDesktop**: Docker Desktop's virtual disk, and it only shrinks from inside Docker. [src: storage]\nNext step: open Storage on the C drive.\n[do: open storage C]", []));
            Shot("80-file-chip");
            Ui.Run(() =>
            {
                var chip = Visuals.Descendants<System.Windows.Controls.Border>(Panel()).FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == "docker_data.vhdx");
                Assert.NotNull(chip);
                host.Shell.Ask.IsOpen = false;
            });
        }
        finally
        {
            Rigsight.Services.DriveScans.Folder = old;
            Rigsight.Services.DriveScans.Clear();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
