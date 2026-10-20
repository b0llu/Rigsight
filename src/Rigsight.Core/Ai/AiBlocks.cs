using System.Globalization;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ai;

/// <summary>What colour a block's number or bar takes: plain, a part's own colour, or a tone for good and bad news.</summary>
public enum AiTone { Plain, Cpu, Gpu, Warm, Good, Faint }

/// <summary>
/// Something RigSight draws inside an answer from its own records (never from the model's words): the model only says
/// which one, with a marker such as [show: crashes], and only after looking at that record.
/// </summary>
public abstract record AiBlock;

/// <summary>A big number with what it counts, and a fainter line under it ("19 · sudden shut-offs · 20 Sep – 20 Oct").</summary>
public sealed record AiTile(string Value, string Label, string? Sub, AiTone Tone);

public sealed record AiTiles(IReadOnlyList<AiTile> Tiles) : AiBlock;

/// <summary>One bar of a list: its label, how long against the longest (0 to 1), and the number at its end.</summary>
public sealed record AiBar(string Label, double Share, string Shown, AiTone Tone);

public sealed record AiBars(string Title, IReadOnlyList<AiBar> Bars) : AiBlock;

/// <summary>A row of a table with a bar: the name, the bar (0 to 1) with its number, and one more column.</summary>
public sealed record AiTableRow(string Name, double Share, string Shown, string Extra, bool First);

public sealed record AiBarTable(string NameHeading, string BarHeading, string ExtraHeading, IReadOnlyList<AiTableRow> Rows) : AiBlock;

/// <summary>A temperature now against its usual range, on a 0–100° line: the range as a band, now as a mark.</summary>
public sealed record AiBand(string Label, string Now, string Usual, double NowAt, double LowAt, double HighAt, string? Note, AiTone Tone)
{
    // The line's three stretches (before the usual, the usual, after it), for drawing it as grid columns.
    public double BandWidth => Math.Max(HighAt - LowAt, 0.01);
    public double AfterBand => Math.Max(1 - LowAt - BandWidth, 0);
    public double AfterNow => 1 - NowAt;
    public bool HasNote => Note is not null;
}

public sealed record AiBands(string Title, IReadOnlyList<AiBand> Bands) : AiBlock;

public sealed partial class AiTools
{
    /// <summary>The markers the model may use, each with the look-up it needs first.</summary>
    public static readonly IReadOnlyDictionary<string, string> Markers = new Dictionary<string, string>
    {
        ["crashes"] = "get_crashes",
        ["temps"] = "get_report",
        ["games"] = "list_games",
    };

    /// <summary>
    /// What a marker shows, read from the records the same way that look-up read them (its own period), or nothing when
    /// the model didn't look at that record or there is nothing to show.
    /// </summary>
    public IReadOnlyList<AiBlock> Blocks(string marker, IReadOnlyList<AiLookUp> lookUps)
    {
        if (!Markers.TryGetValue(marker, out var tool) || !Allowed(tool)) return [];
        // The last time it looked: a follow-up asking about another period shows that period.
        if (lookUps.LastOrDefault(l => l.Tool == tool) is not { } look) return [];
        try
        {
            return marker switch
            {
                "crashes" => WithDb(db => CrashBlocks(db, Day(look.Args, "from"), Day(look.Args, "to"))),
                "temps" => WithDb(db => TempBlocks(db, Day(look.Args, "from"), Day(look.Args, "to"))),
                "games" => WithDb(GameBlocks),
                _ => [],
            };
        }
        catch (FormatException ex)
        {
            Log.Write("ai", $"[show: {marker}] couldn't be drawn: {ex.Message}");
            return [];
        }
    }

    private static string Period(DateTime from, DateTime to) =>
        from == to ? from.ToString("d MMM", CultureInfo.CurrentCulture) : Lang.T($"{from:d MMM} – {to:d MMM}");

    private static string KindWords(CrashKind kind, int n) => (kind, n == 1) switch
    {
        (CrashKind.AppCrash, true) => Lang.T("app crash"),
        (CrashKind.AppCrash, false) => Lang.T("app crashes"),
        (CrashKind.AppHang, true) => Lang.T("app freeze"),
        (CrashKind.AppHang, false) => Lang.T("app freezes"),
        (CrashKind.GpuDriverReset, true) => Lang.T("driver reset"),
        (CrashKind.GpuDriverReset, false) => Lang.T("driver resets"),
        (CrashKind.SystemCrash, true) => Lang.T("blue screen"),
        (CrashKind.SystemCrash, false) => Lang.T("blue screens"),
        (_, true) => Lang.T("sudden shut-off"),
        _ => Lang.T("sudden shut-offs"),
    };

