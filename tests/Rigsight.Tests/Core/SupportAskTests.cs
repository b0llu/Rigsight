using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

/// <summary>When the "Enjoying RigSight?" card may ask (the user's rules, 2026-10-20).</summary>
public sealed class SupportAskTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 18, 0, 0);
    private static readonly DateTime LongAgo = Now.AddDays(-40);

    private static bool May(SupportAskState s, DateTime? firstDay = null, int daysUsed = 25, bool badDay = false) =>
        SupportAsk.May(s, Now, firstDay ?? LongAgo, daysUsed, badDay).May;

    [Fact]
    public void Asks_only_once_the_app_is_known()
    {
        Assert.True(May(new()));
        Assert.False(May(new(), firstDay: Now.AddDays(-20))); // under three weeks of history
        Assert.True(May(new(), firstDay: Now.AddDays(-21)));
        Assert.False(May(new(), daysUsed: 9)); // under ten days of use
        Assert.False(SupportAsk.May(new(), Now, null, 25, false).May); // no history at all
    }

    [Fact]
    public void Never_on_a_bad_day() => Assert.False(May(new(), badDay: true));

    [Fact]
    public void Maybe_later_waits_30_days_then_60_and_the_third_time_is_the_last()
    {
        var once = new SupportAskState { Shown = 1, LastShown = Now.AddDays(-29) };
        Assert.False(May(once));
        once.LastShown = Now.AddDays(-30);
        Assert.True(May(once));

        var twice = new SupportAskState { Shown = 2, LastShown = Now.AddDays(-59) };
        Assert.False(May(twice));
        twice.LastShown = Now.AddDays(-60);
        Assert.True(May(twice));

        Assert.False(May(new SupportAskState { Shown = 3, LastShown = Now.AddYears(-1) }));
    }

    [Fact]
    public void A_coffee_or_dont_show_again_ends_it_and_the_menu_stops_standing_out()
    {
        Assert.False(May(new SupportAskState { Done = true }));
        Assert.True(new SupportAskState().Highlight);
        Assert.False(new SupportAskState { Done = true }.Highlight);
        Assert.False(new SupportAskState { Clicked = true }.Highlight); // the menu's button pressed once
        Assert.False(May(new SupportAskState { Clicked = true })); // and the card never asks on top of it
    }

    [Fact]
    public void A_showing_counts_whether_or_not_it_was_answered()
    {
        // Shown three times (closed, Esc, a click beside it, or the app shut while it was up): never a fourth.
        var s = new SupportAskState();
        foreach (int days in new[] { 0, 30, 90 })
        {
            var at = Now.AddDays(days);
            Assert.True(SupportAsk.May(s, at, LongAgo, 25, false).May);
            s.Shown++;
            s.LastShown = at;
        }
        Assert.False(SupportAsk.May(s, Now.AddYears(5), LongAgo, 25, false).May);
    }

    [Fact]
    public void Its_record_is_kept_in_the_settings_and_nothing_more()
    {
        var s = new RigsightSettings();
        s.Support.Shown = 2;
        s.Support.LastShown = Now;
        s.Support.Clicked = true;
        string json = SettingsStore.Serialize(s);
        Assert.DoesNotContain("Highlight", json); // worked out, not stored
        var back = SettingsStore.Deserialize(json).Support;
        Assert.Equal((2, Now, true, false), (back.Shown, back.LastShown, back.Clicked, back.Done));
        Assert.Equal(0, SettingsStore.Deserialize("""{ "SettingsVersion": 12 }""").Support.Shown); // settings from before it
    }

    [Fact]
    public void It_opens_the_buy_me_a_coffee_page() => Assert.Equal("https://buymeacoffee.com/bollu", SupportAsk.CoffeeUrl);
}
