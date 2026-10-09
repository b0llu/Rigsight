using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A fact under the live speeds: what it is, its value, and a note.</summary>
public sealed record NetFact(string Label, string Value, string Note = "");

/// <summary>An app using the internet right now, with its speeds.</summary>
public sealed partial class NetLiveRow : ObservableObject
{
    public required string Exe { get; init; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _path;
    [ObservableProperty] private string _downText = "";
    [ObservableProperty] private string _upText = "";
}

/// <summary>One app in the period's list: what it moved, how much of it in the background, and (opened) when and how.</summary>
public sealed partial class NetAppRow : ObservableObject
{
    public required NetAppStat Stat { get; set; }
    public string Name => Stat.Name;
    public string? Path => Stat.Path;
    public string CategoryText => AppCatalog.Label(Stat.Category);

    /// <summary>What the row's numbers count: all of it, or only what moved while it wasn't in front.</summary>
    public required bool BackgroundOnly { get; set; }
    public long Down => BackgroundOnly ? Stat.Use.BgDown + Stat.Use.AwayDown : Stat.Use.Down;
    public long Up => BackgroundOnly ? Stat.Use.BgUp + Stat.Use.AwayUp : Stat.Use.Up;
    public string DownText => Units.Data(Down);
    public string UpText => Units.Data(Up);

    /// <summary>The figure the list is sorted by, as the row's one big number.</summary>
    public required string Amount { get; set; }
    /// <summary>The colour the app has on the chart (a brush's name), or the one all other apps share.</summary>
    public required string DotKey { get; set; }
    /// <summary>"↓ 3.1 GB   ↑ 102 MB   12% in the background". Nothing about the background means none of it was.</summary>
    public string SubText => $"↓ {DownText}   ↑ {UpText}" + (BackgroundOnly ? "   background only"
        : Stat.Use.Background >= 1 << 20 && BackgroundPercent >= 1 ? $"   {BackgroundPercent}% in the background" : "");

    /// <summary>The bars' lengths, against the biggest app in the list (0–100).</summary>
    public double DownBar { get; set; }
    public double UpBar { get; set; }

    /// <summary>The row was given new figures: everything it shows is read again (the row itself stays where it is).</summary>
    public void Changed() => OnPropertyChanged(string.Empty);

    public int BackgroundPercent => Stat.Use.Total > 0 ? (int)Math.Round(100.0 * Stat.Use.Background / Stat.Use.Total) : 0;
    public string BackgroundText => $"{BackgroundPercent}% background";

    [ObservableProperty] private bool _isOpen;

    // Opened: in front, in the background, and while nobody was there (shares of all it moved, both ways).
    public double FrontShare => Share(Stat.Use.Total - Stat.Use.Background);
    public double BgShare => Share(Stat.Use.BgDown + Stat.Use.BgUp);
    public double AwayShare => Share(Stat.Use.AwayDown + Stat.Use.AwayUp);
    public string FrontText => $"In front {Percent(FrontShare)}";
    public string BgText => $"Background {Percent(BgShare)}";
    public string AwayText => $"While you were away {Percent(AwayShare)}";

    public required string BusiestLabel { get; set; }
    public required string BusiestText { get; set; }
    public string LocalText => Stat.Use.Lan > 0 ? Units.Data(Stat.Use.Lan) : "None";

