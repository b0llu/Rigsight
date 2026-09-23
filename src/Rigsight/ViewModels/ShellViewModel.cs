using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Protocol;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>Root view model: navigation, the agent connection, and the page view models.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AgentClient _client;
    private readonly DispatcherTimer _refresh;
    private bool _launchAttempted;
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
        SettingsPage = new SettingsViewModel(Settings, client, Reports);

        Settings.Changed += () =>
        {
            Live.ApplySettings();
            Widgets.Refresh();
            SettingsPage.Refresh();
        };
        client.MessageReceived += OnMessage;
        client.ConnectionChanged += OnConnectionChanged;

        // Keep history-based pages fresh while open (the agent writes once a minute).
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _refresh.Tick += (_, _) => _ = RefreshCurrentPageAsync();
        _refresh.Start();

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
    public SettingsViewModel SettingsPage { get; }

    [ObservableProperty] private string _currentPage = "home";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgentWarning))]
    private bool _isConnected;

    [ObservableProperty] private bool _agentIsAdmin = true;
    [ObservableProperty] private string _agentStatus = "Connecting to the Rigsight agent…";

    public bool ShowAgentWarning => !IsConnected || !AgentIsAdmin;

    /// <summary>Raised when the agent asks the app to come to the front.</summary>
    public event Action? ActivateRequested;

    partial void OnCurrentPageChanged(string value) => _ = RefreshCurrentPageAsync();

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
            case "widgets": Widgets.RequestPreviews(); break;
            case "settings":
                SettingsPage.Refresh();
                await SettingsPage.LoadKnownAppsAsync();
                break;
        }
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
        // Only prompt for admin rights once per session if the startup task isn't available.
        bool started = AgentLauncher.Start(allowUacPrompt: !_launchAttempted);
        _launchAttempted = true;
        if (!started) AgentStatus = "The Rigsight agent isn't running.";
    }

    private void OnConnectionChanged(bool connected)
    {
        IsConnected = connected;
        AgentStatus = connected ? "" : "The Rigsight agent isn't running.";
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
                break;
            case "navigate":
                Navigate(msg.Page, msg.Arg);
                break;
        }
    }
}
