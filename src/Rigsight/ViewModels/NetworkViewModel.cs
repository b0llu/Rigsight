using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>An app using the internet right now, with its speeds.</summary>
public sealed partial class NetLiveRow : ObservableObject
{
    public required string Exe { get; init; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _path;
    [ObservableProperty] private string _downText = "";
    [ObservableProperty] private string _upText = "";
}

/// <summary>One bar of an app's small chart: its height (0–1 of the app's biggest) and what hovering it says.</summary>
public sealed record NetSparkBar(double Height, string Tip, bool Off = false);

/// <summary>One app in the period's list: what it moved, its share in the background, and its use day by day.</summary>
public sealed partial class NetAppRow : ObservableObject
{
    public required NetAppStat Stat { get; set; }
    public string Name => Stat.Name;
    public string? Path => Stat.Path;

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
    public string SubText => $"↓ {DownText}   ↑ {UpText}" + (BackgroundOnly ? "   " + Lang.T("background only")
        : Stat.Use.Background >= 1 << 20 && BackgroundPercent >= 1 ? "   " + Lang.T($"{BackgroundPercent}% in the background") : "");

    /// <summary>The bars' lengths, against the biggest app in the list (0–100): shown where the row has no small chart.</summary>
    public double DownBar { get; set; }
    public double UpBar { get; set; }

    /// <summary>Its use day by day (a week, a month) or month by month (a year): none for a day, or a bar or stretch picked.</summary>
    public IReadOnlyList<NetSparkBar> Spark { get; set; } = [];
    public bool HasSpark => Spark.Count > 0;

    /// <summary>The row was given new figures: everything it shows is read again (the row itself stays where it is).</summary>
    public void Changed() => OnPropertyChanged(string.Empty);

    public int BackgroundPercent => Stat.Use.Total > 0 ? (int)Math.Round(100.0 * Stat.Use.Background / Stat.Use.Total) : 0;
}

/// <summary>A colour of the chart and what it stands for.</summary>
public sealed record NetLegend(string BrushKey, string Text, double Opacity = 1);

/// <summary>One app in the graph's hover box: its colour on the chart, its icon, name and what it used.</summary>
public sealed record NetHoverRow(string BrushKey, string? Path, string Name, string Amount, bool Muted = false);

/// <summary>
/// The graph's hover box for one bar: the hour (or day, or month) and the game played then, its total big with the
/// download, upload and background share under it, the ping, what Steam downloaded, a drop, then each part of the bar
/// with its colour and the rest as "N other apps".
/// </summary>
public sealed record NetHoverBox(string Title, string Total)
{
    public string? Game { get; init; }
    public string? GamePath { get; init; }
    public string Split { get; init; } = "";
    public string? Ping { get; init; }
    public string? PingNote { get; init; }
    /// <summary>The ping went high: its dot in the ping's own colour (blue), never a warning colour.</summary>
    public bool PingHigh { get; init; }
    public string? Steam { get; init; }
    public string? SteamPath { get; init; }
    /// <summary>The internet dropped in it: the one line in the warning colour.</summary>
    public string? Drop { get; init; }
    public string? Missing { get; init; }
    public IReadOnlyList<NetHoverRow> Rows { get; init; } = [];
    public string? Empty { get; init; }
    public string Footer { get; init; } = "";

    public bool HasGame => Game is not null;
    public bool HasPing => Ping is not null;
    public bool HasSteam => Steam is not null;
    public bool HasDrop => Drop is not null;
    public bool HasMissing => Missing is not null;
    public bool HasRows => Rows.Count > 0;
    public bool HasEmpty => Empty is not null;
    public bool HasSplit => Split.Length > 0;
    public bool HasFooter => Footer.Length > 0;

    // ── The Ping view's box: the usual big, a bar of the hour's spread on the graph's scale, the three figures, the
    // packet loss (or that there was none), and the app uploading the most then ──

    public bool IsPing { get; init; }
    public bool IsUsage => !IsPing;
    public string? PingBig { get; init; }
    public bool HasPingFigures => IsPing && PingBig is not null;
    /// <summary>The range bar, in pixels across its <see cref="RangeWidth"/>: lowest to highest, lowest to most checks, the usual.</summary>
    public double AllLeft { get; init; }
    public double AllWidth { get; init; }
    public double MostLeft { get; init; }
    public double MostWidth { get; init; }
    public double DotLeft { get; init; }
    public string ScaleTop { get; init; } = "";
    public string UsualText { get; init; } = "";
    public string MostText { get; init; } = "";
    public string HighestText { get; init; } = "";
    /// <summary>Some data didn't get through: the line in the warning colour (null: none, "No packet loss").</summary>
    public string? Loss { get; init; }
    public bool HasLoss => Loss is not null;
    public bool NoLoss => HasPingFigures && Loss is null;
    public string? Context { get; init; }
    public string? ContextPath { get; init; }
    public bool HasContext => Context is not null;

    /// <summary>The range bar's width: the box's 300 less its border and padding.</summary>
    public const double RangeWidth = 270;
}

/// <summary>A line of "What happened", or the heading of a day (<see cref="IsHeader"/>).</summary>
public sealed record NetHappeningRow(bool IsHeader, string Text)
{
    public string When { get; init; } = "";
    public string? Detail { get; init; }
    public string? Amount { get; init; }
    public string? PingNote { get; init; }
    public string? Path { get; init; }
    public string? Icon { get; init; }
    public InsightTone Tone { get; init; }
    public bool IsNew { get; init; }
    /// <summary>The bar it points at on the graph (-1: none).</summary>
    public int Bin { get; init; } = -1;
    /// <summary>Picked on the graph: the line is lit.</summary>
    public bool IsPicked { get; init; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool HasAmount => !string.IsNullOrEmpty(Amount);
    public bool HasPingNote => !string.IsNullOrEmpty(PingNote);
    public bool HasIcon => Path is null && Icon is not null;
    public bool CanPick => Bin >= 0;
}

/// <summary>How the line is right now, in the Right now card's words.</summary>
public enum LineState { Checking, Good, Slow, Losing, Offline }

/// <summary>
/// The Network page: right now (the speeds and the last minute, how the line is, every app using it), then the
/// period: one line of figures, the graph each hour (or day, or month) with its usage and its ping on the same hours,
/// every app, and what happened.
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

    /// <summary>"today", "this week", "in September 2026"… for the labels that follow the period (see <see cref="Controls.PeriodPicker.Words"/>).</summary>
    public string PeriodWord => Controls.PeriodPicker.Words(Unit, Anchor);

