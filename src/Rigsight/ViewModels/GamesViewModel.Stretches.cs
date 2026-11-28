using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Services;
using System.Windows;

namespace Rigsight.ViewModels;

/// <summary>One thing seen around a stretch's stutters: its icon in its kind's colour (palette keys), and the words with their figures **marked**.</summary>
public sealed record SeenRow(string Glyph, string Ink, string Back, string Text);

/// <summary>
/// One stutter of an opened stretch (approved mock-ups "1 · Games, each stutter in a stretch" and "1b · An opened stretch,
/// round 2"): its clock time and how far into the play, its frame against its own usual frame (a bar with the usual as a
/// tick, on the stretch strip's scale), and the readings around it that stood out. <see cref="HasAround"/> is false for a
/// play read by the minute: its rows are the short ones, three across. <see cref="IsPicked"/>: picked on the strip.
/// </summary>
public sealed partial class StutterLine : ObservableObject
{
    public required string Time { get; init; }
    public required string Into { get; init; }
    public required string Frame { get; init; }
    public required string Usual { get; init; }
    public required GridLength Fill { get; init; }
    public required GridLength Empty { get; init; }
    public required GridLength TickAt { get; init; }
    public required GridLength TickAfter { get; init; }
    public required IReadOnlyList<StutterChip> Chips { get; init; }
    public required string Note { get; init; }
    public required bool HasAround { get; init; }
    public bool HasNote => Note.Length > 0;
    /// <summary>A short row's right side: "5:21:09 PM · 20m in".</summary>
    public string TimeInto => $"{Time} · {Into}";

    [ObservableProperty] private bool _isPicked;

    /// <summary>A stutter's line, its bar on a scale to <paramref name="top"/> ms (the strip's).</summary>
    public static StutterLine Of(StutterOne o, double top, long playStart, bool around)
    {
        double scale = Math.Max(1, Math.Max(top, o.UsualMs));
        double fill = Math.Clamp(o.Ms / scale, 0.02, 1), tick = Math.Clamp(o.UsualMs / scale, 0, 1);
        return new StutterLine
        {
            Time = TimeText.At(TimeUtil.FromUnix(o.AtMs / 1000), "h:mm:ss tt"), Into = StutterStretches.In(o.AtMs, playStart),
            Frame = $"{o.Ms:0} ms", Usual = Lang.T($"usual {o.UsualMs:0} ms"),
            Fill = new GridLength(fill, GridUnitType.Star), Empty = new GridLength(1 - fill, GridUnitType.Star),
            TickAt = new GridLength(tick, GridUnitType.Star), TickAfter = new GridLength(1 - tick, GridUnitType.Star),
            Chips = around ? o.Around ?? [] : [], Note = around ? NoteOf(o) : "", HasAround = around,
        };
    }

    /// <summary>Where nothing stood out around a stutter: whether its readings were looked at ("Nothing measured stood out") or none were near it.</summary>
    public static string NoteOf(StutterOne o) =>
        o.Around is { Count: > 0 } ? "" : o.Read ? Lang.T("Nothing measured stood out") : Lang.T("No readings around it");
}

/// <summary>A row of three short stutter rows (a play read by the minute), so the list stays one virtualised column.</summary>
public sealed record StutterTriple(StutterLine A, StutterLine? B, StutterLine? C);

