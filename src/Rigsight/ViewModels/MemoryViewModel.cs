using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// The fullest the memory was today, the last line of "Today's biggest memory users" (mock-up 2026-11-14): one
/// sentence, Windows nearly out of memory then as a second (amber) one, and its hover box, what each app held.
/// </summary>
public sealed record FullestMoment(string Text, string? CommitText, string TipTitle, IReadOnlyList<HeldRow> Held)
{
    /// <summary>What to try under it (1.21.0); left out of comparing two readings.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public TryBlockView? Try { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasTry => Try is not null;
    public bool HasCommit => CommitText is not null;
    public bool HasHeld => Held.Count > 0;
}

/// <summary>A row of the fullest moment's hover box: an app and what it held, or everything else.</summary>
public sealed record HeldRow(string Name, string Figure, bool IsRest);

/// <summary>
/// A row of "Today's biggest memory users": an app, or the fullest moment as the last line. Each has its rank in the
/// card's <see cref="Controls.FitStack"/>, which shows as many apps as the card's height has room for.
/// </summary>
public sealed record TodayRow(AppStat? App, FullestMoment? Fullest, int Rank, bool Essential)
{
    public bool IsApp => App is not null;
    public bool IsFullest => Fullest is not null;
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

    /// <summary>
    /// The most of today's apps "Today's biggest memory users" has to show: the card is as tall as the RAM card beside
    /// it and shows as many of them as fit (more where the RAM card stands taller, on a narrow window).
    /// </summary>
    public const int MostToday = 20;

    /// <summary>The apps that always show in it, whatever its height; the others fill what is left.</summary>
    public const int AlwaysToday = 3;

    /// <summary>How full the memory has to have been today for its line.</summary>
    public const double FullPercent = 85;

    /// <summary>Today's fullest moment, from <see cref="FullPercent"/>; null otherwise.</summary>
    [ObservableProperty] private FullestMoment? _fullest;

    /// <summary>The card's rows: today's apps, biggest first, then the fullest moment.</summary>
    [ObservableProperty] private List<TodayRow> _todayRows = [];

    partial void OnTodayTopChanged(List<AppStat> value) => TodayRows = RowsOf(value, Fullest);
    partial void OnFullestChanged(FullestMoment? value) => TodayRows = RowsOf(TodayTop, value);

    /// <summary>The fullest moment comes right under the apps shown and always shows; the first apps always do too.</summary>
    internal static List<TodayRow> RowsOf(IReadOnlyList<AppStat> apps, FullestMoment? fullest)
    {
        var rows = apps.Select((a, i) => new TodayRow(a, null, i + 1, i < AlwaysToday)).ToList();
        if (fullest is not null) rows.Add(new TodayRow(null, fullest, 0, true));
        return rows;
    }

    /// <summary>
    /// No app has a usual yet (the first days): one line at the top of the live list says so, and no row is marked.
    /// Null once there are usuals.
    /// </summary>
    [ObservableProperty] private string? _tooSoonText;

    /// <summary>How many hours in a row an app has to have grown, and by how much from each to the next, to be called growing.</summary>
    public const int GrowingHours = 3;
    public const double GrowthStep = 1.03;

    public async Task RefreshAsync()
    {
        var past = reports.MemoryPastAsync();
        var today = await reports.BuildRangeAsync(DateTime.Today, DateTime.Today.AddDays(1));
        TodayTop = Kept.Or(TodayTop, today?.Apps.Where(a => a.MemMax is not null).OrderByDescending(a => a.MemMax).Take(MostToday).ToList() ?? [], Kept.Values);
        Apply(await past, DateTime.Now);
        Ready = true;
    }

    // ── What is on record, put beside what each app holds now ──

    private MemoryPast? _past;
    private DateTime _readAt;
    private bool _shown;

