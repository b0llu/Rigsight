using System.Drawing;
using System.Drawing.Imaging;
using Rigsight.Core;
using Rigsight.Core.Settings;
#if RIGSIGHT_APP
// Linked into the app for the Taskbar page's preview, in a namespace of its own there (the tests see both assemblies).
namespace Rigsight.TaskbarDrawing;
#else
using Rigsight.Agent.Widgets;

namespace Rigsight.Agent.Ui;
#endif

/// <summary>
/// The taskbar's readings laid out and drawn, as TaskbarStrip puts them in the taskbar and the Taskbar page previews them
/// (this file is linked into the app, so the page shows exactly what the taskbar will). Since 1.3 each reading has its own
/// style and size: readings next to each other in the list with the same style and size make a run, drawn in that style
/// (the strip's parts, two rows' boxes, icons), and the runs sit side by side, each in the middle of the bar's height.
/// Keeps each graph's last minutes and each reading's widest shape, so it lives as long as the strip (or the page) does.
/// </summary>
internal sealed class StripComposer
{
    /// <summary>How the readings are drawn: on a light taskbar, in grayscale, on tiles, how roomy, two rows' names as one letter, speeds in bits, how far apart.</summary>
    public readonly record struct Options(bool Light, bool Gray, bool Tiles, TrayWidth Width, bool Letters, bool SpeedBits, TrayGap Room = TrayGap.Normal);

    /// <summary>
    /// What will be drawn: the cells (the strip's parts, two rows' boxes, icons; "+N" last when readings wait) with each
    /// one's style, the readings waiting, what tells a change, and its width; <see cref="Render"/> draws it.
    /// </summary>
    public sealed class Layout
    {
        public required List<List<TrayReading>> Cells { get; init; }
        public required List<TrayStyle> CellStyles { get; init; }
        public required int MoreCell { get; init; }
        public required List<TrayReading> Waiting { get; init; }
        public required string Key { get; init; }
        public required int Width { get; init; }
        internal required Func<(Bitmap Bitmap, float[] Edges, float[]?[] ValueEdges)> Draw { get; init; }

        /// <summary>The drawing, each cell's right edge (for clicks and tooltips), and on the strip each reading's within its part.</summary>
        public Bitmap Render(out float[] edges, out float[]?[] valueEdges)
        {
            var (bmp, e, v) = Draw();
            edges = e;
            valueEdges = v;
            return bmp;
        }
    }

    /// <summary>Each graph's readings with their times (ms), the last <see cref="TrayParts.GraphSeconds"/> kept (both speeds' upload as "id#up").</summary>
    private readonly Dictionary<string, Queue<(long At, float Value)>> _graphs = [];


    /// <summary>The graphs kept now (by look source), for the log.</summary>
    public IReadOnlyCollection<string> Graphs => _graphs.Keys;

    /// <summary>
    /// Keeps the newest reading of every look a strip reading draws from (a look switched to a graph then has its minutes
    /// already), and forgets those no longer shown. Called once a second, before <see cref="Build"/>.
    /// </summary>
    public void Remember(IReadOnlyList<TrayReading> readings, long now)
    {
        var wanted = new Dictionary<string, double?>();
        foreach (var r in readings)
        {
            if (r.Style != TrayStyle.Strip) continue;
            wanted[r.LookSource] = r.LookSourceValue;
            if (r.BothSpeeds) wanted[r.Id + "#up"] = r.Second;
        }
        foreach (var gone in _graphs.Keys.Where(k => !wanted.ContainsKey(k)).ToList()) _graphs.Remove(gone);
        foreach (var (key, value) in wanted)
        {
            if (!_graphs.TryGetValue(key, out var points)) _graphs[key] = points = new();
            points.Enqueue((now, value is double v && double.IsFinite(v) ? (float)v : float.NaN));
            while (points.Count > 0 && now - points.Peek().At > TrayParts.GraphSeconds * 1000L) points.Dequeue();
        }
    }

