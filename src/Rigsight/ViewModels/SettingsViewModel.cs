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
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

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
        ExcludedApps.Clear();
        foreach (var e in S.Tracking.ExcludedApps.Order(StringComparer.OrdinalIgnoreCase)) ExcludedApps.Add(e);
    }

    /// <summary>"History goes back to 12 September 2026 (13 days)."</summary>
    [ObservableProperty] private string? _trackingSince;

    public async Task LoadKnownAppsAsync()
    {
        TrackingSince = await reports.FirstDayAsync() is { } first
            ? $"History goes back to {first:d MMMM yyyy} ({(int)(DateTime.Today - first).TotalDays + 1} days)."
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
