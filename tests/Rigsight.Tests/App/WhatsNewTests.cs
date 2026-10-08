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
    private static readonly string[] Pages = ["home", "reports", "apps", "crashes", "timeline", "temperatures", "fans", "memory", "storage", "network", "sensors",
        "widgets", "overlay", "taskbar"];

    private static (WhatsNewViewModel Card, SettingsModel Settings) Make(string? seen, Version? current = null)
    {
        var settings = Kit.OfflineSettings(new RigsightSettings { WhatsNewSeen = seen });
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
    public void Entries_are_newest_first_short_and_plain()
    {
        var versions = Changelog.Releases.Select(r => r.Version).ToList();
        Assert.Equal(versions.OrderDescending(), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.All(Changelog.Releases, r =>
        {
            Assert.NotEmpty(r.Sections);
            Assert.All(r.Sections.SelectMany(s => s.Items), item =>
            {
                Assert.InRange(item.Length, 10, 90);          // a headline, not an explanation
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
            Assert.Equal("What's new since you last looked", card.Heading);
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

    // ---- A headline feature ----

    [Fact]
    public void A_headline_feature_says_what_it_is_in_a_few_short_lines_and_where_it_opens()
    {
        var features = Changelog.Releases.Where(r => r.Feature is not null).Select(r => r.Feature!).ToList();
        Assert.NotEmpty(features);
        Assert.All(features, f =>
        {
            Assert.InRange(f.Name.Length, 3, 24);
            Assert.InRange(f.Pitch.Length, 10, 70);
            Assert.InRange(f.Points.Length, 2, 4);
            Assert.All(f.Points, p =>
            {
                Assert.InRange(p.Length, 10, 90);
                Assert.EndsWith(".", p);
            });
            Assert.Contains(f.Page, Pages); // a page that exists
            Assert.StartsWith("Open ", f.OpenText);
            Assert.True(f.Icon.Length == 1 || f.Icon is "fan" or "network");
            Assert.Contains(f.Tag, new[] { "NEW PAGE", "NEW FEATURE", "EXPERIMENTAL", "REMOVED" });
        });
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
        var settings = Kit.OfflineSettings(new RigsightSettings { WhatsNewSeen = "0.14.4" });
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
            Assert.Equal("31 changes already found on this PC, back to June 2025.", card.FeatureFact);
            Assert.Equal(card.FeatureFact, feature.Fact); // shown in the feature's own block

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
        Ui.Run(() => Assert.Null(card.FeatureFact));

        var settings = Kit.OfflineSettings(new RigsightSettings { WhatsNewSeen = "0.14.4" });
        var quiet = Ui.Run(() => new WhatsNewViewModel(settings, new Version(0, 15, 0), _ => { }, _ => Task.FromResult<string?>(null)));
        Ui.Run(quiet.CheckOnStart);
        Ui.Pump(50);
        Ui.Run(() =>
        {
            Assert.True(quiet.IsOpen);
            Assert.Null(quiet.FeatureFact);
            Assert.Null(quiet.Shown[0].Feature!.Fact); // and the one from the card before is gone
        });
    }
}
