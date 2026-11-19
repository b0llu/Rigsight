using Rigsight.Core.Data;
using Rigsight.Core.Net;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

/// <summary>What a row of Your usual is about: its icon and colour, and the page it opens.</summary>
public enum UsualKind { Rest, Cpu, Game, Fan, Problems, Power, Cooling, FanWear, Drive, Memory, Ping }

/// <summary>
/// One row of Your usual: a usual this PC has (<see cref="Value"/>, "GPU 68° to 70° at 280 W"), or one it doesn't have
/// yet and what it still needs (<see cref="Detail"/>, "Too soon to say. It needs …, and has … so far."), or one that
/// can't be had and why. <see cref="Opens"/>: the page that shows more ("" for none); <see cref="Game"/> a game's exe.
/// </summary>
public sealed record UsualItem(UsualKind Kind, string Name, string? Value = null, string? Detail = null, string Opens = "", string? Game = null)
{
    /// <summary>The names a grouped row stands for, for its hover ("13 games played for less than 2 hours").</summary>
    public string? Tip { get; init; }
    /// <summary>Not read without admin rights: the row offers to restart the agent with them.</summary>
    public bool NeedsAdmin { get; init; }
}

/// <summary>A group of Your usual (TEMPERATURES, FANS, …): its rows, and whether a USUAL heading sits over the figures.</summary>
public sealed record UsualSection(string Title, IReadOnlyList<UsualItem> Items)
{
    public bool HasValues => Items.Any(i => i.Value is not null);
}

/// <summary>One of Your usual's tiles: how much history there is ("RECORDED", "54 days", "since 23 September").</summary>
public sealed record UsualTile(string Label, string Value, string Detail);

/// <summary>
/// Your usual (issue #12's "how much has RigSight learned"): how much history this PC has, every usual it's compared
/// with, and for each one not there yet what it needs and has so far. Every row asks the same check the pages compare
/// with (<see cref="PcUsual.GameReady"/>, <see cref="Wear"/>'s states, <see cref="MemoryPast.TooSoon"/>, the ping's
/// usual), so a row says ready exactly when its page starts comparing. No score and no overall percent: the usuals
/// don't share a unit.
/// </summary>
public sealed class UsualOverview
{
    public IReadOnlyList<UsualTile> Tiles { get; init; } = [];
    public IReadOnlyList<UsualSection> Sections { get; init; } = [];

    /// <summary>Heavy play under this share of a game's play time: it rarely keeps the GPU busy, and more play won't make a usual.</summary>
    internal const double RarelyBusyShare = 0.1;

    /// <summary>Steady minutes from which a game not ready gets its own row (less: it joins the "played for less than" row).</summary>
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
        var games = db.GetGameTotals();
        var played = db.GetGameTotals(from);
        var usual = PcUsual.Read(db, s, today, now, window, now, NameOf);

