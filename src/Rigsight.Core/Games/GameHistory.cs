using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Games;

/// <summary>How a play ended. Only the game's own problems and the PC's (not a power cut) need a look.</summary>
public enum PlayEnd { Closed, PowerCut, ShutOff, BlueScreen, DriverReset, Crashed, Hung }

/// <summary>
/// One time a game was played: its sessions with breaks under <see cref="GameHistory.JoinGapSec"/> joined, never across
/// a shut-off, a crash or a restart. <see cref="Stats"/> is what its minutes say.
/// </summary>
public sealed record GamePlay(long Start, long End, double ActiveSec, int Parts, PlayStats Stats, PlayEnd Ending)
{
    public DateTime StartTime => TimeUtil.FromUnix(Start);
    public DateTime EndTime => TimeUtil.FromUnix(End);
    /// <summary>A power cut, or none: nothing about the game. The rest are its own or the PC's problems.</summary>
    public bool EndedBadly => Ending is not (PlayEnd.Closed or PlayEnd.PowerCut);
}

/// <summary>A game's usual while played, from its own plays at the same graphics card power (like for like).</summary>
public sealed record GameUsual(double Gpu, double Cpu, double Power, int Plays);

/// <summary>Something that changed on the PC between two plays; <see cref="After"/> is the index of the play before it.</summary>
public sealed record ChangeBetween(int After, SystemChange Change);

/// <summary>A game in the library: what RigSight saw played, what a launcher says is installed, or both.</summary>
public sealed record GameEntry
{
    public required string Name { get; init; }
    /// <summary>The game's programs RigSight has history for (usually one), the most played first.</summary>
    public IReadOnlyList<long> AppIds { get; init; } = [];
    public IReadOnlyList<string> Exes { get; init; } = [];
    public string? ExePath { get; init; }
    public InstalledGame? Installed { get; init; }
    /// <summary>Where it came from: the launcher listing it, else worked out from where it was installed.</summary>
    public Launcher? Launcher { get; init; }
    public double ActiveSec { get; init; }
    public int Sessions { get; init; }
    public DateTime? FirstPlayed { get; init; }
    public DateTime? LastPlayed { get; init; }
    /// <summary>How busy it kept the graphics card on average, for the "Hardest on your PC" order.</summary>
    public double? GpuLoad { get; init; }

    public bool IsInstalled => Installed is not null;
    public bool HasHistory => AppIds.Count > 0;
    public string Key => Installed?.Key ?? "app:" + (Exes.FirstOrDefault() ?? Name);
}

/// <summary>Everything the Games page shows for one game.</summary>
public sealed record GameStory
{
    public required GameEntry Entry { get; init; }
    public IReadOnlyList<GamePlay> Plays { get; init; } = [];
    /// <summary>The one play with frames, minute by minute, while it's the only one (empty from the second on).</summary>
    public IReadOnlyList<SystemMinute> OnlyPlayMinutes { get; init; } = [];
    public GameUsual? Usual { get; init; }
    public IReadOnlyList<ChangeBetween> Changes { get; init; } = [];
    /// <summary>Time in front over the last four weeks, oldest first, ending today.</summary>
    public IReadOnlyList<double> WeekSec { get; init; } = [0, 0, 0, 0];
    public DayOfWeek? PlayedMostOn { get; init; }
    public PlayStats All { get; init; } = new();
    /// <summary>The game's own crashes and hangs, driver resets, and shut-offs that weren't a power cut, in its plays.</summary>
    public int Crashes { get; init; }
    public int Hangs { get; init; }
    public int DriverResets { get; init; }
    public int ShutOffs { get; init; }
    public IReadOnlyList<GamePlay> PowerCuts { get; init; } = [];
    /// <summary>"No change", "12% lower"…: the frame rate since the game's last update against before, or null.</summary>
    public string? SinceUpdate { get; init; }
    public DateTime? LastUpdate { get; init; }

    public double? GpuFullShare => All.Minutes > 0 ? 100.0 * All.GpuFullMinutes / All.Minutes : null;
    public GamePlay? Longest => Plays.MaxBy(p => p.ActiveSec);
}

/// <summary>The Games library and each game's story, built from the history already recorded (see the Games page).</summary>
public static class GameHistory
{
    /// <summary>Sessions of one game closer than this are one play (a restart, a menu, a match loading).</summary>
    public const int JoinGapSec = 15 * 60;

    /// <summary>A play counted towards a game's usual: long enough to be steady.</summary>
    public const double UsualPlaySec = 15 * 60;

