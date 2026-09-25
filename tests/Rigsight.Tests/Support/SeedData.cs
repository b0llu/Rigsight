using Microsoft.Data.Sqlite;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.Support;

/// <summary>How much history to generate.</summary>
public sealed record SeedProfile(string Name, int Days, int Apps, double OnChance, double MinHours, double MaxHours, double CrashesPerDay, int Seed)
{
    /// <summary>Two weeks: enough for every page to have something, fast to make.</summary>
    public static readonly SeedProfile Small = new("small", 14, 25, 0.9, 3, 8, 0.5, 1);

    /// <summary>A year of an ordinary PC.</summary>
    public static readonly SeedProfile Typical = new("typical", 365, 120, 0.85, 4, 10, 1.0, 42);

    /// <summary>Two years of a PC that's on all day with hundreds of apps: the worst case pages must stay fast for.</summary>
    public static readonly SeedProfile Heavy = new("heavy", 730, 300, 1.0, 10, 18, 3.0, 7);
}

/// <summary>
/// Writes synthetic history shaped like what the agent records: every table, with the rollups (app_month,
/// system_day) built by Rigsight's own code, so it can never drift from the real schema. Deterministic for a profile
/// (same seed, same data relative to today).
/// </summary>
public static class SeedData
{
    /// <summary>Apps the generated history always includes, so tests can look them up by name.</summary>
    public static readonly (string Exe, string Name, AppCategory Category)[] KnownApps =
    [
        ("cyberpunk2077.exe", "Cyberpunk 2077", AppCategory.Game),
        ("dota2.exe", "Dota 2", AppCategory.Game),
        ("chrome.exe", "Google Chrome", AppCategory.Browser),
        ("code.exe", "Visual Studio Code", AppCategory.Development),
        ("discord.exe", "Discord", AppCategory.Communication),
        ("spotify.exe", "Spotify", AppCategory.Media),
        ("steam.exe", "Steam", AppCategory.Launcher),
        ("explorer.exe", "File Explorer", AppCategory.System),
    ];

