using System.Drawing;
using System.Drawing.Imaging;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using static Rigsight.Tests.Agent.WidgetSamples;

namespace Rigsight.Tests.Agent;

/// <summary>
/// A tile too small for all it lists scrolls instead of leaving rows out (the owner, 2026-11-12, a small Fans tile that
/// showed three fans and no way to the rest: "we should not show less readings cause the widget is small, we should add
/// a scroll"): every row can be reached with the wheel, the heading stays put and nothing is drawn over it, and a list
/// that fits has no scroll bar and no scroll area. Pictures of each at its top, middle and end go to
/// %TEMP%\rigsight-tests\scroll\.
/// </summary>
[Collection("Drawing")]
public class WidgetScrollTests : IDisposable
{
    private readonly Func<DateTime> _now = WidgetRenderer.Now;

    public WidgetScrollTests() => WidgetRenderer.Now = () => new DateTime(2026, 11, 12, 9, 14, 0);

    public void Dispose()
    {
        WidgetRenderer.Now = _now;
        WidgetRenderer.Scrolls.Clear();
    }

    private static readonly string Pictures = Path.Combine(Path.GetTempPath(), "rigsight-tests", "scroll");

    private static WidgetConfig Box(params WidgetTile[] tiles) => new() { Style = WidgetStyle.Custom, Id = "custom-scroll", Tiles = [.. tiles], BackgroundOpacity = 1 };

    /// <summary>More of everything than a small tile holds, with names a person can tell apart in the pictures.</summary>
    private static WidgetData Many()
    {
        var d = Full();
        var day = WidgetRenderer.Now().Date;
        string[] apps = ["Google Chrome", "Discord", "Visual Studio Code", "Steam", "Dota 2", "Spotify", "OBS Studio", "Explorer"];
        d.Pc = new PcNow
        {
            Fans = [new("GPU fans", 1210), .. Enumerable.Range(1, 7).Select(i => new FanNow($"Fan #{i}", 600 + i * 45))],
            Drives = [.. "CDEFG".Select((c, i) => new DriveNow(c.ToString(), 0.2 + i * 0.15, (100L + i * 50) << 30, 30 + i))],
            Cores = [0.2f, 0.1f, 0.4f, 0.05f],
            TopCpu = [.. apps.Select((a, i) => new TileApp(a, null, $"{12 - i}%"))],
            TopMemory = [.. apps.Select((a, i) => new TileApp(a, null, $"{(8 - i) * 210} MB"))],
            NetPerApp = true,
            NetApps = [.. apps.Select((a, i) => new TileApp(a, null, $"{(8 - i) * 120} KB/s"))],
        };
        var f = FactsSample();
        f.MostUsed = [.. apps.Select((a, i) => new AppLine(a, null, $"{48 - i * 5}m", 1 - i * 0.1))];
        f.NetApps = [.. apps.Select((a, i) => new AppLine(a, null, $"{(8 - i) * 300} MB", 1 - i * 0.1))];
        f.YesterdayApps = [.. apps.Select((a, i) => new AppLine(a, null, $"{5 - i * 0.5:0.#}h", 0))];
        f.StandOut = ["Day started at 8:20 AM", "Steam downloaded 2.9 GB while you were away", "Dota 2 ran for 3h 10m",
            "The GPU reached 76°C at 8:52 PM", "Chrome used 1.1 GB of internet", "Away for 52 minutes at lunch"];
        f.Health = [new("\uE9CA", "Temperatures as usual", false), new("\uE9F5", "Fans at their usual speeds", false),
            new("\uE73E", "No PC problems in the last 7 days", false), new("\uEDA2", "Drives healthy; the E drive is 84% full", true),
            new("\uE945", "Power as usual", false), new("\uE7BA", "One app crashed this week", true)];
        f.Changes = [.. Enumerable.Range(0, 8).Select(i => new ChangeCardLine(new SystemChange(day.AddDays(-1 - i), ChangeKind.AppUpdated, $"{apps[i]} updated"), $"{apps[i]} updated", i == 0 ? "Yesterday" : $"{i + 1} days ago"))];
        f.Crashes = 3;
        f.LatestCrash = "Dota 2 crashed on 2 Nov at 9:14 PM, while Discord and Steam were running and the GPU was at 74°C, a minute after a driver update";
        d.Facts = f;
        return d;
    }

