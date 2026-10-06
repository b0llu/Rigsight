using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A wording Riggy was taught, as Settings lists it: the wording, and the topic it is read as.</summary>
public sealed record AskLearnedItem(Core.Ask.AskLearned Learned, string Topic)
{
    public string Text => Learned.Text;
    /// <summary>The wording in quotes, tidied (taking the time out of a question can leave a space before its question mark).</summary>
    public string Quoted => $"\u201C{Learned.Text.Replace(" ?", "?").Replace(" ,", ",").Trim()}\u201D";
}

/// <summary>A swatch in Settings' accent row.</summary>
public sealed record AccentOption(string Key, string Name, System.Windows.Media.Brush Brush);

/// <summary>Settings page. Every property reads from and writes to the shared settings model.</summary>
public sealed partial class SettingsViewModel(SettingsModel settings, AgentClient client, ReportService reports) : ObservableObject
{
    private RigsightSettings S => settings.Current;

    private void Set(Action<RigsightSettings> change, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        settings.Update(change);
        OnPropertyChanged(property);
    }

    // ── General ───────────────────────────────────────────────────────────
    public bool UseFahrenheit { get => S.UseFahrenheit; set => Set(s => s.UseFahrenheit = value); }
    public string Theme { get => S.Theme; set { if (value is not null) Set(s => s.Theme = value); } }
    public int LiveRefreshMs { get => S.LiveRefreshMs; set => Set(s => s.LiveRefreshMs = value); }

    /// <summary>The sidebar's pages, for the Sidebar card (set by the shell).</summary>
    public SidebarViewModel? Sidebar { get; set; }

    public string Accent { get => Accents.Normalize(S.Accent); set { if (value is not null) Set(s => s.Accent = value); } }

    private List<AccentOption>? _accentOptions;
    private (bool Light, System.Windows.Media.Color? Windows) _accentShades;

    /// <summary>
    /// The accent swatches, in the shades the current theme would use; Windows' own accent last. The same list until
    /// the shades change (a new one on every settings change would drop the picked swatch's ring).
    /// </summary>
    public List<AccentOption> AccentOptions
    {
        get
        {
            bool light = ThemeManager.IsLight;
            var shades = (light, Accents.WindowsAccent(light));
            if (_accentOptions is null || shades != _accentShades)
            {
                _accentShades = shades;
                _accentOptions = [.. Accents.All.Select(a => new AccentOption(a.Key, a.Name, Swatch(light ? a.OnLight : a.OnDark)))];
                if (shades.Item2 is { } windows) _accentOptions.Add(new AccentOption(Accents.Windows, "Windows accent", Swatch(windows)));
            }
            return _accentOptions;
        }
    }

