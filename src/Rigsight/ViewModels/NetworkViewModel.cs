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
    public string FastestText => Stat.Fastest is double f ? $"{Units.Speed(f)} ({Units.Mbps(f)})" : "No big downloads";
    public string LocalText => Stat.Use.Lan > 0 ? Units.Data(Stat.Use.Lan) : "None";

    private double Share(long part) => Stat.Use.Total > 0 ? Math.Clamp((double)part / Stat.Use.Total, 0, 1) : 0;
    private static string Percent(double share) => $"{Math.Round(share * 100):0}%";
}

/// <summary>An app and an amount, for the short lists (while you were away).</summary>
public sealed record NetAmountRow(string Name, string? Path, string Amount);

/// <summary>A big download as the card lists it.</summary>
public sealed record NetDownloadRow(string Name, string? Path, string When, string How, string Size);

/// <summary>A quarter hour of the connection strip: online, a drop, or nothing (the PC was off, or it's still to come).</summary>
public enum NetQuarter { Online, Dropped, None }

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

    partial void OnUnitChanged(ReportRange value) => _ = RefreshAsync();
    partial void OnAnchorChanged(DateTime value) => _ = RefreshAsync();

    /// <summary>Midnight passed with the window open (see <see cref="Controls.PeriodPicker.AfterMidnight"/>).</summary>
    public void NewDay(DateTime was)
    {
        _loadedPast = null; // "Yesterday" and "Online all day" are read against the new day
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
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _speedNote = "";

    [ObservableProperty] private IReadOnlyList<NetBin> _bins = [];
    [ObservableProperty] private string _chartTitle = "Each hour";

    /// <summary>Whether the apps list counts only what moved in the background.</summary>
    [ObservableProperty] private bool _backgroundOnly;
    partial void OnBackgroundOnlyChanged(bool value) => FillApps();

    public ObservableCollection<NetAppRow> Apps { get; } = [];
    [ObservableProperty] private bool _hasApps;

    [ObservableProperty] private string _awayText = "—";
    [ObservableProperty] private IReadOnlyList<NetAmountRow> _awayApps = [];

    [ObservableProperty] private string _downloadsTitle = "Biggest downloads";
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
            Downloads = [];
            AwayApps = [];
            Strip = [];
            return;
        }
        bool now = IncludesToday;
        Insights = Models.Kept.Or(Insights, [.. r.Insights.OrderByDescending(i => i.Priority)]);

        DownText = Units.Data(r.Down);
        DownNote = r.Range == ReportRange.Day && r.UsualDayDown is long usual
            ? $"vs {Units.Data(usual)} on a usual day"
            : r.PreviousDown is long before ? Against(r.Down, before, LastWord(r.Range, now)) : "";
        UpText = Units.Data(r.Up);
        var topUp = r.Apps.Where(a => a.Use.Up > 0).MaxBy(a => a.Use.Up);
        UpNote = topUp is null ? "" : $"{topUp.Name} the most";
        BackgroundText = Units.Data(r.Background);
        BackgroundNote = r.Down + r.Up > 0 ? $"{Math.Round(100.0 * r.Background / (r.Down + r.Up)):0}% of all use" : "";
        if (r.TopSpeed is long speed)
        {
            SpeedText = Units.Speed(speed);
            string when = r.TopSpeedAt is { } at ? (r.Range == ReportRange.Day ? $"at {at:h:mm tt}" : $"{at:ddd d MMM}") : "";
            SpeedNote = string.Join(" · ", new[] { Units.Mbps(speed), when, r.TopSpeedApp }.Where(x => !string.IsNullOrEmpty(x)));
        }
        else
        {
            SpeedText = "—";
            SpeedNote = "No big downloads";
        }

        Bins = Models.Kept.Or(Bins, r.Bins);
        ChartTitle = r.Range switch
        {
            ReportRange.Day => "Each hour",
            ReportRange.Year => "Each month",
            _ => "Each day",
        };

        FillApps();

        AwayText = Units.Data(r.AwayDown);
        AwayApps = Models.Kept.Or(AwayApps, [.. r.Apps.Where(a => a.Use.AwayDown >= 1 << 20).OrderByDescending(a => a.Use.AwayDown).Take(4)
            .Select(a => new NetAmountRow(a.Name, a.Path, Units.Data(a.Use.AwayDown)))]);

        DownloadsTitle = $"Biggest downloads {PeriodWord}";
        Downloads = Models.Kept.Or(Downloads, [.. r.Downloads.Select(d => new NetDownloadRow(d.App, d.Path,
            r.Range == ReportRange.Day ? d.Start.ToString("h:mm tt") : d.Start.ToString("ddd d MMM, h:mm tt"),
            $"{Took(d.Took)} at {Units.Speed(d.Speed)}", Units.Data(d.Bytes)))]);

        FillDrops(r);
    }

    private void FillApps()
    {
        if (_report is not { } r)
        {
            Apps.Clear();
            return;
        }
        var open = _openApp;
        var stats = r.Apps.Select(a => (Stat: a, Down: BackgroundOnly ? a.Use.BgDown + a.Use.AwayDown : a.Use.Down, Up: BackgroundOnly ? a.Use.BgUp + a.Use.AwayUp : a.Use.Up))
            .Where(x => x.Down + x.Up >= 1 << 20).OrderByDescending(x => x.Down + x.Up).ToList();
        double max = stats.Count > 0 ? stats.Max(x => x.Down + x.Up) : 1;
        // An app still at its place in the list keeps its row and is given the new figures: a list made anew has every
        // row of it built and laid out again, on each visit and each minute.
        for (int i = 0; i < stats.Count; i++)
        {
            var (stat, down, up) = stats[i];
            var row = new NetAppRow
            {
                Stat = stat, BackgroundOnly = BackgroundOnly, DownBar = down / max * 100, UpBar = up / max * 100,
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
                    && kept.BusiestLabel == row.BusiestLabel && kept.BusiestText == row.BusiestText && kept.IsOpen == row.IsOpen) continue;
                (kept.Stat, kept.BackgroundOnly, kept.DownBar, kept.UpBar, kept.BusiestLabel, kept.BusiestText) = (stat, row.BackgroundOnly, row.DownBar, row.UpBar, row.BusiestLabel, row.BusiestText);
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
        var drops = r.Drops;
        DropsWarn = r.Insights.Any(i => i.Key == "net-drops");
        DropsTitle = drops.Count switch
        {
            0 => IncludesToday && r.Range == ReportRange.Day ? "Online all day" : "No drops",
            1 => "Dropped once",
            2 => "Dropped twice",
            var n => $"Dropped {n} times",
        };
        if (drops.Count > 0)
        {
            var longest = drops.MaxBy(d => d.Seconds)!;
            var at = TimeUtil.FromUnix(longest.Start);
            DropsNote = $"Longest {Took(TimeSpan.FromSeconds(longest.Seconds))} {(r.Range == ReportRange.Day ? $"at {at:h:mm tt}" : $"on {at:ddd d MMM}")}";
        }
        else DropsNote = r.Range == ReportRange.Day ? "" : $"The internet stayed up {PeriodWord}";

        if (r.Range != ReportRange.Day || r.Quarters.Length != 96)
        {
            Strip = [];
            return;
        }
        var strip = new NetQuarter[96];
        for (int i = 0; i < 96; i++) strip[i] = r.Quarters[i] ? NetQuarter.Online : NetQuarter.None;
        foreach (var d in drops)
        {
            var (start, end) = (TimeUtil.FromUnix(d.Start), TimeUtil.FromUnix(d.End));
            for (var q = start; q <= end; q = q.AddMinutes(15))
                if (q.Date == r.From.Date) strip[(q.Hour * 60 + q.Minute) / 15] = NetQuarter.Dropped;
        }
        var day = r.From.Date;
        Strip = Models.Kept.Or(Strip, [.. strip.Select((state, i) =>
        {
            var (from, to) = (day.AddMinutes(i * 15), day.AddMinutes(i * 15 + 15));
            string what = state switch
            {
                NetQuarter.Online => "Online",
                NetQuarter.Dropped => "Dropped",
                _ => from > DateTime.Now ? "Still to come" : "No record",
            };
            string span = from.Hour < 12 == to.Hour < 12 ? $"{from:h:mm} – {to:h:mm tt}" : $"{from:h:mm tt} – {to:h:mm tt}";
            return new NetStripCell(state, $"{span} · {what}");
        })]);
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
