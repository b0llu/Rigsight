using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Protocol;
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
        Live = new LiveData(Settings);
        Home = new HomeViewModel(Reports, Live);
        ReportsPage = new ReportsViewModel(Reports);
        Apps = new AppsViewModel(Reports, Settings);
        Crashes = new CrashesViewModel(Reports);
        Memory = new MemoryViewModel(Reports, Live);
        Storage = new StorageViewModel(Reports, Live);
        Widgets = new WidgetsViewModel(Settings, client);
        Overlay = new OverlayViewModel(Settings, client);
        SettingsPage = new SettingsViewModel(Settings, client, Reports);
        foreach (var config in Settings.Current.CustomPages) CustomPages.Add(CreateCustomPage(config));
        SettingsPage.GetCustomPages = () => CustomPages.Select(p => new PageOption(p.NavKey, p.Name));

        // Open on the page the user picked (if it still exists).
        var start = Settings.Current.StartPage;
        if (BuiltInPages.Any(p => p.Key == start) || FindCustomPage(start) is not null) _currentPage = start;
        foreach (var page in CustomPages) page.IsSelected = page.NavKey == _currentPage;

        Settings.Changed += () =>
        {
            Live.ApplySettings();
            Widgets.Refresh();
            Overlay.Refresh();
            SettingsPage.Refresh();
        };
        client.MessageReceived += OnMessage;
        client.ConnectionChanged += OnConnectionChanged;

        // Keep history-based pages fresh while open (the agent writes once a minute). Widget and overlay
        // previews are still snapshots: drawn when the page opens or something changes, not on this timer.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _refresh.Tick += (_, _) => { if (CurrentPage is not ("widgets" or "overlay")) _ = RefreshCurrentPageAsync(); };
        _refresh.Start();
        _ = RefreshCurrentPageAsync();

        // If the agent isn't running a moment after startup, start it.
        var launchCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        launchCheck.Tick += (_, _) =>
        {
            launchCheck.Stop();
            if (!IsConnected) StartAgent();
        };
        launchCheck.Start();
    }

    public SettingsModel Settings { get; }
    public ReportService Reports { get; }
    public LiveData Live { get; }
    public HomeViewModel Home { get; }
    public ReportsViewModel ReportsPage { get; }
    public AppsViewModel Apps { get; }
    public CrashesViewModel Crashes { get; }
    public MemoryViewModel Memory { get; }
    public StorageViewModel Storage { get; }
    public WidgetsViewModel Widgets { get; }
    public OverlayViewModel Overlay { get; }
    public SettingsViewModel SettingsPage { get; }

    /// <summary>Pages the user built ("Dashboards" in the sidebar).</summary>
    public ObservableCollection<CustomPageViewModel> CustomPages { get; } = [];

    /// <summary>Raised with a page's navigation key after it was deleted, so its view can be dropped.</summary>
    public event Action<string>? CustomPageDeleted;

    public CustomPageViewModel? FindCustomPage(string key) => CustomPages.FirstOrDefault(p => p.NavKey == key);

    private CustomPageViewModel CreateCustomPage(CustomPageConfig config) =>
        new(config, Settings, Live, Home, Crashes, open: p => CurrentPage = p.NavKey, delete: DeleteCustomPage);

    [RelayCommand]
    private void NewPage()
    {
        var names = CustomPages.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string name = "Dashboard";
        for (int i = 2; names.Contains(name); i++) name = $"Dashboard {i}";

        var page = CreateCustomPage(new CustomPageConfig { Name = name, Grid = CustomPageConfig.CurrentGrid });
        CustomPages.Add(page);
        page.AddStarterTiles();
        page.IsEditing = true;
        CurrentPage = page.NavKey;
    }

    private void DeleteCustomPage(CustomPageViewModel page)
    {
        var answer = MessageBox.Show($"Delete the dashboard \"{page.Name}\"? Its tiles are removed; your history isn't affected.",
            "Delete dashboard", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

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
        new("home", "Home"), new("reports", "Reports"), new("apps", "Apps"), new("crashes", "Crashes"),
        new("temperatures", "Temperatures"), new("memory", "Memory"), new("storage", "Storage"), new("sensors", "All sensors"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgentWarning), nameof(AgentHint), nameof(AgentButtonText))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgentWarning), nameof(AgentHint), nameof(AgentButtonText))]
    private bool _agentIsAdmin = true;

    /// <summary>Progress of the last start attempt; empty when there is nothing to report.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentHint))]
    private string _agentStatus = "";

    public bool ShowAgentWarning => !IsConnected || !AgentIsAdmin;

    public string AgentHint =>
        !string.IsNullOrEmpty(AgentStatus) ? AgentStatus
        : IsConnected ? "CPU temperatures, fans and voltages need admin rights."
        : "Tracking and some sensors need it.";

    public string AgentButtonText => IsConnected ? "Restart with admin" : "Start agent";

    /// <summary>Raised when the agent asks the app to come to the front.</summary>
    public event Action? ActivateRequested;

    partial void OnCurrentPageChanged(string value)
    {
        foreach (var page in CustomPages) page.IsSelected = page.NavKey == value;
        _ = RefreshCurrentPageAsync();
    }

    public async Task RefreshCurrentPageAsync()
    {
        switch (CurrentPage)
        {
            case "home": await Home.RefreshAsync(); break;
            case "reports": await ReportsPage.LoadAsync(); break;
            case "apps": await Apps.LoadAsync(); break;
            case "crashes": await Crashes.LoadAsync(); break;
            case "memory": await Memory.RefreshAsync(); break;
            case "storage": await Storage.RefreshAsync(); break;
            case "temperatures": await LoadTemperatureHistoryAsync(); break;
            case "widgets": Widgets.RequestPreviews(); break;
            case "overlay":
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

    /// <summary>The last day of minute history, for the temperature chart's 1-hour to 24-hour windows.</summary>
    private async Task LoadTemperatureHistoryAsync()
    {
        long now = Core.Data.TimeUtil.NowUnix();
        if (await Reports.MinutesAsync(now - 24 * 3600, now + 60) is { } minutes) Live.LoadMinuteHistory(minutes);
    }

    public void Navigate(string? page, string? arg)
    {
        if (!string.IsNullOrEmpty(page))
        {
            if (page == "reports" && arg == "yesterday")
            {
                CurrentPage = "reports";
                ReportsPage.ShowDay(DateTime.Today.AddDays(-1));
                return;
            }
            if (page == "apps" && arg is not null) Apps.SelectExe(arg);
            CurrentPage = page;
        }
        ActivateRequested?.Invoke();
    }

    [RelayCommand]
    private void Go(string page) => CurrentPage = page;

    [RelayCommand]
    private void StartAgent()
    {
        // Running but without admin rights: ask it to restart elevated (Windows shows one UAC prompt).
        if (IsConnected && !AgentIsAdmin)
        {
            AgentStatus = "Restarting the agent with admin rights…";
            _client.SendCommand("restart-elevated");
            return;
        }
        AgentStatus = "Starting the Rigsight agent…";
        if (!AgentLauncher.Start())
        {
            AgentStatus = "Couldn't start the agent. Try again, and accept the admin prompt.";
            return;
        }

        // If it still hasn't connected after a while (UAC declined, blocked by antivirus…), say so.
        _startTimeout?.Stop();
        _startTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _startTimeout.Tick += (_, _) =>
        {
            _startTimeout.Stop();
            if (!IsConnected) AgentStatus = "The agent didn't start. Try again, and accept the admin prompt.";
        };
        _startTimeout.Start();
    }

    private void OnConnectionChanged(bool connected)
    {
        bool wasConnected = IsConnected;
        IsConnected = connected;
        AgentStatus = "";
        // The agent may have just created the database (first run): load the page now rather than in a minute.
        if (connected) Settings.OnConnected();
        if (connected && !wasConnected) _ = RefreshCurrentPageAsync();
        if (!connected)
        {
            // Unknown until the agent says hello again; the sidebar already explains it isn't running.
            AgentIsAdmin = true;
            SettingsPage.AgentIsAdmin = true;
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
                // Without admin the agent can't read CPU temps, fans or voltages: offer the UAC prompt once per launch.
                if (!msg.IsAdmin && !_elevationRequested)
                {
                    _elevationRequested = true;
                    _client.SendCommand("restart-elevated");
                }
                SettingsPage.AgentIsAdmin = msg.IsAdmin;
                if (msg.StartupEnabled is bool startup) SettingsPage.StartupEnabled = startup;
                if (msg.Settings is not null) Settings.ApplyFromAgent(msg.Settings);
                Live.LoadHello(msg);
                ApplyOverlayState(msg);
                if (msg.Page is not null || msg.Arg is not null) Navigate(msg.Page, msg.Arg);
                break;
            case "tick":
                Live.ApplyTick(msg);
                break;
            case "procs":
                if (msg.Procs is not null) Live.ApplyProcs(msg.Procs);
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
        }
    }
}

public sealed record PageOption(string Key, string Name);
