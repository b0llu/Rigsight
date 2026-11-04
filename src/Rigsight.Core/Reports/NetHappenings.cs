using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Reports;

/// <summary>What a line of the Network page's "What happened" list is about.</summary>
public enum NetHappeningKind { Insight, Steam, Played, Busy, New, PingSwitch }

/// <summary>
/// One line of "What happened": when (<see cref="When"/> as the list shows it, empty for a line about the whole period
/// or the whole day), what, a muted line of detail, the amount on the right, and the app it's about (its program, for
/// the icon). <see cref="PingNote"/> is what the ping did meanwhile, in plain words (never a warning: using the internet
/// isn't a problem). <see cref="At"/> is the hour (or day) it points at on the chart; <see cref="Day"/> the day it's
/// listed under (null: the period's own lines, on top).
/// </summary>
public sealed record NetHappening(NetHappeningKind Kind, DateTime? Day, DateTime? At, string When, string Text)
{
    public string? Detail { get; init; }
    public string? Amount { get; init; }
    public string? PingNote { get; init; }
    public string? Path { get; init; }
    /// <summary>An insight's icon (a Segoe Fluent glyph) and tone, where the line has no app.</summary>
    public string? Icon { get; init; }
    public InsightTone Tone { get; init; }
    /// <summary>The app is on the internet for the first time.</summary>
    public bool IsNew { get; init; }
}

/// <summary>
/// The period told in plain lines, newest first under each day: what Steam installed and updated (by name, from its own
/// log), what other apps did while a game was played and what the ping did then, the biggest single hours, and the apps
/// that went online for the first time. The period's insights come first. Only what stands out: an ordinary hour of a
/// browser isn't a line.
/// </summary>
public static class NetHappenings
{
    private const long MB = 1L << 20, GB = 1L << 30;

    /// <summary>A Steam download this small is Steam checking a game, not a line (and Steam logs plenty of those).</summary>
    public const long MinSteamBytes = 50 * MB;
    /// <summary>Steam downloads starting this close together are one line ("Steam updated 4 games").</summary>
    public const int SteamTogetherMinutes = 10;
    /// <summary>An app using this much while a game was played is worth saying.</summary>
    public const long PlayedBytes = GB;
    /// <summary>An app's hour this big is a line of its own (an hourly chart); a day this big on a month's or year's.</summary>
    public const long BusyHourBytes = GB, BusyDayBytes = 5 * GB;
    /// <summary>A game in front this long in an hour makes it an hour of that game (seconds).</summary>
    public const double PlayedHourSeconds = 600;
    /// <summary>…when it is this many times that app's usual hour (or day) in the period: a browser busy every evening isn't news.</summary>
    public const double BusyTimes = 3;
    /// <summary>At most this many lines a day (Steam, games and new apps first, then the biggest), this many of them busy hours, and in all.</summary>
    public const int PerDay = 6, BusyPerDay = 2, MaxLines = 60;

    private sealed record Line(NetHappening Item, long Bytes);

