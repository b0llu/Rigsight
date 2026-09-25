using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>Dashboards: the tile catalog, adding, moving, resizing and removing tiles, and what's saved.</summary>
[Collection("UI")]
public sealed class CustomPageTests
{
    private sealed class Dash
    {
        public required SettingsModel Settings { get; init; }
        public required LiveData Live { get; init; }
        public required CustomPageViewModel Page { get; init; }
        public required List<CustomPageViewModel> Opened { get; init; }
        public required List<CustomPageViewModel> Deleted { get; init; }
        public int LayoutChanges;

        public CustomPageConfig? Saved => Settings.Current.CustomPages.FirstOrDefault(p => p.Id == Page.Id);
        public List<TileViewModel> Real => [.. Page.Tiles.Where(t => !t.IsPlaceholder)];
    }

    private static Dash Make(CustomPageConfig? config = null, bool greeted = true)
    {
        SharedData.EnsureSeeded();
        var settings = Kit.OfflineSettings();
        List<CustomPageViewModel> opened = [], deleted = [];
        return Ui.Run(() =>
        {
            var live = new LiveData(settings);
            if (greeted) live.LoadHello(Pc.Hello());
            var reports = new ReportService(settings);
            var page = new CustomPageViewModel(config ?? new CustomPageConfig { Name = "Test", Grid = CustomPageConfig.CurrentGrid }, settings, live,
                new HomeViewModel(reports, live), new CrashesViewModel(reports, settings), opened.Add, deleted.Add);
            var dash = new Dash { Settings = settings, Live = live, Page = page, Opened = opened, Deleted = deleted };
            page.LayoutChanged += () => dash.LayoutChanges++;
            return dash;
        });
    }

    private static TileKind Kind(string kind, string? sensor = null) => TileCatalog.Find(kind, sensor);