    private double Share(long part) => Stat.Use.Total > 0 ? Math.Clamp((double)part / Stat.Use.Total, 0, 1) : 0;
    private static string Percent(double share) => $"{Math.Round(share * 100):0}%";
}

/// <summary>A colour of the chart and what it stands for.</summary>
public sealed record NetLegend(string BrushKey, string Text, double Opacity = 1);

/// <summary>Something big that came in in one go, as the card lists it.</summary>
public sealed record NetDownloadRow(string Name, string? Path, string When, string How, string Size);

/// <summary>A quarter hour of the connection strip: online, a drop, or nothing (the PC was off, or it's still to come).</summary>
public enum NetQuarter { Online, Dropped, None, Unrecorded }

/// <summary>A quarter hour on the strip and what hovering it says ("2:00 – 2:15 PM · Online").</summary>
public sealed record NetStripCell(NetQuarter State, string Tip);

/// <summary>
/// The Network page: the internet right now (speeds, the connection, who's using it), then the period's use: totals, a
/// chart, every app with its background share, what moved while nobody was there, big downloads and drops, and the
/// period's network insights on top.
/// </summary>
public sealed partial class NetworkViewModel : ObservableObject
{
    // ── The period (Day, Week, Month or Year; today unless picked otherwise) ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDay), nameof(RangeNote), nameof(PeriodWord))]
    private ReportRange _unit = ReportRange.Day;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote), nameof(PeriodWord))]
    private DateTime _anchor = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote))]
    private DateTime? _firstDay;

    [ObservableProperty] private DateTime _customFrom;
    [ObservableProperty] private DateTime _customTo;

    private (DateTime From, DateTime To) Period => ReportBuilder.Bounds(Unit, Anchor);
    public bool IsDay => Unit == ReportRange.Day;
    public bool IncludesToday => Period.To > DateTime.Today && Period.From <= DateTime.Today;
    public string RangeNote => Controls.PeriodPicker.Span(Unit, Anchor, FirstDay);

    /// <summary>"today", "this week", "Sep 2026"… for the labels that follow the period.</summary>
    public string PeriodWord => Controls.PeriodPicker.Text(Unit, Anchor) switch
    {
        "Today" => "today",
        "Yesterday" => "yesterday",
        "This week" => "this week",
        "Last week" => "last week",
        "This month" => "this month",
        "Last month" => "last month",
        "This year" => "this year",
        var other => other,
    };

    partial void OnUnitChanged(ReportRange value) { SelectedBin = -1; _ = RefreshAsync(); }
    partial void OnAnchorChanged(DateTime value) { SelectedBin = -1; _ = RefreshAsync(); }

    /// <summary>Midnight passed with the window open (see <see cref="Controls.PeriodPicker.AfterMidnight"/>).</summary>
    public void NewDay(DateTime was)
    {
        _loadedPast = null; // "Yesterday" is read against the new day
        var anchor = Controls.PeriodPicker.AfterMidnight(Unit, Anchor, was, DateTime.Today);
        if (anchor != Anchor) { Anchor = anchor; return; }
        OnPropertyChanged(nameof(RangeNote));
        OnPropertyChanged(nameof(PeriodWord));
        _ = RefreshAsync();
    }

    private readonly ReportService _reports;
    private int _load;
    private (ReportRange Unit, DateTime From)? _loadedPast;

    public NetworkViewModel(ReportService reports, LiveData live)
    {
        _reports = reports;
        Live = live;
        live.PropertyChanged += OnLiveChanged;
    }

    public LiveData Live { get; }

    [ObservableProperty] private bool _loaded;

    /// <summary>Anything recorded for the period: the page's history shows, or a line saying why not.</summary>
    [ObservableProperty] private bool _hasData;

    // ── Right now ──

    [ObservableProperty] private bool _hasLive;

    /// <summary>The agent isn't getting network events from Windows and couldn't get them back: nothing is recorded for now.</summary>
    [ObservableProperty] private bool _stalled;
    [ObservableProperty] private string _downNow = "—";
    [ObservableProperty] private string _upNow = "—";
    public ObservableCollection<NetLiveRow> UsingNow { get; } = [];
    [ObservableProperty] private bool _nobodyNow;

    /// <summary>The slim line's last part: the apps moving the most right now ("Google Chrome, Discord, Steam").</summary>
    [ObservableProperty] private string _usingNowText = "";

    /// <summary>Whether the live part is open under its line: the minute's chart, the connection, each app's speed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveButtonText), nameof(LiveGlyph))]
    private bool _showLive;
    public string LiveButtonText => ShowLive ? "Hide live" : "Show live";
    /// <summary>The arrow at the line's end: down to open, up to close (icon font).</summary>
    public string LiveGlyph => ShowLive ? "\uE70E" : "\uE70D";

    [RelayCommand]
    private void ToggleLive() => ShowLive = !ShowLive;

    /// <summary>The connection: Ethernet or Wi-Fi, its link, any VPN, the PC's address, whether the internet is there.</summary>
    [ObservableProperty] private IReadOnlyList<NetFact> _facts = [];
    [ObservableProperty] private string _subtitle = "";

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LiveData.Tick) || !Loaded) return;
        UpdateLive();
    }

    private void UpdateLive()
    {
        var net = Live.Net;
        HasLive = net is not null;
        Stalled = net?.Stalled == true;
        if (net is null) return;
        DownNow = Units.Speed(net.Down);
        UpNow = Units.Speed(net.Up);
        // Rows kept where they are when the same apps stay busy (no flicker each second), re-made when they change.
        var apps = net.Apps.Where(a => a.Down + a.Up >= 1024).ToList();
        if (!apps.Select(a => a.Exe).SequenceEqual(UsingNow.Select(r => r.Exe)))
        {
            UsingNow.Clear();
            foreach (var a in apps) UsingNow.Add(new NetLiveRow { Exe = a.Exe });
        }
        for (int i = 0; i < apps.Count; i++)
        {
            var (row, a) = (UsingNow[i], apps[i]);
            row.Name = a.Name;
            row.Path = a.Path;
            row.DownText = "↓ " + Units.Speed(a.Down);
            row.UpText = "↑ " + Units.Speed(a.Up);
        }
        NobodyNow = UsingNow.Count == 0;
        UsingNowText = string.Join(", ", apps.Take(3).Select(a => a.Name));
    }

    private async Task ReadConnectionAsync(NetReport? report)
    {
        // Asking Windows about every adapter takes a moment on a PC with many (VPNs, virtual machines): not on the window's thread.
        var (c, online) = await Task.Run(() => (NetAdapters.Current(), NetAdapters.WindowsSeesInternet()));
        Subtitle = c is null ? "Not connected" : string.Join(" · ", new[] { c.Kind, c.Adapter, c.Vpn is null ? null : $"through {c.Vpn}" }.OfType<string>());
        Facts = Models.Kept.Or(Facts,
        [
            new("Connection", c?.Kind ?? "None"),
            new("Link speed", c is { LinkBitsPerSecond: > 0 } ? Units.Mbps(c.LinkBitsPerSecond / 8.0) : "—"),
            new("VPN", c?.Vpn ?? "Off"),
            new("IP address", c?.IPv4 ?? "—"),
            new("Internet", online switch { true => "Connected", false => "Not reachable", _ => "—" }),
            new("Local network", report is { HasData: true } ? Units.Data(report.Lan) : "—", PeriodWord),
        ]);
    }

    // ── The period ──

    [ObservableProperty] private IReadOnlyList<Insight> _insights = [];

    [ObservableProperty] private string _downText = "—";
    [ObservableProperty] private string _downNote = "";
    [ObservableProperty] private string _upText = "—";
    [ObservableProperty] private string _upNote = "";
    [ObservableProperty] private string _backgroundText = "—";
    [ObservableProperty] private string _backgroundNote = "";
    [ObservableProperty] private string _totalText = "—";
    [ObservableProperty] private string _totalNote = "";

    [ObservableProperty] private IReadOnlyList<NetBin> _bins = [];
    [ObservableProperty] private string _chartTitle = "Usage each hour";

    /// <summary>How the chart's bars are split: by app, or by in front and background.</summary>
    [ObservableProperty] private string _chartMode = "App";
    partial void OnChartModeChanged(string value) => FillLegend();

    /// <summary>The apps with a colour of their own on the chart, and every app's name for its hover box.</summary>
    [ObservableProperty] private IReadOnlyList<long> _colorApps = [];
    [ObservableProperty] private IReadOnlyDictionary<long, string> _appNames = new Dictionary<long, string>();
    [ObservableProperty] private IReadOnlyList<NetLegend> _legend = [];

    /// <summary>The bar picked on the chart (-1: none): the apps list then shows that hour's (or day's) apps.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private int _selectedBin = -1;
    public bool HasSelection => SelectedBin >= 0;
    partial void OnSelectedBinChanged(int value) => FillApps();

    [RelayCommand]
    private void ClearSelection() => SelectedBin = -1;

    [ObservableProperty] private string _appsTitle = "Apps";

    /// <summary>What the apps list is sorted by, and what each row's figure is: Total, Download, Upload or Background.</summary>
    [ObservableProperty] private string _sort = "Total";
    partial void OnSortChanged(string value)
    {
        if (BackgroundOnly != (value == "Background")) BackgroundOnly = value == "Background";
        else FillApps();
    }

    /// <summary>Whether the apps list counts only what moved in the background (the Background sort).</summary>
    [ObservableProperty] private bool _backgroundOnly;
    partial void OnBackgroundOnlyChanged(bool value)
    {
        if (value != (Sort == "Background")) Sort = value ? "Background" : "Total";
        else FillApps();
    }

    public ObservableCollection<NetAppRow> Apps { get; } = [];
    [ObservableProperty] private bool _hasApps;

    [ObservableProperty] private string _downloadsTitle = "Biggest hours";
    [ObservableProperty] private IReadOnlyList<NetDownloadRow> _downloads = [];
    public bool HasDownloads => Downloads.Count > 0;
    partial void OnDownloadsChanged(IReadOnlyList<NetDownloadRow> value) => OnPropertyChanged(nameof(HasDownloads));

    [ObservableProperty] private string _dropsTitle = "";
    [ObservableProperty] private string _dropsNote = "";
    [ObservableProperty] private bool _dropsWarn;
    [ObservableProperty] private IReadOnlyList<NetStripCell> _strip = [];

    private NetReport? _report;

    /// <summary>The period is being read (the picker says so when it takes a moment).</summary>
    [ObservableProperty] private bool _isLoading;

    public async Task RefreshAsync()
    {
        var (from, _) = Period;
        // A past period is read once; the minute refresh only moves the live parts then.
        if (!IncludesToday && _loadedPast == (Unit, from) && _report is not null)
        {
            _load++; // a read still on its way is for a period since left: it mustn't land on this one
            IsLoading = false;
            await ReadConnectionAsync(_report);
            return;
        }
        int id = ++_load;
        IsLoading = true;
        var (report, firstDay) = await _reports.NetworkAsync(Unit, Anchor);
        if (id != _load) return;
        IsLoading = false;
        _report = report;
        _loadedPast = IncludesToday ? null : (Unit, from);
        FirstDay = firstDay ?? DateTime.Today;
        Loaded = true;
        UpdateLive();
        Fill(report);
        await ReadConnectionAsync(report);
    }

    private void Fill(NetReport? r)
    {
        HasData = r is { HasData: true };
        if (r is null || !r.HasData)
        {
            Insights = [];
            Apps.Clear();
            HasApps = false;
            Bins = [];
            SelectedBin = -1;
            Downloads = [];
            Strip = [];
            return;
        }
        bool now = IncludesToday;
        Insights = Models.Kept.Or(Insights, [.. r.Insights.OrderByDescending(i => i.Priority)]);

        TotalText = Units.Data(r.Down + r.Up);
        TotalNote = r.Apps.Count == 1 ? "download and upload · 1 app" : $"download and upload · {r.Apps.Count} apps";
        DownText = Units.Data(r.Down);
        DownNote = r.Range == ReportRange.Day && r.UsualDayDown is long usual
            ? $"vs {Units.Data(usual)} on a usual day"
            : r.PreviousDown is long before ? Against(r.Down, before, LastWord(r.Range, now)) : "";
        UpText = Units.Data(r.Up);
        var topUp = r.Apps.Where(a => a.Use.Up > 0).MaxBy(a => a.Use.Up);
        UpNote = topUp is null ? "" : $"{topUp.Name} the most";
        BackgroundText = Units.Data(r.Background);
        long away = r.AwayDown + r.AwayUp;
        BackgroundNote = r.Down + r.Up > 0
            ? $"{Math.Round(100.0 * r.Background / (r.Down + r.Up)):0}% of all use" + (away >= 50 << 20 ? $" · {Units.Data(away)} while you were away" : "")
            : "";

        Bins = Models.Kept.Or(Bins, r.Bins);
        if (SelectedBin >= Bins.Count) SelectedBin = -1;
        ChartTitle = r.Range switch
        {
            ReportRange.Day => "Usage each hour",
            ReportRange.Year => "Usage each month",
            _ => "Usage each day",
        };
        ColorApps = Models.Kept.Or(ColorApps, r.ColorApps);
        var names = r.Apps.ToDictionary(a => a.Id, a => a.Name);
        if (!names.OrderBy(kv => kv.Key).SequenceEqual(AppNames.OrderBy(kv => kv.Key))) AppNames = names;
        FillLegend();

        FillApps();

        // Each app's biggest hours (a day) or days (longer), everything it moved then.
        DownloadsTitle = r.Range == ReportRange.Day ? $"Biggest hours {PeriodWord}" : $"Biggest days {PeriodWord}";
        Downloads = Models.Kept.Or(Downloads, [.. r.Downloads.Select(d => new NetDownloadRow(d.App, d.Path,
            r.Range == ReportRange.Day ? $"{d.Start:h tt} – {d.End:h tt}" : d.Start.ToString("ddd d MMM"), "", Units.Data(d.Bytes)))]);

        FillDrops(r);
    }

    /// <summary>The colours' names under the chart: the apps with their own, or in front and background.</summary>
    private void FillLegend()
    {
        IReadOnlyList<NetLegend> legend = ChartMode == "Background"
            ? [new("CoolBrush", "The app in front"), new("CoolBrush", "In the background, or while you were away", 0.45)]
            : [.. ColorApps.Take(Controls.NetBarsChart.AppBrushKeys.Length).Select((id, i) => new NetLegend(Controls.NetBarsChart.AppBrushKeys[i], AppNames.GetValueOrDefault(id, "Unknown")))
                    .Where(l => l.Text != "Unknown"),
               new(Controls.NetBarsChart.OtherBrushKey, "Every other app")];
        if (Bins.Any(b => b.UnrecordedMinutes >= Controls.NetBarsChart.MinUnrecorded)) legend = [.. legend, new NetLegend("MutedBrush", "Not recorded", 0.7)];
        Legend = Models.Kept.Or(Legend, legend);
    }

    private string DotKey(long app) => ColorApps.ToList().IndexOf(app) is int i and >= 0 && i < Controls.NetBarsChart.AppBrushKeys.Length
        ? Controls.NetBarsChart.AppBrushKeys[i] : Controls.NetBarsChart.OtherBrushKey;

    private void FillApps()
    {
        if (_report is not { } r)
        {
            Apps.Clear();
            return;
        }
        var open = _openApp;
        // The whole period, or only the bar picked on the chart (each app as it was in that hour or day).
        var picked = SelectedBin >= 0 && SelectedBin < r.Bins.Count ? r.Bins[SelectedBin] : null;
        AppsTitle = picked is null ? "Apps" : "Apps · " + (r.Range switch
        {
            ReportRange.Day => $"{picked.Start:h tt} – {picked.Start.AddHours(1):h tt}",
            ReportRange.Year => picked.Start.ToString("MMMM"),
            _ => picked.Start.ToString("ddd d MMM"),
        });
        var byId = r.Apps.ToDictionary(a => a.Id);
        IEnumerable<NetAppStat> source = picked is null ? r.Apps
            : picked.Apps.Where(u => byId.ContainsKey(u.App)).Select(u => byId[u.App] is var whole
                ? new NetAppStat { Id = whole.Id, Exe = whole.Exe, Name = whole.Name, Path = whole.Path, Category = whole.Category, Use = u, BusiestAt = whole.BusiestAt, BusiestBytes = whole.BusiestBytes }
                : null!);
        var stats = source.Select(a => (Stat: a, Down: BackgroundOnly ? a.Use.BgDown + a.Use.AwayDown : a.Use.Down, Up: BackgroundOnly ? a.Use.BgUp + a.Use.AwayUp : a.Use.Up))
            .Select(x => (x.Stat, x.Down, x.Up, Key: Sort switch { "Download" => x.Down, "Upload" => x.Up, _ => x.Down + x.Up }))
            .Where(x => x.Key >= 1 << 20).OrderByDescending(x => x.Key).ToList();
        double max = stats.Count > 0 ? stats.Max(x => x.Key) : 1;
        // An app still at its place in the list keeps its row and is given the new figures: a list made anew has every
        // row of it built and laid out again, on each visit and each minute.
        for (int i = 0; i < stats.Count; i++)
        {
            var (stat, down, up, key) = stats[i];
            // The bar is as long as the figure sorted by, in the download's and the upload's colours as they share it.
            double length = key / max * 100, both = Math.Max(1, Sort == "Download" ? down : Sort == "Upload" ? up : down + up);
            var row = new NetAppRow
            {
                Stat = stat, BackgroundOnly = BackgroundOnly,
                DownBar = Sort == "Upload" ? 0 : length * down / both, UpBar = Sort == "Download" ? 0 : length * up / both,
                Amount = Units.Data(key), DotKey = DotKey(stat.Id),
                BusiestLabel = r.Range == ReportRange.Day ? "Busiest hour" : "Busiest day",
                BusiestText = stat.BusiestAt is { } at
                    ? $"{(r.Range == ReportRange.Day ? $"{at:h tt} – {at.AddHours(1):h tt}" : $"{at:ddd d MMM}")} · {Units.Data(stat.BusiestBytes)}"
                    : "—",
                IsOpen = stat.Id == open,
            };
            if (i >= Apps.Count) Apps.Add(row);
            else if (Apps[i] is { } kept && kept.Stat.Id == stat.Id)
            {
                if (Models.Kept.Values(kept.Stat, stat) && kept.BackgroundOnly == row.BackgroundOnly && kept.DownBar == row.DownBar && kept.UpBar == row.UpBar
                    && kept.Amount == row.Amount && kept.DotKey == row.DotKey
                    && kept.BusiestLabel == row.BusiestLabel && kept.BusiestText == row.BusiestText && kept.IsOpen == row.IsOpen) continue;
                (kept.Stat, kept.BackgroundOnly, kept.DownBar, kept.UpBar, kept.BusiestLabel, kept.BusiestText) = (stat, row.BackgroundOnly, row.DownBar, row.UpBar, row.BusiestLabel, row.BusiestText);
                (kept.Amount, kept.DotKey) = (row.Amount, row.DotKey);
                kept.IsOpen = row.IsOpen;
                kept.Changed();
            }
            else Apps[i] = row;
        }
        while (Apps.Count > stats.Count) Apps.RemoveAt(Apps.Count - 1);
        HasApps = Apps.Count > 0;
    }

    private long? _openApp;

    /// <summary>Opens a row (closing the one open), or closes it.</summary>
    [RelayCommand]
    private void Toggle(NetAppRow row)
    {
        bool open = !row.IsOpen;
        foreach (var r in Apps) r.IsOpen = false;
        row.IsOpen = open;
        _openApp = open ? row.Stat.Id : null;
    }

    private void FillDrops(NetReport r)
    {
        DropsWarn = r.Insights.Any(i => i.Key == "net-drops");
        (DropsTitle, DropsNote) = DropsText(r, PeriodWord);
        if (r.Range != ReportRange.Day || r.Quarters.Length != 96)
        {
            Strip = [];
            return;
        }
        var day = r.From.Date;
        Strip = Models.Kept.Or(Strip, [.. StripStates(r).Select((state, i) =>
        {
            var (from, to) = (day.AddMinutes(i * 15), day.AddMinutes(i * 15 + 15));
            string what = state switch
            {
                NetQuarter.Online => "Online",
                NetQuarter.Dropped => "Dropped",
                NetQuarter.Unrecorded => "Not recorded",
                _ => from > DateTime.Now ? "Still to come" : "No record",
            };
            string span = from.Hour < 12 == to.Hour < 12 ? $"{from:h:mm} – {to:h:mm tt}" : $"{from:h:mm tt} – {to:h:mm tt}";
            return new NetStripCell(state, $"{span} · {what}");
        })]);
    }

    /// <summary>The Connection card's headline and the line under it.</summary>
    internal static (string Title, string Note) DropsText(NetReport r, string periodWord)
    {
        var drops = r.Drops;
        // "No drops", never "online all day": a day still going on, or one the PC was off for half of, wasn't that.
        string title = drops.Count switch
        {
            0 => "No drops",
            1 => "Dropped once",
            2 => "Dropped twice",
            var n => $"Dropped {n} times",
        };
        string note;
        if (drops.Count > 0)
        {
            var longest = drops.MaxBy(d => d.Seconds)!;
            var at = TimeUtil.FromUnix(longest.Start);
            note = $"Longest {Took(TimeSpan.FromSeconds(longest.Seconds))} {(r.Range == ReportRange.Day ? $"at {at:h:mm tt}" : $"on {at:ddd d MMM}")}";
        }
        else note = r.Range == ReportRange.Day ? "" : $"The internet stayed up {periodWord}";
        // Time the PC was on with nothing written isn't time without drops: say how long, so "No drops" isn't read as the whole day.
        if (r.Unrecorded.Count > 0)
        {
            string missing = $"{Took(TimeSpan.FromSeconds(r.Unrecorded.Sum(u => (u.End - u.Start).TotalSeconds)))} not recorded";
            note = note.Length == 0 ? missing : $"{note} · {missing}";
        }
        return (title, note);
    }

    /// <summary>A day's 96 quarter hours: online where anything was used, then what wasn't recorded, then the drops.</summary>
    internal static NetQuarter[] StripStates(NetReport r)
    {
        var day = r.From.Date;
        var strip = new NetQuarter[96];
        for (int i = 0; i < 96; i++) strip[i] = r.Quarters[i] ? NetQuarter.Online : NetQuarter.None;
        // Every quarter hour a stretch touches, its last one too (a drop from 10:59 to 11:43 is in 11:30 to 11:45, which
        // used to be left showing online).
        void Mark(DateTime start, DateTime end, NetQuarter state, bool overOnline)
        {
            int first = (int)Math.Floor((start - day).TotalMinutes / 15), last = (int)Math.Ceiling((end - day).TotalMinutes / 15) - 1;
            for (int i = Math.Max(0, first); i <= Math.Min(95, Math.Max(first, last)); i++)
                if (overOnline || strip[i] != NetQuarter.Online) strip[i] = state;
        }
        // Not recorded only where nothing at all was: a quarter hour with any use in it is online.
        foreach (var (start, end) in r.Unrecorded) Mark(start, end, NetQuarter.Unrecorded, overOnline: false);
        foreach (var d in r.Drops) Mark(TimeUtil.FromUnix(d.Start), TimeUtil.FromUnix(d.End), NetQuarter.Dropped, overOnline: true);
        return strip;
    }

    private static string LastWord(ReportRange range, bool now) => (range switch
    {
        ReportRange.Day => "yesterday",
        ReportRange.Week => "last week",
        ReportRange.Month => "last month",
        _ => "last year",
    }) + (now ? " by now" : "");

    private static string Against(long now, long before, string last)
    {
        double ratio = (double)now / Math.Max(1, before);
        return ratio >= 1.25 ? $"{ratio:0.0}× {last}" : ratio <= 0.8 ? $"{(1 - ratio) * 100:0}% less than {last}" : $"About the same as {last}";
    }

    /// <summary>"21 min", "2 h 51 min", "40 s".</summary>
    internal static string Took(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{Math.Max(1, (int)t.TotalSeconds)} s";
}
