using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

// The drives as a table of physical drives, their letters under each, and a drive opened in full (wear and drive
// details, approved mock-up 2026-11-14): Needs a look as cards on top, then Inside the PC, then USB and network.
public sealed partial class StorageViewModel : Views.IDayRange
{
    /// <summary>Drives that need a look (failing first), as cards above the table.</summary>
    public ObservableCollection<DriveGroup> Looks { get; } = [];
    /// <summary>Every other drive inside the PC, the Windows drive first, then the fullest.</summary>
    public ObservableCollection<DriveGroup> Inside { get; } = [];
    /// <summary>USB and network drives.</summary>
    public ObservableCollection<DriveGroup> Others { get; } = [];

    public bool HasLooks => Looks.Count > 0;
    public bool HasOthers => Others.Count > 0;
    /// <summary>Over six drives the table scrolls in its own box, the seventh cut in half (see the view).</summary>
    public bool InsideScrolls => Inside.Count > MaxInsideRows;
    public const int MaxInsideRows = 6;
    public string LooksSummary => Looks.Count == 1 ? Lang.T("1 drive") : Lang.T($"{Looks.Count} drives");
    public string InsideSummary => Inside.Count == 0 ? "" : Lang.T($"{Inside.Count} drives · {Units.Bytes(Inside.SelectMany(g => g.Letters).Sum(v => v.Used))} of {Units.Bytes(Inside.SelectMany(g => g.Letters).Sum(v => v.Total))} used");
    public bool StandardAccountHealth => Live.StandardAccount && Live.DriveHealth.Count == 0;

    private bool _groupsHooked;

    /// <summary>The physical drives with their letters, from Windows' letters and the agent's drive readings.</summary>
    private void BuildGroups()
    {
        if (!_groupsHooked)
        {
            _groupsHooked = true;
            Live.DriveHealthUpdated += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(BuildGroups);
        }
        var health = Live.DriveHealth;
        var letters = Volumes.Concat(OtherVolumes).ToList();
        var used = new HashSet<VolumeRow>();
        var groups = new List<DriveGroup>();
        foreach (var h in health)
        {
            var mine = letters.Where(l => l.Disk is int d && d == h.DiskNumber).OrderBy(l => l.Root, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var l in mine) used.Add(l);
            var summary = Live.Drives.FirstOrDefault(d => d.Name == h.Name);
            string id = h.Key ?? $"name:{h.Name}";
            bool usb = DriveGroup.UsesUsb(h, mine);
            groups.Add(Keep(new DriveGroup(id, h.Name, h, summary, mine, usb ? "other" : "inside")));
        }
        // A letter whose drive gave no reading (a network drive, a USB stick behind an adapter, no admin rights): a row of its own.
        foreach (var l in letters.Where(l => !used.Contains(l)))
            groups.Add(Keep(new DriveGroup($"letter:{l.Letter}", l.Kind == "network" && l.HasSub ? l.Sub : l.Title, null, null, [l], l.Kind == "fixed" ? "inside" : "other")));

        Sync(Looks, [.. groups.Where(g => g.NeedsLook).OrderByDescending(g => g.Level).ThenBy(g => g.Name)]);
        Sync(Inside, [.. groups.Where(g => !g.NeedsLook && g.Section == "inside").OrderByDescending(g => g.IsWindows).ThenByDescending(g => g.MainLetter?.UsedPercent ?? -1)]);
        Sync(Others, [.. groups.Where(g => !g.NeedsLook && g.Section == "other").OrderBy(g => g.MainLetter?.Root ?? g.Name, StringComparer.OrdinalIgnoreCase)]);
        foreach (var name in new[] { nameof(HasLooks), nameof(LooksColumns), nameof(HasOthers), nameof(InsideScrolls), nameof(LooksSummary), nameof(InsideSummary), nameof(StandardAccountHealth) })
            OnPropertyChanged(name);
        if (Opened is { } open && groups.FirstOrDefault(g => g.Id == open.Id) is { } again && !ReferenceEquals(again, open))
        {
            Opened = again;
            ShowReadings();
        }
        NameDiskDrivesAgain();
        _ = LoadGroupFiguresAsync(groups);
        if (_openKey is { } key) OpenKey(key);
    }