    /// <summary>No two tiles overlap, and each fits inside the 12 columns.</summary>
    private static void AssertTidy(IEnumerable<TileViewModel> tiles)
    {
        var list = tiles.Where(t => !t.IsPlaceholder).ToList();
        foreach (var t in list)
        {
            Assert.InRange(t.X, 0, TileConfig.Columns - t.W);
            Assert.True(t.Y >= 0);
            Assert.InRange(t.W, 1, TileConfig.Columns);
            Assert.InRange(t.H, 1, TileConfig.MaxHeight);
        }
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                var (a, b) = (list[i], list[j]);
                bool overlap = a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
                Assert.False(overlap, $"{a.Kind} ({a.X},{a.Y} {a.W}×{a.H}) overlaps {b.Kind} ({b.X},{b.Y} {b.W}×{b.H})");
            }
    }

    // ── The catalog ──────────────────────────────────────────────────────

    [Fact]
    public void Catalog_tiles_fit_the_grid_and_are_unique()
    {
        var all = TileCatalog.Groups.SelectMany(g => g.Tiles).ToList();
        Assert.Equal(["LIVE", "SINGLE READINGS", "YOUR DAY"], TileCatalog.Groups.Select(g => g.Name));
        Assert.Equal(all.Count, all.Select(t => (t.Kind, t.Sensor)).Distinct().Count());
        Assert.All(all, t =>
        {
            Assert.InRange(t.W, t.MinW, TileConfig.Columns);
            Assert.InRange(t.H, t.MinH, TileConfig.MaxHeight);
            Assert.True(t.MinW >= 1 && t.MinH >= 1);
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.Equal(t.Kind == "sensor", t.Sensor is not null);
        });
        Assert.All(all.Where(t => t.Sensor is not null), t => Assert.StartsWith("key:", t.Sensor));
    }

    [Fact]
    public void Catalog_lookups()
    {
        Assert.Equal("GPU power", TileCatalog.Find("sensor", "key:" + KeySensors.GpuPower).Title);
        // A sensor the catalog doesn't list falls back to the generic single reading.
        Assert.Equal("CPU load", TileCatalog.Find("sensor", "/some/sensor").Title);
        Assert.Equal("Fans", TileCatalog.Find("fans").Title);
        Assert.Throws<InvalidOperationException>(() => TileCatalog.Find("no-such-tile"));
        Assert.Equal("RAM", TileCatalog.ForTile("sensor", "key:" + KeySensors.RamLoad)!.Title);
        Assert.Equal("sensor", TileCatalog.ForTile("sensor", "/custom/id")!.Kind);
        Assert.Null(TileCatalog.ForTile("retired-tile", null));
        var starter = TileCatalog.Starter().ToList();
        Assert.Equal(7, starter.Count);
        Assert.Equal("cpu-gauge", starter[0].Kind);
        Assert.Equal("today", starter[^1].Kind);
    }

    // ── Adding and removing ──────────────────────────────────────────────

    [Fact]
    public void Every_kind_of_tile_can_be_added_and_is_saved()
    {
        var d = Make();
        var all = TileCatalog.Groups.SelectMany(g => g.Tiles).ToList();
        Ui.Run(() =>
        {
            Assert.True(d.Page.IsEmpty);
            foreach (var kind in all) d.Page.AddTileCommand.Execute(kind);
            Assert.False(d.Page.IsEmpty);
            Assert.Equal(all.Count, d.Page.Tiles.Count);
            Assert.Equal(all.Count, d.LayoutChanges);
            AssertTidy(d.Page.Tiles);
            for (int i = 0; i < all.Count; i++)
            {
                var t = d.Page.Tiles[i];
                Assert.Equal((all[i].Kind, all[i].Sensor, all[i].W, all[i].H), (t.Kind, t.SensorRef, t.W, t.H));
                Assert.Equal(all[i].Title, t.Title);
                Assert.Same(all[i], t.Definition);
            }
            var saved = d.Saved!;
            Assert.Equal("Test", saved.Name);
            Assert.Equal(CustomPageConfig.CurrentGrid, saved.Grid);
            Assert.Equal(d.Page.Tiles.Select(t => (t.Id, t.X, t.Y, t.W, t.H)), saved.Tiles.Select(t => (t.Id, t.X, t.Y, t.W, t.H)));
            // Single readings find their sensor.
            Assert.Same(d.Live.GpuPower, d.Page.Tiles.Single(t => t.SensorRef == "key:" + KeySensors.GpuPower).Sensor);
        });
    }

    [Fact]
    public void A_new_tile_takes_the_first_free_spot()
    {
        var d = Make();
        Ui.Run(() =>
        {
            d.Page.AddTileCommand.Execute(Kind("cpu-gauge"));   // 3×4 at 0,0
            d.Page.AddTileCommand.Execute(Kind("gpu-gauge"));   // 3×4 at 3,0
            d.Page.AddTileCommand.Execute(Kind("temp-chart"));  // 12 wide: below them
            d.Page.AddTileCommand.Execute(Kind("sensor", "key:" + KeySensors.CpuLoad)); // 3×2 back in the top row, at 6,0
            Assert.Equal([(0, 0), (3, 0), (0, 4), (6, 0)], d.Page.Tiles.Select(t => (t.X, t.Y)));
        });
    }

    [Fact]
    public void Any_sensor_can_be_a_tile()
    {
        var d = Make();
        Ui.Run(() =>
        {
            d.Page.AddSensorTileCommand.Execute(null); // nothing picked
            Assert.Empty(d.Page.Tiles);
            d.Page.SensorToAdd = d.Live.Fans[1];
            d.Page.AddSensorTileCommand.Execute(null);
            Assert.Null(d.Page.SensorToAdd);
            var tile = Assert.Single(d.Page.Tiles);
            Assert.Equal("sensor", tile.Kind);
            Assert.Equal("/lpc/fan/1", tile.SensorRef);
            Assert.Same(d.Live.Fans[1], tile.Sensor);
            Assert.Equal("Fan #2", tile.Title);
            Assert.Equal((3, 2), (tile.W, tile.H));
            Assert.Same(d.Live.AllSensors, d.Page.AllSensors);
            Assert.Same(TileCatalog.Groups, d.Page.Catalog);
        });
    }

    [Fact]
    public void A_new_page_starts_with_a_few_useful_tiles()
    {
        var d = Make();
        int saves = 0;
        Ui.Run(() =>
        {
            d.Settings.Changed += () => saves++;
            d.Page.AddStarterTiles();
            Assert.Equal(7, d.Page.Tiles.Count);
            AssertTidy(d.Page.Tiles);
            Assert.Equal(7, d.Saved!.Tiles.Count);
        });
        Assert.Equal(1, saves); // saved once, not per tile
    }

    [Fact]
    public void Removing_a_tile_lets_the_rest_float_up()
    {
        var d = Make();
        Ui.Run(() =>
        {
            d.Page.AddTileCommand.Execute(Kind("temp-chart")); // 12×4 at the top
            d.Page.AddTileCommand.Execute(Kind("fans"));       // below
            d.Page.AddTileCommand.Execute(Kind("drives"));
            var chart = d.Page.Tiles[0];
            chart.RemoveCommand.Execute(null);
            Assert.DoesNotContain(chart, d.Page.Tiles);
            Assert.All(d.Page.Tiles, t => Assert.Equal(0, t.Y));
            Assert.Equal(2, d.Saved!.Tiles.Count);
            AssertTidy(d.Page.Tiles);
            foreach (var t in d.Page.Tiles.ToList()) d.Page.Remove(t);
            Assert.True(d.Page.IsEmpty);
            Assert.Empty(d.Saved!.Tiles);
        });
    }

    // ── Resizing ─────────────────────────────────────────────────────────

    [Fact]
    public void Resizing_stays_within_the_tiles_limits_and_the_grid()
    {
        var d = Make();
        Ui.Run(() =>
        {
            d.Page.AddTileCommand.Execute(Kind("cpu-gauge")); // min 2×3
            var t = d.Page.Tiles[0];
            t.X = 4;
            d.Page.BeginResize(t);
            d.Page.ResizeTo(100, 100);
            Assert.Equal((8, TileConfig.MaxHeight), (t.W, t.H)); // to the right edge, and no taller than the most
            d.Page.ResizeTo(0, -5);
            Assert.Equal((2, 3), (t.W, t.H));
            d.Page.ResizeTo(5, 6);
            Assert.Equal((5, 6), (t.W, t.H));
            d.Page.EndResize();
            Assert.Equal((5, 6), (d.Saved!.Tiles[0].W, d.Saved.Tiles[0].H));
            // Without a resize in progress nothing happens.
            d.Page.ResizeTo(3, 3);
            Assert.Equal((5, 6), (t.W, t.H));
            d.Page.EndResize();
        });
    }

    [Fact]
    public void Growing_a_tile_pushes_the_ones_below_down()
    {
        var d = Make();
        Ui.Run(() =>
        {
            d.Page.AddTileCommand.Execute(Kind("fans"));  // 3×4 at 0,0
            d.Page.AddTileCommand.Execute(Kind("sensor", "key:" + KeySensors.CpuLoad)); // 3,0
            d.Page.AddTileCommand.Execute(Kind("today")); // 6×2 at 6,0
            d.Page.AddTileCommand.Execute(Kind("temp-chart")); // below: 0,4
            var fans = d.Page.Tiles[0];
            int changes = d.LayoutChanges;
            d.Page.BeginResize(fans);
            d.Page.ResizeTo(3, 8);
            Assert.Equal(8, d.Page.Tiles[3].Y); // the chart moved down
            AssertTidy(d.Page.Tiles);
            d.Page.ResizeTo(3, 8); // the same size again: nothing to do
            Assert.Equal(changes + 1, d.LayoutChanges);
            // Shrinking back while still resizing: the others return to where they were.
            d.Page.ResizeTo(3, 4);
            Assert.Equal(4, d.Page.Tiles[3].Y);
            d.Page.EndResize();
        });
    }

    [Fact]
    public void Widening_into_a_neighbour_moves_it_below()
    {
        var d = Make();
        Ui.Run(() =>
        {
            d.Page.AddTileCommand.Execute(Kind("cpu-gauge")); // 0,0 3×4
            d.Page.AddTileCommand.Execute(Kind("gpu-gauge")); // 3,0 3×4
            var cpu = d.Page.Tiles[0];
            d.Page.BeginResize(cpu);
            d.Page.ResizeTo(6, 4);
            var gpu = d.Page.Tiles[1];
            Assert.Equal((3, 4), (gpu.X, gpu.Y));
            AssertTidy(d.Page.Tiles);
            d.Page.EndResize();
        });
    }

    [Fact]
    public void A_tile_at_the_right_edge_narrower_than_its_minimum_can_still_be_resized()
    {
        Ui.TakeProblems();
        // A page from the first grid (4 columns): a one-column tile became three columns, narrower than tiles are
        // allowed now, and one in the last column sits where its minimum width doesn't fit.
        var config = new CustomPageConfig
        {
            Name = "Old", Grid = 1,
            Tiles = [new TileConfig { Kind = "most-used", X = 3, Y = 0, W = 1, H = 2 }],
        };
        config = SettingsStore.Deserialize(SettingsStore.Serialize(new RigsightSettings { CustomPages = [config] })).CustomPages[0];
        var d = Make(config);
        Ui.Run(() =>
        {
            var t = d.Page.Tiles[0];
            Assert.Equal((9, 3), (t.X, t.W));
            Assert.Equal(4, t.MinW);
            d.Page.BeginResize(t);
            d.Page.ResizeTo(2, 5);
            Assert.InRange(t.X + t.W, 1, TileConfig.Columns);
            Assert.Equal(5, t.H);
            d.Page.EndResize();
            AssertTidy(d.Page.Tiles);
        });
        Ui.AssertNoProblems("resizing an old tile");
    }

    // ── Dragging ─────────────────────────────────────────────────────────

    [Fact]
    public void Dragging_shows_where_the_tile_will_land_and_moves_the_others()
    {
        var d = Make();
        Ui.Run(() =>
        {
            d.Page.AddTileCommand.Execute(Kind("cpu-gauge")); // 0,0
            d.Page.AddTileCommand.Execute(Kind("gpu-gauge")); // 3,0
            d.Page.AddTileCommand.Execute(Kind("fans"));      // 6,0
            var cpu = d.Page.Tiles[0];
            d.Page.BeginDrag(cpu);
            Assert.True(cpu.IsDragging);
            var placeholder = d.Page.Tiles[0];
            Assert.True(placeholder.IsPlaceholder);
            Assert.Equal("placeholder", placeholder.Kind);
            Assert.Equal((cpu.X, cpu.Y, cpu.W, cpu.H), (placeholder.X, placeholder.Y, placeholder.W, placeholder.H));
            Assert.False(d.Page.IsEmpty);

            d.Page.DragTo(3, 0); // onto the GPU gauge
            Assert.Equal((3, 0), (cpu.X, cpu.Y));
            Assert.Equal((3, 0), (placeholder.X, placeholder.Y));
            var gpu = d.Page.Tiles.Single(t => t.Kind == "gpu-gauge");
            Assert.Equal((3, 4), (gpu.X, gpu.Y)); // pushed below
            AssertTidy(d.Page.Tiles);

            d.Page.DragTo(50, -3); // past the edges: clamped
            Assert.Equal((9, 0), (cpu.X, cpu.Y));
            AssertTidy(d.Page.Tiles);
            // The others go back to their own spots once they're free again.
            Assert.Equal((3, 0), (gpu.X, gpu.Y));

            int changes = d.LayoutChanges;
            d.Page.DragTo(9, 0); // the same cell
            Assert.Equal(changes, d.LayoutChanges);

            d.Page.EndDrag();
            Assert.False(cpu.IsDragging);
            Assert.DoesNotContain(d.Page.Tiles, t => t.IsPlaceholder);
            Assert.Equal(3, d.Saved!.Tiles.Count);
            Assert.Equal(9, d.Saved.Tiles.Single(t => t.Kind == "cpu-gauge").X);
            d.Page.EndDrag(); // nothing dragged: nothing happens
            d.Page.DragTo(0, 0);
            Assert.Equal(9, cpu.X);
        });
    }

    [Fact]
    public void A_dragged_tile_floats_up_into_room_above_it()
    {
        var d = Make();
        Ui.Run(() =>
        {
            d.Page.AddTileCommand.Execute(Kind("cpu-gauge"));
            var t = d.Page.Tiles[0];
            d.Page.BeginDrag(t);
            d.Page.DragTo(0, 10);
            Assert.Equal(0, t.Y);
            d.Page.EndDrag();
        });
    }

    [Fact]
    public void Random_drags_and_resizes_never_leave_overlaps()
    {
        var d = Make();
        var rnd = new Random(7);
        Ui.Run(() =>
        {
            var kinds = TileCatalog.Groups.SelectMany(g => g.Tiles).ToList();
            for (int i = 0; i < 14; i++) d.Page.AddTileCommand.Execute(kinds[rnd.Next(kinds.Count)]);
            for (int step = 0; step < 300; step++)
            {
                var tiles = d.Real;
                var t = tiles[rnd.Next(tiles.Count)];
                if (rnd.Next(2) == 0)
                {
                    d.Page.BeginDrag(t);
                    for (int k = 0; k < 4; k++) d.Page.DragTo(rnd.Next(-3, 15), rnd.Next(-3, 30));
                    d.Page.EndDrag();
                }
                else
                {
                    d.Page.BeginResize(t);
                    for (int k = 0; k < 3; k++) d.Page.ResizeTo(rnd.Next(-2, 16), rnd.Next(-2, 20));
                    d.Page.EndResize();
                }
                AssertTidy(d.Page.Tiles);
                Assert.All(d.Real, x => Assert.True(x.W >= Math.Min(x.MinW, TileConfig.Columns - x.X) && x.H >= x.MinH));
            }
        });
    }

    // ── The page itself ──────────────────────────────────────────────────

    [Theory]
    [InlineData("  Games  ", "Games")]
    [InlineData("", "Dashboard")]
    [InlineData("   ", "Dashboard")]
    [InlineData("Ünïcödé 🎮", "Ünïcödé 🎮")]
    public void Renaming_saves_a_tidy_name(string typed, string saved)
    {
        var d = Make();
        Ui.Run(() =>
        {
            d.Page.Name = typed;
            Assert.Equal(saved, d.Saved!.Name);
        });
    }

    [Fact]
    public void Making_it_the_start_page_and_back()
    {
        var d = Make();
        Ui.Run(() =>
        {
            Assert.False(d.Page.IsStartPage);
            var changed = Kit.Changes(d.Page, () => d.Page.ToggleStartPageCommand.Execute(null));
            Assert.Equal(d.Page.NavKey, d.Settings.Current.StartPage);
            Assert.True(d.Page.IsStartPage);
            Assert.Contains(nameof(CustomPageViewModel.IsStartPage), changed);
            d.Page.ToggleStartPageCommand.Execute(null);
            Assert.Equal("home", d.Settings.Current.StartPage);
            Assert.StartsWith("custom:", d.Page.NavKey);
            Assert.Equal("custom:" + d.Page.Id, d.Page.NavKey);
        });
    }

    [Fact]
    public void Selecting_opens_it_and_delete_asks_the_shell()
    {
        var d = Make();
        Ui.Run(() =>
        {
            d.Page.IsSelected = true;
            d.Page.IsSelected = false;
            Assert.Equal([d.Page], d.Opened);
            d.Page.DeletePageCommand.Execute(null);
            Assert.Equal([d.Page], d.Deleted);
            d.Page.ToggleEditCommand.Execute(null);
            Assert.True(d.Page.IsEditing);
            d.Page.ToggleEditCommand.Execute(null);
            Assert.False(d.Page.IsEditing);
        });
    }

    [Fact]
    public void Tiles_made_before_the_sensors_arrive_find_them_later()
    {
        var config = new CustomPageConfig
        {
            Tiles =
            [
                new TileConfig { Kind = "sensor", Sensor = "key:" + KeySensors.CpuPower, W = 3, H = 2 },
                new TileConfig { Kind = "sensor", Sensor = "/gpu/temperature/1", X = 3, W = 3, H = 2 },
                new TileConfig { Kind = "sensor", Sensor = "/not/on/this/pc", X = 6, W = 3, H = 2 },
            ],
        };
        var d = Make(config, greeted: false);
        Ui.Run(() =>
        {
            Assert.All(d.Page.Tiles, t => Assert.Null(t.Sensor));
            Assert.Equal("CPU power", d.Page.Tiles[0].Title); // the catalog's name
            Assert.Equal("Sensor", d.Page.Tiles[1].Title);
            d.Live.LoadHello(Pc.Hello());
            Assert.Same(d.Live.CpuPower, d.Page.Tiles[0].Sensor);
            Assert.Same(d.Live.GpuHotSpot, d.Page.Tiles[1].Sensor);
            Assert.Equal("GPU Hot Spot", d.Page.Tiles[1].Title);
            Assert.Null(d.Page.Tiles[2].Sensor);
            Assert.Equal("Sensor", d.Page.Tiles[2].Title);
        });
    }

    [Fact]
    public void A_deleted_page_stops_listening()
    {
        var d = Make(new CustomPageConfig { Tiles = [new TileConfig { Kind = "sensor", Sensor = "key:" + KeySensors.CpuLoad, W = 3, H = 2 }] }, greeted: false);
        Ui.Run(() =>
        {
            d.Page.Dispose();
            d.Live.LoadHello(Pc.Hello());
            Assert.Null(d.Page.Tiles[0].Sensor);
            var changed = Kit.Changes(d.Page, () => d.Settings.Update(s => s.StartPage = d.Page.NavKey));
            Assert.Empty(changed);
        });
    }

    [Fact]
    public void A_sensor_tile_is_named_after_its_sensor_and_follows_a_rename()
    {
        var d = Make();
        Ui.Run(() =>
        {
            d.Page.AddTileCommand.Execute(Kind("sensor", "key:" + KeySensors.CpuPower));
            Assert.Equal("CPU power", d.Page.Tiles[0].Title); // a key sensor from the catalog keeps the catalog's name
            d.Page.SensorToAdd = d.Live.GpuHotSpot;
            d.Page.AddSensorTileCommand.Execute(null);
            var tile = d.Page.Tiles[1];
            Assert.Equal("GPU Hot Spot", tile.Title);
            // Renamed on All sensors while the dashboard (and its tiles) stay built.
            var changed = Kit.Changes(tile, () => d.Live.GpuHotSpot!.Label = "Hot spot");
            Assert.Equal("Hot spot", tile.Title);
            Assert.Contains(nameof(TileViewModel.Title), changed);
            d.Live.GpuHotSpot!.Label = "";
            Assert.Equal("GPU Hot Spot", tile.Title);
        });
    }

    [Fact]
    public void A_removed_sensor_tile_is_not_kept_alive_by_its_sensor()
    {
        var d = Make();
        var weak = Ui.Run(() =>
        {
            d.Page.SensorToAdd = d.Live.GpuHotSpot;
            d.Page.AddSensorTileCommand.Execute(null);
            var tile = d.Page.Tiles[0];
            d.Page.Remove(tile);
            return new WeakReference(tile);
        });
        Ui.Pump(50);
        for (int i = 0; i < 3 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); Ui.Pump(20); }
        Assert.False(weak.IsAlive);
        GC.KeepAlive(d);
    }

    [Fact]
    public void Tile_colors_follow_the_hardware()
    {
        var d = Make();
        Ui.Run(() =>
        {
            Brush Res(string key) => (Brush)System.Windows.Application.Current.FindResource(key);
            d.Page.SensorToAdd = d.Live.CpuTemp; d.Page.AddSensorTileCommand.Execute(null);
            d.Page.SensorToAdd = d.Live.GpuTemp; d.Page.AddSensorTileCommand.Execute(null);
            d.Page.SensorToAdd = d.Live.RamLoad; d.Page.AddSensorTileCommand.Execute(null);
            d.Page.SensorToAdd = d.Live.Fans[0]; d.Page.AddSensorTileCommand.Execute(null);
            d.Page.AddTileCommand.Execute(Kind("fans"));
            Assert.Equal([Res("CpuBrush"), Res("GpuBrush"), Res("PurpleBrush"), Res("AccentBrush"), Res("AccentBrush")], d.Page.Tiles.Select(t => t.Accent));
        });
    }

    [Fact]
    public void Tiles_round_trip_through_settings()
    {
        var d = Make();
        Ui.Run(() =>
        {
            d.Page.AddTileCommand.Execute(Kind("drives"));
            var t = d.Page.Tiles[0];
            var config = t.ToConfig();
            Assert.Equal((t.Id, "drives", (string?)null, 0, 0, 6, 4), (config.Id, config.Kind, config.Sensor, config.X, config.Y, config.W, config.H));
            var again = new TileViewModel(config, d.Page);
            Assert.Equal((t.Id, t.Kind, t.X, t.Y, t.W, t.H, t.MinW, t.MinH), (again.Id, again.Kind, again.X, again.Y, again.W, again.H, again.MinW, again.MinH));
            // A tile kind this version doesn't know (from a newer one) still loads, with no limits of its own.
            var unknown = new TileViewModel(new TileConfig { Kind = "future-tile", W = 2, H = 2 }, d.Page);
            Assert.Null(unknown.Definition);
            Assert.Equal("future-tile", unknown.Title);
            Assert.Equal((1, 1), (unknown.MinW, unknown.MinH));
            Assert.Same(d.Page.Live, unknown.Live);
            Assert.Same(d.Page.Home, unknown.Home);
            Assert.Same(d.Page.Crashes, unknown.Crashes);
        });
    }

    [Fact]
    public void Refresh_loads_only_what_the_tiles_need()
    {
        var d = Make();
        Kit.Wait(() => d.Page.RefreshAsync());
        Ui.Run(() =>
        {
            Assert.False(d.Page.Home.Loaded);
            d.Page.AddTileCommand.Execute(Kind("most-used"));
        });
        Assert.True(Ui.WaitFor(() => d.Page.Home.Loaded, 15_000));
        Ui.Run(() => d.Page.AddTileCommand.Execute(Kind("crashes")));
        Assert.True(Ui.WaitFor(() => d.Page.Crashes.Since is not null, 15_000));
    }

    // ── Layout rules on their own ────────────────────────────────────────

    [Fact]
    public void Flow_untangles_overlapping_tiles_keeping_their_order_and_columns()
    {
        var d = Make();
        Ui.Run(() =>
        {
            var tiles = new List<TileViewModel>
            {
                new(new TileConfig { Kind = "fans", X = 0, Y = 5, W = 4, H = 4 }, d.Page),
                new(new TileConfig { Kind = "fans", X = 2, Y = 5, W = 4, H = 4 }, d.Page),
                new(new TileConfig { Kind = "fans", X = 10, Y = 0, W = 6, H = 2 }, d.Page), // past the right edge
            };
            TileLayout.Flow(tiles, pinned: null);
            Assert.Equal((6, 0), (tiles[2].X, tiles[2].Y));
            Assert.Equal((0, 0), (tiles[0].X, tiles[0].Y));
            Assert.Equal((2, 4), (tiles[1].X, tiles[1].Y));
            AssertTidy(tiles);
        });
    }

    [Fact]
    public void Find_spot_skips_placeholders_and_uses_the_whole_width()
    {
        var d = Make();
        Ui.Run(() =>
        {
            var wide = new TileViewModel(new TileConfig { Kind = "temp-chart", W = 12, H = 4 }, d.Page);
            Assert.Equal((0, 4), TileLayout.FindSpot([wide], 12, 1));
            Assert.Equal((0, 0), TileLayout.FindSpot([TileViewModel.PlaceholderFor(wide)], 12, 4));
            Assert.Equal((0, 0), TileLayout.FindSpot([], 1, 1));
        });
    }
}
