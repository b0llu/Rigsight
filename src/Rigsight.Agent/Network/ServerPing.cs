using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Net;
using Rigsight.Core.Protocol;

namespace Rigsight.Agent.Network;

/// <summary>
/// The ping to the server each game, call or picked app is talking to: found in the admin network trace (the app's busiest
/// server), checked every 5 seconds on a thread of its own while the app keeps talking to it, and added up a minute at a
/// time. Only servers the apps already use are asked anything, and only these apps': a download or a browser tab doesn't
/// care how far its server is, so it gets no checks.
/// </summary>
internal sealed class ServerPing : IDisposable
{
    /// <summary>Seconds between two checks of one server.</summary>
    public const int EverySeconds = 5;

    /// <summary>How long one check is waited for (ms): one not back by then counts as lost.</summary>
    public const int TimeoutMs = 1000;

    /// <summary>At most this many servers are checked at once.</summary>
    public const int MostTargets = 4;

    /// <summary>The checks kept for the live view: the last 10 minutes.</summary>
    private const int Kept = 600 / EverySeconds;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Target> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly StreamPicker _picker = new();
    private volatile bool _enabled;

    /// <summary>One app's server being checked: how, its checks, and what's said about it.</summary>
    private sealed class Target(string exe, IPEndPoint server, string kind)
    {
        public string Exe { get; } = exe;
        public IPEndPoint Server { get; set; } = server;
        public string Kind { get; set; } = kind;
        public ProbeWay Way { get; set; } = ProbeWay.Unknown;
        public int Misses { get; set; }
        public string? Place { get; set; }
        public bool PlaceAsked { get; set; }
        public (int Low, int High)? Usual { get; set; }
        public long UsualAt { get; set; }
        public long LastSeen { get; set; } = TimeUtil.NowUnix();
        public List<(long Time, int? Ms)> Checks { get; } = [];
        /// <summary>The minutes before this (unix seconds) are written already.</summary>
        public long WrittenTo { get; set; }
    }

    internal enum ProbeWay { Unknown, Echo, Tcp443, TcpPort, None }

