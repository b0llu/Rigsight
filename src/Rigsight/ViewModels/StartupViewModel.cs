using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Export;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;
using Rigsight.Core.Startup;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// The Start-up page's period, read off the window's thread: the report, the startup changes in it, the programs that start
/// with Windows (now, or as they were at its end), and each app's file by its exe (for the icons of the apps launched).
/// </summary>
public sealed record StartupPage(StartupReport Report, List<SystemChange> StartupChanges, List<StartupApp> Apps, bool AppsNow, Dictionary<string, string> Paths);

/// <summary>One of the summary's four figures: a caps label, the figure in its colour (a theme brush's name), a grey line.</summary>
public sealed record StartupTile(string Caption, string Big, string Sub, string Brush);

/// <summary>A part of the opened start: its colour, its length (amber when it ran long; small and grey when there's no reading) and its usual.</summary>
public sealed record StartupPartTile(string Name, string Brush, string Value, bool Warm, string Usual, bool Quiet = false);

/// <summary>A line of "Apps that took longer to start": an app against its own usual, or what Windows itself named, with its icon where there's one.</summary>
public sealed record StartupReportedRow(string? IconPath, string Letter, string Glyph, string Text, string? Sub)
{
    public bool HasIcon => IconPath is not null;
    public bool HasSub => Sub is not null;
}

/// <summary>An app launched as the opened start went on, with how long its launch took (amber when it took longer than its usual).</summary>
public sealed record StartupLaunchRow(string? IconPath, string Letter, string Name, string Value, bool Warm)
{
    public bool HasIcon => IconPath is not null;
}

/// <summary>A line of "Changed since the start before": the change in its kind's icon and colour, opening its Timeline line.</summary>
public sealed record StartupChangeRow(string Glyph, string Brush, string Text, string Sub, DateTime Day);

/// <summary>A startup program that began or stopped starting with Windows in the period (nothing opens: no hover shade).</summary>
public sealed record StartupChangedApp(string? IconPath, string Letter, string Text, string? Sub, string When)
{
    public bool HasIcon => IconPath is not null;
    public bool HasSub => Sub is not null;
}

/// <summary>A program that starts with Windows, in the 3-column grid (nothing opens: no hover shade), with its usual launch in quiet grey.</summary>
public sealed record StartupAppChip(string Name, string? IconPath, string Letter, bool Off, string? Usually = null)
{
    public bool HasIcon => IconPath is not null;
    public string? Sub => Off ? Lang.T("Turned off at startup") : Usually;
    public bool HasSub => Sub is not null;
}

/// <summary>The opened start (approved mock-up's startHero): its kind, length and verdict, its bar against its usual, its parts, its apps, and the two lists.</summary>
public sealed class StartOpened
{
    public string Chip { get; init; } = "";
    public bool ChipBlue { get; init; }
    public string When { get; init; } = "";
    public string Big { get; init; } = "";
    public bool BigWarm { get; init; }
    public string Verdict { get; init; } = "";
    public string VerdictBrush { get; init; } = "MutedBrush";
    public StartupBar Bar { get; init; } = null!;
    public Usual? Usual { get; init; }
    public string? UsualTip { get; init; }
    public List<StartupPartTile> Parts { get; init; } = [];
    /// <summary>Each app launched, longest first, six at most.</summary>
    public List<StartupLaunchRow> Launched { get; init; } = [];
    public bool HasLaunched => Launched.Count > 0;
    public string LaunchedMore { get; init; } = "";
    public bool HasLaunchedMore => LaunchedMore.Length > 0;
    /// <summary>The apps that took longer than their own usual, then what Windows itself named (events 101 to 110).</summary>
    public List<StartupReportedRow> Longer { get; init; } = [];
    public bool HasLonger => Longer.Count > 0;
    /// <summary>The line when no app took longer ("No app took longer…", or too soon to say); null to leave the section out.</summary>
    public string? LongerNote { get; init; }
    public bool HasLongerNote => LongerNote is not null;
    public bool ShowLonger => HasLonger || HasLongerNote;
    public string ChangedWhen { get; init; } = "";
    public List<StartupChangeRow> Changes { get; init; } = [];
    public bool HasChanges => Changes.Count > 0;
    public bool NoChanges => Changes.Count == 0;
    public TryBlockView? Try { get; init; }
}

/// <summary>
/// The Start-up page (1.22.0, approved mock-up 2026-11-27): how long this PC takes to start, in three parts against its
/// usual, the apps that start with Windows and how long each took, and what was different when a start took longer. Every
/// period has the same blocks: a sentence and four figures, every start (on a clock on Day, by week on Year) with the picked
/// one opened under it, and what starts with Windows. Where the period has no start with its length on record (the first
/// minutes after a start, or Windows' logs no longer reaching back), one calm card stands in for the first two.
/// </summary>
public sealed partial class StartupViewModel : ObservableObject
{
    private readonly ReportService _reports;

    public StartupViewModel(ReportService reports) => _reports = reports;

