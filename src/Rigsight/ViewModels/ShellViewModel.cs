using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>Root view model: navigation, the agent connection, and the page view models.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AgentClient _client;

    // ── The history file's card (see HistoryRepair) ──────────────────────

    // Pages whose reading hit the damaged part of the history: each says so at its top until the file is repaired.
    private readonly HashSet<string> _unreadablePages = [];
    private bool _historyWasDamaged;

    /// <summary>The page on screen couldn't read part of its history (see <see cref="NoteUnreadable"/>).</summary>
    public bool PageUnreadable => _unreadablePages.Contains(CurrentPage) && Settings.Current.HistoryFile is { Damaged: true } or { Outcome: "waiting" };

    /// <summary>
    /// A reading on the page on screen failed because the history file is damaged (HistoryRepair.DamageSeen, any thread).
    /// The page loads right as it's opened, so the page on screen is the one that read it.
    /// </summary>
    private void NoteUnreadable() => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        if (_unreadablePages.Add(CurrentPage)) OnPropertyChanged(nameof(PageUnreadable));
    });

    /// <summary>The sidebar card about the history file, or null when there's nothing to say.</summary>
    public HistoryCard? HistoryCard => HistoryCard.Of(Settings.Current.HistoryFile);

    [RelayCommand]
    private void RepairHistory() => _client.SendCommand("repair-history");

    [RelayCommand]
    private void DismissHistoryCard() => _client.SendCommand("history-dismiss");

    [RelayCommand]
    private static void OpenHistoryFolder()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RigsightPaths.DataDir) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Write("history", $"The data folder couldn't be opened ({ex.Message})"); }
    }
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
        Live.ChartDetailWanted += () => _ = LoadZoomHistoryAsync();
        Home = new HomeViewModel(Reports, Live) { OpenChange = OpenChange, Open = OpenPage };
        ReportsPage = new ReportsViewModel(Reports) { OpenCrash = OpenCrash, OpenChange = OpenChange, Live = Live, OpenGame = OpenGame };
        Apps = new AppsViewModel(Reports, Settings) { OpenGame = exe => OpenGame(exe) };
        Crashes = new CrashesViewModel(Reports, Settings) { OpenChange = OpenChange, GoBackTo = page => CurrentPage = page, OpenGame = OpenGame };
        Timeline = new TimelineViewModel(Reports, openCrashes: day =>
        {
            Crashes.ShowDay(day);
            CurrentPage = "crashes";
        }, scan: ScanNowAsync, openCrash: OpenCrash, openProblems: (first, last) =>
        {
            Crashes.ShowPcProblems(first, last);
            CurrentPage = "crashes";
        });
        Timeline.OpenGameAt = key => OpenGame(key);
        Memory = new MemoryViewModel(Reports, Live);
        Processes = new ProcessesViewModel(Reports, Live, (cmd, arg) => _client.SendCommand(cmd, arg));
        Storage = new StorageViewModel(Reports, Live);
        Fans = new FansViewModel(Reports, Live, Settings);
        Network = new NetworkViewModel(Reports, Live);
        Widgets = new WidgetsViewModel(Settings, client, Live);
        Overlay = new OverlayViewModel(Settings, client, Live);
        Taskbar = new TaskbarViewModel(Settings, Live);
        Games = new GamesViewModel(Reports, Settings, Live, Overlay) { GoBackTo = page => CurrentPage = page };
        WhatsNew = new WhatsNewViewModel(Settings, Core.Updates.ReleaseFeed.Current, go: OpenFeature, factFor: FeatureFactAsync);
        SettingsPage = new SettingsViewModel(Settings, client, Reports) { SeeAllUpdatesCommand = WhatsNew.SeeAllCommand };
        Rewind = new global::Rigsight.Rewind.RewindViewModel(Reports, Settings) { OpenSettings = () => { SettingsPage.Tab = "rewind"; CurrentPage = "settings"; } };
        SettingsPage.Rewind = Rewind;
        Support = new SupportViewModel(Settings, Reports);
        // A rewind watched to its last card and closed: a good moment for the support card (it asks only if it may).
        Rewind.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Rewind.IsWatching) && !Rewind.IsWatching && Rewind.AtEnd)
                _ = Support.TryAskAsync("end of a rewind", stillGood: () => !Rewind.IsOpen);
        };
        Language = new LanguageViewModel(Settings);
        SettingsPage.Language = Language;
        Ask = new AskViewModel(Settings, Core.Ai.AiConversations.File) { OpenSettings = () => { SettingsPage.Tab = "ai"; CurrentPage = "settings"; }, OpenPage = page => CurrentPage = page };
        Crashes.Ask = Ask;
        SettingsPage.Ai = new AiSettingsViewModel(Settings, Ask);
        Ask.PerQuestion = () => SettingsPage.Ai?.ModelPerQuestion;
        // The newest pack after an update, if it didn't come with it: tried once more where updates may reach GitHub.
        _ = Language.CheckAtStartAsync(mayCheck: Settings.Current.AutoUpdate && !RigsightPaths.IsTestInstance);
        Update = new UpdateViewModel(client, agentCanInstall: () => IsConnected && AgentIsAdmin,
            // On a PC that keeps Rigsight up to date for everyone, the PC's updater does it for an admin too: their own
            // install would close the other people's Rigsight and couldn't start it again.
            autoUpdate: () => Settings.Current.AutoUpdate && !MachineSetup.KeepUpdated,
            pcUpdates: () => MachineSetup.KeepUpdated, needsAdmin: () => StandardAccount && !MachineSetup.KeepUpdated,
            // "Update automatically" off: RigSight doesn't contact GitHub by itself (a PC kept up to date for everyone,
            // set by an admin, still looks: that's the PC's own updater's choice).
            checksOn: () => Settings.Current.AutoUpdate || MachineSetup.KeepUpdated);
        SettingsPage.Update = Update;
        foreach (var config in Settings.Current.CustomPages) CustomPages.Add(CreateCustomPage(config));
        Sidebar = new SidebarViewModel(Settings, page => CurrentPage = page, CustomPages);
        Presets = new PresetPickerViewModel(Live, preset => NewPage(preset));
        Export = new ExportViewModel(Reports, Settings, Live);
        PageExport = new PageExportViewModel(Reports, Settings, Live);
        SettingsPage.OpenExport = Export.Open;
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
            ThemeManager.Apply(Settings.Current.Theme, Settings.Current.Accent, Settings.Current.KeepStatusColors);
            Live.ApplySettings();
            Widgets.Refresh();
            Overlay.Refresh();
            SettingsPage.Refresh();
            Sidebar.Refresh();
            UpdateSettingsAttention();
            OnPropertyChanged(nameof(HistoryCard));
            // Repaired (or found fine): every page reads its history whole again. Only on that change: a page that hit
            // the damage before the agent's check confirmed it keeps its note.
            bool damaged = Settings.Current.HistoryFile is { Damaged: true } or { Outcome: "waiting" };
            if (_historyWasDamaged && !damaged) _unreadablePages.Clear();
            _historyWasDamaged = damaged;
            OnPropertyChanged(nameof(PageUnreadable));
        };
        Rigsight.Core.Data.HistoryRepair.DamageSeen += _ => NoteUnreadable();
        client.MessageReceived += OnMessage;
        client.ConnectionChanged += OnConnectionChanged;

        // Keep history-based pages fresh while open (the agent writes once a minute).
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _refresh.Tick += (_, _) =>
        {
            _ = RefreshTickAsync();
            Rewind.CheckWhileOpen();
            if (!RigsightPaths.IsTestInstance) _ = Update.CheckIfDueAsync(); // a new day while the window stayed open
        };
        // The end-to-end check asks a test copy for this refresh itself, so that it falls where it is measured.
        if (RigsightPaths.IsTestInstance && Environment.GetEnvironmentVariable("RIGSIGHT_REFRESH_ON_SIGNAL") == "1") RefreshOnSignal();
        else _refresh.Start();
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
    private void OpenFeature(string page)
    {
        // "settings:rewind": a Settings tab of its own.
        if (page.StartsWith("settings:", StringComparison.Ordinal)) { SettingsPage.Tab = page["settings:".Length..]; page = "settings"; }
        CurrentPage = page;
    }

    /// <summary>What a new feature's page already holds for this PC, for its What's new ("31 changes already found…").</summary>
    private async Task<string?> FeatureFactAsync(string page)
    {
        if (page != "timeline" || await Reports.TimelineAsync() is not { Changes.Count: > 0 } data) return null;
        var first = data.Changes.Min(c => c.Time);
        int count = data.Changes.Count;
        return count == 1
            ? Lang.T($"1 change already found on this PC, back to {first:MMMM yyyy}.")
            : Lang.T($"{count:N0} changes already found on this PC, back to {first:MMMM yyyy}.");
    }

    public SettingsModel Settings { get; }
    public ReportService Reports { get; }
    public LiveData Live { get; }
    public HomeViewModel Home { get; }
    public ReportsViewModel ReportsPage { get; }
    public AppsViewModel Apps { get; }
    public GamesViewModel Games { get; }
    public CrashesViewModel Crashes { get; }
    public TimelineViewModel Timeline { get; }
    /// <summary>
    /// Opens what a day's big changes made better or worse, on the Timeline (from Home, Reports, Crashes): the same page
    /// wherever it was clicked.
    /// </summary>
    private async void OpenChange(DateTime day)
    {
        // The change is read and opened first, then the page shown: shown first, the Timeline's list flashed up for a
        // moment before the change opened over it (as a crash opened from another page did).
        await Timeline.LoadAsync(onlyIfChanged: true);
        bool opened = Timeline.OpenChange(day);
        CurrentPage = "timeline";
        if (!opened) Timeline.JumpTo(day);
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
    public SupportViewModel Support { get; }

    /// <summary>Rewind: the full screen when one is ready, watching, Share, and Settings → Rewind's list.</summary>
    public global::Rigsight.Rewind.RewindViewModel Rewind { get; }
    public SettingsViewModel SettingsPage { get; }
    public LanguageViewModel Language { get; }
    /// <summary>The Ask window over every page, and its button in the top bar or the sidebar.</summary>
    public AskViewModel Ask { get; }
    public UpdateViewModel Update { get; }
    public SidebarViewModel Sidebar { get; }

    /// <summary>"New dashboard": pick a preset or a blank page.</summary>
    public PresetPickerViewModel Presets { get; }
    /// <summary>The Export box over the window, opened from Settings.</summary>
    public ExportViewModel Export { get; }

    /// <summary>A page's own Export box, opened from its Export button (see <see cref="PageExports"/>).</summary>
    public PageExportViewModel PageExport { get; }

    /// <summary>The page's Export button: its own box, set to what the page shows.</summary>
    [RelayCommand]
    private void ExportPage()
    {
        if (PageExports.For(this, CurrentPage) is { } spec) PageExport.Open(spec);
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
        string first = preset is null || preset == Models.DashboardPresets.Blank ? Lang.T("Dashboard") : preset.Name;
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
        MessageBox.Show(Lang.T($"Delete the dashboard \"{page.Name}\"? Its tiles are removed; your history isn't affected."),
            Lang.T("Delete dashboard"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

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
        new("home", Lang.T("Home")), new("reports", Lang.T("Reports")), new("apps", Lang.T("Apps")), new("processes", Lang.T("Processes")), new("crashes", Lang.T("Crashes")), new("timeline", Lang.T("Timeline")),
        new("temperatures", Lang.T("Temperatures")), new("fans", Lang.T("Fans")), new("memory", Lang.T("Memory")), new("storage", Lang.T("Storage")), new("network", Lang.T("Network")), new("sensors", Lang.T("All sensors")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgentWarning), nameof(ShowAgentStarting), nameof(AgentHint), nameof(AgentButtonText), nameof(AgentOutOfDate), nameof(ShowStandardNote))]
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
    [NotifyPropertyChangedFor(nameof(ShowAgentWarning), nameof(AgentHint), nameof(AgentButtonText), nameof(AgentOutOfDate), nameof(ShowStandardNote))]
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

    /// <summary>The agent runs under a standard account: no admin rights and nobody to ask for them, so no warning either.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgentWarning), nameof(ShowStandardNote))]
    private bool _standardAccount;

    // Opened, or shown in a window that has since been closed: the note has been seen and never comes back.
    private bool _standardNoteShown;

    /// <summary>
    /// On a standard account, once: a plain note that some readings are off, leading to Settings' "Admin rights" tab.
    /// </summary>
    public bool ShowStandardNote
    {
        get
        {
            bool show = IsConnected && StandardAccount && !AgentOutOfDate && !Settings.Current.StandardNoteSeen;
            _standardNoteShown |= show;
            return show;
        }
    }

    /// <summary>The note's button: Settings, on the tab that says what is off; the note has done its job.</summary>
    [RelayCommand]
    private void OpenAdminRights()
    {
        StandardNoteSeen();
        SettingsPage.Tab = "admin";
        CurrentPage = "settings";
    }

    /// <summary>The window is closing: a note that was on show in it has been seen.</summary>
    public void WindowClosing()
    {
        if (_standardNoteShown) StandardNoteSeen();
    }

    private void StandardNoteSeen()
    {
        if (Settings.Current.StandardNoteSeen) return;
        Settings.Update(s => s.StandardNoteSeen = true);
        OnPropertyChanged(nameof(ShowStandardNote));
    }

    public bool ShowAgentWarning => IsConnected ? (!AgentIsAdmin && !StandardAccount) || AgentOutOfDate : !AgentPending;

    /// <summary>The agent is being started: a quiet line saying so, with no button to press meanwhile.</summary>
    public bool ShowAgentStarting => !IsConnected && AgentPending && !string.IsNullOrEmpty(AgentStatus);

    public string AgentHint =>
        !string.IsNullOrEmpty(AgentStatus) ? AgentStatus
        : AgentOutOfDate ? Lang.T($"It's version {OtherAgentVersion} and this window is {AppVersion}. Restart it so they match.")
        : IsConnected ? Lang.T("CPU temperatures, fans and voltages need admin rights.")
        : Lang.T("Tracking and some sensors need it.");

    public string AgentButtonText => !IsConnected ? Lang.T("Start agent") : AgentOutOfDate ? Lang.T("Restart agent") : Lang.T("Restart with admin");

    /// <summary>Raised when the agent asks the app to come to the front.</summary>
    public event Action? ActivateRequested;

    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(PageUnreadable));
        Games.IsShown = value == "games";
        if (value != "settings") SettingsPage.Ai?.ClosePickers();
        else if (SettingsPage.IsAi) SettingsPage.Ai?.Shown();
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
            case "games": await Games.LoadAsync(); break;
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

    /// <summary>A test copy only: the minute's refresh whenever its signal is set, in place of the clock.</summary>
    private void RefreshOnSignal()
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Rigsight.App.Refresh" + RigsightPaths.InstanceSuffix);
        new Thread(() =>
        {
            while (signal.WaitOne()) dispatcher.InvokeAsync(() => _ = RefreshTickAsync());
        })
        { IsBackground = true, Name = "RefreshOnSignal" }.Start();
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
                // A play ending or a game installed shows within two minutes; a game starting or closing within seconds by itself.
                case "games": if (_tickCount % 2 == 0) await Games.LoadAsync(); break;
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
                        await custom.RefreshAsync();
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

    /// <summary>An earlier day, 7 days, month or custom range on the temperature chart never changes; anything else includes now.</summary>
    private bool ChartShowsNow => !(Live.IsChartPaged && Live.ChartPeriod.To <= DateTime.Now);

    /// <summary>
    /// Whether this minute's tick reads the chart's history again. Minutes: every minute. A week or a month of hours
    /// only gains a little in its last hour, and reading it adds up to 45,000 minutes: every five minutes. A year of
    /// days adds up every minute of the year (0.14 s for a year of evenings, 0.9 s for a PC that never sleeps, measured):
    /// every half hour. Changing the range or the period always reads at once.
    /// </summary>
    private bool ChartDueNow => ChartShowsNow && (!Live.IsChartLong || _tickCount % (Live.ChartStepSeconds == 86400 ? 30 : 5) == 0);

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
        var (customFrom, customTo) = (Live.ChartCustomFrom, Live.ChartCustomTo);
        long periodEnd = Math.Min(Core.Data.TimeUtil.ToUnix(periodTo), now + 60);
        var minutes = Live.IsChartLong
            ? await Reports.TempHoursAsync(Core.Data.TimeUtil.ToUnix(periodFrom), periodEnd, byDay: Live.ChartStepSeconds == 86400)
            : Live.IsChartFixed ? await Reports.MinutesAsync(Core.Data.TimeUtil.ToUnix(periodFrom), periodEnd)
            : await Reports.MinutesAsync(from, to);
        // Not if the range was changed again while this was read (the next load is on its way).
        if (id == _historyLoad && minutes is not null && window == Live.ChartWindowSeconds && day == Live.ChartDay
            && customFrom == Live.ChartCustomFrom && customTo == Live.ChartCustomTo) Live.LoadMinuteHistory(minutes, Live.ChartStepSeconds);
        // Why its empty stretches are empty, from Windows' own log (a year would mean reading all of it: those just say "Not recorded").
        if (id == _historyLoad && Live.ChartStepSeconds < 86400)
        {
            var since = (Live.IsChartFixed ? periodFrom : Core.Data.TimeUtil.FromUnix(from)).AddDays(-2);
            var power = await Task.Run(() => Core.Stability.CrashLogReader.ReadPower(since));
            if (id == _historyLoad) Live.LoadPowerEvents(power);
        }
        // A zoom up to now gains its newest points too.
        if (id == _historyLoad && Live.ChartDetail is { } detail && detail.To > DateTime.Now.AddMinutes(-2)) await LoadZoomHistoryAsync();
        if (id == _historyLoad && CurrentPage == "temperatures" && await Reports.RestDaysAsync(DateTime.Today) is { } rest) Live.LoadRest(rest);
        if (id == _historyLoad && CurrentPage == "temperatures" && await Reports.HeatWeekAsync(DateTime.Today) is { } week) Live.HeatWeek = Models.Kept.Or(Live.HeatWeek, week);
    }

    private int _zoomLoad;

    /// <summary>
    /// A zoom into the temperature graph short enough for finer points than its range's (2 days or less of the last 7
    /// days or 30 from minutes, a stretch of 12 months from hours): read for that stretch alone.
    /// </summary>
    private async Task LoadZoomHistoryAsync()
    {
        int id = ++_zoomLoad;
        if (Live.ChartDetail is not { } detail) return;
        long from = Core.Data.TimeUtil.ToUnix(detail.From), to = Math.Min(Core.Data.TimeUtil.ToUnix(detail.To), Core.Data.TimeUtil.NowUnix() + 60);
        var minutes = detail.Step == 60 ? await Reports.MinutesAsync(from, to) : await Reports.TempHoursAsync(from, to, byDay: detail.Step == 86400);
        if (id == _zoomLoad && minutes is not null && Live.ChartDetail == detail) Live.LoadZoomHistory(minutes, detail);
    }

    private static RecapPeriod? RecapPeriodOf(string? arg) => arg switch
    {
        "last-week" => RecapPeriod.LastWeek,
        "last-month" => RecapPeriod.LastMonth,
        "last-year" => RecapPeriod.LastYear,
        _ => null,
    };

    /// <summary>
    /// Opens the crash that happened at <paramref name="time"/> as its own case on the Crashes page. The case is read
    /// first and the page shown after (shown first, the list or the last case flashed for a second while it was read),
    /// and its back button goes back to the page it was opened from, by that page's name.
    /// </summary>
    private async void OpenCrash(DateTime time)
    {
        string from = CurrentPage;
        await Crashes.ShowCrashAsync(time, from is "crashes" or "" ? null : (from, PageName(from)));
        CurrentPage = "crashes";
    }

    /// <summary>
    /// Opens where a line of Home leads (see InsightLinks), on the days the line speaks of: what moved against the usual
    /// opens the report of those days at its "Against your usual" card, problems open Crashes on those days; the rest
    /// open their page.
    /// </summary>
    private void OpenPage(HomeOpen open)
    {
        switch (open.Opens)
        {
            case Core.Reports.InsightOpens.Usual:
                ReportsPage.ShowUsual(open.Range, open.From, open.To);
                CurrentPage = "reports";
                break;
            case Core.Reports.InsightOpens.Crashes:
                Crashes.ShowDays(open.From, open.To.AddTicks(-1), open.PcOnly);
                CurrentPage = "crashes";
                break;
            case Core.Reports.InsightOpens.Temperatures: CurrentPage = "temperatures"; break;
            case Core.Reports.InsightOpens.Fans: CurrentPage = "fans"; break;
            case Core.Reports.InsightOpens.Network: CurrentPage = "network"; break;
            case Core.Reports.InsightOpens.Storage: CurrentPage = "storage"; break;
            case Core.Reports.InsightOpens.Game when open.Game is { } game: OpenGame(game); break;
        }
    }

    /// <summary>A page's name as the sidebar shows it ("Timeline", a dashboard's own name).</summary>
    private string PageName(string key) =>
        Sidebar.Main.Concat(Sidebar.Hardware).Concat(Sidebar.OnScreen).FirstOrDefault(e => e.Key == key)?.Title
        ?? CustomPages.FirstOrDefault(p => p.NavKey == key)?.Name
        ?? (key.Length > 0 ? char.ToUpperInvariant(key[0]) + key[1..] : Lang.T("Back"));

    /// <summary>A game named on another page, opened on the Games page with "Back to" that page.</summary>
    public void OpenGame(string key)
    {
        var from = CurrentPage;
        _ = Games.OpenKeyAsync(key, (from, PageName(from)));
        CurrentPage = "games";
    }

    public void Navigate(string? page, string? arg)
    {
        if (!string.IsNullOrEmpty(page))
        {
            // A crash's notification: that crash, opened (its time, in ticks).
            if (page == "crashes" && long.TryParse(arg, out long ticks) && ticks > 0 && ticks < DateTime.MaxValue.Ticks)
            {
                OpenCrash(new DateTime(ticks));
                ActivateRequested?.Invoke();
                return;
            }
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
            // A rewind's notification: watched straight away.
            if (page == "rewind")
            {
                Rewind.Watch(arg);
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
            // The after-game notification: that game's page (by its program).
            if (page == "games" && arg is not null) _ = Games.OpenKeyAsync(arg);
            if (page == "widgets" && arg is not null) Widgets.Edit(arg); // "Edit widget…" on a widget on the desktop
            CurrentPage = page;
        }
        ActivateRequested?.Invoke();
    }

    [RelayCommand]
    private void Go(string page) => CurrentPage = page;

    /// <summary>"About Rigsight" in the sidebar's menu: Settings, on its About tab.</summary>
    [RelayCommand]
    private void About()
    {
        SettingsPage.Tab = "about";
        CurrentPage = "settings";
    }

    [RelayCommand]
    private void StartAgent()
    {
        // Another version than this window: it's asked to quit, and this install's own is started once it has gone.
        if (AgentOutOfDate)
        {
            AgentStatus = Lang.T("Restarting the agent…");
            _startAfterQuit = true;
            _client.SendCommand("quit");
            return;
        }
        // Running but without admin rights: ask it to restart elevated (Windows shows one UAC prompt).
        if (IsConnected && !AgentIsAdmin)
        {
            AgentStatus = Lang.T("Restarting the agent with admin rights…");
            _client.SendCommand("restart-elevated");
            return;
        }
        AgentPending = true;
        AgentStatus = Lang.T("Starting the background agent…");
        if (!AgentLauncher.Start())
        {
            AgentStatus = Lang.T("Couldn't start the agent. Try again, and accept the admin prompt.");
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
        ? Lang.T("The agent is running but not answering. Restart your PC, then open RigSight again.")
        : Lang.T("The agent didn't start. Try again and accept the admin prompt. If it still won't, restart your PC.");

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
                StandardAccount = msg.StandardAccount;
                AgentIsAdmin = msg.IsAdmin;
                OtherAgentVersion = OtherVersion(msg.Version, AppVersion, RigsightPaths.IsTestInstance);
                // Without admin the agent can't read CPU temps, fans or voltages: offer the UAC prompt once per launch.
                // Not on a standard account, where the prompt asks for a password the person doesn't have.
                if (!msg.IsAdmin && !msg.StandardAccount && !_elevationRequested && !RigsightPaths.IsTestInstance)
                {
                    _elevationRequested = true;
                    _client.SendCommand("restart-elevated");
                }
                SettingsPage.AgentIsAdmin = msg.IsAdmin;
                SettingsPage.StandardAccount = msg.StandardAccount;
                Live.StandardAccount = msg.StandardAccount;
                Update.AccountChanged();
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
