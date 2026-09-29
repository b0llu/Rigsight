using Microsoft.Data.Sqlite;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Support;

/// <summary>
/// A PC lived in minute by minute, with the physics that make insights hard: a GPU warms and cools over minutes, not at
/// once; its fans stop below 60° and start again only above it, and keep going down to 50° (so the same temperature can
/// come with 0 rpm or 1,400); a game running after it's closed keeps the card warm into "idle" minutes; the room is warmer
/// in the afternoon and in summer. Written through the agent's own database writer (day totals and all), so a scenario
/// test runs the reports exactly as the app does. Faults are switched on from a date: dust, a fan that stops, a card that
/// slows itself. Deterministic for a seed.
/// </summary>
internal sealed class PcSim
{
    public sealed record Options
    {
        public int Seed { get; init; } = 1;

        /// <summary>The room, in °C, at its mean; warmer by <see cref="DailySwing"/> mid-afternoon.</summary>
        public double Room { get; init; } = 26;
        public double DailySwing { get; init; } = 2;

        /// <summary>
        /// The room this much warmer at the height of summer (mid-May, as in India) than its mean, as much cooler in
        /// mid-November.
        /// </summary>
        public double SeasonSwing { get; init; }

        /// <summary>
        /// Degrees the GPU and CPU coolers lose at full load per 30 days from <see cref="DustFrom"/>: dust raises a cooler's
        /// resistance to heat, and a rise over the room is power times that, so at rest (a tenth of the power) it shows a
        /// tenth as much.
        /// </summary>
        public double DustPerMonth { get; init; }
        public DateTime? DustFrom { get; init; }

        /// <summary>From this moment the GPU's fans don't turn (0 rpm), and the card runs hotter for it.</summary>
        public DateTime? GpuFanDeadFrom { get; init; }

        /// <summary>A case fan (on the motherboard) that stops from this moment.</summary>
        public DateTime? CaseFanDeadFrom { get; init; }

        /// <summary>A GPU that holds its fans at zero below 60° (most modern cards). Off: a fan always turning, at least 800 rpm.</summary>
        public bool GpuZeroRpm { get; init; } = true;

        /// <summary>From this moment the GPU slows itself above 80°.</summary>
        public DateTime? ThrottleFrom { get; init; }

        /// <summary>Chance of a day with the PC on at all.</summary>
        public double OnChance { get; init; } = 0.9;

        /// <summary>Chance a day that's on has gaming in the evening.</summary>
        public double GameChance { get; init; } = 0.8;

        /// <summary>Hours of gaming on a gaming day, at most (at least half of it).</summary>
        public double GameHours { get; init; } = 3;

        /// <summary>Minutes a game keeps running after it's put down (alt-tabbed, in its menu), still loading the GPU.</summary>
        public int GameTail { get; init; }

        /// <summary>Always this one of <see cref="Games"/> (else mostly the heavy one, sometimes the others).</summary>
        public int? Game { get; init; }
    }

    private readonly Options _o;
    private readonly Random _rnd;
    private readonly RigsightDb _db;
    private readonly Dictionary<string, long> _apps = [];
    private readonly (long Gpu1, long Gpu2, long CpuFan, long CaseFan, long Unused) _fans;

    // The state carried from minute to minute.
    private double _gpuTemp, _cpuTemp;
    private bool _gpuFansOn;
    private DateTime _last = DateTime.MinValue;

    public sealed record Game(string Name, string Exe, double GpuLoad, double GpuPower, double CpuLoad, double CpuPower);

    /// <summary>A heavy game, a lighter one, and an old one: what makes "your other games" a fair comparison.</summary>
    public static readonly Game[] Games =
    [
        new("Cyberpunk 2077", "cyberpunk2077.exe", 98, 300, 35, 70),
        new("Dota 2", "dota2.exe", 70, 190, 30, 60),
        new("Hades", "hades.exe", 35, 110, 15, 40),
    ];

