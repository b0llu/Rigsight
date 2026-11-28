using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Models;

/// <summary>The square that stands for a driver's maker: its colour (a brand's own, or a palette brush), its letter and the letter's colour.</summary>
public sealed record MakerSquare(string Back, string Letter, string Ink)
{
    public static MakerSquare Of(string maker)
    {
        var (back, letter, ink) = DriverMakers.Of(maker);
        return new(back, letter, ink);
    }
}

/// <summary>A tile of one driver's view: a small caps label, the figure (with the version before it and an arrow, when <see cref="Was"/> is set), a line under it.</summary>
public sealed record DriverTile(string Label, string Value, string? Was, string Sub)
{
    public bool HasWas => Was is { Length: > 0 };
    public bool HasSub => Sub.Length > 0;
}

/// <summary>A version in one driver's "Every version": its dot, the version, when, how long, and its chip.</summary>
public sealed record DriverVersionLine(string Version, string Range, string? Days, string? Chip, string ChipBrush, bool IsCurrent, string DotBrush)
{
    /// <summary>The thread between the dots runs up from all but the first and down from all but the last.</summary>
    public bool IsFirst { get; init; }
    public bool IsLast { get; init; }
    public bool HasDays => Days is { Length: > 0 };
    public bool HasChip => Chip is { Length: > 0 };
}

/// <summary>
/// One driver, opened inside Timeline (approved mock-up v8, "only that driver"): who made it and what kind it is, four
/// figures (the version now, its last change, the versions on record, its date), every version newest first, a Before and
/// after card only where the change made something better or worse, and the way back a version through Device Manager.
/// </summary>
public sealed class DriverPage
{
    public DriverDevice Device { get; }
    public string Name => Device.Name;
    public string KindLine { get; }
    public MakerSquare Maker { get; }
    public List<DriverTile> Tiles { get; }
    public List<DriverVersionLine> Versions { get; }

    /// <summary>What this driver's last change made better or worse, where it did (see <see cref="ChangeEffects"/>).</summary>
    public ChangeResult? Result { get; }
    public bool HasResult => Result is not null;

    public bool CanRollBack => !Device.Gone;
    /// <summary>A graphics card's driver: its maker's own installer is the other way back.</summary>
    public string? MakerSite { get; }
    public bool HasMakerSite => MakerSite is not null;

    /// <summary>Opened from the every-driver list: Back goes there.</summary>
    public bool FromList { get; }
    public string BackText => FromList ? Lang.T("Back to every driver") : Lang.T("Back to Timeline");

