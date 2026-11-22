using System.Data;

namespace Rigsight.Core.Data;

/// <summary>An app's time over all its sessions, for the Games library (see <see cref="RigsightDb.GetSessionTotals"/>).</summary>
public sealed record AppTotal(long AppId, int Sessions, double ActiveSec, long First, long Last);

/// <summary>
/// What the minutes say about a stretch of time with one app in front: temperatures, how hard the graphics card
/// worked, power, its memory and the frame rate (see <see cref="RigsightDb.GetPlayStats"/>).
/// </summary>
public sealed record PlayStats
{
    public int Minutes { get; init; }
    public double? Gpu { get; init; }
    public double? GpuMax { get; init; }
    public double? Cpu { get; init; }
    public double? CpuMax { get; init; }
    public double? HotMax { get; init; }
    public double? GpuLoad { get; init; }
    public double? GpuPower { get; init; }
    /// <summary>The CPU's and the graphics card's power together.</summary>
    public double? Power { get; init; }
    /// <summary>Minutes with the graphics card at 95% or more: it set the pace.</summary>
    public int GpuFullMinutes { get; init; }
    public double? VramMax { get; init; }
    public double? VramTotal { get; init; }
    /// <summary>
    /// The game's own video memory at most (MB), recorded since 0.31.2. Where it's null, <see cref="VramMax"/> is all
    /// the card had in use, every app together: older plays only have that.
    /// </summary>
    public double? VramOwnMax { get; init; }
    public double? Fps { get; init; }
    public double? FpsLow { get; init; }
    public int FpsMinutes { get; init; }

    /// <summary>
    /// The game's own RAM (MB of private working set, what Task Manager shows for it) over the hours the play touched, on
    /// average and at most: kept per hour, so a play shares its hours' figures with anything else the game did in them.
    /// </summary>
    public double? MemAvg { get; init; }
    public double? MemMax { get; init; }
    /// <summary>The most RAM in use on the whole PC (GB, every app together) in a minute with the game in front.</summary>
    public double? RamUsedMax { get; init; }

    /// <summary>
    /// From every frame (recorded since 2026-10-25, see <see cref="Games.FrameSpread"/>): the true 0.1% low, how many
    /// stutters, and the minutes that had frames. Where there are such minutes, <see cref="Fps"/> and <see cref="FpsLow"/>
    /// come from the frames too; older plays keep RivaTuner's minute averages.
    /// </summary>
    public double? FpsLow01 { get; init; }
    public int? Stutters { get; init; }
    public int FrameMinutes { get; init; }
    /// <summary>The worst stutter: how long its frame took and when (Unix ms).</summary>
    public Games.Stutter? Worst { get; init; }

    /// <summary>Stutters an hour of play with frames, or null without frames.</summary>
    public double? StuttersAnHour => Stutters is int n && FrameMinutes > 0 ? n * 60.0 / FrameMinutes : null;
}

public sealed partial class RigsightDb
{
    private bool? _hasFps;
    private bool HasFps => _hasFps ??= HasColumn("system_minute", "fps");

    /// <summary>Every app's sessions added up since <paramref name="from"/>: how many, the time in front, the first and last.</summary>
    public List<AppTotal> GetSessionTotals(long from = 0)
    {
        using var cmd = Cmd("""
            SELECT app_id, COUNT(*), SUM(active_sec), min(start), max(end) FROM sessions INDEXED BY ix_sessions_start
            WHERE start >= $from GROUP BY app_id
            """, ("$from", from));
        using var r = cmd.ExecuteReader();
        var list = new List<AppTotal>();
        while (r.Read()) list.Add(new AppTotal(r.GetInt64(0), r.GetInt32(1), r.GetDouble(2), r.GetInt64(3), r.GetInt64(4)));
        return list;
    }

    /// <summary>Every game's sessions added up since <paramref name="from"/> (a game: a session played as one), as <see cref="GetSessionTotals"/>.</summary>
    public List<AppTotal> GetGameTotals(long from = 0)
    {
        using var cmd = Cmd("""
            SELECT app_id, COUNT(*), SUM(active_sec), min(start), max(end) FROM sessions
            WHERE start >= $from AND is_game = 1 GROUP BY app_id
            """, ("$from", from));
        using var r = cmd.ExecuteReader();
        var list = new List<AppTotal>();
        while (r.Read()) list.Add(new AppTotal(r.GetInt64(0), r.GetInt32(1), r.GetDouble(2), r.GetInt64(3), r.GetInt64(4)));
        return list;
    }

