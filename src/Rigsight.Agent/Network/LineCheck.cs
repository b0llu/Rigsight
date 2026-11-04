using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;

namespace Rigsight.Agent.Network;

/// <summary>
/// Checks the line every few seconds: how long a ping takes to this PC's router (the home side: the cable or Wi-Fi) and
/// to the internet provider's first router after it (the provider's side). No outside server is asked anything: the
/// provider's router is found the way a traceroute finds it, by sending pings that may only go so many stops and seeing
/// which stop answers first from outside the home. Pings leave from the network card itself, so a VPN in between (WARP)
/// doesn't hide the line. Its own thread; the sampler takes what it found each second (<see cref="Live"/>) and each
/// minute (<see cref="TakeMinutes"/>).
/// </summary>
internal sealed class LineCheck : IDisposable
{
    /// <summary>Seconds between two checks (a ping to each stop).</summary>
    public const int EverySeconds = 5;

    /// <summary>How long a ping is waited for (ms): one not back by then counts as lost.</summary>
    public const int TimeoutMs = 1000;

    /// <summary>Seconds between two searches for the stops when nothing changed (a provider can move its routers).</summary>
    public const int FindEverySeconds = 1800;

    /// <summary>The farthest stop the provider's router is looked for at: past it, it's somebody else's network.</summary>
    public const int MaxStops = 6;

    // Where the stop-limited pings are aimed. They never get there (each dies at the stop it is limited to, a few stops
    // out, inside the provider's network); any address on the internet would do, it only picks the way out.
    private static readonly IPAddress Aim = IPAddress.Parse("1.1.1.1");

    private readonly Thread _thread;
    private volatile bool _enabled = true;
    private readonly ManualResetEventSlim _stop = new(), _findNow = new();
    private readonly object _lock = new();
    private readonly List<Sample> _samples = [];
    private volatile Stops? _stops;

    private readonly record struct Sample(long Time, int? Lan, int? Isp, bool IspTried);

    /// <summary>The two stops: the card's address, the router, and the provider's router with how many stops out it is.</summary>
    private sealed record Stops(IPAddress Source, IPAddress Router, IPAddress? Provider, int ProviderStop, bool ProviderEcho);

