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
internal sealed record MediaNow(string Title, string Artist, bool Playing, Bitmap? Cover, bool CanPrevious, bool CanNext, int Version,
    string App = "", TimeSpan Position = default, TimeSpan Length = default, DateTime At = default)
{
    /// <summary>How far into the track it is now: where it was read, and the time since while it plays.</summary>
    public TimeSpan PositionAt(DateTime now)
    {
        var p = Playing && At != default ? Position + (now - At) : Position;
        return Length > TimeSpan.Zero && p > Length ? Length : p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }
}

// Everyday tiles, part three (approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U): media, weather, shortcuts, photos.
internal static partial class WidgetRenderer
{
    internal const string HitPrevious = "previous", HitPlay = "play", HitNext = "next", HitOpen = "open";

    /// <summary>
    /// The clickable place under the pointer and how far its hover and press have gone (0 to 1), for the widget the
    /// agent is drawing (the user, 2026-11-11: the buttons wanted a hover and a press). Set by the window before each
    /// drawing of itself; the app never sets it.
    /// </summary>
    internal readonly record struct PointerOn(string WidgetId, int Tile, string Action, int Index, float Hover, float Press);

    [ThreadStatic] internal static PointerOn? Pointer;

    /// <summary>How far this tile's clickable place is hovered and pressed.</summary>
    private static (float Hover, float Press) StateOf(string action, int index) =>
        Pointer is { } ps && ps.WidgetId == _widgetId && ps.Tile == _tileIndex && ps.Action == action && ps.Index == index ? (ps.Hover, ps.Press) : (0, 0);

    /// <summary>A button pressed in: a little smaller, round its middle.</summary>
    private static RectangleF PressedIn(RectangleF b, float press) => RectangleF.Inflate(b, -b.Width * 0.05f * press, -b.Height * 0.05f * press);

    /// <summary>A colour lifted under the pointer (towards white on dark, black on light) and dimmed a touch while pressed.</summary>
    private static Color Lifted(Color c, float hover, float press, bool light)
    {
        float up = (light ? 0.09f : 0.16f) * hover, down = 0.1f * press;
        int To(int v, int target, float t) => (int)Math.Round(v + (target - v) * t);
        int l = light ? 0 : 255;
        var lifted = Color.FromArgb(c.A, To(c.R, l, up), To(c.G, l, up), To(c.B, l, up));
        return Color.FromArgb(lifted.A, To(lifted.R, 0, down), To(lifted.G, 0, down), To(lifted.B, 0, down));
    }

