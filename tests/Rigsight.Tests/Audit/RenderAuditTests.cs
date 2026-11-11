using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Settings;
using static Rigsight.Tests.Agent.WidgetSamples;

namespace Rigsight.Tests.Audit;

/// <summary>
/// An audit of how widgets are drawn (2026-11-11): every tile kind at many sizes, scales, themes and both languages with
/// odd data, where its clickable places are, what its change key says, and the files its pictures come from.
/// </summary>
[Collection("Drawing")]
public class RenderAuditTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly Func<DateTime> _now = WidgetRenderer.Now;
    private readonly bool _fahrenheit = Units.Fahrenheit;
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "audit3-" + Guid.NewGuid().ToString("N")[..8]);

    public RenderAuditTests(ITestOutputHelper output)
    {
        this.output = output;
        WidgetRenderer.Now = () => new DateTime(2026, 11, 11, 9, 14, 32);
    }

    private static readonly string Pictures = Path.Combine(Path.GetTempPath(), "rigsight-tests", "audit3");

    public void Dispose()
    {
        WidgetRenderer.Now = _now;
        WidgetRenderer.Typing = null;
        WidgetRenderer.Scrolls.Clear();
        WeatherFeed.Latest.Clear();
        Units.Fahrenheit = _fahrenheit;
        CultureInfo.CurrentCulture = _culture;
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Keep(Bitmap bmp, string name)
    {
        Directory.CreateDirectory(Pictures);
        bmp.Save(Path.Combine(Pictures, name + ".png"), ImageFormat.Png);
    }

    private static WidgetConfig Box(params WidgetTile[] tiles) => new() { Style = WidgetStyle.Custom, Id = "custom-audit", Tiles = [.. tiles], BackgroundOpacity = 1 };

    private static float GapOf(WidgetGap gap) => gap switch { WidgetGap.Small => 4, WidgetGap.Normal => 8, WidgetGap.Large => 14, _ => 0 };

    /// <summary>A tile's place on the box, as the renderer works it out (WidgetRenderer.TileRect).</summary>
    private static RectangleF TileRect(WidgetTile t, WidgetConfig cfg)
    {
        float gap = GapOf(cfg.Gap);
        return new RectangleF(WidgetTiles.Edge + t.X * WidgetTiles.CellWidth + gap / 2, WidgetTiles.Edge + t.Y * WidgetTiles.CellHeight + gap / 2,
            t.W * WidgetTiles.CellWidth - gap, t.H * WidgetTiles.CellHeight - gap);
    }

    private static bool Inside(RectangleF inner, RectangleF outer) =>
        inner.Left >= outer.Left - 0.5f && inner.Top >= outer.Top - 0.5f && inner.Right <= outer.Right + 0.5f && inner.Bottom <= outer.Bottom + 0.5f;

    private static readonly LanguagePack? Spanish = LanguageStore.Find("es");

    // â”€â”€ Every kind, every size, odd data â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private WeatherNow SampleWeather(bool empty, double temp)
    {
        var start = new DateTime(2026, 11, 11, 9, 0, 0);
        return new WeatherNow(temp, empty ? "" : "lightrainshowersandthunder_night", empty ? null : temp + 9, empty ? null : temp - 3, start)
        {
            Hours = empty ? [] : [.. Enumerable.Range(1, 5).Select(i => new WeatherHour(start.AddHours(i * 2), temp + i, i % 2 == 0 ? "fog" : "heavysnow"))],
            Days = empty ? [] : [.. Enumerable.Range(1, 3).Select(i => new WeatherDay(start.Date.AddDays(i), temp - i, temp + i * 10, "clearsky_day"))],
        };
    }

    private WidgetTile Odd(string kind, int w, int h, int variant)
    {
        var t = WidgetTiles.New(WidgetTiles.Kind(kind)!);
        (t.W, t.H) = (w, h);
        t.Style = WidgetTiles.ClockStyles[variant % WidgetTiles.ClockStyles.Count];
        t.Seconds = variant % 2 == 0;
        t.Hours24 = variant % 3 == 0 ? true : null;
        if (kind == WidgetTiles.Reading)
        {
            var looks = WidgetTiles.Looks(t.Item);
            t.Look = looks[variant % looks.Count];
        }
        t.TextSize = WidgetTiles.TextSizes[variant % 3].Name;
        t.Text = (variant % 4) switch
        {
            0 => null,
            1 => new string('\n', 40),
            2 => string.Concat(Enumerable.Repeat("ÃœnÃ¯cÃ¶dÃ© ðŸ˜€ ×©×œ×•× ×¢×•×œ× e\u0301 averyveryveryverylongwordwithoutanyspacesatallforwrapping ", 40)),
            _ => new string('x', WidgetTiles.MaxNoteLength),
        };
        t.Look = kind == WidgetTiles.Notes ? (variant % 2 == 0 ? WidgetTiles.Sticky : null) : t.Look;
        t.Todos = (variant % 3) switch
        {
            0 => [],
            1 => [new TodoItem { Text = new string('W', 500), Done = true }],
            _ => [.. Enumerable.Range(0, 200).Select(i => new TodoItem { Text = $"Item {i} ðŸ˜€ ×©×œ×•×", Done = i % 3 == 0 })],
        };
        t.TimerKind = WidgetTiles.TimerKinds[variant % 3];
        t.Minutes = variant % 2 == 0 ? 1 : 24 * 60;
        t.RunMs = variant % 4 == 1 ? 30L * 3600_000 : 0;
        t.RunningSince = variant % 2 == 0 ? new DateTimeOffset(WidgetRenderer.Now()).ToUnixTimeMilliseconds() - 1000 : null;
        t.Until = (variant % 4) switch { 0 => null, 1 => DateTime.MaxValue.Date, 2 => DateTime.MinValue, _ => WidgetRenderer.Now().Date };
        if (variant % 3 != 0) (t.Place, t.Lat, t.Lon) = ("Ciudad de MÃ©xico con un nombre muy largo", 19.43, -99.13);
        t.Links = variant % 2 == 0 ? [] :
        [
            new() { Path = @"C:\nowhere\missing.exe", Name = "Missing app with a very long name" },
            new() { Path = "https://ä¾‹ãˆ.jp/", Name = "æ—¥æœ¬èªž ðŸ˜€" },
            new() { Path = Path.GetTempPath(), Name = "" },
            .. Enumerable.Range(0, 9).Select(i => new TileLink { Path = $@"C:\nowhere\{i}.txt", Name = $"File {i}" }),
        ];
        t.Folder = variant % 3 == 0 ? null : variant % 3 == 1 ? @"C:\nowhere\deleted-folder" : _dir;
        t.Items = kind == WidgetTiles.Line ? WidgetCatalog.Items(OverlayMetric.CpuTemp, OverlayMetric.GpuTemp, OverlayMetric.Ram, OverlayMetric.Fps, OverlayMetric.TopApp) : t.Items;
        return t;
    }

    private static WidgetData OddData(int variant, Bitmap cover)
    {
        var d = (variant % 3) switch { 0 => Full(), 1 => Extreme(), _ => Zero() };
        var at = WidgetRenderer.Now();
        d.Media = (variant % 6) switch
        {
            0 => null,
            1 => new MediaNow("", "", true, null, false, false, 1),
            2 => new MediaNow(new string('M', 300), "Artist ðŸ˜€ ×©×œ×•×", false, cover, true, true, 2, "Spotify", TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(3), at),
            3 => new MediaNow("Title", "", true, cover, true, true, 3, "", TimeSpan.FromSeconds(-5), TimeSpan.Zero, at.AddHours(-30)),
            4 => new MediaNow("Long track", "Someone", true, cover, true, true, 4, "App", TimeSpan.FromHours(9), TimeSpan.FromHours(10), at.AddDays(-400)),
            _ => new MediaNow("Paused", "Someone", false, cover, true, true, 5, "App", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(31), default),
        };
        return d;
    }

    [Fact]
    public void Every_kind_draws_everywhere_with_its_clickable_places_inside_it()
    {
        WidgetRenderer.Now = () => new DateTime(2026, 11, 11, 23, 59, 58);
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "empty.jpg"), []);
        File.WriteAllText(Path.Combine(_dir, "broken.png"), "not a picture");
        using (var ok = new Bitmap(30, 3000)) ok.Save(Path.Combine(_dir, "tall.png"), ImageFormat.Png);
        using var cover = new Bitmap(2000, 7);
        float[] scales = [1, 1.25f, 1.5f, 2, 2.5f, 3];
        var failures = new List<string>();
        int n = 0;
        foreach (var k in WidgetTiles.Kinds)
        {
            var sizes = new List<(int W, int H)> { (k.MinW, k.MinH), (k.W, k.H), (k.MinW, 12), (16, k.MinH), (k.MinW + 1, k.MinH + 1) };
            sizes.AddRange(WidgetTiles.Presets(k.Id).Select(p => (p.W, p.H)));
            foreach (var (w, h) in sizes.Distinct())
                foreach (float scale in scales)
                    foreach (var lang in new[] { null, Spanish })
                    {
                        int v = n++;
                        using var here = Lang.UseHere(lang);
                        Units.Fahrenheit = v % 2 == 1;
                        WeatherFeed.Latest[WeatherFeed.Key(19.43, -99.13)] = SampleWeather(v % 5 == 0, v % 7 == 0 ? double.NaN : v % 7 == 1 ? -42 : 31.4);
                        var tile = Odd(k.Id, w, h, v);
                        // Another tile next to it and under it, so a place that runs over shows on the wrong tile.
                        var right = new WidgetTile { Kind = WidgetTiles.Clock, X = w, W = 2, H = h };
                        var below = new WidgetTile { Kind = WidgetTiles.Clock, Y = h, W = w, H = 1 };
                        var cfg = Box(tile, right, below);
                        cfg.Theme = Enum.GetValues<WidgetTheme>()[v % 6];
                        cfg.Grayscale = v % 4 == 3;
                        cfg.Gap = (WidgetGap)(v % 4);
                        cfg.Corners = (WidgetCorners)(v % 3);
                        cfg.TileBoxes = v % 2 == 0;
                        cfg.TileLines = v % 3 == 0;
                        cfg.BackgroundOpacity = (v % 3) switch { 0 => 1, 1 => 0.3, _ => 0 };
                        cfg.Font = v % 5 == 4 ? "No Such Font Anywhere" : null;
                        string what = $"{k.Id} {w}x{h} at {scale} {(lang is null ? "en" : "es")} variant {v} theme {cfg.Theme} gap {cfg.Gap}";
                        try
                        {
                            using var bmp = WidgetRenderer.Render(cfg, OddData(v, cover), scale, hover: v % 2 == 0, out _);
                            var (pw, ph) = WidgetTiles.BoxPixels(cfg.Tiles!);
                            if (Math.Abs(bmp.Width - pw * scale) > 1 || Math.Abs(bmp.Height - ph * scale) > 1) failures.Add($"{what}: size {bmp.Width}x{bmp.Height}");
                            var rect = TileRect(tile, cfg);
                            foreach (var hit in WidgetRenderer.Hits!.Where(x => x.Tile == 0))
                                if (!Inside(hit.Rect, rect)) failures.Add($"{what}: {hit.Action} {hit.Index} at {hit.Rect} outside the tile {rect}");
                            foreach (var area in WidgetRenderer.ScrollAreas!.Where(x => x.Tile == 0))
                                if (!Inside(area.Rect, rect)) failures.Add($"{what}: scroll area {area.Rect} outside the tile {rect}");
                            if (WidgetRenderer.Hits!.Any(x => x.Tile != 0)) failures.Add($"{what}: a clock has a clickable place");
                            // Two places of one tile never on top of each other (a click would go to either).
                            var own = WidgetRenderer.Hits!.Where(x => x.Tile == 0).ToList();
                            for (int a = 0; a < own.Count; a++)
                                for (int b = a + 1; b < own.Count; b++)
                                    if (RectangleF.Intersect(own[a].Rect, own[b].Rect) is { Width: > 0.5f, Height: > 0.5f })
                                        failures.Add($"{what}: {own[a].Action} {own[a].Index} {own[a].Rect} over {own[b].Action} {own[b].Index} {own[b].Rect}");
                            if (v % 97 == 0) Keep(bmp, $"stress-{k.Id}-{w}x{h}-{v}");
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"{what}: {ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
                        }
                        // The change key works it out too, with nothing thrown.
                        // The change key works it out too, with nothing thrown, and the same twice over the same readings.
                        try
                        {
                            var data = OddData(v, cover);
                            if (WidgetRenderer.ChangeKey(cfg, data) != WidgetRenderer.ChangeKey(cfg, data)) failures.Add($"{what}: change key not the same twice");
                        }
                        catch (Exception ex) { failures.Add($"{what}: change key {ex.GetType().Name} {ex.Message}"); }
                    }
        }
        output.WriteLine($"{n} pictures drawn, {failures.Count} problems");
        foreach (var f in failures.Take(60)) output.WriteLine(f);
        Assert.Empty(failures);
    }

    private static byte[] Bytes(Bitmap b)
    {
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    // ── A to-do list in a short tile ─────────────────────────────────────────

    // A to-do two rows high with large text and a large gap: its one row ran past the tile, so its box was cut off at the
    // bottom and a click on the tile under it ticked the item.
    [Fact]
    public void A_short_to_do_lists_row_stays_inside_its_tile()
    {
        var todo = new WidgetTile { Kind = WidgetTiles.Todo, W = 4, H = 2, TextSize = WidgetTiles.TextSizes[2].Name, Todos = [new() { Text = "Back up" }] };
        var below = new WidgetTile { Kind = WidgetTiles.Clock, Y = 2, W = 4, H = 1 };
        var cfg = Box(todo, below);
        cfg.Gap = WidgetGap.Large;
        using var bmp = WidgetRenderer.Render(cfg, Full(), 3, hover: false, out _);
        Keep(bmp, "todo-short-large-text");
        var tile = TileRect(todo, cfg);
        Assert.All(WidgetRenderer.Hits!.Where(h => h.Tile == 0), h => Assert.True(Inside(h.Rect, tile), $"{h.Action} at {h.Rect}, tile {tile}"));
        Assert.All(WidgetRenderer.ScrollAreas!.Where(a => a.Tile == 0), a => Assert.True(Inside(a.Rect, tile), $"scroll area {a.Rect}, tile {tile}"));
    }

    // ── Change keys: the same key must mean the same picture ─────────────────

    // A big ring of a CPU's temperature has its load under it (the user, 2026-11-10); the load wasn't in the key, so the
    // picture kept an old load while the temperature stayed the same.
    [Fact]
    public void A_ring_with_its_load_is_drawn_again_when_only_the_load_changes()
    {
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Reading, W = 3, H = 4, Item = nameof(OverlayMetric.CpuTemp), Look = WidgetTiles.Ring });
        var a = Full();
        var b = Full();
        b.CpuLoad = 91;
        using (var pa = WidgetRenderer.Render(cfg, a, 1, hover: false, out _))
        using (var pb = WidgetRenderer.Render(cfg, b, 1, hover: false, out _))
            Assert.NotEqual(Bytes(pa), Bytes(pb)); // the picture changes...
        Assert.NotEqual(WidgetRenderer.ChangeKey(cfg, a), WidgetRenderer.ChangeKey(cfg, b)); // ...so its key must
    }

    // A reading tile with no reading saved draws the CPU's temperature (as HistoryMetrics reads it); its key read nothing.
    [Fact]
    public void A_reading_tile_with_no_reading_set_is_drawn_again_when_the_CPU_temperature_changes()
    {
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Reading, W = 3, H = 2, Item = null, Look = WidgetTiles.Number });
        var a = Full();
        var b = Full();
        b.CpuTemp = 88;
        using (var pa = WidgetRenderer.Render(cfg, a, 1, hover: false, out _))
        using (var pb = WidgetRenderer.Render(cfg, b, 1, hover: false, out _))
            Assert.NotEqual(Bytes(pa), Bytes(pb));
        Assert.NotEqual(WidgetRenderer.ChangeKey(cfg, a), WidgetRenderer.ChangeKey(cfg, b));
    }

    // ── Typing ───────────────────────────────────────────────────────────────

    // GDI+ leaves trailing spaces out of a measured width: a space typed into a new to-do item didn't move the caret.
    [Fact]
    public void The_caret_moves_on_after_a_space_typed_into_a_new_to_do()
    {
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Todo, W = 6, H = 4 });
        WidgetRenderer.Typing = new WidgetRenderer.TypingNow(cfg.Id, 0, "Back", 4, Adding: true);
        using var before = WidgetRenderer.Render(cfg, new WidgetData(), 2, hover: false, out _);
        WidgetRenderer.Typing = new WidgetRenderer.TypingNow(cfg.Id, 0, "Back ", 5, Adding: true);
        using var after = WidgetRenderer.Render(cfg, new WidgetData(), 2, hover: false, out _);
        Keep(after, "todo-typing-space");
        Assert.NotEqual(Bytes(before), Bytes(after));
    }

    private static RectangleF Caret(string text, int at, float width)
    {
        var method = typeof(WidgetRenderer).GetMethod("CaretAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.None };
        fmt.FormatFlags &= ~StringFormatFlags.NoWrap;
        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        return (RectangleF)method.Invoke(null, [g, text, at, font, new RectangleF(0, 0, width, 100), fmt, font.GetHeight(g)])!;
    }

    // The caret at every place in a note with wrapped words, runs of empty lines, an emoji (two UTF-16 units), an accent
    // put on a letter and right-to-left words: always in the note's width, and never on a line above the one before it.
    [Fact]
    public void The_caret_is_in_the_note_at_every_place()
    {
        string text = "Hello 😀 wörld é\n\n\nשלום עולם and averyveryverylongwordthatmustwrapsomewhere ok\n😀😀";
        float lastY = 0;
        var wrong = new List<string>();
        for (int i = 0; i <= text.Length; i++)
        {
            var c = Caret(text, i, 120);
            if (c.X < -0.5f || c.X > 121) wrong.Add($"{i}: x {c.X}");
            if (c.Y < lastY - 0.5f) wrong.Add($"{i}: y {c.Y} above {lastY}");
            lastY = Math.Max(lastY, c.Y);
        }
        Assert.Empty(wrong);
    }

    // A note of many short lines (a list pasted in): taller than the 100,000 px it was measured in, so its end couldn't
    // be scrolled to or typed at.
    [Fact]
    public void Typing_at_the_end_of_a_very_long_note_shows_its_end()
    {
        string text = string.Join("\n", Enumerable.Range(0, 7000).Select(i => "x"));
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Notes, W = 6, H = 4, Text = text });
        WidgetRenderer.Typing = new WidgetRenderer.TypingNow(cfg.Id, 0, text, text.Length, Adding: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var bmp = WidgetRenderer.Render(cfg, new WidgetData(), 1, hover: false, out _);
        output.WriteLine($"{sw.ElapsedMilliseconds} ms for a note of {text.Length} characters");
        int scroll = WidgetRenderer.Scrolls[(cfg.Id, 0)];
        Assert.True(scroll > 6900, $"scrolled to line {scroll} of 7000");
    }

    // A note at its longest, as a list (950 short lines) and as prose: a list was laid out whole at every picture and every
    // key typed (about 200 ms); drawn a paragraph at a time, only what's in sight is.
    [Theory]
    [Trait("Category", "Perf")]
    [Trait("Alone", "timing")]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void How_long_a_long_note_takes_to_draw(bool typing, bool sticky, bool list)
    {
        var words = list ? "Return the spare fan\n" : "BIOS update on Saturday, return the spare fan and ask about the 1440p monitor before the sale ends. ";
        string text = string.Concat(Enumerable.Repeat(words, WidgetTiles.MaxNoteLength / words.Length));
        text = text.Insert(text.Length / 2, "\n\n");
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Notes, W = 6, H = 4, Text = text, Look = sticky ? WidgetTiles.Sticky : null });
        if (typing) WidgetRenderer.Typing = new WidgetRenderer.TypingNow(cfg.Id, 0, text, text.Length, Adding: false);
        WidgetRenderer.Render(cfg, null, 1.5f, false, out _).Dispose();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 3; i++) WidgetRenderer.Render(cfg, null, 1.5f, false, out _).Dispose();
        output.WriteLine($"{text.Length} characters, typing {typing}, sticky {sticky}, list {list}: {sw.ElapsedMilliseconds / 3} ms a picture");
        // Measured on the development PC: a list 4-6 ms (about 200 before), prose 30-80 ms (one paragraph, laid out whole).
        Assert.True(sw.ElapsedMilliseconds / 3 < (list ? 60 : 400), $"{sw.ElapsedMilliseconds / 3} ms a picture");
    }

    // Typing into one long paragraph leaves a new paragraph to remember at every key: what's remembered stays small.
    [Fact]
    public void Typing_into_a_long_note_keeps_little_in_memory()
    {
        string text = new('x', 19_000);
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Notes, W = 6, H = 4, Text = text });
        for (int i = 0; i < 60; i++)
        {
            text += "y";
            WidgetRenderer.Typing = new WidgetRenderer.TypingNow(cfg.Id, 0, text, text.Length, Adding: false);
            WidgetRenderer.Render(cfg, null, 1, false, out _).Dispose();
        }
        var held = (long)typeof(WidgetRenderer).GetField("_noteLinesChars", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        Assert.True(held <= 240_000, $"{held:N0} characters remembered");
    }

    // Notes of every shape, kept as pictures: drawn a paragraph at a time they must look as they did drawn whole.
    [Fact]
    public void Notes_of_every_shape_as_pictures()
    {
        string[] notes =
        [
            "BIOS update on Saturday\nReturn the spare fan\nAsk about the 1440p monitor",
            "BIOS update on Saturday\r\nReturn the spare fan\r\n\r\nAsk about the 1440p monitor before the sale ends on Sunday night",
            "One very long paragraph that wraps over several lines in a narrow tile, with a word averyveryverylongwordwithoutspaces and more.",
            "\n\nStarts with empty lines\nand ends with them\n\n",
            "   spaces   \n\ttab\n😀 emoji שלום",
        ];
        for (int i = 0; i < notes.Length; i++)
            foreach (var (opacity, sticky, scroll) in new[] { (1.0, false, 0), (0.2, false, 0), (1.0, true, 0), (1.0, false, 2) })
            {
                var cfg = Box(new WidgetTile { Kind = WidgetTiles.Notes, W = 5, H = 3, Text = notes[i], Look = sticky ? WidgetTiles.Sticky : null });
                cfg.BackgroundOpacity = opacity;
                WidgetRenderer.Scrolls[(cfg.Id, 0)] = scroll;
                using var bmp = WidgetRenderer.Render(cfg, null, 1.5f, hover: false, out _);
                Keep(bmp, $"note-shape-{i}-{opacity}-{sticky}-{scroll}");
            }
        WidgetRenderer.Scrolls.Clear();
    }

    // ── Pictures ─────────────────────────────────────────────────────────────

    private static void SaveWithOrientation(string path, int orientation)
    {
        // 40 by 20, a green corner at the top left, the rest grey.
        using var bmp = new Bitmap(40, 20);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Gray);
            g.FillRectangle(Brushes.Lime, 0, 0, 12, 10);
        }
        var item = (PropertyItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PropertyItem));
        item.Id = 0x0112;
        item.Type = 3;
        item.Len = 2;
        item.Value = [(byte)orientation, 0];
        bmp.SetPropertyItem(item);
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var q = new EncoderParameters(1);
        q.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 100L);
        bmp.Save(path, codec, q);
    }

    // EXIF orientation: 1 to 8, the mirrored ones too (2, 4, 5, 7: a phone's front camera, some editors). Where the green
    // corner ends up, and the picture's shape.
    [Theory]
    [InlineData(1, 40, 20, "TL")]
    [InlineData(2, 40, 20, "TR")]
    [InlineData(3, 40, 20, "BR")]
    [InlineData(4, 40, 20, "BL")]
    [InlineData(5, 20, 40, "TL")]
    [InlineData(6, 20, 40, "TR")]
    [InlineData(7, 20, 40, "BR")]
    [InlineData(8, 20, 40, "BL")]
    public void A_picture_is_turned_and_mirrored_as_the_camera_says(int orientation, int w, int h, string corner)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, $"turn-{orientation}-{Guid.NewGuid():N}.jpg");
        SaveWithOrientation(path, orientation);
        var pic = WidgetFiles.Picture(path);
        Assert.NotNull(pic);
        Keep(pic, $"exif-{orientation}");
        Assert.Equal((w, h), (pic.Width, pic.Height));
        bool Green(int x, int y) => pic.GetPixel(x, y) is var c && c.G > 180 && c.R < 120;
        var found = new[] { ("TL", Green(2, 2)), ("TR", Green(w - 3, 2)), ("BL", Green(2, h - 3)), ("BR", Green(w - 3, h - 3)) }.Where(x => x.Item2).Select(x => x.Item1).ToList();
        Assert.Equal([corner], found);
    }

    // ── A sticky note ────────────────────────────────────────────────────────

    // A sticky note asked Windows for every installed font family, twice a picture, to see whether Segoe Print is there.
    [Fact]
    public void A_sticky_note_costs_about_what_a_plain_note_does()
    {
        var plain = Box(new WidgetTile { Kind = WidgetTiles.Notes, W = 6, H = 4, Text = "BIOS update on Saturday" });
        var sticky = Box(new WidgetTile { Kind = WidgetTiles.Notes, W = 6, H = 4, Text = "BIOS update on Saturday", Look = WidgetTiles.Sticky });
        (double Ms, long Bytes) Cost(WidgetConfig cfg)
        {
            for (int i = 0; i < 5; i++) WidgetRenderer.Render(cfg, null, 1.5f, false, out _).Dispose();
            GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 40; i++) WidgetRenderer.Render(cfg, null, 1.5f, false, out _).Dispose();
            return (sw.Elapsed.TotalMilliseconds / 40, (GC.GetAllocatedBytesForCurrentThread() - before) / 40);
        }
        var a = Cost(plain);
        var b = Cost(sticky);
        output.WriteLine($"plain {a.Ms:0.00} ms {a.Bytes:N0} B, sticky {b.Ms:0.00} ms {b.Bytes:N0} B");
        Assert.True(b.Bytes < a.Bytes + 8_000, $"sticky {b.Bytes:N0} B a picture, plain {a.Bytes:N0} B");
    }

    // A cover the media watcher has let go of (it keeps the last two; a widget's readings can be older after quick skips).
    [Theory]
    [InlineData(4, 2)]
    [InlineData(8, 3)]
    [InlineData(8, 7)]
    public void A_cover_let_go_of_draws_nothing_worse_than_no_cover(int w, int h)
    {
        var cover = new Bitmap(60, 60);
        cover.Dispose();
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Media, W = w, H = h });
        var data = new WidgetData { Media = new MediaNow("Night Drive", "Example Artist", true, cover, true, true, 1) };
        using var bmp = WidgetRenderer.Render(cfg, data, 1.5f, hover: false, out _);
        Keep(bmp, $"media-cover-gone-{w}x{h}");
    }

    // A to-do item typed longer than its line: what's typed last must show, not "…" with the caret past it. The end of
    // the line (where the caret is) must change when the last letter does.
    [Fact]
    public void A_long_item_being_typed_shows_its_end()
    {
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Todo, W = 5, H = 3 });
        string typed = "Back up the E drive before the BIOS update on Saturday night";
        Bitmap Draw(string text)
        {
            WidgetRenderer.Typing = new WidgetRenderer.TypingNow(cfg.Id, 0, text, text.Length, Adding: true);
            return WidgetRenderer.Render(cfg, null, 2, false, out _);
        }
        using var a = Draw(typed + "s");
        using var b = Draw(typed + "W");
        Keep(b, "todo-typing-long");
        // The line's last 60 px (at 2x) before the scroll bar's room: where the newest letter is.
        var add = Assert.Single(WidgetRenderer.Hits!, h => h.Action == WidgetRenderer.HitTodoAdd).Rect;
        int x1 = (int)(add.Right * 2), y0 = (int)(add.Top * 2), y1 = (int)(add.Bottom * 2);
        bool differs = false;
        for (int x = x1 - 60; x < x1 && !differs; x++)
            for (int y = y0; y < y1 && !differs; y++)
                differs = a.GetPixel(x, y) != b.GetPixel(x, y);
        Assert.True(differs, "the newest letter isn't in sight");
    }

    // A folder of pictures where one can't be read: in its turn the tile showed "No pictures in this folder.".
    [Fact]
    public void A_picture_that_cant_be_read_is_passed_over()
    {
        Directory.CreateDirectory(_dir);
        using (var photo = new Bitmap(80, 60))
        {
            using (var g = Graphics.FromImage(photo)) g.Clear(Color.FromArgb(46, 139, 87));
            photo.Save(Path.Combine(_dir, "a.png"), ImageFormat.Png);
        }
        File.WriteAllText(Path.Combine(_dir, "b.png"), "not a picture");
        File.WriteAllBytes(Path.Combine(_dir, "c.jpg"), []);
        var tile = new WidgetTile { Kind = WidgetTiles.Photos, W = 6, H = 4, Folder = _dir, Every = 10 };
        var cfg = Box(tile);
        foreach (int second in new[] { 0, 10, 20 }) // a.png, b.png and c.jpg in their turns
        {
            WidgetRenderer.Now = () => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + second).LocalDateTime;
            using var bmp = WidgetRenderer.Render(cfg, null, 1, false, out _);
            Keep(bmp, $"photos-unreadable-{second}");
            var middle = bmp.GetPixel(bmp.Width / 2, bmp.Height / 3);
            Assert.True(middle.G > 120 && middle.R < 80, $"at {second}s the middle of the tile is {middle}, not the picture");
        }
    }

    // Square corners: a large media tile's blurred cover reached into its corners, not a rounded card with the panel
    // showing at each corner.
    [Fact]
    public void Square_corners_are_square_to_the_corner_of_a_large_media_tile()
    {
        using var cover = new Bitmap(60, 60);
        using (var g = Graphics.FromImage(cover)) g.Clear(Color.FromArgb(124, 92, 255));
        var tile = new WidgetTile { Kind = WidgetTiles.Media, W = 8, H = 7 };
        var cfg = Box(tile);
        (cfg.Corners, cfg.TileBoxes, cfg.Gap, cfg.Theme) = (WidgetCorners.Square, true, WidgetGap.Normal, WidgetTheme.Dark);
        var data = new WidgetData { Media = new MediaNow("Night Drive", "Example Artist", true, cover, true, true, 1) };
        using var bmp = WidgetRenderer.Render(cfg, data, 2, false, out _);
        Keep(bmp, "media-square-corners");
        var r = TileRect(tile, cfg);
        var corner = bmp.GetPixel((int)((r.Left + 2) * 2), (int)((r.Top + 2) * 2));
        Assert.True(corner.R > 40 && corner.B > 60, $"the tile's corner is {corner}, the panel's colour");
    }

    [Fact]
    public void Looks_to_check_by_eye()
    {
        // A long item typed into a to-do.
        var todo = Box(new WidgetTile { Kind = WidgetTiles.Todo, W = 5, H = 3 });
        string typed = "Back up the E drive before the BIOS update on Saturday night";
        WidgetRenderer.Typing = new WidgetRenderer.TypingNow(todo.Id, 0, typed, typed.Length, Adding: true);
        using (var bmp = WidgetRenderer.Render(todo, null, 2, false, out _)) Keep(bmp, "look-todo-typing-long");
        WidgetRenderer.Typing = null;
        // Square corners: a large media tile with a cover, and a photo.
        Directory.CreateDirectory(_dir);
        using (var photo = new Bitmap(80, 60))
        {
            using (var g = Graphics.FromImage(photo)) g.Clear(Color.SeaGreen);
            photo.Save(Path.Combine(_dir, "a.png"), ImageFormat.Png);
        }
        File.WriteAllText(Path.Combine(_dir, "b.png"), "not a picture");
        using var cover = new Bitmap(60, 60);
        using (var g = Graphics.FromImage(cover)) g.Clear(Color.FromArgb(124, 92, 255));
        var square = Box(new WidgetTile { Kind = WidgetTiles.Media, W = 8, H = 7 }, new WidgetTile { Kind = WidgetTiles.Photos, X = 8, W = 6, H = 4, Folder = _dir, Every = 10 });
        (square.Corners, square.TileBoxes, square.Gap) = (WidgetCorners.Square, true, WidgetGap.Normal);
        var data = new WidgetData { Media = new MediaNow("Night Drive", "Example Artist", true, cover, true, true, 1, "Spotify", TimeSpan.FromSeconds(84), TimeSpan.FromSeconds(222), WidgetRenderer.Now()) };
        using (var bmp = WidgetRenderer.Render(square, data, 2, false, out _)) Keep(bmp, "look-square-corners");
        // The photo that can't be read, in its turn.
        WidgetRenderer.Now = () => new DateTime(2026, 11, 11, 9, 14, 41);
        using (var bmp = WidgetRenderer.Render(square, data, 2, false, out _)) Keep(bmp, "look-square-corners-unreadable");
    }

    // A forecast kept from earlier (no internet since, the PC asleep): the hours gone by and a day that's become today
    // aren't shown as ahead. Drawn the same as a forecast holding only what's still ahead.
    [Theory]
    [InlineData(8, 3, 15)]
    [InlineData(8, 7, 15)]
    [InlineData(8, 7, 24 + 1)]
    public void A_kept_forecast_shows_only_whats_ahead(int w, int h, int hoursLater)
    {
        var fetched = new DateTime(2026, 11, 11, 9, 0, 0);
        WidgetRenderer.Now = () => fetched.AddHours(hoursLater).AddMinutes(30);
        var now = WidgetRenderer.Now();
        var hours = Enumerable.Range(0, 5).Select(i => new WeatherHour(fetched.AddHours(1 + i * 2), 20 + i, i % 2 == 0 ? "rain" : "clearsky_day")).ToList();
        var days = Enumerable.Range(1, 3).Select(i => new WeatherDay(fetched.Date.AddDays(i), 10 + i, 20 + i, i % 2 == 0 ? "fog" : "snow")).ToList();
        var kept = new WeatherNow(21, "rain", 25, 15, fetched) { Hours = hours, Days = days };
        var ahead = kept with { Hours = [.. hours.Where(x => x.At > now)], Days = [.. days.Where(x => x.Date > now.Date)] };
        var tile = new WidgetTile { Kind = WidgetTiles.Weather, W = w, H = h, Place = "Pune", Lat = 18.52, Lon = 73.85 };
        var cfg = Box(tile);
        byte[] Draw(WeatherNow weather, string name)
        {
            WeatherFeed.Latest[WeatherFeed.Key(18.52, 73.85)] = weather;
            using var bmp = WidgetRenderer.Render(cfg, null, 1.5f, false, out _);
            Keep(bmp, name);
            return Bytes(bmp);
        }
        Assert.Equal(Draw(ahead, $"weather-ahead-{w}x{h}-{hoursLater}"), Draw(kept, $"weather-kept-{w}x{h}-{hoursLater}"));
    }

    // A shortcut to a place on another PC that can't be reached (a NAS that's off): looking whether it was a folder or
    // a file held the drawing for about 21 s (measured, 2026-11-11), the agent's widgets and the app's page with it.
    // 192.0.2.x is kept for documentation: nothing answers there, as with a PC that's off.
    [Fact]
    public void A_shortcut_to_a_place_that_cant_be_reached_draws_at_once()
    {
        var cfg = Box(new WidgetTile
        {
            Kind = WidgetTiles.Shortcuts, W = 6, H = 2,
            Links = [new() { Path = $@"\\192.0.2.{Random.Shared.Next(10, 250)}\share\app.exe", Name = "App" }, new() { Path = @"\\192.0.2.7\share\Games", Name = "Games" }],
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var bmp = WidgetRenderer.Render(cfg, null, 1.5f, false, out _);
        Keep(bmp, "shortcuts-remote");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms to draw");
        Assert.True(WidgetFiles.IsRemote(@"\\nas\share\x.exe"));
        Assert.True(WidgetFiles.IsRemote(@"\\?\UNC\nas\share\x.exe"));
        Assert.False(WidgetFiles.IsRemote(@"\\?\C:\Windows\notepad.exe"));
        Assert.False(WidgetFiles.IsRemote(Environment.ProcessPath!));
        Assert.True(WidgetFiles.IsFolder(new TileLink { Path = @"\\nas\share\Games" }));
        Assert.False(WidgetFiles.IsFolder(new TileLink { Path = @"\\nas\share\app.exe" }));
        Assert.True(WidgetFiles.IsFolder(new TileLink { Path = Path.GetTempPath() }));
    }

    // A photos tile on a folder of another PC that can't be reached: reading its list held the drawing for about 21 s,
    // again every minute (when the list is read again).
    [Fact]
    public void A_photos_folder_that_cant_be_reached_draws_at_once()
    {
        string folder = $@"\\192.0.2.{Random.Shared.Next(10, 250)}\share\Pictures";
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Photos, W = 6, H = 4, Folder = folder });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? key = WidgetRenderer.ChangeKey(cfg, null);
        using (var bmp = WidgetRenderer.Render(cfg, null, 1.5f, false, out _)) Keep(bmp, "photos-remote-waiting");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms to draw");
        // When the answer comes (here: none, after Windows gives up), the tile is drawn again and says so.
        Assert.True(WidgetFiles.IsWaiting(folder));
        while (WidgetFiles.IsWaiting(folder) && sw.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(200);
        Assert.NotEqual(key, WidgetRenderer.ChangeKey(cfg, null));
        using (var bmp = WidgetRenderer.Render(cfg, null, 1.5f, false, out _)) Keep(bmp, "photos-remote-none");
    }

    // ── Words Windows' region writes ─────────────────────────────────────────

    // A calendar's heading in a Spanish region: "Noviembre de 2026", not every word a capital.
    [Theory]
    [InlineData("en-US", "November 2026")]
    [InlineData("es-ES", "Noviembre de 2026")]
    [InlineData("fr-FR", "Novembre 2026")]
    public void A_calendars_heading_has_only_its_first_letter_a_capital(string culture, string expected)
    {
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        Assert.Equal(expected, WidgetRenderer.MonthTitle(new DateTime(2026, 11, 1)));
    }

    // The forecast's days in a Spanish region: "Viernes" under "Mañana", as the clock writes a day, not "viernes"; and
    // "Tomorrow" counted from the clock the tiles show.
    [Fact]
    public void The_forecasts_days_are_named_as_the_clock_names_them()
    {
        CultureInfo.CurrentCulture = new CultureInfo("es-ES");
        WidgetRenderer.Now = () => new DateTime(2026, 11, 11, 23, 30, 0);
        Assert.Equal("Tomorrow", WidgetRenderer.WeatherDayName(new DateTime(2026, 11, 12)));
        Assert.Equal("Viernes", WidgetRenderer.WeatherDayName(new DateTime(2026, 11, 13)));
        WidgetRenderer.Now = () => new DateTime(2026, 11, 12, 0, 30, 0);
        Assert.Equal("Jueves", WidgetRenderer.WeatherDayName(new DateTime(2026, 11, 12)));
    }

    // ── Media in a narrow, taller tile ───────────────────────────────────────

    // Four cells wide and three high: the medium layout, whose buttons left no room for the title at all.
    [Theory]
    [InlineData(4, 3)]
    [InlineData(4, 7)]
    public void A_narrow_media_tile_still_says_whats_playing(int w, int h)
    {
        using var cover = new Bitmap(60, 60);
        using (var g = Graphics.FromImage(cover)) g.Clear(Color.FromArgb(124, 92, 255));
        var cfg = Box(new WidgetTile { Kind = WidgetTiles.Media, W = w, H = h });
        var at = WidgetRenderer.Now();
        Bitmap Draw(string title) => WidgetRenderer.Render(cfg, new WidgetData { Media = new MediaNow(title, "Example Artist", true, cover, true, true, 1, "Spotify", TimeSpan.FromSeconds(84), TimeSpan.FromSeconds(222), at) }, 2, hover: false, out _);
        using var a = Draw("Night Drive");
        using var b = Draw("Morning Run");
        Keep(a, $"media-narrow-{w}x{h}");
        Assert.NotEqual(Bytes(a), Bytes(b));
    }
}
