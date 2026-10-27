using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>"What's new": once after an update, never on a new install, every update on request.</summary>
[Collection("UI")]
public sealed class WhatsNewTests
{
    private static readonly Version V070 = new(0, 7, 0);

    /// <summary>Every page a feature's button can open: the sidebar's.</summary>
    private static readonly string[] Pages = ["home", "reports", "apps", "games", "processes", "crashes", "timeline", "temperatures", "fans", "memory", "storage", "network", "sensors",
        "widgets", "overlay", "taskbar", "settings"];

    private static (WhatsNewViewModel Card, SettingsModel Settings) Make(string? seen, Version? current = null)
    {
        var settings = Kit.OfflineSettings(new RigsightSettings { WhatsNewSeen = seen, WhatsNewAfter = WhatsNewAfter.Open });
        return (Ui.Run(() => new WhatsNewViewModel(settings, current ?? V070)), settings);
    }

    [Fact]
    public void This_version_has_its_whats_new()
    {
        // Written before it ships: the app's own version (from Directory.Build.props) must have an entry.
        var built = typeof(Changelog).Assembly.GetName().Version!;
        Assert.NotNull(Changelog.For(built));
    }

    [Fact]
    public void Entries_are_newest_first_one_sentence_each_and_plain()
    {
        var versions = Changelog.Releases.Select(r => r.Version).ToList();
        Assert.Equal(versions.OrderDescending(), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.All(Changelog.Releases, r =>
        {
            Assert.NotEmpty(r.Sections);
            Assert.All(r.Sections.SelectMany(s => s.Items), item =>
            {
                Assert.InRange(item.Length, 10, 160);         // a sentence that says what changed, not a paragraph
                Assert.EndsWith(".", item);
                Assert.DoesNotContain("GitHub", item);         // in-app text never names it
            });
        });
        Assert.Equal(["NEW", "BETTER", "FIXED"], Changelog.Releases.First(r => r.Version == new Version(0, 6, 0)).Sections.Select(s => s.Title));
    }

    [Fact]
    public void A_new_install_sees_nothing_and_is_marked_up_to_date()
    {
        var (card, settings) = Make(seen: null);
        Ui.Run(() =>
        {
            card.CheckOnStart();
            Assert.False(card.IsOpen);
            Assert.Equal("0.7.0", settings.Current.WhatsNewSeen);
        });
    }

    [Fact]
    public void Switched_off_it_doesnt_open_after_an_update_but_see_all_still_does()
    {
        var (card, settings) = Make(seen: "0.6.3");
        settings.Update(s => s.WhatsNewAfter = WhatsNewAfter.Off);
        Ui.Run(() =>
        {
            card.CheckOnStart();
            Assert.False(card.IsOpen);
            Assert.Equal("0.7.0", settings.Current.WhatsNewSeen);
            card.SeeAllCommand.Execute(null);
            Assert.True(card.IsOpen);
        });
    }

    [Fact]
    public void After_an_update_it_shows_what_came_since_once()
    {
        var (card, settings) = Make(seen: "0.6.3");
        Ui.Run(() =>
        {
            card.CheckOnStart();
            Assert.True(card.IsOpen);
            Assert.False(card.ShowingAll);
            Assert.Equal([V070], card.Shown.Select(r => r.Version));
            Assert.Equal("What's new", card.Heading);
            Assert.Equal("0.6.3", settings.Current.WhatsNewSeen); // not until it's closed

            card.CloseCommand.Execute(null);
            Assert.False(card.IsOpen);
            Assert.Equal("0.7.0", settings.Current.WhatsNewSeen);
            card.CheckOnStart();                                   // the next time: nothing
            Assert.False(card.IsOpen);
        });
    }

    [Fact]
    public void Skipping_versions_shows_each_one_missed()
    {
        var (card, _) = Make(seen: "0.6.1");
        Ui.Run(() =>
        {
            card.CheckOnStart();
            Assert.Equal([V070, new Version(0, 6, 3), new Version(0, 6, 2)], card.Shown.Select(r => r.Version));
            // The heading stays What's new; under it, how many came since the one last seen. The newest in full, the
            // rest one row each, closed until opened (approved mock-up, 2026-10-24).
            Assert.Equal("What's new", card.Heading);
            Assert.Equal("RigSight 0.7.0 · 3 updates since 0.6.1, the last you saw", card.SubHeading);
            Assert.Equal(V070, card.Newest!.Version);
            Assert.Equal([new Version(0, 6, 3), new Version(0, 6, 2)], card.Older.Select(r => r.Notes.Version));
            Assert.All(card.Older, r => Assert.False(r.IsExpanded));
            card.Older[0].ToggleCommand.Execute(null);
            Assert.True(card.Older[0].IsExpanded);
            Assert.NotEmpty(card.Counts);
        });
    }

    [Fact]
    public void The_summary_counts_what_came_and_a_single_update_says_its_version_and_date()
    {
        var (card, _) = Make(seen: "0.33.0", current: new Version(0, 34, 0));
        Ui.Run(() =>
        {
            card.CheckOnStart();
            var r = Changelog.For(new Version(0, 34, 0))!;
            Assert.Equal($"RigSight 0.34.0 · {r.DateText}", card.SubHeading);
            Assert.Equal(r.Feature is null ? r.New.Length > 0 : true, card.Counts.Count > 0);
            if (r.Feature is not null) Assert.Equal("1 new feature", card.Counts[0]);
            Assert.False(card.HasOlder);
        });
    }

    [Theory]
    [InlineData("Opening the Crashes page is quicker because its list keeps the crashes it already showed.", "crashes")]
    [InlineData("A game's page on Games no longer jumps back to the top every couple of minutes.", "games")]
    [InlineData("Switch off Show What's new after an update in Settings, About, and this card won't open by itself any more.", "settings")]
    [InlineData("You can now select and copy text anywhere in RigSight by dragging over it or double-clicking a word, then pressing Ctrl+C.", null)]
    [InlineData("A widget dragged onto the taskbar stays over it.", null)] // "taskbar", not the Taskbar page
    public void A_line_opens_the_page_it_names_by_its_name(string text, string? page)
    {
        var line = ReleaseLine.Of(text, "better");
        Assert.Equal(page, line.Page);
        Assert.Equal(page is not null, line.CanOpen);
        Assert.False(string.IsNullOrEmpty(line.Icon));
    }

    [Fact]
    public void After_an_updates_card_its_features_page_keeps_a_dot_until_opened()
    {
        var feature = Changelog.Releases.First(r => r.Feature is { IsRemoval: false, ShowsButton: true } f && ViewModels.SidebarViewModel.IconOf(f.Page) is not null);
        var before = Changelog.Releases.First(r => r.Version < feature.Version).Version;
        var (card, settings) = Make(seen: before.ToString(3), current: feature.Version);
        Ui.Run(() =>
        {
            var sidebar = new ViewModels.SidebarViewModel(settings, _ => { });
            var entry = sidebar.Hardware.Concat(sidebar.Main).Concat(sidebar.OnScreen).First(e => e.Key == feature.Feature!.Page);
            card.CheckOnStart();
            Assert.False(entry.IsNew); // not while the card is still open
            card.CloseCommand.Execute(null);
            Assert.True(entry.IsNew);
            Assert.EndsWith($" is new in {feature.Version.ToString(3)}", entry.NewText);
            sidebar.Select(feature.Feature!.Page);
            Assert.False(entry.IsNew);
            Assert.Null(settings.Current.NewFeaturePage);
        });
    }

    [Fact]
    public void Every_update_from_Settings_and_a_card_switched_off_leave_no_dot()
    {
        var feature = Changelog.Releases.First(r => r.Feature is { IsRemoval: false, ShowsButton: true } f && ViewModels.SidebarViewModel.IconOf(f.Page) is not null);
        var before = Changelog.Releases.First(r => r.Version < feature.Version).Version;
        var (card, settings) = Make(seen: before.ToString(3), current: feature.Version);
        Ui.Run(() =>
        {
            card.SeeAllCommand.Execute(null);
            card.CloseCommand.Execute(null);
            Assert.Null(settings.Current.NewFeaturePage);
        });
        // Someone who switched What's new off keeps it off: no card, and no dot either.
        var (off, offSettings) = Make(seen: before.ToString(3), current: feature.Version);
        Ui.Run(() =>
        {
            offSettings.Update(s => s.WhatsNewAfter = WhatsNewAfter.Off);
            off.CheckOnStart();
            Assert.False(off.IsOpen);
            Assert.Equal(WhatsNewAfter.Off, offSettings.Current.WhatsNewAfter);
            Assert.Null(offSettings.Current.NewFeaturePage);
        });
    }

    [Fact]
    public void An_update_with_nothing_to_tell_shows_nothing()
    {
        var (card, settings) = Make(seen: "0.7.1", current: new Version(0, 7, 2)); // no entry for 0.7.2
        Ui.Run(() =>
        {
            card.CheckOnStart();
            Assert.False(card.IsOpen);
            Assert.Equal("0.7.2", settings.Current.WhatsNewSeen);
        });
    }

    [Fact]
    public void Every_update_can_be_seen_any_time()
    {
        var (card, _) = Make(seen: "0.7.0");
        Ui.Run(() =>
        {
            card.SeeAllCommand.Execute(null);
            Assert.True(card.IsOpen);
            Assert.True(card.ShowingAll);
            Assert.Equal("Every update", card.Heading);
            Assert.Equal(Changelog.Releases, card.Shown);
        });
    }

    [Fact]
    public void A_line_says_what_changed_not_that_something_is_better()
    {
        // The ways a line says nothing: a bare "better", "smarter", "tidier", "clearer wording" with no word on what is
        // different now (see the note on Changelog for how a line is written).
        string[] empty = ["Better ", "Improved ", "Smarter ", "A tidier ", "A neater ", "A cleaner ", "Calmer ", "Clearer wording", "Safer ", "Various ", "Minor "];
        var lines = Changelog.Releases.SelectMany(r => r.Sections.SelectMany(s => s.Items).Concat(r.Feature?.Points ?? []));
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain(empty, start => line.StartsWith(start, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("processor", line, StringComparison.OrdinalIgnoreCase); // CPU, as the app says
            Assert.False(line.Contains("  "), line);
            Assert.True(char.IsUpper(line[0]) || char.IsDigit(line[0]) || line[0] == '"', line);
        });
        // No line twice in one release, and none of a release's lines repeats its feature card.
        Assert.All(Changelog.Releases, r =>
        {
            var all = r.Sections.SelectMany(s => s.Items).Concat(r.Feature?.Points ?? []).ToList();
            Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
    }

    // ---- A headline feature ----

    [Fact]
    public void A_headline_feature_says_what_it_is_in_a_few_short_lines_and_where_it_opens()
    {
        var features = Changelog.Releases.Where(r => r.Feature is not null).Select(r => r.Feature!).ToList();
        Assert.NotEmpty(features);
        Assert.All(features, f =>
        {
            Assert.InRange(f.Name.Length, 3, 24);
            Assert.InRange(f.Pitch.Length, 10, 110);
            Assert.InRange(f.Points.Length, 2, 4);
            Assert.All(f.Points, p =>
            {
                Assert.InRange(p.Length, 10, 130);
                Assert.EndsWith(".", p);
            });
            // A page that exists, or one of Settings' tabs ("settings:rewind").
            if (f.Page.StartsWith("settings:")) Assert.Contains(f.Page["settings:".Length..], Rigsight.ViewModels.SettingsViewModel.Tabs);
            else Assert.Contains(f.Page, Pages);
            Assert.StartsWith("Open ", f.OpenText);
            Assert.Equal(f.HasButton && !f.IsRemoval, f.ShowsButton);
            Assert.True(f.Icon.Length == 1 || f.Icon is "fan" or "network");
            Assert.Contains(f.Tag, new[] { "NEW PAGE", "NEW FEATURE", "EXPERIMENTAL", "REMOVED" });
        });
        // A change in how Rigsight runs has no page to try it on, so its card has no button (0.23.0, standard accounts).
        var accounts = Changelog.For(new Version(0, 23, 0))!.Feature!;
        Assert.Equal("Standard accounts", accounts.Name);
        Assert.False(accounts.ShowsButton);
        Assert.True(Changelog.For(new Version(0, 22, 0))!.Feature!.ShowsButton);
        // A page of its own opens by its name; a feature opens the page it lives on.
        var timeline = Changelog.Releases.Single(r => r.Version == new Version(0, 15, 0)).Feature!;
        Assert.Equal(("Timeline", "NEW PAGE", "Open Timeline"), (timeline.Name, timeline.Tag, timeline.OpenText));
        var taskbar = Changelog.Releases.Single(r => r.Version == new Version(0, 9, 0)).Feature!;
        Assert.Equal(("NEW FEATURE", "Open Taskbar", "taskbar"), (taskbar.Tag, taskbar.OpenText, taskbar.Page));
        // What a feature block says isn't said again in the list under it.
        Assert.All(Changelog.Releases.Where(r => r.Feature is not null), r =>
            Assert.DoesNotContain(r.New, line => line.StartsWith($"A {r.Feature!.Name} page", StringComparison.OrdinalIgnoreCase)));
        Assert.Null(Changelog.Releases.Single(r => r.Version == new Version(0, 14, 4)).Feature); // most releases have none
    }

    [Fact]
    public void Opening_a_feature_closes_the_card_and_goes_to_its_page()
    {
        var settings = Kit.OfflineSettings(new RigsightSettings { WhatsNewSeen = "0.14.4", WhatsNewAfter = WhatsNewAfter.Open });
        var opened = new List<string>();
        var asked = new List<string>();
        var card = Ui.Run(() => new WhatsNewViewModel(settings, new Version(0, 15, 0), opened.Add, page =>
        {
            asked.Add(page);
            return Task.FromResult<string?>("31 changes already found on this PC, back to June 2025.");
        }));
        Ui.Run(card.CheckOnStart);
        Ui.Pump(50);
        Ui.Run(() =>
        {
            Assert.True(card.IsOpen);
            var feature = card.Shown[0].Feature!;
            // Something true about this PC, asked for the feature's page.
            Assert.Equal(["timeline"], asked);
            Assert.Equal("31 changes already found on this PC, back to June 2025.", feature.Fact); // shown in the feature's own block

            card.OpenFeatureCommand.Execute(feature);
            Assert.False(card.IsOpen);
            Assert.Equal(["timeline"], opened);
            Assert.Equal("0.15.0", settings.Current.WhatsNewSeen);
        });
    }

    [Fact]
    public void A_card_without_a_feature_or_without_anything_to_say_about_this_PC_shows_no_fact()
    {
        var (card, _) = Make(seen: "0.6.0"); // up to 0.7.0: no feature in those
        Ui.Run(card.CheckOnStart);
        Ui.Run(() => Assert.All(card.Shown, n => Assert.Null(n.Feature)));

        var settings = Kit.OfflineSettings(new RigsightSettings { WhatsNewSeen = "0.14.4", WhatsNewAfter = WhatsNewAfter.Open });
        var quiet = Ui.Run(() => new WhatsNewViewModel(settings, new Version(0, 15, 0), _ => { }, _ => Task.FromResult<string?>(null)));
        Ui.Run(quiet.CheckOnStart);
        Ui.Pump(50);
        Ui.Run(() =>
        {
            Assert.True(quiet.IsOpen);
            Assert.Null(quiet.Shown[0].Feature!.Fact); // and the one from the card before is gone
        });
    }

    // The title bar's button (2026-10-27, the default: people asked not to have What's new open up front): nothing opens by
    // itself, the button waits across restarts, opens the update's own card, and goes once that's closed.
    [Fact]
    public void As_a_button_it_waits_in_the_title_bar_until_read()
    {
        var (card, settings) = Make(seen: "0.6.3");
        Ui.Run(() =>
        {
            settings.Update(s => s.WhatsNewAfter = WhatsNewAfter.Button);
            card.CheckOnStart();
            Assert.False(card.IsOpen);
            Assert.True(card.IsWaiting);
            Assert.Equal("0.6.3", settings.Current.WhatsNewSeen);
        });
        // Opened again before reading it: still waiting.
        var again = Ui.Run(() => new WhatsNewViewModel(settings, V070));
        Ui.Run(() =>
        {
            again.CheckOnStart();
            Assert.True(again.IsWaiting);
            again.OpenWaitingCommand.Execute(null);
            Assert.True(again.IsOpen);
            Assert.False(again.ShowingAll);
            Assert.NotNull(again.Newest);
            again.CloseCommand.Execute(null);
            Assert.False(again.IsWaiting);
            Assert.Equal("0.7.0", settings.Current.WhatsNewSeen);
        });
    }

    // Settings from before the choice: the switch on becomes the button, off stays off.
    [Theory]
    [InlineData(true, WhatsNewAfter.Button)]
    [InlineData(false, WhatsNewAfter.Off)]
    public void The_old_switch_becomes_the_choice(bool shown, WhatsNewAfter after)
    {
        var s = SettingsStore.Deserialize($$"""{ "SettingsVersion": 10, "ShowWhatsNew": {{(shown ? "true" : "false")}} }""");
        Assert.Equal(after, s.WhatsNewAfter);
        Assert.Null(s.ShowWhatsNew);
        Assert.DoesNotContain("ShowWhatsNew", System.Text.Json.JsonSerializer.Serialize(s, SettingsStore.JsonOptions));
    }
}