    /// <summary>Each tile that lists, small, with more than it holds, and how many it must reach (null: as many as it says).</summary>
    public static IEnumerable<object[]> Lists() =>
    [
        [WidgetTiles.Fans, 5, 2, 8],
        [WidgetTiles.Drives, 5, 2, 5],
        [WidgetTiles.TopCpu, 5, 2, 5], // its How many: 5 of the 8 apps
        [WidgetTiles.TopMemory, 5, 2, 8],
        [WidgetTiles.NetNow, 5, 2, 8],
        [WidgetTiles.NetApps, 5, 2, 8],
        [WidgetTiles.MostUsed, 5, 3, 8],
        [WidgetTiles.StandOut, 7, 2, 6],
        [WidgetTiles.Yesterday, 5, 3, 8],
        [WidgetTiles.Crashes, 5, 3, -1], // lines of its latest crash
        [WidgetTiles.Health, 8, 3, 6],
        [WidgetTiles.Changed, 7, 3, 8],
        [WidgetTiles.Line, 4, 1, 6],
        [WidgetTiles.Shortcuts, 6, 2, 12],
        [WidgetTiles.Todo, 4, 3, 11], // ten items and the line to add one
        [WidgetTiles.Notes, 4, 3, -1], // lines of the note
    ];

    private static WidgetTile Tile(string kind, int w, int h) => new()
    {
        Kind = kind, W = w, H = h,
        Rows = kind == WidgetTiles.TopCpu ? 5 : null,
        Items = kind == WidgetTiles.Line
            ? WidgetCatalog.Items(OverlayMetric.CpuTemp, OverlayMetric.GpuTemp, OverlayMetric.CpuLoad, OverlayMetric.GpuLoad, OverlayMetric.Ram, OverlayMetric.GpuHotSpot)
            : null,
        Links = kind == WidgetTiles.Shortcuts ? [.. Enumerable.Range(1, 12).Select(i => new TileLink { Path = $@"C:\nowhere\{i}.txt", Name = $"File {i}" })] : null,
        Text = kind == WidgetTiles.Notes ? string.Join("\n", Enumerable.Range(1, 9).Select(i => $"Line {i} of the note")) : null,
        Todos = kind == WidgetTiles.Todo ? [.. Enumerable.Range(1, 10).Select(i => new TodoItem { Text = $"Item {i}", Done = i % 3 == 0 })] : null,
    };

    private static (Bitmap Bmp, WidgetRenderer.ShownPart Shown, List<(int Tile, RectangleF Rect)> Areas) Draw(WidgetConfig cfg, WidgetData d, int scroll, float scale = 2)
    {
        WidgetRenderer.Scrolls[(cfg.Id, 0)] = scroll;
        var bmp = WidgetRenderer.Render(cfg, d, scale, hover: false, out _);
        var shown = WidgetRenderer.Shown!.Single(s => s.Tile == 0);
        return (bmp, shown, [.. WidgetRenderer.ScrollAreas!]);
    }