/// <summary>
/// A row of "Where it stuttered" (1.23.0, approved mock-ups "2 + 1 · Games" and "1 · Games, each stutter in a stretch"):
/// a stretch of the play, or the stutters spread through it, with its colour, times, count and what was seen around
/// them. Every row opens (approved mock-up "1b · An opened stretch, round 2") to a strip of its stutters over its own
/// minutes, then the stutters, the longest first: 8 on a play read around each, 9 short ones three across on a play read
/// by the minute, then "And N more, 30 to 64 ms", which opens the rest in the same list. A stretch is also picked by the
/// click (its span lights on the chart); the spread row has none to light.
/// </summary>
public sealed partial class StretchRow : ObservableObject
{
    public required int Index { get; init; }
    public required string Title { get; init; }
    public required string CountText { get; init; }
    public required IReadOnlyList<SeenRow> Seen { get; init; }
    /// <summary>The rail's colour (a palette brush's key) and the chart's (its colour's key).</summary>
    public required string RailKey { get; init; }
    public required string ColorKey { get; init; }
    public required bool Spread { get; init; }
    public required string Grain { get; init; }
    public required DateTime From { get; init; }
    public required DateTime To { get; init; }
    /// <summary>Its first and last stutters (Unix ms): the strip runs from the first one's minute to the end of the last one's.</summary>
    public required long FirstMs { get; init; }
    public required long LastMs { get; init; }
    public bool CanPick => !Spread;
    /// <summary>", 22 to 37 minutes in" after the stretch's times (none for the spread row).</summary>
    public required string Into { get; init; }
    /// <summary>Its stutters, the longest first, and when the play started (Unix seconds) for "22m in".</summary>
    public required IReadOnlyList<StutterOne> Each { get; init; }
    public required long PlayStart { get; init; }
    /// <summary>The readings around each stutter were kept (not a play read by the minute): full rows with "around it".</summary>
    public required bool HasAround { get; init; }

    [ObservableProperty] private bool _isPicked;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMore))] private bool _showAll;
    /// <summary>The list's rows, built the first time the row opens: a <see cref="StutterLine"/> each, or a <see cref="StutterTriple"/> by the minute.</summary>
    [ObservableProperty] private IReadOnlyList<object> _rows = [];
    /// <summary>The stutter picked on the strip (its place in <see cref="Each"/>), -1 for none.</summary>
    [ObservableProperty] private int _stripPicked = -1;

    /// <summary>Over the strip, left.</summary>
    public string StripTitle => Spread ? Lang.T("STUTTERS THROUGH THE PLAY") : Lang.T("STUTTERS THROUGH THIS STRETCH");

    private StretchStrip? _strip;
    /// <summary>The strip's facts (Core), worked out the first time the row opens.</summary>
    public StretchStrip Strip => _strip ??= StretchStrip.Of(Each, FirstMs, LastMs);

    /// <summary>A list row holds this many stutters: one, or three short ones by the minute (for the strip to find a pick's row).</summary>
    public int PerRow => HasAround ? 1 : 3;

    private int First => StutterStretches.First(HasAround);

    private string? _more;
    /// <summary>"And 9 more, 36 to 40 ms" under the first rows, until they're opened.</summary>
    public string MoreText => _more ??= StutterStretches.More([.. Each.Skip(First)]);
    public bool HasMore => !ShowAll && MoreText.Length > 0;

    /// <summary>Each stutter's hover on the strip, made when first shown.</summary>
    public Func<int, RichTip> StripTip => Tip;

    partial void OnIsOpenChanged(bool value)
    {
        if (value && Rows.Count == 0) Fill();
    }

    partial void OnShowAllChanged(bool value)
    {
        if (IsOpen) Fill();
    }

    partial void OnStripPickedChanged(int value)
    {
        foreach (var line in _lines) line.IsPicked = false;
        if (value >= 0 && value < _lines.Count) _lines[value].IsPicked = true;
    }

    [RelayCommand]
    private void ShowRest() => ShowAll = true;

    /// <summary>A spike clicked: its stutter picked, the rest of the list opened when it's past the first rows.</summary>
    [RelayCommand]
    private void PickStutter(int index)
    {
        if (index < 0 || index >= Each.Count) return;
        if (index >= First && !ShowAll) ShowAll = true;
        StripPicked = index;
    }

    /// <summary>The same play shown again (a quiet refresh): the stutter that was picked stays picked, found by its time.</summary>
    public void KeepPick(StretchRow was)
    {
        if (was.StripPicked < 0 || was.StripPicked >= was.Each.Count) return;
        long at = was.Each[was.StripPicked].AtMs;
        for (int i = 0; i < Each.Count; i++)
            if (Each[i].AtMs == at) { StripPicked = i; return; }
    }

    // The lines built so far, in Each's order (the first rows, or all of them).
    private List<StutterLine> _lines = [];

    private void Fill()
    {
        double top = Strip.Top;
        _lines = [.. (ShowAll ? Each : Each.Take(First)).Select(o => StutterLine.Of(o, top, PlayStart, HasAround))];
        if (StripPicked >= 0 && StripPicked < _lines.Count) _lines[StripPicked].IsPicked = true;
        if (HasAround)
        {
            Rows = [.. _lines];
            return;
        }
        var triples = new List<object>((_lines.Count + 2) / 3);
        for (int i = 0; i < _lines.Count; i += 3)
            triples.Add(new StutterTriple(_lines[i], i + 1 < _lines.Count ? _lines[i + 1] : null, i + 2 < _lines.Count ? _lines[i + 2] : null));
        Rows = triples;
    }

    /// <summary>
    /// A stutter's rich hover on the strip (approved mock-up "1b"): its clock time and how far into the play, its frame big
    /// against its own usual, what stood out in the seconds around it (or, by the minute, in its whole minute, said so), and
    /// what a click does.
    /// </summary>
    private RichTip Tip(int index)
    {
        var o = Each[index];
        RichNote note;
        string? aside = null;
        if (HasAround)
        {
            IReadOnlyList<RichItem> items = o.Around is { Count: > 0 } around
                ? [.. around.Select(c => new RichItem(null, c.Label, c.Value))]
                : [new RichItem(null, StutterLine.NoteOf(o))];
            note = new RichNote(Lang.T("In the seconds around it"), items);
        }
        else
        {
            IReadOnlyList<RichItem> items = o.InMinute is { Count: > 0 } minute
                ? [.. minute.Select(c => new RichItem(null, c.Label, c.Value))]
                : [new RichItem(null, Lang.T("Nothing stood out"))];
            note = new RichNote(Lang.T("In that minute"), items);
            aside = Lang.T("Read by the minute, not around this stutter");
        }
        return new RichTip(TimeText.At(TimeUtil.FromUnix(o.AtMs / 1000), "h:mm:ss tt"))
        {
            Sub = StretchStrip.IntoPlay(o.AtMs, PlayStart),
            Big = $"{o.Ms:0} ms",
            BigWord = StretchStrip.Times(o.Ms, o.UsualMs),
            Notes = [note],
            Aside = aside,
            Foot = Lang.T("Click to find it in the list"),
        };
    }
}

