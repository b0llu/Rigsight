using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// A card of the Memory page's "Worth a look" row: what it is about first (an app, or all the memory), its figure
/// beside that, and one sentence under them.
/// </summary>
public sealed record MemoryNote(string Caption, bool IsWarm, string Name, ImageSource? Icon, string Figure, string Text)
{
    public bool HasIcon => Icon is not null;
}

public sealed partial class MemoryViewModel(ReportService reports, LiveData live) : ObservableObject
{
    public LiveData Live { get; } = live;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TodayMemoryMax), nameof(ShowCollecting))]
    private List<AppStat> _todayTop = [];

    /// <summary>Today has been read: until then its card says it's loading, not that there is nothing yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCollecting))]
    private bool _ready;

    public bool ShowCollecting => Ready && TodayTop.Count == 0;

    public double TodayMemoryMax => Math.Max(1, TodayTop.FirstOrDefault()?.MemMax ?? 1);

    /// <summary>The "Worth a look" cards, most important first; the row is left out with none.</summary>
    public ObservableCollection<MemoryNote> Notes { get; } = [];

    [ObservableProperty] private bool _hasNotes;

    /// <summary>The most cards shown at once.</summary>
    public const int MaxNotes = 4;

    /// <summary>How full the memory has to have been today for its card.</summary>
    public const double FullPercent = 85;

    /// <summary>How many hours in a row an app has to have grown, and by how much from each to the next, to be called growing.</summary>
    public const int GrowingHours = 3;
    public const double GrowthStep = 1.03;

    public async Task RefreshAsync()
    {
        var past = reports.MemoryPastAsync();
        var today = await reports.BuildRangeAsync(DateTime.Today, DateTime.Today.AddDays(1));
        TodayTop = Kept.Or(TodayTop, today?.Apps.Where(a => a.MemMax is not null).OrderByDescending(a => a.MemMax).Take(10).ToList() ?? [], Kept.Values);
        Apply(await past, DateTime.Now);
        Ready = true;
    }

    // ── What is on record, put beside what each app holds now ──

    private MemoryPast? _past;
    private MemoryNote? _fullest;
    private DateTime _readAt;
    private bool _shown;

    /// <summary>
    /// The page came on screen or left it: the cards follow the running apps, and an app that starts gets its usual,
    /// only while it shows.
    /// </summary>
    public void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        if (shown)
        {
            Live.ProcsApplied += UpdateNotes;
            Live.Procs.CollectionChanged += OnProcsChanged;
            // Apps that started while the page was away have nothing beside them yet.
            if (_past is not null) Annotate(Live.Procs);
        }
        else
        {
            Live.ProcsApplied -= UpdateNotes;
            Live.Procs.CollectionChanged -= OnProcsChanged;
        }
    }

    private void OnProcsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The list scales its bars itself right after taking an app in.
        if (_past is null || e.NewItems is null) return;
        foreach (ProcRow row in e.NewItems) Annotate(row);
    }

    /// <summary>Takes in what was read: every app's usual and whether it keeps growing, and the cards.</summary>
    internal void Apply(MemoryPast? past, DateTime now)
    {
        _past = past ?? new MemoryPast();
        _readAt = now;
        _fullest = FullestNote(_past, Live.RamLoad?.Max, (Live.RamUsed?.Value ?? 0) + (Live.RamAvailable?.Value ?? 0));
        Annotate(Live.Procs);
    }

    private void Annotate(IEnumerable<ProcRow> rows)
    {
        foreach (var row in rows) Annotate(row);
        Live.UpdateBars();
        UpdateNotes();
    }

    private void Annotate(ProcRow row)
    {
        row.UsualMB = _past!.Usual.TryGetValue(row.Exe, out double usual) ? usual : null;
        var started = row.Started > 0 ? TimeUtil.FromUnix(row.Started) : (DateTime?)null;
        row.GrowingSince = _past.Hours.TryGetValue(row.Exe, out var hours) && GrowingSince(hours, row.MemMB, row.UsualMB, started, _readAt) is { } since
            ? since.ToString("h:mm tt", CultureInfo.CurrentCulture)
            : null;
    }

    /// <summary>
    /// Since when an app has kept growing: its memory was higher in each of today's last recorded hours than in the
    /// hour before (<see cref="GrowingHours"/> of them at least, up to now), and it is above its usual now. An app
    /// started today has to have grown in every hour since, and the time is when it started; for one running from
    /// before today it is the first hour of the rise. Null when it isn't growing.
    /// </summary>
    /// <param name="hours">Its memory in each hour of today (MB), from midnight; null where not recorded.</param>
    internal static DateTime? GrowingSince(IReadOnlyList<double?> hours, double nowMB, double? usualMB, DateTime? started, DateTime now)
    {
        if (usualMB is not { } usual || nowMB <= usual) return null;
        int last = Math.Min(hours.Count - 1, now.Hour);
        while (last >= 0 && hours[last] is null) last--;
        if (last < now.Hour - 1) return null; // nothing recorded lately: what it did earlier says nothing about now
        int first = last;
        while (first > 0 && hours[first - 1] is { } before && hours[first] >= before * GrowthStep) first--;
        if (last - first + 1 < GrowingHours) return null;
        if (started is not { } start || start.Date != now.Date) return now.Date.AddHours(first);
        int from = Math.Min(start.Hour, last);
        while (from < last && hours[from] is null) from++;
        return first <= from ? start : null;
    }

    /// <summary>
    /// The card for the fullest the memory has been today, when that was <see cref="FullPercent"/> or more. The figure
    /// is the one the gauge gives as today's highest (every reading); without it, the fullest minute against all the
    /// memory there is.
    /// </summary>
    internal static MemoryNote? FullestNote(MemoryPast past, double? highestPercent, double totalGB)
    {
        if (past.Fullest is not { } fullest) return null;
        double? percent = highestPercent ?? (totalGB > 0 ? fullest.UsedGB / totalGB * 100 : null);
        if (percent is not { } p || Math.Round(p) < FullPercent) return null;
        string apps = past.FullestApps.Count switch
        {
            0 => "",
            1 => $", with {past.FullestApps[0]} holding the most",
            _ => $", with {past.FullestApps[0]} and {past.FullestApps[1]} holding the most",
        };
        return new MemoryNote("FULLEST TODAY", false, "All memory", null, $"{p:0}%", $"At {fullest.At.ToString("h:mm tt", CultureInfo.CurrentCulture)}{apps}.");
    }

    /// <summary>
    /// The cards as the running apps are now: each ordinary app well above its usual, the one furthest over first,
    /// then the fullest the memory has been today. A card that says the same stays as it is.
    /// </summary>
    private void UpdateNotes()
    {
        if (_past is null) return;
        var fresh = new List<MemoryNote>();
        foreach (var row in Live.Procs.Where(p => p.IsOver).OrderByDescending(p => p.MemMB - (p.UsualMB ?? 0)))
        {
            string growing = row.GrowingSince is { } since ? $", and has grown every hour since {since}" : "";
            fresh.Add(new MemoryNote("ABOVE ITS USUAL", true, row.Name, row.Icon, row.MemText, $"It usually holds {row.UsualText}{growing}."));
        }
        if (_fullest is not null) fresh.Add(_fullest);
        if (fresh.Count > MaxNotes) fresh.RemoveRange(MaxNotes, fresh.Count - MaxNotes);
        Kept.Sync(Notes, fresh);
        HasNotes = Notes.Count > 0;
    }
}
