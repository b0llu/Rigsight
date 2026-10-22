using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
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
/// or several things failing within minutes of each other (one incident: "the PC froze").
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

    private CrashFinding? _finding;
    private bool _judged;

    /// <summary>
    /// What most likely happened, with how sure that is: the newest time's, or for several things failing together,
    /// one for the moment. Null only for rows made without their inputs.
    /// </summary>
    public CrashFinding? Finding
    {
        get
        {
            if (_judged) return _finding;
            _judged = true;
            return _finding = !IsIncident ? Latest.Finding
                : Rows.Count > 0 && Rows.All(r => r.Input is not null) ? CrashCases.Together([.. Rows.OrderBy(r => r.Time).Select(r => r.Input!)]) : null;
        }
    }

    public bool HasFinding => Finding is not null;
    public string? Chip => Finding?.Chip;
    /// <summary>The colour that goes with how sure the finding is: "Named", "Likely", "Possible" or "NotEnough".</summary>
    public string Sure => (Finding?.Confidence ?? Confidence.NotEnough).ToString();
    /// <summary>The row's second line: what happened, in a sentence.</summary>
    public string Line => Finding?.Line ?? Reason;

    [ObservableProperty] private bool _isMuted;

    /// <summary>Briefly true after "Copy report", to confirm it worked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    private bool _copied;

    public string CopyText => Copied ? Lang.T("Copied") : Lang.T("Copy report");

    public CrashSeverity Severity => IsIncident ? CrashSeverity.Serious : SeverityOf(Latest);

    /// <summary>
    /// A problem whose cause was most likely outside the PC (the mains, a shutdown already under way, a driver being
    /// installed under an app) is no fault of the PC's: grey, and not counted as serious.
    /// </summary>
    public static CrashSeverity SeverityOf(CrashRow r) => r.Finding is { Outside: true } ? CrashSeverity.Info : r.Event.Kind switch
    {
        CrashKind.SystemCrash => CrashSeverity.Critical,
        CrashKind.UnexpectedShutdown => r.Event.Moment == PowerMoment.Running ? CrashSeverity.Critical : CrashSeverity.Info,
        CrashKind.GpuDriverReset => CrashSeverity.Serious,
        _ when r.Event.AppExe.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase) => CrashSeverity.Serious,
        _ => CrashSeverity.Minor,
    };

    public string KindLabel => IsIncident ? (Rows.All(r => r.Event.Kind == CrashKind.AppHang) ? Lang.T("PC FROZE") : Lang.T("SEVERAL AT ONCE")) : Latest.KindLabel;

    public string Title => IsIncident ? IncidentTitle : Latest.Title;
    public string Reason => IsIncident ? IncidentReason : Latest.Reason;
    public string Advice => Finding?.Advice ?? (IsIncident ? IncidentAdvice : Latest.Advice);
    public string? IconPath => IsIncident ? null : Latest.IconPath;
    public string? ContextText => Latest.ContextText;
    public string TechnicalText => Latest.TechnicalText;
    public string? DumpPath => Rows.Select(r => r.DumpPath).FirstOrDefault(p => p is not null);
    public bool HasDump => DumpPath is not null;

    /// <summary>Only a specific app's crashes can be muted (not blue screens, power loss or incidents).</summary>
    public string? AppExe => IsIncident || Latest.IsSystem || string.IsNullOrEmpty(Latest.Event.AppExe) ? null : Latest.Event.AppExe;
    public bool CanMute => AppExe is not null;
    public string MuteText => IsMuted ? Lang.T("Unmute") : Lang.T("Mute");
    partial void OnIsMutedChanged(bool value) => OnPropertyChanged(nameof(MuteText));

    public string CountText => $"×{Count}";

    /// <summary>"Yesterday, 12:32 PM" or, for repeats, "First: 25 Jun · Last: yesterday, 12:32 PM" (a bare "first 25 Jun" at the end wasn't understood).</summary>
    public string WhenText => Count == 1 ? Latest.TimeText
        : Lang.T($"First: {First.Time:d MMM} · Last: {Latest.TimeText}");

    public string ExpandText => IsExpanded ? Lang.T("Hide times") : Lang.T($"All {Count} times");
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandText));
    public string DetailsText => ShowDetails ? Lang.T("Hide details") : Lang.T("Details");
    partial void OnShowDetailsChanged(bool value) => OnPropertyChanged(nameof(DetailsText));

    /// <summary>What to search the web for: in Windows' own words where it has some, as that is what finds the answers.</summary>
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
                CrashKind.UnexpectedShutdown => e.Moment switch
                {
                    PowerMoment.Asleep => "PC loses power during sleep Kernel-Power 41",
                    PowerMoment.ShuttingDown => "Kernel-Power 41 during shutdown fast startup",
                    _ => "PC shuts off unexpectedly Kernel-Power 41",
                },
                CrashKind.GpuDriverReset => "Display driver stopped responding and has recovered",
                CrashKind.AppHang => $"{e.AppExe} not responding",
                // The module only helps when it isn't the app itself.
                _ => string.Join(' ', new[] { e.AppExe, "crash", string.Equals(e.Module, e.AppExe, StringComparison.OrdinalIgnoreCase) ? null : e.Module, e.Code }
                    .Where(p => !string.IsNullOrEmpty(p))),
            };
        }
    }

    // ── Incidents ───────────────────────────────────────────────────────

    /// <summary>What failed, in order, each once: an app by its name, a graphics driver reset as null.</summary>
    private List<string?> IncidentApps => [.. Rows.OrderBy(r => r.Time).Select(r => r.Event.Kind == CrashKind.GpuDriverReset ? null : r.AppName ?? r.Event.AppExe).Distinct()];

    private string IncidentTitle => Rows.All(r => r.Event.Kind == CrashKind.AppHang) ? Lang.T("The PC froze")
        : Rows.Any(r => r.Event.Kind == CrashKind.GpuDriverReset) ? Lang.T("The graphics driver reset and apps failed with it")
        : Lang.T("Several things failed at once");

    private string IncidentReason
    {
        get
        {
            var apps = IncidentApps;
            int minutes = Math.Max(1, (int)Math.Ceiling((Latest.Time - First.Time).TotalMinutes));
            string start = First.Time.ToString(TimeText.Fit("h:mm tt"));
            if (Rows.Any(r => r.Event.Kind == CrashKind.GpuDriverReset))
            {
                string within = minutes == 1 ? Lang.T($"within 1 minute, starting at {start}") : Lang.T($"within {minutes} minutes, starting at {start}");
                var others = apps.OfType<string>().ToList();
                string joined = Core.Wording.JoinAnd(others);
                return others.Count == 1 ? Lang.T($"The graphics driver reset, and {joined} was affected {within}.")
                    : Lang.T($"The graphics driver reset, and {joined} were affected {within}.");
            }
            var names = apps.OfType<string>().ToList();
            string first = string.Join(", ", names.Take(3));
            int more = names.Count - 3;
            string list = names.Count <= 3 ? Core.Wording.JoinAnd(names) : Lang.T($"{first} and {more} more");
            bool froze = Rows.All(r => r.Event.Kind == CrashKind.AppHang);
            return (froze, minutes == 1) switch
            {
                (true, true) => Lang.T($"{Count} problems within 1 minute: {list} froze, starting at {start}."),
                (true, false) => Lang.T($"{Count} problems within {minutes} minutes: {list} froze, starting at {start}."),
                (false, true) => Lang.T($"{Count} problems within 1 minute: {list} crashed or froze, starting at {start}."),
                _ => Lang.T($"{Count} problems within {minutes} minutes: {list} crashed or froze, starting at {start}."),
            };
        }
    }

    private static string IncidentAdvice => Lang.T(
        "When several apps fail together, the cause is usually the whole PC rather than one app: the graphics driver, the drive, running out of memory, or overheating. Check the temperatures below and the drive health on the Storage page.");

    // ── Grouping ────────────────────────────────────────────────────────

    /// <param name="groupRepeats">False lists every crash on its own (newest first), keeping only incidents together:
    /// several failures within minutes are one moment in time, so they stay one card either way.</param>
    public static List<CrashGroup> Build(IEnumerable<CrashRow> rows, bool groupRepeats = true)
    {
        var sorted = rows.OrderBy(r => r.Time).ToList();
        var groups = new List<CrashGroup>();
        var used = new HashSet<CrashRow>();

        // Incidents: app crashes, freezes and driver resets in quick succession, across different apps (the one rule
        // for it is the core's, so a row elsewhere can't split the moment differently).
        var rowOf = new Dictionary<CrashEvent, CrashRow>(ReferenceEqualityComparer.Instance);
        foreach (var r in sorted) rowOf[r.Event] = r;
        foreach (var incident in CrashIncidents.Find(sorted.Select(r => r.Event)))
        {
            var cluster = incident.Select(e => rowOf[e]).ToList();
            groups.Add(new CrashGroup(cluster, incident: true));
            used.UnionWith(cluster);
        }

        // Repeats: the same thing failing the same way.
        foreach (var g in sorted.Where(r => !used.Contains(r)).GroupBy(r => groupRepeats ? KeyOf(r) : r.Event.Ts + "|" + r.Event.Id + "|" + r.GetHashCode()))
            groups.Add(new CrashGroup([.. g], incident: false));

        return [.. groups.OrderByDescending(g => g.LastTime)];
    }

    private static string KeyOf(CrashRow r) => r.Event.Kind switch
    {
        CrashKind.SystemCrash => $"bsod|{r.Event.Code}",
        // Shut-offs are one row per finding: a row of 48 under the newest one's "likely a power cut" would say that of
        // the three that came under load and of the ones nothing was recorded for.
        CrashKind.UnexpectedShutdown => $"power|{r.Event.Moment}|{r.Finding?.Chip}",
        CrashKind.GpuDriverReset => "gpu",
        // …and an app that went down under a driver install is not one of its own crashes.
        _ => $"{r.Event.Kind}|{r.Event.AppExe.ToLowerInvariant()}|{r.Culprit}|{r.Finding is { Outside: true }}",
    };
}