    /// <summary>Opens the Timeline at a day (a change's line, "N more on Timeline").</summary>
    public Action<DateTime>? OpenTimelineAt { get; init; }

    /// <summary>The sidebar's "Restart with admin" (no part of this page needs it since the sign-in logs are read).</summary>
    public ICommand? RestartWithAdmin { get; init; }

    // ── The period (Day by default, today) ──

    [ObservableProperty] private ReportRange _unit = ReportRange.Day;
    [ObservableProperty] private DateTime _anchor = DateTime.Today;
    [ObservableProperty] private DateTime? _firstDay;
    [ObservableProperty] private DateTime _customFrom;
    [ObservableProperty] private DateTime _customTo;

    private (DateTime From, DateTime To) Period => PeriodPicker.Bounds(Unit, Anchor, CustomFrom, CustomTo);
    public bool IncludesToday => Period.To > DateTime.Today;
    public string RangeNote => PeriodPicker.Span(Unit, Anchor, CustomFrom, CustomTo);

    partial void OnUnitChanged(ReportRange value) { OnPropertyChanged(nameof(RangeNote)); _picked = null; if (value != ReportRange.Custom || CustomTo > CustomFrom) _ = RefreshAsync(); }
    partial void OnAnchorChanged(DateTime value) { OnPropertyChanged(nameof(RangeNote)); _picked = null; _ = RefreshAsync(); }
    partial void OnCustomFromChanged(DateTime value) => CustomChanged();
    partial void OnCustomToChanged(DateTime value) => CustomChanged();

    private void CustomChanged()
    {
        OnPropertyChanged(nameof(RangeNote));
        _picked = null;
        if (Unit == ReportRange.Custom && CustomTo > CustomFrom) _ = RefreshAsync();
    }

    public void NewDay(DateTime was)
    {
        var anchor = PeriodPicker.AfterMidnight(Unit, Anchor, was, DateTime.Today);
        if (anchor != Anchor) { Anchor = anchor; return; }
        OnPropertyChanged(nameof(RangeNote));
        _ = RefreshAsync();
    }

    /// <summary>Set by Home's line: the next fill opens the start that line names (the day's first full start), not the day's last longer one.</summary>
    private bool _openHomeStart;