    /// <summary>Every game: those RigSight saw played (any launcher, or none) and those the launchers list as installed.</summary>
    public static List<GameEntry> Library(RigsightDb db, IReadOnlyList<InstalledGame> installed, RigsightSettings s)
    {
        var apps = db.LoadApps();
        var totals = db.GetSessionTotals().ToDictionary(t => t.AppId);
        var loads = db.GetAppGpuLoads();
        var byKey = new Dictionary<string, List<(AppRow App, AppTotal Total)>>();
        foreach (var app in apps)
        {
            var category = s.AppCategories.TryGetValue(app.Exe, out var c) ? c : app.Category;
            if (category != AppCategory.Game || !totals.TryGetValue(app.Id, out var total) || total.ActiveSec < 60) continue;
            var game = GameLibrary.Find(installed, app.Path);
            var key = game?.Key ?? "app:" + app.Exe;
            (byKey.TryGetValue(key, out var list) ? list : byKey[key] = []).Add((app, total));
        }

        var entries = new List<GameEntry>();
        foreach (var (key, list) in byKey)
        {
            var ordered = list.OrderByDescending(x => x.Total.ActiveSec).ToList();
            var main = ordered[0].App;
            var game = installed.FirstOrDefault(g => g.Key == key);
            double active = ordered.Sum(x => x.Total.ActiveSec);
            entries.Add(new GameEntry
            {
                Name = NameOf(main, game, s),
                AppIds = ordered.Select(x => x.App.Id).ToList(),
                Exes = ordered.Select(x => x.App.Exe).ToList(),
                ExePath = main.Path,
                Installed = game,
                Launcher = game?.Launcher ?? LauncherOfPath(main.Path),
                ActiveSec = active,
                Sessions = ordered.Sum(x => x.Total.Sessions),
                FirstPlayed = TimeUtil.FromUnix(ordered.Min(x => x.Total.First)),
                LastPlayed = TimeUtil.FromUnix(ordered.Max(x => x.Total.Last)),
                GpuLoad = loads.TryGetValue(main.Id, out var load) ? load : null,
            });
        }
        foreach (var game in installed.Where(g => !byKey.ContainsKey(g.Key)))
            entries.Add(new GameEntry { Name = game.Name, Installed = game, Launcher = game.Launcher, LastPlayed = null });
        return entries;
    }

    /// <summary>The user's name for it, else the launcher's, else what RigSight read from the exe.</summary>
    private static string NameOf(AppRow app, InstalledGame? game, RigsightSettings s) =>
        s.AppNames.TryGetValue(app.Exe, out var alias) ? alias : app.Name is { Length: > 0 } n ? n : game?.Name ?? app.Exe;

