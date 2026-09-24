using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core.Stability;

namespace Rigsight.Models;

/// <summary>How bad a problem is: sets its colour and where it counts.</summary>
public enum CrashSeverity
{
    /// <summary>Power lost while asleep: not a fault in the PC.</summary>
    Info,
    /// <summary>One app or game crashed or froze.</summary>
    Minor,
    /// <summary>The graphics driver reset, Windows' display crashed, or several things failed at once.</summary>
    Serious,
    /// <summary>A blue screen, or the PC shutting off while in use.</summary>
    Critical,
}

/// <summary>
/// Crashes that belong together: the same app failing the same way (one row instead of 36 identical cards),
/// or several things failing within minutes of each other (one incident: "your PC froze").
/// </summary>
public sealed partial class CrashGroup : ObservableObject
{
    private CrashGroup(List<CrashRow> rows, bool incident)
    {
        Rows = [.. rows.OrderByDescending(r => r.Time)];
        IsIncident = incident;
    }

    /// <summary>Newest first.</summary>
    public List<CrashRow> Rows { get; }
    public bool IsIncident { get; }
    public CrashRow Latest => Rows[0];
    public CrashRow First => Rows[^1];
    public int Count => Rows.Count;
    public bool IsRepeated => Count > 1;
    public DateTime LastTime => Latest.Time;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _showDetails;

    /// <summary>"Started 2 days after: NVIDIA graphics driver installed (9 Aug)", set once changes are loaded.</summary>
    [ObservableProperty] private string? _changesText;

    [ObservableProperty] private bool _isMuted;

    /// <summary>Briefly true after "Copy report", to confirm it worked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    private bool _copied;

    public string CopyText => Copied ? "Copied" : "Copy report";

    public CrashSeverity Severity => IsIncident ? CrashSeverity.Serious : SeverityOf(Latest);

