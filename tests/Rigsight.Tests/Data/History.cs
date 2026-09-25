using Microsoft.Data.Sqlite;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using static Rigsight.Tests.Data.Make;

namespace Rigsight.Tests.Data;

/// <summary>A small, fixed history (raw rows) that can be written into any shape of database, old or new.</summary>
public sealed class History
{
    public List<AppRow> Apps { get; } = [];
    public List<SystemMinute> Minutes { get; } = [];
    public List<AppHour> Hours { get; } = [];
    public List<SessionRow> Sessions { get; } = [];
    public List<CrashEvent> Crashes { get; } = [];

    /// <summary>
    /// About seven weeks across a year and month end (20 Dec 2024 – 5 Feb 2025), with minutes on either side of
    /// midnight and month starts, readings missing now and then, and a two-day session.
    /// </summary>
    public static History Sample(int seed = 5)
    {
        var h = new History();
        var rnd = new Random(seed);
        string[] exes = ["game.exe", "chrome.exe", "discord.exe", "code.exe"];
        for (int i = 0; i < exes.Length; i++)
            h.Apps.Add(new AppRow { Id = i + 1, Exe = exes[i], Name = $"App {i + 1}", Path = $@"C:\Apps\{exes[i]}", Category = i == 0 ? AppCategory.Game : AppCategory.Other, FirstSeen = U(2024, 12, 20) });

        var minutes = new SortedDictionary<long, SystemMinute>();
        var hours = new Dictionary<(long, long), AppHour>();
        void Add(long ts)
        {
            double? Maybe(double v) => rnd.Next(12) == 0 ? null : Math.Round(v, 1);
            long app = 1 + rnd.Next(exes.Length);
            int active = rnd.Next(4) == 0 ? 0 : 60;
            minutes[ts] = new SystemMinute
            {
                Ts = ts, CpuTemp = Maybe(40 + rnd.NextDouble() * 40), CpuTempMax = Maybe(45 + rnd.NextDouble() * 45),
                GpuTemp = Maybe(35 + rnd.NextDouble() * 40), GpuTempMax = Maybe(40 + rnd.NextDouble() * 45), GpuHotMax = Maybe(50 + rnd.NextDouble() * 50),
                CpuLoad = Maybe(rnd.NextDouble() * 100), GpuLoad = Maybe(rnd.NextDouble() * 100), CpuPower = Maybe(20 + rnd.NextDouble() * 100),
                GpuPower = Maybe(30 + rnd.NextDouble() * 300), CpuVoltMax = Maybe(1 + rnd.NextDouble() * 0.4), GpuVoltMax = Maybe(0.7 + rnd.NextDouble() * 0.4),
                RamUsed = Maybe(8 + rnd.NextDouble() * 8), FgApp = active == 0 ? null : app, ActiveSec = active, IdleSec = 60 - active,
            };
            var hourTs = U(TimeUtil.LocalHourStart(L(ts)));
            if (!hours.TryGetValue((hourTs, app), out var hr)) hours[(hourTs, app)] = hr = new AppHour { Ts = hourTs, AppId = app };
            hr.FgSec += active; hr.IdleSec += 60 - active; hr.BgSec += rnd.Next(3) * 10;
            if (minutes[ts].CpuTemp is double c) { hr.CpuTempSum += c; hr.CpuTempN++; hr.CpuTempMax = Math.Max(hr.CpuTempMax ?? 0, c + 2); }
            if (minutes[ts].GpuTemp is double g) { hr.GpuTempSum += g; hr.GpuTempN++; hr.GpuTempMax = Math.Max(hr.GpuTempMax ?? 0, g + 2); hr.GpuHotMax = g + 11; }
            hr.CpuSum += 5; hr.CpuN++; hr.CpuMax = Math.Max(hr.CpuMax ?? 0, rnd.Next(100));
            hr.MemSum += 300; hr.MemN++; hr.MemMax = Math.Max(hr.MemMax ?? 0, 300 + rnd.Next(4000));
            if (rnd.Next(5) == 0) hr.CpuPowerMax = Math.Max(hr.CpuPowerMax ?? 0, rnd.Next(150));
        }

        for (var day = new DateTime(2024, 12, 20); day <= new DateTime(2025, 2, 5); day = day.AddDays(1))
        {
            if (rnd.Next(6) == 0) continue; // a day off
            var start = day.AddHours(8 + rnd.Next(6));
            for (int m = 0; m < 30 + rnd.Next(120); m++) Add(U(start.AddMinutes(m)));
            // Either side of the next midnight.
            Add(U(day.AddDays(1).AddMinutes(-1)));
            Add(U(day.AddDays(1)));
        }
        h.Minutes.AddRange(minutes.Values);
        h.Hours.AddRange(hours.Values.OrderBy(x => x.Ts).ThenBy(x => x.AppId));

        h.Sessions.Add(Session(1, U(2024, 12, 30, 20), U(2025, 1, 1, 20), active: 36000, game: true)); // two days, over new year
        h.Sessions.Add(Session(2, U(2025, 1, 10, 9), U(2025, 1, 10, 11), active: 7000));
        h.Sessions.Add(Session(3, U(2025, 1, 31, 23), U(2025, 2, 1, 1), active: 5000, cpu: null, gpu: null));
        h.Sessions.Add(Session(4, U(2025, 2, 3, 10), U(2025, 2, 3, 10, 0, 30), active: 30));

        h.Crashes.Add(new CrashEvent { Ts = U(2025, 1, 1, 19, 55), Kind = CrashKind.AppCrash, AppExe = "game.exe", Module = "nvwgf2umx.dll", Code = "c0000005" });
        h.Crashes.Add(new CrashEvent { Ts = U(2025, 1, 20, 3), Kind = CrashKind.SystemCrash, Code = "0x00000124" });
        h.Crashes.Add(new CrashEvent { Ts = U(2025, 2, 2, 12), Kind = CrashKind.UnexpectedShutdown, DuringSleep = true });
        return h;
    }