    // Each drive's figures read from its daily rows, kept across rebuilds (the drives are sent again every 10 seconds).
    private readonly Dictionary<string, (Core.Reports.WearRow? Wear, double? Week)> _figures = [];

    private DriveGroup Keep(DriveGroup g)
    {
        if (_figures.TryGetValue(g.Id, out var f)) { g.Wear = f.Wear; g.WrittenWeek = f.Week; }
        return g;
    }

    private DateTime _figuresRead;

    /// <summary>Each drive's pace of wear and what it wrote this week, read again at most every ten minutes.</summary>
    private async Task LoadGroupFiguresAsync(List<DriveGroup> groups)
    {
        if (DateTime.Now - _figuresRead < TimeSpan.FromMinutes(10) && groups.All(g => _figures.ContainsKey(g.Id))) return;
        _figuresRead = DateTime.Now;
        var days = await reports.DriveHealthDaysAsync(8) ?? [];
        foreach (var g in groups.Where(g => g.Health?.Key is not null))
        {
            var wear = await reports.DriveWearAsync(g.Health!.Key!, g.Name);
            var mine = days.Where(d => d.Drive == g.Health.Key && d.WrittenGb is not null).OrderBy(d => d.Day).ToList();
            double? week = mine.Count >= 2 && (mine[^1].Day - mine[0].Day) >= 6 * 86400 ? Math.Max(0, mine[^1].WrittenGb!.Value - mine[0].WrittenGb!.Value) : null;
            _figures[g.Id] = (wear, week);
            foreach (var shown in Looks.Concat(Inside).Concat(Others).Where(x => x.Id == g.Id)) { shown.Wear = wear; shown.WrittenWeek = week; }
            if (Opened?.Id == g.Id) { Opened.Wear = wear; Opened.WrittenWeek = week; foreach (var n in new[] { nameof(OpenedWear), nameof(OpenedTile), nameof(OpenedAgainst), nameof(HasOpenedTile), nameof(UseFullWidth), nameof(UseBesideMonths) }) OnPropertyChanged(n); }
        }
    }

    private static void Sync(ObservableCollection<DriveGroup> list, List<DriveGroup> rows)
    {
        list.Clear();
        foreach (var r in rows) list.Add(r);
    }

    // ── A drive opened ──────────────────────────────────────────────────

    /// <summary>The drive opened in full (null: the page's overview). Back returns to the overview.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDriveOpen), nameof(OpenedWear), nameof(OpenedLetters), nameof(OpenedTile), nameof(OpenedAgainst), nameof(HasOpenedTile), nameof(OpenedTabs), nameof(UseFullWidth), nameof(UseBesideMonths))] private DriveGroup? _opened;
    public bool IsDriveOpen => Opened is not null;
    public Core.Reports.WearRow? OpenedWear => Opened?.Wear;
    /// <summary>The opened drive's "Over the months" lines, and the tile's corner ("Against late September").</summary>
    public IReadOnlyList<WearTileRow> OpenedTile => Opened is { Wear: { } w, Health: { } h } ? WearTiles.Drive(w, h) : [];
    /// <summary>"Against early October": only once there is something to compare (a drive too new to judge has no corner).</summary>
    public string OpenedAgainst => Opened?.Wear is { State: not (Core.Reports.WearState.TooSoon or Core.Reports.WearState.NoReading) } w ? WearTiles.Against(w.ThenDate) : "";
    public bool HasOpenedTile => OpenedTile.Count > 0;
    public IReadOnlyList<VolumeRow> OpenedLetters => Opened?.Letters ?? [];

    /// <summary>
    /// What reads and writes it takes the card's whole width when Over the months isn't shown: a drive with no reading
    /// (a USB adapter, a network drive), or one whose months were read and have nothing to show.
    /// </summary>
    public bool UseFullWidth => Opened is { } o && (o.Health?.Key is null || o.Wear is not null && !HasOpenedTile);
    public bool UseBesideMonths => !UseFullWidth;