    /// <summary>What the page says when the period has nothing.</summary>
    /// <summary>
    /// Why: the PC was off or asleep, RigSight wasn't running, the history starts later (see <see cref="WhyEmpty"/>); and
    /// where the PC was on and recording, that its internet use needs the agent running as admin. It used to say only
    /// the last, for days the PC was simply off too (the user, 2026-11-04).
    /// </summary>
    [ObservableProperty] private string _emptyText = "";

    partial void OnUnitChanged(ReportRange value) => NewPeriod();
    partial void OnAnchorChanged(DateTime value) => NewPeriod();

    private void NewPeriod()
    {
        SelectedBin = -1;
        Zoomed = null;
        _ = RefreshAsync();
    }

    /// <summary>A drop's notification was clicked: that day.</summary>
    public void ShowDay(DateTime day)
    {
        if (Unit == ReportRange.Day && Anchor == day.Date) { _ = RefreshAsync(); return; }
        Unit = ReportRange.Day;
        Anchor = day.Date;
    }

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

    public NetworkViewModel(ReportService reports, LiveData live, SettingsModel? settings = null)
    {
        _reports = reports;
        Live = live;
        _settings = settings;
        _pingWasOn = PingChecksOn;
        live.PropertyChanged += OnLiveChanged;
        if (settings is not null) settings.Changed += OnSettingsChanged;
    }

    private readonly SettingsModel? _settings;
    private bool _pingWasOn;

    // ── Ping checks on and off (here, or Settings › Tracking) ──

    public bool PingChecksOn => _settings?.Current.Tracking.PingChecks ?? true;
    public bool PingChecksOff => !PingChecksOn;

    /// <summary>The "Pause ping checks?" box is open.</summary>
    [ObservableProperty] private bool _confirmStopOpen;

    /// <summary>
    /// The Ping view's status line, under the graph in every state (a graph, still collecting, paused): what is being
    /// done in words, then "Pause" (with a confirm) or "Resume" underlined. The user picked it over a button on its own
    /// row (2026-11-04, mock-up MPgp1z3fTaQXFXL4kSLSx1): paused reads as a word, and the line never moves.
    /// </summary>
    [ObservableProperty] private string _pingStatusText = "";
    public string PingStatusAction => PingChecksOn ? Lang.T("Pause") : Lang.T("Resume");

    [RelayCommand]
    private void PingStatus()
    {
        if (PingChecksOn) ConfirmStopOpen = true;
        else SetPingChecks(true);
    }

    private void FillPingStatus()
    {
        if (PingChecksOn)
        {
            PingStatusText = Live.Line is { ProviderFound: false } ? Lang.T("Checking every 5 seconds, to your router")
                : Lang.T("Checking every 5 seconds, to your internet provider's first router");
            return;
        }
        PingStatusText = _report?.PingPausedSince is { } since
            ? Lang.T($"Paused since {(since.Date == DateTime.Today ? TimeText.At(since, "h:mm tt") : since > DateTime.Today.AddDays(-6) ? $"{since:ddd} {TimeText.At(since, "h:mm tt")}" : since.ToString("d MMM"))} · the ping checked before stays")
            : Lang.T("Paused · the ping checked before stays");
    }

    [RelayCommand]
    private void StopPing()
    {
        ConfirmStopOpen = false;
        SetPingChecks(false);
    }

    [RelayCommand]
    private void CancelStopPing() => ConfirmStopOpen = false;

    [RelayCommand]
    private void StartPing() => SetPingChecks(true);

    private void SetPingChecks(bool on) => _settings?.Update(s => s.Tracking.PingChecks = on);

    private void OnSettingsChanged()
    {
        if (PingChecksOn == _pingWasOn) return;
        _pingWasOn = PingChecksOn;
        OnPropertyChanged(nameof(PingChecksOn));
        OnPropertyChanged(nameof(PingChecksOff));
        OnPropertyChanged(nameof(PingStatusAction));
        FillPingStatus();
        UpdateLine(Live.Line);
        _ = ReloadAfterSwitchAsync();
    }

    /// <summary>The agent writes down the moment it stops or starts: the period is read again once it has, for the graph's mark.</summary>
    private async Task ReloadAfterSwitchAsync()
    {
        await Task.Delay(2000);
        _loadedPast = null;
        await RefreshAsync();
    }

    public LiveData Live { get; }

    /// <summary>An app row was clicked: the shell opens that app on the Apps page, for the same period.</summary>
    public event Action<string, ReportRange, DateTime>? OpenAppRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _loaded;

    /// <summary>Anything recorded for the period: the page's history shows, or a line saying why not.</summary>
    [ObservableProperty] private bool _hasData;

    /// <summary>The line saying nothing was recorded: only once the period has been read (a loader until then).</summary>
    public bool ShowEmpty => Loaded && !HasData;

    // ── Right now ──

    [ObservableProperty] private bool _hasLive;

    /// <summary>The agent isn't getting network events from Windows and couldn't get them back: nothing is recorded for now.</summary>
    [ObservableProperty] private bool _stalled;
    [ObservableProperty] private string _downNow = "—";
    [ObservableProperty] private string _upNow = "—";
    public ObservableCollection<NetLiveRow> UsingNow { get; } = [];
    [ObservableProperty] private bool _nobodyNow;

    /// <summary>The line now, in words ("Working well") and as a state (its dot's colour).</summary>
    [ObservableProperty] private LineState _lineState = LineState.Checking;
    [ObservableProperty] private string _lineWords = Lang.T("Checking…");
    [ObservableProperty] private string _pingNow = "—";
    [ObservableProperty] private string _jitterNow = "—";
    [ObservableProperty] private string _lostNow = "—";
    /// <summary>What the ping is to: the provider's first router, or (none found) this PC's router.</summary>
    [ObservableProperty] private string _pingTip = "";
    [ObservableProperty] private string _connectedBy = "—";
    [ObservableProperty] private string _vpn = "—";
    [ObservableProperty] private string _subtitle = "";