    private static int[] Raw(Bitmap bmp, int rows)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var raw = new int[bmp.Width * rows];
            for (int y = 0; y < rows; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, raw, y * bmp.Width, bmp.Width);
            return raw;
        }
        finally { bmp.UnlockBits(data); }
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public void Every_row_of_a_small_tile_can_be_scrolled_to_and_the_heading_stays_put(string kind, int w, int h, int count)
    {
        Directory.CreateDirectory(Pictures);
        var cfg = Box(Tile(kind, w, h));
        var data = Many();
        var (top, first, areas) = Draw(cfg, data, 0);
        using (top)
        {
            if (count > 0) Assert.Equal(count, first.Total);
            Assert.True(first.Shown < first.Total, $"{kind} {w}x{h} shows {first.Shown} of {first.Total}: no need to scroll, pick a smaller size");
            Assert.Equal(0, first.First);
            var area = Assert.Single(areas, a => a.Tile == 0).Rect;
            // Above where the rows scroll (the heading, and a figure kept with it), every picture is the same as at the top.
            int still = (int)Math.Floor(area.Top * 2);
            var heading = Raw(top, still);
            var reached = new bool[first.Total];
            int last = -1;
            for (int s = 0; s <= first.Total; s++)
            {
                var (bmp, shown, _) = Draw(cfg, data, s);
                using (bmp)
                {
                    Assert.Equal(first.Total, shown.Total);
                    for (int i = shown.First; i < shown.First + shown.Shown; i++) reached[i] = true;
                    Assert.True(Raw(bmp, still).AsSpan().SequenceEqual(heading), $"{kind} scrolled to {s}: drawn over its heading");
                    // Each notch shows more of what's under, until the end; the wheel never runs past it (a scroll past
                    // the end is put back to it).
                    Assert.True(shown.First > last || s == 0 || shown.First + shown.Shown >= shown.Total, $"{kind} scrolled to {s} still shows from {shown.First}");
                    Assert.True(WidgetRenderer.Scrolls[(cfg.Id, 0)] <= s, $"{kind}: scroll {WidgetRenderer.Scrolls[(cfg.Id, 0)]} kept past {s}");
                    if (shown.First != last && (s == 0 || shown.First + shown.Shown >= shown.Total || s == first.Total / 3))
                        bmp.Save(Path.Combine(Pictures, $"{kind}-{w}x{h}-{(s == 0 ? "top" : shown.First + shown.Shown >= shown.Total ? "end" : "middle")}.png"), ImageFormat.Png);
                    last = shown.First;
                }
            }
            Assert.All(reached, (r, i) => Assert.True(r, $"{kind}: row {i} of {first.Total} never shown"));
        }
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public void A_list_that_fits_has_no_scroll_area_and_no_bar(string kind, int w, int h, int count)
    {
        _ = (w, h, count);
        // Its large ready size (big enough for the sample's few rows), with the few rows of the mock-up.
        var (_, lw, lh) = WidgetTiles.Presets(kind).Last();
        var tile = Tile(kind, Math.Max(lw, 10), Math.Max(lh, 1));
        if (kind == WidgetTiles.Shortcuts) tile.Links = tile.Links!.Take(3).ToList();
        if (kind == WidgetTiles.Todo) tile.Todos = tile.Todos!.Take(2).ToList();
        if (kind == WidgetTiles.Notes) tile.Text = "BIOS update on Saturday\nReturn the spare fan";
        if (kind == WidgetTiles.Line) tile.Items = tile.Items!.Take(3).ToList();
        if (kind == WidgetTiles.TopCpu) tile.Rows = 3;
        var cfg = Box(tile);
        WidgetRenderer.Scrolls[(cfg.Id, 0)] = 4; // left from when it held more: back to the top
        using var bmp = WidgetRenderer.Render(cfg, Full(), 1, hover: false, out _);
        Assert.DoesNotContain(WidgetRenderer.ScrollAreas!, a => a.Tile == 0);
        if (WidgetRenderer.Shown!.SingleOrDefault(s => s.Tile == 0) is { Tile: 0 } shown)
            Assert.True(shown.First == 0 && shown.Shown == shown.Total, $"{kind}: shows {shown.First}+{shown.Shown} of {shown.Total}");
        // Drawn as with no scroll at all.
        WidgetRenderer.Scrolls.Clear();
        using var fresh = WidgetRenderer.Render(cfg, Full(), 1, hover: false, out _);
        Assert.True(SamePixels(bmp, fresh), $"{kind}: a stale scroll changed the picture");
    }

    // Each tile keeps its own place: scrolling one list leaves another of the same kind where it was.
    [Fact]
    public void Each_tile_scrolls_on_its_own()
    {
        var second = Tile(WidgetTiles.Fans, 5, 2);
        second.X = 5;
        var cfg = Box(Tile(WidgetTiles.Fans, 5, 2), second);
        WidgetRenderer.Scrolls[(cfg.Id, 0)] = 3;
        WidgetRenderer.Render(cfg, Many(), 1, hover: false, out _).Dispose();
        Assert.Equal(3, WidgetRenderer.Shown!.Single(s => s.Tile == 0).First);
        Assert.Equal(0, WidgetRenderer.Shown!.Single(s => s.Tile == 1).First);
        Assert.Equal(2, WidgetRenderer.ScrollAreas!.Count);
    }

    // Shortcuts scrolled to their end: the links there are the ones clicked, and only those shown can be.
    [Fact]
    public void Scrolled_shortcuts_open_the_links_they_show()
    {
        var cfg = Box(Tile(WidgetTiles.Shortcuts, 6, 2));
        WidgetRenderer.Scrolls[(cfg.Id, 0)] = 99;
        WidgetRenderer.Render(cfg, Many(), 1, hover: false, out _).Dispose();
        var shown = WidgetRenderer.Shown!.Single();
        var opened = WidgetRenderer.Hits!.Where(x => x.Action == WidgetRenderer.HitOpen).Select(x => x.Index).ToList();
        Assert.Equal(Enumerable.Range(shown.First, shown.Shown), opened);
        Assert.Contains(11, opened);
    }

    // A tile with hover beside a scrolled list keeps its hover where it is.
    [Fact]
    public void A_scrolled_list_leaves_the_hover_of_the_tile_beside_it_alone()
    {
        var cfg = Box(Tile(WidgetTiles.Fans, 5, 2), new WidgetTile { Kind = WidgetTiles.Cores, X = 5, W = 6, H = 3 });
        WidgetRenderer.Render(cfg, Many(), 1, hover: false, out _).Dispose();
        var before = WidgetRenderer.HoverAreas!.ToList();
        WidgetRenderer.Scrolls[(cfg.Id, 0)] = 5;
        WidgetRenderer.Render(cfg, Many(), 1, hover: false, out _).Dispose();
        Assert.NotEmpty(before);
        Assert.Equal(before, WidgetRenderer.HoverAreas!);
    }
}