    /// <summary>Opens a day (Home's line about a longer start), its longer start opened.</summary>
    public void ShowDay(DateTime day)
    {
        _picked = null;
        _openHomeStart = true;
        if (Unit != ReportRange.Day) Unit = ReportRange.Day;
        if (Anchor != day.Date) Anchor = day.Date;
        else _ = RefreshAsync();
    }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowLoading), nameof(ShowReport), nameof(ShowEmptyCard), nameof(ShowChart))] private bool _loaded;
    public bool ShowLoading => !Loaded;

    /// <summary>The agent runs without admin rights (set by the shell). Nothing on this page needs them; kept for the log and Windows' own record.</summary>
    [ObservableProperty] private bool _noAdmin;

    private StartupPage? _page;
    private int _load;
    /// <summary>The start picked by a click (its time), kept as the minute's refresh reads the period again.</summary>
    private DateTime? _picked;

    public async Task RefreshAsync()
    {
        int id = ++_load;
        IsLoading = true;
        try
        {
            var (from, to) = Period;
            if (to <= from) return;
            var unit = Unit;
            var page = await _reports.QueryAsync((db, _) => Read(db, unit, from, to, DateTime.Now));
            if (id != _load || page is null) return;
            FirstDay = page.Report.FirstOnRecord?.Date;
            _page = page;
            Fill(page);
            Loaded = true;
        }
        finally
        {
            if (id == _load) IsLoading = false;
        }
    }

    /// <summary>The period's facts (history thread): the report, the startup changes, the startup programs and the icons to show.</summary>
    internal static StartupPage Read(RigsightDb db, ReportRange unit, DateTime from, DateTime to, DateTime now)
    {
        var report = StartupReport.Build(db, unit, from, to, now);
        var changes = db.GetChanges(TimeUtil.ToUnix(from), TimeUtil.ToUnix(now > to ? now : to)).Where(c => c.Kind == ChangeKind.Startup).ToList();
        var apps = StartupApps.Now();
        bool appsNow = to > now;
        if (!appsNow) apps = StartupApps.At(apps, changes, to);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in db.LoadApps())
            if (app.Path is { Length: > 0 } path && app.Exe.Length > 0) paths.TryAdd(app.Exe, path);
        var page = new StartupPage(report, [.. changes.Where(c => c.Time >= from && c.Time < to)], apps, appsNow, paths);
        // The icons, read here rather than on the window's thread (a few ms each, the first time after a start).
        IconCache.Warm(apps.Select(a => a.Path));
        IconCache.Warm(report.Starts.SelectMany(s => s.Record.Slow).Where(s => s.File is not null).Select(s => paths.GetValueOrDefault(s.File!)));
        IconCache.Warm(report.Starts.SelectMany(s => s.Apps).Select(a => IconOf(page, a.Name, a.Exe)));
        return page;
    }

    /// <summary>An app's file for its icon: the program that starts with Windows by that name, else the app RigSight knows by its file.
    /// A file many makers use (setup.exe, updater.exe) isn't matched by name: "Microsoft Edge Installer" (setup.exe) showed
    /// NVIDIA's icon. Its letter stands in.</summary>
    private static string? IconOf(StartupPage page, string name, string? exe) =>
        page.Apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Path is { Length: > 0 } path ? path
        : exe is { Length: > 0 } && !SharedFiles.Contains(exe) && page.Paths.TryGetValue(exe, out var known) ? known : null;

    private static readonly HashSet<string> SharedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup.exe", "update.exe", "updater.exe", "installer.exe", "install.exe", "uninstall.exe", "launcher.exe", "helper.exe",
        "service.exe", "app.exe", "main.exe", "run.exe", "start.exe", "tray.exe", "agent.exe", "client.exe",
    };

    // ── The calm card where the period has no start with its length on record ──

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowReport), nameof(ShowEmptyCard), nameof(ShowChart))] private bool _showEmpty;
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _emptyBrush = "CpuBrush";
    public bool ShowReport => Loaded && !ShowEmpty;
    public bool ShowEmptyCard => Loaded && ShowEmpty;
    /// <summary>The chart card: the period has starts and at least one with its length on record.</summary>
    public bool ShowChart => ShowReport && HasStarts;

    // ── The summary: a sentence and four figures ──

    [ObservableProperty] private string _sentence = "";
    [ObservableProperty] private string _sentenceBrush = "CpuBrush";
    /// <summary>The sentence's icon: the power button, or an info mark in grey with nothing to set it against.</summary>
    [ObservableProperty] private string _sentenceGlyph = "";
    [ObservableProperty] private IReadOnlyList<StartupTile> _tiles = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowChart))] private bool _hasStarts;

    // ── The chart ──

    [ObservableProperty] private string _chartTitle = "";
    [ObservableProperty] private string _chartHelp = "";
    [ObservableProperty] private IReadOnlyList<StartupBar> _bars = [];
    [ObservableProperty] private double _top = 90;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBands))] private IReadOnlyList<Usual> _bands = [];
    public bool HasBands => Bands.Count > 0;
    [ObservableProperty] private int _selected = -1;
    [ObservableProperty] private bool _isClock;
    [ObservableProperty] private double _chartHeight = 230;
    [ObservableProperty] private bool _showFast;
    [ObservableProperty] private bool _showUpdates;
    /// <summary>A longer start's amber dot is on the chart (the legend names only the marks shown).</summary>
    [ObservableProperty] private bool _showLongerMark;
    [ObservableProperty] private string _tooSoon = "";
    [ObservableProperty] private StartOpened? _opened;

    public bool HasTooSoon => TooSoon.Length > 0;
    partial void OnTooSoonChanged(string value) => OnPropertyChanged(nameof(HasTooSoon));

    public string OpenAppsTip => Lang.T("Opens Windows' Startup apps");

    // ── Starts with Windows ──

    [ObservableProperty] private string _changedCaption = "";
    [ObservableProperty] private IReadOnlyList<StartupChangedApp> _changedApps = [];
    [ObservableProperty] private string _noChangedText = "";
    [ObservableProperty] private string _moreText = "";
    [ObservableProperty] private string _appsCaption = "";
    [ObservableProperty] private IReadOnlyList<StartupAppChip> _apps = [];
    /// <summary>More than 12: the box is 4 rows and a half high and scrolls, its last row faded.</summary>
    [ObservableProperty] private bool _appsScroll;
    private DateTime? _moreDay;
    /// <summary>The starts drawn on the chart, in its order (a bar's index points here).</summary>
    private List<StartView> _charted = [];
    public bool HasChangedApps => ChangedApps.Count > 0;
    public bool NoChangedApps => ChangedApps.Count == 0;
    public bool HasMore => MoreText.Length > 0;
    partial void OnChangedAppsChanged(IReadOnlyList<StartupChangedApp> value) { OnPropertyChanged(nameof(HasChangedApps)); OnPropertyChanged(nameof(NoChangedApps)); }
    partial void OnMoreTextChanged(string value) => OnPropertyChanged(nameof(HasMore));

    private void Fill(StartupPage page)
    {
        var r = page.Report;
        var today = DateTime.Today;
        HasStarts = r.Starts.Count > 0;
        FillEmpty(r);
        Sentence = r.Sentence(today);
        SentenceBrush = r.Tone switch { SentenceTone.Warm => "WarmBrush", SentenceTone.Quiet => "MutedBrush", _ => "CpuBrush" };
        SentenceGlyph = r.Tone == SentenceTone.Quiet ? "" : "";
        Tiles = HasStarts ? TilesOf(r, today) : [];

        bool year = r.Unit == ReportRange.Year;
        ChartTitle = year ? Lang.T("Every week") : r.Unit == ReportRange.Day ? Lang.T("Starts this day") : Lang.T("Every start");
        ChartHelp = year && r.FirstOnRecord is { } first
            ? Lang.T($"Each bar is a week's average full start. Starts are on record from {first:d MMM yyyy}. Click a week to open it.")
            : Lang.T("Click a start to open it");
        IsClock = r.Unit == ReportRange.Day;
        ChartHeight = IsClock ? 200 : 230;
        ShowFast = r.Starts.Any(s => s.Kind == StartKind.Fast);
        ShowUpdates = r.Starts.Any(s => s.AfterUpdate);
        TooSoon = HasStarts && r.HasMeasured && r.FullUsual is null && r.Starts.Any(s => s.Kind == StartKind.Full)
            ? StartupWords.TooSoon(StartKind.Full, r.Counted.GetValueOrDefault(StartKind.Full)) : "";
        // A band for each kind shown that has a usual (a laptop with Fast Startup has two); none without a usual.
        Bands = [.. r.Starts.Select(s => s.Kind).Distinct().OrderBy(k => k).Select(k => r.Usuals.GetValueOrDefault(k)).OfType<Usual>()];

        if (year)
        {
            Bars = [.. r.Weeks.Select(w => WeekBar(w, r))];
            Selected = -1;
            Opened = null;
        }
        else
        {
            // Only starts with their length on record are drawn: a start with only some parts (its after-sign-in part from
            // before Windows' logs reached) drew as a short bar, as if it had been quick, beside the measured ones.
            _charted = [.. r.Starts.Where(s => s.Measure is not null || s.AfterUpdate || s.Pending)];
            Bars = [.. _charted.Select(s => BarOf(s, today, page))];
            if (_openHomeStart && StartupReport.HomeLongerStart(r) is { } home) _picked = home.Time;
            _openHomeStart = false;
            int pick = _picked is { } t ? _charted.FindIndex(s => s.Time == t) : -1;
            if (pick < 0) pick = _charted.FindLastIndex(s => s.Longer) is int l and >= 0 ? l : _charted.Count - 1;
            Selected = pick;
            Opened = pick >= 0 ? Open(_charted[pick], today, page) : null;
            if (_charted.Count < r.Starts.Count && _charted.FirstOrDefault(s => s.Measure is not null) is { } firstMeasured)
                ChartHelp = Lang.T($"Start-up times are on record from {firstMeasured.Time:d MMM}. Click a start to open it.");
        }
        ShowLongerMark = Bars.Any(b => b.Longer);
        Top = TopOf(Bars, Bands);
        FillApps(page, r);
    }

    /// <summary>
    /// The calm card in place of the summary and the chart, where the period has no start with its length on record: this
    /// start still being read, nothing on record yet at all, or starts whose times Windows' logs no longer had.
    /// </summary>
    private void FillEmpty(StartupReport r)
    {
        if (r.HasMeasured) { ShowEmpty = false; return; }
        (bool show, string title, string text, string brush) = (r.Pending, r.Starts.Count, r.AnyMeasured) switch
        {
            (true, _, _) => (true, Lang.T("This start shows here in a few minutes"),
                Lang.T("RigSight reads how long each start takes from Windows' own logs, once the apps that start with Windows have opened."), "CpuBrush"),
            (false, > 0, _) => (true, r.Starts.Count == 1 ? StartupWords.NotRecordedWhy : Lang.T("How long these starts took isn't on record"),
                Lang.T("RigSight reads start-up times from Windows' own logs, which keep them for a few days to a few weeks."), "MutedBrush"),
            // Nothing on record at all yet (a new install): the next start fills it.
            (false, 0, _) when r.All.Count == 0 => (true, Lang.T("Start-ups show here after the next start"),
                Lang.T("RigSight reads how long each start takes from Windows' own logs a few minutes after the PC starts."), "CpuBrush"),
            // Before the first start on record, or a hole of weeks in it: not kept, never "no starts".
            (false, 0, _) when r.Unrecorded(r.From, r.To) => (true, Lang.T("Not on record"), HoleText(r), "MutedBrush"),
            // On record, and the PC didn't start in these days.
            (false, 0, _) => (true, r.Unit == ReportRange.Day ? Lang.T("No starts this day") : Lang.T("No starts in these days"),
                r.Unit == ReportRange.Day ? Lang.T("The PC was already on, or off all day.") : Lang.T("The PC was already on, or off the whole time."), "MutedBrush"),
            _ => (false, "", "", "CpuBrush"),
        };
        (ShowEmpty, EmptyTitle, EmptyText, EmptyBrush) = (show, title, text, brush);
    }

    private static string HoleText(StartupReport r)
    {
        var (before, after) = r.AroundHole(r.From, r.To);
        return before is { } b && after is { } a
            ? Lang.T($"No start is on record from {b:d MMM yyyy} to {a:d MMM yyyy}. Windows' logs from then weren't kept.")
            : r.FirstOnRecord is { } f ? Lang.T($"Starts are on record from {f:d MMM yyyy}.") : Lang.T("No starts on record yet.");
    }

    private static List<StartupTile> TilesOf(StartupReport r, DateTime today)
    {
        var u = r.SummaryUsual;
        var longest = r.Longest;
        int longer = r.LongerCount;
        return
        [
            new(Lang.T("STARTS"), r.Starts.Count.ToString(CultureInfo.CurrentCulture), StartupWords.AfterUpdates(r.AfterUpdateCount), "TextBrush"),
            new(r.Starts.Count(s => s.Kind == StartKind.Full && !s.AfterUpdate && s.Measure is not null) == 1 ? Lang.T("FULL START") : Lang.T("AVERAGE FULL START"), r.AverageFull is double avg ? StartupWords.Length(avg) : "–",
                u is null ? Lang.T("Too soon to say") : Lang.T($"Your usual is {StartupWords.Range(u)}"), "CpuBrush"),
            new(Lang.T("LONGEST"), longest?.Measure is double m ? StartupWords.Length(m) : "–", longest is null ? "" : StartupWords.When(longest.Time, today),
                longest is { Longer: true } ? "WarmBrush" : "TextBrush"),
            !r.AnyUsual
                ? new(Lang.T("LONGER THAN USUAL"), "–", Lang.T("Too soon to say"), "TextBrush")
                : longer > 0
                    ? new(Lang.T("LONGER THAN USUAL"), longer.ToString(CultureInfo.CurrentCulture), StartupWords.Starts(longer), "WarmBrush")
                    : new(Lang.T("LONGER THAN USUAL"), Lang.T("None"), Lang.T("All within your usual"), "TextBrush"),
        ];
    }

    /// <summary>The scale's top: past the longest start (not a restart after an update, which is cut) and the usual, at a round figure.</summary>
    internal static double TopOf(IReadOnlyList<StartupBar> bars, IReadOnlyList<Usual> bands)
    {
        double max = bars.Where(b => !b.Update && !b.Empty && !b.Missing).Select(b => b.Height).Concat(bands.Select(u => u.Hi)).DefaultIfEmpty(0).Max();
        if (max <= 0) max = 60;
        foreach (double top in new double[] { 15, 30, 60, 90, 120, 180, 240, 300, 450, 600, 900, 1200, 1800, 3600 })
            if (top >= max * 1.08) return top;
        return Math.Ceiling(max * 1.08 / 600) * 600;
    }

    // ── A start's bar and hover ──

    private static StartupBar BarOf(StartView s, DateTime today, StartupPage page)
    {
        double clock = (s.Time - s.Time.Date).TotalMinutes / 1440;
        string axis = s.Time.ToString("d MMM", CultureInfo.CurrentCulture);
        string title = StartupWords.When(s.Time, today);
        if (s.NotRecorded && !s.AfterUpdate)
            return new StartupBar(null, null, null, new StartupTip(title, s.Pending ? StartupWords.PendingWhy : StartupWords.NotRecordedWhy, Wording.NoReading, "", TipTone.Quiet)
                { Foot = Lang.T("Click to open this start") }) { Missing = true, Clock = clock, Axis = axis };
        if (s.AfterUpdate)
        {
            var updates = s.Changes.Where(c => c.Kind is ChangeKind.WindowsUpdate or ChangeKind.Windows).Take(3)
                .Select(c => new StartupTipItem(null, "", c.Shown, null)).ToList();
            var tip = new StartupTip(title, Lang.T("Restart after an update · not counted in your usual"), s.Length is double len ? StartupWords.Length(len) : Wording.NoReading,
                s.Length is null ? "" : Lang.T("to start"), TipTone.Quiet) { NoteLabel = Lang.T("CHANGED BEFORE IT"), Items = updates, Foot = Lang.T("Click to open this start") };
            return new StartupBar(s.Bios, s.Windows, s.After, tip) { Update = true, Total = s.Length, Clock = clock, Axis = axis };
        }
        var parts = Enumerable.Range(0, 3).Where(p => s.PartOf(p) is not null).ToList();
        string big = s.Measure is double m ? StartupWords.Length(m) : StartupWords.Length(s.Known);
        var shown = new StartupTip(title, StartupWords.Kind(s.Kind), big, StartupWords.Against(s),
            s.Longer ? TipTone.Warm : s.Measure is null ? TipTone.Quiet : TipTone.Plain)
        {
            Split = [.. parts.Select(p => (p, s.PartOf(p)!.Value))],
            Rows = [.. parts.Select(p => new StartupTipRow(p, StartupWords.Part(p), StartupWords.PartLength(s.PartOf(p)!.Value), s.PartLong(p),
                s.PartLong(p) && s.PartUsuals[p] is { } pu ? StartupWords.PartUsual(pu) : null))],
            // What took longer: the apps against their own usual, then what Windows itself named.
            NoteLabel = Lang.T("TOOK LONGER TO START"),
            Items = [.. s.LongerApps.Select(a => new StartupTipItem(IconOf(page, a.Name, a.Exe), null, StartupWords.AppShort(a), StartupWords.PartLength(a.Seconds), Letter(a.Name)))
                .Concat(s.Record.Slow.Select(x => new StartupTipItem(x.File is { } f ? page.Paths.GetValueOrDefault(f) : null, x.Event == 101 ? null : "",
                    StartupWords.ReportedShort(x), StartupWords.Slower(x), Letter(x.Name))))
                .Take(3)],
            Foot = Lang.T("Click to open this start"),
        };
        return new StartupBar(s.Bios, s.Windows, s.After, shown) { Longer = s.Longer, Fast = s.Kind == StartKind.Fast, Clock = clock, Axis = axis };
    }

    private static StartupBar WeekBar(StartWeek w, StartupReport r)
    {
        string title = StartupWords.WeekOf(w.From);
        string axis = w.From.Day <= 7 ? w.From.ToString("MMM", CultureInfo.CurrentCulture) : "";
        if (w.Empty)
        {
            return new StartupBar(null, null, null, new StartupTip(title, null, r.Unrecorded(w.From, w.To) ? Wording.NotRecorded : Lang.T("No starts"), "", TipTone.Quiet)) { Empty = true, Axis = axis };
        }
        var parts = Enumerable.Range(0, 3).Where(p => w.Part(p) is not null).ToList();
        var longer = w.LongerStarts;
        var longest = longer.MaxBy(s => s.Measure) ?? w.Longest;
        var tip = new StartupTip(title, StartupWords.WeekStarts(w.Starts.Count, w.Updates), w.Average is double avg ? StartupWords.Length(avg) : Wording.NoReading,
            w.Average is null ? "" : Lang.T("average full start"), w.Average is null ? TipTone.Quiet : TipTone.Plain)
        {
            Split = [.. parts.Select(p => (p, w.Part(p)!.Value))],
            Facts =
            [
                new(Lang.T("LONGEST"), longest?.Measure is double m ? $"{StartupWords.Length(m)}, {longest.Time:d MMM}" : "–", longest is { Longer: true } ? TipTone.Warm : TipTone.Plain),
                // A week with no start's length on record can't say none was longer.
                w.Average is null && longer.Count == 0
                    ? new(Lang.T("LONGER THAN USUAL"), "–", TipTone.Plain)
                    : new(Lang.T("LONGER THAN USUAL"), longer.Count == 0 ? Lang.T("None") : StartupWords.Starts(longer.Count), longer.Count > 0 ? TipTone.Warm : TipTone.Good),
            ],
            Foot = Lang.T("Click to open this week"),
        };
        // A week without a start's length on record draws nothing (only what's measured is drawn).
        return new StartupBar(w.Part(0), w.Part(1), w.Part(2), tip) { Longer = longer.Count > 0, Empty = w.Average is null, Axis = axis };
    }

    private static string Letter(string name) => name.Length > 0 ? name[..1].ToUpper(CultureInfo.CurrentCulture) : "?";

    // ── The opened start ──

    /// <summary>How many of a start's apps the opened start lists before "N more".</summary>
    private const int LaunchedShown = 6;

    private static StartOpened Open(StartView s, DateTime today, StartupPage page)
    {
        var bar = BarOf(s, today, page);
        // A partial start's figure is the parts it has; a missing part says so small in its own tile.
        string big = s.AfterUpdate ? (s.Length is double len ? StartupWords.Length(len) : "")
            : s.Measure is double m ? StartupWords.Length(m) : s.NotRecorded ? "" : StartupWords.Length(s.Known);
        var parts = Enumerable.Range(0, 3).Select(p =>
        {
            string usual = s.PartUsuals[p] is { } pu ? StartupWords.PartUsual(pu) : "";
            var brush = p switch { 0 => "PinkBrush", 1 => "CpuBrush", _ => "PurpleBrush" };
            // A hyphen that doesn't break: the tile wrapped "sign-" / "in".
            string name = StartupWords.Part(p).Replace('-', '‑');
            return s.PartOf(p) is double v
                ? new StartupPartTile(name, brush, StartupWords.PartLength(v), s.PartLong(p), usual)
                : new StartupPartTile(name, brush, s.Pending && p > 0 ? Lang.T("Still to come") : Wording.NoReading, false, usual, Quiet: true);
        }).ToList();

        var launched = s.Apps.OrderByDescending(a => a.Seconds).Take(LaunchedShown)
            .Select(a => new StartupLaunchRow(IconOf(page, a.Name, a.Exe), Letter(a.Name), a.Name, StartupWords.PartLength(a.Seconds), a.Longer)).ToList();
        int more = s.Apps.Count - launched.Count;

        // Apps that took longer to start: against their own usual first, then what Windows itself named.
        var longer = s.LongerApps.Select(a => new StartupReportedRow(IconOf(page, a.Name, a.Exe), Letter(a.Name), "", StartupWords.AppLonger(a), null))
            .Concat(s.Record.Slow.Select(x => new StartupReportedRow(x.File is { } f ? page.Paths.GetValueOrDefault(f) : null, Letter(x.Name), x.Event == 101 ? "" : "",
                StartupWords.Reported(x), StartupWords.ReportedWhy(x)))).ToList();
        string? longerNote = longer.Count > 0 || s.Apps.Count == 0 || s.AfterUpdate ? null
            : s.Apps.Any(a => a.Usual is not null) ? StartupWords.NoAppLonger
            : StartupWords.AppsTooSoon(s.Apps.Max(a => a.Count));

        var changes = s.Changes.Take(6).Select(c => new StartupChangeRow(Glyph(c.Kind), BrushOf(c.Kind), c.Shown,
            Lang.T($"{StartupWords.Day(c.Time, today)}, on your Timeline"), c.Time.Date)).ToList();
        string verdict = StartupWords.Verdict(s);
        string verdictBrush = s.Verdict switch
        {
            StartVerdict.Longer => "WarmBrush",
            StartVerdict.Within or StartVerdict.Quicker => "GoodBrush",
            StartVerdict.Above => "MutedBrush",
            _ => "MutedBrush",
        };
        string subject = s.Measure is double sm && s.Usual is { } su ? StartupWords.HomeLine(sm, su) + "." : verdict;
        return new StartOpened
        {
            Chip = StartupWords.Chip(s.Kind, s.AfterUpdate), ChipBlue = !s.AfterUpdate, When = StartupWords.When(s.Time, today),
            Big = big, BigWarm = s.Longer, Verdict = verdict, VerdictBrush = verdictBrush,
            Bar = bar, Usual = s.AfterUpdate || s.Measure is null ? null : s.Usual,
            UsualTip = s.Usual is { } u && !s.AfterUpdate && s.Measure is not null ? StartupWords.UsualBox(s.Kind, u) : null,
            Parts = parts,
            Launched = launched, LaunchedMore = more > 0 ? StartupWords.More(more) : "",
            Longer = longer, LongerNote = longerNote,
            ChangedWhen = s.Before is { } before ? StartupWords.When(before.Time, today) : "",
            Changes = changes,
            Try = Core.Advice.WhatToTry.ForStart(s, subject) is { } block ? new TryBlockView(block) : null,
        };
    }

    private static string Glyph(ChangeKind kind) => kind switch
    {
        ChangeKind.Driver => "",
        ChangeKind.WindowsUpdate or ChangeKind.Windows => "",
        ChangeKind.AppInstalled => "",
        ChangeKind.AppRemoved => "",
        ChangeKind.AppUpdated => "",
        ChangeKind.Startup => "",
        ChangeKind.Hardware or ChangeKind.Firmware => "",
        ChangeKind.Setting => "",
        _ => "",
    };

    private static string BrushOf(ChangeKind kind) => kind switch
    {
        ChangeKind.Driver => "GpuBrush",
        ChangeKind.WindowsUpdate or ChangeKind.Windows => "CpuBrush",
        ChangeKind.Startup => "PurpleBrush",
        ChangeKind.Hardware or ChangeKind.Firmware => "PinkBrush",
        ChangeKind.Setting => "MutedBrush",
        _ => "SkyBrush",
    };

    [RelayCommand]
    private void Pick(int index)
    {
        if (_page is not { } page || index < 0) return;
        var r = page.Report;
        if (r.Unit == ReportRange.Year)
        {
            if (index >= r.Weeks.Count || r.Weeks[index].Empty) return;
            // A week opens as Week, ending on its last day; this week (not over yet) opens as its own days so far, not the 7 days to today.
            var week = r.Weeks[index];
            var last = week.To.AddDays(-1);
            if (week.To > DateTime.Today)
            {
                (CustomFrom, CustomTo) = (week.From, DateTime.Today.AddDays(1));
                Unit = ReportRange.Custom;
                return;
            }
            Unit = ReportRange.Week;
            Anchor = last;
            return;
        }
        if (index >= _charted.Count) return;
        _picked = _charted[index].Time;
        Selected = index;
        Opened = Open(_charted[index], DateTime.Today, page);
    }

    [RelayCommand]
    private void OpenChange(StartupChangeRow row) => OpenTimelineAt?.Invoke(row.Day);

    [RelayCommand]
    private void OpenMore()
    {
        if (_moreDay is { } day) OpenTimelineAt?.Invoke(day);
    }

    [RelayCommand]
    private void OpenStartupApps()
    {
        if (Core.Ai.AiActions.Screen("startup-apps") is not { } screen) return;
        Log.Write("startup", "Opened Windows' Startup apps");
        try { TryActions.Launch(screen.Uri); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            Log.Write("startup", $"Opening Startup apps failed: {ex.Message}");
        }
    }

    // ── Starts with Windows ──

    private void FillApps(StartupPage page, StartupReport r)
    {
        bool day = r.Unit == ReportRange.Day;
        ChangedCaption = day ? Lang.T("CHANGED ON THIS DAY") : Lang.T("CHANGED IN THESE DAYS");
        NoChangedText = day ? Lang.T("No app began or stopped starting with Windows this day.") : Lang.T("No app began or stopped starting with Windows in these days.");
        var byName = page.Apps.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        var changes = page.StartupChanges.OrderByDescending(c => c.Time).ToList();
        ChangedApps = [.. changes.Take(5).Select(c =>
        {
            string name = StartupApps.NameOf(c) ?? c.Title;
            int longer = r.All.Count(s => s.LongerApps.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
            int named = r.All.Count(s => s.Longer && s.Record.Slow.Any(x => x.Event == 101 && SameApp(x, name)));
            string? sub = longer == 1 ? Lang.T("Took longer than usual to start once")
                : longer > 1 ? Lang.T($"Took longer than usual to start {longer} times")
                : named == 0 ? null : named == 1 ? Lang.T("Windows named it in 1 longer start") : Lang.T($"Windows named it in {named} longer starts");
            return new StartupChangedApp(byName.GetValueOrDefault(name)?.Path, Letter(name), c.Shown, sub, c.Time.ToString("d MMM", CultureInfo.CurrentCulture));
        })];
        int more = changes.Count - 5;
        MoreText = more > 0 ? Lang.T($"{more} more on Timeline") : "";
        _moreDay = more > 0 ? changes[5].Time.Date : null;

        // Each app's usual launch beside its name, where it has one (not for one that's turned off).
        var shown = page.Apps.Select(a => new StartupAppChip(a.Name, a.Path, Letter(a.Name), !a.On,
            a.On && r.AppUsuals.GetValueOrDefault(a.Name) is { } u ? StartupWords.AppUsually(u) : null)).ToList();
        Apps = shown;
        AppsScroll = shown.Count > 12;
        string caption = page.AppsNow ? Lang.T($"Starts with Windows now · {shown.Count}") : Lang.T($"Started with Windows on {r.To.AddDays(-1):d MMM} · {shown.Count}");
        AppsCaption = caption.ToUpper(CultureInfo.CurrentCulture);
    }

    /// <summary>Whether what Windows named is this startup program: the same name, or its file's name.</summary>
    private static bool SameApp(SlowItem item, string name) =>
        item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
        || (item.File is { } f && System.IO.Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase))
        || (name.Length >= 4 && item.Name.Length >= 4 && (item.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(item.Name, StringComparison.OrdinalIgnoreCase)));

    // ── Export ──

    /// <summary>The page's summary as facts (the Export box), for the period on screen.</summary>
    internal List<FactRow> SummaryFacts() => FactsOf(_page?.Report);

    /// <summary>The summary for a period other than the one on screen, read without a page.</summary>
    internal static async Task<List<FactRow>> SummaryAsync(ReportService reports, ReportRange unit, DateTime anchor)
    {
        var (from, to) = PeriodPicker.Bounds(unit, anchor, default, default);
        return FactsOf(to > from ? await reports.QueryAsync((db, _) => StartupReport.Build(db, unit, from, to, DateTime.Now)) : null);
    }

    private static List<FactRow> FactsOf(StartupReport? r)
    {
        if (r is null || r.Starts.Count == 0) return [new(Lang.T("Starts"), "0", null)];
        var today = DateTime.Today;
        var facts = new List<FactRow>
        {
            new(Lang.T("Starts"), r.Starts.Count.ToString(CultureInfo.CurrentCulture), StartupWords.AfterUpdates(r.AfterUpdateCount)),
            new(Lang.T("Average full start"), r.AverageFull is double a ? StartupWords.Length(a) : "–",
                r.SummaryUsual is { } u ? Lang.T($"Your usual is {StartupWords.Range(u)}") : Lang.T("Too soon to say")),
            new(Lang.T("Longest"), r.Longest?.Measure is double m ? StartupWords.Length(m) : "–", r.Longest is { } l ? StartupWords.When(l.Time, today) : null),
            new(Lang.T("Longer than usual"), r.AnyUsual ? r.LongerCount.ToString(CultureInfo.CurrentCulture) : "–", r.AnyUsual ? null : Lang.T("Too soon to say")),
        };
        foreach (var s in r.Starts)
        {
            string parts = string.Join(", ", Enumerable.Range(0, 3).Where(p => s.PartOf(p) is not null)
                .Select(p => $"{StartupWords.Part(p)} {StartupWords.PartLength(s.PartOf(p)!.Value)}"));
            string longer = string.Join(", ", s.LongerApps.Select(StartupWords.AppLonger));
            string value = s.AfterUpdate ? (s.Length is double len ? StartupWords.Length(len) : Wording.NoReading)
                : s.Measure is double sm ? StartupWords.Length(sm) : s.NotRecorded ? Wording.NoReading : StartupWords.Length(s.Known);
            string detail = s.AfterUpdate ? Lang.T("Restart after an update, not counted in your usual")
                : s.NotRecorded ? StartupWords.NotRecordedWhy
                : parts.Length == 0 ? StartupWords.Verdict(s) : $"{StartupWords.Verdict(s)}. {parts}";
            if (longer.Length > 0) detail = $"{detail}. {longer}";
            facts.Add(new($"{StartupWords.Kind(s.Kind)}, {StartupWords.When(s.Time, today)}", value, detail));
        }
        return facts;
    }
}
