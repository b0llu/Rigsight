using System.Collections;
using System.Drawing;
using System.Reflection;
using Rigsight.Agent.Ui;
using Rigsight.Agent.Widgets;
using Rigsight.Core.Settings;
using static Rigsight.Tests.Agent.WidgetSamples;

namespace Rigsight.Tests.Agent;

/// <summary>The notification card, drawn to a bitmap (never shown), and when notices are held back.</summary>
[Collection("Drawing")]
public class NotificationTests
{
    private const string LongBody = "Likely cause: NVIDIA driver. You played for 2h 14m. Details are on the Crashes page whenever you want them. " +
        "Windows logged a display driver reset a few seconds before the game closed, and the GPU was at 83°C at the time.";

    [Fact]
    public void Every_kind_of_card_draws_at_every_size()
    {
        foreach (var kind in Enum.GetValues<NoticeKind>())
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f, 3f })
            foreach (bool hover in new[] { false, true })
                foreach (var body in new[] { "", "Short.", LongBody, string.Join(' ', Enumerable.Repeat(LongBody, 4)) })
                {
                    using var bmp = ToastRenderer.Render(new Notice(kind, "Running hot", body), scale, hover, out var close);
                    Assert.Equal((int)Math.Ceiling(404 * scale), bmp.Width);
                    Assert.True(bmp.Height >= (int)Math.Ceiling(82 * scale), $"{bmp.Height} at {scale}x");
                    Assert.True(bmp.Height < 800 * scale);
                    Assert.Equal(new RectangleF(374 * scale, 10 * scale, 20 * scale, 20 * scale), close);
                    Assert.True(Coverage(bmp) > 0.9);
                }
    }

    [Fact]
    public void A_longer_text_makes_a_taller_card_but_a_long_title_doesnt()
    {
        int Height(string title, string body)
        {
            using var bmp = ToastRenderer.Render(new Notice(NoticeKind.Recap, title, body), 1, false, out _);
            return bmp.Height;
        }
        Assert.True(Height("T", LongBody) > Height("T", "Short."));
        Assert.Equal(Height("T", "Short."), Height(new string('W', 300), "Short."));
        Assert.True(Height("T", "") <= Height("T", "Short."));
    }

    [Fact]
    public void Cards_are_black_or_white_with_the_app_theme()
    {
        var n = new Notice(NoticeKind.Recap, "Yesterday", "You were on for 6h 12m.");
        using var dark = ToastRenderer.Render(n, 1, false, light: false, out _);
        using var light = ToastRenderer.Render(n, 1, false, light: true, out _);
        // Well inside the card, right of the icon and below the text: plain background.
        var d = dark.GetPixel(dark.Width - 40, dark.Height - 12);
        var l = light.GetPixel(light.Width - 40, light.Height - 12);
        Assert.True(d.R == d.G && d.G == d.B && d.R < 30, $"dark card {d}"); // neutral, not the old navy
        Assert.True(l.R == l.G && l.G == l.B && l.R > 245, $"light card {l}");
        // Each kind's colour stays its own on white too.
        var kinds = Enum.GetValues<NoticeKind>();
        Assert.Equal(5, kinds.Select(k => ToastRenderer.Accent(k, light: true)).Distinct().Count());
    }

    [Fact]
    public void The_close_button_shows_on_hover()
    {
        var n = new Notice(NoticeKind.Session, "Dota 2 session", "Played for 2h 14m.");
        using var plain = ToastRenderer.Render(n, 1, false, out _);
        using var hover = ToastRenderer.Render(n, 1, true, out _);
        Assert.False(SamePixels(plain, hover));
    }

    [Fact]
    public void Each_kind_has_its_own_label_and_colour()
    {
        var kinds = Enum.GetValues<NoticeKind>();
        Assert.Equal(kinds.Length, kinds.Select(ToastRenderer.KindLabel).Distinct().Count());
        Assert.All(kinds, k => Assert.Equal(ToastRenderer.KindLabel(k).ToUpperInvariant(), ToastRenderer.KindLabel(k)));
        Assert.Equal("TEMPERATURE ALERT", ToastRenderer.KindLabel(NoticeKind.Alert));
        Assert.Equal(5, kinds.Select(k => ToastRenderer.Accent(k)).Distinct().Count()); // overlay and update share one
    }

    [Fact]
    [Trait("Category", "Machine")]
    public void A_card_shows_the_apps_icon_when_it_has_one()
    {
        using var without = ToastRenderer.Render(new Notice(NoticeKind.Session, "T", "B", IconPath: @"C:\no\such.exe"), 1, false, out _);
        using var with = ToastRenderer.Render(new Notice(NoticeKind.Session, "T", "B", IconPath: Path.Combine(Environment.SystemDirectory, "notepad.exe")), 1, false, out _);
        Assert.False(SamePixels(without, with));
    }

    // ── Held back during fullscreen ──

    private sealed class Center
    {
        public RigsightSettings Settings { get; } = new();
        public List<Notice> Sent { get; } = [];
        public InGame Result { get; set; } = InGame.Shown;
        public NotificationCenter Notices { get; }

        public Center()
        {
            // No tray and no windows: only notices that are held or drawn in the game are sent here.
            Notices = new NotificationCenter(() => Settings, null!, (_, _) => { }, n =>
            {
                Sent.Add(n);
                return Result;
            });
            Notices.SetFullscreen(true);
        }

        public int Held => ((ICollection)typeof(NotificationCenter).GetField("_held", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Notices)!).Count;
    }

    [Fact]
    public void Recaps_and_summaries_wait_while_a_fullscreen_app_is_in_front()
    {
        var c = new Center();
        c.Notices.Show(new Notice(NoticeKind.Recap, "Yesterday", "…"));
        c.Notices.Show(new Notice(NoticeKind.Session, "Dota 2 session", "…"));
        Assert.Equal(2, c.Held);
        Assert.Empty(c.Sent);
    }

    [Fact]
    public void Urgent_alerts_go_into_the_game()
    {
        var c = new Center();
        c.Notices.Show(new Notice(NoticeKind.Alert, "Running hot", "GPU 90°", Urgent: true));
        Assert.Single(c.Sent);
        Assert.Equal(0, c.Held);
    }

    [Fact]
    public void An_urgent_alert_that_cant_reach_the_game_waits()
    {
        var c = new Center { Result = InGame.Unreachable };
        c.Notices.Show(new Notice(NoticeKind.Alert, "Running hot", "GPU 90°", Urgent: true));
        Assert.Single(c.Sent);
        Assert.Equal(1, c.Held);
    }

    [Fact]
    public void Without_quiet_mode_everything_goes_into_the_game()
    {
        var c = new Center();
        c.Settings.Alerts.QuietDuringFullscreen = false;
        c.Notices.Show(new Notice(NoticeKind.Recap, "Yesterday", "…"));
        Assert.Single(c.Sent);
        Assert.Equal(0, c.Held);
    }

    [Fact]
    public void Closing_everything_drops_what_was_held()
    {
        var c = new Center();
        for (int i = 0; i < 5; i++) c.Notices.Show(new Notice(NoticeKind.Recap, $"#{i}", "…"));
        Assert.Equal(5, c.Held);
        c.Notices.CloseAll();
        Assert.Equal(0, c.Held);
    }

    [Fact]
    public void Staying_fullscreen_releases_nothing()
    {
        var c = new Center();
        c.Notices.Show(new Notice(NoticeKind.Recap, "Yesterday", "…"));
        c.Notices.SetFullscreen(true);
        c.Notices.SetFullscreen(true);
        Assert.Equal(1, c.Held);
    }
}