    public DriverPage(DriverDevice d, DateTime now, ChangeResult? result, bool fromList)
    {
        Device = d;
        FromList = fromList;
        Result = result;
        Maker = MakerSquare.Of(d.Maker);
        string kind = DriverHistory.KindName(d.Kind);
        KindLine = d.Gone ? $"{kind} · {d.Maker} · {Lang.T($"removed {DriverHistory.Day(d.Seen, now)}")}" : $"{kind} · {d.Maker}";
        MakerSite = d.IsGraphicsCard && !d.Gone && d.Maker.Length > 0 ? Lang.T($"{d.Maker}'s own drivers can also be reinstalled from {d.Maker}'s site.") : null;

        var cur = d.Current;
        bool known = cur.How != DriverStart.Unknown;
        int on = (now.Date - cur.Since.Date).Days;
        string nowSub = d.Gone ? Lang.T($"On {DriverHistory.Day(d.Seen, now)}")
            : !known ? Lang.T("No change on record")
            : on <= 0 ? Lang.T("On this PC since today") : on == 1 ? Lang.T("On this PC for 1 day") : Lang.T($"On this PC for {on} days");
        var last = d.Versions.Count > 1 && known ? new DriverTile(Lang.T("LAST CHANGE"), cur.Shown, d.Versions[1].Shown, DriverHistory.Day(cur.Since, now))
            : d.FirstSeen ? new DriverTile(Lang.T("LAST CHANGE"), Lang.T("First seen"), null, DriverHistory.Day(cur.Since, now))
            : new DriverTile(Lang.T("LAST CHANGE"), "–", null, "");
        Tiles =
        [
            new(Lang.T("VERSION NOW"), d.Gone ? Lang.T("Removed") : cur.Shown, null, nowSub),
            last,
            new(Lang.T("VERSIONS ON RECORD"), d.Versions.Count.ToString("N0"), null, Record(d, now)),
            new(Lang.T("DRIVER DATE"), cur.Date is { } date ? date.ToString("d MMM yyyy") : "–", null, Lang.T("Set by its maker, not when it was installed")),
        ];

        string brush = DriverHistory.KindLook(d.Kind).Brush;
        Versions = [.. d.Versions.Select((v, i) =>
        {
            bool current = i == 0 && !d.Gone;
            DateTime? until = v.Until ?? (d.Gone ? d.Seen : null);
            string range = v.How == DriverStart.Unknown
                ? (until is { } u ? Lang.T($"Until {DriverHistory.Day(u, now)}") : Lang.T($"On record since {DriverHistory.Day(v.Since, now)}"))
                : until is { } end ? Lang.T($"{DriverHistory.Day(v.Since, now)} to {DriverHistory.Day(end, now)}") : Lang.T($"{DriverHistory.Day(v.Since, now)} to now");
            string? days = v.How == DriverStart.Unknown ? null : until is { } to ? Days((to.Date - v.Since.Date).Days) : SoFar((now.Date - v.Since.Date).Days);
            string? chip = v.Older ? Lang.T("DOWNGRADED") : current ? Lang.T("NOW") : null;
            return new DriverVersionLine(v.Shown, range, days, chip, v.Older ? "MutedBrush" : "CpuBrush", current, current ? brush : "FaintBrush")
            {
                IsFirst = i == 0, IsLast = i == d.Versions.Count - 1,
            };
        })];
    }

    /// <summary>Where its record starts: "First seen 29 Aug" for a device that came after the first look, else "On record since 3 Feb".</summary>
    internal static string Record(DriverDevice d, DateTime now) =>
        d.FirstSeen ? Lang.T($"First seen {DriverHistory.Day(d.RecordStart, now)}") : Lang.T($"On record since {DriverHistory.Day(d.RecordStart, now)}");

    private static string Days(int n) => n <= 0 ? Lang.T("Under a day") : n == 1 ? Lang.T("1 day") : Lang.T($"{n} days");

    private static string SoFar(int n) => n <= 0 ? Lang.T("Since today") : n == 1 ? Lang.T("1 day so far") : Lang.T($"{n} days so far");
}

/// <summary>A row of the every-driver list: the maker's square, the device and its maker, the version, and what changed last.</summary>
public sealed record DriverListRow(string Key, string Name, string Maker, string Version, MakerSquare Square, string? Was, string? Now, string Change, bool Quiet)
{
    /// <summary>A last change from one version to another: shown as "621.01 → 621.24 · since 13 Nov".</summary>
    public bool HasStep => Was is { Length: > 0 };
    public string Spoken => HasStep ? $"{Name}, {Version}, {Was} → {Now} · {Change}" : $"{Name}, {Version}, {Change}";
}

/// <summary>A group of the every-driver list: a kind of device (or the ones no longer on this PC), its coloured icon and its rows.</summary>
public sealed record DriverListGroup(string Name, string Glyph, string Brush, List<DriverListRow> Rows)
{
    public string Count => Rows.Count.ToString("N0");
}

/// <summary>A driver that comes with Windows, in the list's scrolling box at the end (not opened: they change with Windows updates).</summary>
public sealed record InboxRow(string Name, string Version, string Change);

/// <summary>
/// Every driver on this PC, opened inside Timeline from This PC now's "Drivers" (approved mock-up v8): the maker's drivers
/// grouped by kind, then the ones no longer here, and Windows' own drivers last in a box that scrolls. A filter for the
/// ones changed in the last 90 days, and a search. A row opens that driver.
/// </summary>
public sealed partial class EveryDriverPage : ObservableObject
{
    private readonly DriverHistory _history;

