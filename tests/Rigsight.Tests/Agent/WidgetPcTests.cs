using System.Drawing;
using System.Drawing.Imaging;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Tests.Data;
using Rigsight.Tests.Support;
using static Rigsight.Tests.Agent.WidgetSamples;

namespace Rigsight.Tests.Agent;

/// <summary>
/// The widgets' PC tiles (the PC tiles mock-up, 2026-11-11): what each shows comes from the reader its page uses, read
/// from a database of the test's own; each draws at its ready sizes; and what's read is only what a widget asks for.
/// </summary>
[Collection("Drawing")]
public class WidgetPcTests : IDisposable
{
    private readonly Func<DateTime> _now = WidgetRenderer.Now;

    public WidgetPcTests() => WidgetRenderer.Now = () => DateTime.Today.AddHours(9).AddMinutes(14);

    public void Dispose() => WidgetRenderer.Now = _now;

    private static WidgetConfig Box(params WidgetTile[] tiles) => new() { Style = WidgetStyle.Custom, Id = "custom-pc", Tiles = [.. tiles], BackgroundOpacity = 0.94 };

    private static readonly string[] PcKinds =
    [
        WidgetTiles.CpuGauge, WidgetTiles.GpuGauge, WidgetTiles.Fans, WidgetTiles.Drives, WidgetTiles.Cores, WidgetTiles.TopCpu, WidgetTiles.TopMemory,
        WidgetTiles.NetSpeed, WidgetTiles.NetNow, WidgetTiles.NetToday, WidgetTiles.NetApps, WidgetTiles.NetHours, WidgetTiles.NetDrops,
        WidgetTiles.TodaySoFar, WidgetTiles.MostUsed, WidgetTiles.StandOut, WidgetTiles.Peaks, WidgetTiles.Yesterday, WidgetTiles.Crashes,
        WidgetTiles.Health, WidgetTiles.Changed, WidgetTiles.Week, WidgetTiles.LastGame,
    ];

    private static WidgetData Sample()
    {
        var d = Full();
        (d.CpuTemp, d.CpuLoad, d.GpuTemp, d.GpuLoad) = (46, 3, 44, 1);
        d.Today = new TodayInfo
        {
            ActiveSec = 45 * 60, OnSec = 54 * 60, IdleSec = 8 * 60, CpuPeak = 81, CpuPeakApp = "Google Chrome", CpuPeakCategory = AppCategory.Browser,
            GpuPeak = 54, GpuPeakApp = "Google Chrome", GpuPeakCategory = AppCategory.Browser,
        };
        d.Pc!.TopCpu = d.Pc.TopCpu!.Take(5).ToList();
        return d;
    }