    // A count in big type for each kind (three at most), then what RigSight found for the kind there were most of.
    private List<AiBlock> CrashBlocks(Data.RigsightDb db, DateTime from, DateTime to)
    {
        if (to < from) (from, to) = (to, from);
        var stories = Stories(db, from, to);
        string period = Period(from, to);
        if (stories.Count == 0) return [new AiTiles([new AiTile("0", Lang.T("crashes or shut-offs"), period, AiTone.Good)])];
        var kinds = stories.GroupBy(s => s.Crash.Kind).OrderByDescending(g => g.Count()).ToList();
        var tiles = kinds.Take(3).Select(k => new AiTile(k.Count().ToString(CultureInfo.CurrentCulture), KindWords(k.Key, k.Count()), period,
            k.Key is CrashKind.UnexpectedShutdown or CrashKind.SystemCrash ? AiTone.Warm : AiTone.Plain)).ToList();
        var blocks = new List<AiBlock> { new AiTiles(tiles) };
        // The PC's own problems come first (they're what "is my PC ok?" is about), then the kind there were most of.
        var top = kinds.FirstOrDefault(k => k.Key is CrashKind.UnexpectedShutdown or CrashKind.SystemCrash or CrashKind.GpuDriverReset) ?? kinds[0];
        int n = top.Count();
        if (n < 2) return blocks;
        string kindWords = KindWords(top.Key, n);
        bool byApp = top.Key is CrashKind.AppCrash or CrashKind.AppHang;
        // An app's crashes are told apart by the app (each finding is worded for its own crash); the PC's own by RigSight's finding.
        var groups = byApp
            ? top.GroupBy(s => s.App ?? Lang.T("An app")).OrderByDescending(g => g.Count()).ToList()
            : top.GroupBy(s => s.Finding?.Head.TrimEnd('.') ?? Lang.T("No finding")).OrderByDescending(g => g.Count()).ToList();
        if (groups.Count < 2 && byApp) return blocks;
        int most = groups[0].Count();
        string title = byApp ? Lang.T($"Which apps, of the {n} {kindWords}") : Lang.T($"What RigSight found for the {n} {kindWords}");
        blocks.Add(new AiBars(title.ToUpper(CultureInfo.CurrentCulture), [.. groups.Take(7).Select(g =>
        {
            // Strong where it's a real problem (a power cut, the PC itself); faint where Windows was only shutting down.
            var first = g.First();
            var tone = byApp ? AiTone.Plain
                : top.Key != CrashKind.UnexpectedShutdown || PcUsual.ShutOffOf(first.Crash, first.Finding) != ShutOff.None ? AiTone.Warm : AiTone.Faint;
            return new AiBar(g.Key, (double)g.Count() / most, g.Count().ToString(CultureInfo.CurrentCulture), tone);
        })]));
        return blocks;
    }

    // The peaks in big type, then this period's temperatures against their usual range.
    private List<AiBlock> TempBlocks(Data.RigsightDb db, DateTime from, DateTime to)
    {
        if (to < from) (from, to) = (to, from);
        var r = BuildReport(db, from, to);
        if (!r.HasData) return [];
        var blocks = new List<AiBlock>();
        static string When(Peak p) => p.Time.ToString("d MMM, h:mm tt", CultureInfo.CurrentCulture) + (p.App is { } app ? ", " + app : "");
        var tiles = new List<AiTile>();
        if (r.CpuTempPeak is { } cpu) tiles.Add(new AiTile(Units.TempShort(cpu.Value), Lang.T("CPU PEAK"), When(cpu), AiTone.Cpu));
        if (r.GpuTempPeak is { } gpu) tiles.Add(new AiTile(Units.TempShort(gpu.Value), Lang.T("GPU PEAK"), When(gpu), AiTone.Gpu));
        if (tiles.Count > 0) blocks.Add(new AiTiles(tiles));

        var bands = new List<AiBand>();
        static double At(double c) => Math.Clamp(c / 100, 0, 1);
        if (r.Usual is { } u)
        {
            foreach (var h in u.Parts)
            {
                bool isGpu = h.Part.Contains("GPU", StringComparison.OrdinalIgnoreCase);
                string part = isGpu ? Lang.T("GPU") : Lang.T("CPU");
                // As the usual line says it: past the drift bar it counts; under it, a few degrees is said to be too little.
                string? note = h.Tone switch
                {
                    EffectTone.Worse => Lang.T($"{h.Diff} warmer than usual"),
                    EffectTone.Better => Lang.T($"{h.Diff} cooler than usual"),
                    _ when Math.Abs(h.Now - h.Usual) >= 3 => h.Hotter ? Lang.T($"{h.Diff} warmer, too little to count") : Lang.T($"{h.Diff} cooler, too little to count"),
                    _ => null,
                };
                bands.Add(new AiBand(Lang.T($"{part} in {h.App}"), Units.TempShort(h.Now), Lang.T($"usual {Range(h.Low, h.High)}"),
                    At(h.Now), At(h.Low), At(h.High), note, isGpu ? AiTone.Gpu : AiTone.Cpu));
            }
            if (u.RestNow is { } rest)
            {
                if (rest.Cpu is double c && u.RestCpuRange is { } cr)
                    bands.Add(new AiBand(Lang.T("At rest, CPU"), Units.TempShort(c), Lang.T($"usual {Range(cr.Low, cr.High)}"), At(c), At(cr.Low), At(cr.High), null, AiTone.Cpu));
                if (rest.Gpu is double g && u.RestGpuRange is { } gr)
                    bands.Add(new AiBand(Lang.T("At rest, GPU"), Units.TempShort(g), Lang.T($"usual {Range(gr.Low, gr.High)}"), At(g), At(gr.Low), At(gr.High), null, AiTone.Gpu));
            }
        }
        if (bands.Count > 0) blocks.Add(new AiBands(Lang.T("AGAINST YOUR USUAL"), bands));
        return blocks;
    }

    private static string Range(double low, double high)
    {
        string l = Units.TempShort(low).TrimEnd('°'), h = Units.TempShort(high);
        return l + "–" + h;
    }

    // The games that work the graphics card hardest, with how long each was played.
    private List<AiBlock> GameBlocks(Data.RigsightDb db)
    {
        var games = Played(db).Where(g => g.GpuLoad is not null).OrderByDescending(g => g.GpuLoad).Take(8).ToList();
        if (games.Count == 0) return [];
        return [new AiBarTable(Lang.T("GAME"), Lang.T("GRAPHICS CARD BUSY"), Lang.T("PLAYED"),
            [.. games.Select((g, i) => new AiTableRow(g.Name, g.GpuLoad!.Value / 100, $"{g.GpuLoad:0}%", Units.Duration(g.ActiveSec), i == 0))])];
    }
}
