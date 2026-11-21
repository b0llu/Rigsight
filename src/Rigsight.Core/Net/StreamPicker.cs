using System.Net;

namespace Rigsight.Core.Net;

/// <summary>
/// Which apps' servers get a ping, from what each app sent to and got from each server second by second: a game's busiest
/// server, a call's, remote play's, and any app picked with "Check its ping". The rest (a download, a browser tab, a
/// sync) get none: their delay isn't felt. A stream counts once it has gone both ways in 20 of the last 30 seconds, so a
/// short exchange never gets a check; a call app's counts sooner and stays counted while it's quiet (see <see cref="InCall"/>).
/// </summary>
public sealed class StreamPicker
{
    /// <summary>The seconds looked back over.</summary>
    public const int Window = 30;

    /// <summary>Of those, the seconds a stream must have gone both ways in.</summary>
    public const int SteadySeconds = 20;

    /// <summary>An app neither a game nor picked counts as a live stream from this many packets a second each way.</summary>
    public const double LivePacketsPerSecond = 5;

    /// <summary>…while getting less than this (bytes a second): more is a download or a video, not a call.</summary>
    public const long LiveMostBytesPerSecond = 1_500_000;

    /// <summary>A call app's stream is a call from this many seconds with traffic…</summary>
    public const int CallSeconds = 4;

    /// <summary>…spread over at least this many seconds…</summary>
    public const int CallSpan = 10;

    /// <summary>…the last of them at most this many seconds ago.</summary>
    public const int CallRecent = 10;

    /// <summary>One second's traffic between one process and one server.</summary>
    public readonly record struct Seen(string Exe, int Pid, IPAddress Address, ushort Port, bool Udp, int Sent, int Received, long ReceivedBytes);

    /// <summary>A server to check: the app, the server, and what the app is (game, call, remote, live or picked).</summary>
    public readonly record struct Chosen(string Exe, IPEndPoint Server, string Kind, bool Udp = true);

    private readonly record struct Tick(long Time, int Sent, int Received, long ReceivedBytes);

    private sealed class Stream(string exe, IPEndPoint server, bool udp)
    {
        public string Exe { get; } = exe;
        public IPEndPoint Server { get; } = server;
        public bool Udp { get; } = udp;
        public int Pid { get; set; }
        public List<Tick> Seconds { get; } = [];
    }

    private readonly Dictionary<(string Exe, IPEndPoint Server, bool Udp), Stream> _streams = [];

    public void Reset() => _streams.Clear();

    /// <summary>One second's traffic in; the servers to check now out, at most <paramref name="most"/>, most wanted first.</summary>
    public List<Chosen> Second(long now, IEnumerable<Seen> seen, Func<string, int, bool> isGame, IReadOnlyCollection<string> picked, int most)
    {
        // This second, added up per app and server (an app's processes together).
        var sums = new Dictionary<(string, IPEndPoint, bool), (int Pid, int Sent, int Received, long Bytes)>();
        foreach (var s in seen)
        {
            var key = (s.Exe.ToLowerInvariant(), new IPEndPoint(s.Address, s.Port), s.Udp);
            sums.TryGetValue(key, out var v);
            sums[key] = (s.Pid, v.Sent + s.Sent, v.Received + s.Received, v.Bytes + s.ReceivedBytes);
        }
        foreach (var ((exe, server, udp), v) in sums)
        {
            if (!_streams.TryGetValue((exe, server, udp), out var stream))
                _streams[(exe, server, udp)] = stream = new Stream(seen.First(x => x.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)).Exe, server, udp);
            stream.Pid = v.Pid;
            stream.Seconds.Add(new Tick(now, v.Sent, v.Received, v.Bytes));
        }
        foreach (var (key, stream) in _streams.ToList())
        {
            stream.Seconds.RemoveAll(x => x.Time <= now - Window);
            if (stream.Seconds.Count == 0) _streams.Remove(key);
        }