public sealed partial class GamesViewModel
{
    // ── Where it stuttered (1.23.0) ─────────────────────────────────────

    /// <summary>The opened play's stretches and the spread stutters, in time order with the spread ones last.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowStretches))] private IReadOnlyList<StretchRow> _stretchRows = [];
    /// <summary>Under the rows, for a play from before the seconds around each stutter were kept: why it's by the minute.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStretchNote))] private string _stretchNote = "";
    /// <summary>The stretches as the chart lights them.</summary>
    [ObservableProperty] private IReadOnlyList<RunsStretch> _chartStretches = [];
    public bool HasStretchNote => StretchNote.Length > 0;
    /// <summary>
    /// With no part found for the play's stutters, which case it was (issue #15 follow-up: not checked, too soon, or none
    /// stood out), quietly under the rows, so a play's stutters never stand without saying whether devices were looked at.
    /// </summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDevicesNote))] private string _devicesNote = "";
    public bool HasDevicesNote => DevicesNote.Length > 0;
    /// <summary>Under the play's chart: an opened play with stutters.</summary>
    public bool ShowStretches => FramesView && StretchRows.Count > 0;
    /// <summary>Beside the heading: which readings these are ("Read in the seconds around each stutter").</summary>
    [ObservableProperty] private string _stretchGrain = "";

    // The stretches' colours, in order: palette colours that aren't a part's or a warning's (the mock-up's pink and violet).
    private static readonly (string Brush, string Color)[] StretchColors = [("PinkBrush", "PinkColor"), ("PurpleBrush", "PurpleColor"), ("SkyBrush", "SkyColor")];

