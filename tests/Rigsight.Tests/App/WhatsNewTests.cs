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
                Assert.InRange(item.Length, 10, 140);         // one short line
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
}
