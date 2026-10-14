using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Stability;

/// <summary>
/// Reads what each crash's finding is worked out from (the minutes before, earlier times, changes, logged trouble) and
/// works it out. The one place a finding is made: the Crashes page, Home, Reports, the Timeline, notifications and
/// exports all come here, so none of them can say something the others don't.
/// </summary>
public static class CrashCases
{
    /// <summary>Days of rest temperatures a PC's "usual at rest" is taken from.</summary>
    private const int RestDays = 14;

    /// <summary>The inputs for the crashes given (any order), by crash id.</summary>
    public static Dictionary<long, CrashInput> Inputs(RigsightDb db, IReadOnlyList<CrashEvent> crashes, RigsightSettings settings)
    {
        var map = new Dictionary<long, CrashInput>();
        if (crashes.Count == 0) return map;
        long from = crashes.Min(c => c.Ts), to = crashes.Max(c => c.Ts) + 1;

        var apps = db.LoadApps();
        var byExe = new Dictionary<string, AppRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps) byExe[a.Exe] = a;
        var byId = apps.ToDictionary(a => a.Id);
        string NameOf(string exe) => settings.AppNames.TryGetValue(exe, out var alias) ? alias
            : AppCatalog.KnownName(exe) ?? (byExe.TryGetValue(exe, out var row) ? row.Name : AppCatalog.FallbackName(exe));

        var context = db.GetCrashContext(from, to);
        // Every crash up to the newest one, to find each one's earlier times.
        var history = db.GetCrashes(0, to).GroupBy(CrashFindings.KeyOf).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Ts).ToList());
        var changes = db.GetChanges(from - 7 * 86400, to + 120); // a driver's logged time is its last part's: up to two minutes after
        long? firstChange = db.HasChanges ? db.FirstChangeTime() : null;
        var signs = db.GetSigns(from - 1800, to + 86400);
        var rest = db.GetHeatDays(from - (RestDays + 1) * 86400L, to).Where(d => d.App == 0).ToList();
        double? ram = db.GetInventoryValue(Inventory.Ram) is { } installed && double.TryParse(installed.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture, out var gb) ? gb : null;

        foreach (var e in crashes)
        {
            context.TryGetValue(e.Id, out var ctx);
            byExe.TryGetValue(e.AppExe, out var app);
            string? name = string.IsNullOrEmpty(e.AppExe) ? null : NameOf(e.AppExe);
            string? front = ctx?.FrontApp is long id && byId.TryGetValue(id, out var frontRow) ? NameOf(frontRow.Exe) : null;
            var days = rest.Where(d => d.Day < e.Ts && d.Day >= e.Ts - (RestDays + 1) * 86400L).ToList();
            int minutes = days.Sum(d => d.N);
            var before = e.Time;
            map[e.Id] = new CrashInput(e, CrashExplainer.Explain(e, name), settings.Alerts)
            {
                AppName = name,
                IsGame = app is not null && (settings.AppCategories.TryGetValue(app.Exe, out var category) ? category : app.Category) == AppCategory.Game,
                Context = ctx,
                FrontApp = front is not null && !string.Equals(front, name, StringComparison.OrdinalIgnoreCase) ? front : e.Kind is CrashKind.AppCrash or CrashKind.AppHang ? null : front,
                Rest = minutes == 0 ? null : new LoadTemps(Ratio(days.Sum(d => d.CpuSum), days.Sum(d => d.CpuN)), Ratio(days.Sum(d => d.GpuSum), days.Sum(d => d.GpuN)), minutes),
                RamTotal = ram,
                Earlier = history.TryGetValue(CrashFindings.KeyOf(e), out var same) ? [.. same.Where(p => p.Ts < e.Ts || (p.Ts == e.Ts && p.Id < e.Id))] : [],
                // No record of changes from a week before it: nothing is said about changes either way.
                Changes = firstChange is long first && first <= e.Ts - 7 * 86400 ? [.. changes.Where(c => c.Time <= before.AddMinutes(2) && c.Time >= before.AddDays(-7))] : null,
                Signs = [.. signs.Where(s => s.Ts >= e.Ts - 1800 && s.Ts <= e.Ts + 86400)],
                NameOf = NameOf,
            };
        }
        return map;

        static double? Ratio(double sum, int n) => n > 0 ? sum / n : null;
    }

    /// <summary>Each crash's finding, by crash id.</summary>
    public static Dictionary<long, CrashFinding> Of(RigsightDb db, IReadOnlyList<CrashEvent> crashes, RigsightSettings settings) =>
        Findings(Inputs(db, crashes, settings));

    /// <summary>
    /// The findings for inputs already read. An app that went down within two minutes of a graphics driver's logged
    /// install gets that as its finding (the driver's time is its last part's, so it can read as just after).
    /// </summary>
    public static Dictionary<long, CrashFinding> Findings(Dictionary<long, CrashInput> inputs)
    {
        var found = inputs.ToDictionary(p => p.Key, p => Swap(p.Value) is { } driver ? CrashFindings.DriverSwap(p.Value, driver) : CrashFindings.Judge(p.Value));
        // Several failing together are one moment with one finding, on every page: each of them says the moment's.
        foreach (var incident in CrashIncidents.Find(inputs.Values.Select(x => x.Crash)))
        {
            var together = Together([.. incident.Select(e => inputs[e.Id])]);
            foreach (var e in incident) found[e.Id] = together;
        }
        return found;
    }

    /// <summary>The finding for several things that failed together (oldest first): a graphics driver being installed under any of them explains them all.</summary>
    public static CrashFinding Together(IReadOnlyList<CrashInput> members) =>
        CrashFindings.Together(members, members.Select(Swap).FirstOrDefault(d => d is not null));

    private static SystemChange? Swap(CrashInput x) => x.Crash.Kind is not (CrashKind.AppCrash or CrashKind.AppHang) ? null
        : x.Changes?.FirstOrDefault(c => c.IsGraphicsDriver && !c.IsApproximate && Math.Abs((c.Time - x.Crash.Time).TotalMinutes) <= 2);
}