    /// <summary>A row clicked: it opens to its stutters or closes, and a stretch lights on the chart.</summary>
    [RelayCommand]
    private void PickStretch(StretchRow? row)
    {
        if (row is null) return;
        row.IsOpen = !row.IsOpen;
        if (!row.CanPick || row.IsPicked) return;
        foreach (var r in StretchRows) r.IsPicked = ReferenceEquals(r, row);
        LightStretches();
    }

    private void LightStretches() =>
        ChartStretches = [.. StretchRows.Where(r => !r.Spread).Select(r => new RunsStretch(r.From, r.To, r.ColorKey, r.IsPicked))];

    /// <summary>
    /// The panel's rows from a play's report (none without one), the first stretch picked. The same play shown again (a
    /// quiet refresh) keeps the rows that were open, the one picked and the stutters that were all shown.
    /// </summary>
    private void ShowStretchRows(StutterReport? report, Core.Drivers.StutterParts.Cause? device, long playStart)
    {
        if (report is null)
        {
            StretchRows = [];
            StretchNote = "";
            StretchGrain = "";
            ChartStretches = [];
            return;
        }
        var before = _stretchesOf == playStart ? StretchRows.ToDictionary(r => (r.Spread, r.Spread ? 0 : r.From.Ticks)) : [];
        _stretchesOf = playStart;
        var rows = new List<StretchRow>();
        int colour = 0;
        foreach (var s in report.Stretches)
        {
            var (brush, color) = s.Spread ? ("FaintBrush", "FaintColor") : StretchColors[colour++ % StretchColors.Length];
            // A stretch lights from the start of its first stutter's minute to the end of its last one's.
            var from = TimeUtil.FromUnix(s.FromMs / 60_000 * 60);
            var to = TimeUtil.FromUnix(s.ToMs / 60_000 * 60 + 60);
            var row = new StretchRow
            {
                Index = rows.Count, Title = s.Title, CountText = s.CountText, Spread = s.Spread, RailKey = brush, ColorKey = color,
                Seen = [.. s.Seen.Select(x => SeenOf(x, device))], Grain = report.Grain, From = from, To = to,
                Into = s.Into.Length > 0 ? ", " + s.Into : "", Each = s.Each, PlayStart = playStart, HasAround = report.BySeconds,
                FirstMs = s.FromMs, LastMs = s.ToMs,
            };
            if (before.TryGetValue((row.Spread, row.Spread ? 0 : row.From.Ticks), out var was))
            {
                row.IsPicked = was.IsPicked && row.CanPick;
                row.ShowAll = was.ShowAll;
                row.KeepPick(was);
                row.IsOpen = was.IsOpen;
            }
            rows.Add(row);
        }
        if (!rows.Any(r => r.IsPicked) && rows.FirstOrDefault(r => !r.Spread) is { } first) first.IsPicked = true;
        StretchRows = rows;
        StretchNote = report.OldNote ?? "";
        StretchGrain = report.GrainLine;
        LightStretches();
    }

    // The play the rows are for (its start, Unix seconds), so a refresh of the same play keeps what was open.
    private long _stretchesOf = -1;

    /// <summary>An observation's icon in its kind's colour, as the mock-up has them: the disk blue, the GPU cyan, another app violet.</summary>
    private static SeenRow SeenOf(StutterSeen s, Core.Drivers.StutterParts.Cause? device)
    {
        var (glyph, ink, back) = s.Kind switch
        {
            StutterSeenKind.GameDisk or StutterSeenKind.PcDisk => ("", "CpuBrush", "CpuSoftBrush"),
            StutterSeenKind.GpuMemory or StutterSeenKind.GpuBusy => ("", "GpuBrush", "GpuSoftBrush"),
            StutterSeenKind.CpuBusy => ("", "CpuBrush", "CpuSoftBrush"),
            StutterSeenKind.OtherApp => ("", "PurpleBrush", "PurpleSoftBrush"),
            StutterSeenKind.Memory => ("", "RamBrush", "RamSoftBrush"),
            StutterSeenKind.Device when device is not null => DeviceIcon(device),
            _ => ("", "MutedBrush", "Surface2Brush"),
        };
        return new SeenRow(glyph, ink, back, s.Text);
    }

