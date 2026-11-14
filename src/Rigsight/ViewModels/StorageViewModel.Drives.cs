using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

// The drives as a table of physical drives, their letters under each, and a drive opened in full (wear and drive
// details, approved mock-up 2026-11-14): Needs a look as cards on top, then Inside the PC, then USB and network.
public sealed partial class StorageViewModel
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
            bool usb = h.Bus is "Usb" or "USB" || mine.Any(l => l.Kind == "usb");
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
            if (Opened?.Id == g.Id) { Opened.Wear = wear; Opened.WrittenWeek = week; foreach (var n in new[] { nameof(OpenedWear), nameof(OpenedTile), nameof(OpenedAgainst), nameof(HasOpenedTile) }) OnPropertyChanged(n); }
        }
    }

    private static void Sync(ObservableCollection<DriveGroup> list, List<DriveGroup> rows)
    {
        list.Clear();
        foreach (var r in rows) list.Add(r);
    }

    // ── A drive opened ──────────────────────────────────────────────────

    /// <summary>The drive opened in full (null: the page's overview). Back returns to the overview.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDriveOpen), nameof(OpenedWear), nameof(OpenedLetters), nameof(OpenedTile), nameof(OpenedAgainst), nameof(HasOpenedTile), nameof(OpenedTabs))] private DriveGroup? _opened;
    public bool IsDriveOpen => Opened is not null;
    public Core.Reports.WearRow? OpenedWear => Opened?.Wear;
    /// <summary>The opened drive's "Over the months" lines, and the tile's corner ("Against late September").</summary>
    public IReadOnlyList<WearTileRow> OpenedTile => Opened is { Wear: { } w, Health: { } h } ? WearTiles.Drive(w, h) : [];
    /// <summary>"Against early October": only once there is something to compare (a drive too new to judge has no corner).</summary>
    public string OpenedAgainst => Opened?.Wear is { State: not (Core.Reports.WearState.TooSoon or Core.Reports.WearState.NoReading) } w ? WearTiles.Against(w.ThenDate) : "";
    public bool HasOpenedTile => OpenedTile.Count > 0;
    public IReadOnlyList<VolumeRow> OpenedLetters => Opened?.Letters ?? [];

    public ObservableCollection<ReadingRow> Readings { get; } = [];
    /// <summary>The plain list's groups, a column each (two at the most side by side).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadingColumns))]
    private IReadOnlyList<ReadingGroup> _readingGroups = [];
    public int ReadingColumns => Math.Clamp(ReadingGroups.Count, 1, 2);
    /// <summary>"Every reading" as the drive itself names them (its numbers, scores and limits) instead of in plain words.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PlainNames))] private bool _ownNames;
    public bool PlainNames => !OwnNames;
    [RelayCommand] private void PickNames(string? which) => OwnNames = which == "own";
    partial void OnOwnNamesChanged(bool value) => ShowReadings();
    [ObservableProperty] private string _readingsThenTitle = "";
    [ObservableProperty] private bool _hasReadingsThen;

    private DriveHealthDay? _monthAgo;

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
        _monthAgo = null;
        ShowReadings();
        DriveOpened?.Invoke();
        if (group.Health?.Key is { } key)
        {
            var rows = await reports.DriveHealthDaysAsync(40) ?? [];
            _monthAgo = rows.Where(r => r.Drive == key && r.Day <= TimeUtil.ToUnix(DateTime.Today.AddDays(-28))).MaxBy(r => r.Day);
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

    private void ShowReadings()
    {
        Readings.Clear();
        if (Opened?.Health is not { } h) { HasReadingsThen = false; ReadingGroups = []; return; }
        var rows = OwnNames ? Models.DriveReadings.Own(h) : Models.DriveReadings.Plain(h, _monthAgo);
        string? group = null;
        foreach (var r in rows)
        {
            if (!OwnNames && r.Group != group) Readings.Add(new ReadingRow(r.Group, "", "", "", null) { IsHeader = true });
            group = r.Group;
            Readings.Add(r);
        }
        HasReadingsThen = !OwnNames && rows.Any(r => r.Then.Length > 0);
        ReadingGroups = OwnNames ? [] : Models.DriveReadings.Groups(rows);
        ReadingsThenTitle = _monthAgo is { } then ? Lang.T($"ON {TimeUtil.FromUnix(then.Day):d MMM}").ToUpperInvariant() : "";
    }

    // ── What reads and writes ───────────────────────────────────────────

    /// <summary>Day (the last 24 hours), Week or Month.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UseDay), nameof(UseWeek), nameof(UseMonth))] private string _usePeriod = "Week";
    public bool UseDay => UsePeriod == "Day";
    public bool UseWeek => UsePeriod == "Week";
    public bool UseMonth => UsePeriod == "Month";
    /// <summary>The Needs a look cards a row: as many as there are, up to three, so one card never leaves a half row empty.</summary>
    public int LooksColumns => Math.Clamp(Looks.Count, 1, 3);
    partial void OnUsePeriodChanged(string value) => _ = LoadDiskUseAsync();
    [RelayCommand] private void PickUsePeriod(string? period) { if (period is "Day" or "Week" or "Month") UsePeriod = period; }

    public ObservableCollection<DiskUseRow> DiskUse { get; } = [];
    [ObservableProperty] private bool _diskUseRead;
    public bool DiskUseEmpty => DiskUseRead && DiskUse.Count == 0;
    /// <summary>Under the opened drive's list: its writes this week against its usual week, when known.</summary>
    [ObservableProperty] private string _diskUseNote = "";

    private async Task LoadDiskUseAsync()
    {
        int days = UsePeriod switch { "Day" => 1, "Month" => 30, _ => 7 };
        string? disk = Opened?.Health?.Key;
        var lines = await reports.DiskUseAsync(days, disk, Opened is null ? 5 : 6) ?? [];
        DiskUse.Clear();
        double top = lines.Count == 0 ? 1 : Math.Max(1, lines.Max(l => l.Total));
        foreach (var l in lines)
        {
            string letter = Opened is null && LetterOf(l.MainDisk) is { } x ? $" · {x}" : "";
            string figure = Units.DiskBytes(l.Total) + letter;
            string tip = Lang.T($"Read {Units.DiskBytes(l.Read)}, written {Units.DiskBytes(l.Write)}");
            DiskUse.Add(new DiskUseRow(l.Name, l.IsWindows, l.Read / top, l.Write / top, figure, tip));
        }
        DiskUseRead = true;
        OnPropertyChanged(nameof(DiskUseEmpty));
        DiskUseNote = Opened?.WrittenWeek is double gb ? Lang.T($"Written in the last 7 days {Units.DiskBytes(gb * 1e9)}.") : "";
    }

    // ── Space you could free, one line a letter (the overview) ──────────

    public ObservableCollection<CleanupLine> CleanupLines { get; } = [];

    /// <summary>Each fixed letter's space to free in a line: what it is and how much, or that it isn't scanned yet.</summary>
    private void BuildCleanupLines()
    {
        CleanupLines.Clear();
        foreach (var v in Volumes.OrderBy(v => v.Root, StringComparer.OrdinalIgnoreCase))
        {
            var items = ItemsFor(v.Root);
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

    /// <summary>The first letter of a drive by its key ("E"), for a line of the page-wide list.</summary>
    private string? LetterOf(string? key) =>
        key is null ? null : Looks.Concat(Inside).Concat(Others).FirstOrDefault(g => g.Health?.Key == key)?.Letters.OrderBy(l => l.Root).FirstOrDefault()?.Root.TrimEnd('\\', ':');
}
