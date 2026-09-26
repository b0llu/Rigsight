using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Controls;
using Rigsight.Core.Stability;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

public sealed partial class CrashesViewModel(ReportService reports, SettingsModel settings) : ObservableObject
{
    /// <summary>The period shown (see <see cref="PeriodPicker"/>): this month unless picked otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote), nameof(IsDay), nameof(ShowTimeline), nameof(EmptyText), nameof(IncludesToday))]
    private Core.Reports.ReportRange _unit = Core.Reports.ReportRange.Month;

    /// <summary>Any day in the period shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote), nameof(IncludesToday))]
    private DateTime _anchor = DateTime.Today;

    /// <summary>First day with anything recorded (tracking, or crashes Windows logged before Rigsight was installed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote))]
    private DateTime? _since;

    /// <summary>A custom range's start and end (whole hours), when <see cref="Unit"/> is Custom.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote), nameof(IsDay), nameof(ShowTimeline), nameof(EmptyText), nameof(IncludesToday))]
    private DateTime _customFrom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeNote), nameof(IsDay), nameof(ShowTimeline), nameof(EmptyText), nameof(IncludesToday))]
    private DateTime _customTo;

    partial void OnCustomFromChanged(DateTime value) => CustomChanged();
    partial void OnCustomToChanged(DateTime value) => CustomChanged();

    // Both ends usually change together: one load for the pair.
    private bool _customPending;

    private void CustomChanged()
    {
        if (Unit != Core.Reports.ReportRange.Custom || _customPending) return;
        _customPending = true;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            _customPending = false;
            _limit = PageSize;
            _ = LoadAsync();
        });
    }

    /// <summary>One day, or a custom range of up to two days: listed as it happened, without the day strip.</summary>
    public bool IsDay => Core.Reports.ReportBuilder.IsDayLike(Unit, Bounds.From, Bounds.To);

    /// <summary>The period shown includes today, so new crashes can appear.</summary>
    public bool IncludesToday => Bounds.To > DateTime.Today;
    public bool ShowTimeline => !IsDay;

    /// <summary>List every crash newest first (default), or group repeats of the same crash into one row.</summary>
    [ObservableProperty] private bool _grouped;

    partial void OnGroupedChanged(bool value)
    {
        _limit = PageSize;
        Regroup();
        ApplyView();
    }

    partial void OnAnchorChanged(DateTime value)
    {
        _limit = PageSize;
        if (Unit is not (Core.Reports.ReportRange.All or Core.Reports.ReportRange.Custom)) _ = LoadAsync();
    }

    [RelayCommand]
    private void OpenDay(DateTime day) => ShowDay(day);

    /// <summary>Shows one day's crashes (clicking a day on the timeline).</summary>
    public void ShowDay(DateTime day)
    {
        Anchor = day.Date;
        Unit = Core.Reports.ReportRange.Day;
    }

    /// <summary>Which dates are covered. Windows' crash log is read back 90 days when Rigsight is installed,
    /// so "All time" can reach further back than the rest of the app's history.</summary>
    public string RangeNote => PeriodPicker.Span(Unit, Anchor, Since, CustomFrom, CustomTo);

    /// <summary>The period's start and end; all time runs from the first recorded problem.</summary>
    private (DateTime From, DateTime To) Bounds => Unit == Core.Reports.ReportRange.All
        ? (Since ?? DateTime.Today, DateTime.Today.AddDays(1))
        : PeriodPicker.Bounds(Unit, Anchor, CustomFrom, CustomTo);

    private DateTime From => Bounds.From;

    /// <summary>"All", "Apps" (app and game crashes, freezes) or "Pc" (blue screens, sudden shutdowns, driver resets, freezes).</summary>
    [ObservableProperty] private string _filter = "All";

    /// <summary>Show muted apps' crashes (faded) in the list.</summary>
    [ObservableProperty] private bool _showMuted;

    private List<CrashRow> _all = [];
    private List<CrashGroup> _groups = [];
    private List<SystemChange> _changes = [];

    /// <summary>Problems as they're listed: repeats and bursts grouped, newest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrashes), nameof(EmptyText))]
    private List<CrashGroup> _groupsShown = [];

    // Years of history can hold thousands of crashes: build the newest cards, and more as the page scrolls.
    private const int PageSize = 50;
    private int _limit = PageSize;
    private List<CrashGroup> _matching = [];

    public bool HasMore => _matching.Count > GroupsShown.Count;
    /// <summary>Called by the page's infinite scroll near the bottom.</summary>
    [RelayCommand]
    private void ShowMore()
    {
        if (!HasMore) return;
        _limit += PageSize;
        ShowPage();
    }

    private void ShowPage()
    {
        GroupsShown = [.. _matching.Take(_limit)];
        OnPropertyChanged(nameof(HasMore));
    }

    /// <summary>Individual crashes (not muted) for the current filter, newest first (dashboard tile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Latest))]
    private List<CrashRow> _crashes = [];

    /// <summary>The newest of <see cref="Crashes"/>, if any (dashboard tile).</summary>
    public CrashRow? Latest => Crashes.FirstOrDefault();

    [ObservableProperty] private List<CrashDay> _days = [];
    [ObservableProperty] private List<string> _patterns = [];

    [ObservableProperty] private int _appCrashCount;
    [ObservableProperty] private int _systemCount;
    [ObservableProperty] private int _driverResetCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMuted), nameof(MutedToggleText))]
    private int _mutedCount;

    public bool HasMuted => MutedCount > 0;
    public string MutedToggleText => $"Show muted ({MutedCount})";

    [ObservableProperty] private string _statusTitle = "";
    [ObservableProperty] private string _statusDetail = "";
    /// <summary>The palette brush for the status (a key, so it follows the dark or light theme).</summary>
    [ObservableProperty] private string _statusBrush = "MutedBrush";
    [ObservableProperty] private string _statusIcon = "";

    public bool HasCrashes => GroupsShown.Count > 0;

    public string EmptyText => Unit == Core.Reports.ReportRange.Day ? "No crashes on this day" : Filter switch
    {
        "Apps" => "No app or game crashes in this period",
        "Pc" => "No PC crashes, driver resets or sudden shutdowns in this period",
        _ => "No crashes in this period",
    };

    partial void OnFilterChanged(string value) { _limit = PageSize; ApplyView(); }
    partial void OnShowMutedChanged(bool value) { _limit = PageSize; ApplyView(); }
    partial void OnUnitChanged(Core.Reports.ReportRange value) { _limit = PageSize; _ = LoadAsync(); }

    private int _loadId;

    /// <param name="onlyIfChanged">The minute refresh: leave the page alone unless a crash arrived.</param>
    public async Task LoadAsync(bool onlyIfChanged = false)
    {
        int id = ++_loadId;
        var since = await reports.FirstCrashDayAsync();
        var all = Unit == Core.Reports.ReportRange.All;
        var (from, to) = all ? (DateTime.Today.AddYears(-20), DateTime.Today.AddDays(1)) : Bounds;
        if (to > DateTime.Now) to = DateTime.Now.AddMinutes(1);
        var rows = await reports.CrashesAsync(from, to, includeMuted: true) ?? [];
        if (onlyIfChanged && rows.Select(r => r.Event.Id).SequenceEqual(_all.Select(r => r.Event.Id))) return;
        // Drivers and updates from a week before the oldest problem, to explain what came after.
        var changesFrom = (rows.Count > 0 ? rows.Min(c => c.Time) : from).Date.AddDays(-7);
        if (!all && changesFrom > from.AddDays(-7)) changesFrom = from.AddDays(-7);
        var changes = await ReportService.ChangesAsync(changesFrom, to);
        if (id != _loadId) return;

        Since = since;
        _all = rows;
        _changes = changes;
        Regroup();
        ApplyView();
    }

    private void Regroup()
    {
        // Cards the user opened ("All N times", "Details") stay open when the list is rebuilt.
        static string KeyOf(CrashGroup g) => $"{g.IsIncident}|{g.First.Event.Id}";
        var open = _groups.Where(g => g.IsExpanded || g.ShowDetails).ToDictionary(KeyOf, g => (g.IsExpanded, g.ShowDetails));
        _groups = CrashGroup.Build(_all, groupRepeats: Grouped);
        foreach (var g in _groups)
            if (open.TryGetValue(KeyOf(g), out var state)) (g.IsExpanded, g.ShowDetails) = state;
        foreach (var g in _groups) g.ChangesText = ChangesBefore(g);
    }

    /// <summary>Re-applies mute state, counts, timeline, status and the visible list.</summary>
    private void ApplyView()
    {
        var s = settings.Current;
        foreach (var g in _groups) g.IsMuted = g.AppExe is { } exe && s.IsCrashMuted(exe);
        var counted = _groups.Where(g => !g.IsMuted).ToList();
        var rows = counted.SelectMany(g => g.Rows).OrderByDescending(r => r.Time).ToList();

        MutedCount = _groups.Where(g => g.IsMuted).Sum(g => g.Count);
        AppCrashCount = rows.Count(c => c.Event.Kind is CrashKind.AppCrash or CrashKind.AppHang);
        SystemCount = rows.Count(c => c.Event.Kind is CrashKind.SystemCrash or CrashKind.UnexpectedShutdown);
        DriverResetCount = rows.Count(c => c.Event.Kind == CrashKind.GpuDriverReset);

        bool Matches(CrashGroup g) => Filter switch
        {
            "Apps" => !g.IsIncident && !g.Latest.IsSystem,
            "Pc" => g.IsIncident || g.Latest.IsSystem,
            _ => true,
        };
        _matching = [.. _groups.Where(g => (ShowMuted || !g.IsMuted) && Matches(g))];
        ShowPage();
        Crashes = [.. counted.Where(Matches).SelectMany(g => g.Rows).OrderByDescending(r => r.Time)];
        Days = IsDay ? [] : CrashStrip.BuildDays(From, Bounds.To, counted, _changes);
        Patterns = BuildPatterns(rows);
        BuildStatus(rows, [.. counted.Where(g => g.IsIncident).SelectMany(g => g.Rows)]);
    }

    // ── Status line ─────────────────────────────────────────────────────

    private void BuildStatus(List<CrashRow> rows, HashSet<CrashRow> inIncidents)
    {
        static string Ago(DateTime t)
        {
            int days = (int)(DateTime.Today - t.Date).TotalDays;
            return days switch { 0 => "today", 1 => "yesterday", _ => $"{days} days ago" };
        }
        var serious = rows.Where(r => CrashGroup.SeverityOf(r) >= CrashSeverity.Serious || inIncidents.Contains(r)).ToList();
        var bsod = rows.FirstOrDefault(r => r.Event.Kind == CrashKind.SystemCrash);
        var app = rows.FirstOrDefault(r => r.Event.Kind is CrashKind.AppCrash or CrashKind.AppHang);
        var lastSerious = serious.FirstOrDefault();

        if (IsDay || !IncludesToday)
        {
            // One day, or a period that's over: say what happened in it rather than "stable for N days" (counted to today).
            string label = PeriodPicker.Text(Unit, Anchor, CustomFrom, CustomTo);
            string when = Unit == Core.Reports.ReportRange.Day ? "on this day" : label.StartsWith("Last ") ? label.ToLowerInvariant() : $"in {label}";
            int worst = rows.Count == 0 ? -1 : rows.Max(r => (int)CrashGroup.SeverityOf(r));
            StatusTitle = rows.Count == 0 ? $"No problems {when}" : $"{rows.Count} problem{(rows.Count == 1 ? "" : "s")} {when}";
            StatusDetail = rows.Count == 0 ? "Nothing crashed, froze or shut down unexpectedly."
                : string.Join(" · ", rows.GroupBy(r => r.Event.Kind).Select(g => KindCount(g.Key, g.Count()))) + ".";
            (StatusIcon, StatusBrush) = worst switch
            {
                >= (int)CrashSeverity.Critical => ("", "HotBrush"),
                >= (int)CrashSeverity.Serious => ("", "WarmBrush"),
                >= (int)CrashSeverity.Minor => ("", "WarmBrush"),
                _ => ("", "GpuBrush"),
            };
            return;
        }
        var parts = new List<string>
        {
            bsod is null ? "No blue screens in this period" : $"Last blue screen {Ago(bsod.Time)}",
            app is null ? "no app crashes" : $"last app crash {Ago(app.Time)}",
        };
        int asleep = rows.Count(r => r.Event.Kind == CrashKind.UnexpectedShutdown && r.Event.DuringSleep);
        if (asleep > 0) parts.Add($"{asleep} power loss{(asleep == 1 ? "" : "es")} while asleep (not a fault)");
        StatusDetail = string.Join(" · ", parts) + ".";

        int daysSince = lastSerious is null ? int.MaxValue : (int)(DateTime.Today - lastSerious.Time.Date).TotalDays;
        (StatusTitle, StatusIcon, StatusBrush) = daysSince switch
        {
            <= 7 when lastSerious!.Event.Kind is CrashKind.SystemCrash =>
                ($"Blue screen {Ago(lastSerious.Time)}", "", "HotBrush"),
            <= 7 when lastSerious!.Event.Kind is CrashKind.UnexpectedShutdown =>
                ($"Your PC shut off unexpectedly {Ago(lastSerious.Time)}", "", "HotBrush"),
            <= 7 when inIncidents.Contains(lastSerious!) => ($"Your PC froze {Ago(lastSerious.Time)}", "", "WarmBrush"),
            <= 7 => ($"Graphics trouble {Ago(lastSerious!.Time)}", "", "WarmBrush"),
            int.MaxValue => (rows.Count == 0 ? "All clear" : "No serious problems", "", "GpuBrush"),
            _ => ($"Stable for {daysSince} days", "", "GpuBrush"),
        };
    }

    private static string KindCount(CrashKind kind, int n) => kind switch
    {
        CrashKind.AppCrash => n == 1 ? "1 app crash" : $"{n} app crashes",
        CrashKind.AppHang => n == 1 ? "1 app froze" : $"{n} apps froze",
        CrashKind.GpuDriverReset => n == 1 ? "1 graphics driver reset" : $"{n} graphics driver resets",
        CrashKind.SystemCrash => n == 1 ? "1 blue screen" : $"{n} blue screens",
        _ => n == 1 ? "1 unexpected shutdown" : $"{n} unexpected shutdowns",
    };

    // ── Patterns ────────────────────────────────────────────────────────

    /// <summary>Things that only show across several problems (repeats of one app are shown by the grouping itself).</summary>
    private static List<string> BuildPatterns(List<CrashRow> rows)
    {
        var list = new List<string>();
        var shutdowns = rows.Where(c => c.Event.Kind == CrashKind.UnexpectedShutdown).ToList();
        int asleep = shutdowns.Count(c => c.Event.DuringSleep);
        if (asleep >= 2)
            list.Add($"{asleep} of {shutdowns.Count} unexpected shutdowns happened while the PC was asleep. If you switch off power at the wall, shut down fully first; otherwise a BIOS or chipset driver update often fixes sleep problems.");

        int gpuRelated = rows.Count(c => c.Culprit.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                         c.Culprit.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                                         c.Culprit.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
                                         c.Event.Kind == CrashKind.GpuDriverReset ||
                                         c.Event.Code is "0x116" or "0x119" or "0x117");
        if (gpuRelated >= 2)
            list.Add($"{gpuRelated} problems involved the graphics driver. A clean driver reinstall (and stock GPU clocks) is the first thing to try.");

        int hot = rows.Count(c => c.GpuBefore >= 83 || c.CpuBefore >= 88);
        if (hot >= 1)
            list.Add($"{hot} crash{(hot == 1 ? "" : "es")} happened while your hardware was running hot. Check airflow and fan curves.");
        return list;
    }

    // ── What changed before ─────────────────────────────────────────────

    private string? ChangesBefore(CrashGroup g)
    {
        // Only where a driver or update could plausibly be the cause: PC-level problems (any driver), and app
        // crashes inside a graphics driver or DirectX (graphics drivers and Windows updates). A bug in an app's
        // own code isn't explained by a network driver installed the same week.
        bool graphics = g.Latest.Culprit is "NVIDIA driver" or "AMD driver" or "Intel graphics driver" or "DirectX" or "Vulkan" or "Graphics driver"
                        or "GPU driver" or "VIDEO_TDR_FAILURE" or "VIDEO_SCHEDULER_INTERNAL_ERROR";
        // Graphics problems point at graphics drivers (and Windows updates); other PC-level problems can be any driver.
        bool pcLevel = (g.IsIncident || g.Severity >= CrashSeverity.Serious) && !graphics;
        if (!pcLevel && !graphics) return null;
        if (g.Latest.Event.Kind == CrashKind.UnexpectedShutdown && g.Latest.Event.DuringSleep) return null;

        var first = g.First.Time;
        // Graphics drivers and Windows updates are the usual suspects; take the nearest two in the week before.
        var before = _changes.Where(c => c.Time < first && c.Time >= first.AddDays(-7))
            .Where(c => pcLevel || c.Kind == ChangeKind.WindowsUpdate || c.Title.Contains("graphics", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Title.Contains("graphics", StringComparison.OrdinalIgnoreCase) || c.Kind == ChangeKind.WindowsUpdate)
            .ThenByDescending(c => c.Time)
            .Take(2)
            .OrderBy(c => c.Time)
            .ToList();
        if (before.Count == 0) return null;
        string list = string.Join(", ", before.Select(c => $"{c.Title} ({When(c.Time, first)})"));
        return g.Count > 1 ? $"In the week before the first time: {list}" : $"In the week before: {list}";

        static string When(DateTime change, DateTime problem)
        {
            int days = (int)(problem.Date - change.Date).TotalDays;
            return days switch { 0 => "same day", 1 => "the day before", _ => $"{days} days before" };
        }
    }

    // ── Actions ─────────────────────────────────────────────────────────

    [RelayCommand]
    private static void ToggleExpand(CrashGroup g) => g.IsExpanded = !g.IsExpanded;

    [RelayCommand]
    private static void ToggleDetails(CrashGroup g) => g.ShowDetails = !g.ShowDetails;

    [RelayCommand]
    private void ToggleMute(CrashGroup g)
    {
        if (g.AppExe is not { } exe) return;
        bool mute = !g.IsMuted;
        settings.Update(s =>
        {
            s.MutedCrashApps.RemoveAll(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase));
            if (mute) s.MutedCrashApps.Add(exe);
        });
        ApplyView();
    }

    [RelayCommand]
    private static void Search(CrashGroup g) => Open("https://www.google.com/search?q=" + Uri.EscapeDataString(g.SearchQuery));

    [RelayCommand]
    private static void ShowDump(CrashGroup g)
    {
        if (g.DumpPath is not { } path) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private async Task Copy(CrashGroup g)
    {
        try
        {
            Clipboard.SetText(Report(g));
            g.Copied = true;
            await Task.Delay(2000);
            g.Copied = false;
        }
        catch
        {
            // The clipboard can be briefly locked by another app; the user can simply click again.
        }
    }

    /// <summary>A plain-text summary for forums or support: what, when, why, context, and this PC's hardware.</summary>
    public static string Report(CrashGroup g)
    {
        var sb = new StringBuilder();
        string kind = g.KindLabel.ToLowerInvariant();
        sb.AppendLine(g.Title.Contains(kind, StringComparison.OrdinalIgnoreCase) ? $"Problem: {g.Title}" : $"Problem: {g.Title} ({kind})");
        sb.AppendLine(g.IsIncident
            ? $"When: {g.First.Time:dddd d MMMM yyyy, h:mm tt} – {g.Latest.Time:h:mm tt}"
            : g.Count == 1
            ? $"When: {g.Latest.Time:dddd d MMMM yyyy, h:mm tt}"
            : $"When: {g.Count} times, most recently {g.Latest.Time:d MMM yyyy, h:mm tt} (first {g.First.Time:d MMM yyyy})");
        sb.AppendLine($"What happened: {g.Reason}");
        if (g.IsIncident)
            foreach (var r in g.Rows.OrderBy(r => r.Time))
                sb.AppendLine($"  {r.Time:h:mm:ss tt}  {(r.Event.Kind == CrashKind.GpuDriverReset ? "Graphics driver" : r.AppName ?? r.Event.AppExe)}: {r.KindLabel.ToLowerInvariant()}" +
                              (string.IsNullOrEmpty(r.TechnicalText) || r.Event.Kind == CrashKind.GpuDriverReset ? "" : $" ({r.TechnicalText})"));
        if (g.ContextText is { } context) sb.AppendLine($"Just before: {context}");
        if (g.ChangesText is { } changes) sb.AppendLine(changes);
        if (!g.IsIncident && !string.IsNullOrEmpty(g.TechnicalText)) sb.AppendLine($"Technical: {g.TechnicalText}");
        sb.AppendLine($"Suggested fix: {g.Advice}");
        sb.AppendLine();
        sb.Append(PcInfo.Text);
        return sb.ToString();
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private static void OpenEventViewer()
    {
        try { Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true }); } catch { }
    }
}
