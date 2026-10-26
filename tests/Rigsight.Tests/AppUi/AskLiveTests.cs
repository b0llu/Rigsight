using System.Windows;
using System.Windows.Controls;
using Rigsight.Core.Ai;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;
using Rigsight.Views;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// The chat buttons of 2026-10-24 end to end: the real window and shell, a real relay (wrangler dev, never the live one)
/// and the real model, nothing typed on the PC (the question is put in the box in memory and sent by the app's own
/// command). Runs only when RIGSIGHT_LIVE_RELAY names a local relay; RIGSIGHT_LIVE_PLAN says which plan the relay gives
/// this test PC (free or complete). Pictures go to screens\live-*.png, and what each answer had to live.log.
/// </summary>
[Collection("UI")]
public sealed class AskLiveTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string? RelayUrl = Environment.GetEnvironmentVariable("RIGSIGHT_LIVE_RELAY");
    private static readonly string Plan = Environment.GetEnvironmentVariable("RIGSIGHT_LIVE_PLAN") ?? "free";
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");
    private static readonly string LogFile = Path.Combine(TestEnvironment.DataDir, "screens", "live.log");

    private void Note(string line) { Directory.CreateDirectory(Shots); File.AppendAllText(LogFile, line + Environment.NewLine); }

    private FrameworkElement Panel() => (FrameworkElement)Visuals.Descendants<AskWindow>(host.Window).Single().FindName("Panel");

    private void Shot(string name)
    {
        Ui.Pump(300);
        Ui.Run(() =>
        {
            Visuals.Descendants<ScrollViewer>(Panel()).First(s => s.Name == "Chat").ScrollToEnd();
        });
        Ui.Pump(200);
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
            using var file = File.Create(Path.Combine(Shots, $"live-{Plan}-{name}.png"));
            png.Save(file);
        });
    }

    /// <summary>A new conversation, the question put in the box and sent, then the finished answer.</summary>
    private AskAnswer? AskAndWait(AskViewModel ask, string question, int timeoutMs = 120_000)
    {
        Ui.Run(() =>
        {
            ask.IsPoppedOut = false;
            ask.IsOpen = true;
            if (ask.ShowFirstQuestion) ask.StartAskingCommand.Execute(null);
            ask.NewCommand.Execute(null);
            ask.Draft = question;
            ask.SendCommand.Execute(null);
        });
        Ui.Pump(500);
        Ui.WaitFor(() => Ui.Run(() => !ask.IsAnswering), timeoutMs);
        return Ui.Run(() => ask.Current?.Messages.OfType<AskAnswer>().LastOrDefault());
    }

    private void Describe(string id, AskAnswer? a)
    {
        if (a is null) { Note($"{id}: NO ANSWER"); return; }
        var buttons = a.Parts.OfType<AskNext>().SelectMany(n => n.Actions).Select(x => $"{x.Kind} \"{x.Label}\"");
        Note($"{id}: buttons [{string.Join(", ", buttons)}]; plan card: {(a.Parts.OfType<AskLockedCard>().FirstOrDefault() is { } c ? $"\"{c.Title}\" / \"{c.Text}\" / {(c.HasPlan ? c.Button : "no plan button")}" : "none")}");
        Note("    " + a.Raw.Replace("\n", "\n    "));
    }

    [Fact]
    public void The_new_buttons_with_a_real_relay_and_model()
    {
        if (RelayUrl is null || RelayUrl.Contains("rigsight.app")) return;
        Environment.SetEnvironmentVariable("RIGSIGHT_RELAY_URL", RelayUrl);
        var ask = host.Shell.Ask;
        Note($"--- {DateTime.Now:HH:mm} plan {Plan}, device {Relay.DeviceId}");
        Ui.Run(() => ask.RefreshQuestionsAsync(theyAsked: true)).GetAwaiter();
        Ui.WaitFor(() => Ui.Run(() => ask.Standing is not null), 15_000);
        // Each run is its own PC to the relay (its data folder is new): for Complete, wait while the relay is given this PC's plan.
        for (int i = 0; i < 36 && Plan != "free" && Ui.Run(() => ask.Standing?.Plan?.Id) != Plan; i++)
        {
            Ui.Pump(5000);
            Ui.Run(() => ask.RefreshQuestionsAsync(theyAsked: true)).GetAwaiter();
            Ui.Pump(500);
        }
        Note($"standing: plan {Ui.Run(() => ask.Standing?.Plan?.Id) ?? "?"}");
        // A Complete run that stayed on Free tested nothing of the scan (a side agent's catch, 2026-10-25): fail, don't pass quietly.
        Assert.Equal(Plan, Ui.Run(() => ask.Standing?.Plan?.Id));

        // A drive question: on Free the plan card and the page, never a scan; on Complete the scan card, pressed for real.
        var drive = AskAndWait(ask, "why is my D drive full?");
        Describe("drive", drive);
        Shot("1-drive");
        var scanButton = drive?.Parts.OfType<AskNext>().FirstOrDefault()?.ScanAction;
        if (Plan == "free") Assert.Null(scanButton);
        else Assert.NotNull(scanButton);
        if (Plan == "complete" && scanButton is { } scan)
        {
            Ui.Run(() => ask.ActCommand.ExecuteAsync(scan)).GetAwaiter();
            Ui.WaitFor(() => Ui.Run(() => scan.IsDone || scan.IsFailed), 60_000);
            Note($"scan: {(scan.IsDone ? scan.Scan!.DoneText : "failed: " + scan.Scan!.Failed)}");
            Shot("2-scanned");
            Ui.Pump(1500);
            Ui.WaitFor(() => Ui.Run(() => !ask.IsAnswering), 120_000);
            var after = Ui.Run(() => ask.Current!.Messages.OfType<AskAnswer>().LastOrDefault());
            Note($"answers in the chat after the scan: {Ui.Run(() => ask.Current!.Messages.OfType<AskAnswer>().Count())}, questions: {Ui.Run(() => ask.Current!.Messages.OfType<AskQuestion>().Count())}");
            Note($"look-ups of the answer after the scan: {string.Join(", ", after?.LookUps.Select(l => $"{l.Tool} ({l.Chars} chars)") ?? [])}");
            Describe("after-scan", after);
            Assert.True(scan.IsDone, "the scan didn't finish");
            Assert.Equal(2, Ui.Run(() => ask.Current!.Messages.OfType<AskAnswer>().Count()));
            Assert.Contains(after!.LookUps, l => l.Tool == "get_drive_scan" && l.Chars > 300);
            Shot("3-after-scan");
        }

        // A drive that isn't there.
        Describe("no-drive", AskAndWait(ask, "Why is my I drive full?"));
        Shot("4-no-drive");

        // An app to open, and a Windows screen (shown, not pressed: they'd open on the person's screen).
        Describe("driver", AskAndWait(ask, "Is my graphics driver up to date?"));
        Shot("5-driver");
        Describe("update", AskAndWait(ask, "How do I check for Windows updates?"));
        Shot("6-update");

        // A false claim about the PC.
        Describe("wrong-gpu", AskAndWait(ask, "My RTX 5060 is running really hot in games, why?"));
        Shot("7-wrong-gpu");

        Ui.Run(() => ask.IsOpen = false);
        Environment.SetEnvironmentVariable("RIGSIGHT_RELAY_URL", null);
    }
}