    public EveryDriverPage(DriverHistory history)
    {
        _history = history;
        int makers = history.FromMakers.Count(), windows = history.FromWindows.Count();
        string mine = makers == 1 ? Lang.T("1 from a device maker") : Lang.T($"{makers} from device makers");
        string theirs = windows == 1 ? Lang.T("1 that comes with Windows") : Lang.T($"{windows} that come with Windows");
        Counts = $"{mine} · {theirs}";
        InboxCount = windows.ToString("N0");
        int recent = history.FromWindows.Count(history.ChangedRecently);
        InboxNote = recent == 0 ? Lang.T("These change with Windows updates. None changed in the last 90 days.")
            : recent == 1 ? Lang.T("These change with Windows updates. 1 changed in the last 90 days.")
            : Lang.T($"These change with Windows updates. {recent} changed in the last 90 days.");
        Build();
    }

    public string Counts { get; }
    public string InboxCount { get; }
    public string InboxNote { get; }

    /// <summary>"Changed in the last 90 days" picked (else All, the default).</summary>
    [ObservableProperty] private bool _recentOnly;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private List<DriverListGroup> _groups = [];
    [ObservableProperty] private List<InboxRow> _inbox = [];
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyText = "";

    public bool HasInbox => Inbox.Count > 0;

    partial void OnRecentOnlyChanged(bool value) => Build();
    partial void OnSearchChanged(string value) => Build();
    partial void OnInboxChanged(List<InboxRow> value) => OnPropertyChanged(nameof(HasInbox));

    private void Build()
    {
        var now = _history.Now;
        var words = TextMatch.Words(Search);
        bool Keep(DriverDevice d) => (!RecentOnly || _history.ChangedRecently(d)) && (words.Length == 0 || TextMatch.Has(words, d.Name, d.Maker, d.Current.Shown));
        Groups = [.. _history.Groups(Keep).Select(g =>
        {
            var (glyph, brush) = DriverHistory.KindLook(g.Kind);
            return new DriverListGroup(DriverHistory.KindName(g.Kind).ToUpper(System.Globalization.CultureInfo.CurrentCulture), glyph, brush,
                [.. g.Devices.Select(d => RowOf(d, now))]);
        })];
        Inbox = [.. _history.FromWindows.Where(Keep)
            .OrderByDescending(d => _history.ChangedRecently(d)).ThenByDescending(d => d.Current.How == DriverStart.Unknown ? DateTime.MinValue : d.Current.Since).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new InboxRow(d.Name, d.Current.Shown, d.Current.How == DriverStart.Unknown ? "" : Lang.T($"since {DriverHistory.Day(d.Current.Since, now)}")))];
        IsEmpty = Groups.Count == 0 && Inbox.Count == 0;
        EmptyText = words.Length > 0 ? Lang.T("Nothing matches") : Lang.T("No driver changed in the last 90 days");
    }

    private static DriverListRow RowOf(DriverDevice d, DateTime now)
    {
        var cur = d.Current;
        var square = MakerSquare.Of(d.Maker);
        if (d.Gone) return new(d.Key, d.Name, d.Maker, cur.Shown, square, null, null, Lang.T($"Removed {DriverHistory.Day(d.Seen, now)}"), false);
        if (cur.How == DriverStart.Unknown) return new(d.Key, d.Name, d.Maker, cur.Shown, square, null, null, Lang.T("No change on record"), true);
        string since = Lang.T($"since {DriverHistory.Day(cur.Since, now)}");
        return d.Versions.Count > 1 ? new(d.Key, d.Name, d.Maker, cur.Shown, square, d.Versions[1].Shown, cur.Shown, since, false)
            : new(d.Key, d.Name, d.Maker, cur.Shown, square, null, null, since, false);
    }
}