    /// <summary>Writes the history through Rigsight's own writer (as the agent records it now).</summary>
    public void WriteFresh(RigsightDb db)
    {
        using var tx = db.BeginTransaction();
        foreach (var a in Apps) db.UpsertApp(a.Exe, a.Name, a.Path, a.Category);
        foreach (var m in Minutes) db.WriteMinute(m);
        foreach (var hr in Hours) db.AddAppHour(hr);
        foreach (var s in Sessions) db.InsertSession(s);
        db.InsertCrashes(Crashes);
        tx.Commit();
    }

    /// <summary>Inserts the raw rows with plain SQL into a database of any shape (no rollups).</summary>
    public void WriteRaw(SqliteConnection conn, bool withGpuMem)
    {
        using var tx = conn.BeginTransaction();
        void Run(string sql, params (string, object?)[] args)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        foreach (var a in Apps)
            Run("INSERT INTO apps(id, exe, name, path, category, first_seen) VALUES($i, $e, $n, $p, $c, $f)",
                ("$i", a.Id), ("$e", a.Exe), ("$n", a.Name), ("$p", a.Path), ("$c", a.Category.ToString()), ("$f", a.FirstSeen));
        foreach (var m in Minutes)
            Run($"""
                INSERT INTO system_minute(ts, cpu_temp, cpu_temp_max, gpu_temp, gpu_temp_max, gpu_hot_max, cpu_load, gpu_load, cpu_power, gpu_power,
                    cpu_volt_max, gpu_volt_max, ram_used, fg_app, active_sec, idle_sec{(withGpuMem ? ", gpu_mem_max" : "")})
                VALUES($ts, $a, $b, $c, $d, $e, $f, $g, $h, $i, $j, $k, $l, $m, $n, $o{(withGpuMem ? ", $p" : "")})
                """, ("$ts", m.Ts), ("$a", m.CpuTemp), ("$b", m.CpuTempMax), ("$c", m.GpuTemp), ("$d", m.GpuTempMax), ("$e", m.GpuHotMax),
                ("$f", m.CpuLoad), ("$g", m.GpuLoad), ("$h", m.CpuPower), ("$i", m.GpuPower), ("$j", m.CpuVoltMax), ("$k", m.GpuVoltMax),
                ("$l", m.RamUsed), ("$m", m.FgApp), ("$n", m.ActiveSec), ("$o", m.IdleSec), ("$p", m.GpuMemMax));
        foreach (var h in Hours)
            Run("""
                INSERT INTO app_hour(ts, app_id, fg_sec, idle_sec, bg_sec, min_sec, cpu_sum, cpu_n, cpu_max, mem_sum, mem_n, mem_max,
                    cpu_temp_sum, cpu_temp_n, cpu_temp_max, gpu_temp_sum, gpu_temp_n, gpu_temp_max, gpu_hot_max,
                    cpu_power_max, gpu_power_max, cpu_volt_max, gpu_volt_max, gpu_load_sum, gpu_load_n)
                VALUES($ts, $app, $fg, $idle, $bg, $min, $cs, $cn, $cm, $ms, $mn, $mm, $cts, $ctn, $ctm, $gts, $gtn, $gtm, $ghm, $cpm, $gpm, $cvm, $gvm, $gls, $gln)
                """, ("$ts", h.Ts), ("$app", h.AppId), ("$fg", h.FgSec), ("$idle", h.IdleSec), ("$bg", h.BgSec), ("$min", h.MinSec),
                ("$cs", h.CpuSum), ("$cn", h.CpuN), ("$cm", h.CpuMax), ("$ms", h.MemSum), ("$mn", h.MemN), ("$mm", h.MemMax),
                ("$cts", h.CpuTempSum), ("$ctn", h.CpuTempN), ("$ctm", h.CpuTempMax), ("$gts", h.GpuTempSum), ("$gtn", h.GpuTempN),
                ("$gtm", h.GpuTempMax), ("$ghm", h.GpuHotMax), ("$cpm", h.CpuPowerMax), ("$gpm", h.GpuPowerMax), ("$cvm", h.CpuVoltMax),
                ("$gvm", h.GpuVoltMax), ("$gls", h.GpuLoadSum), ("$gln", h.GpuLoadN));
        foreach (var s in Sessions)
            Run("INSERT INTO sessions(app_id, start, end, active_sec, cpu_temp_max, gpu_temp_max, is_game) VALUES($a, $s, $e, $act, $c, $g, $game)",
                ("$a", s.AppId), ("$s", s.Start), ("$e", s.End), ("$act", s.ActiveSec), ("$c", s.CpuTempMax), ("$g", s.GpuTempMax), ("$game", s.IsGame ? 1 : 0));
        foreach (var c in Crashes)
            Run("INSERT INTO crashes(ts, kind, app_exe, app_path, module, code, detail, during_sleep) VALUES($t, $k, $e, $p, $m, $c, $d, $s)",
                ("$t", c.Ts), ("$k", c.Kind.ToString()), ("$e", c.AppExe), ("$p", c.AppPath), ("$m", c.Module), ("$c", c.Code), ("$d", c.Detail),
                ("$s", c.DuringSleep ? 1 : 0));
        tx.Commit();
    }
}