    /// <summary>All of one app's sessions, oldest first.</summary>
    public List<SessionRow> GetAppSessions(long appId)
    {
        using var cmd = Cmd("""
            SELECT id, app_id, start, end, active_sec, cpu_temp_max, gpu_temp_max, is_game FROM sessions
            WHERE app_id = $app AND start >= 0 ORDER BY start
            """, ("$app", appId));
        return ReadSessions(cmd);
    }

    /// <summary>The minutes from <paramref name="from"/> to <paramref name="to"/> with <paramref name="appId"/> in front, summed up.</summary>
    public PlayStats GetPlayStats(long appId, long from, long to)
    {
        // Minutes are stored at their start: a play that began at 9:18:06 PM owns the 9:18 minute too (its 9 stutters
        // were left out of the play while all plays together counted them, real PC 26 Oct 2026).
        from -= from % 60;
        string vram = HasVram ? "max(vram_used), max(vram_total)" : "NULL, NULL";
        string fps = HasFps ? "AVG(fps), AVG(fps_low), COUNT(fps)" : "NULL, NULL, 0";
        // Windows' per-app figure isn't held to the card's size: it counts memory the game only refers to, and once read
        // 146 GB for Crimson Desert on a 12 GB card with 7.3 GB in use (real PC, 28 Oct 2026). One minute above the whole
        // card means it can't be trusted for this play, so the play says the card's figure instead, as before 0.31.2.
        string own = !HasFgVram ? "NULL" : HasVram ? "CASE WHEN max(fg_vram > vram_total) = 1 THEN NULL ELSE max(fg_vram) END" : "max(fg_vram)";
        using var cmd = Cmd($"""
            SELECT COUNT(*), AVG(gpu_temp), max(gpu_temp_max), AVG(cpu_temp), max(cpu_temp_max), max(gpu_hot_max), AVG(gpu_load),
                AVG(gpu_power), AVG(CASE WHEN cpu_power IS NULL AND gpu_power IS NULL THEN NULL ELSE coalesce(cpu_power, 0) + coalesce(gpu_power, 0) END),
                SUM(CASE WHEN gpu_load >= 95 THEN 1 ELSE 0 END), {vram}, {fps}, {own}, max(ram_used)
            FROM system_minute WHERE ts >= $from AND ts < $to AND fg_app = $app
            """, ("$from", from), ("$to", to), ("$app", appId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new PlayStats();
        var stats = new PlayStats
        {
            Minutes = r.GetInt32(0), Gpu = D(r, 1), GpuMax = D(r, 2), Cpu = D(r, 3), CpuMax = D(r, 4), HotMax = D(r, 5),
            GpuLoad = D(r, 6), GpuPower = D(r, 7), Power = D(r, 8), GpuFullMinutes = r.IsDBNull(9) ? 0 : r.GetInt32(9),
            VramMax = D(r, 10), VramTotal = D(r, 11), Fps = D(r, 12), FpsLow = D(r, 13), FpsMinutes = r.IsDBNull(14) ? 0 : r.GetInt32(14),
            VramOwnMax = D(r, 15), RamUsedMax = D(r, 16),
        };
        r.Close();
        var (memAvg, memMax) = GetAppMemory(appId, from, to);
        stats = stats with { MemAvg = memAvg, MemMax = memMax };
        // Every frame, where it was recorded: the true lows and the stutters.
        var (spread, minutes, stutters) = GetFrameSpread(appId, from, to);
        if (minutes == 0 || spread.IsEmpty) return stats;
        var worst = GetStutters(appId, from * 1000, to * 1000).MaxBy(s => s.Ms);
        return stats with
        {
            Fps = spread.Fps ?? stats.Fps, FpsLow = spread.Low1 ?? stats.FpsLow, FpsLow01 = spread.Low01,
            Stutters = stutters, FrameMinutes = minutes, Worst = worst,
        };
    }

    // ── Every frame (see Games.FrameSpread) ──

    private bool? _hasFrames;
    private bool HasFrames => _hasFrames ??= HasTable("frame_minute");

    // Where each minute's frames came from (2026-11-22): RivaTuner's (0, and every row from before) or RigSight's own
    // reading of the game's presents (1), for players without RivaTuner.
    private void EnsureFrameSourceSchema()
    {
        if (!HasColumn("frame_minute", "src")) Exec("ALTER TABLE frame_minute ADD COLUMN src INTEGER");
    }

    private bool? _hasFrameSource;
    private bool HasFrameSource => _hasFrameSource ??= HasFrames && HasColumn("frame_minute", "src");

    /// <summary>A minute of a game's frames: their spread, how many stutters, and where they came from.</summary>
    public void WriteFrameMinute(long ts, long app, Games.FrameSpread spread, IReadOnlyList<Games.Stutter> stutters, Games.FrameSource src = Games.FrameSource.RivaTuner)
    {
        if (spread.IsEmpty) return;
        using (var cmd = HasFrameSource
                   ? Cmd("INSERT OR REPLACE INTO frame_minute(ts, app, frames, stutters, spread, src) VALUES($ts, $app, $frames, $stutters, $spread, $src)",
                       ("$ts", ts), ("$app", app), ("$frames", spread.Frames), ("$stutters", stutters.Count), ("$spread", spread.ToBytes()), ("$src", (int)src))
                   : Cmd("INSERT OR REPLACE INTO frame_minute(ts, app, frames, stutters, spread) VALUES($ts, $app, $frames, $stutters, $spread)",
                       ("$ts", ts), ("$app", app), ("$frames", spread.Frames), ("$stutters", stutters.Count), ("$spread", spread.ToBytes())))
            cmd.ExecuteNonQuery();
        foreach (var s in stutters)
        {
            // The driver whose long bursts fell inside the frame, when there was one (driver activity, issue #15).
            using var cmd = s.Drv is { Length: > 0 } && HasStutterDrivers
                ? Cmd("INSERT OR REPLACE INTO stutter(ts_ms, app, ms, usual_ms, drv, drv_us) VALUES($ts, $app, $ms, $usual, $drv, $us)",
                    ("$ts", s.AtMs), ("$app", app), ("$ms", Math.Round(s.Ms, 1)), ("$usual", Math.Round(s.UsualMs, 1)), ("$drv", s.Drv), ("$us", s.DrvUs))
                : Cmd("INSERT OR REPLACE INTO stutter(ts_ms, app, ms, usual_ms) VALUES($ts, $app, $ms, $usual)",
                    ("$ts", s.AtMs), ("$app", app), ("$ms", Math.Round(s.Ms, 1)), ("$usual", Math.Round(s.UsualMs, 1)));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>A game's frames from <paramref name="from"/> to <paramref name="to"/> (Unix seconds) added up: their spread, the minutes with frames, the stutters.</summary>
    public (Games.FrameSpread Spread, int Minutes, int Stutters) GetFrameSpread(long appId, long from, long to)
    {
        var all = new Games.FrameSpread();
        if (!HasFrames) return (all, 0, 0);
        using var cmd = Cmd("SELECT spread, stutters FROM frame_minute WHERE app = $app AND ts >= $from AND ts < $to", ("$app", appId), ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        int minutes = 0, stutters = 0;
        while (r.Read())
        {
            all.Add(Games.FrameSpread.FromBytes((byte[])r.GetValue(0)));
            stutters += r.GetInt32(1);
            minutes++;
        }
        return (all, minutes, stutters);
    }

    /// <summary>A game's frames minute by minute: each minute's true average, 1% low and stutters (a play's chart).</summary>
    public List<(long Ts, double? Fps, double? Low, int Stutters)> GetFrameMinutes(long appId, long from, long to)
    {
        var list = new List<(long, double?, double?, int)>();
        if (!HasFrames) return list;
        using var cmd = Cmd("SELECT ts, spread, stutters FROM frame_minute WHERE app = $app AND ts >= $from AND ts < $to ORDER BY ts", ("$app", appId), ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var spread = Games.FrameSpread.FromBytes((byte[])r.GetValue(1));
            list.Add((r.GetInt64(0), spread.Fps, spread.Low1, r.GetInt32(2)));
        }
        return list;
    }

    /// <summary>Where each minute of a game's frames came from, oldest first (a row from before the column: RivaTuner).</summary>
    public List<(long Ts, Games.FrameSource Src)> GetFrameSources(long appId, long from, long to)
    {
        var list = new List<(long, Games.FrameSource)>();
        if (!HasFrames) return list;
        using var cmd = Cmd(HasFrameSource
            ? "SELECT ts, src FROM frame_minute WHERE app = $app AND ts >= $from AND ts < $to ORDER BY ts"
            : "SELECT ts, NULL FROM frame_minute WHERE app = $app AND ts >= $from AND ts < $to ORDER BY ts", ("$app", appId), ("$from", from), ("$to", to));
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.IsDBNull(1) ? Games.FrameSource.RivaTuner : (Games.FrameSource)r.GetInt32(1)));
        return list;
    }

    /// <summary>How many minutes of a game's frames came from each source.</summary>
    public (int RivaTuner, int Own) CountFrameSources(long appId, long from, long to)
    {
        var all = GetFrameSources(appId, from, to);
        int own = all.Count(x => x.Src == Games.FrameSource.Own);
        return (all.Count - own, own);
    }

    /// <summary>A game's stutters from <paramref name="fromMs"/> to <paramref name="toMs"/> (Unix ms), oldest first.</summary>
    public List<Games.Stutter> GetStutters(long appId, long fromMs, long toMs)
    {
        var list = new List<Games.Stutter>();
        if (!HasFrames) return list;
        bool drivers = HasStutterDrivers;
        using var cmd = Cmd(drivers
            ? "SELECT ts_ms, ms, usual_ms, drv, drv_us FROM stutter WHERE app = $app AND ts_ms >= $from AND ts_ms < $to ORDER BY ts_ms"
            : "SELECT ts_ms, ms, usual_ms FROM stutter WHERE app = $app AND ts_ms >= $from AND ts_ms < $to ORDER BY ts_ms", ("$app", appId), ("$from", fromMs), ("$to", toMs));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(drivers && !r.IsDBNull(3)
                ? new Games.Stutter(r.GetInt64(0), r.GetDouble(1), r.GetDouble(2), r.GetString(3), r.IsDBNull(4) ? 0 : r.GetInt32(4))
                : new Games.Stutter(r.GetInt64(0), r.GetDouble(1), r.GetDouble(2)));
        return list;
    }

    /// <summary>
    /// How busy each app kept the graphics card while in front, on average over all its months: the Games library's
    /// "Hardest on your PC" order, read from the monthly totals so it costs nothing with years of history.
    /// </summary>
    public Dictionary<long, double> GetAppGpuLoads()
    {
        var map = new Dictionary<long, double>();
        if (!HasAppMonth()) return map;
        using var cmd = Cmd("""
            SELECT app_id, SUM(gpu_load_sum) / SUM(gpu_load_n) FROM app_month
            WHERE month >= 0 GROUP BY app_id HAVING SUM(gpu_load_n) > 0
            """);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = r.GetDouble(1);
        return map;
    }

    /// <summary>Whether any game was played from <paramref name="from"/> on (unix seconds).</summary>
    public bool PlayedGameSince(long from)
    {
        using var cmd = Cmd("SELECT EXISTS(SELECT 1 FROM sessions WHERE is_game = 1 AND start >= $from)", ("$from", from));
        return cmd.ExecuteScalar() is long v && v != 0;
    }

    /// <summary>
    /// An app's own RAM over the hours that [<paramref name="from"/>, <paramref name="to"/>) touched (MB of private working
    /// set): its average and its highest. An hour starts within the hour before <paramref name="from"/> at the earliest, so
    /// the hour a play began in counts. Nulls with nothing recorded.
    /// </summary>
    public (double? Avg, double? Peak) GetAppMemory(long appId, long from, long to)
    {
        using var cmd = Cmd("SELECT total(mem_sum), total(mem_n), max(mem_max) FROM app_hour WHERE ts > $from AND ts < $to AND app_id = $app",
            ("$from", from - 3600), ("$to", to), ("$app", appId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null);
        double sum = r.GetDouble(0), n = r.GetDouble(1);
        return (n > 0 ? sum / n : null, D(r, 2));
    }

    /// <summary>The PC's installed RAM in GB, as the inventory last read it ("32 GB DDR5-6000"); null when not read yet.</summary>
    public double? GetRamTotalGb() =>
        GetInventoryValue(Stability.Inventory.Ram) is { } installed
        && double.TryParse(installed.Split(' ')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb) && gb > 0 ? gb : null;

    /// <summary>An app's time in front, in seconds, from <paramref name="from"/> to <paramref name="to"/> (by its hours).</summary>
    public double GetAppFrontSec(long appId, long from, long to)
    {
        using var cmd = Cmd("SELECT SUM(fg_sec) FROM app_hour WHERE ts >= $from AND ts < $to AND app_id = $app", ("$from", from), ("$to", to), ("$app", appId));
        return cmd.ExecuteScalar() is double v ? v : 0;
    }
}
