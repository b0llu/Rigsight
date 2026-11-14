using Rigsight.Agent.Native;
using Rigsight.Agent.Ui;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Notices;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Agent;

// The notifications round (2026-11-07, mock-up LWycKU9ZC8jzeVH8bVjPWm): every notification goes one way (Notify): its
// group, the daily limit, Do not disturb, kept in the recent list, the app told; and the checks that turn what RigSight
// already works out for its pages into notifications (Core's NoticeChecks).
internal sealed partial class AgentContext
{
    // The first checks 5 minutes after the agent starts, not in its first minute, when sensors, scans and the window
    // opening are already busy (the 1.7.0 gate, 2026-11-08: a check in the first minute read 200 ms of agent CPU inside the
    // "agent alone" measure); every 10 minutes after.
    private DateTime _noticesChecked = DateTime.Now - NoticeChecksEvery + TimeSpan.FromMinutes(5), _changesChecked;

    /// <summary>How often the checks run while the PC is in use; the changes' results once an hour.</summary>
    private static readonly TimeSpan NoticeChecksEvery = TimeSpan.FromMinutes(10), ChangeChecksEvery = TimeSpan.FromHours(1);

    /// <summary>
    /// Any thread: decides whether a notification shows as a card or only goes in the recent list (worth knowing and recaps
    /// past today's limit, or while Windows says not to disturb), keeps it, marks what it says as said, shows it, and tells
    /// an open app to read its list again. Problems and RigSight's own are never held back.
    /// </summary>
    private void Notify(Notice n) => RunOnSampler(() =>
    {
        var s = _settings.Alerts;
        var now = DateTime.Now;
        bool shown = true;
        string? why = null;
        if (n.Level is NoticeLevel.Worth or NoticeLevel.Recap)
        {
            int today = SafeDb(() => _db.CountShownNotices(TimeUtil.ToUnix(now.Date), NoticeLevel.Worth, NoticeLevel.Recap));
            if (s.CardsPerDay > 0 && today >= s.CardsPerDay) (shown, why) = (false, $"{today} cards shown today, the limit is {s.CardsPerDay}");
            else if (QuietByWindows() is { } state) (shown, why) = (false, $"Windows says {state}");
        }
        if (n.Kind != NoticeKind.Overlay)
            SafeDb(() =>
            {
                _db.SaveNotice(new NoticeRecord(now, n.Kind.ToString(), n.Level, n.Title, n.Body, n.Page, n.Arg, n.IconPath, n.Key, shown));
                if (n.Key is { } key) _db.MarkSaid(key, now, n.Value);
                foreach (var also in n.AlsoSays) _db.MarkSaid(also, now);
                return 0;
            });
        Log.Write("notice", $"{n.Kind} ({n.Level}) \"{n.Title}\": {(shown ? "shown" : "kept in the list only, " + why)}{(n.Key is null ? "" : $" [{n.Key}]")}");
        if (shown) _ui.Post(_ => _notices.Show(n), null);
        if (_pipe.ClientCount > 0) _pipe.Broadcast(new AgentMessage { T = "notice" });
    });

    private int SafeDb(Func<int> work)
    {
        try { return work(); }
        catch (Exception ex) { Log.Error("notice", ex); return 0; }
    }

    /// <summary>
    /// What Windows tells apps about interrupting now, when it says to hold back: a presentation, or its quiet time.
    /// (Windows 11's Do not disturb isn't reported to apps this way; a fullscreen game is handled as before.)
    /// </summary>
    private static string? QuietByWindows()
    {
        if (Win32.SHQueryUserNotificationState(out int state) != 0) return null;
        return state switch
        {
            Win32.QUNS_PRESENTATION_MODE => "a presentation is on",
            Win32.QUNS_QUIET_TIME => "it's in its quiet time",
            _ => null,
        };
    }

