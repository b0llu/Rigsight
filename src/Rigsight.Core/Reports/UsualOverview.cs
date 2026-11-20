using Rigsight.Core.Data;
using Rigsight.Core.Net;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

/// <summary>What a line of Your usual is: a row (a name and its usual, or grey with what it has so far), the line saying
/// what the grey rows under it each need, or a plain sentence.</summary>
public enum UsualLine { Row, Need, Note }

/// <summary>
/// One line of Your usual. A <see cref="UsualLine.Row"/>: <see cref="Text"/> is the name and <see cref="Value"/> the
/// usual ("GPU 68° to 70° at 280 W"), or, <see cref="Dim"/>, what it has so far ("1h 07m so far") under the
/// <see cref="UsualLine.Need"/> line that says what each one needs. A <see cref="UsualLine.Note"/> is a sentence.
/// <see cref="Opens"/>: the page that shows more ("" for none); <see cref="Game"/> a game's exe.
/// </summary>
public sealed record UsualItem(UsualLine Line, string Text, string? Value = null, string Opens = "", string? Game = null)
{
    /// <summary>Not there yet, or can't be read: said in grey.</summary>
    public bool Dim { get; init; }
    /// <summary>What a line stands for, on hover (the games a count names, what "PC problems" counts).</summary>
    public string? Tip { get; init; }
    /// <summary>Not read without admin rights: the line offers to restart the agent with them.</summary>
    public bool NeedsAdmin { get; init; }
}

public enum UsualSectionKind { Temperatures, Fans, Problems, Months, Apps }

/// <summary>A part of Your usual (TEMPERATURES, FANS, …) and its lines.</summary>
public sealed record UsualSection(UsualSectionKind Kind, string Title, IReadOnlyList<UsualItem> Items);

/// <summary>
/// Your usual (issue #12's "how much has RigSight learned"): how much history this PC has, every usual it's compared
/// with, and for those not there yet what each needs and has so far. Every line asks the same check the pages compare
/// with (<see cref="PcUsual.GameReady"/>, <see cref="Wear"/>'s states, <see cref="MemoryPast.TooSoon"/>, the ping's
/// usual). No score and no overall percent: the usuals don't share a unit. Drawn light (the user, 2026-11-20): what
/// each group needs is said once, over short grey rows, not in a sentence on every row.
/// </summary>
public sealed class UsualOverview
{
    /// <summary>"Since 23 September".</summary>
    public string Since { get; init; } = "";
    /// <summary>"59 days recorded · 582h active · 206h of games".</summary>
    public string Facts { get; init; } = "";
    public IReadOnlyList<UsualSection> Sections { get; init; } = [];

    /// <summary>Heavy play under this share of a game's play time: it rarely keeps the GPU busy, and more play won't make a usual.</summary>
    internal const double RarelyBusyShare = 0.1;

    /// <summary>Steady minutes from which a game not ready gets its own row (less: it joins the "played for less than" line).</summary>
    internal const int OwnRowMinutes = 10;

    public static UsualOverview Read(RigsightDb db, RigsightSettings s, DateTime now, bool admin)
    {
        var today = now.Date;
        var names = db.LoadApps().ToDictionary(a => a.Id, a => (Exe: a.Exe, Name: s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : Rigsight.Core.Apps.AppCatalog.KnownName(a.Exe) ?? a.Name));
        string NameOf(long id) => names.TryGetValue(id, out var n) ? n.Name : Lang.T("a game");
        int window = PcUsual.UsualDaysFor(ReportRange.Day);
        long from = TimeUtil.ToUnix(today.AddDays(-window)), to = TimeUtil.ToUnix(now) + 60;

        var heat = db.GetHeatDays(from, to);
        var use = db.GetSystemDays(0, to) ?? [];
        var usual = PcUsual.Read(db, s, today, now, window, now, NameOf);

        var sections = new List<UsualSection>
        {
            new(UsualSectionKind.Temperatures, Lang.T("TEMPERATURES"), Temperatures(heat, db.GetGameTotals(from), names, admin)),
            new(UsualSectionKind.Fans, Lang.T("FANS"), Fans(db, from, to, now, admin)),
            new(UsualSectionKind.Problems, Lang.T("PC PROBLEMS"), Problems(usual, use, today)),
            new(UsualSectionKind.Months, Lang.T("OVER THE MONTHS"), Months(db, today, admin)),
        };
        var apps = AppsAndNetwork(db, now, names);
        if (apps.Count > 0) sections.Add(new(UsualSectionKind.Apps, Lang.T("APPS AND NETWORK"), apps));
        var (since, facts) = SummaryOf(db.FirstDataTime(), use, db.GetGameTotals(), now);
        return new UsualOverview { Since = since, Facts = facts, Sections = sections };
    }