    private PcSim(RigsightDb db, Options o)
    {
        _db = db;
        _o = o;
        _rnd = new Random(o.Seed);
        foreach (var (exe, name, cat) in new[] { ("chrome.exe", "Google Chrome", AppCategory.Browser), ("code.exe", "Visual Studio Code", AppCategory.Development),
                     ("discord.exe", "Discord", AppCategory.Communication) })
            _apps[exe] = db.UpsertApp(exe, name, null, cat);
        foreach (var g in Games) _apps[g.Exe] = db.UpsertApp(g.Exe, g.Name, null, AppCategory.Game);
        _fans = (db.FanId("/gpu-nvidia/0/fan/1", "GPU Fan 1", "NVIDIA GeForce RTX 3080 Ti"),
            db.FanId("/gpu-nvidia/0/fan/2", "GPU Fan 2", "NVIDIA GeForce RTX 3080 Ti"),
            db.FanId("/lpc/it8686e/0/fan/0", "Fan #1", "ITE IT8686E"),
            db.FanId("/lpc/it8686e/0/fan/1", "Fan #2", "ITE IT8686E"),
            db.FanId("/lpc/it8686e/0/fan/3", "Fan #4", "ITE IT8686E"));
        _gpuTemp = _cpuTemp = o.Room + 10;
    }

