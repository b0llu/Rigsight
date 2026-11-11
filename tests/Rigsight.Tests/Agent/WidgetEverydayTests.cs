using System.Drawing;
using System.Drawing.Imaging;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>
/// The everyday tiles of part three (approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U): Media, Weather, Shortcuts and Photos.
/// The weather's answers are read from MET Norway's and OpenStreetMap's own formats (no internet here), a photos tile
/// takes turns through its folder by the clock, and what's clickable on the desktop is where it's drawn.
/// </summary>
[Collection("Drawing")]
public class WidgetEverydayTests : IDisposable
{
    private readonly Func<DateTime> _now = WidgetRenderer.Now;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "photos-" + Guid.NewGuid().ToString("N")[..8]);

    public WidgetEverydayTests() => WidgetRenderer.Now = () => new DateTime(2026, 11, 11, 9, 14, 32);

    public void Dispose()
    {
        WidgetRenderer.Now = _now;
        WeatherFeed.Latest.Clear();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private const string Met = """
        {"type":"Feature","properties":{"timeseries":[
          {"time":"2026-11-11T03:00:00Z","data":{"instant":{"details":{"air_temperature":21.4}},"next_1_hours":{"summary":{"symbol_code":"partlycloudy_day"}}}},
          {"time":"2026-11-11T06:00:00Z","data":{"instant":{"details":{"air_temperature":26.0}}}},
          {"time":"2026-11-11T12:00:00Z","data":{"instant":{"details":{"air_temperature":17.5}}}},
          {"time":"2026-11-13T12:00:00Z","data":{"instant":{"details":{"air_temperature":2.0}}}}
        ]}}
        """;

    [Fact]
    public void The_weather_is_read_from_MET_Norways_forecast()
    {
        var w = WeatherFeed.Parse(Met, new DateTime(2026, 11, 11, 9, 0, 0))!;
        Assert.Equal((21.4, "partlycloudy_day"), (w.TempC, w.Symbol));
        // Today's range leaves out the days after.
        Assert.Equal((26.0, 17.5), (w.HighC, w.LowC));
        Assert.Null(WeatherFeed.Parse("""{"properties":{"timeseries":[]}}""", DateTime.Now));
        Assert.Equal("Partly cloudy", WeatherFeed.Words(w.Symbol));
        Assert.Equal((WeatherFeed.Sky.Rain, false), WeatherFeed.SkyOf("lightrainshowers_day"));
        Assert.Equal((WeatherFeed.Sky.Clear, true), WeatherFeed.SkyOf("clearsky_night"));
        Assert.Equal((WeatherFeed.Sky.Thunder, false), WeatherFeed.SkyOf("heavyrainshowersandthunder_day"));
        Assert.Equal("Light rain", WeatherFeed.Words("lightrain"));
    }

    [Fact]
    public void A_place_search_gives_short_names_with_their_region()
    {
        const string json = """
            [{"lat":"18.5214","lon":"73.8545","name":"Pune","display_name":"Pune, Pune City, Pune District, Maharashtra, 411001, India"},
             {"lat":"51.5074","lon":"-0.1278","name":"London","display_name":"London, Greater London, England, United Kingdom"},
             {"lat":"x","lon":"1","name":"Nowhere","display_name":"Nowhere"}]
            """;
        var places = WeatherFeed.ParsePlaces(json);
        Assert.Equal(2, places.Count);
        Assert.Equal(new WeatherPlace("Pune", "Maharashtra, India", 18.5214, 73.8545), places[0]);
        Assert.Equal("England, United Kingdom", places[1].Detail);
        // Only the place's latitude and longitude, to two decimals, go to MET Norway.
        Assert.Equal("18.52,73.85", WeatherFeed.Key(places[0].Lat, places[0].Lon));
    }

    [Fact]
    public void Saved_tiles_are_kept_sensible()
    {
        var links = Enumerable.Range(0, 20).Select(i => new TileLink { Path = $@"C:\App{i}.exe", Name = $"App {i}" }).ToList();
        links.Insert(0, new TileLink { Path = " ", Name = "blank" });
        var w = new WidgetConfig
        {
            Style = WidgetStyle.Custom, Id = "custom-x",
            Tiles =
            [
                new() { Kind = WidgetTiles.Shortcuts, W = 6, H = 2, Links = links },
                new() { Kind = WidgetTiles.Weather, X = 6, W = 6, H = 2, Place = "Mars", Lat = 120, Lon = 5 },
                new() { Kind = WidgetTiles.Photos, Y = 2, W = 6, H = 4, Folder = "  ", Every = 7 },
            ],
        };
        WidgetTiles.Clean(w);
        Assert.Equal(WidgetTiles.MaxLinks, w.Tiles![0].Links!.Count);
        Assert.Equal("App 0", w.Tiles[0].Links![0].Name);
        Assert.Equal((null, null), (w.Tiles[1].Lat, w.Tiles[1].Lon));
        Assert.Equal((null, 60), (w.Tiles[2].Folder, w.Tiles[2].Every));
        // A tile's copy is its own (the editor's undo keeps copies).
        var copy = w.Tiles[0].Copy();
        copy.Links![0].Name = "changed";
        Assert.Equal("App 0", w.Tiles[0].Links![0].Name);
    }

    private void MakePictures(int n)
    {
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < n; i++)
        {
            using var bmp = new Bitmap(40, 30);
            using (var g = Graphics.FromImage(bmp)) g.Clear(Color.FromArgb(40 * i, 90, 160));
            bmp.Save(Path.Combine(_dir, $"p{i}.png"), ImageFormat.Png);
        }
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a picture");
    }

    [Fact]
    public void A_photos_tile_takes_turns_through_its_folder_by_the_clock()
    {
        MakePictures(3);
        var t = new WidgetTile { Kind = WidgetTiles.Photos, Folder = _dir, Every = 60 };
        var start = new DateTime(2026, 11, 11, 9, 0, 0);
        var seen = Enumerable.Range(0, 6).Select(m => Path.GetFileName(WidgetFiles.PictureNow(t, start.AddMinutes(m)))).ToList();
        Assert.Equal(3, seen.Distinct().Count());
        Assert.Equal(seen[0], seen[3]); // round again after three
        Assert.Equal(seen[0], Path.GetFileName(WidgetFiles.PictureNow(t, start.AddSeconds(59))));
        Assert.Equal(3, WidgetFiles.PicturesIn(_dir).Length);
        Assert.NotNull(WidgetFiles.Picture(WidgetFiles.PictureNow(t, start)!));
        Assert.Null(WidgetFiles.PictureNow(new WidgetTile { Kind = WidgetTiles.Photos }, start));
    }

    private static WidgetConfig Box(params WidgetTile[] tiles) => new() { Style = WidgetStyle.Custom, Id = "custom-everyday", Tiles = [.. tiles], BackgroundOpacity = 1 };

    // A folder that can't be opened (gone, on a drive or PC that isn't there) said "No pictures in this folder." (the
    // audit, 2026-11-11); an empty one still does.
    [Fact]
    public void A_photos_folder_that_cant_be_opened_is_told_from_an_empty_one()
    {
        Directory.CreateDirectory(_dir);
        string gone = Path.Combine(_dir, "not-there");
        Assert.Empty(WidgetFiles.PicturesIn(_dir));
        Assert.False(WidgetFiles.CantOpen(_dir));
        Assert.Empty(WidgetFiles.PicturesIn(gone));
        Assert.True(WidgetFiles.CantOpen(gone));

        static string Shown(WidgetConfig cfg)
        {
            using var bmp = WidgetRenderer.Render(cfg, new WidgetData(), 1, hover: false, out _);
            return string.Join(",", Enumerable.Range(0, bmp.Width * bmp.Height / 7).Select(i => bmp.GetPixel(i * 7 % bmp.Width, i * 7 / bmp.Width).ToArgb()));
        }
        // The two lines differ on the tile.
        Assert.NotEqual(Shown(Box(new WidgetTile { Kind = WidgetTiles.Photos, W = 6, H = 4, Folder = _dir })),
            Shown(Box(new WidgetTile { Kind = WidgetTiles.Photos, W = 6, H = 4, Folder = gone })));
    }

    // An emoji typed in a note came out as an empty box: GDI+ never looks for Segoe UI Emoji (the audit, 2026-11-11).
    // A text with one is drawn in it (its letters are Segoe UI's, the same size), and one without is left as it was.
    [Fact]
    public void A_text_with_an_emoji_is_drawn_in_Segoe_UI_Emoji_and_one_without_is_left_alone()
    {
        using var font = new Font("Segoe UI", 14, GraphicsUnit.Pixel);
        Assert.Same(font, WidgetRenderer.ForText(font, "Game night at 9"));
        Assert.False(WidgetRenderer.HasEmoji("Café → 9:00 © Ünïcode 日本"));
        foreach (var text in new[] { "Game night \U0001F3AE", "Done ✅", "Love ❤️", "\U0001F44D\U0001F3FD", "\U0001F1EE\U0001F1F3" })
        {
            Assert.True(WidgetRenderer.HasEmoji(text), text);
            var used = WidgetRenderer.ForText(font, text);
            Assert.Equal("Segoe UI Emoji", used.Name);
            // The same letters, the same width: nothing moves.
            using var bmp = new Bitmap(10, 10);
            using var g = Graphics.FromImage(bmp);
            Assert.Equal(g.MeasureString("Game night", font).Width, g.MeasureString("Game night", used).Width, 0.01);
        }

        // On a note: two different emoji drew the same empty box before; now each is its own picture.
        string Note(string text)
        {
            using var bmp = WidgetRenderer.Render(Box(new WidgetTile { Kind = WidgetTiles.Notes, W = 6, H = 4, Text = text }), new WidgetData(), 1, hover: false, out _);
            return string.Join(",", Enumerable.Range(0, bmp.Width * bmp.Height / 3).Select(i => bmp.GetPixel(i * 3 % bmp.Width, i * 3 / bmp.Width).ToArgb()));
        }
        Assert.NotEqual(Note("Tonight \U0001F3AE"), Note("Tonight \U0001F600"));

        // A picture for a person: %TEMP%\rigsight-tests\boxes\emoji-note.png.
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Notes, W = 8, H = 4, Text = "Game night \U0001F3AE at 9\nBring snacks \U0001F355 ❤️" },
            new WidgetTile { Kind = WidgetTiles.Todo, Y = 4, W = 8, H = 4, Todos = [new() { Text = "Update drivers ✅", Done = true }, new() { Text = "Clean the fans \U0001F9F9" }] });
        using var shot = WidgetRenderer.Render(cfg, new WidgetData(), 1.5f, hover: false, out _);
        var dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "boxes");
        Directory.CreateDirectory(dir);
        shot.Save(Path.Combine(dir, "emoji-note.png"), ImageFormat.Png);
    }

    [Fact]
    public void Media_and_shortcuts_are_clickable_where_they_are_drawn()
    {
        using var cover = new Bitmap(60, 60);
        var data = new WidgetData { Media = new MediaNow("Night Drive", "Example Artist", true, cover, true, true, 1) };
        var cfg = Box(
            new WidgetTile { Kind = WidgetTiles.Media, W = 9, H = 2 },
            new WidgetTile { Kind = WidgetTiles.Shortcuts, Y = 2, W = 9, H = 2, Links = [new() { Path = "https://rigsight.app/", Name = "RigSight" }, new() { Path = Path.GetTempPath(), Name = "Temp" }] });
        using var bmp = WidgetRenderer.Render(cfg, data, 1, hover: false, out _);
        var hits = WidgetRenderer.Hits!;
        Assert.Equal([WidgetRenderer.HitPrevious, WidgetRenderer.HitPlay, WidgetRenderer.HitNext], hits.Where(h => h.Tile == 0).OrderBy(h => h.Rect.X).Select(h => h.Action));
        Assert.Equal([0, 1], hits.Where(h => h.Tile == 1 && h.Action == WidgetRenderer.HitOpen).Select(h => h.Index));
        // Nothing playing: no buttons to press.
        using var none = WidgetRenderer.Render(cfg, new WidgetData(), 1, hover: false, out _);
        Assert.DoesNotContain(WidgetRenderer.Hits!, h => h.Tile == 0);
    }

    [Fact]
    public void A_weather_tile_shows_its_weather_once_it_has_come()
    {
        var t = new WidgetTile { Kind = WidgetTiles.Weather, W = 6, H = 2, Place = "Pune", Lat = 18.52, Lon = 73.85 };
        var cfg = Box(t);
        string? before = WidgetRenderer.ChangeKey(cfg, new WidgetData());
        WeatherFeed.Latest[WeatherFeed.Key(18.52, 73.85)] = WeatherFeed.Parse(Met, new DateTime(2026, 11, 11, 9, 0, 0))!;
        Assert.NotEqual(before, WidgetRenderer.ChangeKey(cfg, new WidgetData())); // drawn again when it comes
        using var bmp = WidgetRenderer.Render(cfg, new WidgetData(), 1.5f, hover: false, out _);
        Assert.True(bmp.Width > 0);
    }

    // A picture of the four, for a person to compare with the mock-up: %TEMP%\rigsight-tests\boxes\everyday.png.
    [Fact]
    public void The_four_as_they_look()
    {
        MakePictures(2);
        WeatherFeed.Latest[WeatherFeed.Key(18.52, 73.85)] = WeatherFeed.Parse(Met, new DateTime(2026, 11, 11, 9, 0, 0))!;
        using var cover = new Bitmap(60, 60);
        using (var g = Graphics.FromImage(cover)) g.Clear(Color.FromArgb(124, 92, 255));
        var data = new WidgetData { Media = new MediaNow("Night Drive", "Example Artist", true, cover, true, true, 1) };
        var cfg = Box(
            new WidgetTile { Kind = WidgetTiles.Media, W = 9, H = 2 },
            new WidgetTile { Kind = WidgetTiles.Weather, Y = 2, W = 6, H = 2, Place = "Pune", Lat = 18.52, Lon = 73.85 },
            new WidgetTile { Kind = WidgetTiles.Shortcuts, Y = 4, W = 9, H = 2, Links = [new() { Path = "https://rigsight.app/", Name = "RigSight" }, new() { Path = Path.GetTempPath(), Name = "Temp" }, new() { Path = Environment.ProcessPath!, Name = "Tests" }] },
            new WidgetTile { Kind = WidgetTiles.Photos, X = 9, W = 6, H = 6, Folder = _dir });
        foreach (var theme in new[] { WidgetTheme.Dark, WidgetTheme.Light })
        {
            cfg.Theme = theme;
            using var bmp = WidgetRenderer.Render(cfg, data, 1.5f, hover: false, out _);
            var dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "boxes");
            Directory.CreateDirectory(dir);
            bmp.Save(Path.Combine(dir, $"everyday-{theme}.png"), ImageFormat.Png);
        }
    }

    // Each tile small, medium and large, for a person to compare with the mock-up's "Everyday tiles, small to large"
    // board: %TEMP%\rigsight-tests\boxes\sizes-*.png.
    [Fact]
    public void Every_everyday_tile_small_medium_and_large()
    {
        MakePictures(3);
        var start = new DateTime(2026, 11, 11, 9, 0, 0);
        string Hour(int h) => start.AddHours(h).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        var steps = new List<string>();
        for (int h = 0; h < 72; h++)
            steps.Add("{\"time\":\"" + Hour(h) + "\",\"data\":{\"instant\":{\"details\":{\"air_temperature\":" + (20 + h % 7) + "}},\"next_1_hours\":{\"summary\":{\"symbol_code\":\""
                + (h % 3 == 0 ? "partlycloudy_day" : h % 3 == 1 ? "clearsky_day" : "lightrain") + "\"}}}}");
        var w = WeatherFeed.Parse("{\"properties\":{\"timeseries\":[" + string.Join(",", steps) + "]}}", start)!;
        Assert.Equal(5, w.Hours.Count);
        Assert.All(w.Hours, h => Assert.Equal(0, h.At.Hour % 2));
        Assert.Equal(3, w.Days.Count);
        WeatherFeed.Latest[WeatherFeed.Key(18.52, 73.85)] = w;
        using var cover = new Bitmap(60, 60);
        using (var g = Graphics.FromImage(cover)) { g.Clear(Color.FromArgb(124, 92, 255)); g.FillEllipse(Brushes.HotPink, 20, 20, 40, 40); }
        var data = new WidgetData { Media = new MediaNow("Night Drive", "Example Artist", true, cover, true, true, 1, "Spotify", TimeSpan.FromSeconds(84), TimeSpan.FromSeconds(222), WidgetRenderer.Now()) };
        var links = new List<TileLink> { new() { Path = "https://rigsight.app/", Name = "RigSight" }, new() { Path = Path.GetTempPath(), Name = "Temp" }, new() { Path = Environment.ProcessPath!, Name = "Tests" },
            new() { Path = "https://github.com/", Name = "GitHub" }, new() { Path = Path.GetTempPath() + "x", Name = "Games" }, new() { Path = "https://example.com/", Name = "Example" } };
        var dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "boxes");
        Directory.CreateDirectory(dir);
        foreach (var (kind, sizes) in new (string, (int W, int H)[])[]
        {
            (WidgetTiles.Media, [(4, 2), (8, 2), (8, 3), (8, 7)]),
            (WidgetTiles.Weather, [(4, 2), (8, 3), (8, 7)]),
            (WidgetTiles.Shortcuts, [(6, 2), (6, 5)]),
            (WidgetTiles.Timer, [(6, 2), (6, 3)]),
            (WidgetTiles.Photos, [(6, 3), (6, 5)]),
            (WidgetTiles.Notes, [(6, 5)]),
        })
            foreach (var (tw, th) in sizes)
            {
                var tile = new WidgetTile { Kind = kind, W = tw, H = th, Place = "Pune", Lat = 18.52, Lon = 73.85, Links = links, Folder = _dir, RunningSince = 1, Minutes = 25,
                    Text = "BIOS update on Saturday\nReturn the spare fan\nAsk about the 1440p monitor", Look = kind == WidgetTiles.Notes ? WidgetTiles.Sticky : null };
                using var bmp = WidgetRenderer.Render(Box(tile), data, 1.5f, hover: false, out _);
                bmp.Save(Path.Combine(dir, $"sizes-{kind}-{tw}x{th}.png"), ImageFormat.Png);
                if (kind == WidgetTiles.Media)
                    Assert.Contains(WidgetRenderer.Hits!, h => h.Action == WidgetRenderer.HitPlay); // Pause can be pressed at every size
            }
        // Nothing playing.
        using var none = WidgetRenderer.Render(Box(new WidgetTile { Kind = WidgetTiles.Media, W = 8, H = 3 }), new WidgetData(), 1.5f, hover: false, out _);
        none.Save(Path.Combine(dir, "sizes-media-nothing.png"), ImageFormat.Png);
    }

    // Ready sizes (the user, 2026-11-11): every kind starts at one of them, each is at least its smallest, none twice.
    [Fact]
    public void Every_kind_starts_at_one_of_its_ready_sizes()
    {
        foreach (var k in WidgetTiles.Kinds)
        {
            var presets = WidgetTiles.Presets(k.Id);
            Assert.NotEmpty(presets);
            Assert.Contains(presets, p => p.W == k.W && p.H == k.H);
            Assert.All(presets, p => Assert.True(p.W >= k.MinW && p.H >= k.MinH, $"{k.Id} {p.Name} {p.W}x{p.H}"));
            Assert.Equal(presets.Count, presets.Select(p => (p.W, p.H)).Distinct().Count());
        }
    }

    // The buttons' hover and press, frame by frame (the user, 2026-11-11): each step of the ease draws differently, a
    // hover lights the button, a press makes it a little smaller; strips for a person to look at in boxes\anim-*.png.
    [Fact]
    public void Buttons_light_up_under_the_pointer_and_give_when_pressed()
    {
        using var cover = new Bitmap(60, 60);
        var data = new WidgetData { Media = new MediaNow("Night Drive", "Example Artist", true, cover, true, true, 1, "Spotify", TimeSpan.FromSeconds(84), TimeSpan.FromSeconds(222), WidgetRenderer.Now()) };
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Media, W = 8, H = 3 });
        var frames = new List<Bitmap>();
        try
        {
            foreach (var (action, hover, press) in new (string, float, float)[]
            {
                (WidgetRenderer.HitPlay, 0, 0), (WidgetRenderer.HitPlay, 0.35f, 0), (WidgetRenderer.HitPlay, 0.7f, 0), (WidgetRenderer.HitPlay, 1, 0),
                (WidgetRenderer.HitPlay, 1, 0.5f), (WidgetRenderer.HitPlay, 1, 1), (WidgetRenderer.HitNext, 1, 0), (WidgetRenderer.HitNext, 1, 1),
            })
            {
                WidgetRenderer.Pointer = new WidgetRenderer.PointerOn(cfg.Id, 0, action, 0, hover, press);
                frames.Add(WidgetRenderer.Render(cfg, data, 2, hover: false, out _));
            }
        }
        finally { WidgetRenderer.Pointer = null; }
        var play = WidgetRenderer.Hits!.Single(h => h.Action == WidgetRenderer.HitPlay).Rect;
        var next = WidgetRenderer.Hits!.Single(h => h.Action == WidgetRenderer.HitNext).Rect;
        long Diff(Bitmap a, Bitmap b, RectangleF r)
        {
            long sum = 0;
            for (int y = (int)(r.Top * 2); y < (int)(r.Bottom * 2); y++)
                for (int x = (int)(r.Left * 2); x < (int)(r.Right * 2); x++)
                {
                    Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                    sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
                }
            return sum;
        }
        // Each hover step draws differently, and more so the further it has gone.
        long d1 = Diff(frames[0], frames[1], play), d2 = Diff(frames[0], frames[2], play), d3 = Diff(frames[0], frames[3], play);
        Assert.True(d1 > 0 && d2 > d1 && d3 > d2, $"hover steps {d1} {d2} {d3}");
        // Pressed: different again; and the next button lights only when it's the one under the pointer.
        Assert.True(Diff(frames[3], frames[5], play) > 0);
        Assert.True(Diff(frames[0], frames[6], next) > 0);
        Assert.Equal(0, Diff(frames[0], frames[6], play));
        // A strip of the frames, side by side.
        var crop = Rectangle.Round(new RectangleF(play.Left * 2 - 90, play.Top * 2 - 10, play.Width * 2 + 180, play.Height * 2 + 20));
        using var strip = new Bitmap(crop.Width * frames.Count, crop.Height);
        using (var g = Graphics.FromImage(strip))
            for (int i = 0; i < frames.Count; i++) g.DrawImage(frames[i], new Rectangle(i * crop.Width, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
        var dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "boxes");
        Directory.CreateDirectory(dir);
        strip.Save(Path.Combine(dir, "anim-media-buttons.png"), ImageFormat.Png);
        foreach (var f in frames) f.Dispose();
    }
}