    /// <summary>The launcher a game came from, by where it was installed (for a game that's since been removed).</summary>
    public static Launcher? LauncherOfPath(string? path)
    {
        if (path is null) return null;
        var p = path.ToLowerInvariant();
        return p.Contains(@"\steamapps\") ? Games.Launcher.Steam
            : p.Contains(@"\epic games\") ? Games.Launcher.Epic
            : p.Contains(@"\riot games\") ? Games.Launcher.Riot
            : p.Contains(@"\gog galaxy\") ? Games.Launcher.Gog
            : p.Contains(@"\ubisoft game launcher\") ? Games.Launcher.Ubisoft
            : p.Contains(@"\ea games\") ? Games.Launcher.Ea
            : null;
    }

    /// <summary>One game's plays, usual, problems and the changes between its plays.</summary>
    public static GameStory Story(RigsightDb db, GameEntry entry, RigsightSettings s, DateTime now)
    {
        if (!entry.HasHistory) return new GameStory { Entry = entry };
        var sessions = entry.AppIds.SelectMany(db.GetAppSessions).OrderBy(x => x.Start).ToList();
        long first = sessions[0].Start, last = sessions.Max(x => x.End);
        var crashes = db.GetCrashes(first - 60, Math.Max(last, TimeUtil.ToUnix(now)) + 3600).Where(c => !s.IsCrashMuted(c.AppExe)).ToList();
        // Anything that stops the PC ends a play: two sessions either side of it are two plays.
        var stops = crashes.Where(c => c.Kind is CrashKind.UnexpectedShutdown or CrashKind.SystemCrash).Select(c => c.Ts).ToList();
        var spans = Join(sessions, stops);

        var mainApp = entry.AppIds[0];
        var exes = new HashSet<string>(entry.Exes, StringComparer.OrdinalIgnoreCase);
        var shutOffs = crashes.Where(c => c.Kind == CrashKind.UnexpectedShutdown).ToList();
        var verdicts = shutOffs.Count > 0 ? CrashCases.Of(db, shutOffs, s) : [];

        var plays = new List<GamePlay>();
        int gameCrashes = 0, hangs = 0, resets = 0, offs = 0;
        foreach (var (start, end, active, parts) in spans)
        {
            var ending = PlayEnd.Closed;
            foreach (var c in crashes)
            {
                // The game's own crash or hang as it ended; the PC's problems while it ran or just after.
                if (c.Kind is CrashKind.AppCrash or CrashKind.AppHang && exes.Contains(c.AppExe) && c.Ts >= start && c.Ts <= end + 120)
                    ending = c.Kind == CrashKind.AppCrash ? PlayEnd.Crashed : PlayEnd.Hung;
                else if (c.Kind == CrashKind.GpuDriverReset && c.Ts >= start && c.Ts <= end + 120) ending = PlayEnd.DriverReset;
                else if (c.Kind == CrashKind.SystemCrash && c.Ts >= start && c.Ts <= end + 600) ending = PlayEnd.BlueScreen;
                else if (c.Kind == CrashKind.UnexpectedShutdown && c.Ts >= end - 120 && c.Ts <= end + 600)
                    ending = PcUsual.LooksLikePower(c, verdicts.GetValueOrDefault(c.Id)) ? PlayEnd.PowerCut : PlayEnd.ShutOff;
            }
            switch (ending)
            {
                case PlayEnd.Crashed: gameCrashes++; break;
                case PlayEnd.Hung: hangs++; break;
                case PlayEnd.DriverReset: resets++; break;
                case PlayEnd.ShutOff or PlayEnd.BlueScreen: offs++; break;
            }
            plays.Add(new GamePlay(start, end, active, parts, db.GetPlayStats(mainApp, start, end + 60), ending));
        }

        // Changes between plays: graphics drivers, Windows and BIOS updates, hardware and fan settings, and this game's own updates.
        var gameKey = entry.Installed is { } inst ? Inventory.Game + ":" + inst.Key : null;
        var changes = new List<ChangeBetween>();
        foreach (var c in db.GetChanges(first, TimeUtil.ToUnix(now) + 60))
        {
            bool relevant = c.IsGraphicsDriver || c.Kind is ChangeKind.WindowsUpdate or ChangeKind.Windows or ChangeKind.Firmware or ChangeKind.Hardware or ChangeKind.Fans
                || c.Kind == ChangeKind.GameUpdated && gameKey is not null && string.Equals(c.Subject, gameKey, StringComparison.OrdinalIgnoreCase);
            if (!relevant) continue;
            long ts = TimeUtil.ToUnix(c.Time);
            int before = plays.FindLastIndex(p => p.Start <= ts);
            if (before >= 0 && before < plays.Count - 1 || before == plays.Count - 1 && c.Kind == ChangeKind.GameUpdated) changes.Add(new ChangeBetween(before, c));
        }

        var weeks = new double[4];
        var today = now.Date.AddDays(1);
        for (int w = 0; w < 4; w++)
        {
            long from = TimeUtil.ToUnix(today.AddDays(-7 * (4 - w))), to = TimeUtil.ToUnix(today.AddDays(-7 * (3 - w)));
            weeks[w] = entry.AppIds.Sum(id => db.GetAppFrontSec(id, from, to));
        }
        var lastUpdate = changes.Where(c => c.Change.Kind == ChangeKind.GameUpdated).Select(c => (DateTime?)c.Change.Time).LastOrDefault();

        // While one play alone has frames, its minutes stand in for the play-by-play chart (one dot draws nothing).
        var framed = plays.Where(p => p.Stats.Fps is not null).ToList();
        var onlyPlayMinutes = framed.Count == 1 ? db.GetMinutes(framed[0].Start, framed[0].End + 60).Where(m => m.Fps is not null).ToList() : [];
        return new GameStory
        {
            Entry = entry,
            Plays = plays,
            OnlyPlayMinutes = onlyPlayMinutes,
            Usual = UsualOf(plays),
            Changes = changes,
            WeekSec = weeks,
            PlayedMostOn = plays.Count == 0 ? null : plays.GroupBy(p => p.StartTime.DayOfWeek).MaxBy(g => g.Sum(p => p.ActiveSec))!.Key,
            All = Sum(plays.Select(p => p.Stats)),
            Crashes = gameCrashes, Hangs = hangs, DriverResets = resets, ShutOffs = offs,
            PowerCuts = plays.Where(p => p.Ending == PlayEnd.PowerCut).ToList(),
            LastUpdate = lastUpdate ?? entry.Installed?.Updated,
            SinceUpdate = SinceUpdate(plays, lastUpdate ?? entry.Installed?.Updated),
        };
    }

    /// <summary>
    /// Sessions joined into plays: a break under <see cref="JoinGapSec"/> joins two, a stop of the PC between them
    /// (<paramref name="stops"/>) never does.
    /// </summary>
    internal static List<(long Start, long End, double Active, int Parts)> Join(IReadOnlyList<SessionRow> sessions, IReadOnlyList<long> stops)
    {
        var plays = new List<(long Start, long End, double Active, int Parts)>();
        foreach (var x in sessions.OrderBy(x => x.Start))
        {
            if (plays.Count > 0)
            {
                var p = plays[^1];
                bool stopBetween = stops.Any(t => t >= p.End - 120 && t <= x.Start);
                if (x.Start - p.End < JoinGapSec && !stopBetween)
                {
                    plays[^1] = (p.Start, Math.Max(p.End, x.End), p.Active + x.ActiveSec, p.Parts + 1);
                    continue;
                }
            }
            plays.Add((x.Start, x.End, x.ActiveSec, 1));
        }
        return plays;
    }

    /// <summary>
    /// The game's usual: its plays of a quarter of an hour or more, at its usual graphics card power give or take 15%
    /// (a lighter scene or a frame cap isn't compared with full load). Three such plays at least, or none.
    /// </summary>
    internal static GameUsual? UsualOf(IReadOnlyList<GamePlay> plays)
    {
        var full = plays.Where(p => p.ActiveSec >= UsualPlaySec && p.Stats.Gpu is not null && p.Stats.GpuPower is > 0).ToList();
        if (full.Count < 3) return null;
        double power = Median(full.Select(p => p.Stats.GpuPower!.Value));
        var band = full.Where(p => Math.Abs(p.Stats.GpuPower!.Value - power) / power <= 0.15).ToList();
        if (band.Count < 3) return null;
        return new GameUsual(Median(band.Select(p => p.Stats.Gpu!.Value)), Median(band.Where(p => p.Stats.Cpu is not null).Select(p => p.Stats.Cpu!.Value).DefaultIfEmpty()),
            power, band.Count);
    }

    /// <summary>Whether a play counts towards and can be set against the usual (long enough, at the usual power).</summary>
    public static bool LikeUsual(GamePlay p, GameUsual? u) =>
        u is not null && p.ActiveSec >= UsualPlaySec && p.Stats.GpuPower is double w && Math.Abs(w - u.Power) / u.Power <= 0.15 && p.Stats.Gpu is not null;

    /// <summary>
    /// The frame rate since the last update against before it: up to five plays with frames each side, two at least.
    /// Within 5% is no change.
    /// </summary>
    internal static string? SinceUpdate(IReadOnlyList<GamePlay> plays, DateTime? update)
    {
        if (update is not { } u) return null;
        long t = TimeUtil.ToUnix(u);
        var before = plays.Where(p => p.End <= t && p.Stats.Fps is not null).TakeLast(5).Select(p => p.Stats.Fps!.Value).ToList();
        var after = plays.Where(p => p.Start >= t && p.Stats.Fps is not null).Take(5).Select(p => p.Stats.Fps!.Value).ToList();
        if (before.Count < 2 || after.Count < 2) return null;
        double change = (Median(after) - Median(before)) / Median(before) * 100;
        double by = Math.Abs(change);
        return by < 5 ? Lang.T("No change") : change < 0 ? Lang.T($"{by:0}% lower") : Lang.T($"{by:0}% higher");
    }

    private static PlayStats Sum(IEnumerable<PlayStats> all)
    {
        var list = all.Where(p => p.Minutes > 0).ToList();
        if (list.Count == 0) return new PlayStats();
        double? W(Func<PlayStats, double?> f, Func<PlayStats, int>? weight = null)
        {
            weight ??= p => p.Minutes;
            double sum = 0, n = 0;
            foreach (var p in list) if (f(p) is double v) { sum += v * weight(p); n += weight(p); }
            return n > 0 ? sum / n : null;
        }
        return new PlayStats
        {
            Minutes = list.Sum(p => p.Minutes),
            Gpu = W(p => p.Gpu), Cpu = W(p => p.Cpu), GpuLoad = W(p => p.GpuLoad), GpuPower = W(p => p.GpuPower), Power = W(p => p.Power),
            GpuMax = list.Max(p => p.GpuMax), CpuMax = list.Max(p => p.CpuMax), HotMax = list.Max(p => p.HotMax),
            GpuFullMinutes = list.Sum(p => p.GpuFullMinutes),
            VramMax = list.Max(p => p.VramMax), VramTotal = list.Max(p => p.VramTotal),
            Fps = W(p => p.Fps, p => p.FpsMinutes), FpsLow = W(p => p.FpsLow, p => p.FpsMinutes), FpsMinutes = list.Sum(p => p.FpsMinutes),
        };
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return 0;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }
}
