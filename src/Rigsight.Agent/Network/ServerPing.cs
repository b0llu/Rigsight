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
/// care how far its server is, so it gets no checks. Each app is timed from its own traffic where it can be, with no
/// check of ours (the user, 2026-11-20: Dota 2's relays answer none of our checks, and up to 10 apps at once mustn't
/// load the line): a UDP app by the packets it sends that come back (see <see cref="EchoTimer"/>), a TCP app by the
/// round trips Windows times on its connection (see <see cref="TcpRtt"/>). Only an app neither works for gets a check
/// of ours, one small one every 5 seconds; the checks run side by side, so one slow server holds up no other.
/// </summary>
internal sealed class ServerPing : IDisposable
{
    /// <summary>Seconds between two checks of one server.</summary>
    public const int EverySeconds = 5;

    /// <summary>How long one check is waited for (ms): one not back by then counts as lost.</summary>
    public const int TimeoutMs = 1000;

    /// <summary>At most this many servers are checked at once (10 apps' checks of ours, at worst, are 2 small packets a second).</summary>
    public const int MostTargets = 10;

    /// <summary>How long a UDP app's server is left unchecked while its own echoes are waited for (ms).</summary>
    public const int EchoWaitMs = 20_000;

    /// <summary>
    /// How late the trace's events can come (ms): Windows hands them over a buffer at a time, about a second apart, so a
    /// packet's answer can be in the next one. A packet is called lost only by the trace's own times or after this long,
    /// never by the clock alone (the first build called 3 of 16 of Discord's lost that had all come back).
    /// </summary>
    public const int TraceLagMs = 3000;

    /// <summary>An app whose echoes stopped this long ago (ms) is checked by us again.</summary>
    public const int EchoGoneMs = 30_000;

    /// <summary>The checks kept for the live view: the last 10 minutes.</summary>
    private const int Kept = 600 / EverySeconds;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Target> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly StreamPicker _picker = new();
    private volatile bool _enabled;

    /// <summary>One app's server being checked: how, its checks, and what's said about it.</summary>
    private sealed class Target(string exe, IPEndPoint server, string kind, bool udp)
    {
        public string Exe { get; } = exe;
        public IPEndPoint Server { get; set; } = server;
        public string Kind { get; set; } = kind;
        /// <summary>A UDP stream (timed by its echoes) or TCP (timed by Windows on its connection).</summary>
        public bool Udp { get; } = udp;
        /// <summary>The app's processes now, for finding its TCP connection.</summary>
        public HashSet<int> Pids { get; set; } = [];
        /// <summary>The last reading of its TCP connection's round trips.</summary>
        public TcpRtt.Reading? Tcp { get; set; }
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
        /// <summary>The app's own echoes with this server, and the size they have once found.</summary>
        public EchoTimer Echoes { get; } = new();
        public int? EchoSize { get; set; }
        /// <summary>Its echoes sent before this (ms, the trace's clock) are in <see cref="Checks"/> already.</summary>
        public double EchoesTo { get; set; }
        public double Since { get; } = NowMs();
    }

    /// <summary>How a server is checked: Echo is a ping of ours, Own the app's own echoes timed, Conn the round trips
    /// Windows times on the app's own TCP connection.</summary>
    internal enum ProbeWay { Unknown, Echo, Tcp443, TcpPort, None, Own, Conn }