    // ── The two lines under the title ──

    internal static (string Since, string Facts) SummaryOf(long? first, IReadOnlyList<SystemDay> use, IReadOnlyList<AppTotal> games, DateTime now)
    {
        if (first is not long f) return (Lang.T("Not recorded"), "");
        var at = TimeUtil.FromUnix(f);
        int days = (now.Date - at.Date).Days + 1;
        string since = days >= 2 ? Lang.T($"Since {at:d MMMM}")
            : at.Date == now.Date ? Lang.T($"Since {at:h:mm tt} today") : Lang.T($"Since {at:h:mm tt} yesterday");
        string recorded = days >= 2 ? Lang.T($"{days} days") : Units.Duration((now - at).TotalSeconds);
        string active = Hours(use.Sum(d => d.ActiveSec));
        double played = games.Sum(g => g.ActiveSec);
        string facts = played < 60 ? Lang.T($"{recorded} recorded · {active} active · no games played")
            : Lang.T($"{recorded} recorded · {active} active · {Hours(played)} of games");
        return (since, facts);
    }

    /// <summary>"580h" from 10 hours, "2h 40m" under it.</summary>
    internal static string Hours(double seconds) => seconds >= 36_000 ? Lang.T($"{(int)(seconds / 3600)}h") : Units.Duration(seconds);

    // ── Pieces every part uses ──

    /// <summary>A grey row of something not there yet, with what it has so far.</summary>
    private static UsualItem Soon(string name, string has) => new(UsualLine.Row, name, has) { Dim = true };

    /// <summary>"1h 07m so far", "none so far".</summary>
    internal static string SoFar(double seconds) => seconds < 60 ? Lang.T("none so far") : Lang.T($"{Units.Duration(seconds)} so far");

    /// <summary>"1 day so far", "6 days so far", "none so far".</summary>
    internal static string DaysSoFar(int days) => days <= 0 ? Lang.T("none so far") : days == 1 ? Lang.T("1 day so far") : Lang.T($"{days} days so far");

    /// <summary>"1 week so far", "8 weeks so far", "none so far".</summary>
    internal static string WeeksSoFar(int weeks) => weeks <= 0 ? Lang.T("none so far") : weeks == 1 ? Lang.T("1 week so far") : Lang.T($"{weeks} weeks so far");

    /// <summary>The need line over <paramref name="rows"/> (<paramref name="one"/> over a single row), then the rows.</summary>
    private static void AddSoon(List<UsualItem> items, string one, string many, IReadOnlyList<UsualItem> rows)
    {
        if (rows.Count == 0) return;
        items.Add(new(UsualLine.Need, rows.Count == 1 ? one : many));
        items.AddRange(rows);
    }

    // ── Temperatures: the CPU without admin rights, at rest, then each game ──