    /// <summary>Whether Windows sees the internet (null: it can't tell), read with the connection.</summary>
    private bool? _online;
    /// <summary>The period's usual ping: "slow right now" is against it.</summary>
    private int? _usualPing;

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
        UpdateLine(Live.Line);
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
            row.DownText = Units.Speed(a.Down);
            row.UpText = Units.Speed(a.Up);
        }
        NobodyNow = UsingNow.Count == 0;
    }

    /// <summary>The line check's last minute into the Right now card: its words, ping, jitter and what went unanswered.</summary>
    internal void UpdateLine(LineLive? line)
    {
        if (!PingChecksOn) line = null;
        int? ping = line?.IspMs ?? line?.LanMs;
        PingNow = !PingChecksOn ? Lang.T("Paused") : ping is int p ? $"{p} ms" : "—";
        if (ShowPingNote) FillPingNote();
        FillPingStatus();
        JitterNow = line?.Jitter is int j ? $"{j} ms" : "—";
        LostNow = line is null ? "—" : $"{line.LostPercent:0.#}%";
        PingTip = line is null ? "" : line.ProviderFound ? Lang.T("To your internet provider's first router") : Lang.T("To your router (your provider's didn't answer)");
        (LineState, LineWords) = LineNow(line, _online, _usualPing, PingChecksOn);
    }

    /// <summary>The line's state from its last minute: offline, losing pings, slow against the period's usual, or working well.</summary>
    internal static (LineState, string) LineNow(LineLive? line, bool? online, int? usual, bool checksOn = true)
    {
        if (online == false) return (LineState.Offline, Lang.T("No internet"));
        // Ping checks off: Windows seeing the internet is all there is to go by.
        if (!checksOn) return online == true ? (LineState.Good, Lang.T("Working well")) : (LineState.Checking, Lang.T("Checking…"));
        if (line is null) return (LineState.Checking, Lang.T("Checking…"));
        if (line.LostPercent >= LosingPercent) return (LineState.Losing, Lang.T("Losing data"));
        int? ping = line.IspMs ?? line.LanMs;
        if (ping is int p && p >= NetLinePing.HighFor(usual ?? 0)) return (LineState.Slow, Lang.T("Slow right now"));
        return (LineState.Good, Lang.T("Working well"));
    }

    /// <summary>The share of pings going unanswered (a percent) from which the line is "losing data".</summary>
    public const double LosingPercent = 5;

    private async Task ReadConnectionAsync()
    {
        // Asking Windows about every adapter takes a moment on a PC with many (VPNs, virtual machines): not on the window's thread.
        var (c, online) = await Task.Run(() => (NetAdapters.Current(), NetAdapters.WindowsSeesInternet()));
        Subtitle = c is null ? Lang.T("Not connected") : string.Join(" · ", new[] { c.Kind, c.Adapter, c.Vpn is null ? null : Lang.T($"through {c.Vpn}") }.OfType<string>());
        ConnectedBy = c?.Kind ?? Lang.T("None");
        Vpn = c?.Vpn ?? Lang.T("Off");
        _online = online;
        (LineState, LineWords) = LineNow(PingChecksOn ? Live.Line : null, _online, _usualPing, PingChecksOn);
    }

    // ── The period's line of figures ──

    [ObservableProperty] private string _totalText = "—";
    [ObservableProperty] private string _downText = "—";
    [ObservableProperty] private string _upText = "—";
    [ObservableProperty] private string _backgroundText = "—";
    [ObservableProperty] private string _backgroundTip = "";
    [ObservableProperty] private string _usualPingText = "—";
    /// <summary>"usual ping", or "usual ping, until checks stopped" with when in its tip.</summary>
    [ObservableProperty] private string _usualPingWord = Lang.T("usual ping");
    [ObservableProperty] private string? _usualPingTip;
    [ObservableProperty] private bool _hasPing;
    [ObservableProperty] private string _dropsCount = "";
    [ObservableProperty] private string _dropsWord = "";
    [ObservableProperty] private string _dropsTip = "";

    /// <summary>Drops worth a look: the AI button asks why, from the Network page's records.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DropsAsk))] private bool _dropsWarn;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DropsAsk))] private string _dropsTitle = "";
    public Services.AiQuestion? DropsAsk => DropsWarn ? new(Lang.T($"{DropsTitle} {PeriodWord}. Why does my connection drop, and is it the PC or the line?"), Core.Ai.AiPages.Network) : null;

    /// <summary>
    /// The period has the whole PC's use and no app's: a standard account, where which app used the internet can't be
    /// read. The page keeps its totals and its graph, and the Apps box says why it is empty.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoAppsText))]
    private bool _wholeOnly;

    public string NoAppsText => WholeOnly ? Lang.T("Each app's internet use needs admin rights on this PC.") : Lang.T("No app used 1 MB or more.");

    // ── The graph ──

    [ObservableProperty] private IReadOnlyList<NetBin> _bins = [];
    [ObservableProperty] private string _chartTitle = Lang.T("Each hour");
    [ObservableProperty] private IReadOnlyList<NetDropSpan> _drops = [];
    [ObservableProperty] private int _highPing;

    /// <summary>The bars split by app (or, with no app's use read, by in front and background).</summary>
    [ObservableProperty] private NetBarsMode _chartMode = NetBarsMode.App;

    /// <summary>What the graph shows: "Usage" or "Ping". Both keep the same zoom and the bar picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChartView), nameof(IsPingView), nameof(LineText), nameof(ChartHint), nameof(ShowPingNote))]
    private string _view = "Usage";
    public NetChartView ChartView => View == "Ping" ? NetChartView.Ping : NetChartView.Usage;
    public bool IsPingView => View == "Ping";
    partial void OnViewChanged(string value)
    {
        FillLegend();
        if (ShowPingNote) FillPingNote();
    }

    /// <summary>The period's ping in a sentence, over the graph in its Ping view (empty in its Usage view, and while the note shows).</summary>
    public string LineText => IsPingView && !ShowPingNote ? _lineSentence + "  " : "";
    private string _lineSentence = "";

    /// <summary>
    /// Too little ping checked in the period for a graph (see <see cref="NetLine.BarsToDraw"/>): the Ping view says why
    /// in place of it, at the graph's height (a few dots on an empty graph read as a broken line).
    /// </summary>
    public bool ShowPingNote => IsPingView && _report is { } r && !r.PingEnough;
    [ObservableProperty] private string _pingNoteTitle = "";
    [ObservableProperty] private string _pingNoteBody = "";
    [ObservableProperty] private string _pingNoteIcon = "\uE916";
    /// <summary>Still collecting: how far along (0 to 1), in words, and the ping now beside the usual so far.</summary>
    [ObservableProperty] private bool _pingNoteCollecting;
    [ObservableProperty] private double _pingNoteProgress;
    [ObservableProperty] private string _pingNoteSoFar = "";
    [ObservableProperty] private string _pingNoteNow = "";

    /// <summary>The internet strip under the graph (see <see cref="NetReport.Link"/>).</summary>
    [ObservableProperty] private IReadOnlyList<NetLinkCell> _link = [];

    /// <summary>When ping checks were stopped and started again in the period, marked on the Ping view.</summary>
    [ObservableProperty] private IReadOnlyList<NetPingMark> _pingMarks = [];

    /// <summary>The note's words: checks off, still collecting, never checked in the period, or too little checked in it.</summary>
    private void FillPingNote()
    {
        if (_report is not { } r) return;
        string Count(int n) => Hourly ? (n == 1 ? Lang.T("1 hour") : Lang.T($"{n} hours"))
            : Unit == ReportRange.Year ? (n == 1 ? Lang.T("1 month") : Lang.T($"{n} months"))
            : n == 1 ? Lang.T("1 day") : Lang.T($"{n} days");
        int have = r.PingBars, need = r.PingBarsNeeded;
        PingNoteCollecting = false;
        if (!PingChecksOn)
        {
            PingNoteIcon = "\uE769";
            PingNoteTitle = Lang.T("Ping checks are paused");
            PingNoteBody = have > 0
                ? Lang.T($"Too little ping was checked {PeriodWord} for a graph. Resumed, your connection is checked every 5 seconds and it fills in as you use your PC.")
                : Lang.T("Your connection's ping isn't being checked. Resumed, it's checked every 5 seconds and shows here.");
        }
        else if (IncludesToday)
        {
            PingNoteIcon = "\uE916";
            PingNoteTitle = Lang.T("Ping is being checked");
            PingNoteBody = Lang.T($"Your connection is checked every 5 seconds. The graph shows here once there are {Count(need)} of checks {PeriodWord}.");
            PingNoteCollecting = true;
            PingNoteProgress = need > 0 ? Math.Clamp((double)have / need, 0, 1) : 0;
            PingNoteSoFar = Lang.T($"{have} of {Count(need)} so far");
            PingNoteNow = string.Join(" · ", new[]
            {
                PingNow != "—" ? Lang.T($"Right now {PingNow}") : null,
                r.Line is { } line ? Lang.T($"Usual so far {line.Usual} ms") : null,
            }.OfType<string>());
        }
        else if (have == 0)
        {
            PingNoteIcon = "\uE916";
            PingNoteTitle = Lang.T($"Ping wasn't checked {PeriodWord}");
            PingNoteBody = r.PingSince is { } since && since >= Period.To
                ? Lang.T($"It was first checked on {since:d MMM yyyy}.")
                : r.PingSince is null ? Lang.T("Ping hasn't been checked on this PC yet.") : Lang.T("Your PC was off, or RigSight wasn't running.");
        }
        else
        {
            PingNoteIcon = "\uE916";
            PingNoteTitle = Lang.T($"Too little ping checked {PeriodWord}");
            PingNoteBody = Lang.T($"Ping was checked in {Count(have)} of it; the graph needs {Count(need)}.");
        }
    }

    /// <summary>What dragging and clicking the graph do, under its title.</summary>
    public string ChartHint => ShowPingNote ? "" : Hourly ? Lang.T("Drag across to zoom in · click an hour for its apps")
        : Unit == ReportRange.Year ? Lang.T("Click a month for its apps") : Lang.T("Drag across to zoom in · click a day for its apps");

    /// <summary>The apps with a colour of their own on the chart, and every app's name for its hover box.</summary>
    [ObservableProperty] private IReadOnlyList<long> _colorApps = [];
    [ObservableProperty] private IReadOnlyDictionary<long, string> _appNames = new Dictionary<long, string>();
    [ObservableProperty] private IReadOnlyList<NetLegend> _legend = [];

    /// <summary>The bar picked on the chart (-1: none): the apps list then shows that hour's (or day's) apps.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private int _selectedBin = -1;
    public bool HasSelection => SelectedBin >= 0;
    partial void OnSelectedBinChanged(int value)
    {
        FillApps();
        FillHappenings();
    }

    /// <summary>The stretch dragged across on the graph (null: all of it): the apps list follows it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZoomed), nameof(ZoomText))]
    private NetDropSpan? _zoomed;
    public bool IsZoomed => Zoomed is not null;
    public string ZoomText => Zoomed is { } z ? NetBarsChart.ZoomWords(z, Step) : "";
    /// <summary>The button back: "Whole day", "Whole week", "Whole month".</summary>
    public string ResetText => Unit switch { ReportRange.Day => Lang.T("Whole day"), ReportRange.Week => Lang.T("Whole week"), _ => Lang.T("Whole month") };
    partial void OnZoomedChanged(NetDropSpan? value) => FillApps();

    [RelayCommand]
    private void ResetZoom() => Zoomed = null;

    private TimeSpan Step => _report is { Hourly: true } ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);

    /// <summary>The hour, day or month of the bar picked, or the stretch zoomed into: for the page's Export box.</summary>
    internal (DateTime From, DateTime To)? PickedBar
    {
        get
        {
            if (SelectedBin < 0 || SelectedBin >= Bins.Count) return null;
            var from = Bins[SelectedBin].Start;
            return (from, BinEnd(SelectedBin));
        }
    }

    internal (DateTime From, DateTime To)? ZoomedStretch => Zoomed is { } z ? (z.From, z.To) : null;

    /// <summary>The page's bars are hours (a day, a week): its Export box offers each hour's use first.</summary>
    internal bool Hourly => _report?.Hourly ?? Unit == ReportRange.Day;

    private DateTime BinEnd(int i) => i + 1 < Bins.Count ? Bins[i + 1].Start
        : Hourly ? Bins[i].Start.AddHours(1) : Unit == ReportRange.Year ? Bins[i].Start.AddMonths(1) : Bins[i].Start.AddDays(1);

    [RelayCommand]
    private void ClearSelection() => SelectedBin = -1;

    // ── The hover box ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoverOpen))]
    private int _hoverIndex = -1;
    [ObservableProperty] private System.Windows.Point _hoverPoint;
    [ObservableProperty] private NetHoverBox? _hoverBox;
    public bool HoverOpen => HoverIndex >= 0 && HoverBox is not null;
    partial void OnHoverIndexChanged(int value)
    {
        HoverBox = value >= 0 && value < Bins.Count ? BuildHover(value) : null;
        OnPropertyChanged(nameof(HoverOpen));
    }

    /// <summary>The hover box for bar <paramref name="i"/>: everything the bar is, in the order it's read.</summary>
    internal NetHoverBox BuildHover(int i)
    {
        var b = Bins[i];
        var end = BinEnd(i);
        string title = Hourly
            ? (Unit == ReportRange.Day ? "" : b.Start.ToString("ddd d MMM") + " · ") + $"{TimeText.At(b.Start, "h tt")} – {TimeText.At(end, "h tt")}"
            : Unit == ReportRange.Year ? b.Start.ToString("MMMM yyyy") : b.Start.ToString("dddd, d MMMM");
        // What a click does on this bar: picks it (or lets it go); dragging zooms in (not a year's months).
        string unitWord = Hourly ? "hour" : Unit == ReportRange.Year ? "month" : "day";
        string click = SelectedBin == i
            ? unitWord switch { "hour" => Lang.T("Click again to let go of this hour"), "month" => Lang.T("Click again to let go of this month"), _ => Lang.T("Click again to let go of this day") }
            : unitWord switch { "hour" => Lang.T("Click to see this hour's apps"), "month" => Lang.T("Click to see this month's apps"), _ => Lang.T("Click to see this day's apps") };
        string footer = b.Total == 0 ? "" : Unit == ReportRange.Year ? click : click + " · " + Lang.T("drag across to zoom in");
        if (IsPingView) return PingHover(b, title, footer);
        string? missing = b.UnrecordedMinutes >= NetBarsChart.MinUnrecorded ? Lang.T($"Not recorded for {Units.Duration(b.UnrecordedMinutes * 60)}") : null;
        var drops = _report?.Drops.Where(d => TimeUtil.FromUnix(d.Start) < end && TimeUtil.FromUnix(d.End) > b.Start).ToList() ?? [];
        string? drop = drops.Count == 0 ? null : drops.Count == 1
            ? Lang.T($"Your internet was down for {DropWords.Long(drops[0].Seconds)}, from {TimeText.At(TimeUtil.FromUnix(drops[0].Start), "h:mm tt")}")
            : Lang.T($"Your internet dropped {drops.Count} times, for {DropWords.Long(drops.Sum(d => d.Seconds))} in all");
        string? steam = b.Steam.Count switch
        {
            0 => null,
            1 => b.Steam[0].Name is { } n ? (b.Steam[0].NewInstall ? Lang.T($"Steam installed {n}") : Lang.T($"Steam updated {n}")) + $" · {Units.Data(b.Steam[0].Bytes)}"
                 : Lang.T($"Steam downloaded {Units.Data(b.Steam[0].Bytes)}"),
            var n => Lang.T($"Steam downloaded {n} games") + $" · {Units.Data(b.Steam.Sum(d => d.Bytes))}",
        };
        string? ping = b.Ping is { } p ? Lang.T($"Ping {p.Usual} ms") : null;
        string? pingNote = b.Ping is { } q ? (q.LostPercent >= NetLine.LostWorthSaying ? Lang.T($"highest {q.Highest} ms · {q.LostPercent:0.#}% packet loss") : Lang.T($"highest {q.Highest} ms")) : null;
        if (b.Total == 0)
            return new NetHoverBox(title, "")
            {
                Game = b.Game, GamePath = b.GamePath, Ping = ping, PingNote = pingNote, PingHigh = b.Ping?.Highest >= HighPing && HighPing > 0,
                Drop = drop, Missing = missing,
                // Why it's empty: still to come, the PC off (or asleep…), or nothing used with the PC on.
                Empty = b.Start > DateTime.Now ? Lang.T("Still to come") : b.OffWhy ?? (missing is null ? Lang.T("No internet use") : null),
            };
        long behind = Math.Min(b.Background, b.Total);
        int bgPercent = (int)Math.Round(100.0 * behind / b.Total);
        var split = $"↓ {Units.Data(b.Down)}   ↑ {Units.Data(b.Up)}" + (bgPercent > 0 ? "   " + Lang.T($"{bgPercent}% in the background") : "");
        var rows = new List<NetHoverRow>();
        if (WholeOnly) rows.Add(new("SkyBrush", null, Lang.T("This PC"), Units.Data(b.Total)));
        else
        {
            var shown = b.Apps.Take(HoverApps).ToList();
            foreach (var a in shown) rows.Add(new(DotKey(a.App), _paths.GetValueOrDefault(a.App), AppNames.GetValueOrDefault(a.App) ?? Lang.T("Unknown"), Units.Data(a.Total)));
            long rest = b.Total - shown.Sum(a => a.Total);
            int more = Math.Max(b.AppCount, b.Apps.Count) - shown.Count;
            if (more > 0 && rest >= 1 << 20)
                rows.Add(new(NetBarsChart.OtherBrushKey, null, more == 1 ? Lang.T("1 other app") : Lang.T($"{more} other apps"), Units.Data(rest), Muted: true));
        }
        return new NetHoverBox(title, Units.Data(b.Total))
        {
            Game = b.Game, GamePath = b.GamePath, Split = split, Ping = ping, PingNote = pingNote, PingHigh = HighPing > 0 && b.Ping?.Highest >= HighPing,
            Steam = steam, SteamPath = _steamPath, Drop = drop, Missing = missing, Rows = rows,
            Footer = footer,
        };
    }

    /// <summary>
    /// The Ping view's box for a bar: the usual ping big; a bar of its spread on the graph's own scale (lowest to most
    /// checks darker, up to the highest lighter, the usual a dot); usual, most checks and highest side by side; packet
    /// loss (or none); and the app uploading the most then, which is what most often pushes a ping up.
    /// </summary>
    private NetHoverBox PingHover(NetBin b, string title, string footer)
    {
        if (b.Ping is not { } p)
            return new NetHoverBox(title, "")
            {
                IsPing = true, Game = b.Game, GamePath = b.GamePath, Footer = footer,
                Empty = b.Start > DateTime.Now ? Lang.T("Still to come") : b.PingOff ? Lang.T("Ping checks were paused") : b.OffWhy ?? Lang.T("Ping wasn't checked"),
            };
        var shown = Zoomed is { } z ? Bins.Where(x => x.Start >= z.From && x.Start < z.To) : Bins;
        double top = NetBarsChart.PingTop(shown.Select(x => (double)(x.Ping?.Highest ?? 0)).DefaultIfEmpty(0).Max());
        double Px(int v) => NetHoverBox.RangeWidth * Math.Clamp(v / top, 0, 1);
        var busy = b.Apps.Where(a => a.Up >= NetLine.BusyUploadBytes).MaxBy(a => a.Up);
        string? context = busy is null ? null
            : Hourly ? Lang.T($"{AppNames.GetValueOrDefault(busy.App) ?? Lang.T("Unknown")} uploaded {Units.Data(busy.Up)} this hour")
            : Unit == ReportRange.Year ? Lang.T($"{AppNames.GetValueOrDefault(busy.App) ?? Lang.T("Unknown")} uploaded {Units.Data(busy.Up)} that month")
            : Lang.T($"{AppNames.GetValueOrDefault(busy.App) ?? Lang.T("Unknown")} uploaded {Units.Data(busy.Up)} that day");
        return new NetHoverBox(title, "")
        {
            IsPing = true, Game = b.Game, GamePath = b.GamePath, Footer = footer,
            PingBig = $"{p.Usual} ms",
            AllLeft = Px(p.Low), AllWidth = Math.Max(4, Px(p.Highest) - Px(p.Low)),
            MostLeft = Px(p.Low), MostWidth = Math.Max(4, Px(p.Most) - Px(p.Low)),
            DotLeft = Math.Clamp(Px(p.Usual) - 5, 0, NetHoverBox.RangeWidth - 10),
            ScaleTop = $"{top:0.#} ms",
            UsualText = $"{p.Usual} ms", MostText = Lang.T($"under {p.Most} ms"), HighestText = $"{p.Highest} ms",
            Loss = p.LostPercent >= NetLine.LostWorthSaying ? Lang.T($"Some data didn't get through: {p.LostPercent:0.#}% packet loss") : null,
            Context = context, ContextPath = busy is null ? null : _paths.GetValueOrDefault(busy.App),
        };
    }

    /// <summary>The apps a bar's hover box names; the rest are "N other apps".</summary>
    public const int HoverApps = 5;

    private Dictionary<long, string?> _paths = [];
    private string? _steamPath;

    // ── Apps ──

    [ObservableProperty] private string _appsTitle = Lang.T("Apps");

    /// <summary>What the apps list is sorted by, and what each row's figure is: Total, Download, Upload or Background.</summary>
    [ObservableProperty] private string _sort = "Total";
    partial void OnSortChanged(string value) => FillApps();
    public bool BackgroundOnly => Sort == "Background";

    public ObservableCollection<NetAppRow> Apps { get; } = [];
    [ObservableProperty] private bool _hasApps;

    [RelayCommand]
    private void OpenApp(NetAppRow row)
    {
        var (unit, anchor) = (Unit, Anchor);
        OpenAppRequested?.Invoke(row.Stat.Exe, unit, anchor);
    }

    // ── What happened ──

    public ObservableCollection<NetHappeningRow> Happenings { get; } = [];
    /// <summary>What's said of the whole period (its insights): a summary set apart on top of the card, with no time.</summary>
    public ObservableCollection<NetHappeningRow> Summary { get; } = [];
    [ObservableProperty] private bool _hasSummary;
    /// <summary>Anything at all in the card: the summary or a line (else it says nothing stood out).</summary>
    [ObservableProperty] private bool _hasHappenings;

    [RelayCommand]
    private void PickHappening(NetHappeningRow row)
    {
        if (row.Bin < 0 || row.Bin >= Bins.Count) return;
        // A line outside the stretch zoomed into: back to all of it, so the bar it points at shows.
        if (Zoomed is { } z && (Bins[row.Bin].Start < z.From || Bins[row.Bin].Start >= z.To)) Zoomed = null;
        SelectedBin = SelectedBin == row.Bin ? -1 : row.Bin;
    }

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
            await ReadConnectionAsync();
            return;
        }
        int id = ++_load;
        IsLoading = true;
        var (report, firstDay) = await _reports.NetworkAsync(Unit, Anchor);
        if (id != _load) return;
        if (report is not { HasData: true })
        {
            var (pf, pt) = Period;
            string when = PeriodWord;
            string why = await _reports.EmptyWhyAsync(pf, pt, when,
                pcWasOn: Lang.T($"Your PC was on {when}, but its internet use wasn't recorded: that needs the background agent running as admin."));
            if (id != _load) return;
            EmptyText = why;
        }
        IsLoading = false;
        _report = report;
        _loadedPast = IncludesToday ? null : (Unit, from);
        FirstDay = firstDay ?? DateTime.Today;
        Loaded = true;
        UpdateLive();
        Fill(report);
        await ReadConnectionAsync();
    }

    private void Fill(NetReport? r)
    {
        HasData = r is { HasData: true };
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ResetText));
        if (r is null || !r.HasData)
        {
            Apps.Clear();
            HasApps = false;
            Bins = [];
            SelectedBin = -1;
            Happenings.Clear();
            Summary.Clear();
            HasHappenings = HasSummary = false;
            return;
        }

        WholeOnly = Live.StandardAccount && r.Apps.Count == 0;
        // No app to colour by: the graph shows the PC's use, lighter for what moved with nobody there.
        ChartMode = WholeOnly ? NetBarsMode.Background : NetBarsMode.App;
        _paths = r.Apps.ToDictionary(a => a.Id, a => a.Path);
        _steamPath = r.Apps.FirstOrDefault(a => a.Exe.Equals("steam.exe", StringComparison.OrdinalIgnoreCase))?.Path;

        // The line of figures.
        long all = r.Down + r.Up, away = r.AwayDown + r.AwayUp;
        TotalText = Units.Data(all);
        DownText = Units.Data(r.Down);
        UpText = Units.Data(r.Up);
        long behind = WholeOnly ? away : r.Background;
        BackgroundText = all > 0 ? $"{Math.Round(100.0 * behind / all):0}%" : "—";
        BackgroundTip = WholeOnly ? Lang.T("Used while nobody was at the PC") : away >= 1 << 20 ? Lang.T($"{Units.Data(away)} of it while you were away") : Lang.T("What apps used while they weren't the window in front");
        _usualPing = r.Line?.Usual;
        HasPing = r.Line is not null;
        UsualPingText = r.Line is { } line ? $"{line.Usual} ms" : "—";
        bool stopped = r.PingSwitches is { Count: > 0 } sw && !sw[^1].On;
        UsualPingWord = stopped ? Lang.T("usual ping, until checks were paused") : Lang.T("usual ping");
        UsualPingTip = stopped ? Lang.T($"Until ping checks were paused on {r.PingSwitches[^1].At:ddd d MMM} at {TimeText.At(r.PingSwitches[^1].At, "h:mm tt")}") : null;
        string MarkWhen(DateTime at) => Unit == ReportRange.Day ? TimeText.At(at, "h:mm tt") : $"{at:ddd} {TimeText.At(at, "h:mm tt")}";
        PingMarks = Models.Kept.Or(PingMarks, [.. r.PingSwitches.Select(s => new NetPingMark(s.At, s.On,
            s.On ? Lang.T($"Ping checks resumed · {MarkWhen(s.At)}") : Lang.T($"Ping checks paused · {MarkWhen(s.At)}")))]);
        HighPing = r.Line is { } l ? NetLinePing.HighFor(l.Usual) : 0;
        DropsWarn = r.Insights.Any(i => i.Key == "net-drops");
        (DropsTitle, DropsTip) = DropsText(r, PeriodWord);
        (DropsCount, DropsWord) = r.Drops.Count switch { 0 => (Lang.C("drops", "No"), Lang.T("drops")), 1 => ("1", Lang.T("drop")), var n => ($"{n}", Lang.T("drops")) };
        Drops = Models.Kept.Or(Drops, [.. r.Drops.Select(d => new NetDropSpan(TimeUtil.FromUnix(d.Start), TimeUtil.FromUnix(d.End)))]);

        // The graph.
        Bins = Models.Kept.Or(Bins, r.Bins);
        Link = Models.Kept.Or(Link, r.Link);
        if (SelectedBin >= Bins.Count) SelectedBin = -1;
        ChartTitle = r.Hourly ? Lang.T("Each hour") : r.Range == ReportRange.Year ? Lang.T("Each month") : Lang.T("Each day");
        _lineSentence = r.LineText;
        OnPropertyChanged(nameof(ShowPingNote));
        FillPingStatus();
        OnPropertyChanged(nameof(LineText));
        OnPropertyChanged(nameof(ChartHint));
        FillPingNote();
        ColorApps = Models.Kept.Or(ColorApps, r.ColorApps);
        var names = r.Apps.ToDictionary(a => a.Id, a => a.Name);
        if (!names.OrderBy(kv => kv.Key).SequenceEqual(AppNames.OrderBy(kv => kv.Key))) AppNames = names;
        FillLegend();

        FillApps();
        FillHappenings();
    }

    /// <summary>The colours' names next to the graph's title: the apps with their own (or in front and background), or the ping's marks.</summary>
    private void FillLegend()
    {
        IReadOnlyList<NetLegend> legend = IsPingView
            ? ShowPingNote ? []
            : [new("CpuBrush", Lang.T("Usual ping")), new("CpuBrush", Lang.T("Most checks"), 0.4), new("CpuBrush", Lang.T("All checks"), 0.18),
               .. Bins.Any(b => b.Ping?.LostPercent >= NetLine.LostWorthSaying) ? [new NetLegend("WarmBrush", Lang.T("Packet loss"))] : Array.Empty<NetLegend>(),
               .. Bins.Any(b => b.Game is not null) ? [new NetLegend("TextBrush", Lang.T("Playing"), 0.15)] : Array.Empty<NetLegend>()]
            : WholeOnly
            ? [new("SkyBrush", Lang.T("This PC")), new("SkyBrush", Lang.T("While you were away"), 0.45)]
            : [.. ColorApps.Take(NetBarsChart.AppBrushKeys.Length).Select((id, i) => (Key: NetBarsChart.AppBrushKeys[i], Name: AppNames.GetValueOrDefault(id)))
                    .Where(l => l.Name is not null).Select(l => new NetLegend(l.Key, l.Name!)),
               new(NetBarsChart.OtherBrushKey, Lang.T("Every other app"))];
        if (!IsPingView && Bins.Any(b => b.UnrecordedMinutes >= NetBarsChart.MinUnrecorded)) legend = [.. legend, new NetLegend("MutedBrush", Lang.T("Not recorded"), 0.7)];
        // The internet strip's colours, in both views (none while the Ping view's note stands in for the graph).
        if (Link.Count > 0 && !(IsPingView && ShowPingNote))
            legend = [.. legend, new NetLegend("GoodBrush", Lang.T("Internet up"), 0.5), .. Link.Any(c => c.State == NetLinkState.Dropped) ? [new NetLegend("WarmBrush", Lang.T("Dropped"))] : Array.Empty<NetLegend>()];
        Legend = Models.Kept.Or(Legend, legend);
    }

    private string DotKey(long app) => ColorApps.ToList().IndexOf(app) is int i and >= 0 && i < NetBarsChart.AppBrushKeys.Length
        ? NetBarsChart.AppBrushKeys[i] : NetBarsChart.OtherBrushKey;

    private void FillApps()
    {
        if (_report is not { } r)
        {
            Apps.Clear();
            return;
        }
        // The whole period, the stretch zoomed into, or the bar picked (each app as it was then).
        var picked = SelectedBin >= 0 && SelectedBin < r.Bins.Count ? r.Bins[SelectedBin] : null;
        var zoomBins = picked is null && Zoomed is { } z ? r.Bins.Where(b => b.Start >= z.From && b.Start < z.To).ToList() : null;
        AppsTitle = picked is not null ? Lang.T("Apps") + " · " + BinWords(SelectedBin)
            : Zoomed is { } zz ? Lang.T("Apps") + " · " + NetBarsChart.ZoomWords(zz, Step) : Lang.T("Apps");
        var byId = r.Apps.ToDictionary(a => a.Id);
        IEnumerable<NetAppUse>? uses = picked?.Apps ?? zoomBins?.SelectMany(b => b.Apps).GroupBy(u => u.App).Select(g => Sum(g.Key, g));
        IEnumerable<NetAppStat> source = uses is null ? r.Apps
            : uses.Where(u => byId.ContainsKey(u.App)).Select(u => byId[u.App] is var whole
                ? new NetAppStat { Id = whole.Id, Exe = whole.Exe, Name = whole.Name, Path = whole.Path, Category = whole.Category, Use = u }
                : null!);
        bool background = BackgroundOnly;
        var stats = source.Select(a => (Stat: a, Down: background ? a.Use.BgDown + a.Use.AwayDown : a.Use.Down, Up: background ? a.Use.BgUp + a.Use.AwayUp : a.Use.Up))
            .Select(x => (x.Stat, x.Down, x.Up, Key: Sort switch { "Download" => x.Down, "Upload" => x.Up, _ => x.Down + x.Up }))
            .Where(x => x.Key >= 1 << 20).OrderByDescending(x => x.Key).ToList();
        double max = stats.Count > 0 ? stats.Max(x => x.Key) : 1;
        // Each app's use day by day (a week, a month) or month by month (a year), when the list is the whole period's.
        var sparks = picked is null && zoomBins is null && r.Range != ReportRange.Day ? SparkBuckets(r) : null;
        // An app still at its place in the list keeps its row and is given the new figures: a list made anew has every
        // row of it built and laid out again, on each visit and each minute.
        for (int i = 0; i < stats.Count; i++)
        {
            var (stat, down, up, key) = stats[i];
            double length = key / max * 100, both = Math.Max(1, Sort == "Download" ? down : Sort == "Upload" ? up : down + up);
            var row = new NetAppRow
            {
                Stat = stat, BackgroundOnly = background,
                DownBar = Sort == "Upload" ? 0 : length * down / both, UpBar = Sort == "Download" ? 0 : length * up / both,
                Amount = Units.Data(key), DotKey = DotKey(stat.Id),
                Spark = sparks is null ? [] : Spark(stat, sparks),
            };
            if (i >= Apps.Count) Apps.Add(row);
            else if (Apps[i] is { } kept && kept.Stat.Id == stat.Id)
            {
                if (Models.Kept.Values(kept.Stat, stat) && kept.BackgroundOnly == row.BackgroundOnly && kept.DownBar == row.DownBar && kept.UpBar == row.UpBar
                    && kept.Amount == row.Amount && kept.DotKey == row.DotKey && kept.Spark.SequenceEqual(row.Spark)) continue;
                (kept.Stat, kept.BackgroundOnly, kept.DownBar, kept.UpBar, kept.Amount, kept.DotKey, kept.Spark) =
                    (stat, row.BackgroundOnly, row.DownBar, row.UpBar, row.Amount, row.DotKey, row.Spark);
                kept.Changed();
            }
            else Apps[i] = row;
        }
        while (Apps.Count > stats.Count) Apps.RemoveAt(Apps.Count - 1);
        HasApps = Apps.Count > 0;
    }

    private static NetAppUse Sum(long app, IEnumerable<NetAppUse> uses)
    {
        var u = new NetAppUse { App = app };
        foreach (var x in uses)
        {
            u.Down += x.Down; u.Up += x.Up; u.BgDown += x.BgDown; u.BgUp += x.BgUp;
            u.AwayDown += x.AwayDown; u.AwayUp += x.AwayUp; u.GameDown += x.GameDown; u.Lan += x.Lan;
        }
        return u;
    }

    /// <summary>The period's bars gathered into the small charts' buckets: days (a week's hours, a month's days) or months.</summary>
    private static List<(DateTime Start, bool Off, string? Why, Dictionary<long, long> Use)> SparkBuckets(NetReport r) =>
        [.. r.Bins.GroupBy(b => r.Hourly ? b.Start.Date : b.Start)
            .Select(g => (g.Key, Off: g.All(b => b.Total == 0), Why: g.All(b => b.OffWhy is not null) ? g.First().OffWhy : null,
                Use: g.SelectMany(b => b.Apps).GroupBy(a => a.App).ToDictionary(x => x.Key, x => x.Sum(a => a.Total))))];

    private IReadOnlyList<NetSparkBar> Spark(NetAppStat stat, List<(DateTime Start, bool Off, string? Why, Dictionary<long, long> Use)> buckets)
    {
        double top = Math.Max(1, buckets.Max(b => b.Use.GetValueOrDefault(stat.Id)));
        string format = Unit == ReportRange.Year ? "MMMM" : "ddd d MMM";
        return [.. buckets.Select(b =>
        {
            long v = b.Use.GetValueOrDefault(stat.Id);
            string off = b.Start > DateTime.Now ? Lang.T("Still to come") : b.Why ?? Lang.T("Not recorded");
            return new NetSparkBar(v / top, b.Off ? $"{b.Start.ToString(format)} · {off}" : $"{b.Start.ToString(format)} · {stat.Name} {Units.Data(v)}", b.Off);
        })];
    }

    /// <summary>"Tue 3 Nov, 7 PM – 8 PM", "Fri 30 Oct", "October" for bar <paramref name="i"/>.</summary>
    private string BinWords(int i)
    {
        var start = Bins[i].Start;
        return Hourly ? (Unit == ReportRange.Day ? "" : start.ToString("ddd d MMM") + ", ") + $"{TimeText.At(start, "h tt")} – {TimeText.At(BinEnd(i), "h tt")}"
            : Unit == ReportRange.Year ? start.ToString("MMMM") : start.ToString("ddd d MMM");
    }

    /// <summary>
    /// "What happened": the period's own lines (its insights) as a summary on top, with no time, then the timeline: each
    /// day under its heading (a Day view has none: the period picker already says which day), the line of the bar picked lit.
    /// </summary>
    private void FillHappenings()
    {
        var list = new List<NetHappeningRow>();
        var summary = new List<NetHappeningRow>();
        if (_report is { } r)
        {
            DateTime? day = null;
            foreach (var h in r.Happenings)
            {
                if (h.Day is null)
                {
                    summary.Add(new NetHappeningRow(false, h.Text) { Detail = h.Detail, Icon = h.Icon, Tone = h.Tone });
                    continue;
                }
                if (Unit != ReportRange.Day && h.Day != day) list.Add(new NetHappeningRow(true, DayHeading(h.Day.Value)));
                day = h.Day;
                int bin = h.At is { } at ? r.Bins.FindLastIndex(b => b.Start <= at) : -1;
                list.Add(new NetHappeningRow(false, h.Text)
                {
                    When = h.When, Detail = h.Detail, Amount = h.Amount, PingNote = h.PingNote, Path = h.Path, Icon = h.Icon, Tone = h.Tone, IsNew = h.IsNew,
                    Bin = bin, IsPicked = bin >= 0 && bin == SelectedBin,
                });
            }
        }
        if (!list.SequenceEqual(Happenings))
        {
            Happenings.Clear();
            foreach (var row in list) Happenings.Add(row);
        }
        if (!summary.SequenceEqual(Summary))
        {
            Summary.Clear();
            foreach (var row in summary) Summary.Add(row);
        }
        HasSummary = Summary.Count > 0;
        HasHappenings = HasSummary || Happenings.Count > 0;
    }

    private static string DayHeading(DateTime d) =>
        d == DateTime.Today ? Lang.T("Today") : d == DateTime.Today.AddDays(-1) ? Lang.T("Yesterday") : d.ToString("ddd d MMM");

    /// <summary>The Connection headline and the line under it (the dashboard's connection tile says them too).</summary>
    internal static (string Title, string Note) DropsText(NetReport r, string periodWord)
    {
        var drops = r.Drops;
        // "No drops", never "online all day": a day still going on, or one the PC was off for half of, wasn't that.
        string title = drops.Count switch
        {
            0 => Lang.T("No drops"),
            1 => Lang.T("Dropped once"),
            2 => Lang.T("Dropped twice"),
            var n => Lang.T($"Dropped {n} times"),
        };
        var parts = new List<string>();
        if (drops.Count > 0)
        {
            var longest = drops.MaxBy(d => d.Seconds)!;
            var at = TimeUtil.FromUnix(longest.Start);
            string took = Took(longest.Seconds);
            parts.Add(r.Range == ReportRange.Day ? Lang.T($"Longest {took} at {at:h:mm tt}") : Lang.T($"Longest {took} on {at:ddd d MMM}"));
        }
        else if (r.Range != ReportRange.Day) parts.Add(Lang.T($"The internet stayed up {periodWord}"));
        // Time the PC was on with nothing written isn't time without drops: say how long, so "No drops" isn't read as the whole day.
        if (r.Unrecorded.Count > 0)
        {
            string took = Took(r.Unrecorded.Sum(u => (u.End - u.Start).TotalSeconds));
            parts.Add(parts.Count == 0 && r.Range != ReportRange.Day ? Lang.T($"{took} not recorded {periodWord}") : Lang.T($"{took} not recorded"));
        }
        if (r.Range == ReportRange.Year && r.RecordedSince is { } since) parts.Add(Lang.T($"recorded since {since:d MMM}"));
        return (title, string.Join(" · ", parts));
    }

    /// <summary>"21m", "2h 51m", "40s", as every duration in the app is written (a drop is never "0s").</summary>
    internal static string Took(double seconds) => Units.Duration(Math.Max(1, seconds));
}
