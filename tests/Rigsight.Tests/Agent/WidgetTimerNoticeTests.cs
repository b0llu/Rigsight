using Rigsight.Agent.Widgets;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>
/// A timer says when it's up even while its widget isn't shown (the audit, 2026-11-11: with Desktop widgets off, or the
/// widget switched off, it said nothing until the widget came back, maybe hours later), and says it once.
/// </summary>
public class WidgetTimerNoticeTests
{
    [Fact]
    public void A_timer_on_a_hidden_widget_still_says_when_its_up_and_only_once()
    {
        var timer = new WidgetTile { Kind = WidgetTiles.Timer, W = 6, H = 2, Minutes = 1, RunningSince = DateTimeOffset.Now.ToUnixTimeMilliseconds() - 120_000 };
        var settings = new RigsightSettings
        {
            WidgetsShown = false,
            Widgets = [new WidgetConfig { Style = WidgetStyle.Custom, Id = "custom-timer", Name = "Desk", Enabled = false, Tiles = [timer] }],
        };
        var said = new List<(string Title, string Body)>();
        var manager = new WidgetManager(() => settings, change => change(settings), _ => { }) { TimerUp = (t, b) => said.Add((t, b)) };
        manager.Update(new WidgetData(), false, false, false, false, IntPtr.Zero);
        var notice = Assert.Single(said);
        Assert.Equal("Your timer is up", notice.Title);
        Assert.False(WidgetTimer.Running(settings.Widgets[0].Tiles![0]));
        manager.Update(new WidgetData(), false, false, false, false, IntPtr.Zero);
        Assert.Single(said);
    }
}