        var chosen = new List<(Chosen Pick, int Rank, long Packets)>();
        foreach (var app in _streams.Values.GroupBy(x => x.Exe, StringComparer.OrdinalIgnoreCase))
        {
            bool isPicked = picked.Contains(app.Key, StringComparer.OrdinalIgnoreCase);
            // Its server that matters most (a game's before a call's…), the busiest of those over the window; UDP only
            // unless the app was picked (TCP is noted only for those). A busier stream that matters less doesn't hide it: a
            // call app loading pictures over QUIC while its call is quiet.
            var best = app.Where(x => x.Udp || isPicked)
                .Select(x => (Stream: x, Kind: KindOf(x, isPicked, isGame, now), Packets: x.Seconds.Sum(s => (long)s.Sent + s.Received)))
                .Where(x => x.Kind is not null)
                .OrderBy(x => Rank(x.Kind!)).ThenByDescending(x => x.Packets).FirstOrDefault();
            if (best.Stream is null) continue;
            chosen.Add((new Chosen(best.Stream.Exe, best.Stream.Server, best.Kind!, best.Stream.Udp), Rank(best.Kind!), best.Packets));
        }
        return [.. chosen.OrderBy(x => x.Rank).ThenByDescending(x => x.Packets).Take(most).Select(x => x.Pick)];
    }

    private static string? KindOf(Stream s, bool isPicked, Func<string, int, bool> isGame, long now)
    {
        bool steady = Steady(s) >= SteadySeconds;
        return isPicked && steady ? "picked"
            : steady && isGame(s.Exe, s.Pid) ? "game"
            : AppKinds.IsCall(s.Exe) && InCall(s, now) ? "call"
            : steady && AppKinds.IsRemote(s.Exe) ? "remote"
            : steady && Live(s) ? "live"
            : null;
    }

    private static int Steady(Stream s) => s.Seconds.Count(x => x.Sent > 0 && x.Received > 0);

    /// <summary>
    /// A call app's stream in a call, talked in or not. A call where only one side talks, or nobody does, sends next to
    /// nothing: Discord then only trades a keep-alive with its voice server every 5 seconds, so the steady two-way rule
    /// never saw it (the user, 2026-11-17: in a Discord call to Hong Kong, "nothing is shown"). So: traffic both ways
    /// over the window, in <see cref="CallSeconds"/> seconds spread over <see cref="CallSpan"/>, the last one recent, not
    /// a download, and not port 443 (that's QUIC, the app's web traffic, never its call).
    /// </summary>
    private static bool InCall(Stream s, long now)
    {
        if (s.Server.Port == 443 || s.Seconds.Count < CallSeconds) return false;
        var active = s.Seconds.Where(x => x.Sent > 0 || x.Received > 0).ToList();
        return active.Count >= CallSeconds && active[^1].Time - active[0].Time >= CallSpan && active[^1].Time >= now - CallRecent
            && active.Sum(x => x.Sent) > 0 && active.Sum(x => x.Received) > 0
            && s.Seconds.Sum(x => x.ReceivedBytes) / (double)Window < LiveMostBytesPerSecond;
    }

    /// <summary>Small packets both ways, many a second, and not a download: how voice and games talk.</summary>
    private static bool Live(Stream s)
    {
        double seconds = Window;
        double sent = s.Seconds.Sum(x => x.Sent) / seconds, received = s.Seconds.Sum(x => x.Received) / seconds;
        double bytes = s.Seconds.Sum(x => x.ReceivedBytes) / seconds;
        return sent >= LivePacketsPerSecond && received >= LivePacketsPerSecond && bytes < LiveMostBytesPerSecond && sent * 8 >= received;
    }

    private static int Rank(string kind) => kind switch { "picked" => 0, "game" => 1, "call" => 2, "remote" => 3, _ => 4 };
}

/// <summary>Apps known by name to be calls or remote play (their exe, without ".exe", any case).</summary>
public static class AppKinds
{
    private static readonly HashSet<string> Calls = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord", "discordptb", "discordcanary", "teams", "ms-teams", "zoom", "slack", "skype", "whatsapp", "telegram", "signal",
        "webex", "ciscocollabhost", "mumble", "teamspeak", "ts3client_win64", "element", "guilded",
    };

    private static readonly HashSet<string> Remote = new(StringComparer.OrdinalIgnoreCase)
    {
        "parsecd", "parsec", "moonlight", "sunshine", "rustdesk", "anydesk", "teamviewer", "mstsc", "msrdc", "geforcenow",
        "steamstreamingclient", "rainway", "chrome remote desktop host", "remoting_host",
    };

    public static bool IsCall(string exe) => Calls.Contains(Stem(exe));
    public static bool IsRemote(string exe) => Remote.Contains(Stem(exe));

    private static string Stem(string exe) => exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
}

/// <summary>The city in a server's name, for the servers named for their airport (sin, bom, fra…). Null when there's none.</summary>
public static class ServerPlaces
{
    private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sin"] = "Singapore", ["sgp"] = "Singapore", ["bom"] = "Mumbai", ["maa"] = "Chennai", ["del"] = "Delhi", ["blr"] = "Bengaluru",
        ["hyd"] = "Hyderabad", ["ccu"] = "Kolkata", ["hkg"] = "Hong Kong", ["nrt"] = "Tokyo", ["hnd"] = "Tokyo", ["tyo"] = "Tokyo",
        ["kix"] = "Osaka", ["icn"] = "Seoul", ["syd"] = "Sydney", ["mel"] = "Melbourne", ["akl"] = "Auckland", ["fra"] = "Frankfurt",
        ["ams"] = "Amsterdam", ["lhr"] = "London", ["lon"] = "London", ["cdg"] = "Paris", ["mad"] = "Madrid", ["waw"] = "Warsaw",
        ["arn"] = "Stockholm", ["sto"] = "Stockholm", ["hel"] = "Helsinki", ["vie"] = "Vienna", ["mil"] = "Milan", ["mxp"] = "Milan",
        ["iad"] = "Washington", ["ord"] = "Chicago", ["dfw"] = "Dallas", ["atl"] = "Atlanta", ["lax"] = "Los Angeles",
        ["sea"] = "Seattle", ["sjc"] = "San Jose", ["sfo"] = "San Francisco", ["mia"] = "Miami", ["yyz"] = "Toronto",
        ["gru"] = "São Paulo", ["scl"] = "Santiago", ["lim"] = "Lima", ["eze"] = "Buenos Aires", ["jnb"] = "Johannesburg",
        ["dxb"] = "Dubai", ["tlv"] = "Tel Aviv", ["ist"] = "Istanbul", ["bah"] = "Bahrain", ["cpt"] = "Cape Town",
    };

    private static readonly string[] Cities = ["singapore", "mumbai", "chennai", "frankfurt", "amsterdam", "london", "paris", "tokyo",
        "seoul", "sydney", "stockholm", "dubai", "madrid", "warsaw", "vienna", "helsinki", "chicago", "dallas", "atlanta", "seattle", "miami"];

    public static string? FromHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out _)) return null;
        foreach (var part in host.Split('.', '-', '_'))
        {
            string letters = new([.. part.TakeWhile(char.IsLetter)]);
            if (letters.Length == 3 && Codes.TryGetValue(letters, out var city)) return city;
            if (Cities.FirstOrDefault(c => letters.Equals(c, StringComparison.OrdinalIgnoreCase)) is { } named)
                return char.ToUpperInvariant(named[0]) + named[1..];
        }
        return null;
    }
}