    public static CrashSeverity SeverityOf(CrashRow r) => r.Event.Kind switch
    {
        CrashKind.SystemCrash => CrashSeverity.Critical,
        CrashKind.UnexpectedShutdown => r.Event.DuringSleep ? CrashSeverity.Info : CrashSeverity.Critical,
        CrashKind.GpuDriverReset => CrashSeverity.Serious,
        _ when r.Event.AppExe.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase) => CrashSeverity.Serious,
        _ => CrashSeverity.Minor,
    };

    public string KindLabel => IsIncident ? (Rows.All(r => r.Event.Kind == CrashKind.AppHang) ? "PC FROZE" : "SEVERAL AT ONCE") : Latest.KindLabel;

    public string Title => IsIncident ? IncidentTitle : Latest.Title;
    public string Reason => IsIncident ? IncidentReason : Latest.Reason;
    public string Advice => IsIncident ? IncidentAdvice : Latest.Advice;
    public string? IconPath => IsIncident ? null : Latest.IconPath;
    public string? ContextText => Latest.ContextText;
    public string ContextLabel => Count == 1 ? "Just before: " : "Last time, just before: ";
    public string TechnicalText => Latest.TechnicalText;
    public string? DumpPath => Rows.Select(r => r.DumpPath).FirstOrDefault(p => p is not null);
    public bool HasDump => DumpPath is not null;

    /// <summary>Only a specific app's crashes can be muted (not blue screens, power loss or incidents).</summary>
    public string? AppExe => IsIncident || Latest.IsSystem || string.IsNullOrEmpty(Latest.Event.AppExe) ? null : Latest.Event.AppExe;
    public bool CanMute => AppExe is not null;
    public string MuteText => IsMuted ? "Unmute" : "Mute";
    partial void OnIsMutedChanged(bool value) => OnPropertyChanged(nameof(MuteText));

    public string CountText => $"×{Count}";

    /// <summary>"Yesterday, 12:32 PM" or, for repeats, "Last: yesterday, 12:32 PM · first 25 Jun".</summary>
    public string WhenText => Count == 1 ? Latest.TimeText
        : $"Last: {Latest.TimeText} · first {First.Time:d MMM}";

    public string ExpandText => IsExpanded ? "Hide times" : $"All {Count} times";
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandText));
    public string DetailsText => ShowDetails ? "Hide details" : "Details";
    partial void OnShowDetailsChanged(bool value) => OnPropertyChanged(nameof(DetailsText));

    /// <summary>What to search the web for.</summary>
    public string SearchQuery
    {
        get
        {
            if (IsIncident)
                return Rows.Any(r => r.Event.Kind == CrashKind.GpuDriverReset)
                    ? "Display driver stopped responding and has recovered games crash"
                    : "Windows PC freezes several apps not responding at once";
            var e = Latest.Event;
            return e.Kind switch
            {
                CrashKind.SystemCrash => $"{Latest.Culprit} {e.Code} blue screen",
                CrashKind.UnexpectedShutdown => e.DuringSleep ? "PC loses power during sleep Kernel-Power 41" : "PC shuts off unexpectedly Kernel-Power 41",
                CrashKind.GpuDriverReset => "Display driver stopped responding and has recovered",
                CrashKind.AppHang => $"{e.AppExe} not responding",
                // The module only helps when it isn't the app itself.
                _ => string.Join(' ', new[] { e.AppExe, "crash", string.Equals(e.Module, e.AppExe, StringComparison.OrdinalIgnoreCase) ? null : e.Module, e.Code }
                    .Where(p => !string.IsNullOrEmpty(p))),
            };
        }
    }

    // ── Incidents ───────────────────────────────────────────────────────

    private List<string> IncidentApps => [.. Rows.OrderBy(r => r.Time).Select(r => r.Event.Kind == CrashKind.GpuDriverReset ? "the graphics driver" : r.AppName ?? r.Event.AppExe).Distinct()];

    private string IncidentTitle => Rows.All(r => r.Event.Kind == CrashKind.AppHang) ? "Your PC froze"
        : Rows.Any(r => r.Event.Kind == CrashKind.GpuDriverReset) ? "The graphics driver reset and took apps with it"
        : "Several things failed at once";

    private string IncidentReason
    {
        get
        {
            var apps = IncidentApps;
            int minutes = Math.Max(1, (int)Math.Ceiling((Latest.Time - First.Time).TotalMinutes));
            string within = $"within {minutes} minute{(minutes == 1 ? "" : "s")}, starting at {First.Time:h:mm tt}";
            if (Rows.Any(r => r.Event.Kind == CrashKind.GpuDriverReset))
            {
                var others = apps.Where(a => a != "the graphics driver").ToList();
                return $"The graphics driver reset, and {JoinAnd(others)} {(others.Count == 1 ? "was" : "were")} affected {within}.";
            }
            string list = apps.Count <= 3 ? JoinAnd(apps) : $"{string.Join(", ", apps.Take(3))} and {apps.Count - 3} more";
            string what = Rows.All(r => r.Event.Kind == CrashKind.AppHang) ? "stopped responding" : "crashed or froze";
            return $"{Count} problems within {minutes} minute{(minutes == 1 ? "" : "s")}: {list} {what}, starting at {First.Time:h:mm tt}.";
        }
    }

    private const string IncidentAdvice =
        "When several apps fail together, the cause is usually the whole PC rather than one app: the graphics driver, the drive, running out of memory, or overheating. Check the temperatures below and the drive health on the Storage page.";

    private static string JoinAnd(List<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };

    // ── Grouping ────────────────────────────────────────────────────────

    /// <summary>Failures this close together, of at least this many different things, are one incident.</summary>
    private static readonly TimeSpan IncidentGap = TimeSpan.FromMinutes(5);
    private const int IncidentMinEvents = 3, IncidentMinApps = 2;

    /// <param name="groupRepeats">False lists every crash on its own (newest first), keeping only incidents together:
    /// several failures within minutes are one moment in time, so they stay one card either way.</param>
    public static List<CrashGroup> Build(IEnumerable<CrashRow> rows, bool groupRepeats = true)
    {
        var sorted = rows.OrderBy(r => r.Time).ToList();
        var groups = new List<CrashGroup>();
        var used = new HashSet<CrashRow>();

        // Incidents: app crashes, freezes and driver resets in quick succession, across different apps.
        var appLevel = sorted.Where(r => r.Event.Kind is CrashKind.AppCrash or CrashKind.AppHang or CrashKind.GpuDriverReset).ToList();
        var cluster = new List<CrashRow>();
        void Close()
        {
            // Two apps crashing together (an app and its own helper, say) isn't a PC-wide problem; a freeze or a driver reset is.
            int apps = cluster.Select(r => r.Event.AppExe.ToLowerInvariant()).Distinct().Count();
            bool pcWide = cluster.Any(r => r.Event.Kind is CrashKind.AppHang or CrashKind.GpuDriverReset);
            if (cluster.Count >= IncidentMinEvents && (apps >= 3 || (apps >= IncidentMinApps && pcWide)))
            {
                groups.Add(new CrashGroup(cluster, incident: true));
                used.UnionWith(cluster);
            }
            cluster = [];
        }
        foreach (var r in appLevel)
        {
            if (cluster.Count > 0 && r.Time - cluster[^1].Time > IncidentGap) Close();
            cluster.Add(r);
        }
        Close();

        // Repeats: the same thing failing the same way.
        foreach (var g in sorted.Where(r => !used.Contains(r)).GroupBy(r => groupRepeats ? KeyOf(r) : r.Event.Ts + "|" + r.Event.Id + "|" + r.GetHashCode()))
            groups.Add(new CrashGroup([.. g], incident: false));

        return [.. groups.OrderByDescending(g => g.LastTime)];
    }

    private static string KeyOf(CrashRow r) => r.Event.Kind switch
    {
        CrashKind.SystemCrash => $"bsod|{r.Event.Code}",
        CrashKind.UnexpectedShutdown => $"power|{r.Event.DuringSleep}",
        CrashKind.GpuDriverReset => "gpu",
        _ => $"{r.Event.Kind}|{r.Event.AppExe.ToLowerInvariant()}|{r.Culprit}",
    };
}