    /// <summary>Now in ms on the trace's clock: the time of day as a file time, which is how the trace gives each event's.</summary>
    private static double NowMs() => DateTime.UtcNow.ToFileTimeUtc() / 10_000.0;


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
    public void OnSecond(Dictionary<FlowKey, FlowCount> flows, List<EchoPacket> echoes, IReadOnlyDictionary<int, string> pidToExe,
        Func<string, int, bool> isGame, IReadOnlyCollection<string> picked, bool enabled)
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
        var pidsOf = pidToExe.GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToHashSet(), StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var c in chosen)
            {
                if (_targets.TryGetValue(c.Exe, out var t))
                {
                    t.LastSeen = now;
                    t.Kind = c.Kind;
                    // Another server for the same app (a new match, another call): its checks start over.
                    if (!t.Server.Equals(c.Server) || t.Udp != c.Udp)
                    {
                        Log.Write("server ping", $"{c.Exe} now talks to {c.Server} ({c.Kind}, {(c.Udp ? "UDP" : "TCP")}), was {t.Server}");
                        _targets[c.Exe] = new Target(c.Exe, c.Server, c.Kind, c.Udp) { LastSeen = now };
                    }
                }
                else
                {
                    Log.Write("server ping", $"Checking {c.Exe}'s server {c.Server} ({c.Kind}, {(c.Udp ? "UDP" : "TCP")}) every {EverySeconds} s");
                    _targets[c.Exe] = new Target(c.Exe, c.Server, c.Kind, c.Udp) { LastSeen = now };
                }
                _targets[c.Exe].Pids = pidsOf.GetValueOrDefault(c.Exe) ?? [];
            }
            // The packets with each call's server, for its echoes.
            double nowMs = NowMs();
            foreach (var e in echoes)
            {
                var server = new IPEndPoint(Address(e.Server), e.Server.Port);
                foreach (var t in _targets.Values)
                    if (t.Server.Equals(server)) t.Echoes.Packet(e.FileTime / 10_000.0, e.Sent, e.Size);
            }
            foreach (var t in _targets.Values) t.Echoes.Expire(nowMs - TraceLagMs);
            // An app that stopped talking for 15 s: its server isn't checked any more.
            foreach (var gone in _targets.Values.Where(t => now - t.LastSeen > 15).ToList())
            {
                Log.Write("server ping", $"Stopped checking {gone.Exe}'s server: no traffic for 15 s");
                _targets.Remove(gone.Exe);
            }
        }
    }

    /// <summary>The servers of the UDP apps being checked, whose every packet the trace keeps (null: none).</summary>
    public HashSet<FlowKey>? EchoServers()
    {
        lock (_lock)
        {
            var set = _targets.Values.Where(t => t.Udp).Select(t => Key(t.Server)).ToHashSet();
            return set.Count == 0 ? null : set;
        }
    }

    /// <summary>A server as the trace keys it (Pid 0): an IPv4 address's 4 bytes in <see cref="FlowKey.Lo"/>, as they come.</summary>
    internal static FlowKey Key(IPEndPoint server)
    {
        var bytes = server.Address.GetAddressBytes();
        return bytes.Length == 4 ? new FlowKey(0, 0, BitConverter.ToUInt32(bytes), (ushort)server.Port, true)
            : new FlowKey(0, BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8), (ushort)server.Port, true);
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

    internal static IPAddress Address(FlowKey key)
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
            var round = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                List<Target> targets;
                lock (_lock) targets = _enabled ? [.. _targets.Values] : [];
                // Side by side: a check of ours waits up to a second for a server, and ten in a row would make the
                // last one's checks 15 seconds apart.
                Parallel.ForEach(targets, new ParallelOptions { MaxDegreeOfParallelism = MostTargets }, t =>
                {
                    if (_stop.IsSet) return;
                    try
                    {
                        var checks = Check(t);
                        lock (_lock)
                        {
                            t.Checks.AddRange(checks);
                            if (t.Checks.Count > Kept) t.Checks.RemoveRange(0, t.Checks.Count - Kept);
                        }
                        if (!t.PlaceAsked) Name(t);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("server ping", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error("server ping", ex);
            }
            _stop.Wait(Math.Max(1000, EverySeconds * 1000 - (int)round.ElapsedMilliseconds));
        }
    }

    /// <summary>
    /// One check of a target's server, in the way that worked for it: a ping when the server itself answers one (Discord's
    /// don't), else the time to connect to its port 443, else to the app's own port, where a refusal is an answer too
    /// (it came from the server). A server that answers none of them after six tries is said to, not counted as all lost.
    /// </summary>
    private List<(long Time, int? Ms)> Check(Target t)
    {
        if (OwnEchoes(t) is { } own) return own;
        if (OwnConnection(t) is { } conn) return conn;
        // A UDP app's server: its own echoes are waited for a while before a check of ours is sent.
        if (t.Way == ProbeWay.Unknown && t.Udp && NowMs() - t.Since < EchoWaitMs) return [];
        return [(TimeUtil.NowUnix(), Probe(t))];
    }

    /// <summary>
    /// A TCP app's check from the round trips Windows timed on its own connection since the last check: their average.
    /// No check when none were timed (an idle connection has nothing to say, which isn't loss). Null when the
    /// connection can't be read (gone, IPv6, no admin rights): then it's checked by us.
    /// </summary>
    private List<(long Time, int? Ms)>? OwnConnection(Target t)
    {
        if (t.Udp || t.Way is not (ProbeWay.Unknown or ProbeWay.Conn)) return null;
        HashSet<int> pids;
        lock (_lock) pids = [.. t.Pids];
        if (TcpRtt.Read(t.Server, pids) is not { } r)
        {
            if (t.Way == ProbeWay.Conn)
            {
                Log.Write("server ping", $"{t.Exe}'s connection to {t.Server} can't be read any more: checking the server ourselves");
                TcpRtt.Forget(t.Server);
                lock (_lock) { (t.Way, t.Tcp) = (ProbeWay.Unknown, null); t.Checks.Clear(); }
            }
            return null;
        }
        var before = t.Tcp;
        t.Tcp = r;
        if (t.Way != ProbeWay.Conn)
        {
            Log.Write("server ping", $"{t.Exe}'s server {t.Server}: timing its own connection with Windows' round trips ({r.SmoothedMs} ms now)");
            lock (_lock) { t.Way = ProbeWay.Conn; t.Checks.Clear(); }
            return [];
        }
        // Another connection (its port changed) or counts that went back: start again from this reading.
        if (before is not { } b || b.LocalPort != r.LocalPort || r.Count < b.Count) return [];
        uint timed = r.Count - b.Count;
        return timed == 0 ? [] : [(TimeUtil.NowUnix(), (int)Math.Round((r.SumMs - b.SumMs) / (double)timed))];
    }

    /// <summary>
    /// The checks from the app's own echoes since the last time, one per <see cref="EverySeconds"/>: the middle of those
    /// back, or lost when none came back. Null while the app has no echo (or it stopped: then it's checked by us again).
    /// Once its echoes are found, the checks of ours before are dropped, so the two are never mixed in one figure.
    /// </summary>
    private List<(long Time, int? Ms)>? OwnEchoes(Target t)
    {
        double now = NowMs();
        long unix = TimeUtil.NowUnix();
        lock (_lock)
        {
            if (t.Way != ProbeWay.Own)
            {
                if (!t.Udp || t.Echoes.Size() is not int found) return null;
                var back = t.Echoes.Between(found, 0, double.MaxValue).Select(e => e.Ms).OfType<int>().Order().ToList();
                string dropped = t.Way is ProbeWay.Unknown ? "" : $"; {t.Checks.Count} checks of ours dropped";
                Log.Write("server ping", $"{t.Exe}'s server {t.Server}: timing {t.Exe}'s own {found}-byte packets, which come back from it"
                    + $" (middle {back[back.Count / 2]} ms of {back.Count}){dropped}");
                (t.Way, t.EchoSize, t.EchoesTo, t.Misses) = (ProbeWay.Own, found, 0, 0);
                t.Checks.Clear();
            }
            int size = t.EchoSize!.Value;
            if (t.Echoes.LastSent(size) is not double last || now - last > EchoGoneMs)
            {
                Log.Write("server ping", $"{t.Exe} stopped sending its {size}-byte packets to {t.Server}: checking the server ourselves again");
                (t.Way, t.EchoSize) = (ProbeWay.Unknown, null);
                t.Checks.Clear();
                return null;
            }
            // Only packets whose wait is over, with the trace's lag and a second spare for the sampler's next pass.
            double to = now - EchoTimer.WaitMs - TraceLagMs - 1000;
            var echoes = t.Echoes.Between(size, t.EchoesTo, to);
            t.EchoesTo = Math.Max(t.EchoesTo, to);
            return [.. echoes.GroupBy(e => Math.Floor(e.At / (EverySeconds * 1000.0))).Select(g =>
            {
                var answered = g.Select(e => e.Ms).OfType<int>().Order().ToList();
                long time = unix - (long)Math.Round((now - g.Max(e => e.At)) / 1000);
                return (time, answered.Count > 0 ? answered[answered.Count / 2] : (int?)null);
            })];
        }
    }

    private static int? Probe(Target t)
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