    private static System.Windows.Media.SolidColorBrush Swatch(System.Windows.Media.Color c)
    {
        var b = new System.Windows.Media.SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Supplied by the shell: the user's dashboards, which can also be the start page.</summary>
    public Func<IEnumerable<PageOption>>? GetCustomPages { get; set; }
    public List<PageOption> StartPageOptions => [.. ShellViewModel.BuiltInPages, .. GetCustomPages?.Invoke() ?? []];
    public string StartPage
    {
        get => S.StartPage;
        set { if (value is not null) Set(s => s.StartPage = value); }
    }

    // ── Tracking ──────────────────────────────────────────────────────────
    public int SensorIntervalMs { get => S.Tracking.SensorIntervalMs; set => Set(s => s.Tracking.SensorIntervalMs = value); }
    public int ProcessIntervalSeconds { get => S.Tracking.ProcessIntervalSeconds; set => Set(s => s.Tracking.ProcessIntervalSeconds = value); }
    public int IdleMinutes { get => S.Tracking.IdleMinutes; set => Set(s => s.Tracking.IdleMinutes = value); }
    public bool FullscreenCountsAsActive { get => S.Tracking.FullscreenCountsAsActive; set => Set(s => s.Tracking.FullscreenCountsAsActive = value); }
    public int KeepHistoryDays
    {
        get => S.Tracking.KeepHistoryDays;
        set
        {
            int current = S.Tracking.KeepHistoryDays;
            if (value == current) return;
            // Shorter than now: say what goes before anything is lost (the agent tidies up once a day).
            bool shorter = value > 0 && (current == 0 || value < current);
            if (shorter)
            {
                string label = value switch { 90 => "3 months", 365 => "1 year", _ => "2 years" };
                var answer = MessageBox.Show(Application.Current.MainWindow,
                    $"History from before {DateTime.Today.AddDays(-value):d MMMM yyyy} will be permanently deleted: temperatures, app time, sessions and crashes.\n\nKeep history for {label}?",
                    "Keep history", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    // Put the choice back once the radio button has finished changing.
                    Application.Current.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(KeepHistoryDays)));
                    return;
                }
            }
            Set(s => s.Tracking.KeepHistoryDays = value);
        }
    }

    public bool IsPaused => S.Tracking.IsPaused(TimeUtil.NowUnix());
    public string? PauseText => S.Tracking.PausedUntil switch
    {
        -1 => "Tracking is paused until you resume it.",
        > 0 when S.Tracking.PausedUntil > TimeUtil.NowUnix() => $"Tracking is paused until {TimeUtil.FromUnix(S.Tracking.PausedUntil):h:mm tt}.",
        _ => null,
    };

    // ── Sensors ───────────────────────────────────────────────────────────
    /// <summary>Changing the switch is dealing with what it's about: the programs' notice counts as seen.</summary>
    public bool YieldToHardwareApps
    {
        get => S.YieldToHardwareApps;
        set => Set(s =>
        {
            s.YieldToHardwareApps = value;
            s.SensorNoticeSeen = Acknowledge(s.SensorNoticeSeen, SensorStatus, problems: false);
        });
    }

    /// <summary>What the agent isn't reading and why (from its hello).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SensorPausedText), nameof(SensorSkippedText), nameof(SensorsNeedAttention), nameof(AttentionSection), nameof(ShowPausedGotIt))]
    private SensorStatus? _sensorStatus;

    /// <summary>The programs' notice has a part not acknowledged yet: offer "Got it" next to it.</summary>
    public bool ShowPausedGotIt
    {
        get
        {
            var seen = SeenParts(S.SensorNoticeSeen);
            return NoticeParts(SensorStatus).Any(p => !IsProblem(p) && !seen.Contains(p));
        }
    }

    /// <summary>
    /// A problem that's gone is forgotten, so the same problem coming back later is pointed at again. (Programs aren't:
    /// once iCUE has been acknowledged, iCUE quitting and starting again isn't news.)
    /// </summary>
    partial void OnSensorStatusChanged(SensorStatus? value)
    {
        var seen = SeenParts(S.SensorNoticeSeen);
        var current = NoticeParts(value);
        if (seen.RemoveWhere(p => IsProblem(p) && !current.Contains(p)) > 0)
            settings.Update(s => s.SensorNoticeSeen = JoinParts(seen));
    }

    /// <summary>
    /// Something on the Sensors card still needs the user: a program's notice not yet acknowledged ("Got it" or the switch),
    /// or a problem not yet dealt with ("Try again"). Outlines the card, puts a dot by its title and on Settings in the sidebar.
    /// </summary>
    public bool SensorsNeedAttention => NeedsAttention(SensorStatus, S.SensorNoticeSeen);

    /// <summary>
    /// The card Settings scrolls to when it opens, while it needs attention (null: stay at the top). Other cards can join
    /// by naming themselves here and in the view (Attention.Section).
    /// </summary>
    public string? AttentionSection => _goTo ?? (SensorsNeedAttention ? "sensors" : null);

    private string? _goTo;

    /// <summary>Asks for Settings to be shown at one part of it (Riggy's, from a link in its chat), this once.</summary>
    public void GoTo(string section)
    {
        _goTo = section;
        OnPropertyChanged(nameof(AttentionSection));
    }

    /// <summary>The view has scrolled to where it was asked to: the next visit opens as usual.</summary>
    public void Arrived() => _goTo = null;

    /// <summary>
    /// What's left out and why, one part per program's hardware ("Fan hubs:iCUE") plus "safe" or "memory" for a problem.
    /// Each is pointed at until acknowledged; see <see cref="SensorsNeedAttention"/>.
    /// </summary>
    internal static HashSet<string> NoticeParts(SensorStatus? status)
    {
        var parts = new HashSet<string>(StringComparer.Ordinal);
        if (status is null) return parts;
        foreach (var p in status.Paused)
            foreach (var app in p.Because)
                parts.Add($"{p.Part}:{app}");
        if (status.SafeMode) parts.Add("safe");
        if (status.StoppedForMemory) parts.Add("memory");
        return parts;
    }

    internal static bool IsProblem(string part) => part is "safe" or "memory";

    internal static HashSet<string> SeenParts(string? seen) =>
        new((seen ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    internal static string? JoinParts(IEnumerable<string> parts) =>
        parts.Any() ? string.Join("|", parts.Order(StringComparer.Ordinal)) : null;

    internal static bool NeedsAttention(SensorStatus? status, string? seen)
    {
        var done = SeenParts(seen);
        return NoticeParts(status).Any(p => !done.Contains(p));
    }

    /// <summary>The seen list with the current programs' parts (<paramref name="problems"/> false) or problems (true) added.</summary>
    internal static string? Acknowledge(string? seen, SensorStatus? status, bool problems)
    {
        var done = SeenParts(seen);
        done.UnionWith(NoticeParts(status).Where(p => IsProblem(p) == problems));
        return JoinParts(done);
    }

    /// <summary>"Got it" on the programs' notice.</summary>
    [RelayCommand]
    private void AcknowledgePaused() => Set(s => s.SensorNoticeSeen = Acknowledge(s.SensorNoticeSeen, SensorStatus, problems: false));

    /// <summary>"Not read right now: fan hubs and power supply (iCUE), motherboard (Gigabyte Control Center)."</summary>
    public string? SensorPausedText => PausedText(SensorStatus);

    /// <summary>Why the risky parts are off after a scan that didn't finish or ran memory up; null when they aren't.</summary>
    public string? SensorSkippedText => SkippedText(SensorStatus);

    internal static string? PausedText(SensorStatus? status)
    {
        if (status is null || status.Paused.Count == 0) return null;
        // Per program, in the order they're first named: its parts, lower-case, joined the way a sentence would.
        var byApp = new List<(string App, List<string> Parts)>();
        foreach (var p in status.Paused)
            foreach (var app in p.Because)
            {
                int i = byApp.FindIndex(x => x.App == app);
                if (i < 0) byApp.Add((app, [p.Part.ToLowerInvariant()]));
                else byApp[i].Parts.Add(p.Part.ToLowerInvariant());
            }
        static string Join(List<string> parts) => parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1];
        return "Not read right now: " + string.Join(", ", byApp.Select(x => $"{Join(x.Parts)} ({x.App})")) + ".";
    }

    internal static string? SkippedText(SensorStatus? status) => status switch
    {
        { StoppedForMemory: true } => "Windows' memory kept growing while the motherboard, fan hubs and power supply were read, so Rigsight stopped reading them. Try again once your PC has restarted.",
        { SafeMode: true } => "The last time Rigsight read your hardware it didn't finish (your PC may have frozen or been restarted), so the motherboard, fan hubs and power supply are skipped for now.",
        _ => null,
    };

    /// <summary>"Try again" on a problem: dealt with (if it happens again, it's pointed at again), and everything is read again.</summary>
    [RelayCommand]
    private void RetrySensors()
    {
        Set(s => s.SensorNoticeSeen = Acknowledge(s.SensorNoticeSeen, SensorStatus, problems: true));
        client.SendCommand("sensors-retry");
    }

    /// <summary>Supplied by the shell: the hardware as the agent listed it (type, name).</summary>
    public Func<IReadOnlyList<(string, string)>>? GetHardware { get; set; }

    [ObservableProperty] private string? _reportStatus;

    [RelayCommand]
    private void CopyProblemReport()
    {
        string report = ProblemReport.ForThisPc(AppVersion, client.IsConnected, AgentIsAdmin, GetHardware?.Invoke() ?? [], SensorStatus);
        try
        {
            Clipboard.SetText(report);
            ReportStatus = "Copied. Paste it wherever you're asking for help.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ReportStatus = "Couldn't reach the clipboard. Try again.";
        }
    }

    public ObservableCollection<string> ExcludedApps { get; } = [];
    public ObservableCollection<string> KnownApps { get; } = [];
    [ObservableProperty] private string? _appToExclude;

    // ── Notifications ─────────────────────────────────────────────────────
    public bool AlertsEnabled { get => S.Alerts.Enabled; set => Set(s => s.Alerts.Enabled = value); }
    public double CpuLimit { get => S.Alerts.CpuLimit; set { Set(s => s.Alerts.CpuLimit = Math.Round(value)); OnPropertyChanged(nameof(CpuLimitText)); } }
    public double GpuLimit { get => S.Alerts.GpuLimit; set { Set(s => s.Alerts.GpuLimit = Math.Round(value)); OnPropertyChanged(nameof(GpuLimitText)); } }
    public double HotSpotLimit { get => S.Alerts.GpuHotSpotLimit; set { Set(s => s.Alerts.GpuHotSpotLimit = Math.Round(value)); OnPropertyChanged(nameof(HotSpotLimitText)); } }
    public string CpuLimitText => Units.TempShort(CpuLimit);
    public string GpuLimitText => Units.TempShort(GpuLimit);
    public string HotSpotLimitText => Units.TempShort(HotSpotLimit);
    public int SustainSeconds { get => S.Alerts.SustainSeconds; set => Set(s => s.Alerts.SustainSeconds = value); }
    public int CooldownMinutes { get => S.Alerts.CooldownMinutes; set => Set(s => s.Alerts.CooldownMinutes = value); }
    public bool SessionSummaries { get => S.Alerts.SessionSummaries; set => Set(s => s.Alerts.SessionSummaries = value); }
    public bool DailyRecap { get => S.Alerts.DailyRecap; set => Set(s => s.Alerts.DailyRecap = value); }
    public bool CrashNotifications { get => S.Alerts.CrashNotifications; set => Set(s => s.Alerts.CrashNotifications = value); }
    public bool QuietDuringFullscreen { get => S.Alerts.QuietDuringFullscreen; set => Set(s => s.Alerts.QuietDuringFullscreen = value); }
    public int SessionSummaryMinMinutes { get => S.Alerts.SessionSummaryMinMinutes; set => Set(s => s.Alerts.SessionSummaryMinMinutes = value); }
    public int CardSeconds { get => S.Alerts.CardSeconds; set => Set(s => s.Alerts.CardSeconds = value); }
    public string NotificationStyle { get => S.Alerts.Style.ToString(); set => Set(s => s.Alerts.Style = Enum.Parse<NotificationStyle>(value)); }

    /// <summary>Shows an example notification so you can see exactly what it looks like.</summary>
    [RelayCommand]
    private void Preview(string kind) => client.SendCommand("preview-notification", kind);

    // ── Agent ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _startupEnabled;
    /// <summary>True until the agent reports otherwise, so no admin warning shows while it isn't connected.</summary>
    [ObservableProperty] private bool _agentIsAdmin = true;
    [ObservableProperty] private string? _statusMessage;
    /// <summary>Riggy's bubble (Ask) is shown.</summary>
    public bool ShowAsk
    {
        get => S.ShowAsk;
        set => Set(s => s.ShowAsk = value);
    }

    public AskViewModel? Ask { get; set; }

    public bool AskHasLearned => AskLearned.Count > 0;

    /// <summary>The wordings Riggy was taught, newest first, each with what it is read as.</summary>
    public IReadOnlyList<AskLearnedItem> AskLearned => Ask is null ? []
        : [.. Ask.Learned.Reverse().Select(l => new AskLearnedItem(l, AskViewModel.Topics.FirstOrDefault(t => t.Intent == l.Intent)?.Label ?? l.Intent.ToString()))];

    [RelayCommand]
    private void ForgetAsk()
    {
        Ask?.Forget();
        OnPropertyChanged(nameof(AskHasLearned));
        OnPropertyChanged(nameof(AskLearned));
    }

    /// <summary>The Microsoft runtime Riggy's model needs is missing or old on this PC (see <see cref="AskEmbedder.RuntimeReady"/>).</summary>
    public bool AskNeedsRuntime => S.ShowAsk && AskEmbedder.FilesPresent && !AskEmbedder.RuntimeReady;

    /// <summary>
    /// Has the browser fetch the add-on from Microsoft (its own link to the 64-bit Visual C++ Redistributable, the one
    /// file needed; the page that lists every edition asked people to pick). Rigsight downloads nothing itself.
    /// </summary>
    [RelayCommand]
    private static void GetAskRuntime()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://aka.ms/vs/17/release/vc_redist.x64.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("settings", ex);
        }
    }

    /// <summary>Questions Riggy had no answer for, kept on this PC: to copy and send to whoever can add them.</summary>
    public int AskUnansweredCount => Ask?.Unanswered.Count ?? 0;
    public string AskUnansweredText => AskUnansweredCount == 1 ? "1 question, kept on this PC. Nothing is sent." : $"{AskUnansweredCount} questions, kept on this PC. Nothing is sent.";
    [ObservableProperty] private string? _askCopied;

    [RelayCommand]
    private void CopyAskUnanswered()
    {
        if (Ask is not { Unanswered.Count: > 0 } ask) return;
        try
        {
            System.Windows.Clipboard.SetText("Questions Riggy couldn't answer (Rigsight " + AppVersion + "):" + Environment.NewLine + string.Join(Environment.NewLine, ask.Unanswered.Select(q => "- " + q)));
            AskCopied = "Copied. Paste it wherever you send feedback.";
        }
        catch (Exception ex)
        {
            Log.Error("settings", ex);
        }
    }

    [RelayCommand]
    private void ForgetAskItem(AskLearnedItem item)
    {
        Ask?.Forget(item.Learned);
        OnPropertyChanged(nameof(AskHasLearned));
        OnPropertyChanged(nameof(AskLearned));
    }

    public bool AutoUpdate
    {
        get => S.AutoUpdate;
        set { Set(s => s.AutoUpdate = value); Update?.OnAutoUpdateChanged(); }
    }

    /// <summary>Supplied by the shell: the Updates section.</summary>
    public UpdateViewModel? Update { get; set; }

    public string AppVersion { get; } = "v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>Changes the startup task through the agent (it has the admin rights needed).</summary>
    public bool StartWithWindows
    {
        get => StartupEnabled;
        set
        {
            client.SendCommand(value ? "startup-on" : "startup-off");
            StartupEnabled = value;
            OnPropertyChanged();
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        OnPropertyChanged(nameof(Accent)); // again, after the swatches (new ones in a new theme) are in
        ExcludedApps.Clear();
        foreach (var e in S.Tracking.ExcludedApps.Order(StringComparer.OrdinalIgnoreCase)) ExcludedApps.Add(e);
    }

    /// <summary>"History goes back to 12 September 2026 (13 days)."</summary>
    [ObservableProperty] private string? _trackingSince;

    public async Task LoadKnownAppsAsync()
    {
        TrackingSince = await reports.FirstDayAsync() is { } first
            ? $"History goes back to {first:d MMMM yyyy} ({Controls.PeriodPicker.Duration(TimeSpan.FromDays((int)(DateTime.Today - first).TotalDays + 1))})."
            : "Nothing recorded yet.";
        var apps = await reports.KnownAppsAsync() ?? [];
        KnownApps.Clear();
        foreach (var a in apps.Where(a => a.Category != AppCategory.System).OrderBy(a => a.Exe, StringComparer.OrdinalIgnoreCase))
            KnownApps.Add(a.Exe);
    }

    [RelayCommand]
    private void AddExcluded()
    {
        var exe = AppToExclude?.Trim();
        if (string.IsNullOrEmpty(exe)) return;
        if (!exe.Contains('.')) exe += ".exe";
        settings.Update(s =>
        {
            if (!s.Tracking.ExcludedApps.Contains(exe, StringComparer.OrdinalIgnoreCase)) s.Tracking.ExcludedApps.Add(exe);
        });
        AppToExclude = null;
        Refresh();
    }

    [RelayCommand]
    private void RemoveExcluded(string? exe)
    {
        if (exe is null) return;
        settings.Update(s => s.Tracking.ExcludedApps.RemoveAll(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase)));
        Refresh();
    }

    // The Pause row itself shows the result once the agent sends the new settings.
    [RelayCommand]
    private void Pause(string minutes) => client.SendCommand("pause", minutes);

    [RelayCommand]
    private void Resume() => client.SendCommand("resume");

    [RelayCommand]
    private static void OpenDataFolder()
    {
        Directory.CreateDirectory(RigsightPaths.DataDir);
        Process.Start(new ProcessStartInfo(RigsightPaths.DataDir) { UseShellExecute = true });
    }

    /// <summary>Settings → About: opens the licences of what Rigsight includes (a text file installed next to the app).</summary>
    [RelayCommand]
    private static void OpenNotices()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        try
        {
            if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("settings", ex);
        }
    }

    /// <summary>Settings → About: opens the Buy Me a Coffee page in the browser; the app itself never contacts it.</summary>
    [RelayCommand]
    private static void Support()
    {
        try { Process.Start(new ProcessStartInfo("https://buymeacoffee.com/bollu") { UseShellExecute = true })?.Dispose(); } catch { }
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export app usage",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"Rigsight-app-usage-{DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dialog.ShowDialog() != true) return;

        var report = await reports.BuildRangeAsync(DateTime.Today.AddYears(-20), DateTime.Today.AddDays(1));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder("App,Exe,Category,Active hours,Background hours,Minimized hours,Away hours,Sessions,Avg CPU °C,Peak CPU °C,Avg GPU °C,Peak GPU °C,Peak memory MB\n");
        foreach (var a in report?.Apps ?? [])
        {
            sb.AppendLine(string.Join(",", Csv(a.Name), Csv(a.Exe), a.Category,
                (a.ActiveSec / 3600).ToString("0.00", inv), (a.BackgroundSec / 3600).ToString("0.00", inv),
                (a.MinimizedSec / 3600).ToString("0.00", inv), (a.AwaySec / 3600).ToString("0.00", inv), a.SessionCount,
                a.CpuTempAvg?.ToString("0.0", inv), a.CpuTempMax?.ToString("0.0", inv),
                a.GpuTempAvg?.ToString("0.0", inv), a.GpuTempMax?.ToString("0.0", inv), a.MemMax?.ToString("0", inv)));
        }
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), Encoding.UTF8);
            StatusMessage = $"Exported to {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusMessage = "Export failed: " + ex.Message;
        }

        static string Csv(string v) => v.Contains(',') || v.Contains('"') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        var answer = MessageBox.Show(Application.Current.MainWindow,
            "This permanently deletes all recorded activity, temperatures and reports. Settings are kept.\n\nDelete all history?",
            "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        client.SendCommand("clear-history");
        StatusMessage = "History cleared.";
    }

    [RelayCommand]
    private void QuitAgent()
    {
        var answer = MessageBox.Show(Application.Current.MainWindow,
            "Quitting stops tracking, the tray icon and widgets until the agent starts again (at next sign-in, or when you open Rigsight).\n\nQuit the Rigsight agent?",
            "Quit agent", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) client.SendCommand("quit");
    }
}
