using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>Root view model: navigation, the agent connection, and the page view models.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AgentClient _client;
    private readonly DispatcherTimer _refresh;
    private DispatcherTimer? _startTimeout;
    private bool _elevationRequested;

    public ShellViewModel(AgentClient client)
    {
        _client = client;
        Settings = new SettingsModel(client);
        Reports = new ReportService(Settings);
        Live = new LiveData(Settings) { Dashboards = () => CustomPages };
        Live.ProcessDetailChanged += apps => _client.SendCommand("procs-detail", apps);
        Live.ChartRangeChanged += () => _ = LoadTemperatureHistoryAsync();
        Home = new HomeViewModel(Reports, Live);
        ReportsPage = new ReportsViewModel(Reports);
        Apps = new AppsViewModel(Reports, Settings);
        Crashes = new CrashesViewModel(Reports, Settings);
        Timeline = new TimelineViewModel(Reports, openCrashes: day =>
        {
            Crashes.ShowDay(day);
            CurrentPage = "crashes";
        }, scan: ScanNowAsync);
        Memory = new MemoryViewModel(Reports, Live);
        Processes = new ProcessesViewModel(Reports, Live, (cmd, arg) => _client.SendCommand(cmd, arg));
        Storage = new StorageViewModel(Reports, Live);
        Fans = new FansViewModel(Reports, Live, Settings);
        Network = new NetworkViewModel(Reports, Live);
        Widgets = new WidgetsViewModel(Settings, client, Live);
        Overlay = new OverlayViewModel(Settings, client, Live);
        Taskbar = new TaskbarViewModel(Settings, Live);
        WhatsNew = new WhatsNewViewModel(Settings, Core.Updates.ReleaseFeed.Current, go: OpenFeature, factFor: FeatureFactAsync);
        SettingsPage = new SettingsViewModel(Settings, client, Reports);
        Update = new UpdateViewModel(client, agentCanInstall: () => IsConnected && AgentIsAdmin, autoUpdate: () => Settings.Current.AutoUpdate);
        SettingsPage.Update = Update;
        foreach (var config in Settings.Current.CustomPages) CustomPages.Add(CreateCustomPage(config));
        Sidebar = new SidebarViewModel(Settings, page => CurrentPage = page, CustomPages);
        Presets = new PresetPickerViewModel(Live, preset => NewPage(preset));
        Sidebar.Changed += UpdateDashboardsInNav;
        SettingsPage.Sidebar = Sidebar;
        SettingsPage.GetCustomPages = () => CustomPages.Select(p => new PageOption(p.NavKey, p.Name));
        // With its sensors counted by kind: a CPU listed with loads only is one read without its driver.
        SettingsPage.GetHardware = () => [.. Live.Hardware.Select(h => (h.Type,
            $"{h.Name} ({string.Join(", ", h.Sensors.GroupBy(s => s.Kind).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"))})"))];
        SettingsPage.GetReadings = () =>
        [
            $"CPU ({Live.CpuName}): temperature {Reading(Live.CpuTemp)}, load {Reading(Live.CpuLoad)}, power {Reading(Live.CpuPower)}, clock {Reading(Live.CpuClock)}",
            $"GPU ({Live.GpuName}): temperature {Reading(Live.GpuTemp)}, load {Reading(Live.GpuLoad)}, power {Reading(Live.GpuPower)}",
            $"Memory: in use {Reading(Live.RamLoad)}",
        ];

        // Open on the page the user picked (if it still exists).
        var start = Settings.Current.StartPage;
        if (BuiltInPages.Any(p => p.Key == start) || FindCustomPage(start) is not null) _currentPage = start;
        foreach (var page in CustomPages) page.IsSelected = page.NavKey == _currentPage;
        Sidebar.Select(_currentPage);

        Settings.Changed += () =>
        {
            ThemeManager.Apply(Settings.Current.Theme, Settings.Current.Accent);
            Live.ApplySettings();
            Widgets.Refresh();
            Overlay.Refresh();
            SettingsPage.Refresh();
            Sidebar.Refresh();
            UpdateSettingsAttention();
        };
        client.MessageReceived += OnMessage;
        client.ConnectionChanged += OnConnectionChanged;

        // Keep history-based pages fresh while open (the agent writes once a minute).
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _refresh.Tick += (_, _) =>
        {
            _ = RefreshTickAsync();
            if (!RigsightPaths.IsTestInstance) _ = Update.CheckIfDueAsync(); // a new day while the window stayed open
        };
        _refresh.Start();
        _ = RefreshCurrentPageAsync();

        // An update already found or downloaded shows at once; GitHub is asked once the window has settled (at most once a day).
        _ = Update.ShowKnownAsync();
        var updateCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        updateCheck.Tick += (_, _) =>
        {
            updateCheck.Stop();
            _ = Update.CheckIfDueAsync();
        };
        if (!RigsightPaths.IsTestInstance) updateCheck.Start(); // a test copy never looks for updates by itself

        // If the agent isn't connected a moment after startup, start it: at once when there's no agent at all, after
        // giving one that is running the time to answer.
        bool none = !RigsightPaths.IsTestInstance && !AgentLauncher.IsRunning();
        var launchCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(none ? 0.3 : 2.5) };
        launchCheck.Tick += (_, _) =>
        {
            launchCheck.Stop();
            if (IsConnected) return;
            if (!RigsightPaths.IsTestInstance) StartAgent(); // the tests start their own agent
            else AgentPending = false;
        };
        launchCheck.Start();
    }

    /// <summary>A What's new feature's "Open" button: its page.</summary>
    private void OpenFeature(string page) => CurrentPage = page;

    /// <summary>What a new feature's page already holds for this PC, for its What's new ("31 changes already found…").</summary>
    private async Task<string?> FeatureFactAsync(string page)
    {
        if (page != "timeline" || await Reports.TimelineAsync() is not { Changes.Count: > 0 } data) return null;
        var first = data.Changes.Min(c => c.Time);
        return $"{data.Changes.Count:N0} change{(data.Changes.Count == 1 ? "" : "s")} already found on this PC, back to {first:MMMM yyyy}.";
    }

    public SettingsModel Settings { get; }
    public ReportService Reports { get; }
    public LiveData Live { get; }
    public HomeViewModel Home { get; }
    public ReportsViewModel ReportsPage { get; }
    public AppsViewModel Apps { get; }
    public CrashesViewModel Crashes { get; }
    public TimelineViewModel Timeline { get; }
    private async Task JumpTimelineAsync(DateTime day)
    {
        await Timeline.LoadAsync();
        Timeline.JumpTo(day);
    }
    public MemoryViewModel Memory { get; }
    public ProcessesViewModel Processes { get; }
    public StorageViewModel Storage { get; }
    public FansViewModel Fans { get; }
    public NetworkViewModel Network { get; }
    public WidgetsViewModel Widgets { get; }
    public OverlayViewModel Overlay { get; }
    public TaskbarViewModel Taskbar { get; }
    public WhatsNewViewModel WhatsNew { get; }
    public SettingsViewModel SettingsPage { get; }
    public UpdateViewModel Update { get; }
    public SidebarViewModel Sidebar { get; }

    /// <summary>"New dashboard": pick a preset or a blank page.</summary>
    public PresetPickerViewModel Presets { get; }

    /// <summary>A dashboard is in the sidebar as its entry there is: hidden or in a folded group, only while it's the page you're on.</summary>
    private void UpdateDashboardsInNav()
    {
        foreach (var page in CustomPages) page.InNav = Sidebar.EntryOf(page.NavKey)?.IsVisible ?? true;
    }

    /// <summary>Pages the user built ("Dashboards" in the sidebar).</summary>
    public ObservableCollection<CustomPageViewModel> CustomPages { get; } = [];

    /// <summary>Raised with a page's navigation key after it was deleted, so its view can be dropped.</summary>
    public event Action<string>? CustomPageDeleted;

    public CustomPageViewModel? FindCustomPage(string key) => CustomPages.FirstOrDefault(p => p.NavKey == key);

    private CustomPageViewModel CreateCustomPage(CustomPageConfig config) =>
        new(config, Settings, Live, Home, Crashes, open: p => CurrentPage = p.NavKey, delete: DeleteCustomPage, net: NetToday);

    /// <summary>The internet right now and today, shared by every dashboard's internet tiles.</summary>
    public NetTodayViewModel NetToday => _netToday ??= new NetTodayViewModel(Reports, Live);
    private NetTodayViewModel? _netToday;

    /// <summary>A new dashboard from a preset (named after it; a blank one is "Dashboard"), or with the starter tiles when none is given.</summary>
    [RelayCommand]
    private void NewPage(Models.DashboardPreset? preset)
    {
        var names = CustomPages.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string first = preset is null || preset == Models.DashboardPresets.Blank ? "Dashboard" : preset.Name;
        string name = first;
        for (int i = 2; names.Contains(name); i++) name = $"{first} {i}";

        var page = CreateCustomPage(new CustomPageConfig { Name = name, Grid = CustomPageConfig.CurrentGrid });
        CustomPages.Add(page);
        if (Sidebar.DashboardsCollapsed) Sidebar.ToggleSectionCommand.Execute(SidebarViewModel.Dashboards); // show where it went
        if (preset is null) page.AddStarterTiles();
        else page.AddTiles(Models.DashboardPresets.TilesFor(preset, Presets.Available));
        page.IsEditing = preset is null || preset == Models.DashboardPresets.Blank; // a preset is ready as it is
        CurrentPage = page.NavKey;
    }

    /// <summary>Asks before a dashboard is deleted (the tests answer for the user).</summary>
    internal Func<CustomPageViewModel, bool> ConfirmDelete { get; set; } = page =>
        MessageBox.Show($"Delete the dashboard \"{page.Name}\"? Its tiles are removed; your history isn't affected.",
            "Delete dashboard", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void DeleteCustomPage(CustomPageViewModel page)
    {
        if (!ConfirmDelete(page)) return;

        Settings.Update(s =>
        {
            s.CustomPages.RemoveAll(p => p.Id == page.Id);
            if (s.StartPage == page.NavKey) s.StartPage = "home";
        });
        CustomPages.Remove(page);
        page.Dispose();
        if (CurrentPage == page.NavKey) CurrentPage = "home";
        CustomPageDeleted?.Invoke(page.NavKey);
    }

    [ObservableProperty] private string _currentPage = "home";

    /// <summary>Pages that can be chosen as the start page (besides dashboards).</summary>
    public static readonly IReadOnlyList<PageOption> BuiltInPages =
    [
        new("home", "Home"), new("reports", "Reports"), new("apps", "Apps"), new("processes", "Processes"), new("crashes", "Crashes"), new("timeline", "Timeline"),
        new("temperatures", "Temperatures"), new("fans", "Fans"), new("memory", "Memory"), new("storage", "Storage"), new("network", "Network"), new("sensors", "All sensors"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgentWarning), nameof(ShowAgentStarting), nameof(AgentHint), nameof(AgentButtonText), nameof(AgentOutOfDate))]
    private bool _isConnected;

    /// <summary>
    /// The agent is on its way: the window has just opened, the agent is being started, or it went a moment ago and may
    /// be straight back (it starts again by itself after a graphics driver changes). Nothing to warn about yet: the
    /// sidebar used to say "not running" with a Start button the moment the window opened, then "Starting…" with the
    /// button still there.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgentWarning), nameof(ShowAgentStarting))]
    private bool _agentPending = true;

    /// <summary>No agent after all (it didn't start, or went and stayed away): the boxes waiting for its readings stop saying so.</summary>
    partial void OnAgentPendingChanged(bool value)
    {
        if (!value && !IsConnected) Live.StopWaiting();
    }

    /// <summary>How long a connected agent is given to send its first readings before the boxes stop saying they're loading.</summary>
    private static readonly TimeSpan FirstReadings = TimeSpan.FromSeconds(10);
    private DispatcherTimer? _waitTimer;

    /// <summary>How long an agent that went is given to come back before the sidebar says it isn't running.</summary>
    private static readonly TimeSpan AgentGrace = TimeSpan.FromSeconds(5);
    private DispatcherTimer? _graceTimer;

    /// <summary>The agent that went a moment ago hasn't come back (and nothing is starting it): say so.</summary>
    internal void EndAgentGrace()
    {
        _graceTimer?.Stop();
        if (!IsConnected && string.IsNullOrEmpty(AgentStatus)) AgentPending = false;
    }

    /// <summary>
    /// The running agent's version when it isn't this window's (null: the same, or not known). An update that couldn't
    /// replace a running agent leaves the old one answering a newer window: what the newer one added arrives empty, and
    /// pages looked blank with nothing to say why.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgentWarning), nameof(AgentHint), nameof(AgentButtonText), nameof(AgentOutOfDate))]
    private string? _otherAgentVersion;

    public bool AgentOutOfDate => IsConnected && OtherAgentVersion is not null;

    private static readonly string AppVersion = typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>The agent's version if it differs from the app's. A test copy pairs with whatever agent the test gives it.</summary>
    internal static string? OtherVersion(string? agent, string app, bool testCopy) =>
        testCopy || string.IsNullOrWhiteSpace(agent) || agent == app ? null : agent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgentWarning), nameof(AgentHint), nameof(AgentButtonText))]
    private bool _agentIsAdmin = true;

    /// <summary>Progress of the last start attempt; empty when there is nothing to report.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentHint), nameof(ShowAgentStarting))]
    private string _agentStatus = "";

    public bool ShowAgentWarning => IsConnected ? !AgentIsAdmin || AgentOutOfDate : !AgentPending;

    /// <summary>The agent is being started: a quiet line saying so, with no button to press meanwhile.</summary>
    public bool ShowAgentStarting => !IsConnected && AgentPending && !string.IsNullOrEmpty(AgentStatus);

    public string AgentHint =>
        !string.IsNullOrEmpty(AgentStatus) ? AgentStatus
        : AgentOutOfDate ? $"It's version {OtherAgentVersion} and this window is {AppVersion}. Restart it so they match."
        : IsConnected ? "CPU temperatures, fans and voltages need admin rights."
        : "Tracking and some sensors need it.";

    public string AgentButtonText => !IsConnected ? "Start agent" : AgentOutOfDate ? "Restart agent" : "Restart with admin";

    /// <summary>Raised when the agent asks the app to come to the front.</summary>
    public event Action? ActivateRequested;

    partial void OnCurrentPageChanged(string value)
    {
        foreach (var page in CustomPages) page.IsSelected = page.NavKey == value;
        Sidebar.Select(value);
        _ = RefreshCurrentPageAsync();
    }

    /// <summary>
    /// A dot on Settings in the sidebar while something there still needs the user. Opening Settings doesn't clear it:
    /// dealing with the notice does (see <see cref="SettingsViewModel.SensorsNeedAttention"/>).
    /// </summary>
    [ObservableProperty] private bool _settingsNeedAttention;

    private void UpdateSettingsAttention() => SettingsNeedAttention = SettingsPage.SensorsNeedAttention;

    /// <summary>For the copied logs: a reading's figure, "none" for a sensor that gives nothing, "no sensor" where there isn't one.</summary>
    internal static string Reading(Models.SensorItem? sensor) => sensor is null ? "no sensor" : sensor.Value is null ? "none" : sensor.FormattedValue;

    public async Task RefreshCurrentPageAsync()
    {
        switch (CurrentPage)
        {
            case "home": await Home.RefreshAsync(); break;
            case "reports": await ReportsPage.LoadAsync(); break;
            case "apps": await Apps.LoadAsync(); break;
            // Coming back: rebuilt only if the crashes changed (rebuilding its cards froze the window for a quarter second).
            case "crashes": await Crashes.LoadAsync(onlyIfChanged: Crashes.Loaded); break;
            case "timeline": await Timeline.LoadAsync(); break;
            case "memory": await Memory.RefreshAsync(); break;
            case "storage": await Storage.RefreshAsync(); break;
            case "temperatures": await LoadTemperatureHistoryAsync(); break;
            case "fans": await Fans.RefreshAsync(); break;
            case "network": await Network.RefreshAsync(); break;
            case "widgets": Widgets.RequestPreviews(); break;
            case "overlay":
                Overlay.LoadSensors();
                Overlay.RequestPreview();
                Overlay.RequestStatus();
                break;
            case "settings":
                SettingsPage.Refresh();
                await SettingsPage.LoadKnownAppsAsync();
                break;
            default:
                if (FindCustomPage(CurrentPage) is { } custom)
                {
                    await custom.RefreshAsync();
                    await LoadTemperatureHistoryAsync(); // for a temperature chart tile's longer windows
                }
                break;
        }
    }

    private bool _ticking;
    private int _tickCount;

    /// <summary>
    /// The once-a-minute refresh of the page on screen. Only data that can still change is re-read (periods that
    /// include now), nothing while the window is minimized or hidden, and in ways that keep what you're looking
    /// at in place (scroll position, selection, expanded cards). Settings, widget and overlay pages don't show
    /// history, so they aren't refreshed.
    /// </summary>
    private async Task RefreshTickAsync()
    {
        if (_ticking || Application.Current?.MainWindow is not { IsVisible: true, WindowState: not WindowState.Minimized }) return;
        _ticking = true;
        _tickCount++;
        try
        {
            CheckNewDay();
            switch (CurrentPage)
            {
                case "home": await Home.RefreshAsync(); break;
                case "reports": if (ReportsPage.IncludesNow) await ReportsPage.LoadAsync(); break;
                case "apps": if (Apps.IncludesToday) await Apps.LoadAsync(); break;
                case "crashes": if (Crashes.IncludesToday) await Crashes.LoadAsync(onlyIfChanged: true); break;
                case "timeline": await Timeline.LoadAsync(onlyIfChanged: true); break;
                case "memory": await Memory.RefreshAsync(); break;
                // Free space changes slowly: every five minutes is plenty.
                case "storage": if (_tickCount % 5 == 0) await Storage.RefreshAsync(); break;
                case "fans": await Fans.RefreshAsync(); break;
                case "network": await Network.RefreshAsync(); break;
                case "temperatures": if (ChartDueNow) await LoadTemperatureHistoryAsync(); break;
                default:
                    if (FindCustomPage(CurrentPage) is { } custom)
                    {
                        await custom.RefreshAsync(quiet: true);
                        if (ChartDueNow) await LoadTemperatureHistoryAsync();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Core.Log.Error("refresh", ex);
        }
        finally
        {
            _ticking = false;
        }
    }

    /// <summary>The window is back from the taskbar: what it shows is as old as when it was put away, so it's read now.</summary>
    public Task RefreshOnRestoreAsync() => RefreshTickAsync();

    private DateTime _today = DateTime.Today;

    /// <summary>
    /// Midnight passed with the window open: every page with a date picker took "today" when it was made. A page on
    /// today (or this week, month, year) moves to the new day; one on an earlier period stays there, in the new day's
    /// words. Pages not on screen too, so going to one doesn't find it a day behind.
    /// </summary>
    internal void CheckNewDay()
    {
        var was = _today;
        if (DateTime.Today == was) return;
        _today = DateTime.Today;
        ReportsPage.NewDay(was);
        Apps.NewDay(was);
        Crashes.NewDay(was);
        Fans.NewDay(was);
        Network.NewDay(was);
        Live.NewDay(was);
        Controls.PeriodPicker.OnDayChanged();
    }

    /// <summary>An earlier day, week or month on the temperature chart never changes; anything else includes now.</summary>
    private bool ChartShowsNow => !(Live.IsChartPaged && Live.ChartPeriod.To <= DateTime.Today);

    /// <summary>
    /// Whether this minute's tick reads the chart's history again. Minutes: every minute. A week or a month of hours
    /// only gains a little in its last hour, and reading it adds up to 45,000 minutes: every five minutes. A year of
    /// days adds up every minute of the year (0.14 s for a year of evenings, 0.9 s for a PC that never sleeps, measured):
    /// every half hour. Changing the range or the period always reads at once.
    /// </summary>
    private bool ChartDueNow => ChartShowsNow && (!Live.IsChartLong || _tickCount % (Live.IsChartYear ? 30 : 5) == 0);

    private int _historyLoad;

    /// <summary>
    /// Minute history for the temperature chart: the last 24 hours (1-hour to 24-hour windows, and today),
    /// or the whole of an earlier day picked in day mode; for a week or a month, its hours instead.
    /// </summary>
    private async Task LoadTemperatureHistoryAsync()
    {
        int id = ++_historyLoad;
        Live.ChartHistoryStart ??= await Reports.FirstMinuteDayAsync();
        long now = Core.Data.TimeUtil.NowUnix();
        var day = Live.ChartDay;
        var (from, to) = Live.IsChartDay && day < DateTime.Today
            ? (Core.Data.TimeUtil.ToUnix(day), Core.Data.TimeUtil.ToUnix(day.AddDays(1)))
            : (now - 24 * 3600, now + 60);
        int window = Live.ChartWindowSeconds;
        var (periodFrom, periodTo) = Live.ChartPeriod;
        var minutes = Live.IsChartLong
            ? await Reports.TempHoursAsync(Core.Data.TimeUtil.ToUnix(periodFrom), Math.Min(Core.Data.TimeUtil.ToUnix(periodTo), now + 60), byDay: Live.IsChartYear)
            : await Reports.MinutesAsync(from, to);
        // Not if the range was changed again while this was read (the next load is on its way).
        if (id == _historyLoad && minutes is not null && window == Live.ChartWindowSeconds && day == Live.ChartDay) Live.LoadMinuteHistory(minutes, Live.ChartStepSeconds);
        // Why its empty stretches are empty, from Windows' own log (a year would mean reading all of it: those just say "Not recorded").
        if (id == _historyLoad && !Live.IsChartYear)
        {
            var since = (Live.IsChartLong ? periodFrom : Core.Data.TimeUtil.FromUnix(from)).AddDays(-2);
            var power = await Task.Run(() => Core.Stability.CrashLogReader.ReadPower(since));
            if (id == _historyLoad) Live.LoadPowerEvents(power);
        }
        if (id == _historyLoad && CurrentPage == "temperatures" && await Reports.RestDaysAsync(DateTime.Today) is { } rest) Live.LoadRest(rest);
    }

    private static RecapPeriod? RecapPeriodOf(string? arg) => arg switch
    {
        "last-week" => RecapPeriod.LastWeek,
        "last-month" => RecapPeriod.LastMonth,
        "last-year" => RecapPeriod.LastYear,
        _ => null,
    };

    public void Navigate(string? page, string? arg)
    {
        if (!string.IsNullOrEmpty(page))
        {
            // The recap of a whole period: last week, month or year.
            if (page == "reports" && RecapPeriodOf(arg) is { } period)
            {
                var (range, anchor) = HomeViewModel.Bounds(period, DateTime.Today);
                CurrentPage = "reports";
                ReportsPage.ShowPeriod(range, anchor);
                ActivateRequested?.Invoke();
                return;
            }
            // A recap: that day's report ("yesterday", or its date when it's from longer ago).
            if (page == "reports" && (arg == "yesterday" ? DateTime.Today.AddDays(-1)
                : DateTime.TryParseExact(arg, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : (DateTime?)null) is { } day)
            {
                CurrentPage = "reports";
                ReportsPage.ShowDay(day);
                ActivateRequested?.Invoke();
                return;
            }
            if (page == "update")
            {
                // The "new version" notification: the sidebar asks to restart (or shows the download) straight away.
                _ = Update.ShowKnownAsync(asked: true);
                ActivateRequested?.Invoke();
                return;
            }
            if (page == "apps" && arg is not null) Apps.ShowToday(arg);
            if (page == "widgets" && arg is not null) Widgets.Edit(arg); // "Edit widget…" on a widget on the desktop
            CurrentPage = page;
        }
        ActivateRequested?.Invoke();
    }

    [RelayCommand]
    private void Go(string page) => CurrentPage = page;

    [RelayCommand]
    private void StartAgent()
    {
        // Another version than this window: it's asked to quit, and this install's own is started once it has gone.
        if (AgentOutOfDate)
        {
            AgentStatus = "Restarting the agent…";
            _startAfterQuit = true;
            _client.SendCommand("quit");
            return;
        }
        // Running but without admin rights: ask it to restart elevated (Windows shows one UAC prompt).
        if (IsConnected && !AgentIsAdmin)
        {
            AgentStatus = "Restarting the agent with admin rights…";
            _client.SendCommand("restart-elevated");
            return;
        }
        AgentPending = true;
        AgentStatus = "Starting the background agent…";
        if (!AgentLauncher.Start())
        {
            AgentStatus = "Couldn't start the agent. Try again, and accept the admin prompt.";
            AgentPending = false;
            return;
        }

        // If it still hasn't connected after a while (UAC declined, blocked by antivirus…), say so.
        _startTimeout?.Stop();
        _startTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _startTimeout.Tick += (_, _) =>
        {
            _startTimeout.Stop();
            if (IsConnected) return;
            AgentStatus = NotStartedText(AgentLauncher.IsRunning());
            AgentPending = false;
        };
        _startTimeout.Start();
    }

    /// <summary>
    /// Why the agent isn't there after a start: running but silent (stuck, usually on hardware; clicking again won't help,
    /// a restart will), or not running at all (the admin prompt was declined, or something stopped it from starting).
    /// </summary>
    internal static string NotStartedText(bool processRunning) => processRunning
        ? "The agent is running but not answering. Restart your PC, then open Rigsight again."
        : "The agent didn't start. Try again and accept the admin prompt. If it still won't, restart your PC.";

    private bool _startAfterQuit;

    private void OnConnectionChanged(bool connected)
    {
        bool wasConnected = IsConnected;
        IsConnected = connected;
        AgentStatus = "";
        _graceTimer?.Stop();
        if (connected)
        {
            AgentPending = false;
            // An agent that is still finding the hardware (the PC has just started) sends readings a few seconds later.
            _waitTimer?.Stop();
            _waitTimer = new DispatcherTimer { Interval = FirstReadings };
            _waitTimer.Tick += (_, _) =>
            {
                _waitTimer.Stop();
                Live.StopWaiting();
            };
            _waitTimer.Start();
        }
        else if (wasConnected && !_startAfterQuit)
        {
            AgentPending = true;
            _graceTimer = new DispatcherTimer { Interval = AgentGrace };
            _graceTimer.Tick += (_, _) => EndAgentGrace();
            _graceTimer.Start();
        }
        // The agent may have just created the database (first run): load the page now rather than in a minute.
        if (connected && !wasConnected) _ = RefreshCurrentPageAsync();
        if (!connected)
        {
            OtherAgentVersion = null;
            if (wasConnected) Live.AgentGone();
            // Unknown until the agent says hello again; the sidebar already explains it isn't running.
            AgentIsAdmin = true;
            SettingsPage.AgentIsAdmin = true;
            // It was asked to quit so this install's own could take its place (see StartAgent).
            if (_startAfterQuit)
            {
                _startAfterQuit = false;
                StartAgent();
            }
        }
    }

    private void ApplyOverlayState(AgentMessage msg)
    {
        if (msg.OverlayVisible is bool visible) Overlay.IsVisible = visible;
        if (msg.OverlayHotkeyTaken is bool taken) Overlay.HotkeyTaken = taken;
        if (msg.RtssState is { } rtss) Overlay.RtssState = rtss;
    }

    private void OnMessage(AgentMessage msg)
    {
        switch (msg.T)
        {
            case "hello":
                AgentIsAdmin = msg.IsAdmin;
                OtherAgentVersion = OtherVersion(msg.Version, AppVersion, RigsightPaths.IsTestInstance);
                // Without admin the agent can't read CPU temps, fans or voltages: offer the UAC prompt once per launch.
                if (!msg.IsAdmin && !_elevationRequested && !RigsightPaths.IsTestInstance)
                {
                    _elevationRequested = true;
                    _client.SendCommand("restart-elevated");
                }
                SettingsPage.AgentIsAdmin = msg.IsAdmin;
                SettingsPage.AgentVersion = msg.Version;
                // The first hello comes before the hardware scan; the one with the hardware says what was left out.
                if (msg.Hardware is not null)
                {
                    SettingsPage.SensorStatus = msg.SensorStatus;
                    SettingsPage.IsRetryingSensors = false;
                    UpdateSettingsAttention();
                }
                if (msg.StartupEnabled is bool startup) SettingsPage.StartupEnabled = startup;
                if (msg.Settings is not null) Settings.ApplyFromAgent(msg.Settings);
                // A change made while the agent was away goes out now, after the hello: sent as soon as the pipe
                // connected, the hello's older copy would undo it (and flip the page back) until the agent echoed it.
                Settings.OnConnected();
                Live.LoadHello(msg);
                if (Live.ExpandedApps is { } expanded) _client.SendCommand("procs-detail", expanded); // a restarted agent
                ApplyOverlayState(msg);
                if (msg.Page is not null || msg.Arg is not null) Navigate(msg.Page, msg.Arg);
                break;
            case "tick":
                Live.ApplyTick(msg);
                break;
            case "procs":
                if (msg.Procs is not null) _ = ApplyProcsAsync(msg.Procs, msg.Disk);
                break;
            case "settings":
                if (msg.Settings is not null) Settings.ApplyFromAgent(msg.Settings);
                break;
            case "status":
                if (msg.StartupEnabled is bool enabled)
                {
                    SettingsPage.StartupEnabled = enabled;
                    SettingsPage.Refresh();
                }
                // The driver was looked at again, or is being installed: Settings says what is so now.
                if (msg.SensorStatus is not null)
                {
                    SettingsPage.SensorStatus = msg.SensorStatus;
                    UpdateSettingsAttention();
                }
                break;
            case "previews":
                Widgets.OnPreviewsReady();
                Overlay.OnPreviewsReady();
                break;
            case "overlay":
                ApplyOverlayState(msg);
                break;
            case "navigate":
                Navigate(msg.Page, msg.Arg);
                break;
            case "update":
                Update.OnAgentMessage(msg);
                break;
            case "scanned":
                _scanned?.TrySetResult();
                break;
            case "history-cleared":
                SettingsPage.OnHistoryCleared();
                break;
        }
    }

    private int _procsRead;

    /// <summary>
    /// The running apps, once the icons of any not seen before have been read (off the window's thread: the first list
    /// of a session names dozens). A list overtaken by a newer one meanwhile is dropped.
    /// </summary>
    private async Task ApplyProcsAsync(List<Core.Protocol.ProcInfo> procs, double? disk)
    {
        int id = ++_procsRead;
        if (procs.Any(p => !Services.IconCache.Has(p.Path))) await Task.Run(() => Services.IconCache.Warm(procs.Select(p => p.Path)));
        if (id == _procsRead) Live.ApplyProcs(procs, disk);
    }

    private TaskCompletionSource? _scanned;

    /// <summary>
    /// Has the agent look for new changes and problems now and waits until it has. Without an agent, or with one that
    /// doesn't answer (an older one, or a long first read of Windows' logs), it's given up on after a while.
    /// </summary>
    private async Task ScanNowAsync()
    {
        if (!IsConnected) return;
        var scanned = _scanned = new TaskCompletionSource();
        _client.SendCommand("scan-now");
        await Task.WhenAny(scanned.Task, Task.Delay(TimeSpan.FromSeconds(20)));
    }
}

public sealed record PageOption(string Key, string Name);