    /// <summary>
    /// Lays the readings out at <paramref name="barHeight"/> pixels and <paramref name="scale"/>, the strip's graphs
    /// <paramref name="graphWide"/> wide (at 96 dpi), the last <paramref name="wait"/> of them (in the order shown) left for "+N".
    /// </summary>
    public Layout Build(IReadOnlyList<TrayReading> readings, Options o, float graphWide, int wait, int barHeight, float scale, long now)
    {
        // Runs: readings next to each other with the same style, size, colours, background and names (each run drawn with
        // its own). With pairs saved (1.3), an icon per part is an icon each with its pairs.
        bool paired = readings.Count > 0 && readings.All(r => r.PairNext is not null);
        TrayStyle StyleOf(TrayReading r) => paired && r.Style == TrayStyle.Grouped ? TrayStyle.Icons : r.Style;
        Options With(TrayReading r) => o with { Gray = r.Gray ?? o.Gray, Tiles = r.Tiles ?? o.Tiles, Letters = r.Letters ?? o.Letters };
        var runs = new List<(TrayStyle Style, TraySize Size, Options With, List<List<TrayReading>> Cells)>();
        foreach (var r in readings)
        {
            var with = With(r);
            if (runs.Count > 0 && runs[^1].Style == StyleOf(r) && runs[^1].Size == r.Size && runs[^1].With == with) runs[^1].Cells.Add([r]);
            else runs.Add((StyleOf(r), r.Size, with, [[r]]));
        }
        // Each run's cells: its spots (see TrayParts.Spots; both speeds take a box of their own, their two lines fill it).
        for (int k = 0; k < runs.Count; k++)
        {
            var items = runs[k].Cells.Select(c => c[0]).ToList();
            runs[k] = (runs[k].Style, runs[k].Size, runs[k].With, TrayParts.Spots(items, runs[k].Style, r => r.Spot));
        }

        // The last ones shown wait for room in "+N", whatever their style.
        var shown = runs.SelectMany(run => run.Cells.SelectMany(c => c)).ToList();
        var waiting = wait > 0 ? shown.Skip(Math.Max(0, shown.Count - wait)).ToList() : [];
        var waitIds = waiting.Select(r => r.Id).ToHashSet();
        for (int k = 0; k < runs.Count; k++)
            runs[k] = (runs[k].Style, runs[k].Size, runs[k].With, [.. runs[k].Cells.Select(c => c.Where(r => !waitIds.Contains(r.Id)).ToList()).Where(c => c.Count > 0)]);
        runs.RemoveAll(run => run.Cells.Count == 0);
        int moreRun = -1;
        if (waiting.Count > 0)
        {
            if (runs.Count == 0) runs.Add((StyleOf(waiting[0]), waiting[0].Size, With(waiting[0]), []));
            moreRun = runs.Count - 1;
            runs[moreRun].Cells.Add(waiting);
        }

        // The parts' names, numbered across the whole taskbar where one comes twice ("GPU 1", "GPU 2").
        // The game's frame rate is "FPS", never a second GPU ("GPU 1", "GPU 2" for one card).
        var groups = readings.Where(r => r.Id != TrayParts.Fps).Select(r => r.Group).Distinct().ToList();
        var partNames = TrayParts.Names([.. groups.Select(g => readings.First(r => r.Group == g).Part)]);
        var nameOf = groups.Select((g, i) => (g, partNames[i])).ToDictionary(x => x.g, x => x.Item2);
        // Each reading's word across the whole taskbar: a part's name once, never one word twice (see TrayParts.Words).
        var words = TrayParts.Words([.. runs.SelectMany(run => run.Cells).Select(c => (IReadOnlyList<TrayReading>)c)],
            r => (r.Id, r.Group, nameOf.GetValueOrDefault(r.Group, TrayParts.Label(r.Part)), r.Part == TrayPart.Network || r.Id == TrayParts.Fps, r.OwnShort, r.Short, r.Name));

        var segments = runs.Select((run, k) =>
        {
            int h = TrayParts.SizeHeight(run.Size, barHeight, scale);
            // Large is drawn bigger as well as taller: its text, rings and icons a fifth bigger (when the taskbar has the room).
            float grow = TrayParts.SizeGrows(run.Size, barHeight, scale) ? TrayParts.LargeGrowth : 1;
            var look = new StripLook(run.With.Tiles, o.Width, o.Light, o.Room);
            return Segment(run.Style, run.Cells, k == moreRun ? run.Cells.Count - 1 : -1, waiting.Count, readings, words, run.With, look, graphWide, h, scale * grow, now);
        }).ToList();

        var cellsAll = new List<List<TrayReading>>();
        var styles = new List<TrayStyle>();
        int moreCell = -1;
        for (int k = 0; k < runs.Count; k++)
        {
            if (k == moreRun) moreCell = cellsAll.Count + runs[k].Cells.Count - 1;
            cellsAll.AddRange(runs[k].Cells);
            styles.AddRange(Enumerable.Repeat(runs[k].Style, runs[k].Cells.Count));
        }

        // Every tile the same distance from the next, whatever its style: a run's own blank edges count towards the gap
        // between runs (between a strip's parts, two rows' boxes and icons the same room is kept inside a run).
        float between = new StripLook(o.Tiles, o.Width, o.Light, o.Room).Spacing.Gap * scale;
        // A gap smaller than two blank edges (None, Small) lets the runs overlap there: their edges are empty, so only the
        // room asked for shows between the tiles.
        int[] gaps = [.. segments.Select((s, k) => k == segments.Count - 1 ? 0 : (int)Math.Round(between - s.Edge - segments[k + 1].Edge))];
        int width = Math.Max(1, segments.Sum(s => s.Width) + gaps.Sum());
        string key = string.Join("‖", segments.Select(s => s.Key));
        return new Layout
        {
            Cells = cellsAll,
            CellStyles = styles,
            MoreCell = moreCell,
            Waiting = waiting,
            Key = key,
            Width = width,
            Draw = () =>
            {
                var bmp = new Bitmap(width, Math.Max(1, barHeight), PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(bmp);
                // An all-but-invisible background, so clicks between the letters and between runs still land on it.
                using (var catcher = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(catcher, 0, 0, width, barHeight);
                var edges = new List<float>();
                var valueEdges = new List<float[]?>();
                int x = 0;
                for (int k = 0; k < segments.Count; k++)
                {
                    var (part, e, v) = segments[k].Draw();
                    using (part)
                    {
                        int y = (barHeight - part.Height) / 2;
                        g.DrawImage(part, new Rectangle(x, y, part.Width, part.Height), 0, 0, part.Width, part.Height, GraphicsUnit.Pixel);
                    }
                    bool last = k == segments.Count - 1;
                    for (int i = 0; i < e.Length; i++)
                    {
                        // A run's last cell reaches halfway into the gap to the next; the very last, to the end.
                        bool runEnd = i == e.Length - 1;
                        edges.Add(runEnd ? (last ? width : x + segments[k].Width + gaps[k] / 2f) : x + e[i]);
                        valueEdges.Add(i < v.Length && v[i] is { } ve ? [.. ve.Select(edge => edge == float.MaxValue ? edge : x + edge)] : null);
                    }
                    x += segments[k].Width + gaps[k];
                }
                return (bmp, [.. edges], [.. valueEdges]);
            },
        };
    }

    /// <summary>
    /// The readings fitted to <paramref name="room"/> pixels: the strip's graphs narrower first, as wide as still fits down
    /// to their words' width, then the fewest last readings waiting in "+N" (at least "+N" itself shows). Each found by
    /// halving, a handful of measurements, not one a pixel: this runs with every reading.
    /// </summary>
    public (float GraphWide, int Wait) Fit(IReadOnlyList<TrayReading> readings, Options o, float graphWide, int barHeight, float scale, int room, long now)
    {
        if (room == int.MaxValue) return (graphWide, 0);
        int Width(float wide, int wait) => Build(readings, o, wide, wait, barHeight, scale, now).Width;
        float graph = graphWide;
        bool anyStrip = readings.Any(r => r.Style == TrayStyle.Strip);
        if (anyStrip && graph > 0 && Width(graph, 0) > room)
        {
            float lo = 0, hi = graph;
            if (Width(0, 0) > room) hi = 0;
            else while (hi - lo > 1) { float mid = (lo + hi) / 2; if (Width(mid, 0) <= room) lo = mid; else hi = mid; }
            graph = hi == 0 ? 0 : MathF.Floor(lo);
        }
        if (Width(graph, 0) <= room) return (graph, 0);
        int fewest = 1, most = readings.Count;
        while (fewest < most) { int mid = (fewest + most) / 2; if (Width(graph, mid) <= room) most = mid; else fewest = mid + 1; }
        return (graph, fewest);
    }

    /// <summary>One run drawn: its width, what tells a change, its drawing at its own height, and the blank it leaves at each end.</summary>
    private sealed record Run(int Width, string Key, Func<(Bitmap Bitmap, float[] Edges, float[]?[] ValueEdges)> Draw, float Edge);

    private Run Segment(TrayStyle style, List<List<TrayReading>> cells, int moreCell, int waitCount, IReadOnlyList<TrayReading> readings,
        Dictionary<string, string> words, Options o, StripLook look, float graphWide, int height, float scale, long now)
    {
        var ink = o.Light ? Color.Black : Color.White;
        var neutral = Color.FromArgb(TrayColors.NeutralTileAlpha(o.Light), ink);
        string sizeKey = $"{style}:{height}:{o.Gray}:{o.Tiles}:{o.Letters}:";

        if (style == TrayStyle.Strip)
        {
            var strip = cells.Select((c, i) =>
            {
                if (i == moreCell)
                    return new StripCell("", ink, [new StripValue($"+{waitCount}", ink, Lang.T("more"), "+88", LabelColor: Color.FromArgb(160, ink),
                        Tile: look.Tiles ? neutral : null, More: true)]);
                // Each reading's word as the whole taskbar has it (a part's name once; see TrayParts.Words).
                string Word(TrayReading r) => words.GetValueOrDefault(r.Id, r.Short);
                return new StripCell(Word(c[0]), c[0].WordOn(o.Light, o.Gray), [.. c.Select(r => Value(r, o, Word(r), now))]);
            }).ToList();
            string key = sizeKey + string.Join('|', strip.Select(c => c.Name + ":" + string.Join(',', c.Values.Select(v => v.Text + v.Second + v.Label + v.Color.ToArgb() + "/" + v.LabelColor?.ToArgb()
                + v.Look + v.Fill.ToString("0.###") + v.Tile?.ToArgb() + Points(v.Graph) + Points(v.Graph2) + (v.Cores is { } cores ? string.Join(';', cores.Select(f => f.ToString("0.##"))) : "")))));
            int width = WidgetRenderer.MeasureStrip(strip, height, scale, graphWide, look);
            return new Run(width, key, () =>
            {
                var bmp = WidgetRenderer.RenderStrip(strip, height, scale, out var edges, out var valueEdges, graphWide, look);
                return (bmp, edges, [.. valueEdges.Select(v => (float[]?)v)]);
            }, WidgetRenderer.StripEdge * scale);
        }

        if (style == TrayStyle.TwoRows)
        {
            var columns = TwoRowColumns(cells, moreCell, waitCount, words, o, look.Tiles);
            string key = sizeKey + string.Join('|', columns.Select(c => c.Tile?.ToArgb() + ":" + string.Join(',', c.Lines.Select(l => $"{l.Label}{l.LabelColor.ToArgb()}{l.Text}{l.Color.ToArgb()}{l.Reserve}"))));
            int width = WidgetRenderer.MeasureTwoRows(columns, height, scale, look);
            return new Run(width, key, () =>
            {
                var bmp = WidgetRenderer.RenderTwoRows(columns, height, scale, out var edges, look);
                return (bmp, edges, new float[]?[edges.Length]);
            }, WidgetRenderer.StripEdge * scale);
        }

        // Icons, as the notification area would draw them, side by side about as far apart as Windows' own.
        var icons = cells.Select((c, i) => i == moreCell
            ? ((IReadOnlyList<(string Text, Color Color)>)[($"+{waitCount}", ink)], look.Tiles ? neutral : (Color?)null, (WidgetRenderer.IconLook?)null)
            : (IconLines(c, o), IconTile(c, o), IconLookOf(c, o))).ToList();
        string iconKey = sizeKey + string.Join('|', icons.Select(ic => string.Join(',', ic.Item1.Select(l => l.Text + l.Color.ToArgb())) + "/" + ic.Item2?.ToArgb()
            + "/" + ic.Item3?.Look + ic.Item3?.Fill.ToString("0.##")));
        // Icons as far apart as every other tile (the room between readings set on the page).
        int size = IconSize(height, scale), slot = size + (int)Math.Round(look.Spacing.Gap * scale);
        return new Run(Math.Max(1, slot * icons.Count), iconKey, () =>
        {
            var bmp = WidgetRenderer.RenderIconRow(icons, height, size, slot, out var edges);
            return (bmp, edges, new float[]?[edges.Length]);
        }, (slot - size) / 2f);
    }

    private static string Points(StripGraph? graph) => graph is null ? "" : string.Join(';', graph.Points.Select(pt => $"{pt.X:0.###},{pt.Y:0.###}"));

    /// <summary>
    /// An icon's size (see <see cref="TrayParts.IconSize"/>: 30 at 100%, 45 at 150%), kept clear of a short taskbar's top
    /// and bottom.
    /// </summary>
    internal static int IconSize(int height, float scale) =>
        Math.Max(1, Math.Min((int)Math.Round(TrayParts.IconSize * scale), height - (int)Math.Round(12 * scale)));

    /// <summary>The room each icon takes: itself and the gap to the next.</summary>
    internal static int IconSlot(int height, float scale) => IconSize(height, scale) + (int)Math.Round(TrayParts.IconGap * scale);

    /// <summary>A reading's number as the strip shows it: speeds with their unit stepping up (see <see cref="Units.StripSpeed"/>).</summary>
    private static string StripText(TrayReading r, double? value, Options o) =>
        r.Kind == SensorKind.Throughput ? Units.StripSpeed(value, o.SpeedBits) : Units.StripText(r.Kind, value);

    /// <summary>
    /// A reading's number on an icon: speeds with one letter for their unit ("0.2K", "12M", "1.1G"; in bits the same
    /// steps of bits), since a slow speed in MB would read "0.0".
    /// </summary>
    private static string IconText(TrayReading r, double? value, Options o)
    {
        if (r.Kind != SensorKind.Throughput) return WidgetRenderer.TrayText(r.Kind, value);
        if (value is not double v || !double.IsFinite(v)) return "–";
        v = Math.Max(0, v);
        double k = o.SpeedBits ? 1000 : 1024, n = o.SpeedBits ? v * 8 / k : v / k;
        string unit = "K";
        if (n >= k) { n /= k; unit = "M"; }
        if (n >= k) { n /= k; unit = "G"; }
        string num = n < 10 ? n.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : Math.Round(n).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        return num + unit;
    }

    /// <summary>An icon's numbers, each in its colour: both speeds as two (download on top).</summary>
    private static IReadOnlyList<(string Text, Color Color)> IconLines(IReadOnlyList<TrayReading> icon, Options o)
    {
        var lines = new List<(string, Color)>();
        foreach (var r in icon)
        {
            lines.Add((IconText(r, r.Value, o), r.NumberOn(o.Light, o.Gray)));
            if (r.BothSpeeds) lines.Add((IconText(r, r.Second, o), r.NumberOn(o.Light, o.Gray)));
        }
        return [.. lines.Take(TrayParts.PerIcon)];
    }

    /// <summary>The icon's tile with tiles on: its first reading's; none while no reading is found, or with tiles off.</summary>
    private static Color? IconTile(IReadOnlyList<TrayReading> icon, Options o) =>
        !o.Tiles || icon.All(r => r.Hardware.Length == 0) ? null : icon[0].TileOn(o.Light, o.Gray, icon: true);

    /// <summary>The icon's look (its first reading's): filling up or a ring, in its name's colour; none for its number alone.</summary>
    private static WidgetRenderer.IconLook? IconLookOf(IReadOnlyList<TrayReading> icon, Options o) =>
        icon[0].IconLook is TrayLook.Level or TrayLook.Ring && icon[0].Hardware.Length > 0
            ? new WidgetRenderer.IconLook(icon[0].IconLook, icon[0].Fill, icon[0].NameOn(o.Light, o.Gray))
            : null;

    /// <summary>
    /// Two rows' boxes as drawn: each reading's word (see <see cref="TrayParts.BoxLabel"/>; a part's name numbered when it
    /// comes twice, "GPU 2") in its words' colour and its number in its own, on the box's tile (the first one picked in
    /// it, else the top's); both speeds as two lines, "↓" and "↑"; "+N" last, over "more".
    /// </summary>
    private List<TwoRowColumn> TwoRowColumns(List<List<TrayReading>> cells, int moreCell, int waitCount, Dictionary<string, string> words, Options o, bool tiles)
    {
        var ink = o.Light ? Color.Black : Color.White;
        return [.. cells.Select((box, i) =>
        {
            if (i == moreCell)
                return new TwoRowColumn([new TwoRowLine("", ink, $"+{waitCount}", ink, "+88"), new TwoRowLine(Lang.T("more"), Color.FromArgb(160, ink), "", ink)],
                    tiles ? Color.FromArgb(TrayColors.NeutralTileAlpha(o.Light), ink) : null);
            var lines = new List<TwoRowLine>();
            for (int j = 0; j < box.Count; j++)
            {
                var r = box[j];
                string text = StripText(r, r.Value, o), widest = Widest(r.Id, text);
                if (r.BothSpeeds)
                {
                    string up = StripText(r, r.Second, o);
                    lines.Add(new TwoRowLine(o.Letters ? TrayParts.Letter(Lang.T("Down")) : Lang.T("Down"), r.WordOn(o.Light, o.Gray), text, r.NumberOn(o.Light, o.Gray), widest));
                    lines.Add(new TwoRowLine(o.Letters ? TrayParts.Letter(Lang.T("Up")) : Lang.T("Up"), r.WordOn(o.Light, o.Gray), up, r.NumberOn(o.Light, o.Gray), Widest(r.Id + "#up", up)));
                    continue;
                }
                // Its word as the whole taskbar has it (a part's name once; see TrayParts.Words), or one letter of it.
                string word = words.GetValueOrDefault(r.Id, r.Short);
                string label = r.Hardware.Length == 0 ? "" // not found yet: the hover names it
                    : o.Letters ? TrayParts.Letter(word) : word;
                lines.Add(new TwoRowLine(label, r.WordOn(o.Light, o.Gray), text, r.NumberOn(o.Light, o.Gray), widest));
            }
            return new TwoRowColumn(lines, tiles ? box.FirstOrDefault(r => r.TileColor is not null, box[0]).TileOn(o.Light, o.Gray, icon: false) : null);
        })];
    }

    /// <summary>
    /// The room a reading's number keeps: its shape now (see <see cref="Shape"/>), each digit an 8, so a digit changing
    /// moves nothing. Since 1.3 not the widest it has ever been: a load that once hit 100% left a gap beside "5%" for good.
    /// </summary>
    private static string Widest(string key, string text) => Shape(text);

    /// <summary>A reading on the strip as drawn: its number, its word, its look with what it needs (graph, fill, cores, the upload), its tile.</summary>
    private StripValue Value(TrayReading r, Options o, string label, long now)
    {
        string text = StripText(r, r.Value, o);
        string widest = Widest(r.Id, text);
        var look = r.StripLook;
        // A graph in its name's colour (its part's, or the one picked; grayscale: the taskbar's text colour).
        var graphColor = o.Gray ? WidgetRenderer.TrayColor(SensorKind.Load, 0, o.Light, gray: true) : r.NameOn(o.Light, o.Gray);
        bool graphed = look is TrayLook.Area or TrayLook.Line or TrayLook.Bars or TrayLook.Spark or TrayLook.Mirror;
        StripGraph? graph = graphed ? new StripGraph(GraphPoints(r.LookSource, r.LookSourceKind, now), graphColor) : null;
        StripGraph? graph2 = look == TrayLook.Mirror ? new StripGraph(GraphPoints(r.Id + "#up", r.Kind, now), graphColor) : null;
        string second = r.BothSpeeds ? StripText(r, r.Second, o) : "";
        return new StripValue(text, r.NumberOn(o.Light, o.Gray), label, widest, graph, r.WordOn(o.Light, o.Gray), look, r.Fill,
            o.Tiles ? r.TileOn(o.Light, o.Gray, icon: false) : null, Second: second, SecondReserve: second.Length > 0 ? Widest(r.Id + "#up", second) : "",
            Graph2: graph2, Cores: r.Cores, Behind: r.GraphBehind);
    }

    /// <summary>A graph's points, across its two minutes (now at the right) and up its range (see <see cref="TrayParts.GraphRange"/>).</summary>
    private List<PointF> GraphPoints(string key, SensorKind kind, long now)
    {
        if (!_graphs.TryGetValue(key, out var points) || points.Count == 0) return [];
        long window = TrayParts.GraphSeconds * 1000L;
        var (lo, hi) = TrayParts.GraphRange(kind, [.. points.Select(p => p.Value)]);
        return [.. points.Select(p => new PointF(1 - (now - p.At) / (float)window,
            float.IsFinite(p.Value) && hi > lo ? (float)((p.Value - lo) / (hi - lo)) : float.NaN))];
    }

    /// <summary>
    /// The room a reading needs, whatever its digits: each one an 8, the widest ("56°" → "88°", "4.5 GHz" → "8.8 GHz"),
    /// and a whole number at least two ("9%" → "88%"), so the everyday change from one digit to two moves nothing.
    /// </summary>
    internal static string Shape(string text)
    {
        var shape = new string([.. text.Select(ch => char.IsDigit(ch) ? '8' : ch)]);
        int digits = shape.TakeWhile(ch => ch == '8').Count();
        bool whole = digits == shape.Length || shape[digits] != '.'; // "4.5 GHz" is as wide as it gets
        return digits == 1 && whole ? "8" + shape : shape;
    }
}
