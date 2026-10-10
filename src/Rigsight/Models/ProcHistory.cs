using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;

namespace Rigsight.Models;

/// <summary>A fact about an app: what it is and its figure ("Usual memory", "520 MB").</summary>
public sealed record ProcFact(string Label, string Value);

/// <summary>
/// The box between an app's row and its processes on the Processes page: a slim line of what is on record about it,
/// which opens into today's memory as a chart beside every fact there is. A fact with nothing recorded is left out.
/// </summary>
public sealed partial class ProcHistory : ObservableObject
{
    /// <summary>Opened into the chart and the facts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isOpen;

    /// <summary>The history is being read: until then the line says only what the running app itself tells.</summary>
    [ObservableProperty] private bool _isLoading = true;

    /// <summary>The slim line's items.</summary>
    [ObservableProperty] private IReadOnlyList<ProcFact> _glance = [];

    /// <summary>Every fact, for the opened box.</summary>
    [ObservableProperty] private IReadOnlyList<ProcFact> _facts = [];

    /// <summary>Its memory in each hour of today (MB), the hour now as it is at this moment; null where not recorded.</summary>
    [ObservableProperty] private IReadOnlyList<double?> _hours = [];

    /// <summary>Its usual memory (MB), the dashed line across the chart; null with too little recorded.</summary>
    [ObservableProperty] private double? _usualMB;

    /// <summary>There are two hours or more to draw a line through.</summary>
    [ObservableProperty] private bool _hasChart;

    public string ToggleText => IsOpen ? "Hide" : "History";

    /// <summary>When it was read (the box reads again when opened a while later).</summary>
    public DateTime ReadAt { get; private set; }

    /// <summary>What the running app says by itself, before (or without) anything read from the history.</summary>
    public void Fill(ProcRow row, DateTime now) => Fill(row, null, now, loading: IsLoading);

    public void Fill(ProcRow row, AppPast? past, DateTime now, bool loading = false)
    {
        var facts = new List<ProcFact>();
        var glance = new List<ProcFact>();
        void Add(string label, string value, bool atAGlance = false)
        {
            facts.Add(new(label, value));
            if (atAGlance) glance.Add(new(label, value));
        }

        if (row.Started > 0) Add("Running since", Since(TimeUtil.FromUnix(row.Started), now), atAGlance: true);
        if (past?.UsualMB is { } usual) Add("Usual memory", Units.Megabytes(usual), atAGlance: true);
        if (past?.FrontSec is { } front) Add("In front today", Time(front));
        if (past?.BackSec is { } back) Add("In the background today", Time(back), atAGlance: true);
        if (past?.NetBytes is { } net) Add("Internet today", Units.Data(net));
        if (past is not null) Add("Crashes (30 days)", past.Crashes == 0 ? "None" : past.Crashes.ToString(CultureInfo.CurrentCulture));
        if (past?.FirstSeen is { } first) Add("First seen", first.ToString("d MMM yyyy", CultureInfo.CurrentCulture));

        // The hour now is what the app holds at this moment, as the list above says it.
        var hours = past?.Hours.ToList() ?? [];
        if (hours.Count > 0 && hours.Any(h => h is not null)) hours[^1] = row.MemMB;

        Facts = Kept.Or(Facts, facts);
        Glance = Kept.Or(Glance, glance);
        Hours = Kept.Or(Hours, hours);
        UsualMB = past?.UsualMB;
        HasChart = hours.Count(h => h is not null) >= 2;
        IsLoading = loading;
        if (past is not null) ReadAt = now;
    }

    /// <summary>"9:10 AM" today, "Yesterday, 7:40 PM", or with its date.</summary>
    internal static string Since(DateTime started, DateTime now)
    {
        var culture = CultureInfo.CurrentCulture;
        int days = (int)(now.Date - started.Date).TotalDays;
        return days <= 0 ? started.ToString("h:mm tt", culture)
            : days == 1 ? $"Yesterday, {started.ToString("h:mm tt", culture)}"
            : started.ToString("d MMM, h:mm tt", culture);
    }

    /// <summary>"5 h 40 min", "22 min", "Under a minute"; "None" for no time at all.</summary>
    internal static string Time(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 1) return "None";
        int minutes = (int)(seconds / 60);
        return minutes < 1 ? "Under a minute" : minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60} min";
    }
}
