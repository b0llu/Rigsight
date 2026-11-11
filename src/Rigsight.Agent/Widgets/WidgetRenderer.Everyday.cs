using System.Drawing;
using System.IO;
using System.Drawing.Drawing2D;
using Rigsight.Core;
using Rigsight.Core.Settings;

#if RIGSIGHT_APP
// Linked into the app, which draws widgets with the same code for its Widgets page (see Rigsight.csproj).
namespace Rigsight.TaskbarDrawing;
#else
namespace Rigsight.Agent.Widgets;
#endif

/// <summary>What's playing on the PC (the Media tile): from Windows' media controls, in the agent only.</summary>
internal sealed record MediaNow(string Title, string Artist, bool Playing, Bitmap? Cover, bool CanPrevious, bool CanNext, int Version);

// Everyday tiles, part three (approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U): media, weather, shortcuts, photos.
internal static partial class WidgetRenderer
{
    internal const string HitPrevious = "previous", HitPlay = "play", HitNext = "next", HitOpen = "open";

    /// <summary>A round button with an icon; filled with the accent for the main one.</summary>
    private static void RoundButton(Graphics g, RectangleF b, string glyph, Palette p, Color accent, bool main)
    {
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(main ? accent : p.Track)) g.FillEllipse(fill, b);
        g.SmoothingMode = smooth;
        IconIn(g, glyph, b.Width * 0.4f, main ? OnAccent(accent) : p.Muted, b);
    }

    /// <summary>A picture filling a box with rounded corners, cut to fit (nothing squashed).</summary>
    private static void Cover(Graphics g, Image image, RectangleF box, float radius)
    {
        float scale = Math.Max(box.Width / image.Width, box.Height / image.Height);
        float w = image.Width * scale, h = image.Height * scale;
        using var clip = RoundRect(box, radius);
        var state = g.Save();
        g.SetClip(clip, CombineMode.Intersect);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        g.Restore(state);
    }

    /// <summary>A square with an icon in it, for something with no picture of its own.</summary>
    private static void GlyphSquare(Graphics g, RectangleF box, string glyph, Palette p, float radius)
    {
        using (var path = RoundRect(box, radius))
        using (var fill = new SolidBrush(p.Track))
            g.FillPath(fill, path);
        IconIn(g, glyph, box.Height * 0.45f, p.Muted, box);
    }

    /// <summary>What's playing: its cover, title and artist, and previous, play or pause, and next (clicked on the desktop).</summary>
    private static void MediaTile(Graphics g, RectangleF r, WidgetData d, Palette p, Color accent)
    {
        float s = MathF.Round(Math.Min(r.Height, 56));
        var art = new RectangleF(r.Left, r.Top + (r.Height - s) / 2, s, s);
        var m = d.Media;
        if (m?.Cover is { } cover) Cover(g, cover, art, 8);
        else GlyphSquare(g, art, "\uE8D6", p, 8);
        float bs = MathF.Round(Math.Clamp(r.Height * 0.6f, 22, 30)), gap = 6, cy = r.Top + r.Height / 2;
        float right = r.Right;
        if (m is not null)
        {
            var next = new RectangleF(right - bs, cy - bs / 2, bs, bs);
            var play = next with { X = next.X - bs - gap };
            RoundButton(g, next, "\uE893", p, accent, false);
            AddHit(HitNext, 0, next);
            RoundButton(g, play, m.Playing ? "\uE769" : "\uE768", p, accent, true);
            AddHit(HitPlay, 0, play);
            right = play.X - gap;
            if (r.Width >= 250)
            {
                var prev = play with { X = play.X - bs - gap };
                RoundButton(g, prev, "\uE892", p, accent, false);
                AddHit(HitPrevious, 0, prev);
                right = prev.X - gap;
            }
        }
        float tx = art.Right + 10, tw = right - 4 - tx;
        if (tw < 20) return;
        if (m is null)
        {
            Text(g, Lang.T("Nothing playing"), 13, FontStyle.Regular, p.Faint, tx, cy - 9, maxWidth: tw);
            return;
        }
        bool two = m.Artist.Length > 0 && r.Height >= 40;
        Text(g, m.Title.Length > 0 ? m.Title : Lang.C("media", "Playing"), 13, FontStyle.Regular, p.Text, tx, two ? cy - 18 : cy - 9, semibold: true, maxWidth: tw);
        if (two) Text(g, m.Artist, 12, FontStyle.Regular, p.Muted, tx, cy + 1, maxWidth: tw);
    }

    /// <summary>The weather at the tile's place: its icon, the temperature, and the place, the sky and today's range where there's room.</summary>
    private static void WeatherTile(Graphics g, WidgetTile t, RectangleF r, Palette p, Color accent)
    {
        if (t.Place is not { } place || t.Lat is null)
        {
            Caption(g, Lang.T("WEATHER"), r.Left, r.Top, p.Faint);
            TextIn(g, Lang.T("Pick a place in the editor."), 12.5f, p.Faint, RectangleF.FromLTRB(r.Left, r.Top + 16, r.Right, r.Bottom));
            return;
        }
        var w = WeatherFeed.For(t);
        float icon = MathF.Round(Math.Clamp(r.Height - 8, 22, 40));
        float cy = r.Top + r.Height / 2;
        WeatherIcon(g, w?.Symbol, new RectangleF(r.Left, cy - icon / 2, icon, icon), w is null ? p.Faint : p.Text, w is null ? p.Faint : accent);
        float x = r.Left + icon + 10;
        string temp = w is null ? "–" : Units.TempShort(w.TempC);
        float big = r.Height >= 56 ? 28 : 22;
        Text(g, temp, big, FontStyle.Regular, p.Text, x, cy - big * 0.68f);
        x += Measure(g, temp, big, FontStyle.Regular) + 12;
        float tw = r.Right - x;
        if (tw < 36) return;
        var lines = new List<(string Text, float Px, Color Color, bool Bold)> { (place, 12.5f, p.Text, true) };
        if (w is not null && WeatherFeed.Words(w.Symbol) is { Length: > 0 } words) lines.Add((words, 12, p.Muted, false));
        if (w is { HighC: double hi, LowC: double lo }) lines.Add((Lang.T($"{Units.TempShort(lo)} to {Units.TempShort(hi)} today"), 11.5f, p.Faint, false));
        int fit = Math.Max(1, Math.Min(lines.Count, (int)(r.Height / 17)));
        float y = cy - fit * 17 / 2f;
        foreach (var l in lines.Take(fit))
        {
            Text(g, l.Text, l.Px, FontStyle.Regular, l.Color, x, y, semibold: l.Bold, maxWidth: tw);
            y += 17;
        }
    }

    /// <summary>
    /// The sky as an icon, drawn here (Windows' icon font has no weather icons): the sun or the moon in the accent, a
    /// cloud in the text colour, and rain, snow, fog or a bolt under it.
    /// </summary>
    private static void WeatherIcon(Graphics g, string? symbol, RectangleF box, Color main, Color sun)
    {
        var (sky, night) = symbol is null ? (WeatherFeed.Sky.Cloudy, false) : WeatherFeed.SkyOf(symbol);
        float s = box.Height;
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var line = new Pen(main, Math.Max(1.4f, s / 16f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var sunPen = new Pen(sun, Math.Max(1.4f, s / 16f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        void SunOrMoon(RectangleF b)
        {
            if (night)
            {
                // A crescent: the disc less a second one moved up and right.
                using var disc = new GraphicsPath();
                disc.AddEllipse(RectangleF.Inflate(b, -b.Width * 0.12f, -b.Height * 0.12f));
                using var bite = new GraphicsPath();
                var cut = RectangleF.Inflate(b, -b.Width * 0.12f, -b.Height * 0.12f);
                cut.Offset(b.Width * 0.3f, -b.Height * 0.22f);
                bite.AddEllipse(cut);
                using var moon = new Region(disc);
                moon.Exclude(bite);
                using var fill = new SolidBrush(sun);
                g.FillRegion(fill, moon);
                return;
            }
            float c = b.Width / 2, x = b.X + c, y = b.Y + c, core = b.Width * 0.24f;
            using (var fill = new SolidBrush(sun)) g.FillEllipse(fill, x - core, y - core, core * 2, core * 2);
            for (int i = 0; i < 8; i++)
            {
                double a = Math.PI / 4 * i;
                float r1 = b.Width * 0.36f, r2 = b.Width * 0.48f;
                g.DrawLine(sunPen, x + (float)Math.Cos(a) * r1, y + (float)Math.Sin(a) * r1, x + (float)Math.Cos(a) * r2, y + (float)Math.Sin(a) * r2);
            }
        }

        void Cloud(RectangleF b)
        {
            using var path = new GraphicsPath(FillMode.Winding); // overlapping parts fill as one
            path.AddEllipse(b.X, b.Y + b.Height * 0.38f, b.Width * 0.46f, b.Height * 0.56f);
            path.AddEllipse(b.X + b.Width * 0.2f, b.Y + b.Height * 0.06f, b.Width * 0.56f, b.Height * 0.74f);
            path.AddEllipse(b.X + b.Width * 0.5f, b.Y + b.Height * 0.32f, b.Width * 0.5f, b.Height * 0.62f);
            path.AddRectangle(new RectangleF(b.X + b.Width * 0.22f, b.Y + b.Height * 0.6f, b.Width * 0.56f, b.Height * 0.34f));
            using var region = new Region(path);
            using var fill = new SolidBrush(main);
            g.FillRegion(fill, region);
        }

        var whole = new RectangleF(box.X, box.Y + s * 0.12f, s, s * 0.62f);
        switch (sky)
        {
            case WeatherFeed.Sky.Clear:
                SunOrMoon(box);
                break;
            case WeatherFeed.Sky.Partly:
                SunOrMoon(new RectangleF(box.X + s * 0.36f, box.Y, s * 0.62f, s * 0.62f));
                Cloud(new RectangleF(box.X, box.Y + s * 0.34f, s * 0.78f, s * 0.5f));
                break;
            case WeatherFeed.Sky.Cloudy:
                Cloud(whole);
                break;
            case WeatherFeed.Sky.Fog:
                for (int i = 0; i < 3; i++)
                {
                    float y = box.Y + s * (0.3f + i * 0.2f), inset = i == 1 ? 0.06f : 0.14f;
                    g.DrawLine(line, box.X + s * inset, y, box.Right - s * (0.2f - inset), y);
                }
                break;
            default:
                var top = new RectangleF(box.X, box.Y + s * 0.04f, s, s * 0.56f);
                Cloud(top);
                float y0 = top.Bottom + s * 0.08f;
                if (sky == WeatherFeed.Sky.Rain)
                    for (int i = 0; i < 3; i++)
                    {
                        float x = box.X + s * (0.28f + i * 0.2f);
                        g.DrawLine(sunPen, x, y0, x - s * 0.07f, y0 + s * 0.2f);
                    }
                else if (sky == WeatherFeed.Sky.Snow)
                    using (var dot = new SolidBrush(sun))
                        for (int i = 0; i < 3; i++)
                        {
                            float d = s * 0.1f, x = box.X + s * (0.26f + i * 0.2f), y = y0 + (i % 2) * s * 0.1f;
                            g.FillEllipse(dot, x, y, d, d);
                        }
                else
                {
                    using var bolt = new GraphicsPath();
                    float bx = box.X + s * 0.48f;
                    bolt.AddPolygon([new PointF(bx, y0 - s * 0.06f), new PointF(bx - s * 0.14f, y0 + s * 0.16f), new PointF(bx, y0 + s * 0.14f),
                        new PointF(bx - s * 0.08f, y0 + s * 0.34f), new PointF(bx + s * 0.14f, y0 + s * 0.08f), new PointF(bx, y0 + s * 0.1f)]);
                    using var fill = new SolidBrush(sun);
                    g.FillPath(fill, bolt);
                }
                break;
        }
        g.SmoothingMode = smooth;
    }

    /// <summary>A shortcut's icon: the app's own, or a folder's or a web page's.</summary>
    private static string LinkGlyph(TileLink l) => l.IsWeb ? "\uE774" : Directory.Exists(l.Path) ? "\uE8B7" : "\uE8A5";

    /// <summary>Apps, files, folders and links in a row, each opened with a click on the desktop; names under them where there's room.</summary>
    private static void ShortcutsTile(Graphics g, WidgetTile t, RectangleF r, Palette p)
    {
        var links = t.Links ?? [];
        if (links.Count == 0)
        {
            TextIn(g, Lang.T("Add apps, folders and links in the editor."), 12.5f, p.Faint, r);
            return;
        }
        bool names = r.Height >= 58;
        float s = MathF.Round(Math.Clamp(names ? r.Height - 18 : r.Height, 18, 44)), gap = names ? 14 : 10;
        int n = Math.Clamp((int)((r.Width + gap) / (s + gap)), 1, links.Count);
        float width = n * s + (n - 1) * gap, x = r.Left + (r.Width - width) / 2, y = r.Top + (r.Height - s - (names ? 18 : 0)) / 2;
        for (int i = 0; i < n; i++)
        {
            var l = links[i];
            var box = new RectangleF(x, y, s, s);
            using (var path = RoundRect(box, s * 0.24f))
            using (var fill = new SolidBrush(p.Track))
                g.FillPath(fill, path);
            if (WidgetFiles.IconOf(l) is { } icon)
            {
                float iz = MathF.Round(s * 0.66f);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, box.X + (s - iz) / 2, box.Y + (s - iz) / 2, iz, iz);
            }
            else IconIn(g, LinkGlyph(l), s * 0.42f, p.Muted, box);
            if (names)
            {
                float nw = s + gap - 4;
                string name = l.Name;
                float tw = Math.Min(Measure(g, name, 10.5f, FontStyle.Regular), nw);
                Text(g, name, 10.5f, FontStyle.Regular, p.Muted, box.X + (s - tw) / 2, box.Bottom + 4, maxWidth: nw);
            }
            AddHit(HitOpen, i, RectangleF.Inflate(box, gap / 2, names ? 0 : gap / 4) with { Height = s + (names ? 18 : 0) });
            x += s + gap;
        }
    }

    /// <summary>The folder's pictures, one after another, each filling the tile.</summary>
    private static void PhotosTile(Graphics g, WidgetTile t, RectangleF tile, Palette p)
    {
        var r = RectangleF.Inflate(tile, -4, -4);
        string? now = WidgetFiles.PictureNow(t, Now());
        if (now is not null && WidgetFiles.Picture(now) is { } picture)
        {
            Cover(g, picture, r, 9);
            return;
        }
        var inner = RectangleF.Inflate(tile, -10, -8);
        float icon = Math.Min(28, inner.Height - 24);
        if (icon >= 14) IconIn(g, "\uE91B", icon, p.Faint, new RectangleF(inner.Left, inner.Top, icon, icon));
        string line = t.Folder is null ? Lang.T("Pick a folder in the editor.") : Lang.T("No pictures in this folder.");
        TextIn(g, line, 12.5f, p.Faint, RectangleF.FromLTRB(inner.Left, inner.Top + (icon >= 14 ? icon + 6 : 0), inner.Right, inner.Bottom));
    }

    /// <summary>The part of a box's change key for the everyday tiles of part three (null: not one of them).</summary>
    private static string? EverydayKey(WidgetTile t, WidgetData d) => t.Kind switch
    {
        WidgetTiles.Media => d.Media is { } m ? $"{m.Title}|{m.Artist}|{m.Playing}|{m.Version}" : "-",
        WidgetTiles.Weather => WeatherFeed.For(t) is { } w ? $"{t.Place}|{w.TempC}|{w.Symbol}|{w.HighC}|{w.LowC}|{Units.Fahrenheit}" : $"{t.Place}|-",
        WidgetTiles.Shortcuts => string.Join("|", (t.Links ?? []).Select(l => l.Path + ">" + l.Name)),
        WidgetTiles.Photos => $"{t.Folder}|{WidgetFiles.PictureNow(t, Now())}",
        _ => null,
    };
}
