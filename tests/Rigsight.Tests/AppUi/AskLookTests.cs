using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
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
            Assert.DoesNotContain(words, w => w.Contains("[src", StringComparison.Ordinal) || w.Contains("[do", StringComparison.Ordinal) || w.Contains("Ask next", StringComparison.Ordinal));
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
            // Each source fills the list's width, so its hover covers the whole row (it covered half a row).
            var list = (FrameworkElement)popup.Child;
            Assert.All(Visuals.Descendants<Button>(list), b => Assert.True(b.ActualWidth >= list.ActualWidth - 4, $"{b.ActualWidth} of {list.ActualWidth}"));
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
}
