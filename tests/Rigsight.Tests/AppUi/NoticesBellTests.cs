using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Notices;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// The bell in the title bar (the notifications round, approved mock-up LWycKU9ZC8jzeVH8bVjPWm, 2026-11-07): beside the
/// window's buttons, in the same place with AI on or off; its number counts what wasn't seen; its list shows the last 30
/// days by day, the held-back ones marked; a row opens its page. The list and Settings › Notifications are saved as
/// pictures to compare with the mock-up.
/// </summary>
[Collection("UI")]
public sealed class NoticesBellTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");

    private static void Seed()
    {
        using var db = RigsightDb.OpenWriter(RigsightPaths.Database);
        var now = DateTime.Now;
        db.SaveNotice(new NoticeRecord(now.AddDays(-1).Date.AddHours(20.75), "Internet", NoticeLevel.Problem, "Your internet is down",
            "Your router is reachable, but there's no internet connection.", "network", null, null, null, true));
        db.SaveNotice(new NoticeRecord(now.AddMinutes(-150), "Recap", NoticeLevel.Recap, "Yesterday: 6h 12m active", "Mostly Dota 2, for 3 hours and 5 minutes.", "reports", "yesterday", null, null, true));
        db.SaveNotice(new NoticeRecord(now.AddMinutes(-40), "Space", NoticeLevel.Worth, "Your C drive is almost full", "It has 8.0 GB left of 476.0 GB.", "storage", "C", null, "drive-full|C|x", false));
        db.SaveNotice(new NoticeRecord(now.AddMinutes(-10), "FanStopped", NoticeLevel.Problem, "Your GPU fan stopped",
            "It showed 0 rpm for 3 minutes from 8:37 PM with the GPU at 74°C, though it always turns at that heat. Check it isn't blocked or unplugged.", "fans", null, null, null, true));
    }

    private Button Bell() => Ui.Run(() => (Button)host.Window.FindName("BellButton"));

    private double BellX() => Ui.Run(() => Bell().TransformToAncestor(host.Window).Transform(new Point(0, 0)).X);

    [Fact]
    public void The_bell_sits_beside_the_window_buttons_with_AI_on_or_off()
    {
        Ui.Run(() => host.Shell.Settings.Update(s => s.Ai.Mode = AiMode.RigSight));
        Ui.Pump(200);
        double on = BellX();
        Assert.True(Ui.Run(() => Bell().IsVisible));
        var min = Ui.Run(() => (Button)host.Window.FindName("MinButton"));
        double minX = Ui.Run(() => min.TransformToAncestor(host.Window).Transform(new Point(0, 0)).X);
        Assert.InRange(minX - (on + 30), 0, 12); // just left of Minimize

        Ui.Run(() => host.Shell.Settings.Update(s => s.Ai.Mode = AiMode.Off));
        Ui.Pump(200);
        Assert.False(Ui.Run(() => host.Shell.ShowAskBar));
        Assert.True(Ui.Run(() => Bell().IsVisible));
        Assert.Equal(on, BellX(), 0.5);
        Ui.Run(() => host.Shell.Settings.Update(s => s.Ai.Mode = AiMode.RigSight));
    }

    [Fact]
    public void The_list_counts_what_was_not_seen_shows_the_days_and_a_row_opens_its_page()
    {
        Seed();
        Ui.Run(() => host.Shell.Settings.Update(s => { s.NoticesSeen = 0; s.NoticesRead.Clear(); }));
        var notices = host.Shell.Notices;
        Ui.Run(() => notices.LoadAsync());
        Ui.Pump(200);
        Assert.True(Ui.Run(() => notices.Unseen) >= 4);
        Assert.True(Ui.Run(() => notices.HasUnseen));
        Assert.Equal(["Today", "Yesterday"], Ui.Run(() => notices.Days.Select(d => d.Title).Take(2).ToList()));
        var held = Ui.Run(() => notices.Days.SelectMany(d => d.Rows).First(r => r.Title == "Your C drive is almost full"));
        Assert.True(held.HeldBack);
        Assert.Equal("WarmBrush", held.BrushKey);

        Shot();

        // Opening the list reads nothing by itself.
        int before = Ui.Run(() => notices.Unseen);
        Ui.Run(() => notices.ToggleCommand.Execute(null));
        Assert.Equal(before, Ui.Run(() => notices.Unseen));
        Ui.Run(() => notices.IsOpen = false);

        // A row opens its page, as its card would have, and is read: one fewer, and still read after a reload.
        var fan = Ui.Run(() => notices.Days.SelectMany(d => d.Rows).First(r => r.Title == "Your GPU fan stopped"));
        Ui.Run(() => notices.OpenRowCommand.Execute(fan));
        Ui.Pump(200);
        Assert.Equal("fans", Ui.Run(() => host.Shell.CurrentPage));
        Assert.False(Ui.Run(() => notices.IsOpen));
        Assert.False(fan.Unseen);
        Assert.Equal(before - 1, Ui.Run(() => notices.Unseen));
        Ui.Run(() => notices.LoadAsync());
        Assert.Equal(before - 1, Ui.Run(() => notices.Unseen));
        Assert.False(Ui.Run(() => notices.Days.SelectMany(d => d.Rows).First(r => r.Title == "Your GPU fan stopped").Unseen));

        // Mark all as read: no dots, no number, and the next start knows it.
        Ui.Run(() => notices.MarkAllReadCommand.Execute(null));
        Assert.Equal(0, Ui.Run(() => notices.Unseen));
        Assert.All(Ui.Run(() => notices.Days.SelectMany(d => d.Rows).ToList()), r => Assert.False(r.Unseen));
        Assert.True(Ui.Run(() => host.Shell.Settings.Current.NoticesSeen) > 0);
        Assert.Empty(Ui.Run(() => host.Shell.Settings.Current.NoticesRead));
        Ui.Run(() => notices.LoadAsync());
        Assert.Equal(0, Ui.Run(() => notices.Unseen));
    }

    [Fact]
    public void Settings_notifications_is_grouped_by_when_they_show()
    {
        Ui.Run(() => host.Shell.SettingsPage.Tab = "notifications");
        host.Show("settings", 600);
        var texts = Ui.Run(() => Visuals.Descendants<TextBlock>(host.Window).Where(t => t.IsVisible).Select(t => t.Text).ToList());
        foreach (var heading in new[] { "Problems", "Worth knowing", "Recaps", "How they show", "Fans that stop", "Hotter than usual", "Rewind ready", "Cards a day, besides problems" })
            Assert.Contains(heading, texts);
        Assert.True(texts.IndexOf("Problems") < texts.IndexOf("Worth knowing") && texts.IndexOf("Worth knowing") < texts.IndexOf("Recaps"));
        WindowShot("settings-notifications");
    }

    /// <summary>The list's card drawn on its own (a pop-up opened off-screen would show on the real screen and take the mouse).</summary>
    private static void Shot() => Ui.Run(() =>
    {
        var window = Application.Current.MainWindow;
        var popup = (Popup)window.FindName("NoticesDrop");
        var card = (FrameworkElement)popup.Child;
        card.DataContext = window.DataContext;
        card.Measure(new Size(468, double.PositiveInfinity));
        card.Arrange(new Rect(card.DesiredSize));
        card.UpdateLayout();
        Save(card, (int)Math.Ceiling(card.ActualWidth + 28), (int)Math.Ceiling(card.ActualHeight + 20), "bell-list");
    });

    private void WindowShot(string name) => Ui.Run(() => Save(host.Window, (int)host.Window.ActualWidth, (int)host.Window.ActualHeight, name));

    private static void Save(FrameworkElement what, int w, int h, string name)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(what);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Shots);
        using var file = File.Create(Path.Combine(Shots, $"{name}.png"));
        png.Save(file);
    }
}