    internal static List<UsualItem> Temperatures(IReadOnlyList<HeatDay> heat, IReadOnlyList<AppTotal> played, IReadOnlyDictionary<long, (string Exe, string Name)> names, bool admin)
    {
        var items = new List<UsualItem>();
        string NameOf(long id) => names.TryGetValue(id, out var n) ? n.Name : Lang.T("a game");
        var (loads, rest) = PcUsual.SteadyOf(heat, NameOf);

        // Without admin rights the CPU isn't read at all: said once, with the way to get it.
        if (!admin) items.Add(new(UsualLine.Row, Lang.T("CPU"), Lang.T("Not read without admin rights")) { Dim = true, NeedsAdmin = true });

        int restMinutes = rest?.Minutes ?? 0;
        bool restReady = false;
        if (rest is { } r && PcUsual.RestReady(restMinutes))
        {
            var cpu = PcUsual.DayRange(heat, 0, gpu: false) ?? Pair(r.Cpu);
            var gpu = PcUsual.DayRange(heat, 0, gpu: true) ?? Pair(r.Gpu);
            string? value = (cpu, gpu) switch
            {
                ({ } c, { } g) => Lang.T($"CPU {Span(c)}, GPU {Span(g)}"),
                ({ } c, null) => Lang.T($"CPU {Span(c)}"),
                (null, { } g) => Lang.T($"GPU {Span(g)}"),
                _ => null,
            };
            if (value is not null) { items.Add(new(UsualLine.Row, Lang.T("At rest"), value, Opens: "temperatures")); restReady = true; }
        }

        // Every game played in the usual's weeks, and any with steady play in them.
        var steady = loads.ToDictionary(l => l.AppId);
        var playSec = played.ToDictionary(p => p.AppId, p => p.ActiveSec);
        var ids = playSec.Keys.Concat(steady.Keys).Distinct().ToList();
        var ready = new List<(int Minutes, UsualItem Item)>();
        var soon = new List<(int Minutes, UsualItem Item)>();
        var rarely = new List<(double Sec, string Name)>();
        var little = new List<(double Sec, string Name, int Minutes)>();
        foreach (long id in ids)
        {
            string name = NameOf(id), exe = names.TryGetValue(id, out var n) ? n.Exe : "";
            double sec = playSec.GetValueOrDefault(id);
            var load = steady.GetValueOrDefault(id);
            int minutes = load?.Minutes ?? 0, days = load?.Days ?? 0;
            if (load is not null && PcUsual.GameReady(minutes, days))
            {
                var range = PcUsual.DayRange(heat, id, gpu: true) ?? (load.Gpu, load.Gpu);
                string value = load.GpuPower is double w && w > 0 ? Lang.T($"GPU {Span(range)} at {w:0} W") : Lang.T($"GPU {Span(range)}");
                ready.Add((minutes, new(UsualLine.Row, name, value, Opens: "games", Game: exe)));
            }
            else if (minutes >= OwnRowMinutes) soon.Add((minutes, Soon(name, GameHas(minutes, days))));
            else if (sec >= PcUsual.MinUsualMinutes * 60.0 && minutes < sec / 60 * RarelyBusyShare) rarely.Add((sec, name));
            else if (sec > 0) little.Add((sec, name, minutes));
        }
        items.AddRange(ready.OrderByDescending(x => x.Minutes).Select(x => x.Item));

        if (!restReady)
            AddSoon(items, Lang.T($"Too soon to say for this one. It needs {PcUsual.MinRestMinutes} minutes at rest."), "",
                [Soon(Lang.T("At rest"), SoFar(restMinutes * 60.0))]);

        // One or two little-played games join the grey rows; more are one sentence, named on hover.
        var soonRows = soon.OrderByDescending(x => x.Minutes).Select(x => x.Item).ToList();
        if (little.Count <= 2) soonRows.AddRange(little.OrderByDescending(x => x.Sec).Select(x => Soon(x.Name, GameHas(x.Minutes, 0))));
        int hours = PcUsual.MinUsualMinutes / 60, needDays = PcUsual.MinUsualDays;
        AddSoon(items, Lang.T($"Too soon to say for this one. It needs {hours} hours of heavy play over {needDays} days."),
            Lang.T($"Too soon to say for these. Each needs {hours} hours of heavy play over {needDays} days."), soonRows);

        if (rarely.Count > 0)
        {
            var list = rarely.OrderByDescending(x => x.Sec).Select(x => x.Name).ToList();
            items.Add(new(UsualLine.Note, list.Count == 1
                ? Lang.T($"{list[0]} isn't compared, since it rarely keeps the GPU busy.")
                : Lang.T($"{Wording.JoinAnd(list)} aren't compared, since they rarely keep the GPU busy.")));
        }
        if (little.Count > 2)
            items.Add(new(UsualLine.Note, ready.Count + soon.Count + rarely.Count > 0
                ? Lang.T($"{little.Count} other games were played for less than {hours} hours.")
                : Lang.T($"{little.Count} games were played for less than {hours} hours."))
                { Tip = Wording.JoinAnd(little.OrderByDescending(x => x.Sec).Select(x => x.Name)) });
        if (ids.Count == 0) items.Add(new(UsualLine.Note, Lang.T("No games played.")));
        return items;
    }