    /// <summary>A check's candidate, sent as a notice.</summary>
    private void Notify(NoticeCandidate c, string? iconPath = null) =>
        Notify(new Notice(NoticeLevels.KindOf(c), c.Text.Title, c.Text.Body, iconPath, c.Page, c.Arg, Urgent: c.Level == NoticeLevel.Problem) { Key = c.Key, Value = c.Value });

    private (DateTime At, string? Value)? SaidOf(string key)
    {
        try { return _db.Said(key); }
        catch (Exception ex) { Log.Error("notice", ex); return (DateTime.Now, null); } // unreadable: say nothing rather than repeat
    }

    /// <summary>
    /// Sampler thread, each minute (the checks every 10 minutes while the PC is in use): fans, drives, space, the PC's
    /// problems, memory and new startup apps; the changes' results once an hour. The first run on a PC only takes note of
    /// the changes and startup apps already there, so an update doesn't open with news from last week.
    /// </summary>
    private void MaybeCheckNotices()
    {
        var now = DateTime.Now;
        if (now - _noticesChecked < NoticeChecksEvery || !_presentNow) return;
        _noticesChecked = now;
        var settings = _settings;
        var a = settings.Alerts;
        bool first = SaidOf("notices-began") is null;
        bool heavy = false; // read a day's history or the changes' results: memory to give back after
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            // Today's report's lines: fans and the PC's problems against their usual.
            if (a.FanStops || a.FansSlower || a.MoreProblems)
            {
                heavy = true;
                var today = ReportBuilder.Build(_db, ReportRange.Day, now.Date, settings);
                var found = NoticeChecks.FromReport(today, a, SaidOf, now);
                Log.Write("notice", $"Checked today's report: {today.Insights.Count(i => i.Notice is not null)} lines could notify, {found.Count} to say");
                foreach (var c in found) Notify(c);
            }

            // Drives: their own health check, and space.
            var life = DriveLife();
            var drives = NoticeChecks.Drives(_sensors.DriveHealth, life, a, SaidOf, now, DrivePace);
            Log.Write("notice", $"Checked {_sensors.DriveHealth.Count} drives' health ({string.Join(", ", _sensors.DriveHealth.Select(d => $"{d.Name} {d.Status}, {(d.Temperature is double t ? $"{t:0}" : "no")} C against {(d.TemperatureWarning is double w ? $"{w:0}" : "no")} warning, life {(d.Life is double l ? $"{l:0}%" : "not reported")}"))}): {drives.Count(c => !c.Quiet)} to say, {drives.Count(c => c.Quiet)} noted");
            foreach (var c in drives)
                if (c.Quiet) _db.MarkSaid(c.Key, now, c.Value);
                else Notify(c);

            // A CPU or GPU that runs hotter under the same load than in its first weeks: worked out once a day (the weeks
            // change by the week, and it reads a year of minutes).
            if (a.PartsChanged && _wearCheckedDay != now.Date)
            {
                heavy = true;
                _wearCheckedDay = now.Date;
                var cooling = new[] { Wear.Cooling(_db, false, now.Date), Wear.Cooling(_db, true, now.Date) };
                var hotter = NoticeChecks.Cooling(cooling, a, SaidOf, now);
                Log.Write("notice", $"Checked the cooling over the months: {string.Join(", ", cooling.Select(r => $"{r.Name} {r.State} {(r.Now is double n ? $"{n:0.0}" : "-")} against {(r.Then is double t ? $"{t:0.0}" : "-")}, {r.History.Count} weeks"))}; {hotter.Count} to say");
                foreach (var c in hotter) Notify(c);
            }
            var space = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => (Letter: d.Name[0], Free: d.AvailableFreeSpace, Total: d.TotalSize)).ToList();
            char windows = (Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } w ? w[0] : 'C');
            var full = NoticeChecks.Space(space, windows, a, SaidOf, now);
            Log.Write("notice", $"Checked space: {string.Join(", ", space.Select(d => $"{d.Letter} {Units.Bytes(d.Free)} free of {Units.Bytes(d.Total)}"))}; {full.Count} to say");
            foreach (var c in full) Notify(c);

            // Memory that keeps growing.
            if (a.MemoryGrowth)
            {
                var apps = _db.LoadApps().ToDictionary(x => x.Id);
                var rows = _db.GetAppMemory(TimeUtil.ToUnix(now - NoticeChecks.GrowthSpan), NoticeChecks.GrowthMinBytes);
                string NameOf(AppRow x) => settings.AppNames.TryGetValue(x.Exe, out var alias) ? alias : Core.Apps.AppCatalog.KnownName(x.Exe) ?? x.Name;
                var byApp = rows.Where(r => apps.ContainsKey(r.Key)).ToDictionary(r => (apps[r.Key].Exe, NameOf(apps[r.Key])), r => r.Value);
                var grown = NoticeChecks.Memory(byApp, a, SaidOf, now);
                Log.Write("notice", $"Checked memory: {byApp.Count} apps reached {Units.Bytes(NoticeChecks.GrowthMinBytes)} in the last 3 hours, {grown.Count} kept growing");
                foreach (var c in grown) Notify(c);
            }

            // New startup apps, and the changes' results (once an hour).
            var changes = _db.GetChanges(TimeUtil.ToUnix(now.AddDays(-2)), TimeUtil.ToUnix(now.AddMinutes(1)));
            var startup = NoticeChecks.Startup(changes, first || a.NewStartupApps, SaidOf, now);
            if (now - _changesChecked >= ChangeChecksEvery || first)
            {
                _changesChecked = now;
                heavy = true;
                var results = ChangeEffects.Read(_db, settings);
                var worse = NoticeChecks.Changes(results, first || a.WorseChanges, SaidOf, now);
                Log.Write("notice", $"Checked {results.Count} change results: {worse.Count} made a game worse and weren't said");
                if (first) foreach (var c in worse) _db.MarkSaid(c.Key, now);
                else foreach (var c in worse) Notify(c);
            }
            if (first)
            {
                foreach (var c in startup) _db.MarkSaid(c.Key, now);
                _db.MarkSaid("notices-began", now);
                Log.Write("notice", $"First check on this PC: {startup.Count} startup apps taken as they are");
            }
            else foreach (var c in startup) Notify(c);
        }
        catch (Exception ex)
        {
            Log.Error("notice", ex);
        }
        Measure("notice checks", t0);
        // A day's report and the changes' results are read into memory for a moment: given back, as after a rewind is made
        // (the 1.7.0 gate, 2026-11-08: the agent read 92 MB with the window open, against 67.6 before the checks).
        if (heavy)
        {
            MemoryAt("notice checks");
            CompactMemory();
        }
    }

    /// <summary>The day the cooling over the months was last checked.</summary>
    private DateTime _wearCheckedDay;

    /// <summary>A drive's share of rated life used a year at the pace its daily record shows (null until 4 weeks of it).</summary>
    private double? DrivePace(string key)
    {
        try { return Wear.Drive(_db, key, key, DateTime.Today).LifePerYear; }
        catch (Exception ex) { Log.Error("notice", ex); return null; }
    }

    /// <summary>Each drive's rated life left (an SSD's "Life" or "Remaining Life" reading), by its name.</summary>
    private Dictionary<string, double> DriveLife()
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var hw in _sensors.Schema)
            foreach (var s in hw.Sensors.Where(s => s.Kind == SensorKind.Level && s.Name is "Life" or "Remaining Life"))
                if (_sensors.ReadSensor(s.Id)?.Value is double v) map[hw.Name] = v;
        return map;
    }

    /// <summary>
    /// The temperature alert's last sentence: the game's usual for that part over the 30 days before today, when there are
    /// enough days of steady play to say it. Only for one part, the CPU or the GPU.
    /// </summary>
    private string? UsualForAlert(string? exe, IReadOnlyList<string> parts)
    {
        if (exe is null || parts.Count != 1 || parts[0] is not ("CPU" or "GPU")) return null;
        try
        {
            if (_db.LoadApps().FirstOrDefault(x => x.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)) is not { } app) return null;
            var heat = _db.GetHeatDays(TimeUtil.ToUnix(DateTime.Today.AddDays(-30)), TimeUtil.ToUnix(DateTime.Today));
            var range = PcUsual.UsualIn(heat, app.Id, parts[0] == "GPU");
            Log.Write("notice", $"Running hot in {exe}: usual {parts[0]} {(range is { } r ? $"{r.Low:0.0}-{r.High:0.0}" : "not known")}");
            return range is { } u ? NoticeWords.UsualInGame(u.Low, u.High) : null;
        }
        catch (Exception ex) { Log.Error("notice", ex); return null; }
    }

    /// <summary>
    /// Sampler thread, when a game ends: what's worth knowing about it (hotter than usual at the same power, the GPU slowing
    /// itself). Returned as sentences for its summary, and as cards for when there's no summary.
    /// </summary>
    private (List<string> Sentences, List<NoticeCandidate> Cards) AfterGame(SessionRow row, string name, string exe)
    {
        var a = _settings.Alerts;
        if (!a.HotterThanUsual && !a.GpuSlowing) return ([], []);
        try
        {
            var now = DateTime.Now;
            var settings = _settings;
            var usual = a.HotterThanUsual ? PcUsual.Read(_db, settings, now.Date.AddDays(-6), now, PcUsual.UsualDaysFor(ReportRange.Day), now, game: row.AppId) : null;
            var throttle = a.GpuSlowing ? ReportBuilder.Build(_db, ReportRange.Day, now.Date, settings).GpuThrottle : null;
            var found = NoticeChecks.AfterGame(usual, throttle, name, exe, a, SaidOf, now);
            Log.Write("notice", $"{name} ended: usual compared for {(usual?.Parts.Count > 0 ? string.Join(", ", usual.Parts.Select(p => $"{p.Part} {p.Now:0.0} vs {p.Usual:0.0} ({p.Tone})")) : "nothing (too little steady play on a side)")}"
                + $"; GPU slowing itself {(throttle is { } t ? $"{t.Minutes} min, {t.DropPercent:0}%" : "no")}; {found.Cards.Count} to say");
            return found;
        }
        catch (Exception ex)
        {
            Log.Error("notice", ex);
            return ([], []);
        }
    }

    /// <summary>
    /// Sampler thread, with the daily recap: on the first use of a Monday, the last 7 days (with the 7 before to compare),
    /// when the weekly recap is on.
    /// </summary>
    private void MaybeShowWeeklyRecap(DateTime today)
    {
        if (!_settings.Alerts.WeeklyRecap || today.DayOfWeek != DayOfWeek.Monday) return;
        string key = $"weekly|{today:yyyy-MM-dd}";
        if (SaidOf(key) is not null) return;
        try
        {
            var report = ReportBuilder.Build(_db, ReportRange.Week, today.AddDays(-1), _settings);
            var before = ReportBuilder.Build(_db, ReportRange.Week, today.AddDays(-8), _settings);
            _db.MarkSaid(key, DateTime.Now);
            if (!report.HasData || report.ActiveSec < 3600) return;
            var top = report.Apps.FirstOrDefault(x => x.ActiveSec > 0 && x.Category != AppCategory.System);
            var (title, body) = NoticeWords.Weekly(report.ActiveSec, top?.Name, top?.ActiveSec ?? 0, before.HasData ? before.ActiveSec : null);
            Notify(new Notice(NoticeKind.Weekly, title, body, Page: "reports", Arg: "last-week") { Key = key });
        }
        catch (Exception ex) { Log.Error("notice", ex); }
    }
}