    /// <summary>
    /// The page came on screen or left it: an app that starts gets its usual and its hours of today, only while it shows.
    /// </summary>
    public void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        if (shown)
        {
            Live.Procs.CollectionChanged += OnProcsChanged;
            // Apps that started while the page was away have nothing beside them yet.
            if (_past is not null) Annotate(Live.Procs);
        }
        else Live.Procs.CollectionChanged -= OnProcsChanged;
    }

    private void OnProcsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The list scales its bars itself right after taking an app in.
        if (_past is null || e.NewItems is null) return;
        foreach (ProcRow row in e.NewItems) Annotate(row);
    }

    /// <summary>
    /// Takes in what was read: every app's usual, its hours and peak of today and whether it keeps growing, today's
    /// fullest moment, and whether it is too soon for usuals.
    /// </summary>
    internal void Apply(MemoryPast? past, DateTime now)
    {
        _past = past ?? new MemoryPast();
        _readAt = now;
        var fullest = FullestOf(_past, Live.RamLoad?.Max, (Live.RamUsed?.Value ?? 0) + (Live.RamAvailable?.Value ?? 0));
        if (!Kept.Values(Fullest, fullest)) Fullest = fullest is null ? null : fullest with { Try = TryOf(_past, fullest) };
        TooSoonText = _past.TooSoon ? TooSoonOf(_past.RecordedDays) : null;
        Annotate(Live.Procs);
    }

    /// <summary>
    /// What to try under today's fullest moment: the app that held the most, closed after a question where it still has a
    /// window open now, then the apps that start with Windows.
    /// </summary>
    private TryBlockView TryOf(MemoryPast past, FullestMoment fullest)
    {
        var top = past.FullestApps.FirstOrDefault();
        var row = top is null ? null : Live.Procs.FirstOrDefault(p => p.HasWindow && string.Equals(p.Name, top, StringComparison.OrdinalIgnoreCase));
        return new TryBlockView(Core.Advice.WhatToTry.MemoryFull(row?.Name, row?.Exe, row is null ? null : Units.Megabytes(row.MemMB), fullest.Text));
    }

    private void Annotate(IEnumerable<ProcRow> rows)
    {
        foreach (var row in rows) Annotate(row);
        Live.UpdateBars();
    }

    private void Annotate(ProcRow row)
    {
        row.UsualSpread = _past!.Spread.TryGetValue(row.Exe, out var spread) ? spread : null;
        row.UsualMB = _past.Usual.TryGetValue(row.Exe, out double usual) ? usual : null;
        var started = row.Started > 0 ? TimeUtil.FromUnix(row.Started) : (DateTime?)null;
        bool hasHours = _past.Hours.TryGetValue(row.Exe, out var hours);
        row.GrowingSince = hasHours && GrowingSince(hours!, row.MemMB, row.UsualMB, started, _readAt) is { } since
            ? since.ToString(TimeText.Fit("h:mm tt"), CultureInfo.CurrentCulture)
            : null;
        row.Today = new MemoryToday(hours ?? [], _past.Peak.TryGetValue(row.Exe, out double peak) ? peak : null, _readAt);
    }

    /// <summary>
    /// "Too soon to say which apps are above their usual. It needs 3 days of records, and has 1 so far.": the days are
    /// those the app with the most of them has on record before today, as an app's usual needs them.
    /// </summary>
    internal static string TooSoonOf(int days) => days switch
    {
        <= 0 => Lang.T($"Too soon to say which apps are above their usual. It needs {AppPast.MinUsualDays} days of records."),
        1 => Lang.T($"Too soon to say which apps are above their usual. It needs {AppPast.MinUsualDays} days of records, and has 1 so far."),
        _ => Lang.T($"Too soon to say which apps are above their usual. It needs {AppPast.MinUsualDays} days of records, and has {days} so far."),
    };

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
    /// Today's fullest moment, when the memory was <see cref="FullPercent"/> full or more: "Memory was 95% full at
    /// 2:20 PM, with CONTROL Resonant and Google Chrome holding the most.", and "Windows was nearly out of memory then, …"
    /// when it had promised <see cref="MemoryPast.NearlyOutPercent"/> of its memory and page file or more that minute.
    /// The figure is the one the gauge gives as today's highest (every reading); without it, the fullest minute against
    /// all the memory there is. Its hover box: what each app held that minute (or over its hour, where the minute kept
    /// too few of them), and everything else.
    /// </summary>
    internal static FullestMoment? FullestOf(MemoryPast past, double? highestPercent, double totalGB)
    {
        if (past.Fullest is not { } fullest) return null;
        double? percent = highestPercent ?? (totalGB > 0 ? fullest.UsedGB / totalGB * 100 : null);
        if (percent is not { } p || Math.Round(p) < FullPercent) return null;
        string at = fullest.At.ToString(TimeText.Fit("h:mm tt"), CultureInfo.CurrentCulture);
        int pct = (int)Math.Round(Math.Min(p, 100));
        var apps = past.FullestApps;
        string text = apps.Count switch
        {
            0 => Lang.T($"Memory was {pct}% full at {at}."),
            1 => Lang.T($"Memory was {pct}% full at {at}, with {apps[0]} holding the most."),
            _ => Lang.T($"Memory was {pct}% full at {at}, with {apps[0]} and {apps[1]} holding the most."),
        };
        string? commit = past.FullestCommitPct is double c && c >= MemoryPast.NearlyOutPercent
            ? Lang.T($"Windows was nearly out of memory then, with {c:0}% of its memory and page file already promised to apps.")
            : null;

        var held = past.FullestHeld.Select(a => new HeldRow(a.Name, Units.Megabytes(a.MB), false)).ToList();
        string title;
        if (past.FullestByHour)
        {
            // Averages over the hour say so; with them, what the rest held that minute isn't known.
            var hour = fullest.At.Date.AddHours(fullest.At.Hour);
            string from = hour.ToString(TimeText.Fit("h:mm tt"), CultureInfo.CurrentCulture), to = hour.AddHours(1).ToString(TimeText.Fit("h:mm tt"), CultureInfo.CurrentCulture);
            title = Lang.T($"Average between {from} and {to}");
        }
        else
        {
            title = Lang.C("time", $"At {at}");
            double rest = fullest.UsedGB * 1024 - past.FullestHeld.Sum(a => a.MB);
            if (held.Count > 0 && rest >= 1) held.Add(new HeldRow(Lang.T("Everything else"), Units.Megabytes(rest), true));
        }
        return new FullestMoment(text, commit, title, held);
    }
}