        var sections = new List<UsualSection>
        {
            new(Lang.T("TEMPERATURES"), Temperatures(heat, played, names, admin)),
            new(Lang.T("FANS"), Fans(db, from, to, now, admin)),
            new(Lang.T("PC PROBLEMS"), Problems(usual, use, today)),
            new(Lang.T("OVER THE MONTHS"), Months(db, today, admin)),
        };
        var apps = AppsAndNetwork(db, now, names);
        if (apps.Count > 0) sections.Add(new(Lang.T("APPS AND NETWORK"), apps));
        return new UsualOverview { Tiles = TilesOf(db.FirstDataTime(), use, games, now), Sections = sections };
    }

    // ── The tiles ──

    internal static List<UsualTile> TilesOf(long? first, IReadOnlyList<SystemDay> use, IReadOnlyList<AppTotal> games, DateTime now)
    {
        var tiles = new List<UsualTile>();
        if (first is long f)
        {
            var since = TimeUtil.FromUnix(f);
            int days = (now.Date - since.Date).Days + 1;
            tiles.Add(days >= 2
                ? new(Lang.T("RECORDED"), Lang.T($"{days} days"), Lang.T($"since {since:d MMMM}"))
                : new(Lang.T("RECORDED"), Units.Duration((now - since).TotalSeconds), since.Date == now.Date ? Lang.T($"since {since:h:mm tt} today") : Lang.T($"since {since:h:mm tt} yesterday")));
        }
        else tiles.Add(new(Lang.T("RECORDED"), Lang.T("None"), Lang.T("Not recorded")));

        double active = use.Sum(d => d.ActiveSec);
        int usedDays = use.Count(d => d.ActiveSec > 0);
        tiles.Add(new(Lang.T("ACTIVE"), Hours(active), usedDays == 1 ? Lang.T("on 1 day") : Lang.T($"on {usedDays} days")));

        double played = games.Sum(g => g.ActiveSec);
        tiles.Add(games.Count == 0 || played < 60
            ? new(Lang.T("GAMES"), Lang.T("None"), Lang.T("No games played"))
            : new(Lang.T("GAMES"), Hours(played), games.Count == 1 ? Lang.T("1 game") : Lang.T($"{games.Count} games")));
        return tiles;
    }

    /// <summary>"580h" from 10 hours, "2h 40m" under it.</summary>
    internal static string Hours(double seconds) => seconds >= 36_000 ? Lang.T($"{(int)(seconds / 3600)}h") : Units.Duration(seconds);

    // ── Temperatures: at rest, then each game ──

    internal static List<UsualItem> Temperatures(IReadOnlyList<HeatDay> heat, IReadOnlyList<AppTotal> played, IReadOnlyDictionary<long, (string Exe, string Name)> names, bool admin)
    {
        var items = new List<UsualItem>();
        string NameOf(long id) => names.TryGetValue(id, out var n) ? n.Name : Lang.T("a game");
        var (loads, rest) = PcUsual.SteadyOf(heat, NameOf);

        // Without admin rights the CPU isn't read at all: said once, with the way to get it.
        if (!admin) items.Add(new(UsualKind.Cpu, Lang.T("CPU"), Detail: Lang.T("Not read without admin rights.")) { NeedsAdmin = true });

        int restMinutes = rest?.Minutes ?? 0;
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
            if (value is not null) items.Add(new(UsualKind.Rest, Lang.T("At rest"), value, Opens: "temperatures"));
        }
        else
            items.Add(new(UsualKind.Rest, Lang.T("At rest"), Detail: restMinutes == 0
                ? Lang.T($"Too soon to say. It needs {PcUsual.MinRestMinutes} minutes at rest.")
                : Lang.T($"Too soon to say. It needs {PcUsual.MinRestMinutes} minutes at rest, and has {Units.Duration(restMinutes * 60.0)} so far.")));

        // Every game played in the usual's weeks, and any with steady play in them.
        var steady = loads.ToDictionary(l => l.AppId);
        var playSec = played.ToDictionary(p => p.AppId, p => p.ActiveSec);
        var ids = playSec.Keys.Concat(steady.Keys).Distinct().ToList();
        var ready = new List<(SteadyLoad Load, UsualItem Item)>();
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
                ready.Add((load, new(UsualKind.Game, name, value, Opens: "games", Game: exe)));
            }
            else if (minutes >= OwnRowMinutes)
                soon.Add((minutes, new(UsualKind.Game, name, Detail: GameNeeds(minutes, days))));
            else if (sec >= PcUsual.MinUsualMinutes * 60.0 && minutes < sec / 60 * RarelyBusyShare) rarely.Add((sec, name));
            else if (sec > 0) little.Add((sec, name, minutes));
        }
        items.AddRange(ready.OrderByDescending(x => x.Load.Minutes).Select(x => x.Item));
        items.AddRange(soon.OrderByDescending(x => x.Minutes).Select(x => x.Item));
        if (rarely.Count > 0)
        {
            var list = rarely.OrderByDescending(x => x.Sec).Select(x => x.Name).ToList();
            items.Add(new(UsualKind.Game, Wording.JoinAnd(list), Detail: list.Count == 1
                ? Lang.T("Not compared, since it rarely keeps the GPU busy.") : Lang.T("Not compared, since they rarely keep the GPU busy.")));
        }
        if (little.Count > 0)
        {
            var list = little.OrderByDescending(x => x.Sec).ToList();
            // One or two named; more as a count, named in the hover.
            if (list.Count <= 2)
                foreach (var g in list) items.Add(new(UsualKind.Game, g.Name, Detail: GameNeeds(g.Minutes, 0)));
            else
                items.Add(new(UsualKind.Game, Lang.T($"{list.Count} games played for less than {PcUsual.MinUsualMinutes / 60} hours"), Detail: Lang.T("Too soon to say for any of them."))
                    { Tip = Wording.JoinAnd(list.Select(x => x.Name)) });
        }
        if (ids.Count == 0) items.Add(new(UsualKind.Game, Lang.T("Games"), Detail: Lang.T("No games played.")));
        return items;
    }

    /// <summary>What a game's usual still needs: the minutes first, then the days.</summary>
    internal static string GameNeeds(int minutes, int days)
    {
        int need = PcUsual.MinUsualMinutes / 60;
        if (minutes == 0) return Lang.T($"Too soon to say. It needs {need} hours of heavy play over {PcUsual.MinUsualDays} days.");
        if (minutes < PcUsual.MinUsualMinutes)
            return Lang.T($"Too soon to say. It needs {need} hours of heavy play over {PcUsual.MinUsualDays} days, and has {Units.Duration(minutes * 60.0)} so far.");
        return Lang.T($"Too soon to say. It needs heavy play on {PcUsual.MinUsualDays} days, and has {days} so far.");
    }

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
        foreach (var fan in turning)
        {
            var spun = fanDays.Where(d => d.Fan == fan.Id && Spun(d)).ToList();
            var set = changes.Where(c => c.Time <= now && c.Subject.Contains($"|{fan.Id}|", StringComparison.Ordinal)).MaxBy(c => c.Time);
            if (set is not null) spun = [.. spun.Where(d => d.Day >= TimeUtil.ToUnix(set.Time.Date.AddDays(1)))];
            string name = twice.Contains(fan.Name) ? $"{fan.Name} · {fan.Hardware}" : fan.Name;
            if (PcUsual.FanReady(spun.Count))
            {
                double low = spun.Min(d => d.Rpm!.Value), high = spun.Max(d => d.Rpm!.Value);
                items.Add(new(UsualKind.Fan, name, Rpm(low, high), Opens: "fans"));
            }
            else
            {
                string needs = spun.Count == 0
                    ? Lang.T($"It needs {PcUsual.FanUsualDays} days of turning.")
                    : spun.Count == 1 ? Lang.T($"It needs {PcUsual.FanUsualDays} days of turning, and has 1 so far.")
                    : Lang.T($"It needs {PcUsual.FanUsualDays} days of turning, and has {spun.Count} so far.");
                items.Add(new(UsualKind.Fan, name, Detail: set is not null
                    ? Lang.T($"Too soon to say since its setting changed on {set.Time:d MMMM}.") + " " + needs
                    : Lang.T("Too soon to say.") + " " + needs));
            }
        }
        if (items.Count == 0)
            items.Add(admin ? new(UsualKind.Fan, Lang.T("Fans"), Detail: Lang.T("No fan readings on this PC."))
                : new(UsualKind.Fan, Lang.T("Fans"), Detail: Lang.T("Not read without admin rights.")) { NeedsAdmin = true });
        return items;
    }

    // ── The PC's problems, against its hours of use ──

    internal static List<UsualItem> Problems(PcUsual usual, IReadOnlyList<SystemDay> use, DateTime today)
    {
        double hours = usual.UsualHours, weeks = usual.UsualDays / 7;
        if (PcUsual.ProblemsReady(hours, weeks))
        {
            var items = new List<UsualItem>();
            if (usual.Problems is { } p)
            {
                items.Add(new(UsualKind.Problems, Lang.T("PC problems"), p.UsualCount == 0 ? Lang.T($"None in {Weeks(p.UsualWeeks)}") : Wording.Capital(PcUsual.About(p.UsualCount, p.UsualWeeks, 1)), Opens: "crashes")
                    { Tip = Lang.T("Blue screens, graphics driver resets and sudden shut-offs while in use, not power cuts") });
                if (p.PowerUsual > 0)
                    items.Add(new(UsualKind.Power, Lang.T("Sudden shut-offs that look like the power"), Wording.Capital(PcUsual.About(p.PowerUsual, p.UsualWeeks, 1)), Opens: "crashes"));
            }
            else
                // Ready, but nothing compared today (the PC wasn't used yet): what it's from.
                items.Add(new(UsualKind.Problems, Lang.T("PC problems"), Lang.T($"From {Weeks(weeks)} and {Hours(hours * 3600)} of use"), Opens: "crashes"));
            return items;
        }
        // Today counts tomorrow: said with what's on record now.
        double todayHours = use.Where(d => d.Day >= TimeUtil.ToUnix(today)).Sum(d => d.ActiveSec) / 3600;
        double has = hours + todayHours;
        int days = (int)Math.Round(usual.UsualDays) + (todayHours > 0 ? 1 : 0);
        double need = PcUsual.MinProblemHours;
        string detail = has < need
            ? has * 60 < 1 ? Lang.T($"Too soon to say. It needs a week with {need:0} hours of use.")
                : Lang.T($"Too soon to say. It needs a week with {need:0} hours of use, and has {Units.Duration(has * 3600)} so far.")
            : days == 1 ? Lang.T($"Too soon to say. It needs a week with {need:0} hours of use, and has 1 day so far.")
            : Lang.T($"Too soon to say. It needs a week with {need:0} hours of use, and has {days} days so far.");
        return [new(UsualKind.Problems, Lang.T("PC problems"), Detail: detail)
            { Tip = Lang.T("Blue screens, graphics driver resets and sudden shut-offs while in use, not power cuts") }];
    }

    // ── Over the months: cooling, fans and drives against their first weeks ──

    internal static List<UsualItem> Months(RigsightDb db, DateTime today, bool admin)
    {
        var items = new List<UsualItem>();
        var cooling = new[] { Wear.Cooling(db, false, today), Wear.Cooling(db, true, today) };
        items.Add(WearItem(UsualKind.Cooling, Lang.T("Cooling"), cooling, "temperatures"));

        var spunSensors = FanList.SpunIn(db, DateTime.Now);
        var fanIds = db.GetFans().Where(f => spunSensors.Contains(f.Sensor)).Select(f => $"fan:{f.Id}").ToHashSet();
        var fans = Wear.Fans(db, today).Where(r => fanIds.Contains(r.Part)).ToList();
        if (fans.Count > 0) items.Add(WearItem(UsualKind.FanWear, Lang.T("Fans"), fans, "fans"));

        var drives = db.GetDriveHealthDays(TimeUtil.ToUnix(Wear.Origin(today))).GroupBy(d => d.Drive).ToList();
        foreach (var g in drives.OrderBy(g => g.Last().Name, StringComparer.CurrentCultureIgnoreCase))
            items.Add(DriveItem(Wear.DriveOf(g.Key, g.Last().Name, Wear.Origin(today), [.. g.OrderBy(d => d.Day)])));
        if (drives.Count == 0 && !admin)
            items.Add(new(UsualKind.Drive, Lang.T("Drives"), Detail: Lang.T("Only how full they are, without admin rights.")) { NeedsAdmin = true });
        return items;
    }

    /// <summary>A wear row of several parts (the CPU and GPU, every fan): ready once any is, with the most weeks any has.</summary>
    internal static UsualItem WearItem(UsualKind kind, string name, IReadOnlyList<WearRow> rows, string opens)
    {
        int weeks = rows.Select(r => r.History.Count).DefaultIfEmpty(0).Max();
        if (rows.Any(r => r.State is WearState.Moved or WearState.Steady))
            return new(kind, name, Detail: Lang.T($"Each week is compared with its first {Wear.BaselineWeeks} weeks, and there are {weeks} so far."), Opens: opens);
        return new(kind, name, Detail: Lang.T("Too soon to say.") + " " + (weeks <= 0
            ? Lang.T($"It needs {Wear.MinWeeks} weeks of games or other heavy use.")
            : Lang.T($"It needs {Wear.MinWeeks} weeks of games or other heavy use, and has {weeks} so far.")));
    }

    internal static UsualItem DriveItem(WearRow w)
    {
        if (w.State == WearState.TooSoon)
        {
            int days = w.ThenDate is DateTime then ? Math.Max(1, (int)Math.Round((DateTime.Today - then.Date).TotalDays)) : 0;
            string needs = days <= 0 ? Lang.T($"Too soon to say how fast it wears. It needs {Wear.DriveMinDays} days of readings.")
                : days == 1 ? Lang.T($"Too soon to say how fast it wears. It needs {Wear.DriveMinDays} days of readings, and has 1 so far.")
                : Lang.T($"Too soon to say how fast it wears. It needs {Wear.DriveMinDays} days of readings, and has {days} so far.");
            return new(UsualKind.Drive, w.Name, Detail: needs);
        }
        string value = w.LifePerYear is double perYear ? perYear < 0.5 ? Lang.T("Under 1% a year") : Lang.T($"{perYear:0}% a year")
            : w.SectorsNow is long now ? now == (w.SectorsThen ?? now) ? Lang.T("No new bad sectors") : Lang.T($"{now - (w.SectorsThen ?? 0)} new bad sectors")
            : Wording.NoReading;
        return new(UsualKind.Drive, w.Name, value, Opens: "storage");
    }

    // ── Apps and network ──

    internal static List<UsualItem> AppsAndNetwork(RigsightDb db, DateTime now, IReadOnlyDictionary<long, (string Exe, string Name)> names)
    {
        var items = new List<UsualItem>();
        var memory = MemoryPast.Read(db, now);
        if (memory.RecordedDays > 0 || memory.Usual.Count > 0)
            items.Add(memory.TooSoon
                ? new(UsualKind.Memory, Lang.T("Memory per app"), Detail: memory.RecordedDays == 1
                    ? Lang.T($"Too soon to say. It needs {AppPast.MinUsualDays} days of records, and has 1 so far.")
                    : Lang.T($"Too soon to say. It needs {AppPast.MinUsualDays} days of records, and has {memory.RecordedDays} so far."))
                : new(UsualKind.Memory, Lang.T("Memory per app"), Lang.T($"From the last {Math.Min(AppPast.UsualDays, Math.Max(memory.RecordedDays, AppPast.MinUsualDays))} days"), Opens: "memory"));

        // Each app's server checked in the last 7 days, once per network (the usual is kept that way).
        long nowUnix = TimeUtil.ToUnix(now);
        var byExe = names.Values.GroupBy(n => n.Exe, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        foreach (var g in db.GetNetServers(nowUnix - 7 * 86400, nowUnix + 60).GroupBy(m => (m.Exe, Net: RigsightDb.NetServerPrefix(m.Server))))
        {
            var last = g.Last();
            string app = byExe.GetValueOrDefault(last.Exe) ?? Path.GetFileNameWithoutExtension(last.Exe);
            string name = last.Kind == "call" ? Lang.T($"Ping to {app}'s call server") : Lang.T($"Ping to {app}'s server");
            items.Add(db.NetServerUsual(last.Exe, last.Server, nowUnix + 60) is { } u
                ? new(UsualKind.Ping, name, Lang.T($"{u.Low} to {u.High} ms"), Opens: "network")
                : new(UsualKind.Ping, name, Detail: Lang.T("Too soon to say what's usual. It needs an hour of checks to this server.")));
        }
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