    private static (string, string, string) DeviceIcon(Core.Drivers.StutterParts.Cause device)
    {
        var (ink, back) = LatencyViewModel.KindTint(device.Part.Driver, device.Part);
        return (Core.Drivers.LatencyWords.Icon(device.Part.Driver, device.Part), ink, back);
    }

    /// <summary>
    /// A minute's rich hover (approved mock-up's gTip): its time and stretch, the frame rate, 1% low and stutters, then what
    /// the game and the PC were doing (around its stutters, or the minute as a whole), another app well past its usual,
    /// the part found for the play, and which readings these are.
    /// </summary>
    private static RichTip MinuteTip(DateTime at, double? fps, double? low, int stutters, int soundCuts, HoverNote? note,
        StutterMinuteRead? read, StutterReport report, string game)
    {
        long ts = TimeUtil.ToUnix(at);
        var stretch = report.StretchAt(ts);
        var fpsBrush = ChartPaint.Fps;
        var rows = new List<RichRow>
        {
            new(fpsBrush, Lang.T("Average FPS"), fps is double f ? $"{f:0}" : "—"),
            new(ChartPaint.Brush(fpsBrush.Color, 0.5), Lang.T("1% low"), low is double l ? $"{l:0}" : "—"),
            new(ChartPaint.Warm, Lang.T("Stutters"), stutters == 0 ? Lang.T("None") : $"{stutters}"),
        };
        var facts = new List<RichFact>();
        if (read is { } r)
        {
            if (r.Disk is double d)
            {
                string rate = d < 1e6 ? Lang.T("under 1 MB/s") : $"{d / 1e6:0} MB/s";
                facts.Add(new(r.DiskIsGame ? Lang.T("Game's disk use") : Lang.T("Disk, all apps"),
                    r.AtStutters ? Lang.T($"{rate} at the stutters") : Lang.T($"{rate} this minute")));
            }
            if (r.VramMb is double v && r.VramTotalMb is double vt && vt > 0) facts.Add(new(Lang.T("GPU memory"), Lang.T($"{v / 1024:0.0} of {vt / 1024:0} GB")));
            if (r.Gpu is double g) facts.Add(new("GPU", r.GpuTemp is double gt ? Lang.T($"{g:0}% busy, {Units.TempShort(gt)}") : Lang.T($"{g:0}% busy")));
            if (r.Cpu is double c) facts.Add(new("CPU", Lang.T($"{c:0}% busy")));
        }
        if (soundCuts > 0)
            facts.Add(new(Lang.T("Sound"), soundCuts == 1 ? Lang.T("Cut out once in this minute") : Lang.T($"Cut out {soundCuts} times in this minute"), Brush: ChartPaint.Warm));
        var notes = new List<RichNote>();
        if (read is { BusyApp: long app, BusyCpu: double busy } && report.Apps.TryGetValue(app, out var a))
        {
            var icon = IconCache.Get(a.Path) is { } img ? new RichIcon(Image: img) : new RichIcon(Letter: a.Name[..1].ToUpperInvariant());
            notes.Add(new RichNote(Lang.T("Also busy"), [new RichItem(icon, a.Name, Lang.T($"{busy:0}% CPU"))]));
        }
        // The part found for the play (issue #15), or readings Windows dropped in this minute.
        if (note is { } n) notes.Add(new RichNote(Lang.T("Your devices"), [new RichItem(null, n.Sentence)]));
        string? foot = read is null ? null : read.AtStutters ? Lang.T("Read in the seconds around each stutter") : Lang.T("This minute as a whole");
        return new RichTip(TimeText.At(at, "h:mm tt"))
        {
            Sub = stretch is not null ? Lang.T($"In the stretch {stretch.Title}") : game,
            Rows = rows,
            Facts = facts,
            Notes = notes,
            Foot = foot,
        };
    }
}
