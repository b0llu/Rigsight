using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ai;

/// <summary>
/// The look-ups beyond the report, the crashes, the changes and the games: one minute in full, temperatures and fans
/// over a period, apps, memory, the drives, internet use and when the PC was on. Each leaves out what Settings › Ask
/// RigSight says not to send, and each answer is a summary (a peak with its minute, not every minute), so a small model
/// keeps its place and a question doesn't take long.
/// </summary>
public sealed partial class AiTools
{
    private const int MaxRows = 48;

    private static JsonArray MoreDefinitions() =>
    [
        Def("get_moment", "Everything RigSight recorded around one time (10 minutes either side): temperatures, loads, power, fans, memory, the app in front and the busiest apps, internet use, and any crash then. For \"what happened at 3 PM\" or right before something.", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["time"] = new JsonObject { ["type"] = "string", ["description"] = "The local time, yyyy-MM-dd HH:mm (24-hour)." } },
            ["required"] = new JsonArray("time"),
        }),
        Def("get_temperatures", "CPU, GPU and GPU hot spot temperatures over a period: hour by hour (day by day for longer periods), with each peak's minute and what was working the part then.", Dates()),
        Def("get_fans", "Each fan's speed over a period, day by day: average, at rest, highest; and fan setting changes.", Dates()),
        Def("get_apps", "Every app used in a period, busiest first: time in front and open behind, CPU and GPU use, memory, temperatures while in use, internet.", Dates()),
        Def("get_memory", "Memory (RAM) in use over a period: hour by hour (day by day for longer), its fullest minute with the apps holding the most then, how close Windows came to running out, and the apps using the most.", Dates()),
        Def("get_storage", "Each drive: how full it is now, how much it grew over the last week and month, and the days it grew the most.", new JsonObject()),
        Def("get_network", "Internet use over a period: downloaded and uploaded day by day, the apps that used the most, big downloads, and times the connection dropped.", Dates()),
        Def("get_on_times", "When the PC was on over a period, and when it was off or asleep: each stretch it was on (day by day for longer periods), with how much of it someone was using it.", Dates()),
    ];

    /// <summary>The switches in Settings › Ask RigSight, as the person sees them there.</summary>
    private enum Switch { Crashes, Heat, Apps, Changes, Memory, Network, Parts, Usage }

    private bool On(Switch s) => s switch
    {
        Switch.Crashes => settings.Ai.ShareCrashes,
        Switch.Heat => settings.Ai.ShareHeat,
        Switch.Apps => settings.Ai.ShareApps,
        Switch.Changes => settings.Ai.ShareChanges,
        Switch.Memory => settings.Ai.ShareMemory,
        Switch.Network => settings.Ai.ShareNetwork,
        Switch.Usage => settings.Ai.ShareUsage,
        _ => settings.Ai.ShareParts,
    };

    private static string Label(Switch s) => s switch
    {
        Switch.Crashes => "Crashes and shut-offs",
        Switch.Heat => "Temperatures and fans",
        Switch.Apps => "Apps and games you use",
        Switch.Changes => "Changes: drivers, updates, BIOS, settings",
        Switch.Memory => "Memory and storage",
        Switch.Network => "Internet use",
        Switch.Usage => "When your PC was on and in use",
        _ => "Your PC's parts",
    };

    /// <summary>The switches a look-up needs (any one of them is enough for it to answer).</summary>
    private static Switch[] Needs(string name) => name switch
    {
        "get_pc_info" => [Switch.Parts],
        "get_report" => [Switch.Heat, Switch.Apps],
        "get_crashes" => [Switch.Crashes],
        "get_changes" => [Switch.Changes],
        "list_games" or "get_game" or "get_apps" => [Switch.Apps],
        "get_moment" => [Switch.Heat, Switch.Apps, Switch.Memory, Switch.Network, Switch.Crashes, Switch.Usage],
        "get_temperatures" or "get_fans" => [Switch.Heat],
        "get_memory" or "get_storage" => [Switch.Memory],
        "get_network" => [Switch.Network],
        "get_on_times" => [Switch.Usage],
        _ => [],
    };

    private static bool Known(string name) => Needs(name).Length > 0;

    /// <summary>
    /// What a turned-off look-up answers: that the person chose not to share it, and where to turn it on, so the model
    /// tells them that rather than guessing or saying nothing was recorded.
    /// </summary>
    private static string NotShared(string name)
    {
        var off = Needs(name);
        string which = string.Join(" or ", off.Select(s => $"\"{Label(s)}\""));
        Log.Write("ask", $"{name} asked for, not shared ({which} off)");
        return $"Not shared: the user turned off {which} in Settings › Ask RigSight, so none of these records are sent. "
            + $"Tell them you couldn't look at this because it's turned off, and that they can turn on {which} there and ask again. Don't guess what it would show.";
    }

    /// <summary>A line at the foot of a look-up naming what it left out because a switch is off ("" when nothing was).</summary>
    private string LeftOut(params Switch[] relevant)
    {
        var off = relevant.Where(s => !On(s)).ToList();
        return off.Count == 0 ? "" : Environment.NewLine + $"Left out because the user turned off sharing in Settings › Ask RigSight: {string.Join(", ", off.Select(Label))}. If the question needs it, tell them it's turned off and where to turn it on.";
    }

    private string? RunMore(string name, JsonObject args) => name switch
    {
        "get_moment" => WithDb(db => Moment(db, At(args))) + LeftOut(Switch.Heat, Switch.Apps, Switch.Memory, Switch.Network, Switch.Crashes, Switch.Usage),
        "get_temperatures" => WithDb(db => Temperatures(db, Day(args, "from"), Day(args, "to"))) + LeftOut(Switch.Apps),
        "get_fans" => WithDb(db => Fans(db, Day(args, "from"), Day(args, "to"))),
        "get_apps" => WithDb(db => Apps(db, Day(args, "from"), Day(args, "to"))) + LeftOut(Switch.Memory, Switch.Heat, Switch.Network),
        "get_memory" => WithDb(db => Memory(db, Day(args, "from"), Day(args, "to"))) + LeftOut(Switch.Apps),
        "get_storage" => WithDb(Storage),
        "get_network" => WithDb(db => Network(db, Day(args, "from"), Day(args, "to"))) + LeftOut(Switch.Apps),
        "get_on_times" => WithDb(db => OnTimes(db, Day(args, "from"), Day(args, "to"))) + LeftOut(Switch.Crashes),
        _ => null,
    };

    private static DateTime At(JsonObject args)
    {
        string? s = (string?)args["time"];
        foreach (var f in new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd HH:mm:ss" })
            if (DateTime.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return t;
        throw new FormatException("'time' must be a local time like 2026-10-20 15:00.");
    }

    /// <summary>The period as unix seconds, its days in order, and how to say it.</summary>
    private static (long From, long To, string Said) Span(DateTime from, DateTime to)
    {
        if (to < from) (from, to) = (to, from);
        string said = from == to ? from.ToString("dddd d MMM yyyy", CultureInfo.InvariantCulture) : $"{from:d MMM yyyy} to {to:d MMM yyyy}";
        return (TimeUtil.ToUnix(from.Date), TimeUtil.ToUnix(to.Date.AddDays(1)), said);
    }

    private Func<long?, string> Namer(RigsightDb db)
    {
        var apps = db.LoadApps().ToDictionary(a => a.Id);
        return id => id is long i && apps.TryGetValue(i, out var a)
            ? settings.AppNames.TryGetValue(a.Exe, out var alias) ? alias : AppCatalog.KnownName(a.Exe) ?? a.Name
            : "an app";
    }

    private static string T(double? c) => c is double v ? $"{v:0} °C" : "-";
    private static string Pct(double? p) => p is double v ? $"{v:0}%" : "-";

    // ── One minute in full ───────────────────────────────────────────────
    private string Moment(RigsightDb db, DateTime at)
    {
        long t = TimeUtil.ToUnix(at);
        var minutes = db.GetMinutes(t - 600, t + 660);
        var name = Namer(db);
        var sb = new StringBuilder();
        sb.AppendLine($"Around {at:dddd d MMM yyyy, h:mm tt} (10 minutes either side).");
        if (minutes.Count == 0)
        {
            string before = db.LastMinuteBefore(t) is long b ? TimeUtil.FromUnix(b).ToString("d MMM h:mm tt", CultureInfo.InvariantCulture) : "never";
            string after = db.GetMinutes(t, t + 86400).FirstOrDefault() is { } next ? TimeUtil.FromUnix(next.Ts).ToString("d MMM h:mm tt", CultureInfo.InvariantCulture) : "not within a day";
            sb.AppendLine($"Nothing was recorded then: the PC was off or asleep (or recording wasn't running). Last minute recorded before: {before}. First after: {after}.");
        }
        else
        {
            var a = settings.Ai;
            sb.AppendLine("Each recorded minute:");
            foreach (var m in minutes)
            {
                var parts = new List<string>();
                if (a.ShareHeat)
                {
                    parts.Add($"CPU {T(m.CpuTempMax)} {Pct(m.CpuLoad)} busy");
                    parts.Add($"GPU {T(m.GpuTempMax)} {Pct(m.GpuLoad)} busy" + (m.GpuHotMax is double h ? $", hot spot {h:0} °C" : ""));
                    if (m.CpuPower is double cp || m.GpuPower is double) parts.Add($"power CPU {m.CpuPower:0} W, GPU {m.GpuPower:0} W");
                }
                if (a.ShareMemory && m.RamUsed is double ram) parts.Add($"memory {ram:0.0} GB" + (m.CommitPct is double c && c >= 80 ? $" (Windows {c:0}% of what it can promise)" : ""));
                if (a.ShareApps && m.FgApp is long fg) parts.Add($"in front: {name(fg)}" + (m.Fps is double fps ? $" at {fps:0} fps" : ""));
                if (a.ShareUsage && m.ActiveSec == 0) parts.Add("nobody at the PC");
                sb.AppendLine($"  {TimeUtil.FromUnix(m.Ts):h:mm tt}: {string.Join("; ", parts)}");
            }
            if (a.ShareHeat)
            {
                var fans = db.GetFans().ToDictionary(f => f.Id);
                var speeds = db.GetFanMinutes(t - 300, t + 360).GroupBy(f => f.Fan)
                    .Select(g => fans.TryGetValue(g.Key, out var f) ? $"{f.Name} {g.Average(x => x.RpmAvg):0} rpm (up to {g.Max(x => x.RpmMax)})" : null)
                    .OfType<string>().ToList();
                if (speeds.Count > 0) sb.AppendLine("Fans in the 5 minutes either side: " + string.Join(", ", speeds) + ".");
            }
            if (a.ShareApps)
            {
                var busiest = db.GetAppMinutes(t - 300, t + 360).GroupBy(x => x.App)
                    .Select(g => (App: g.Key, Cpu: g.Average(x => x.Cpu), Gpu: g.Average(x => x.Gpu), Mem: g.Max(x => x.MemMb)))
                    .OrderByDescending(x => x.Cpu + x.Gpu).Take(6).ToList();
                if (busiest.Count > 0)
                    sb.AppendLine("Busiest apps in the 5 minutes either side: " + string.Join(", ", busiest.Select(b =>
                        $"{name(b.App)} (CPU {b.Cpu:0}%, GPU {b.Gpu:0}%" + (a.ShareMemory ? $", memory {Units.Megabytes(b.Mem)})" : ")"))) + ".");
            }
            if (a.ShareNetwork && db.GetNetMinutes(t - 300, t + 360) is { Count: > 0 } net)
                sb.AppendLine($"Internet in the 5 minutes either side: {Units.Data(net.Sum(n => n.Down))} down, {Units.Data(net.Sum(n => n.Up))} up.");
        }
        if (settings.Ai.ShareCrashes)
        {
            var near = db.GetCrashes(t - 1800, t + 1800);
            if (near.Count > 0)
                sb.AppendLine("Problems within half an hour: " + string.Join("; ", near.Select(c => $"{TimeUtil.FromUnix(c.Ts):h:mm tt} {CrashExplainer.Explain(c).Title}")) + ".");
        }
        return sb.ToString();
    }

    // ── Temperatures ─────────────────────────────────────────────────────
    private string Temperatures(RigsightDb db, DateTime from, DateTime to)
    {
        var (f, t, said) = Span(from, to);
        bool byDay = (t - f) > 2 * 86400;
        var rows = db.GetTempHours(f, t, byDay);
        if (rows.Count == 0) return $"No temperatures recorded on {said}.";
        var sb = new StringBuilder();
        sb.AppendLine($"Temperatures on {said}, {(byDay ? "day by day" : "hour by hour")} (average / highest):");
        foreach (var r in rows.TakeLast(MaxRows))
            sb.AppendLine($"  {TimeUtil.FromUnix(r.Ts).ToString(byDay ? "ddd d MMM" : "ddd h tt", CultureInfo.InvariantCulture)}: CPU {T(r.CpuTemp)} / {T(r.CpuTempMax)}, GPU {T(r.GpuTemp)} / {T(r.GpuTempMax)}"
                + (r.GpuHotMax is double h ? $", hot spot up to {h:0} °C" : ""));
        if (rows.Count > MaxRows) sb.AppendLine($"  (the {rows.Count - MaxRows} earlier rows left out)");
        var range = db.TempRange(f, t);
        var name = Namer(db);
        void Peak(string part, string column, string appColumn, double? value)
        {
            if (value is not double v) return;
            string when = db.FindMinute(column, v, f, t, settings.Ai.ShareApps ? appColumn : null) is { } m
                ? $" at {TimeUtil.FromUnix(m.Ts):ddd d MMM h:mm tt}" + (m.App is long app ? $", with {name(app)} working it hardest" : "") : "";
            sb.AppendLine($"{part} peak: {v:0} °C{when}.");
        }
        Peak("CPU", "cpu_temp_max", "cpu_app", range.CpuMax);
        Peak("GPU", "gpu_temp_max", "gpu_app", range.GpuMax);
        if (range.HotMax is double hot) sb.AppendLine($"GPU hot spot peak: {hot:0} °C.");
        sb.AppendLine("For how this compares with the PC's usual, see get_report for the same period.");
        return sb.ToString();
    }

    // ── Fans ─────────────────────────────────────────────────────────────
    private string Fans(RigsightDb db, DateTime from, DateTime to)
    {
        var (f, t, said) = Span(from, to);
        var fans = db.GetFans().ToDictionary(x => x.Id);
        var days = db.GetFanDays(f, t);
        var sb = new StringBuilder();
        if (days.Count == 0) sb.AppendLine($"No fan speeds recorded on {said}.");
        else
        {
            sb.AppendLine($"Fan speeds on {said}, day by day (average, at rest, highest):");
            var groups = days.GroupBy(d => d.Fan).ToList();
            string FanName(long id) => fans.TryGetValue(id, out var row) ? $"{row.Name} ({row.Hardware})" : "a fan";
            // A header that never read a speed has nothing on it (or a fan it can't read): one line for those, not a week of zeros.
            var still = groups.Where(g => g.All(d => d.RpmMax == 0)).ToList();
            foreach (var g in groups.Except(still))
            {
                sb.AppendLine($"  {FanName(g.Key)}:");
                foreach (var d in g.TakeLast(14))
                    sb.AppendLine($"    {TimeUtil.FromUnix(d.Day):ddd d MMM}: {d.Rpm:0} rpm, at rest {(d.IdleRpm is double i ? $"{i:0} rpm" : "-")}, up to {d.RpmMax} rpm");
            }
            if (still.Count > 0) sb.AppendLine($"Read 0 rpm the whole time (nothing plugged in there, or a fan it can't read): {string.Join(", ", still.Select(g => FanName(g.Key)))}.");
        }
        var changes = db.GetChanges(f, t).Where(c => c.Kind == ChangeKind.Fans).ToList();
        if (changes.Count > 0)
            sb.AppendLine("Fan setting changes: " + string.Join("; ", changes.Select(c => $"{c.Time:d MMM h:mm tt} {c.Shown}")) + ".");
        return sb.ToString();
    }

    // ── Apps ─────────────────────────────────────────────────────────────
    private string Apps(RigsightDb db, DateTime from, DateTime to)
    {
        var (f, t, said) = Span(from, to);
        var name = Namer(db);
        var totals = db.GetAppTotals(f, t).Where(a => a.FgSec + a.BgSec + a.IdleSec >= 60).OrderByDescending(a => a.FgSec).ThenByDescending(a => a.BgSec).ToList();
        if (totals.Count == 0) return $"No apps recorded on {said}.";
        var net = settings.Ai.ShareNetwork ? db.GetNetAppTotals(f, t).ToDictionary(n => n.App) : [];
        var sb = new StringBuilder();
        sb.AppendLine($"Apps on {said}, most time in front first (at most 25):");
        foreach (var a in totals.Take(25))
        {
            var parts = new List<string> { $"{Units.Duration(a.FgSec)} in front", $"{Units.Duration(a.BgSec + a.MinSec)} open behind" };
            if (a.CpuN > 0) parts.Add($"CPU {a.CpuSum / a.CpuN:0}% on average (up to {a.CpuMax:0}%)");
            if (a.GpuLoadN > 0) parts.Add($"GPU {a.GpuLoadSum / a.GpuLoadN:0}% busy while in front");
            if (settings.Ai.ShareMemory && a.MemN > 0) parts.Add($"memory {Units.Megabytes(a.MemSum / a.MemN)} on average (up to {Units.Megabytes(a.MemMax ?? 0)})");
            if (settings.Ai.ShareHeat && a.GpuTempN > 0) parts.Add($"GPU {a.GpuTempSum / a.GpuTempN:0} °C while in use (up to {a.GpuTempMax:0} °C)");
            if (settings.Ai.ShareHeat && a.CpuTempN > 0) parts.Add($"CPU {a.CpuTempSum / a.CpuTempN:0} °C while in use");
            if (net.TryGetValue(a.AppId, out var n) && n.Down + n.Up > 0) parts.Add($"internet {Units.Data(n.Down)} down, {Units.Data(n.Up)} up");
            sb.AppendLine($"  {name(a.AppId)}: {string.Join(", ", parts)}");
        }
        if (totals.Count > 25) sb.AppendLine($"  ({totals.Count - 25} more with less time)");
        return sb.ToString();
    }

    // ── Memory ───────────────────────────────────────────────────────────
    private string Memory(RigsightDb db, DateTime from, DateTime to)
    {
        var (f, t, said) = Span(from, to);
        var minutes = db.GetMinutes(f, t).Where(m => m.RamUsed is not null).ToList();
        if (minutes.Count == 0) return $"No memory readings on {said}.";
        bool byDay = (t - f) > 2 * 86400;
        var sb = new StringBuilder();
        if (db.GetInventoryValue("ram") is { Length: > 0 } ram) sb.AppendLine($"RAM in this PC: {ram}.");
        sb.AppendLine($"Memory in use on {said}, {(byDay ? "day by day" : "hour by hour")} (average / highest):");
        var groups = minutes.GroupBy(m => { var d = TimeUtil.FromUnix(m.Ts); return byDay ? d.Date : d.Date.AddHours(d.Hour); }).ToList();
        foreach (var g in groups.TakeLast(MaxRows))
            sb.AppendLine($"  {g.Key.ToString(byDay ? "ddd d MMM" : "ddd h tt", CultureInfo.InvariantCulture)}: {g.Average(m => m.RamUsed!.Value):0.0} / {g.Max(m => m.RamUsed!.Value):0.0} GB");
        var full = minutes.MaxBy(m => m.RamUsed!.Value)!;
        sb.Append($"Fullest minute: {full.RamUsed:0.0} GB at {TimeUtil.FromUnix(full.Ts):ddd d MMM h:mm tt}");
        var name = Namer(db);
        if (settings.Ai.ShareApps)
        {
            var holding = db.GetAppMinutes(full.Ts, full.Ts + 60).OrderByDescending(a => a.MemMb).Take(5).ToList();
            if (holding.Count > 0) sb.Append("; holding the most then: " + string.Join(", ", holding.Select(h => $"{name(h.App)} {Units.Megabytes(h.MemMb)}")));
        }
        sb.AppendLine(".");
        if (minutes.Where(m => m.CommitPct is not null).MaxBy(m => m.CommitPct) is { CommitPct: double c } tight)
            sb.AppendLine($"Closest Windows came to running out: {c:0}% of what it can promise (RAM and page file together) at {TimeUtil.FromUnix(tight.Ts):ddd d MMM h:mm tt}. Near 100% is about to run out.");
        if (settings.Ai.ShareApps)
        {
            var most = db.GetAppMemoryTotals(f, t).Where(x => x.MemN > 0).OrderByDescending(x => x.MemSum / x.MemN).Take(8).ToList();
            if (most.Count > 0) sb.AppendLine("Apps using the most memory on average while open: " + string.Join(", ", most.Select(m => $"{name(m.AppId)} {Units.Megabytes(m.MemSum / m.MemN)}")) + ".");
        }
        return sb.ToString();
    }

    // ── Drives ───────────────────────────────────────────────────────────
    private string Storage(RigsightDb db)
    {
        var days = db.GetDriveDays(TimeUtil.ToUnix(DateTime.Today.AddDays(-90)));
        if (days.Count == 0) return "No drive sizes recorded.";
        var sb = new StringBuilder();
        sb.AppendLine("Drives (from the daily record of how full each is):");
        foreach (var g in days.GroupBy(d => d.Drive).OrderBy(g => g.Key))
        {
            var list = g.OrderBy(d => d.Day).ToList();
            var now = list[^1];
            double? Grew(int back) => list.LastOrDefault(d => d.Day <= TimeUtil.ToUnix(DateTime.Today.AddDays(-back))) is { } then ? now.UsedGb - then.UsedGb : null;
            string Said(double? gb) => gb is double v ? $"{(v >= 0 ? "+" : "")}{Units.DataGb(v)}" : "not recorded";
            sb.AppendLine($"  {g.Key}: {Units.DataGb(now.UsedGb)} used of {Units.DataGb(now.TotalGb)} ({now.UsedGb / Math.Max(1, now.TotalGb) * 100:0}% full), last week {Said(Grew(7))}, last month {Said(Grew(30))}");
            var jumps = list.Zip(list.Skip(1), (a, b) => (b.Day, Gb: b.UsedGb - a.UsedGb)).Where(j => Math.Abs(j.Gb) >= 1).OrderByDescending(j => Math.Abs(j.Gb)).Take(3).ToList();
            if (jumps.Count > 0) sb.AppendLine("    Biggest changes in a day: " + string.Join(", ", jumps.Select(j => $"{TimeUtil.FromUnix(j.Day):d MMM} {Said(j.Gb)}")));
        }
        sb.AppendLine("How full each drive is gets recorded once a day, not which files changed.");
        return sb.ToString();
    }

    // ── Internet ─────────────────────────────────────────────────────────
    private string Network(RigsightDb db, DateTime from, DateTime to)
    {
        var (f, t, said) = Span(from, to);
        var days = db.GetNetDays(f, t);
        if (days.Count == 0) return $"No internet use recorded on {said}.";
        var name = Namer(db);
        var sb = new StringBuilder();
        sb.AppendLine($"Internet on {said}: {Units.Data(days.Sum(d => d.Down))} downloaded, {Units.Data(days.Sum(d => d.Up))} uploaded. Day by day:");
        foreach (var d in days.TakeLast(31))
            sb.AppendLine($"  {TimeUtil.FromUnix(d.Day):ddd d MMM}: {Units.Data(d.Down)} down, {Units.Data(d.Up)} up, {Units.Data(d.Background)} of it by apps not in front"
                + (d.Best is long best ? $", fastest steady download {Units.Speed(best)}" : ""));
        if (settings.Ai.ShareApps)
        {
            var apps = db.GetNetAppTotals(f, t).OrderByDescending(a => a.Down + a.Up).Take(10).ToList();
            if (apps.Count > 0) sb.AppendLine("Apps that used the most: " + string.Join(", ", apps.Select(a => $"{name(a.App)} {Units.Data(a.Down)} down, {Units.Data(a.Up)} up")) + ".");
            var big = db.GetNetTransfers(f, t, 8);
            if (big.Count > 0) sb.AppendLine("Big downloads: " + string.Join("; ", big.Select(b => $"{TimeUtil.FromUnix(b.Start):d MMM h:mm tt} {name(b.App)} {Units.Data(b.Bytes)} at {Units.Speed(b.Speed)}")) + ".");
        }
        var drops = db.GetNetDrops(f, t);
        sb.AppendLine(drops.Count == 0 ? "The connection didn't drop." : $"The connection dropped {drops.Count} times: " + string.Join("; ", drops.TakeLast(10).Select(d =>
            $"{TimeUtil.FromUnix(d.Start):d MMM h:mm tt} for {Units.Duration(d.Seconds)} ({(d.Kind == NetDropKind.Link ? "the cable or Wi-Fi itself" : "past the PC: the router or the provider")})")) + ".");
        return sb.ToString();
    }

    // ── When the PC was on ───────────────────────────────────────────────
    private string OnTimes(RigsightDb db, DateTime from, DateTime to)
    {
        var (f, t, said) = Span(from, to);
        var sb = new StringBuilder();
        if ((t - f) > 3 * 86400)
        {
            var use = db.GetDayUse(f, t);
            if (use.Count == 0) return $"Nothing recorded on {said}: the PC was off, or recording wasn't running.";
            sb.AppendLine($"The PC on {said}, day by day (on, and in use):");
            foreach (var d in use) sb.AppendLine($"  {TimeUtil.FromUnix(d.Day):ddd d MMM}: on {Units.Duration(d.OnSec)}, in use {Units.Duration(d.ActiveSec)}");
            return sb.ToString();
        }
        var minutes = db.GetMinutes(f, t);
        if (minutes.Count == 0) return $"Nothing recorded on {said}: the PC was off, or recording wasn't running.";
        // A gap of more than three minutes is the PC off or asleep (RigSight records every minute it's on).
        var runs = new List<(long Start, long End, int Active)>();
        foreach (var m in minutes)
        {
            if (runs.Count > 0 && m.Ts - runs[^1].End <= 180) runs[^1] = (runs[^1].Start, m.Ts + 60, runs[^1].Active + m.ActiveSec);
            else runs.Add((m.Ts, m.Ts + 60, m.ActiveSec));
        }
        sb.AppendLine($"The PC on {said}. Between these stretches it was off or asleep (the records can't tell which):");
        foreach (var r in runs.TakeLast(40))
            sb.AppendLine($"  {TimeUtil.FromUnix(r.Start):ddd d MMM h:mm tt} to {TimeUtil.FromUnix(r.End):h:mm tt}: on {Units.Duration(r.End - r.Start)}, in use {Units.Duration(r.Active)}");
        if (settings.Ai.ShareCrashes && db.GetCrashes(f, t).Where(c => c.Kind is CrashKind.UnexpectedShutdown or CrashKind.SystemCrash).ToList() is { Count: > 0 } downs)
            sb.AppendLine("Stretches that ended in a crash or sudden shut-off: " + string.Join("; ", downs.Select(c => $"{TimeUtil.FromUnix(c.Ts):d MMM h:mm tt} {CrashExplainer.Explain(c).Title}")) + ".");
        return sb.ToString();
    }

    // ── The report, without what isn't to be sent ────────────────────────
    private static readonly HashSet<string> HeatFree = ["screen", "screen-compare", "early", "span", "streak", "stretch", "top-app", "idle-app", "gaming", "away", "memory",
        "crash", "app-crashes", "usual-problems", "usual-power", "power-cuts", "pc-problems", "no-problems"];
    private static readonly HashSet<string> AppFree = ["screen", "screen-compare", "early", "span", "streak", "away", "comfortable", "drift", "throttle", "over-limit", "hotspot",
        "usual-heat", "temps-usual", "fans-usual", "fan-stopped", "fan-slower", "fan-older", "usual-problems", "usual-power", "power-cuts", "pc-problems", "no-problems",
        "net-recap", "net-drops", "net-upload", "net-record"];
    private static readonly HashSet<string> UsageKeys = ["screen", "screen-compare", "early", "span", "streak", "stretch", "away", "record", "gaming"];
    private static readonly HashSet<string> CrashKeys = ["crash", "app-crashes", "usual-problems", "usual-power", "power-cuts", "pc-problems", "no-problems"];

    /// <summary>
    /// Leaves out of the report what Settings › Ask RigSight says not to send: it once sent a day's internet use with the
    /// Internet switch off, as the report always includes it.
    /// </summary>
    private void Trim(Report r)
    {
        var a = settings.Ai;
        if (!a.ShareNetwork)
        {
            r.Net = null;
            r.Insights.RemoveAll(i => i.Key.StartsWith("net-", StringComparison.Ordinal));
            foreach (var app in r.Apps) app.NetBytes = null;
        }
        if (!a.ShareMemory)
        {
            r.Insights.RemoveAll(i => i.Key == "memory");
            foreach (var app in r.Apps) { app.MemAvg = null; app.MemMax = null; }
        }
        if (!a.ShareUsage) r.Insights.RemoveAll(i => UsageKeys.Contains(i.Key));
        if (!a.ShareCrashes)
        {
            r.Crashes.Clear();
            r.Insights.RemoveAll(i => CrashKeys.Contains(i.Key));
        }
        if (!a.ShareHeat)
        {
            r.CpuTempPeak = r.GpuTempPeak = null;
            r.Usual = null;
            r.Insights.RemoveAll(i => !HeatFree.Contains(i.Key) && !i.Key.StartsWith("net-", StringComparison.Ordinal));
            foreach (var app in r.Apps) { app.CpuTempMax = app.GpuTempMax = app.CpuTempAvg = app.GpuTempAvg = app.GpuHotMax = null; }
        }
        if (!a.ShareApps)
        {
            r.Apps.Clear();
            r.Insights.RemoveAll(i => !AppFree.Contains(i.Key));
            if (r.CpuTempPeak is { } c) r.CpuTempPeak = c with { App = null };
            if (r.GpuTempPeak is { } g) r.GpuTempPeak = g with { App = null };
        }
    }
}