    // Every PC tile at each of its ready sizes, on the dark and the light theme, for a person to set beside the mock-up:
    // %TEMP%\rigsight-tests\boxes\pc-*.png (one board of them all: pc-board-*.png).
    [Fact]
    public void Every_pc_tile_at_its_ready_sizes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "boxes");
        Directory.CreateDirectory(dir);
        var data = Sample();
        foreach (var theme in new[] { WidgetTheme.Dark, WidgetTheme.Light })
        {
            var rows = new List<List<Bitmap>>();
            foreach (var kind in PcKinds)
            {
                var row = new List<Bitmap>();
                foreach (var (_, w, h) in WidgetTiles.Presets(kind))
                {
                    var cfg = Box(new WidgetTile { Kind = kind, W = w, H = h });
                    cfg.Theme = theme;
                    var bmp = WidgetRenderer.Render(cfg, data, 1, hover: false, out _);
                    bmp.Save(Path.Combine(dir, $"pc-{kind}-{w}x{h}-{theme}.png"), ImageFormat.Png);
                    row.Add(bmp);
                }
                Assert.NotEmpty(row);
                rows.Add(row);
            }
            int bw = rows.Max(r => r.Sum(b => b.Width) + 14 * (r.Count + 1)), bh = rows.Sum(r => r.Max(b => b.Height) + 14) + 14;
            using var board = new Bitmap(bw, bh);
            using (var g = Graphics.FromImage(board))
            {
                g.Clear(Color.FromArgb(58, 58, 68));
                int y = 14;
                foreach (var r in rows)
                {
                    int x = 14;
                    foreach (var b in r) { g.DrawImage(b, x, y); x += b.Width + 14; }
                    y += r.Max(b => b.Height) + 14;
                }
            }
            board.Save(Path.Combine(dir, $"pc-board-{theme}.png"), ImageFormat.Png);
            foreach (var b in rows.SelectMany(r => r)) b.Dispose();
        }
    }

    [Fact]
    public void Every_pc_kind_has_ready_sizes_no_smaller_than_its_least()
    {
        foreach (var kind in PcKinds)
        {
            var k = WidgetTiles.Kind(kind)!;
            var presets = WidgetTiles.Presets(kind);
            Assert.NotEmpty(presets);
            Assert.All(presets, p => Assert.True(p.W >= k.MinW && p.H >= k.MinH, $"{kind} {p.Name} {p.W}x{p.H}"));
            Assert.Contains(presets, p => p.W == k.W && p.H == k.H);
        }
        Assert.Equal(PcKinds.Length + 16, WidgetTiles.Kinds.Count);
        Assert.Equal([WidgetTiles.GroupEveryday, WidgetTiles.GroupLive, WidgetTiles.GroupInternet, WidgetTiles.GroupYourDay], WidgetTiles.Kinds.Select(k => k.Group).Distinct());
    }

    [Fact]
    public void Only_the_tiles_from_the_records_ask_for_them()
    {
        Assert.Equal(PcNeeds.None, PcFacts.NeedsOf([Box(new WidgetTile { Kind = WidgetTiles.Clock }, new WidgetTile { Kind = WidgetTiles.Fans }, new WidgetTile { Kind = WidgetTiles.TopCpu })]));
        Assert.Equal(PcNeeds.None, PcFacts.NeedsOf([new WidgetConfig { Style = WidgetStyle.Compact }]));
        Assert.Equal(PcNeeds.Internet | PcNeeds.Today | PcNeeds.Health,
            PcFacts.NeedsOf([Box(new WidgetTile { Kind = WidgetTiles.NetHours }), Box(new WidgetTile { Kind = WidgetTiles.Health }, new WidgetTile { Kind = WidgetTiles.NetDrops })]));
        foreach (var kind in PcKinds.Where(k => k is not (WidgetTiles.CpuGauge or WidgetTiles.GpuGauge or WidgetTiles.Fans or WidgetTiles.Drives or WidgetTiles.Cores
                     or WidgetTiles.TopCpu or WidgetTiles.TopMemory or WidgetTiles.NetSpeed or WidgetTiles.NetNow or WidgetTiles.TodaySoFar or WidgetTiles.Peaks)))
            Assert.NotEqual(PcNeeds.None, PcFacts.NeedsOf(kind));
    }

    [Fact]
    public void A_tile_from_the_records_draws_nothing_alarming_before_they_are_read()
    {
        var d = Sample();
        d.Facts = null;
        foreach (var kind in PcKinds)
        {
            var cfg = Box(WidgetTiles.New(WidgetTiles.Kind(kind)!));
            using var bmp = WidgetRenderer.Render(cfg, d, 1, hover: false, out _);
            Assert.True(bmp.Width > 0);
        }
        // Nothing read yet, and nothing like "No …" or "Not recorded" said: the list tiles show their heading alone.
        using var empty = WidgetRenderer.Render(Box(new WidgetTile { Kind = WidgetTiles.MostUsed, W = 6, H = 4 }), d, 1, hover: false, out _);
        using var heading = WidgetRenderer.Render(Box(new WidgetTile { Kind = WidgetTiles.MostUsed, W = 6, H = 4 }), new WidgetData { Facts = new PcFacts { Holds = PcNeeds.Week } }, 1, hover: false, out _);
        Assert.True(SamePixels(empty, heading));
    }

    [Fact]
    public void A_widget_is_drawn_again_only_when_what_a_pc_tile_shows_changes()
    {
        foreach (var kind in PcKinds)
        {
            var cfg = Box(WidgetTiles.New(WidgetTiles.Kind(kind)!));
            Assert.Equal(WidgetRenderer.ChangeKey(cfg, Sample()), WidgetRenderer.ChangeKey(cfg, Sample()));
        }
        var fans = Box(new WidgetTile { Kind = WidgetTiles.Fans, W = 6, H = 3 });
        var a = Sample();
        var b = Sample();
        b.Pc!.Fans![0] = b.Pc.Fans[0] with { Rpm = 900 };
        Assert.NotEqual(WidgetRenderer.ChangeKey(fans, a), WidgetRenderer.ChangeKey(fans, b));
        var crashes = Box(new WidgetTile { Kind = WidgetTiles.Crashes, W = 6, H = 4 });
        b.Facts!.Crashes = 2;
        Assert.NotEqual(WidgetRenderer.ChangeKey(crashes, a), WidgetRenderer.ChangeKey(crashes, b));
    }

    [Fact]
    public void How_many_a_top_cpu_tile_lists_is_kept_and_an_odd_one_put_back()
    {
        var t = new WidgetTile { Kind = WidgetTiles.TopCpu, W = 6, H = 5, Rows = 5 };
        Assert.Equal(5, t.Copy().Rows);
        Assert.Equal(5, WidgetTiles.RowsOf(t));
        var cfg = Box(t, new WidgetTile { Kind = WidgetTiles.TopCpu, Y = 5, W = 6, H = 3, Rows = 4 });
        WidgetTiles.Clean(cfg);
        Assert.Equal([5, null], cfg.Tiles!.Select(x => x.Rows));
        Assert.Equal(3, WidgetTiles.RowsOf(cfg.Tiles![1]));
        // A settings file from before these tiles reads as it was.
        var old = System.Text.Json.JsonSerializer.Deserialize<WidgetTile>("""{"Kind":"clock","W":6,"H":2}""")!;
        Assert.Null(old.Rows);
    }

    [Fact]
    public void Connection_today_says_drops_as_the_Network_page_does()
    {
        Assert.Equal("No drops", DropWords.Count(0));
        Assert.Equal("Dropped once", DropWords.Count(1));
        Assert.Equal("Dropped twice", DropWords.Count(2));
        Assert.Equal("Dropped 5 times", DropWords.Count(5));
    }

    [Fact]
    public void The_last_crash_and_the_last_game_are_said_with_when()
    {
        var now = new DateTime(2026, 11, 11, 10, 0, 0);
        Assert.Equal("Dota 2 crashed today at 9:14 AM", PcFacts.When("Dota 2 crashed", now.Date.AddMinutes(9 * 60 + 14), now).Replace(' ', ' '));
        Assert.Equal("Dota 2 crashed yesterday at 9:14 PM", PcFacts.When("Dota 2 crashed", now.Date.AddMinutes(-(2 * 60 + 46)), now).Replace(' ', ' '));
        Assert.StartsWith("Dota 2 crashed on 2 Nov at ", PcFacts.When("Dota 2 crashed", new DateTime(2026, 11, 2, 21, 14, 0), now));
        Assert.Equal("yesterday, 1h 12m", WidgetRenderer.PlayedWhen(new LastPlayLine("Dota 2", null, now.AddDays(-1), 72 * 60, 141), now));
        Assert.Equal("today, 45m", WidgetRenderer.PlayedWhen(new LastPlayLine("Dota 2", null, now.AddHours(-1), 45 * 60, null), now));
    }

    // ── What the tiles read, from a database of the test's own ──────────────

    /// <summary>A PC lived in for ten days (PcSim), with a crash, a change, today's internet and a drop added.</summary>
    private static (TestDb Db, DateTime Now) Lived()
    {
        var db = new TestDb("pc-tiles");
        db.Db.Dispose();
        var now = DateTime.Today.AddHours(15);
        PcSim.Live(db.Path, now.Date.AddDays(-10), now, new PcSim.Options { Seed = 5, GameChance = 0.6 });
        var w = db.OpenWriter();
        long chrome = w.UpsertApp("chrome.exe", "Google Chrome", null, AppCategory.Browser), steam = w.UpsertApp("steam.exe", "Steam", null, AppCategory.Game);
        for (int m = 0; m < 120; m++)
        {
            long ts = TimeUtil.ToUnix(now.Date.AddHours(8).AddMinutes(m));
            w.WriteNetMinute(new NetMinute(ts, 40L << 20, 2L << 20, 0, 0, 0, 0, 0, null, chrome));
            w.AddNetAppUse(new NetAppUse { Ts = ts, App = m < 60 ? steam : chrome, Down = 40L << 20, Up = 2L << 20 });
        }
        w.InsertNetDrop(new NetDrop(TimeUtil.ToUnix(now.Date.AddHours(9)), TimeUtil.ToUnix(now.Date.AddHours(9).AddMinutes(2)), NetDropKind.Internet));
        w.InsertCrashes([new CrashEvent { Ts = TimeUtil.ToUnix(now.AddHours(-30)), Kind = CrashKind.AppCrash, AppExe = "dota2.exe", Module = "dota2.exe", Code = "c0000005" }]);
        w.InsertChanges([new SystemChange(now.AddDays(-2), ChangeKind.AppUpdated, "Discord updated to 1.0.9200")]);
        return (db, now);
    }

    [Fact]
    public void The_records_tiles_read_what_their_pages_read()
    {
        var (test, now) = Lived();
        using (test)
        {
            var s = new RigsightSettings();
            using var db = test.Reader();
            var f = PcFacts.Read(db, s, now, (PcNeeds)255, [new DriveCheck("Samsung SSD 980", true, false)], "the C drive is 58% full");

            // Most used and What stands out: Home's own lists from today's report.
            var today = ReportBuilder.Build(db, ReportRange.Day, now.Date, s);
            Assert.Equal(HomeCards.TopToday(today).Select(a => (a.Name, Units.Duration(a.ActiveSec))), f.MostUsed.Select(a => (a.Name, a.Amount)));
            Assert.NotEmpty(f.MostUsed);
            Assert.Equal(1, f.MostUsed[0].Share);
            Assert.Equal(HomeCards.StandOut(today).Select(i => i.Line), f.StandOut);

            // Yesterday, as Home's recap reads it; and the six days before today.
            var y = ReportBuilder.Build(db, ReportRange.Day, now.Date.AddDays(-1), s);
            Assert.Equal(y.HasData, f.YesterdayHasData);
            Assert.Equal(y.ActiveSec, f.YesterdayActiveSec, 0.5);
            Assert.Equal(HomeCards.TopApps(y, PcFacts.ListMost).Select(a => a.Name), f.YesterdayApps.Select(a => a.Name));
            Assert.Equal(6, f.Days.Count);
            Assert.Equal(now.Date.AddDays(-1), f.Days[^1].Day);
            var week = ReportBuilder.Build(db, ReportRange.Week, now.Date, s);
            foreach (var (day, sec) in f.Days) Assert.Equal(week.Days.Single(b => b.Day == day).ActiveSec, sec, 0.5);

            // Crashes: the Crashes page's 30 days, its latest in a sentence.
            Assert.Equal(db.GetCrashes(TimeUtil.ToUnix(now.Date.AddDays(-29)), TimeUtil.ToUnix(now.AddMinutes(1))).Count, f.Crashes);
            Assert.True(f.Crashes >= 1);
            Assert.NotNull(f.LatestCrash);

            // PC health and What changed lately: Home's two cards.
            var rec = HomeCards.Read(db, s, now);
            var results = ChangeEffects.Read(db, s);
            Assert.Equal(HomeCards.Changes(rec.Changes, now.Date, results, PcFacts.ListMost).Select(c => c.Text), f.Changes.Select(c => c.Text));
            Assert.Contains(f.Changes, c => c.Text == "Discord updated" && c.When == HomeCards.DayWord(now.AddDays(-2), now));
            Assert.Equal(HomeCards.Health(today, rec.Problems, rec.Mains, [new DriveCheck("Samsung SSD 980", true, false)], "the C drive is 58% full", rec.MainsLikely).Select(i => i.Line),
                f.Health.Select(h => h.Text).Where(t => results.Values.All(r => r.Said != t)));
            Assert.Contains(f.Health, h => h.Text == "The drive is healthy; the C drive is 58% full");

            // The last game, as the Games page has its last play.
            var last = Rigsight.Core.Games.GameHistory.Library(db, [], s).Where(e => e.LastPlayed is not null).MaxBy(e => e.LastPlayed)!;
            var story = Rigsight.Core.Games.GameHistory.Story(db, last, s, now);
            Assert.Equal(last.Name, f.LastGame!.Name);
            Assert.Equal(story.Plays[^1].ActiveSec, f.LastGame.ActiveSec, 0.5);
            Assert.Equal(story.Plays[^1].Stats.Fps, f.LastGame.Fps);

            // Internet today: the Network page's report.
            var net = NetReportBuilder.Build(db, ReportRange.Day, now.Date, db.LoadApps().ToDictionary(a => a.Id), s);
            Assert.True(f.NetHasData);
            Assert.Equal((net.Down, net.Up, net.Background), (f.NetDown, f.NetUp, f.NetBackground));
            Assert.Equal(["Google Chrome", "Steam"], f.NetApps.Select(a => a.Name).Order());
            Assert.Equal(net.Bins.Take(24).Select(b => b.Total), Enumerable.Range(0, 24).Select(h => f.HourTop[h] + f.HourRest[h]));
            Assert.Single(f.Drops);
            Assert.True(f.Quarters[8 * 4]);
        }
    }

    [Fact]
    public void Only_what_is_asked_for_is_read_and_what_is_slow_is_kept()
    {
        var (test, now) = Lived();
        using (test)
        {
            var s = new RigsightSettings();
            using var db = test.Reader();
            var f = PcFacts.Read(db, s, now, PcNeeds.Crashes);
            Assert.Equal(PcNeeds.Crashes, f.Holds);
            Assert.Empty(f.MostUsed);
            Assert.False(f.NetHasData);
            Assert.Null(f.LastGame);
            Assert.True(f.Crashes >= 1);

            var first = PcFacts.Read(db, s, now, PcNeeds.Changes | PcNeeds.Week | PcNeeds.Yesterday);
            var again = PcFacts.Read(db, s, now.AddMinutes(1), PcNeeds.Changes | PcNeeds.Week | PcNeeds.Yesterday, before: first);
            Assert.Same(first.Days, again.Days);
            Assert.Same(first.YesterdayApps, again.YesterdayApps);
            // Today's report, the dearest part, is kept a few minutes, then read again.
            var t1 = PcFacts.Read(db, s, now, PcNeeds.Today);
            Assert.Same(t1.TodayReport, PcFacts.Read(db, s, now.AddMinutes(1), PcNeeds.Today, before: t1).TodayReport);
            Assert.NotSame(t1.TodayReport, PcFacts.Read(db, s, now + PcFacts.TodayFor, PcNeeds.Today, before: t1).TodayReport);
            // At midnight they're read again.
            var tomorrow = PcFacts.Read(db, s, now.Date.AddDays(1).AddMinutes(1), PcNeeds.Week, before: first);
            Assert.NotSame(first.Days, tomorrow.Days);
            Assert.Equal(now.Date, tomorrow.Days[^1].Day);
        }
    }

    [Fact]
    public void Yesterday_not_recorded_says_why()
    {
        using var test = new TestDb("pc-tiles-empty");
        var s = new RigsightSettings();
        using var db = test.Reader();
        var f = PcFacts.Read(db, s, DateTime.Today.AddHours(10), PcNeeds.Yesterday | PcNeeds.LastGame | PcNeeds.Internet);
        Assert.False(f.YesterdayHasData);
        Assert.False(string.IsNullOrEmpty(f.YesterdayWhy));
        Assert.Null(f.LastGame);
        Assert.False(f.NetHasData);
    }
}