    // ── Every reading (approved mock-up "Every reading, made clearer", 2026-11-14) ──

    /// <summary>In plain words, WHAT TO WATCH: the counts and flags RigSight watches, each with its dot and what's usual.</summary>
    public ObservableCollection<ReadingLine> WatchReadings { get; } = [];
    /// <summary>In plain words, WEAR AND USE: the tiles' readings and the rest, each with what changed since the month-ago day.</summary>
    public ObservableCollection<ReadingLine> WearReadings { get; } = [];
    /// <summary>As the drive reports: every attribute, its ID as a number and as hex.</summary>
    public ObservableCollection<OwnReadingLine> Readings { get; } = [];
    public bool HasWatchReadings => WatchReadings.Count > 0;
    public bool HasWearReadings => WearReadings.Count > 0;
    public bool HasBothReadingGroups => HasWatchReadings && HasWearReadings;
    /// <summary>"All as usual", or "2 aren't as usual" in amber.</summary>
    [ObservableProperty] private string _watchSummary = "";
    [ObservableProperty] private bool _watchSummaryWarm;
    /// <summary>"SINCE 14 OCT" over the change column; the column isn't there on a drive's first day.</summary>
    [ObservableProperty] private string _sinceTitle = "";
    [ObservableProperty] private GridLength _sinceWidth = new(220);
    /// <summary>The drive's own list: NVMe drives give counts only, so Score, Lowest and Limit go.</summary>
    [ObservableProperty] private bool _ownNvme;
    public GridLength OwnScoreWidth => DriveReadingBrushes.ScoreWidth(OwnNvme);
    public GridLength OwnCountWidth => DriveReadingBrushes.CountWidth(OwnNvme);
    partial void OnOwnNvmeChanged(bool value) { OnPropertyChanged(nameof(OwnScoreWidth)); OnPropertyChanged(nameof(OwnCountWidth)); OnPropertyChanged(nameof(OwnFoot)); }
    /// <summary>The line under the drive's own list on how scores work.</summary>
    public string OwnFoot => Core.Storage.DriveReadings.OwnFoot(OwnNvme);

    /// <summary>"Every reading" as the drive itself names them (its numbers, scores and limits) instead of in plain words.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PlainNames))] private bool _ownNames;
    public bool PlainNames => !OwnNames;
    [RelayCommand] private void PickNames(string? which) => OwnNames = which == "own";

    /// <summary>The opened drive's own daily rows (the last 40 days), which the change column is worked out from.</summary>
    private IReadOnlyList<DriveHealthDay> _driveDays = [];

    [RelayCommand]
    private async Task OpenDrive(DriveGroup? group)
    {
        if (group is null) return;
        Opened = group;
        Log.Write("storage", $"Opened {group.Name} ({group.Letters.Count} letters, {(group.HasReading ? "with readings" : "no reading")})");
        // Its first letter is the one What fills it and Space you could free on it start on.
        if (group.MainLetter is { } letter)
        {
            if (!IsScanning) PickedRoot = group.Letters.OrderBy(l => l.Root, StringComparer.OrdinalIgnoreCase).First().Root;
            CleanupRoot = letter.Root;
        }
        _driveDays = [];
        ShowReadings();
        DriveOpened?.Invoke();
        if (group.Health?.Key is { } key)
        {
            var rows = await reports.DriveHealthDaysAsync(40) ?? [];
            if (Opened?.Health?.Key != key) return;
            _driveDays = [.. rows.Where(r => r.Drive == key)];
            ShowReadings();
        }
        await LoadDiskUseAsync();
    }

    /// <summary>A drive's notification: that drive opened, now or once the drives are in (the page may not have read them yet).</summary>
    public void OpenKey(string key)
    {
        if (Looks.Concat(Inside).Concat(Others).FirstOrDefault(g => g.Health?.Key == key) is { } group)
        {
            _openKey = null;
            _ = OpenDrive(group);
        }
        else _openKey = key;
    }

    /// <summary>A drive asked to open before the drives were in.</summary>
    private string? _openKey;