    /// <summary>What a game not ready has: its heavy play, and the days it came on once the play is enough.</summary>
    internal static string GameHas(int minutes, int days) =>
        minutes < PcUsual.MinUsualMinutes || days >= PcUsual.MinUsualDays ? SoFar(minutes * 60.0)
            : days == 1 ? Lang.T($"{Units.Duration(minutes * 60.0)} so far, on 1 day")
            : Lang.T($"{Units.Duration(minutes * 60.0)} so far, on {days} days");

    // ── Fans: each board fan's speed since it was last set ──

    internal static List<UsualItem> Fans(RigsightDb db, long from, long to, DateTime now, bool admin)
    {
        var fans = db.GetFans().Where(f => !f.Sensor.StartsWith("/gpu", StringComparison.Ordinal)).ToList();
        var days = fans.Count > 0 ? db.GetFanDays(from, to) : [];
        var changes = fans.Count > 0 ? db.GetChanges(0, to).Where(c => c.Kind == ChangeKind.Fans).ToList() : [];
        return FansOf(fans, days, changes, now, admin);
    }

    internal static List<UsualItem> FansOf(IReadOnlyList<FanRow> fans, IReadOnlyList<FanDay> fanDays, IReadOnlyList<SystemChange> changes, DateTime now, bool admin)
    {
        var items = new List<UsualItem>();
        bool Spun(FanDay d) => d.RpmMax >= FanList.MinSpinRpm && d.Rpm is > 0;
        // Only the fans that turn; two chips can both have a "Fan #1" turning, told apart by their chip.
        var turning = fans.Where(f => fanDays.Any(d => d.Fan == f.Id && Spun(d))).ToList();
        var twice = turning.GroupBy(f => f.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var soon = new List<(DateTime? Set, UsualItem Row)>();
        foreach (var fan in turning)
        {
            var spun = fanDays.Where(d => d.Fan == fan.Id && Spun(d)).ToList();
            var set = changes.Where(c => c.Time <= now && c.Subject.Contains($"|{fan.Id}|", StringComparison.Ordinal)).MaxBy(c => c.Time);
            if (set is not null) spun = [.. spun.Where(d => d.Day >= TimeUtil.ToUnix(set.Time.Date.AddDays(1)))];
            string name = twice.Contains(fan.Name) ? $"{fan.Name} · {fan.Hardware}" : fan.Name;
            if (PcUsual.FanReady(spun.Count))
                items.Add(new(UsualLine.Row, name, Rpm(spun.Min(d => d.Rpm!.Value), spun.Max(d => d.Rpm!.Value)), Opens: "fans"));
            else soon.Add((set?.Time.Date, Soon(name, DaysSoFar(spun.Count))));
        }
        int need = PcUsual.FanUsualDays;
        foreach (var g in soon.GroupBy(x => x.Set).OrderBy(g => g.Key))
            if (g.Key is DateTime on)
                AddSoon(items, Lang.T($"Too soon to say since its setting changed on {on:d MMMM}. It needs {need} days of turning."),
                    Lang.T($"Too soon to say since their setting changed on {on:d MMMM}. Each needs {need} days of turning."), [.. g.Select(x => x.Row)]);
            else
                AddSoon(items, Lang.T($"Too soon to say for this one. It needs {need} days of turning."),
                    Lang.T($"Too soon to say for these. Each needs {need} days of turning."), [.. g.Select(x => x.Row)]);
        if (items.Count == 0)
            items.Add(admin ? new(UsualLine.Note, Lang.T("No fan readings on this PC."))
                : new(UsualLine.Note, Lang.T("Fans aren't read without admin rights.")) { NeedsAdmin = true });
        return items;
    }

    // ── The PC's problems, against its hours of use ──

    internal static List<UsualItem> Problems(PcUsual usual, IReadOnlyList<SystemDay> use, DateTime today)
    {
        string counts = Lang.T("Blue screens, graphics driver resets and sudden shut-offs while in use, not power cuts");
        double hours = usual.UsualHours, weeks = usual.UsualDays / 7;
        var items = new List<UsualItem>();
        if (PcUsual.ProblemsReady(hours, weeks))
        {
            if (usual.Problems is { } p)
            {
                items.Add(new(UsualLine.Row, Lang.T("PC problems"), p.UsualCount == 0 ? Lang.T($"None in {Weeks(p.UsualWeeks)}") : Wording.Capital(PcUsual.About(p.UsualCount, p.UsualWeeks, 1)), Opens: "crashes")
                    { Tip = counts });
                if (p.PowerUsual > 0)
                    items.Add(new(UsualLine.Row, Lang.T("Sudden shut-offs that look like the power"), Wording.Capital(PcUsual.About(p.PowerUsual, p.UsualWeeks, 1)), Opens: "crashes"));
            }
            else
                // Ready, but nothing compared today (the PC wasn't used yet): what it's from.
                items.Add(new(UsualLine.Row, Lang.T("PC problems"), Lang.T($"From {Weeks(weeks)} and {Hours(hours * 3600)} of use"), Opens: "crashes") { Tip = counts });
            return items;
        }
        // Today counts tomorrow: said with what's on record now.
        double todayHours = use.Where(d => d.Day >= TimeUtil.ToUnix(today)).Sum(d => d.ActiveSec) / 3600;
        double has = hours + todayHours;
        int days = (int)Math.Round(usual.UsualDays) + (todayHours > 0 ? 1 : 0);
        AddSoon(items, Lang.T($"Too soon to say for this one. It needs a week with {PcUsual.MinProblemHours:0} hours of use."), "",
            [Soon(Lang.T("PC problems"), has < PcUsual.MinProblemHours ? SoFar(has * 3600) : DaysSoFar(days)) with { Tip = counts }]);
        return items;
    }

    // ── Over the months: cooling, fans and drives against their first weeks ──

    internal static List<UsualItem> Months(RigsightDb db, DateTime today, bool admin)
    {
        var spunSensors = FanList.SpunIn(db, DateTime.Now);
        var fanIds = db.GetFans().Where(f => spunSensors.Contains(f.Sensor)).Select(f => $"fan:{f.Id}").ToHashSet();
        var fans = Wear.Fans(db, today).Where(r => fanIds.Contains(r.Part)).ToList();
        var drives = db.GetDriveHealthDays(TimeUtil.ToUnix(Wear.Origin(today))).GroupBy(d => d.Drive)
            .OrderBy(g => g.Last().Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => Wear.DriveOf(g.Key, g.Last().Name, Wear.Origin(today), [.. g.OrderBy(d => d.Day)])).ToList();
        return MonthsOf([Wear.Cooling(db, false, today), Wear.Cooling(db, true, today)], fans, drives, admin);
    }

    internal static List<UsualItem> MonthsOf(IReadOnlyList<WearRow> cooling, IReadOnlyList<WearRow> fans, IReadOnlyList<WearRow> drives, bool admin)
    {
        var items = new List<UsualItem>();
        var soon = new List<UsualItem>();
        void Wears(string name, IReadOnlyList<WearRow> rows, string opens)
        {
            if (rows.Count == 0) return;
            // Ready once any part is (the CPU or the GPU, any fan), with the most weeks any has.
            int weeks = rows.Select(r => r.History.Count).DefaultIfEmpty(0).Max();
            if (rows.Any(r => r.State is WearState.Moved or WearState.Steady)) items.Add(new(UsualLine.Row, name, WeeksSoFar(weeks), Opens: opens));
            else soon.Add(Soon(name, WeeksSoFar(weeks)));
        }
        Wears(Lang.T("Cooling"), cooling, "temperatures");
        Wears(Lang.T("Fans"), fans, "fans");

        var driveSoon = new List<UsualItem>();
        foreach (var w in drives)
        {
            if (w.State == WearState.TooSoon)
            {
                int days = w.ThenDate is DateTime then ? Math.Max(1, (int)Math.Round((DateTime.Today - then.Date).TotalDays)) : 0;
                driveSoon.Add(Soon(w.Name, DaysSoFar(days)));
                continue;
            }
            string value = w.LifePerYear is double perYear ? perYear < 0.5 ? Lang.T("Under 1% a year") : Lang.T($"{perYear:0}% a year")
                : w.SectorsNow is long now ? now == (w.SectorsThen ?? now) ? Lang.T("No new bad sectors") : Lang.T($"{now - (w.SectorsThen ?? 0)} new bad sectors")
                : Wording.NoReading;
            items.Add(new(UsualLine.Row, w.Name, value, Opens: "storage"));
        }
        AddSoon(items, Lang.T($"Too soon to say for this one. It needs {Wear.MinWeeks} weeks of games or other heavy use."),
            Lang.T($"Too soon to say for these. Each needs {Wear.MinWeeks} weeks of games or other heavy use."), soon);
        AddSoon(items, Lang.T($"Too soon to say how fast it wears. It needs {Wear.DriveMinDays} days of readings."),
            Lang.T($"Too soon to say how fast these wear. Each needs {Wear.DriveMinDays} days of readings."), driveSoon);
        if (drives.Count == 0 && !admin) items.Add(new(UsualLine.Note, Lang.T("Drives show only how full they are, without admin rights.")));
        return items;
    }

    // ── Apps and network ──

    internal static List<UsualItem> AppsAndNetwork(RigsightDb db, DateTime now, IReadOnlyDictionary<long, (string Exe, string Name)> names)
    {
        var ready = new List<UsualItem>();
        var memorySoon = new List<UsualItem>();
        var memory = MemoryPast.Read(db, now);
        if (memory.RecordedDays > 0 || memory.Usual.Count > 0)
        {
            if (memory.TooSoon) memorySoon.Add(Soon(Lang.T("Memory per app"), DaysSoFar(memory.RecordedDays)));
            else ready.Add(new(UsualLine.Row, Lang.T("Memory per app"), Lang.T($"From the last {Math.Min(AppPast.UsualDays, Math.Max(memory.RecordedDays, AppPast.MinUsualDays))} days"), Opens: "memory"));
        }

        // Each app's server checked in the last 7 days, once per network (the usual is kept that way).
        long nowUnix = TimeUtil.ToUnix(now);
        var byExe = names.Values.GroupBy(n => n.Exe, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        var pingSoon = new List<UsualItem>();
        var silent = new List<string>();
        foreach (var g in db.GetNetServers(nowUnix - 7 * 86400, nowUnix + 60).GroupBy(m => (m.Exe, Net: RigsightDb.NetServerPrefix(m.Server))))
        {
            var last = g.Last();
            string app = byExe.GetValueOrDefault(last.Exe) ?? Path.GetFileNameWithoutExtension(last.Exe);
            string name = last.Kind == "call" ? Lang.T($"Ping to {app}'s call server") : Lang.T($"Ping to {app}'s server");
            if (db.NetServerUsual(last.Exe, last.Server, nowUnix + 60) is { } u) ready.Add(new(UsualLine.Row, name, Lang.T($"{u.Low} to {u.High} ms"), Opens: "network"));
            else if (g.All(m => m.Ms is null)) silent.Add(app);
            else pingSoon.Add(Soon(name, SoFar(g.Count(m => m.Ms is not null && m.Sent >= 6) * 60.0)));
        }

        var items = new List<UsualItem>(ready);
        AddSoon(items, Lang.T($"Too soon to say for this one. It needs {AppPast.MinUsualDays} days of records."), "", memorySoon);
        AddSoon(items, Lang.T("Too soon to say what's usual for this one. It needs an hour of checks to its server."),
            Lang.T("Too soon to say what's usual for these. Each needs an hour of checks to its server."), pingSoon);
        // A server that answers no ping or connection (Dota 2's relays): its ping can't be had, so not "too soon".
        foreach (var app in silent.Distinct())
            items.Add(new(UsualLine.Note, Lang.T($"{ServerWords.ServerOf(app, null)} doesn't answer checks, so its ping can't be shown.")));
        return items;
    }

    // ── Words ──

    /// <summary>"68° to 70°", or "68°" when both ends show the same.</summary>
    internal static string Span((double Low, double High) r)
    {
        string a = Units.TempShort(r.Low), b = Units.TempShort(r.High);
        return a == b ? a : Lang.T($"{a} to {b}");
    }

    private static (double Low, double High)? Pair(double? v) => v is double x ? (x, x) : null;

    /// <summary>"1,430 to 1,490 rpm", to the nearest 10.</summary>
    internal static string Rpm(double low, double high)
    {
        double a = Math.Round(low / 10) * 10, b = Math.Round(high / 10) * 10;
        return a == b ? Lang.T($"{a:N0} rpm") : Lang.T($"{a:N0} to {b:N0} rpm");
    }

    private static string Weeks(double weeks) => Math.Round(weeks) <= 1 ? Lang.T("1 week") : Lang.T($"{Math.Round(weeks):0} weeks");
}
