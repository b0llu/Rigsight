using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Rigsight.Core.Ai;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;
using Rigsight.Views;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// Questions given from the admin site end to end (the person's ask, 2026-10-25: "did you check by actually giving free
/// questions"): a real relay (wrangler dev, never the live one) with its admin API, the real window and the real model.
/// This test PC is left 1 free question this week and given 2 with a message; the card shows, then three real questions
/// use the week's one and then the two given, and a fourth can't be sent. Runs only when RIGSIGHT_LIVE_RELAY names a
/// local relay started with ADMIN_DEV=1. Pictures go to screens\gift-*.png, what happened to gift.log.
/// </summary>
[Collection("UI")]
public sealed class GiftLiveTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string? RelayUrl = Environment.GetEnvironmentVariable("RIGSIGHT_LIVE_RELAY");
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");
    private void Note(string line) { Directory.CreateDirectory(Shots); File.AppendAllText(Path.Combine(Shots, "gift.log"), line + Environment.NewLine); }

    private FrameworkElement Panel() => (FrameworkElement)Visuals.Descendants<AskWindow>(host.Window).Single().FindName("Panel");

    private void Shot(string name)
    {
        Ui.Pump(300);
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
            using var file = File.Create(Path.Combine(Shots, $"gift-{name}.png"));
            png.Save(file);
        });
    }

    private static async Task<JsonNode?> Admin(string path, object? body = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri(RelayUrl!.TrimEnd('/') + "/") };
        var res = body is null ? await http.GetAsync($"admin/api/{path}") : await http.PostAsJsonAsync($"admin/api/{path}", body);
        string text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"admin {path}: {(int)res.StatusCode} {text}");
        return JsonNode.Parse(text);
    }

    private void Refresh(AskViewModel ask)
    {
        Ui.Run(() => ask.RefreshQuestionsAsync(theyAsked: true)).GetAwaiter().GetResult();
        Ui.Pump(300);
    }

    private void Ask(AskViewModel ask, string question)
    {
        Ui.Run(() =>
        {
            ask.NewCommand.Execute(null);
            ask.Draft = question;
            ask.SendCommand.Execute(null);
        });
        Ui.Pump(500);
        Ui.WaitFor(() => Ui.Run(() => !ask.IsAnswering), 120_000);
        Ui.Pump(1500); // the app refreshes its standing after each question
        var answer = Ui.Run(() => ask.Current?.Messages.OfType<AskAnswer>().LastOrDefault());
        Note($"asked \"{question}\": {(answer is null ? "NO ANSWER" : answer.Raw.Length > 0 ? "answered" : "empty")}; questions left {Ui.Run(() => ask.QuestionsLeft)}, plan left {Ui.Run(() => ask.Standing?.Plan?.Left)}, given {Ui.Run(() => ask.Standing?.Plan?.Given)}");
    }

    [Fact]
    public async Task Questions_given_are_told_once_then_used_after_the_week_s_own()
    {
        if (RelayUrl is null || RelayUrl.Contains("rigsight.app")) return;
        Environment.SetEnvironmentVariable("RIGSIGHT_RELAY_URL", RelayUrl);
        try
        {
            var ask = host.Shell.Ask;
            string pc = Relay.DeviceId;
            Note($"--- {DateTime.Now:HH:mm} device {pc}");
            Ui.Run(() =>
            {
                ask.IsPoppedOut = false;
                ask.IsOpen = true;
                if (ask.ShowFirstQuestion) ask.StartAskingCommand.Execute(null);
            });
            Refresh(ask);
            Assert.Equal("free", Ui.Run(() => ask.Standing?.Plan?.Id));

            // The admin site: 1 free question left this week, then 2 given with a message.
            await Admin($"devices/{pc}", new { action = "set_free", n = 1 });
            var said = await Admin($"devices/{pc}", new { action = "give_more", n = 2, message = "Thanks for testing the gifts.", reason = "live test" });
            Note($"admin said: {said?["said"]}");
            Refresh(ask);
            var gift = Ui.Run(() => ask.Gift);
            Note($"card: {gift?.Title} / {gift?.Text} / note {gift?.Note}");
            Assert.Equal("You've got 2 extra questions", gift?.Title);
            Assert.Equal("Thanks for testing the gifts.", gift?.Note);
            Assert.Equal(3, Ui.Run(() => ask.QuestionsLeft));
            Shot("1-card");

            // The week's one first (the card closes with the question), then the two given.
            Ask(ask, "How hot is my CPU right now?");
            Assert.Null(Ui.Run(() => ask.Gift));
            Assert.Equal((0, 2), Ui.Run(() => (ask.Standing!.Plan!.Left, ask.Standing.Plan.Given)));
            Shot("2-week-used");
            Ask(ask, "Is my graphics driver up to date?");
            Assert.Equal(1, Ui.Run(() => ask.Standing!.Plan!.Given));
            Shot("3-one-given-left");
            Ask(ask, "How much memory does Chrome use?");
            Assert.Equal(0, Ui.Run(() => ask.QuestionsLeft));
            Assert.True(Ui.Run(() => ask.IsOutOfQuestions));
            Ui.Run(() => ask.Draft = "One more?");
            Assert.False(Ui.Run(() => ask.SendCommand.CanExecute(null)));
            Shot("4-all-used");

            // The relay's own record: how each was paid for, newest first.
            var questions = (await Admin($"devices/{pc}"))?["questions"]?.AsArray().Select(q => q?["paid_by"]?.ToString()).ToList();
            Note($"relay paid_by, newest first: {string.Join(", ", questions ?? [])}");
            Assert.Equal(["given", "given", "free"], questions);

            // Seen once: opened again, the card isn't told again.
            Refresh(ask);
            Assert.Null(Ui.Run(() => ask.Gift));
            Ui.Run(() => { ask.Draft = ""; ask.IsOpen = false; });
        }
        finally { Environment.SetEnvironmentVariable("RIGSIGHT_RELAY_URL", null); }
    }
}