    private ServerPing()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "RigSight server ping", Priority = ThreadPriority.BelowNormal };
    }

    public static ServerPing Start()
    {
        var ping = new ServerPing();
        ping._thread.Start();
        return ping;
    }

    /// <summary>
    /// Sampler thread, each second: what each process sent to and got from each server, turned into the servers to check.
    /// <paramref name="enabled"/> off forgets every target (nothing is asked until it's on again).
    /// </summary>
    public void OnSecond(Dictionary<FlowKey, FlowCount> flows, IReadOnlyDictionary<int, string> pidToExe, Func<string, int, bool> isGame,
        IReadOnlyCollection<string> picked, bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            lock (_lock) _targets.Clear();
            _picker.Reset();
            return;
        }
        long now = TimeUtil.NowUnix();
        var seen = new List<StreamPicker.Seen>(flows.Count);
        foreach (var (key, count) in flows)
        {
            if (!pidToExe.TryGetValue(key.Pid, out var exe)) continue;
            seen.Add(new Rigsight.Core.Net.StreamPicker.Seen(exe, key.Pid, Address(key), key.Port, key.Udp, count.SentPackets, count.ReceivedPackets, count.ReceivedBytes));
        }
        var chosen = _picker.Second(now, seen, isGame, picked, MostTargets);
        lock (_lock)
        {
            foreach (var c in chosen)
            {
                if (_targets.TryGetValue(c.Exe, out var t))
                {
                    t.LastSeen = now;
                    t.Kind = c.Kind;
                    // Another server for the same app (a new match, another call): its checks start over.
                    if (!t.Server.Equals(c.Server))
                    {
                        Log.Write("server ping", $"{c.Exe} now talks to {c.Server} ({c.Kind}), was {t.Server}");
                        _targets[c.Exe] = new Target(c.Exe, c.Server, c.Kind) { LastSeen = now };
                    }
                }
                else
                {
                    Log.Write("server ping", $"Checking {c.Exe}'s server {c.Server} ({c.Kind}) every {EverySeconds} s");
                    _targets[c.Exe] = new Target(c.Exe, c.Server, c.Kind) { LastSeen = now };
                }
            }
            // An app that stopped talking for 15 s: its server isn't checked any more.
            foreach (var gone in _targets.Values.Where(t => now - t.LastSeen > 15).ToList())
            {
                Log.Write("server ping", $"Stopped checking {gone.Exe}'s server: no traffic for 15 s");
                _targets.Remove(gone.Exe);
            }
        }
    }

    /// <summary>The processes of picked apps, whose TCP servers the trace notes too.</summary>
    public static HashSet<int>? PickedPids(IReadOnlyDictionary<int, string> pidToExe, IReadOnlyCollection<string> picked)
    {
        if (picked.Count == 0) return null;
        var set = new HashSet<int>();
        foreach (var (pid, exe) in pidToExe)
            if (picked.Contains(exe, StringComparer.OrdinalIgnoreCase)) set.Add(pid);
        return set.Count == 0 ? null : set;
    }

    private static IPAddress Address(FlowKey key)
    {
        if (key.Hi == 0 && key.Lo <= uint.MaxValue) return new IPAddress((long)key.Lo);
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), key.Hi);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), key.Lo);
        return new IPAddress(bytes);
    }

    /// <summary>Sampler thread, with the live readings: each server checked now, with its usual from <paramref name="db"/>.</summary>
    public List<ServerLive>? Live(RigsightDb db)
    {
        if (!_enabled) return null;
        long now = TimeUtil.NowUnix();
        var live = new List<ServerLive>();
        List<Target> targets;
        lock (_lock) targets = [.. _targets.Values];
        foreach (var t in targets)
        {
            List<(long Time, int? Ms)> checks;
            lock (_lock) checks = [.. t.Checks];
            if (checks.Count == 0) continue;
            // The usual is read again every 10 minutes, not each second.
            if (now - t.UsualAt > 600)
            {
                t.UsualAt = now;
                try { t.Usual = db.NetServerUsual(t.Exe, t.Server.Address.ToString(), now); }
                catch (Exception ex) { Log.Error("server ping", ex); }
            }
            var minute = checks.Where(c => c.Time >= now - 60).Select(c => c.Ms).OfType<int>().ToList();
            live.Add(new ServerLive
            {
                Exe = t.Exe, Kind = t.Kind, Server = t.Server.Address.ToString(), Place = t.Place,
                // Shown once there are 30 seconds of checks: a connection just made never reads as 0 ms.
                Ms = checks.Count >= 30 / EverySeconds ? LineCheck.Middle(minute) : null,
                Sent = checks.Count, Lost = checks.Count(c => c.Ms is null), NoAnswer = t.Way == ProbeWay.None,
                UsualLow = t.Usual?.Low, UsualHigh = t.Usual?.High,
                Recent = [.. checks.Select(c => c.Ms ?? -1)],
            });
        }
        return live.Count == 0 ? null : live;
    }

    /// <summary>The minutes whole before <paramref name="now"/>, one row per app and minute.</summary>
    public List<NetServerMinute> TakeMinutes(long now)
    {
        long current = now - now % 60;
        var rows = new List<NetServerMinute>();
        lock (_lock)
        {
            foreach (var t in _targets.Values)
            {
                // Each minute is written once, when it's whole; the live view keeps its 10 minutes.
                foreach (var g in t.Checks.Where(c => c.Time < current && c.Time >= t.WrittenTo).GroupBy(c => c.Time - c.Time % 60))
                {
                    var answered = g.Select(c => c.Ms).OfType<int>().ToList();
                    rows.Add(new NetServerMinute(g.Key, t.Exe, t.Server.Address.ToString(), t.Kind, g.Count(), LineCheck.Middle(answered),
                        answered.Count > 0 ? answered.Max() : null, g.Count(c => c.Ms is null)));
                }
                t.WrittenTo = current;
            }
        }
        return rows;
    }

    // ── The checks' thread ────────────────────────────────────────────────

    private void Run()
    {
        while (!_stop.IsSet)
        {
            try
            {
                List<Target> targets;
                lock (_lock) targets = _enabled ? [.. _targets.Values] : [];
                foreach (var t in targets)
                {
                    if (_stop.IsSet) break;
                    int? ms = Check(t);
                    long now = TimeUtil.NowUnix();
                    lock (_lock)
                    {
                        t.Checks.Add((now, ms));
                        if (t.Checks.Count > Kept) t.Checks.RemoveRange(0, t.Checks.Count - Kept);
                    }
                    if (!t.PlaceAsked) Name(t);
                }
            }
            catch (Exception ex)
            {
                Log.Error("server ping", ex);
            }
            _stop.Wait(EverySeconds * 1000);
        }
    }

    /// <summary>
    /// One check of a target's server, in the way that worked for it: a ping when the server itself answers one (Discord's
    /// don't), else the time to connect to its port 443, else to the app's own port, where a refusal is an answer too
    /// (it came from the server). A server that answers none of them after six tries is said to, not counted as all lost.
    /// </summary>
    private static int? Check(Target t)
    {
        if (t.Way == ProbeWay.None) return null;
        if (t.Way != ProbeWay.Unknown) return Probe(t.Way, t.Server);
        foreach (var way in new[] { ProbeWay.Echo, ProbeWay.Tcp443, ProbeWay.TcpPort })
        {
            if (way == ProbeWay.TcpPort && (t.Server.Port is 0 or 443)) continue;
            if (Probe(way, t.Server) is int ms)
            {
                t.Way = way;
                Log.Write("server ping", $"{t.Exe}'s server {t.Server.Address} answers {Words(way)} ({ms} ms)");
                return ms;
            }
        }
        if (++t.Misses >= 6)
        {
            t.Way = ProbeWay.None;
            Log.Write("server ping", $"{t.Exe}'s server {t.Server} answers no ping or connection: its ping can't be shown");
        }
        return null;
    }

    private static string Words(ProbeWay way) => way switch
    {
        ProbeWay.Echo => "pings",
        ProbeWay.Tcp443 => "a connection to port 443",
        _ => "a connection to its own port",
    };

    internal static int? Probe(ProbeWay way, IPEndPoint server)
    {
        try
        {
            if (way == ProbeWay.Echo)
            {
                using var ping = new Ping();
                var reply = ping.Send(server.Address, TimeoutMs, new byte[32]);
                // Only the server itself counts: a router on the way saying "too far" is not an answer from it.
                return reply.Status == IPStatus.Success && reply.Address.Equals(server.Address) ? (int)reply.RoundtripTime : null;
            }
            int port = way == ProbeWay.Tcp443 ? 443 : server.Port;
            using var socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                var task = socket.ConnectAsync(new IPEndPoint(server.Address, port));
                if (!task.Wait(TimeoutMs)) return null;
            }
            catch (AggregateException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                // Refused: the server's own reply, one round trip away.
            }
            return (int)Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The city a server's name gives (some games' servers are named for their airport), asked once.</summary>
    private void Name(Target t)
    {
        t.PlaceAsked = true;
        try
        {
            var task = Dns.GetHostEntryAsync(t.Server.Address);
            if (task.Wait(2000) && ServerPlaces.FromHostName(task.Result.HostName) is { } place)
            {
                t.Place = place;
                Log.Write("server ping", $"{t.Exe}'s server is {task.Result.HostName}: {place}");
            }
        }
        catch (Exception)
        {
            // No name for it (most servers have none): its place isn't said.
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeoutMs * 3);
    }
}