    /// <summary>A round button with an icon, filled with the accent for the main one; lit under the pointer and pressed in when clicked.</summary>
    private static void RoundButton(Graphics g, RectangleF b, string glyph, Palette p, Color accent, bool main, string? action = null, int index = 0)
    {
        var (hover, press) = action is null ? (0f, 0f) : StateOf(action, index);
        var at = PressedIn(b, press);
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(Lifted(main ? accent : p.Track, hover, press, p.Light && !main || main && Brightness(accent) > 0.6f))) g.FillEllipse(fill, at);
        g.SmoothingMode = smooth;
        IconIn(g, glyph, at.Width * 0.4f, main ? OnAccent(accent) : Lifted(p.Muted, hover, 0, p.Light), at);
    }

    /// <summary>Whether a bitmap is still there to draw (one disposed of says so by throwing on its size).</summary>
    private static bool Alive(Image image)
    {
        try { return image.Width > 0; }
        catch (ArgumentException) { return false; }
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

    /// <summary>A track's time as people read it: 3:42, or 1:02:05 past an hour.</summary>
    private static string TrackTime(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    /// <summary>A thin bar with how far it is, filled in the accent (or white over a cover).</summary>
    private static void ProgressBar(Graphics g, RectangleF bar, float fraction, Color track, Color fill)
    {
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundRect(bar, bar.Height / 2))
        using (var b = new SolidBrush(track))
            g.FillPath(b, path);
        float w = Math.Clamp(fraction, 0, 1) * bar.Width;
        if (w >= bar.Height)
        {
            using var path = RoundRect(bar with { Width = w }, bar.Height / 2);
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
        }
        g.SmoothingMode = smooth;
    }

    /// <summary>The time played, the bar and the length, in a row; nothing when the player doesn't say its length.</summary>
    private static void TimeRow(Graphics g, MediaNow m, RectangleF row, Color text, Color track, Color fill)
    {
        if (m.Length <= TimeSpan.Zero) return;
        var now = m.PositionAt(Now());
        string a = TrackTime(now), b = TrackTime(m.Length);
        float aw = Measure(g, a, 10.5f, FontStyle.Regular), bw = Measure(g, b, 10.5f, FontStyle.Regular);
        float cy = row.Y + row.Height / 2;
        Text(g, a, 10.5f, FontStyle.Regular, text, row.Left, TextTopFor(cy, 10.5f, FontStyle.Regular));
        Text(g, b, 10.5f, FontStyle.Regular, text, row.Right - bw, TextTopFor(cy, 10.5f, FontStyle.Regular));
        var bar = RectangleF.FromLTRB(row.Left + aw + 8, cy - 2, row.Right - bw - 8, cy + 2);
        if (bar.Width > 20) ProgressBar(g, bar, (float)(now.TotalMilliseconds / m.Length.TotalMilliseconds), track, fill);
    }

    /// <summary>Back, play or pause, and next, side by side from <paramref name="x"/> (the main one bigger), each clicked on the desktop.</summary>
    private static float Controls(Graphics g, float x, float cy, float size, bool previous, MediaNow m, Palette p, Color accent, Color? on = null)
    {
        float gap = MathF.Round(size * 0.22f), big = MathF.Round(size * 1.18f);
        if (previous)
        {
            var prev = new RectangleF(x, cy - size / 2, size, size);
            RoundButtonOn(g, prev, "\uE892", p, accent, false, on, HitPrevious);
            AddHit(HitPrevious, 0, prev);
            x = prev.Right + gap;
        }
        var play = new RectangleF(x, cy - big / 2, big, big);
        RoundButtonOn(g, play, m.Playing ? "\uE769" : "\uE768", p, accent, true, on, HitPlay);
        AddHit(HitPlay, 0, play);
        var next = new RectangleF(play.Right + gap, cy - size / 2, size, size);
        RoundButtonOn(g, next, "\uE893", p, accent, false, on, HitNext);
        AddHit(HitNext, 0, next);
        return next.Right;
    }

    /// <summary>The width <see cref="Controls"/> takes.</summary>
    private static float ControlsWidth(float size, bool previous) => MathF.Round(size * 1.18f) + size + MathF.Round(size * 0.22f) * (previous ? 2 : 1) + (previous ? size : 0);

    // Over a cover the side buttons are see-through white, not the panel's grey.
    private static void RoundButtonOn(Graphics g, RectangleF b, string glyph, Palette p, Color accent, bool main, Color? on, string action)
    {
        if (on is not { } over || main)
        {
            RoundButton(g, b, glyph, p, main && on is not null ? Color.White : accent, main, action);
            return;
        }
        var (hover, press) = StateOf(action, 0);
        var at = PressedIn(b, press);
        var smooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(Color.FromArgb((int)(46 + 40 * hover - 20 * press), 255, 255, 255))) g.FillEllipse(fill, at);
        g.SmoothingMode = smooth;
        IconIn(g, glyph, at.Width * 0.4f, over, at);
    }

    /// <summary>
    /// What's playing, laid out by the tile's size (the user, 2026-11-11, from what media widgets do): small, the cover, the
    /// title, a bar and Pause; a row, the cover, title and artist, and the buttons; medium, that and the time played with
    /// the length; large, the cover blurred behind it all, a big cover, the title, artist and app, the time and the buttons.
    /// </summary>
    private static void MediaTile(Graphics g, RectangleF tile, RectangleF r, WidgetData d, Palette p, Color accent)
    {
        var m = d.Media;
        // A narrow tile is small however tall it is: the medium card's buttons leave a narrow one no room for the title.
        bool large = r.Height >= 150 && r.Width >= 220, small = !large && r.Width < 200, medium = !large && !small && r.Height >= 70;
        if (m is null)
        {
            float s0 = MathF.Round(Math.Min(r.Height, large ? 72 : 52));
            var art0 = new RectangleF(r.Left, r.Top + (r.Height - s0) / 2, s0, s0);
            GlyphSquare(g, art0, "\uE8D6", p, 8);
            float tx0 = art0.Right + 12, tw0 = r.Right - tx0;
            if (tw0 < 30) return;
            bool line = r.Height >= 44 && tw0 >= 120;
            float cy0 = r.Top + r.Height / 2;
            Text(g, Lang.T("Nothing playing"), 13, FontStyle.Regular, p.Muted, tx0, line ? cy0 - 18 : TextTopFor(cy0, 13, FontStyle.Regular), maxWidth: tw0);
            if (line) TextIn(g, Lang.T("Play something in any app with media controls."), 11.5f, p.Faint, RectangleF.FromLTRB(tx0, cy0, r.Right, r.Bottom));
            return;
        }
        // A cover the media watcher has let go of (it keeps the last two; these readings can be older after quick skips):
        // drawn as no cover, where drawing it threw and the picture wasn't made.
        if (m.Cover is { } held && !Alive(held)) m = m with { Cover = null };
        string title = m.Title.Length > 0 ? m.Title : Lang.C("media", "Playing");
        string by = m.Artist.Length > 0 && m.App.Length > 0 ? $"{m.Artist} · {m.App}" : m.Artist.Length > 0 ? m.Artist : m.App;
        if (large)
        {
            // The cover, blurred and darkened, behind everything: drawn tiny, then stretched.
            if (m.Cover is { } cover)
            {
                using var tiny = new Bitmap(10, 10);
                using (var tg = Graphics.FromImage(tiny))
                {
                    tg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    tg.DrawImage(cover, 0, 0, 10, 10);
                }
                var back = RectangleF.Inflate(tile, -1, -1);
                using var clip = RoundRect(back, FillRadius(12));
                var state = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(tiny, RectangleF.Inflate(back, back.Width * 0.15f, back.Height * 0.15f));
                using (var shade = new SolidBrush(Color.FromArgb(120, 10, 10, 10))) g.FillRectangle(shade, back);
                g.Restore(state);
            }
            bool overCover = m.Cover is not null;
            Color text = overCover ? Color.White : p.Text, soft = overCover ? Color.FromArgb(214, 214, 214) : p.Muted;
            float s = MathF.Round(Math.Min(96, r.Height * 0.45f));
            var art = new RectangleF(r.Left + 4, r.Top + 4, s, s);
            if (m.Cover is { } c2) Cover(g, c2, art, 9); else GlyphSquare(g, art, "\uE8D6", p, 9);
            float tx = art.Right + 14, tw = r.Right - tx, ty = art.Top + 4;
            Caption(g, Lang.T("NOW PLAYING"), tx, ty, overCover ? Color.FromArgb(200, 200, 200) : p.Faint);
            Text(g, title, 16, FontStyle.Regular, text, tx, ty + 16, semibold: true, maxWidth: tw);
            if (m.Artist.Length > 0) Text(g, m.Artist, 12.5f, FontStyle.Regular, soft, tx, ty + 40, maxWidth: tw);
            if (m.App.Length > 0) Text(g, m.App, 11.5f, FontStyle.Regular, overCover ? Color.FromArgb(190, 190, 190) : p.Faint, tx, ty + 58, maxWidth: tw);
            float size = 28, cy = r.Bottom - 22;
            Controls(g, r.Left + (r.Width - ControlsWidth(size, true)) / 2, cy, size, true, m, p, accent, overCover ? Color.White : null);
            TimeRow(g, m, new RectangleF(r.Left + 4, cy - 40, r.Width - 8, 14), text, overCover ? Color.FromArgb(64, 255, 255, 255) : p.Track, overCover ? Color.White : accent);
            return;
        }
        float artSize = MathF.Round(Math.Min(medium ? 52 : r.Height, 56));
        float top = medium && m.Length > TimeSpan.Zero ? r.Top + 2 : r.Top + (r.Height - artSize) / 2;
        var cover2 = new RectangleF(r.Left, top, artSize, artSize);
        if (m.Cover is { } c3) Cover(g, c3, cover2, 8); else GlyphSquare(g, cover2, "\uE8D6", p, 8);
        float rowCy = cover2.Y + artSize / 2;
        if (small)
        {
            // The title over a bar, and Pause.
            float bs = MathF.Round(Math.Clamp(artSize * 0.62f, 24, 30));
            var play = new RectangleF(r.Right - bs, rowCy - bs / 2, bs, bs);
            RoundButton(g, play, m.Playing ? "\uE769" : "\uE768", p, accent, true, HitPlay);
            AddHit(HitPlay, 0, play);
            float sx = cover2.Right + 10, sw = play.X - 8 - sx;
            if (sw < 20) return;
            Text(g, title, 12.5f, FontStyle.Regular, p.Text, sx, rowCy - 15, semibold: true, maxWidth: sw);
            if (m.Length > TimeSpan.Zero)
                ProgressBar(g, new RectangleF(sx, rowCy + 6, sw, 3), (float)(m.PositionAt(Now()).TotalMilliseconds / m.Length.TotalMilliseconds), p.Track, accent);
            return;
        }
        float buttons = MathF.Round(Math.Clamp(artSize * 0.52f, 22, 28));
        bool withPrevious = r.Width >= 250;
        float cw = ControlsWidth(buttons, withPrevious);
        Controls(g, r.Right - cw, rowCy, buttons, withPrevious, m, p, accent);
        float x0 = cover2.Right + 12, w0 = r.Right - cw - 10 - x0;
        if (w0 >= 24)
        {
            bool two = by.Length > 0 && artSize >= 40;
            Text(g, title, 13.5f, FontStyle.Regular, p.Text, x0, two ? rowCy - 18 : TextTopFor(rowCy, 13.5f, FontStyle.Regular), semibold: true, maxWidth: w0);
            if (two) Text(g, by, 12, FontStyle.Regular, p.Muted, x0, rowCy + 1, maxWidth: w0);
        }
        if (medium) TimeRow(g, m, RectangleF.FromLTRB(r.Left, cover2.Bottom + 6, r.Right, Math.Min(r.Bottom, cover2.Bottom + 22)), p.Faint, p.Track, accent);
    }

    /// <summary>An hour ahead as the clock is read here: "2 PM", or "14:00".</summary>
    private static string HourText(DateTime at) =>
        Culture.DateTimeFormat.ShortTimePattern.Contains('H') ? at.ToString("HH:mm", Culture) : at.ToString("h tt", Culture);

    /// <summary>
    /// The weather at the tile's place, laid out by the tile's size (the user, 2026-11-11, from what weather widgets do):
    /// small, the sky, the temperature and the place; medium, that with the sky in words and today's range, and the next
    /// hours under it; large, the next hours with their sky, and the next three days with their range.
    /// </summary>
    private static void WeatherTile(Graphics g, WidgetTile t, RectangleF r, Palette p, Color accent)
    {
        if (t.Place is not { } place || t.Lat is null)
        {
            Caption(g, Lang.T("WEATHER"), r.Left, r.Top, p.Faint);
            TextIn(g, Lang.T("Pick a place in the editor."), 12.5f, p.Faint, RectangleF.FromLTRB(r.Left, r.Top + 16, r.Right, r.Bottom));
            return;
        }
        var w = WeatherFeed.For(t);
        bool large = r.Height >= 150 && r.Width >= 220, medium = !large && r.Height >= 60 && r.Width >= 200;
        float headH = large || medium ? Math.Min(48, r.Height) : r.Height;
        float icon = MathF.Round(Math.Clamp(headH - 8, 22, large ? 40 : 36));
        float cy = r.Top + headH / 2;
        WeatherIcon(g, w?.Symbol, new RectangleF(r.Left, cy - icon / 2, icon, icon), w is null ? p.Faint : p.Text, w is null ? p.Faint : accent);
        float x = r.Left + icon + 10;
        string temp = w is null ? "–" : Units.TempShort(w.TempC);
        float big = large ? 32 : medium ? 28 : r.Height >= 56 ? 26 : 22;
        Text(g, temp, big, FontStyle.Regular, p.Text, x, TextTopFor(cy, big, FontStyle.Regular));
        x += Measure(g, temp, big, FontStyle.Regular) + 12;
        float tw = r.Right - x;
        if (tw >= 36)
        {
            var lines = new List<(string Text, float Px, Color Color, bool Bold)> { (place, 12.5f, p.Text, true) };
            string words = w is null ? "" : WeatherFeed.Words(w.Symbol);
            string range = w is { HighC: double hi, LowC: double lo } ? Lang.T($"{Units.TempShort(lo)} to {Units.TempShort(hi)}") : "";
            string second = medium && words.Length > 0 && range.Length > 0 ? $"{words} · {range}" : words.Length > 0 ? words : range;
            // A small tile says the place only: the sky is its icon.
            if (second.Length > 0 && (medium || large || tw >= 130)) lines.Add((second, 12, p.Muted, false));
            if (!medium && !large && tw >= 130 && range.Length > 0 && words.Length > 0) lines.Add((range, 11.5f, p.Faint, false));
            int fit = Math.Max(1, Math.Min(lines.Count, (int)(headH / 17)));
            float y = cy - fit * 17 / 2f;
            foreach (var l in lines.Take(fit))
            {
                Text(g, l.Text, l.Px, FontStyle.Regular, l.Color, x, y, semibold: l.Bold, maxWidth: tw);
                y += 17;
            }
        }
        if (w is null || (!medium && !large)) return;
        float top = r.Top + headH + 8;
        // Only what's still ahead: a forecast kept from earlier (no internet since, the PC asleep) had hours already
        // gone, and after midnight a day that's today.
        var clock = Now();
        var hours = w.Hours.Where(h => h.At > clock).ToList();
        var days = w.Days.Where(d => d.Date.Date > clock.Date).ToList();
        if (medium)
        {
            // The next hours in a row: "2 PM 26°", as many as fit.
            if (hours.Count == 0 || r.Bottom - top < 12) return;
            var cells = hours.Select(h => $"{HourText(h.At)} {Units.TempShort(h.TempC)}").ToList();
            int n = cells.Count;
            while (n > 1 && cells.Take(n).Sum(c => Measure(g, c, 11, FontStyle.Regular)) + (n - 1) * 10 > r.Width) n--;
            float cw = r.Width / n;
            for (int i = 0; i < n; i++)
            {
                float mw = Measure(g, cells[i], 11, FontStyle.Regular);
                float cx = n == 1 ? r.Left : r.Left + i * (r.Width - mw) / (n - 1);
                Text(g, cells[i], 11, FontStyle.Regular, p.Muted, cx, top);
            }
            return;
        }
        // Large: the next hours with their sky, then the days.
        if (hours.Count > 0)
        {
            int n = Math.Min(hours.Count, Math.Max(1, (int)(r.Width / 46)));
            float cw = r.Width / n;
            for (int i = 0; i < n; i++)
            {
                var h = hours[i];
                float cx = r.Left + i * cw + cw / 2;
                string label = HourText(h.At), tt = Units.TempShort(h.TempC);
                Text(g, label, 10.5f, FontStyle.Regular, p.Faint, cx - Measure(g, label, 10.5f, FontStyle.Regular) / 2, top);
                WeatherIcon(g, h.Symbol, new RectangleF(cx - 9, top + 16, 18, 18), p.Text, accent);
                Text(g, tt, 11.5f, FontStyle.Regular, p.Text, cx - Measure(g, tt, 11.5f, FontStyle.Regular) / 2, top + 37);
            }
            top += 58;
        }
        if (days.Count == 0 || r.Bottom - top < 30) return;
        using (var line = new Pen(p.Track, 1)) g.DrawLine(line, r.Left, top, r.Right, top);
        top += 8;
        foreach (var day in days)
        {
            if (top + 16 > r.Bottom) break;
            string name = WeatherDayName(day.Date);
            string span = Lang.T($"{Units.TempShort(day.LowC)} to {Units.TempShort(day.HighC)}");
            float rowCy = top + 8;
            Text(g, name, 11.5f, FontStyle.Regular, p.Muted, r.Left, TextTopFor(rowCy, 11.5f, FontStyle.Regular), maxWidth: r.Width * 0.4f);
            WeatherIcon(g, day.Symbol, new RectangleF(r.Left + r.Width * 0.42f, rowCy - 8, 16, 16), p.Text, accent);
            Text(g, span, 11.5f, FontStyle.Regular, p.Text, r.Right - Measure(g, span, 11.5f, FontStyle.Regular), TextTopFor(rowCy, 11.5f, FontStyle.Regular));
            top += 19;
        }
    }

    /// <summary>
    /// A day of the forecast: "Tomorrow", else its name with a capital as the clock writes it ("Viernes" in a Spanish
    /// region, where Windows writes "viernes"), counted from the clock the tiles show.
    /// </summary>
    internal static string WeatherDayName(DateTime day) => day.Date == Now().Date.AddDays(1) ? Lang.T("Tomorrow") : DayName(day);

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
    private static string LinkGlyph(TileLink l) => l.IsWeb ? "\uE774" : WidgetFiles.IsFolder(l) ?"\uE8B7" : "\uE8A5";

    /// <summary>
    /// Apps, files, folders and links, each opened with a click on the desktop: a row in a short tile, a grid with names
    /// under them once there's room (the user, 2026-11-11).
    /// </summary>
    private static void ShortcutsTile(Graphics g, WidgetTile t, RectangleF r, Palette p)
    {
        var links = t.Links ?? [];
        if (links.Count == 0)
        {
            TextIn(g, Lang.T("Add apps, folders and links in the editor."), 12.5f, p.Faint, r);
            return;
        }
        bool names = r.Height >= 58;
        const float name = 18;
        float gap = names ? 12 : 10;
        // One row in a short tile, the icons as big as it allows; in a taller one, 40 px icons in as many rows as fit.
        float s = MathF.Round(Math.Clamp(names ? Math.Min(r.Height - name, 44) : r.Height, 18, 44));
        int rows = Math.Max(1, (int)((r.Height + gap) / (s + (names ? name : 0) + gap)));
        if (rows > 1) s = 40;
        int cols = Math.Max(1, (int)((r.Width + gap) / (s + gap)));
        rows = Math.Max(1, Math.Min(rows, (int)((r.Height + gap) / (s + (names ? name : 0) + gap))));
        int n = Math.Min(links.Count, cols * rows);
        int usedCols = Math.Min(cols, n), usedRows = (n + cols - 1) / cols;
        float cellH = s + (names ? name : 0);
        float left = r.Left + (r.Width - (usedCols * s + (usedCols - 1) * gap)) / 2;
        float top = r.Top + (r.Height - (usedRows * cellH + (usedRows - 1) * gap)) / 2;
        for (int i = 0; i < n; i++)
        {
            var l = links[i];
            var (hover, press) = StateOf(HitOpen, i);
            var box = PressedIn(new RectangleF(left + i % cols * (s + gap), top + i / cols * (cellH + gap), s, s), press);
            using (var path = RoundRect(box, s * 0.24f))
            using (var fill = new SolidBrush(Lifted(p.Track, hover, press, p.Light)))
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
                float nw = s + gap - 2;
                float tw = Math.Min(Measure(g, l.Name, 10.5f, FontStyle.Regular), nw);
                Text(g, l.Name, 10.5f, FontStyle.Regular, p.Muted, box.X + (s - tw) / 2, box.Bottom + 4, maxWidth: nw);
            }
            AddHit(HitOpen, i, new RectangleF(box.X - gap / 2, box.Y, s + gap, cellH));
        }
    }

    /// <summary>
    /// The folder's pictures, one after another, each filling the tile; a tile with room says the folder and which
    /// picture it is at its foot ("Goa trip · 3 of 48").
    /// </summary>
    private static void PhotosTile(Graphics g, WidgetTile t, RectangleF tile, Palette p)
    {
        var r = RectangleF.Inflate(tile, -4, -4);
        string? now = WidgetFiles.PictureNow(t, Now());
        Bitmap? picture = null;
        // One that can't be read is passed over from then on (see PictureNow): the next one in its place.
        for (int tries = 0; tries < 5 && now is not null && (picture = WidgetFiles.Picture(now)) is null; tries++)
            now = WidgetFiles.PictureNow(t, Now());
        if (now is not null && picture is not null)
        {
            Cover(g, picture, r, FillRadius(9));
            if (r.Height >= 88 && r.Width >= 120 && t.Folder is { } folder)
            {
                var files = WidgetFiles.PicturesIn(folder);
                int at = Array.IndexOf(files, now) + 1;
                string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
                string caption = at > 0 ? Lang.T($"{name} · {at} of {files.Length}") : name;
                var foot = RectangleF.FromLTRB(r.Left, r.Bottom - 40, r.Right, r.Bottom);
                using var clip = RoundRect(r, FillRadius(9));
                var state = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                using (var shade = new LinearGradientBrush(new PointF(0, foot.Top - 1), new PointF(0, foot.Bottom), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 0, 0, 0)))
                    g.FillRectangle(shade, foot);
                g.Restore(state);
                Text(g, caption, 11.5f, FontStyle.Regular, Color.White, r.Left + 10, r.Bottom - 22, maxWidth: r.Width - 20);
            }
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
        WidgetTiles.Media => d.Media is { } m ? $"{m.Title}|{m.Artist}|{m.App}|{m.Playing}|{m.Version}|{(int)m.PositionAt(Now()).TotalSeconds}|{(int)m.Length.TotalSeconds}" : "-",
        WidgetTiles.Weather => WeatherFeed.For(t) is { } w ? $"{t.Place}|{w.TempC}|{w.Symbol}|{w.HighC}|{w.LowC}|{w.Fetched.Ticks}|{Units.Fahrenheit}|{Now().Hour}" : $"{t.Place}|-",
        WidgetTiles.Shortcuts => string.Join("|", (t.Links ?? []).Select(l => l.Path + ">" + l.Name)),
        WidgetTiles.Photos => $"{t.Folder}|{WidgetFiles.PictureNow(t, Now())}",
        _ => null,
    };
}