    public static readonly string[] Drives = [@"C:\", @"D:\"];

    public static void Generate(string dbPath, SeedProfile p, DateTime? now = null)
    {
        var end = now ?? DateTime.Now;
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);

        // The schema, exactly as the agent makes it.
        using (RigsightDb.OpenWriter(dbPath)) { }

        var rnd = new Random(p.Seed);
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var tx = conn.BeginTransaction();

        // ── Apps ──
        var categories = new[] { AppCategory.Game, AppCategory.Game, AppCategory.Game, AppCategory.Browser, AppCategory.Development,
            AppCategory.Development, AppCategory.Communication, AppCategory.Media, AppCategory.Launcher, AppCategory.Productivity,
            AppCategory.Productivity, AppCategory.System, AppCategory.System, AppCategory.Other };
        var apps = new List<(long Id, string Exe, AppCategory Category)>();
        var start = end.Date.AddDays(-p.Days);
        using (var cmd = Cmd(conn, tx, "INSERT INTO apps(id, exe, name, path, category, first_seen) VALUES($id, $exe, $name, $path, $cat, $fs)"))
        {
            for (int i = 0; i < Math.Max(p.Apps, KnownApps.Length); i++)
            {
                var (exe, name, cat) = i < KnownApps.Length ? KnownApps[i] : ($"app{i:000}.exe", $"Synthetic App {i}", categories[rnd.Next(categories.Length)]);
                long id = i + 1;
                Set(cmd, ("$id", id), ("$exe", exe), ("$name", name), ("$path", $@"C:\Program Files\Synthetic\{exe}"), ("$cat", cat.ToString()),
                    ("$fs", TimeUtil.ToUnix(start)));
                cmd.ExecuteNonQuery();
                apps.Add((id, exe, cat));
            }
        }
        // Popularity falls off like real use: the first apps get most of the time.
        double[] weights = [.. apps.Select((_, i) => 1 / Math.Pow(i + 1, 1.1))];
        double totalWeight = weights.Sum();
        (long Id, string Exe, AppCategory Category) Pick()
        {
            double x = rnd.NextDouble() * totalWeight;
            for (int i = 0; i < apps.Count; i++) if ((x -= weights[i]) <= 0) return apps[i];
            return apps[^1];
        }

        using var minute = Cmd(conn, tx, """
            INSERT OR REPLACE INTO system_minute(ts, cpu_temp, cpu_temp_max, gpu_temp, gpu_temp_max, gpu_hot_max, gpu_mem_max, cpu_load, gpu_load,
                cpu_power, gpu_power, cpu_volt_max, gpu_volt_max, ram_used, fg_app, active_sec, idle_sec)
            VALUES($ts, $ct, $ctm, $gt, $gtm, $gh, $gm, $cl, $gl, $cp, $gp, $cv, $gv, $ram, $fg, $act, $idle)
            """);
        using var session = Cmd(conn, tx, """
            INSERT INTO sessions(app_id, start, end, active_sec, cpu_temp_max, gpu_temp_max, is_game)
            VALUES($app, $start, $end, $act, $ct, $gt, $game)
            """);
        var hours = new Dictionary<(long Ts, long App), AppHour>();

        for (var day = start; day <= end.Date; day = day.AddDays(1))
        {
            bool isToday = day == end.Date;
            // Yesterday always has history (Home and the recap show it).
            if (!isToday && day != end.Date.AddDays(-1) && rnd.NextDouble() > p.OnChance) continue;
            double length = p.MinHours + rnd.NextDouble() * (p.MaxHours - p.MinHours);
            var from = day.AddHours(7 + rnd.NextDouble() * Math.Max(0.5, 24 - 7 - length));
            var to = from.AddHours(length);
            if (to > day.AddDays(1)) to = day.AddDays(1);
            if (isToday)
            {
                // Today: the PC has been on for the last few hours, up to the current minute.
                from = end.AddHours(-Math.Min(6, (end - day).TotalHours * 0.9));
                to = end;
            }
            var gamingFrom = from + (to - from) * (0.4 + rnd.NextDouble() * 0.3);

            // Foreground blocks: an app in front for a while, then the next.
            var t = TimeUtil.LocalMinuteStart(from);
            while (t < to)
            {
                var app = t >= gamingFrom && rnd.NextDouble() < 0.6 ? apps[rnd.Next(2)] : Pick();
                bool game = app.Category == AppCategory.Game;
                int blockMinutes = game ? rnd.Next(20, 180) : rnd.Next(3, 60);
                var blockStart = t;
                double activeTotal = 0, cpuPeak = 0, gpuPeak = 0;
                for (int m = 0; m < blockMinutes && t < to; m++, t = t.AddMinutes(1))
                {
                    long ts = TimeUtil.ToUnix(t);
                    bool away = !game && rnd.NextDouble() < 0.08;
                    int active = away ? 0 : 60, idle = 60 - active;
                    double load = game ? 40 + rnd.NextDouble() * 35 : 3 + rnd.NextDouble() * 20;
                    double gpuLoad = game ? 85 + rnd.NextDouble() * 14 : rnd.NextDouble() * 12;
                    double cpuTemp = 38 + load * 0.45 + rnd.NextDouble() * 4;
                    double gpuTemp = 32 + gpuLoad * 0.42 + rnd.NextDouble() * 3;
                    // Now and then a hot spell, so alerts, peaks and "hotter than usual" have something to find.
                    if (game && rnd.NextDouble() < 0.01) { cpuTemp += 18; gpuTemp += 14; }
                    cpuPeak = Math.Max(cpuPeak, cpuTemp + 3);
                    gpuPeak = Math.Max(gpuPeak, gpuTemp + 2);
                    activeTotal += active;

                    Set(minute, ("$ts", ts), ("$ct", R(cpuTemp)), ("$ctm", R(cpuTemp + 3)), ("$gt", R(gpuTemp)), ("$gtm", R(gpuTemp + 2)),
                        ("$gh", R(gpuTemp + 12)), ("$gm", R(gpuTemp + 16)), ("$cl", R(load)), ("$gl", R(gpuLoad)), ("$cp", R(20 + load * 1.1)),
                        ("$gp", R(30 + gpuLoad * 2.8)), ("$cv", 1.1 + load / 400), ("$gv", 0.75 + gpuLoad / 500), ("$ram", R(9 + load / 10)),
                        ("$fg", away ? null : app.Id), ("$act", active), ("$idle", idle));
                    minute.ExecuteNonQuery();

                    var hour = Hour(hours, TimeUtil.ToUnix(TimeUtil.LocalHourStart(t)), app.Id);
                    hour.FgSec += active;
                    hour.IdleSec += idle;
                    hour.CpuSum += load / 4; hour.CpuN++; hour.CpuMax = Math.Max(hour.CpuMax ?? 0, load / 3);
                    double mem = game ? 6000 + rnd.NextDouble() * 3000 : 200 + rnd.NextDouble() * 1500;
                    hour.MemSum += mem; hour.MemN++; hour.MemMax = Math.Max(hour.MemMax ?? 0, mem);
                    hour.CpuTempSum += cpuTemp; hour.CpuTempN++; hour.CpuTempMax = Math.Max(hour.CpuTempMax ?? 0, cpuTemp + 3);
                    hour.GpuTempSum += gpuTemp; hour.GpuTempN++; hour.GpuTempMax = Math.Max(hour.GpuTempMax ?? 0, gpuTemp + 2);
                    hour.GpuHotMax = Math.Max(hour.GpuHotMax ?? 0, gpuTemp + 12);
                    hour.GpuLoadSum += gpuLoad; hour.GpuLoadN++;

                    // Something in the background (chat, music) each minute.
                    var bg = apps[4 + rnd.Next(3)];
                    if (bg.Id != app.Id)
                    {
                        var bh = Hour(hours, TimeUtil.ToUnix(TimeUtil.LocalHourStart(t)), bg.Id);
                        bh.BgSec += 60; bh.CpuSum += 1; bh.CpuN++; bh.MemSum += 400; bh.MemN++; bh.MemMax = Math.Max(bh.MemMax ?? 0, 420);
                    }
                }
                if (activeTotal >= 60)
                {
                    Set(session, ("$app", app.Id), ("$start", TimeUtil.ToUnix(blockStart)), ("$end", TimeUtil.ToUnix(t)), ("$act", activeTotal),
                        ("$ct", R(cpuPeak)), ("$gt", R(gpuPeak)), ("$game", game ? 1 : 0));
                    session.ExecuteNonQuery();
                }
            }
        }

        using (var hourCmd = Cmd(conn, tx, """
            INSERT INTO app_hour(ts, app_id, fg_sec, idle_sec, bg_sec, min_sec, cpu_sum, cpu_n, cpu_max, mem_sum, mem_n, mem_max,
                cpu_temp_sum, cpu_temp_n, cpu_temp_max, gpu_temp_sum, gpu_temp_n, gpu_temp_max, gpu_hot_max,
                cpu_power_max, gpu_power_max, cpu_volt_max, gpu_volt_max, gpu_load_sum, gpu_load_n)
            VALUES($ts, $app, $fg, $idle, $bg, 0, $cs, $cn, $cm, $ms, $mn, $mm, $cts, $ctn, $ctm, $gts, $gtn, $gtm, $ghm, NULL, NULL, NULL, NULL, $gls, $gln)
            """))
        {
            foreach (var h in hours.Values)
            {
                Set(hourCmd, ("$ts", h.Ts), ("$app", h.AppId), ("$fg", h.FgSec), ("$idle", h.IdleSec), ("$bg", h.BgSec), ("$cs", h.CpuSum),
                    ("$cn", h.CpuN), ("$cm", h.CpuMax), ("$ms", h.MemSum), ("$mn", h.MemN), ("$mm", h.MemMax), ("$cts", h.CpuTempSum),
                    ("$ctn", h.CpuTempN), ("$ctm", h.CpuTempMax), ("$gts", h.GpuTempSum), ("$gtn", h.GpuTempN), ("$gtm", h.GpuTempMax),
                    ("$ghm", h.GpuHotMax), ("$gls", h.GpuLoadSum), ("$gln", h.GpuLoadN));
                hourCmd.ExecuteNonQuery();
            }
        }

        // ── Crashes, of every kind ──
        var kinds = new[] { CrashKind.AppCrash, CrashKind.AppCrash, CrashKind.AppCrash, CrashKind.AppHang, CrashKind.AppHang,
            CrashKind.GpuDriverReset, CrashKind.SystemCrash, CrashKind.UnexpectedShutdown };
        using (var crash = Cmd(conn, tx, """
            INSERT OR IGNORE INTO crashes(ts, kind, app_exe, app_path, module, code, detail, during_sleep)
            VALUES($ts, $kind, $exe, $path, $module, $code, $detail, $sleep)
            """))
        {
            int count = (int)(p.Days * p.CrashesPerDay);
            long span = TimeUtil.ToUnix(end) - TimeUtil.ToUnix(start);
            for (int i = 0; i < count; i++)
            {
                var kind = kinds[rnd.Next(kinds.Length)];
                long ts = TimeUtil.ToUnix(start) + (long)(rnd.NextDouble() * span);
                var app = kind is CrashKind.AppCrash or CrashKind.AppHang ? Pick() : default;
                var (module, code, detail) = kind switch
                {
                    CrashKind.AppCrash => (rnd.Next(3) switch { 0 => "nvwgf2umx.dll", 1 => "ntdll.dll", _ => "KERNELBASE.dll" }, "0xc0000005", null),
                    CrashKind.AppHang => ((string?)null, (string?)null, (string?)null),
                    CrashKind.GpuDriverReset => ("nvlddmkm", null, "Display driver nvlddmkm stopped responding and has successfully recovered."),
                    CrashKind.SystemCrash => (null, "0x00000124", "WHEA_UNCORRECTABLE_ERROR"),
                    _ => (null, null, null),
                };
                Set(crash, ("$ts", ts), ("$kind", kind.ToString()), ("$exe", app.Exe ?? ""), ("$path", app.Exe is null ? null : $@"C:\Program Files\Synthetic\{app.Exe}"),
                    ("$module", module), ("$code", code), ("$detail", detail), ("$sleep", kind == CrashKind.UnexpectedShutdown && rnd.Next(4) == 0 ? 1 : 0));
                crash.ExecuteNonQuery();
            }
        }

        // ── Drives filling up slowly ──
        using (var drive = Cmd(conn, tx, "INSERT OR REPLACE INTO drive_day(day, drive, used_gb, total_gb) VALUES($d, $drive, $u, $t)"))
        {
            double c = 300, d = 900;
            for (var day = start; day <= end.Date; day = day.AddDays(1))
            {
                c = Math.Clamp(c + rnd.NextDouble() * 1.2 - 0.5, 100, 930);
                d = Math.Clamp(d + rnd.NextDouble() * 3 - 1, 100, 1850);
                foreach (var (name, used, total) in new[] { (Drives[0], c, 953.0), (Drives[1], d, 1863.0) })
                {
                    Set(drive, ("$d", TimeUtil.ToUnix(day)), ("$drive", name), ("$u", R(used)), ("$t", total));
                    drive.ExecuteNonQuery();
                }
            }
        }

        // The rollups and the longest session are rebuilt from the raw tables by RigsightDb itself, below.
        using (var drop = Cmd(conn, tx, "DROP TABLE app_month; DROP TABLE system_day; DELETE FROM meta WHERE key = 'max_session_sec';"))
            drop.ExecuteNonQuery();
        tx.Commit();
        conn.Close();

        using (RigsightDb.OpenWriter(dbPath)) { }
    }

    /// <summary>Settings for a test copy: nothing pops up on the tester's screen (widgets, overlay, alerts, recaps).</summary>
    public static RigsightSettings QuietSettings()
    {
        var s = new RigsightSettings { StartupConfigured = true, LastRecapDay = DateTime.Today.ToString("yyyy-MM-dd") };
        s = SettingsStore.Deserialize(SettingsStore.Serialize(s)); // normalized, like a file the agent wrote
        foreach (var w in s.Widgets) w.Enabled = false;
        s.Overlay.Enabled = false;
        s.Alerts.Enabled = false;
        s.Alerts.DailyRecap = false;
        s.Alerts.SessionSummaries = false;
        s.Alerts.CrashNotifications = false;
        return s;
    }

    private static AppHour Hour(Dictionary<(long, long), AppHour> hours, long ts, long app)
    {
        if (!hours.TryGetValue((ts, app), out var h)) hours[(ts, app)] = h = new AppHour { Ts = ts, AppId = app };
        return h;
    }

    private static double R(double v) => Math.Round(v, 1);

    private static SqliteCommand Cmd(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Set(SqliteCommand cmd, params (string Name, object? Value)[] args)
    {
        cmd.Parameters.Clear();
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