    public static List<NetHappening> Build(RigsightDb db, NetReport r, IReadOnlyDictionary<long, AppRow> apps, RigsightSettings settings)
    {
        var list = new List<NetHappening>();
        if (!r.HasData) return list;
        list.AddRange(r.Insights.OrderByDescending(i => i.Priority).Select(i =>
            new NetHappening(NetHappeningKind.Insight, null, null, "", i.Text) { Detail = i.DetailOrNull, Icon = i.Icon, Tone = i.Tone }));

        long f = TimeUtil.ToUnix(r.From), t = TimeUtil.ToUnix(r.To);
        string Name(long id) => NetReportBuilder.NameOf(apps, settings, id);
        string? PathOf(long id) => apps.TryGetValue(id, out var a) ? a.Path : null;
        var lines = new List<Line>();
        var steamApp = apps.Values.FirstOrDefault(a => a.Exe.Equals("steam.exe", StringComparison.OrdinalIgnoreCase));

        // ── Steam, by name ──
        var steam = r.Bins.SelectMany(b => b.Steam).Where(d => d.Bytes >= MinSteamBytes).OrderBy(d => d.Started).ToList();
        var steamCovered = new List<(DateTime From, DateTime To)>();
        for (int i = 0; i < steam.Count;)
        {
            var group = new List<GameDownload> { steam[i] };
            while (++i < steam.Count && (steam[i].Started - group[0].Started).TotalMinutes <= SteamTogetherMinutes) group.Add(steam[i]);
            var first = group[0];
            long bytes = group.Sum(d => d.Bytes);
            var end = group.Max(d => d.Ended);
            steamCovered.Add((first.Started.AddMinutes(-30), end.AddMinutes(30)));
            string text, detail;
            if (group.Count == 1)
            {
                text = first.Name is null ? Lang.T($"Steam downloaded {Units.Data(bytes)} for a game it no longer names")
                    : first.NewInstall ? Lang.T($"Steam installed {first.Name}") : Lang.T($"Steam updated {first.Name}");
                var minutes = (first.Ended - first.Started).TotalMinutes;
                detail = first.Name is null ? Lang.T("Steam forgets a game's name once it's uninstalled")
                    : minutes >= 10 ? Lang.T($"Over {Units.Duration(minutes * 60)}") : "";
            }
            else
            {
                bool installs = group.All(d => d.NewInstall);
                text = installs ? Lang.T($"Steam installed {group.Count} games") : Lang.T($"Steam updated {group.Count} games");
                detail = string.Join(" · ", group.OrderByDescending(d => d.Bytes).Select(d => $"{d.Name ?? Lang.T("a game it no longer names")} {Units.Data(d.Bytes)}"));
            }
            // A game being played meanwhile: said, with what the ping did (a download behind a game is what players feel).
            var during = GameDuring(r, first.Started, end);
            if (during is not null) detail = string.Join(" · ", new[] { detail, Lang.T($"while you played {during}") }.Where(s => s.Length > 0));
            lines.Add(new(new NetHappening(NetHappeningKind.Steam, first.Started.Date, first.Started, Clock(first.Started), text)
            {
                Detail = detail.Length > 0 ? detail : null, Amount = Units.Data(bytes), Path = steamApp?.Path,
                PingNote = during is null ? null : PingDuring(r, first.Started, end),
            }, bytes));
        }

        // ── While a game was played: the app that used the most besides it (hourly charts only) ──
        var covered = new HashSet<(long App, DateTime Hour)>();
        if (r.Hourly)
        {
            for (int i = 0; i < r.Bins.Count;)
            {
                if (r.Bins[i].Game is not { } game) { i++; continue; }
                int start = i;
                while (i < r.Bins.Count && r.Bins[i].Game == game) i++;
                var stretch = r.Bins.Skip(start).Take(i - start).ToList();
                var others = stretch.SelectMany(b => b.Apps).Where(a => !string.Equals(Name(a.App), game, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(a => a.App).Select(g => (App: g.Key, Down: g.Sum(a => a.Down), Up: g.Sum(a => a.Up))).OrderByDescending(a => a.Down + a.Up).ToList();
                if (others.FirstOrDefault() is not { App: > 0 } top || top.Down + top.Up < PlayedBytes) continue;
                var from = stretch[0].Start;
                var to = stretch[^1].Start.AddHours(1);
                // Steam downloading behind the game is already Steam's own line.
                if (top.App == steamApp?.Id && steamCovered.Any(c => c.From < to && c.To > from)) continue;
                long used = top.Down + top.Up;
                bool upload = top.Up * 2 >= used;
                foreach (var b in stretch) covered.Add((top.App, b.Start));
                string? note = PingDuring(r, from, to);
                bool rose = stretch.Any(b => b.Ping is { } p && r.Line is { } l && p.Highest >= NetLinePing.HighFor(l.Usual));
                string detail = upload ? Lang.T($"↑ {Units.Data(top.Up)} of it upload") : Lang.T($"↓ {Units.Data(top.Down)} of it download");
                if (rose && upload) detail = Lang.T("When one app fills the upload, game traffic waits behind it") + " · " + detail;
                lines.Add(new(new NetHappening(NetHappeningKind.Played, from.Date, from, Span(from, to), Lang.T($"{Name(top.App)} used {Units.Data(used)} while you played {game}"))
                {
                    Detail = detail, Amount = Units.Data(used), Path = PathOf(top.App), PingNote = note,
                }, used));
            }
        }

        // ── The biggest single hours (or days): an app using far more at once than it usually does, not already a line ──
        var usualBin = r.Bins.SelectMany(b => b.Apps).Where(a => a.Total > 0).GroupBy(a => a.App)
            .ToDictionary(g => g.Key, g => NetReportBuilder.Median([.. g.Select(a => a.Total)]));
        TimeSpan binLength = r.Hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        var busy = r.Bins.SelectMany(b => b.Apps.Select(a => (Bin: b, Use: a)))
            .Where(x => x.Use.Total >= (r.Hourly ? BusyHourBytes : BusyDayBytes) && x.Use.Total >= usualBin.GetValueOrDefault(x.Use.App) * BusyTimes)
            .Where(x => !covered.Contains((x.Use.App, x.Bin.Start)))
            .Where(x => !(x.Use.App == steamApp?.Id && steamCovered.Any(c => c.From < x.Bin.Start + binLength && c.To > x.Bin.Start)))
            .GroupBy(x => x.Bin.Start.Date).SelectMany(g => g.OrderByDescending(x => x.Use.Total).Take(BusyPerDay))
            .ToList();
        foreach (var (bin, use) in busy)
        {
            bool behind = use.Background * 10 >= use.Total * 9;
            lines.Add(new(new NetHappening(NetHappeningKind.Busy, bin.Start.Date, bin.Start, r.Hourly ? Clock(bin.Start, hourOnly: true) : "",
                Lang.T($"{Name(use.App)} used {Units.Data(use.Total)}"))
            {
                Detail = behind ? Lang.T("In the background") : null, Amount = Units.Data(use.Total), Path = PathOf(use.App),
            }, use.Total));
        }

        // ── Ping checks paused or resumed (Settings, or the Network page) ──
        foreach (var (at, on) in r.PingSwitches)
            lines.Add(new(new NetHappening(NetHappeningKind.PingSwitch, at.Date, at, Clock(at), on ? Lang.T("Ping checks were resumed") : Lang.T("Ping checks were paused"))
            {
                Icon = on ? "\uE768" : "\uE769",
            }, long.MaxValue / 2));

        // ── On the internet for the first time: the biggest of the day by name, the others after it ──
        var firstDays = db.GetNetFirstDays();
        long began = firstDays.Count > 0 ? firstDays.Values.Min() : long.MaxValue;
        var newApps = firstDays.Where(kv => kv.Value >= f && kv.Value < t && kv.Value > began).ToLookup(kv => TimeUtil.FromUnix(kv.Value).Date, kv => kv.Key);
        foreach (var day in newApps)
        {
            var used = db.GetNetAppDays(TimeUtil.ToUnix(day.Key), TimeUtil.ToUnix(day.Key.AddDays(1))).ToDictionary(u => u.App, u => u.Total);
            var named = day.Where(apps.ContainsKey).Select(id => (Id: id, Bytes: used.GetValueOrDefault(id))).Where(x => x.Bytes >= MB).OrderByDescending(x => x.Bytes).ToList();
            if (named.Count == 0) continue;
            var lead = named[0];
            var rest = named.Skip(1).Select(x => Name(x.Id)).ToList();
            string? also = rest.Count == 0 ? null : rest.Count <= 3 ? Lang.T($"Also new: {string.Join(", ", rest)}")
                : Lang.T($"Also new: {string.Join(", ", rest.Take(3))} and {rest.Count - 3} more");
            // The hour it first moved anything, on an hourly chart.
            var firstHour = r.Hourly ? r.Bins.FirstOrDefault(b => b.Start.Date == day.Key && b.Apps.Any(a => a.App == lead.Id))?.Start : null;
            lines.Add(new(new NetHappening(NetHappeningKind.New, day.Key, firstHour ?? day.Key, firstHour is { } h ? Clock(h, hourOnly: true) : "",
                Lang.T($"{Name(lead.Id)} used the internet for the first time"))
            {
                Detail = also, Amount = Units.Data(lead.Bytes), Path = PathOf(lead.Id), IsNew = true,
            }, lead.Bytes));
        }

        // Each day newest first, its lines newest first; where a day has too many, Steam's, the games' and the new apps'
        // are kept before the busy hours, then the biggest.
        foreach (var day in lines.GroupBy(l => l.Item.Day!.Value).OrderByDescending(g => g.Key))
            list.AddRange(day.OrderBy(l => l.Item.Kind == NetHappeningKind.Busy).ThenByDescending(l => l.Bytes).Take(PerDay)
                .OrderByDescending(l => l.Item.At).ThenBy(l => l.Item.Kind).Select(l => l.Item));
        return list.Take(MaxLines).ToList();
    }

    /// <summary>The game in front most of the hours from <paramref name="from"/> to <paramref name="to"/> (hourly charts only).</summary>
    private static string? GameDuring(NetReport r, DateTime from, DateTime to) =>
        r.Hourly ? r.Bins.Where(b => b.Game is not null && b.Start < to && b.Start.AddHours(1) > from).GroupBy(b => b.Game!).MaxBy(g => g.Count())?.Key : null;

    /// <summary>What the ping did from <paramref name="from"/> to <paramref name="to"/>, in plain words; null when it wasn't checked.</summary>
    private static string? PingDuring(NetReport r, DateTime from, DateTime to)
    {
        if (r.Line is not { } line) return null;
        var bins = r.Bins.Where(b => b.Ping is not null && b.Start < to && b.Start.AddHours(r.Hourly ? 1 : 24) > from).Select(b => b.Ping!).ToList();
        if (bins.Count == 0) return null;
        int highest = bins.Max(p => p.Highest), usual = NetLine.Middle([.. bins.Select(p => p.Usual)]);
        return highest >= NetLinePing.HighFor(line.Usual) ? Lang.T($"Ping rose to {highest} ms meanwhile") : Lang.T($"Ping held at {usual} ms");
    }

    private static string Clock(DateTime t, bool hourOnly = false) => TimeText.At(t, hourOnly ? "h tt" : "h:mm tt");

    /// <summary>"6 – 9 PM", or "11 AM – 2 PM" across noon.</summary>
    private static string Span(DateTime from, DateTime to) => from.Hour < 12 == to.Hour < 12 && to.Hour != 0
        ? $"{TimeText.At(from, "%h")} – {TimeText.At(to, "h tt")}" : $"{TimeText.At(from, "h tt")} – {TimeText.At(to, "h tt")}";
}