    /// <summary>A new database at <paramref name="path"/>, lived in from <paramref name="from"/> up to <paramref name="to"/> (a day's plan cut there).</summary>
    public static void Live(string path, DateTime from, DateTime to, Options? options = null, Action<PcSim, DateTime>? eachDay = null)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
        using var db = RigsightDb.OpenWriter(path);
        using var raw = new SqliteConnection($"Data Source={path};Pooling=False");
        raw.Open();
        var sim = new PcSim(db, options ?? new Options());
        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            if (eachDay is null) sim.Day(day, to);
            else eachDay(sim, day);
            sim.Flush(raw);
        }
    }

    // What's been lived since the last flush: written a day at a time (see Flush).
    private readonly List<(SystemMinute Minute, (long Fan, int Avg, int Max)[] Fans)> _pending = [];
    private readonly Dictionary<(long Hour, long App), AppHour> _hours = [];
    private readonly List<SessionRow> _sessions = [];

    /// <summary>
    /// Writes what's been lived. The minutes go in directly but the last of each day, which goes through the agent's own
    /// writer: that adds the whole day up again from its minutes (and the fans' day with it), exactly as it does live.
    /// One add-up a day instead of one a minute.
    /// </summary>
    private void Flush(SqliteConnection raw)
    {
        foreach (var day in _pending.GroupBy(p => TimeUtil.FromUnix(p.Minute.Ts).Date))
        {
            var list = day.ToList();
            using (var tx = raw.BeginTransaction())
            {
                using var minute = raw.CreateCommand();
                minute.Transaction = tx;
                minute.CommandText = "INSERT OR REPLACE INTO system_minute(ts, cpu_temp, cpu_temp_max, gpu_temp, gpu_temp_max, gpu_hot_max, cpu_load, gpu_load, "
                    + "cpu_power, gpu_power, ram_used, fg_app, cpu_app, gpu_app, cpu_clock, gpu_clock, active_sec, idle_sec) "
                    + "VALUES($ts, $ct, $ctm, $gt, $gtm, $gh, $cl, $gl, $cp, $gp, $ram, $fg, $ca, $ga, $cc, $gc, $act, $idle)";
                using var fan = raw.CreateCommand();
                fan.Transaction = tx;
                fan.CommandText = "INSERT OR REPLACE INTO fan_minute(ts, fan, rpm_avg, rpm_max) VALUES($ts, $f, $a, $m)";
                foreach (var (m, fans) in list.Take(list.Count - 1))
                {
                    minute.Parameters.Clear();
                    foreach (var (name, value) in new (string, object?)[] { ("$ts", m.Ts), ("$ct", m.CpuTemp), ("$ctm", m.CpuTempMax), ("$gt", m.GpuTemp),
                                 ("$gtm", m.GpuTempMax), ("$gh", m.GpuHotMax), ("$cl", m.CpuLoad), ("$gl", m.GpuLoad), ("$cp", m.CpuPower), ("$gp", m.GpuPower),
                                 ("$ram", m.RamUsed), ("$fg", m.FgApp), ("$ca", m.CpuApp), ("$ga", m.GpuApp), ("$cc", (long?)m.CpuClock), ("$gc", (long?)m.GpuClock),
                                 ("$act", m.ActiveSec), ("$idle", m.IdleSec) })
                        minute.Parameters.AddWithValue(name, value ?? DBNull.Value);
                    minute.ExecuteNonQuery();
                    foreach (var (id, avg, max) in fans)
                    {
                        fan.Parameters.Clear();
                        fan.Parameters.AddWithValue("$ts", m.Ts);
                        fan.Parameters.AddWithValue("$f", id);
                        fan.Parameters.AddWithValue("$a", avg);
                        fan.Parameters.AddWithValue("$m", max);
                        fan.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            var (lastMinute, lastFans) = list[^1];
            _db.WriteMinute(lastMinute);
            _db.WriteFanMinutes(lastMinute.Ts, lastFans);
        }
        _pending.Clear();
        using (var tx = _db.BeginTransaction())
        {
            foreach (var h in _hours.Values) _db.AddAppHour(h);
            foreach (var s in _sessions) _db.InsertSession(s);
            tx.Commit();
        }
        _hours.Clear();
        _sessions.Clear();
    }

    /// <summary>
    /// An ordinary day: on in the morning, work and browsing with breaks, a game in the evening on most days, off late
    /// (sometimes past midnight, into the next day).
    /// </summary>
    public void Day(DateTime day, DateTime until)
    {
        if (_rnd.NextDouble() > _o.OnChance) return;
        var t = day.AddHours(8 + _rnd.NextDouble() * 2);
        var end = day.AddHours(22 + _rnd.NextDouble() * 2.5);
        var gameAt = _rnd.NextDouble() < _o.GameChance ? day.AddHours(18 + _rnd.NextDouble() * 2) : DateTime.MaxValue;
        while (t < end && t < until)
        {
            if (t >= gameAt)
            {
                int pick = _rnd.Next(10) switch { < 6 => 0, < 9 => 1, _ => 2 };
                var game = Games[_o.Game ?? pick];
                t = Play(game, t, TimeSpan.FromHours(_o.GameHours * (0.5 + _rnd.NextDouble() * 0.5)), until);
                gameAt = DateTime.MaxValue;
                continue;
            }
            // Work in stretches, a break (away from the PC) now and then.
            string app = _rnd.Next(3) switch { 0 => "chrome.exe", 1 => "code.exe", _ => "discord.exe" };
            t = Desk(app, t, TimeSpan.FromMinutes(_rnd.Next(20, 90)), until);
            if (_rnd.NextDouble() < 0.3) t = Away(t, TimeSpan.FromMinutes(_rnd.Next(10, 40)), until);
        }
    }

    /// <summary>Light work in front: a few percent of CPU and GPU.</summary>
    public DateTime Desk(string exe, DateTime from, TimeSpan length, DateTime until) =>
        Run(from, length, until, _ => new Load(_apps[exe], null, 3 + _rnd.NextDouble() * 6, 12, 5 + _rnd.NextDouble() * 5, 30, true));

    /// <summary>At the PC but away: nothing running hard, no input.</summary>
    public DateTime Away(DateTime from, TimeSpan length, DateTime until) =>
        Run(from, length, until, _ => new Load(_apps["chrome.exe"], null, 1, 10, 3, 25, false));

    /// <summary>A game for <paramref name="length"/>, then (see <see cref="Options.GameTail"/>) left running behind the browser.</summary>
    public DateTime Play(Game game, DateTime from, TimeSpan length, DateTime until)
    {
        long id = _apps[game.Exe];
        var t = Run(from, length, until, _ => new Load(id, id, CpuLoad: game.CpuLoad + _rnd.NextDouble() * 10, CpuPower: game.CpuPower,
            GpuLoad: game.GpuLoad - 2 + _rnd.NextDouble() * 4, GpuPower: game.GpuPower * (0.95 + _rnd.NextDouble() * 0.1), Present: true), session: game);
        if (_o.GameTail > 0)
            t = Run(t, TimeSpan.FromMinutes(_o.GameTail), until, _ => new Load(_apps["chrome.exe"], id, CpuLoad: game.CpuLoad, CpuPower: game.CpuPower, GpuLoad: game.GpuLoad * 0.9, GpuPower: game.GpuPower * 0.9, Present: true));
        return t;
    }

    /// <summary>Heavy work on the CPU (a compile): the CPU at full tilt, the GPU resting.</summary>
    public DateTime Compile(DateTime from, TimeSpan length, DateTime until) =>
        Run(from, length, until, _ => new Load(_apps["code.exe"], null, 90 + _rnd.NextDouble() * 10, 110, 4, 30, true, CpuApp: _apps["code.exe"]));

    private readonly record struct Load(long Front, long? GpuApp, double CpuLoad, double CpuPower, double GpuLoad, double GpuPower, bool Present, long? CpuApp = null);

    private DateTime Run(DateTime from, TimeSpan length, DateTime until, Func<DateTime, Load> load, Game? session = null)
    {
        var t = TimeUtil.LocalMinuteStart(from);
        // The PC was off (or asleep) since the last minute: everything is back at room temperature.
        if (t - _last > TimeSpan.FromMinutes(30)) (_gpuTemp, _cpuTemp, _gpuFansOn) = (Room(t) + 8, Room(t) + 8, false);
        var end = from + length;
        if (end > until) end = until;
        double active = 0, gpuMax = 0, cpuMax = 0;
        var start = t;
        for (; t < end; t = t.AddMinutes(1))
        {
            var l = load(t);
            Minute(t, l);
            if (l.Present) active += 60;
            gpuMax = Math.Max(gpuMax, _gpuTemp);
            cpuMax = Math.Max(cpuMax, _cpuTemp);
            _last = t;
        }
        if (session is not null && t > start)
            _sessions.Add(new SessionRow
            {
                AppId = _apps[session.Exe], Start = TimeUtil.ToUnix(start), End = TimeUtil.ToUnix(t), ActiveSec = active,
                CpuTempMax = cpuMax, GpuTempMax = gpuMax, IsGame = true,
            });
        return t;
    }

    /// <summary>The room at <paramref name="t"/>: its mean, the afternoon, the season.</summary>
    public double Room(DateTime t)
    {
        double daily = _o.DailySwing * Math.Cos((t.TimeOfDay.TotalHours - 15) / 24 * 2 * Math.PI);
        double season = _o.SeasonSwing * Math.Cos((t.DayOfYear - 135) / 365.0 * 2 * Math.PI);
        return _o.Room + daily + season;
    }

    private static bool From(DateTime? since, DateTime t) => since is { } s && t >= s;

    private void Minute(DateTime t, Load l)
    {
        double room = Room(t);
        double dust = _o.DustFrom is { } d && t > d ? _o.DustPerMonth * (t - d).TotalDays / 30 : 0;
        bool gpuFanDead = From(_o.GpuFanDeadFrom, t);

        // Each chip heads for where its heat and its cooler balance out, about a third of the way each minute.
        double gpuTarget = room + 6 + l.GpuPower * 0.14 + dust * l.GpuPower / 300 + (gpuFanDead ? 8 + l.GpuPower * 0.06 : 0);
        double cpuTarget = room + 8 + l.CpuPower * 0.45 + dust * l.CpuPower / 100;
        _gpuTemp += (gpuTarget - _gpuTemp) * 0.3;
        _cpuTemp += (cpuTarget - _cpuTemp) * 0.35;

        // Slowing itself once hot: clocks down by a quarter.
        bool throttling = From(_o.ThrottleFrom, t) && _gpuTemp >= 80 && l.GpuLoad >= 80;
        double gpuClock = l.GpuLoad >= 30 ? (throttling ? 1400 : 1900) : 300;
        double cpuClock = l.CpuLoad >= 30 ? 4100 : 3000;

        // GPU fans: stopped below 60°, spinning until back under 50° (a fan curve with a gap, as cards have).
        _gpuFansOn = !_o.GpuZeroRpm || (_gpuFansOn ? _gpuTemp > 50 : _gpuTemp >= 60);
        double gpuRpm = gpuFanDead ? 0 : _gpuFansOn ? Math.Clamp(1000 + (_gpuTemp - 55) * 45, _o.GpuZeroRpm ? 1000 : 800, 3000) : 0;
        double cpuFanRpm = 700 + Math.Max(0, _cpuTemp - 40) * 30;
        double caseRpm = From(_o.CaseFanDeadFrom, t) ? 0 : 1000;

        long ts = TimeUtil.ToUnix(t);
        var minute = new SystemMinute
        {
            Ts = ts,
            CpuTemp = Math.Round(_cpuTemp, 1), CpuTempMax = Math.Round(_cpuTemp + 3, 1),
            GpuTemp = Math.Round(_gpuTemp, 1), GpuTempMax = Math.Round(_gpuTemp + 1, 1), GpuHotMax = Math.Round(_gpuTemp + 12, 1),
            CpuLoad = Math.Round(l.CpuLoad, 1), GpuLoad = Math.Round(l.GpuLoad, 1), CpuPower = l.CpuPower, GpuPower = l.GpuPower,
            RamUsed = 12_000, FgApp = l.Front, CpuApp = l.CpuApp, GpuApp = l.GpuApp,
            CpuClock = cpuClock, GpuClock = gpuClock,
            ActiveSec = l.Present ? 60 : 0, IdleSec = l.Present ? 0 : 60,
        };
        int Jitter(double rpm) => rpm <= 0 ? 0 : (int)Math.Round(rpm + _rnd.Next(-15, 16));
        _pending.Add((minute,
        [
            (_fans.Gpu1, Jitter(gpuRpm), Jitter(gpuRpm)), (_fans.Gpu2, Jitter(gpuRpm * 1.03), Jitter(gpuRpm * 1.03)),
            (_fans.CpuFan, Jitter(cpuFanRpm), Jitter(cpuFanRpm)), (_fans.CaseFan, Jitter(caseRpm), Jitter(caseRpm)), (_fans.Unused, 0, 0),
        ]));
        // The hour's totals for the app in front, as the agent adds them up.
        var key = (TimeUtil.ToUnix(TimeUtil.LocalHourStart(t)), l.Front);
        if (!_hours.TryGetValue(key, out var h)) _hours[key] = h = new AppHour { Ts = key.Item1, AppId = l.Front };
        h.FgSec += l.Present ? 60 : 0;
        h.IdleSec += l.Present ? 0 : 60;
        h.CpuSum += l.CpuLoad; h.CpuN++; h.CpuMax = Math.Max(h.CpuMax ?? 0, l.CpuLoad);
        h.MemSum += 900; h.MemN++; h.MemMax = 900;
        h.CpuTempSum += _cpuTemp; h.CpuTempN++; h.CpuTempMax = Math.Max(h.CpuTempMax ?? 0, _cpuTemp);
        h.GpuTempSum += _gpuTemp; h.GpuTempN++; h.GpuTempMax = Math.Max(h.GpuTempMax ?? 0, _gpuTemp);
    }
}