    private LineCheck()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "RigSight line check", Priority = ThreadPriority.BelowNormal };
    }

    public static LineCheck Start(bool enabled = true)
    {
        var check = new LineCheck { _enabled = enabled };
        check._thread.Start();
        return check;
    }

    /// <summary>Ping checks turned on or off (Settings, or the Network page): off, nothing is pinged and nothing kept.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value) lock (_lock) _samples.Clear();
            else _findNow.Set();
            Log.Write("line check", value ? "Ping checks turned on" : "Ping checks turned off: nothing is pinged until they're on again");
        }
    }

    /// <summary>The network changed (a new address, a cable plugged in, the PC woke): look for the stops again.</summary>
    public void FindAgain() => _findNow.Set();

    private void Run()
    {
        long nextFind = 0;
        while (!_stop.IsSet)
        {
            try
            {
                if (!_enabled) { _stop.Wait(EverySeconds * 1000); continue; }
                long now = TimeUtil.NowUnix();
                if (_findNow.IsSet || now >= nextFind || _stops is null)
                {
                    _findNow.Reset();
                    _stops = Find();
                    nextFind = now + (_stops is null ? 60 : FindEverySeconds);
                }
                if (_stops is { } s)
                {
                    int? lan = Echo(s.Source, s.Router);
                    int? isp = s.Provider is null ? null : s.ProviderEcho ? Echo(s.Source, s.Provider) : Limited(s.Source, s.ProviderStop, s.Provider);
                    lock (_lock)
                    {
                        _samples.Add(new Sample(TimeUtil.NowUnix(), lan, isp, s.Provider is not null));
                        // Two minutes kept: the minute being filled and the one before it, not yet taken.
                        long cut = TimeUtil.NowUnix() - 180;
                        _samples.RemoveAll(x => x.Time < cut);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("line check", ex);
            }
            _stop.Wait(EverySeconds * 1000);
        }
    }

    /// <summary>
    /// The router (the network card's gateway) and the provider's first router: the first stop out from the router that
    /// answers from an address outside the home (a provider's shared address, 100.64/10, counts as the provider's).
    /// Null when no network card has a router.
    /// </summary>
    private static Stops? Find()
    {
        var card = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up && NetAdapters.IsHardware(ni))
            .Select(ni => (ni, ip: ni.GetIPProperties()))
            .Select(x => (x.ni,
                Source: x.ip.UnicastAddresses.Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork),
                Router: x.ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))))
            .Where(x => x.Source is not null && x.Router is not null)
            .OrderBy(x => x.ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
            .FirstOrDefault();
        if (card.Source is null || card.Router is null)
        {
            Log.Write("line check", "No network card with a router: the line isn't checked for now");
            return null;
        }
        // Stop by stop: the first to answer from outside the home is the provider's.
        IPAddress? provider = null;
        int stop = 0;
        for (int ttl = 2; ttl <= MaxStops && provider is null; ttl++)
        {
            for (int attempt = 0; attempt < 2 && provider is null; attempt++)
                if (Send(card.Source, Aim, ttl, out var from) is not null && from is not null && !IsHome(from)) (provider, stop) = (from, ttl);
        }
        // A router that answers pings is pinged; one that only says "too many stops" is reached the same way it was found.
        bool echo = provider is not null && Enumerable.Range(0, 3).Any(_ => Echo(card.Source, provider) is not null);
        int? lanMs = Echo(card.Source, card.Router), ispMs = provider is null ? null : echo ? Echo(card.Source, provider) : Limited(card.Source, stop, provider);
        Log.Write("line check", $"Router {card.Router} on {card.ni.Name} ({Ms(lanMs)}); "
            + (provider is null ? $"no provider's router answered within {MaxStops} stops: only the home side is checked"
               : $"the provider's first router is {stop} stops out ({Ms(ispMs)}, {(echo ? "answers pings" : "reached stop-limited")})"));
        return new Stops(card.Source, card.Router, provider, stop, echo);
    }

    private static string Ms(int? ms) => ms is int v ? $"{v} ms" : "no answer";

    /// <summary>A home address: private (10/8, 172.16/12, 192.168/16) or link-local (169.254/16). Anything else is outside.</summary>
    internal static bool IsHome(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254);
    }

    /// <summary>
    /// What the sampler shows now: the last minute's middle ping to each stop, how much the provider's jumped, and the
    /// share of the last minute's pings that went unanswered. Null before the stops are known.
    /// </summary>
    public LineLive? Live()
    {
        var stops = _stops;
        if (stops is null || !_enabled) return null;
        List<Sample> last;
        long since = TimeUtil.NowUnix() - 60;
        lock (_lock) last = [.. _samples.Where(x => x.Time >= since)];
        if (last.Count == 0) return null;
        var m = Summarize(last);
        // The provider's when it was reached, else the router's: what was lost on the way to the farthest stop checked.
        int lost = stops.Provider is not null ? m.IspLost : m.LanLost;
        return new LineLive
        {
            LanMs = m.LanMs, IspMs = m.IspMs, Jitter = m.Jitter, LostPercent = m.Sent > 0 ? Math.Round(100.0 * lost / m.Sent, 1) : 0,
            ProviderFound = stops.Provider is not null,
        };
    }

    /// <summary>The minutes whole before <paramref name="now"/> (unix seconds), each added up into a row, and forgotten.</summary>
    public List<NetPing> TakeMinutes(long now)
    {
        long current = now - now % 60;
        List<Sample> done;
        lock (_lock)
        {
            done = [.. _samples.Where(x => x.Time < current)];
            _samples.RemoveAll(x => x.Time < current);
        }
        return [.. done.GroupBy(x => x.Time - x.Time % 60).OrderBy(g => g.Key).Select(g => Summarize([.. g]) with { Ts = g.Key })];
    }

    /// <summary>A minute's (or any stretch's) samples as one row: middles, longest, lost, and the provider's jitter.</summary>
    internal static NetPing Summarize(IReadOnlyList<(long Time, int? Lan, int? Isp, bool IspTried)> samples)
    {
        var lan = samples.Select(x => x.Lan).OfType<int>().ToList();
        var isp = samples.Where(x => x.IspTried).Select(x => x.Isp).OfType<int>().ToList();
        int? jitter = isp.Count >= 2 ? (int)Math.Round(isp.Zip(isp.Skip(1), (a, b) => Math.Abs(a - b)).Average()) : null;
        return new NetPing(0, samples.Count, Middle(lan), lan.Count > 0 ? lan.Max() : null, samples.Count(x => x.Lan is null),
            Middle(isp), isp.Count > 0 ? isp.Max() : null, samples.Count(x => x.IspTried && x.Isp is null), jitter);
    }

    private static NetPing Summarize(List<Sample> samples) => Summarize([.. samples.Select(x => (x.Time, x.Lan, x.Isp, x.IspTried))]);

    internal static int? Middle(List<int> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    // ── Pings ────────────────────────────────────────────────────────────

    /// <summary>A ping to <paramref name="to"/> from the card's address: milliseconds, or null when it went unanswered.</summary>
    private static int? Echo(IPAddress from, IPAddress to) => Send(from, to, 64, out var at) is int ms && at is not null && at.Equals(to) ? ms : null;

    /// <summary>A ping limited to <paramref name="stops"/> stops, answered by <paramref name="expected"/>: milliseconds or null.</summary>
    private static int? Limited(IPAddress from, int stops, IPAddress expected) => Send(from, Aim, stops, out var at) is int ms && expected.Equals(at) ? ms : null;

    /// <summary>
    /// One ping from <paramref name="from"/> to <paramref name="to"/> that may go <paramref name="ttl"/> stops: the
    /// milliseconds until someone answered (the target, or the stop it ran out at, in <paramref name="answeredBy"/>), or
    /// null when nobody did within <see cref="TimeoutMs"/>.
    /// </summary>
    private static unsafe int? Send(IPAddress from, IPAddress to, int ttl, out IPAddress? answeredBy)
    {
        answeredBy = null;
        nint handle = IcmpCreateFile();
        if (handle == -1) return null;
        try
        {
            // Filled as Windows' own ping fills it ("abcdefg…"): an empty one went unanswered on a real router (2026-11-04).
            byte* data = stackalloc byte[32];
            for (int i = 0; i < 32; i++) data[i] = (byte)('a' + i % 23);
            const int replySize = 256;
            byte* reply = stackalloc byte[replySize];
            var options = new IpOptionInformation { Ttl = (byte)ttl };
            uint n = IcmpSendEcho2Ex(handle, 0, 0, 0, ToUint(from), ToUint(to), data, 32, &options, reply, replySize, TimeoutMs);
            if (n == 0) return null;
            var r = (IcmpEchoReply*)reply;
            if (r->Status is not (IpSuccess or IpTtlExpiredTransit)) return null;
            answeredBy = new IPAddress(r->Address);
            return (int)Math.Min(r->RoundTripTime, int.MaxValue);
        }
        finally
        {
            IcmpCloseHandle(handle);
        }
    }

    private static uint ToUint(IPAddress a) => BitConverter.ToUInt32(a.GetAddressBytes());

    private const uint IpSuccess = 0, IpTtlExpiredTransit = 11013;

    [StructLayout(LayoutKind.Sequential)]
    private struct IpOptionInformation
    {
        public byte Ttl, Tos, Flags, OptionsSize;
        public nint OptionsData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IcmpEchoReply
    {
        public uint Address, Status, RoundTripTime;
        public ushort DataSize, Reserved;
        public nint Data;
        public IpOptionInformation Options;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern nint IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern bool IcmpCloseHandle(nint handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern unsafe uint IcmpSendEcho2Ex(nint handle, nint evt, nint apcRoutine, nint apcContext, uint source, uint destination,
        void* requestData, ushort requestSize, IpOptionInformation* options, void* replyBuffer, uint replySize, uint timeout);

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeoutMs * 3);
    }
}