    /// <summary>A letter's Scan on the overview: its drive opens on What fills it, and the scan starts there.</summary>
    [RelayCommand]
    private async Task ScanLetter(string? root)
    {
        if (string.IsNullOrEmpty(root)) return;
        var group = Looks.Concat(Inside).Concat(Others).FirstOrDefault(g => g.Letters.Any(l => string.Equals(l.Root, root, StringComparison.OrdinalIgnoreCase)));
        if (group is not null) await OpenDrive(group);
        await Scan(root);
    }

    [RelayCommand]
    private void Back()
    {
        Opened = null;
        _ = LoadDiskUseAsync();
        DriveOpened?.Invoke();
    }

    /// <summary>The page goes to the top when a drive opens or closes.</summary>
    public event Action? DriveOpened;

    /// <summary>Both views of Every reading, built together so the switch between them is instant.</summary>
    private void ShowReadings()
    {
        WatchReadings.Clear();
        WearReadings.Clear();
        Readings.Clear();
        if (Opened?.Health is { } h)
        {
            var plain = Core.Storage.DriveReadings.Plain(h, _driveDays, DateTime.Today);
            SinceWidth = new GridLength(plain.Since is null ? 0 : 220);
            SinceTitle = plain.SinceTitle;
            foreach (var r in plain.Watch) WatchReadings.Add(new ReadingLine(r, SinceWidth, WatchReadings.Count == 0));
            foreach (var r in plain.Wear) WearReadings.Add(new ReadingLine(r, SinceWidth, WearReadings.Count == 0));
            WatchSummary = plain.Summary;
            WatchSummaryWarm = plain.NotUsual > 0;
            OwnNvme = h.IsNvme;
            foreach (var a in Core.Storage.DriveReadings.Own(h, plain)) Readings.Add(new OwnReadingLine(a, h.IsNvme));
        }
        OnPropertyChanged(nameof(HasWatchReadings));
        OnPropertyChanged(nameof(HasWearReadings));
        OnPropertyChanged(nameof(HasBothReadingGroups));
    }

    // ── What reads and writes ───────────────────────────────────────────

    /// <summary>Day (the last 24 hours), Week, Month or Custom (days picked in the shared calendar, 1.23.0).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UseDay), nameof(UseWeek), nameof(UseMonth), nameof(UseCustom))] private string _usePeriod = "Week";
    public bool UseDay => UsePeriod == "Day";
    public bool UseWeek => UsePeriod == "Week";
    public bool UseMonth => UsePeriod == "Month";
    public bool UseCustom => UsePeriod == "Custom";
    /// <summary>The Needs a look cards a row: as many as there are, up to three, so one card never leaves a half row empty.</summary>
    public int LooksColumns => Math.Clamp(Looks.Count, 1, 3);
    partial void OnUsePeriodChanged(string value) => _ = LoadDiskUseAsync();
    [RelayCommand] private void PickUsePeriod(string? period) { if (period is "Day" or "Week" or "Month") UsePeriod = period; }

    // Custom, as on every other look-back (Processes' opened app, the pages' pickers): the same day calendar, whose first
    // day is the first one with reads and writes on record.

    /// <summary>The first day with any app's reads and writes on record (read with the list); null before it's known.</summary>
    public DateTime? FirstDay { get; private set; }
    /// <summary>The days picked, both in it (the last 7 days until some are).</summary>
    public DateTime CustomFrom { get; private set; } = DateTime.Today.AddDays(-6);
    public DateTime CustomTo { get; private set; } = DateTime.Today;

    /// <summary>Days picked in the calendar: the list is read for them.</summary>
    public void PickCustom(DateTime a, DateTime b)
    {
        (CustomFrom, CustomTo) = a <= b ? (a.Date, b.Date) : (b.Date, a.Date);
        if (UseCustom) _ = LoadDiskUseAsync();
        else UsePeriod = "Custom";
    }

