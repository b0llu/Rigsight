using System.Windows;
using System.Windows.Controls;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Tests.Support;
using Rigsight.Views;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// A game's page with what 2026-10-25 added, in the real window: how the game is set (from its own settings file),
/// every frame of its newest play with the stutters, the true lows and stutters an hour, and each play's stutters.
/// The test's history gets a play's frames written in; pictures go to screens\game-frames-*.png.
/// </summary>
[Collection("UI")]
public sealed class GameFramesLookTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");

    /// <summary>Cyberpunk 2077's newest play, a frame minute for each of its minutes: 144 fps with a stutter every 20 s.</summary>
    private static long WriteFrames()
    {
        using var db = RigsightDb.OpenWriter();
        long app = db.LoadApps().Single(a => a.Exe == "cyberpunk2077.exe").Id;
        var play = db.GetAppSessions(app).Last();
        var spreads = new List<(long, FrameSpread, List<Stutter>)>();
        var random = new Random(7);
        for (long ts = play.Start / 60 * 60; ts < play.End; ts += 60)
        {
            var spread = new FrameSpread();
            var stutters = new List<Stutter>();
            var frames = new List<uint>();
            for (int i = 0; i < 8600; i++)
            {
                uint us = (uint)(6500 + random.Next(0, 1200));
                if (i % 4300 == 2150 && random.Next(3) == 0) { us = (uint)random.Next(28_000, 70_000); stutters.Add(new Stutter(ts * 1000 + i * 7, us / 1000.0, 6.9)); }
                spread.Add(us);
                frames.Add(us);
            }
            db.WriteFrameMinute(ts, app, spread, stutters);
            FrameStore.Append(app, (ts + 60) * 1000, frames.ToArray());
        }
        return app;
    }

    [Fact]
    public void A_game_page_says_how_it_s_set_and_draws_every_frame_of_its_newest_play()
    {
        Ui.TakeProblems();
        WriteFrames();
        // Its settings, where Cyberpunk keeps them (a test folder, never this PC's).
        string root = TestEnvironment.NewFolder("game-settings-look");
        string local = Path.Combine(root, "Local");
        Directory.CreateDirectory(Path.Combine(local, "CD Projekt Red", "Cyberpunk 2077"));
        File.WriteAllText(Path.Combine(local, "CD Projekt Red", "Cyberpunk 2077", "UserSettings.json"), """
            { "version": 140, "data": [
              { "group_name": "/graphics/presets", "options": [ { "name": "ResolutionScaling", "value": "DLSS" }, { "name": "DLSS", "value": "Balanced" },
                { "name": "QuickPresets", "value": "Ultra" }, { "name": "RayTracing", "value": true } ] },
              { "group_name": "/video/display", "options": [ { "name": "Resolution", "value": "2560x1440" }, { "name": "WindowMode", "value": "Borderless" },
                { "name": "VSync", "value": false }, { "name": "MaximumFPS_Value", "value": 0 } ] } ] }
            """);
        Ui.Run(() => host.Shell.Games.SettingsPlaces = new GameSettings.Places { LocalAppData = local, RoamingAppData = root, LocalLow = root, Documents = root, SavedGames = root });

        host.Show("games", 1500);
        // The library's covers can log a binding of their own mask while they draw (older than this test): only the opened game counts here.
        Ui.TakeProblems();
        Ui.Run(() => host.Shell.Games.OpenKeyAsync("cyberpunk2077.exe")).GetAwaiter().GetResult();
        Ui.WaitFor(() => Ui.Run(() => host.Shell.Games.HasFramePlays && host.Shell.Games.HasSettings && host.Shell.Games.RunsPoints.Count > 0), 10_000);
        Ui.Pump(600);
        var games = host.Shell.Games;
        Ui.Run(() =>
        {
            Assert.True(games.HasSettings);
            var rows = games.SettingsRows.ToDictionary(r => r.Label, r => r.Value);
            Assert.Equal("2560 × 1440", rows["Resolution"]);
            Assert.Equal("Borderless window", rows["Display mode"]);
            Assert.Equal("DLSS Balanced", rows["Upscaling"]);
            Assert.StartsWith("From UserSettings.json · saved ", games.SettingsNote);
            // "How it runs" opens on the last 30 days, its numbers for them (approved mock-up, 2026-10-27).
            Assert.Equal("Last 30 days", games.RunsLabel);
            Assert.StartsWith("Last 30 days · ", games.RunsScope);
            Assert.Equal(30, games.RunsPick);
            Assert.Contains("stutters an hour", games.SmoothDetail);
            // About 40 stutters an hour in this play (two chances a minute, one in three): it stutters.
            Assert.Equal("It stutters", games.SmoothVerdict);
            Assert.EndsWith(" play", games.FramesViewName);
            Assert.Contains(games.RunsPoints, p => p.CanOpen && p.StuttersAnHour is not null);
            Assert.False(games.HasStutterWhy); // only over a play
            Assert.Contains(games.Rows, r => r.Stutters is not null);
        });
        // The page from its top (all plays), then the play (a minute at a time), then down to the new cards.
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "game-frames-1-top.png")));
        Ui.Run(() => games.ShowFramesCommand.Execute(null));
        Ui.WaitFor(() => Ui.Run(() => games.PlayMinutes.Count >= 2), 5_000);
        Ui.Pump(700);
        Ui.Run(() =>
        {
            Assert.True(games.FramesView);
            Assert.True(games.HasStutterWhy);
            Assert.EndsWith("stutters in this play", games.SmoothFrom);
            Assert.Contains(games.PlayStutters, _ => true);
            Assert.DoesNotContain("Last", games.RunsLabel); // the pager names the play
        });
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "game-frames-1b-every-frame.png")));
        // The page's own refresh keeps the view picked (2026-10-27: it went back to All plays after a while).
        Ui.Run(() => games.LoadAsync()).GetAwaiter().GetResult();
        Ui.Pump(400);
        Ui.TakeProblems(); // the library's covers again, as above
        Ui.Run(() => { Assert.True(games.FramesView); Assert.True(games.PlayMinutes.Count >= 2); });
        // The range: 12 months is a point a week; a step back names its dates; a custom range is its own.
        Ui.Run(() => games.ShowPlaysCommand.Execute(null));
        Ui.Run(() => games.PickRunsRangeCommand.Execute("365"));
        Ui.Pump(500);
        Ui.Run(() =>
        {
            Assert.Equal("Last 12 months", games.RunsLabel);
            Assert.All(games.RunsPoints, p => Assert.True(p.IsWeek));
            Ui.SavePng(host.Window, Path.Combine(Shots, "game-frames-1c-year.png"));
        });
        Ui.Run(() => games.PickRunsRangeCommand.Execute("7"));
        Ui.Run(() => games.RunsPreviousCommand.Execute(null));
        Ui.Pump(300);
        Ui.Run(() => { Assert.Equal(-1, games.RunsPick); Assert.Contains("–", games.RunsLabel); Assert.True(games.CanRunsNext); });
        Ui.Run(() => games.SetRunsCustom(DateTime.Now.AddDays(-3), DateTime.Now));
        Ui.Pump(300);
        Ui.Run(() => { Assert.Equal(-1, games.RunsPick); Assert.StartsWith(DateTime.Now.AddDays(-3).ToString("d MMM"), games.RunsLabel); });
        Ui.Run(() => games.PickRunsRangeCommand.Execute("30"));
        Ui.Pump(300);
        var page = Ui.Run(() => Visuals.Descendants<GamesView>(host.Window).Single());
        var scroller = Ui.Run(() => Visuals.Descendants<ScrollViewer>(page).Where(s => s.ScrollableHeight > 0).MaxBy(s => s.ScrollableHeight)!);
        Ui.Run(() => scroller.ScrollToVerticalOffset(Math.Min(scroller.ScrollableHeight, 560)));
        Ui.Pump(400);
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "game-frames-2-every-frame.png")));
        Ui.Run(() => scroller.ScrollToVerticalOffset(Math.Min(scroller.ScrollableHeight, 1250)));
        Ui.Pump(400);
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "game-frames-3-how-it-s-set.png")));
        Ui.Run(() => scroller.ScrollToTop());
        Ui.AssertNoProblems("game page with frames");
        Ui.Run(() => games.SettingsPlaces = GameSettings.Places.ThisPc());
    }
}