    /// <summary>Every app that read or wrote in the period (1.23.0: all of them, in a box that scrolls), in the order picked, as searched.</summary>
    /// <summary>
    /// The apps in the list, sorted and searched. Replaced whole (1.23.0): it holds every app now, a few hundred, and adding
    /// them one by one to a collection the list watches cost 100-170 ms on the window's thread, which landed on the next
    /// page switch when the history came in late (seen at the 1.23.0 gate, Storage to Home 167 ms).
    /// </summary>
    [ObservableProperty] private IReadOnlyList<DiskUseRow> _diskUse = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DiskUseLoading))] private bool _diskUseRead;
    public bool DiskUseEmpty => DiskUseRead && _diskRows.Count == 0;
    /// <summary>Something is searched for and no app in the period matches.</summary>
    public bool DiskUseNoneMatch => DiskUseRead && _diskRows.Count > 0 && DiskUse.Count == 0;
    /// <summary>The list hasn't been read yet: the card shows the spinner.</summary>
    public bool DiskUseLoading => !DiskUseRead;
    /// <summary>How many apps read or wrote in the period, at the list's top right ("328 apps", whatever is searched); none while it's empty.</summary>
    public string DiskUseCount => _diskRows.Count switch { 0 => "", 1 => Lang.T("1 app"), int n => Lang.T($"{n} apps") };
    /// <summary>Under the opened drive's list: its writes this week against its usual week, when known.</summary>
    [ObservableProperty] private string _diskUseNote = "";
    /// <summary>What's typed in the card's search box: the list keeps the apps whose name has every word.</summary>
    [ObservableProperty] private string _diskUseSearch = "";
    private string[] _diskWords = [];

    partial void OnDiskUseSearchChanged(string value)
    {
        _diskWords = TextMatch.Words(value);
        FillDiskUse();
    }

    /// <summary>The list's column headings, each a button that sorts by it: TOTAL (first), READ, WRITTEN.</summary>
    public SortHeading DiskTotalHeading { get; } = new("Total", Lang.T("TOTAL")) { IsActive = true, Arrow = "↓" };
    public SortHeading DiskReadHeading { get; } = new("Read", Lang.T("READ"));
    public SortHeading DiskWrittenHeading { get; } = new("Written", Lang.T("WRITTEN"));
    private string _diskSort = "Total";
    private List<DiskUseRow> _diskRows = [];
    private int _diskReads;

    /// <summary>A heading clicked: the list sorts by it, most first (the page keeps the pick while it's open).</summary>
    [RelayCommand]
    private void SortDiskUse(string? key)
    {
        if (key is not ("Total" or "Read" or "Written") || key == _diskSort) return;
        _diskSort = key;
        foreach (var h in new[] { DiskTotalHeading, DiskReadHeading, DiskWrittenHeading })
        {
            h.IsActive = h.Key == key;
            h.Arrow = h.IsActive ? "↓" : "↕";
        }
        FillDiskUse();
    }

    /// <summary>A line on more than one drive clicked: it opens to its split by drive, or closes.</summary>
    [RelayCommand]
    private static void ToggleDiskUse(DiskUseRow? row)
    {
        if (row is { CanOpen: true }) row.IsOpen = !row.IsOpen;
    }

    /// <param name="again">
    /// False when the page is only shown again: the same period and drive read in the last 2 minutes keep their rows. The
    /// list holds every app (a few hundred) and reading it again on each visit rebuilt the rows in view, about 140 ms on the
    /// window's thread at each switch to Storage (the 1.23.0 gate, 6 to 10 switches over 100 ms). Reads and writes are kept
    /// by the hour, so two minutes lose nothing.
    /// </param>
    private async Task LoadDiskUseAsync(bool again = true)
    {
        int days = UsePeriod switch { "Day" => 1, "Month" => 30, _ => 7 };
        var span = UseCustom ? Core.Storage.AppDrives.Span(CustomFrom, CustomTo) : Core.Storage.AppDrives.Span(days, DateTime.Now);
        var opened = Opened;
        string? disk = opened?.Health?.Key;
        string key = $"{(UseCustom ? $"{CustomFrom:yyyy-MM-dd}~{CustomTo:yyyy-MM-dd}" : days.ToString())}|{disk}";
        if (!again && DiskUseRead && key == _diskKey && DateTime.UtcNow - _diskReadAt < TimeSpan.FromMinutes(2)) return;
        int read = ++_diskReads;
        var result = await reports.DiskUseAsync(span, disk);
        if (read != _diskReads) return; // a newer period or drive was asked for meanwhile
        _diskPeriod = UseCustom ? $"{CustomFrom:yyyy-MM-dd} to {CustomTo:yyyy-MM-dd}" : $"{days} days";
        if (result is null) Log.Write("Storage", $"what reads and writes ({_diskPeriod}, drive {disk ?? "all"}) couldn't be read; the list shows nothing");
        if (result?.FirstDay is { } first) FirstDay = first;
        _diskLines = result?.Lines ?? [];
        _diskKey = key;
        _diskReadAt = DateTime.UtcNow;
        _diskPageWide = opened is null;
        BuildDiskRows();
        DiskUseRead = true;
        DiskUseNote = Opened?.WrittenWeek is double gb ? Lang.T($"Written in the last 7 days {Units.DiskBytes(gb * 1e9)}.") : "";
    }

    // The lines as read, for the page-wide list or an opened drive's, and the drives on record none of the page's drives
    // answered to when they were named (gone, or the drives not sent by the agent yet).
    private List<DiskUseLine> _diskLines = [];
    private string _diskKey = "";
    private DateTime _diskReadAt;
    private bool _diskPageWide = true;
    private string _diskPeriod = "";
    private HashSet<string> _diskUnknown = [];

    /// <summary>
    /// The lines from what was read: on the page-wide list each names its drives and, on more than one, opens to them (a
    /// line open stays open); an opened drive's list is that drive's alone, with neither.
    /// </summary>
    private void BuildDiskRows()
    {
        // The bars are sized against the line with the most in total, whatever the list is sorted by, so a sort keeps each bar.
        double top = _diskLines.Count == 0 ? 1 : Math.Max(1, _diskLines.Max(l => l.Total));
        var open = _diskRows.Where(r => r.IsOpen).Select(r => r.Name).ToHashSet();
        var names = new Dictionary<string, DriveName>();
        DriveName Of(string key) => names.TryGetValue(key, out var n) ? n : names[key] = DriveLabel(key);
        _diskRows = [.. _diskLines.Select(l =>
        {
            bool page = _diskPageWide && l.Drives.Count > 0;
            string caption = page ? Core.Storage.AppDrives.Caption(l.Drives.Select(d => Of(d.Disk).Said)) : "";
            double own = Math.Max(1, l.Total);
            List<DiskDriveRow> split = page && l.Drives.Count > 1
                ? [.. l.Drives.Select(d => { var n = Of(d.Disk); return new DiskDriveRow(n.Chip, n.HasLetter, n.Name, d.Read / own, d.Write / own, d.Read, d.Write); })]
                : [];
            return new DiskUseRow(l.Name, l.IsWindows, l.Path, l.Read / top, l.Write / top, l.Read, l.Write, caption, split) { IsOpen = split.Count > 1 && open.Contains(l.Name) };
        })];
        _diskUnknown = [.. names.Where(n => !n.Value.Known).Select(n => n.Key)];
        if (_diskUnknown.Count > 0)
            Log.Write("Storage", $"what reads and writes ({_diskPeriod}): {_diskUnknown.Count} of {names.Count} drives on record aren't among the drives now ({Live.DriveHealth.Count} sent by the agent); said by model or as another drive");
        FillDiskUse();
    }

    /// <summary>The drives were sent again: a drive the list couldn't name a moment ago is named now (no read of the history).</summary>
    private void NameDiskDrivesAgain()
    {
        if (_diskPageWide && _diskUnknown.Count > 0 && _diskUnknown.Any(k => DriveOf(k) is not null)) BuildDiskRows();
    }

    private void FillDiskUse()
    {
        Func<DiskUseRow, long> key = _diskSort switch { "Read" => r => r.ReadBytes, "Written" => r => r.WriteBytes, _ => r => r.TotalBytes };
        DiskUse = [.. _diskRows.OrderByDescending(key).ThenByDescending(r => r.TotalBytes)
            .Where(r => _diskWords.Length == 0 || TextMatch.Has(_diskWords, r.Name))];
        OnPropertyChanged(nameof(DiskUseCount));
        OnPropertyChanged(nameof(DiskUseEmpty));
        OnPropertyChanged(nameof(DiskUseNoneMatch));
    }

    // ── Space you could free, one line a letter (the overview) ──────────

    public ObservableCollection<CleanupLine> CleanupLines { get; } = [];

    /// <summary>Each fixed letter's space to free in a line: what it is and how much, or that it isn't scanned yet.</summary>
    private void BuildCleanupLines()
    {
        CleanupLines.Clear();
        foreach (var v in Volumes.OrderBy(v => v.Root, StringComparer.OrdinalIgnoreCase))
        {
            // Every drive's Recycle Bin has its own line above these (StorageViewModel.Bin.cs), so it isn't counted again here.
            var items = ItemsFor(v.Root).Where(i => !(i.IsRecycleBin && i.BinRoot is null)).ToList();
            if (items.Count > 0)
            {
                var names = items.Select(i => i.Title).ToList();
                string what = names.Count switch
                {
                    1 => names[0],
                    2 => Lang.T($"{names[0]} and {names[1]}"),
                    _ => Lang.T($"{names[0]}, {names[1]} and {names.Count - 2} more"),
                };
                CleanupLines.Add(new CleanupLine(v.Root, v.Letter, what, Units.Bytes(items.Sum(i => i.Size)), false));
            }
            else if (DriveScans.Letter(v.Root) is { } l && !DriveScans.Has(l))
                CleanupLines.Add(new CleanupLine(v.Root, v.Letter, Lang.T("Not scanned yet"), "", true));
        }
        OnPropertyChanged(nameof(HasCleanupLines));
    }

    public bool HasCleanupLines => CleanupLines.Count > 0;

    /// <summary>A line of the overview's list: the drive opens on its space to free (or its scan starts).</summary>
    [RelayCommand]
    private async Task OpenCleanupLine(CleanupLine? line)
    {
        if (line is null) return;
        var group = Looks.Concat(Inside).Concat(Others).FirstOrDefault(g => g.Letters.Any(l => string.Equals(l.Root, line.Root, StringComparison.OrdinalIgnoreCase)));
        if (group is not null) await OpenDrive(group);
        CleanupRoot = line.Root;
        if (line.NeedsScan) await Scan(line.Root);
    }

    /// <summary>
    /// A drive as the page-wide list of what reads and writes says it. <see cref="Chip"/> is its letter ("E:") or, with
    /// none, its short name; <see cref="Said"/> the same in the drives under an app's name (null for a drive the page
    /// doesn't know, "another drive" there); <see cref="Name"/> beside the chip in a line's split ("Local Disk").
    /// <see cref="Known"/>: one of the page's drives now.
    /// </summary>
    private sealed record DriveName(string Chip, bool HasLetter, string Name, string? Said, bool Known);

    private DriveGroup? DriveOf(string key) => Looks.Concat(Inside).Concat(Others).FirstOrDefault(g => g.Health?.Key == key);

    /// <summary>
    /// A drive by its key: its first letter in the chip and that letter's name ("Local Disk", "Local disk" with none); a
    /// drive with no letter by its short name (a drive icon in the chip); a drive the page doesn't know now by the model in
    /// its key, or as a drive that isn't connected now.
    /// </summary>
    private DriveName DriveLabel(string key)
    {
        var group = DriveOf(key);
        if (group?.Letters.OrderBy(l => l.Root, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } letter)
            return new(letter.Letter, true, string.IsNullOrWhiteSpace(letter.Label) ? Lang.T("Local disk") : letter.Label, letter.Letter, true);
        string? name = Core.Storage.AppDrives.ShortName(group?.Name, key);
        return new(name ?? "", false, name ?? Lang.T("Not connected now"), name, group is not null);
    }
}
