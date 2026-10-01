using Rigsight.Agent.Tracking;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;

namespace Rigsight.Agent.Network;

/// <summary>Where an app stood while it used the network: in front, behind another app, or with nobody at the PC.</summary>
internal enum NetState { Front, Background, Away }

/// <summary>
/// Turns each second's bytes per app into history: the minute (all apps together, with the speed the download held), each
/// app's hour and day, and big downloads. A VPN's traffic is counted once: what goes through its tunnel counts for the
/// app that sent it, and the VPN keeps only what it adds on top (see <see cref="FindCarriers"/>). Sampler thread only.
/// </summary>
internal sealed class NetTracker(RigsightDb db)
{
    /// <summary>A second moving at least this much (bytes) starts a download…</summary>
    internal const long TransferStartRate = 1_000_000;
    /// <summary>…which goes on while the app moves at least this much a second, with pauses up to <see cref="TransferGapSeconds"/>…</summary>
    internal const long TransferMoveRate = 100_000;
    internal const int TransferGapSeconds = 30;
    /// <summary>…and is kept if it came to this much.</summary>
    internal const long MinTransferBytes = 100_000_000;
    /// <summary>The fewest seconds in a minute for the speed the download held to be said.</summary>
    internal const int MinSteadySeconds = 50;
    /// <summary>The least that must go through a tunnel in a minute to look for the VPN carrying it.</summary>
    internal const long MinTunnelBytes = 1_000_000;

    private const string CarriersKey = "net_carriers";

    /// <summary>One app's bytes since the last write, by where it stood (index: <see cref="NetState"/>).</summary>
    private sealed class AppChunk
    {
        public required AppInfo App;
        public readonly long[] Down = new long[3], Up = new long[3];
        public long PhysDown, PhysUp, TunnelDown, TunnelUp, LanDown, LanUp, GameDown;

        public long TotalDown => Down[0] + Down[1] + Down[2];
        public long TotalUp => Up[0] + Up[1] + Up[2];
    }

    private sealed class Transfer
    {
        public required AppInfo App;
        public long Start, LastMoving, Bytes;
        public int Sec;
    }

    // Since the last write (several within a minute when the agent saves early); added into the hour and day at each write.
    private readonly Dictionary<long, AppChunk> _chunk = [];
    private long _chunkOtherDown, _chunkOtherUp, _chunkOtherLan, _chunkOtherTunnelDown, _chunkOtherTunnelUp;
    private long _chunkOtherBgDown, _chunkOtherBgUp, _chunkOtherAwayDown, _chunkOtherAwayUp;

    // The minute as written so far.
    private long _minDown, _minUp, _minBgDown, _minBgUp, _minAwayDown, _minAwayUp, _minLan;
    private readonly Dictionary<long, long> _minAppDown = [];
    private readonly List<double> _rates = [];

    private readonly Dictionary<long, Transfer> _transfers = [];
    private HashSet<string>? _carriers;

    /// <summary>The apps (exe) known to carry other apps' traffic through a tunnel: VPNs.</summary>
    internal HashSet<string> Carriers => _carriers ??= LoadCarriers();

    /// <summary>The last second: the whole connection's speed and each app's, for the open app.</summary>
    public NetLive Live { get; private set; } = new();

    private HashSet<string> LoadCarriers()
    {
        try
        {
            return new HashSet<string>((db.GetMeta(CarriersKey) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error("network", ex);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// One second's bytes (or a little more, <paramref name="seconds"/>): by app, and those of processes that couldn't be
    /// named or aren't tracked (null app), which count for the totals only.
    /// </summary>
    public void OnSecond(long now, IReadOnlyList<(AppInfo? App, NetCounts Counts)> counts, double seconds, AppInfo? front, bool present, bool gameInFront,
        Func<AppInfo, string> name)
    {
        seconds = Math.Max(seconds, 0.001);
        long physDown = 0, physUp = 0, tunnelDown = 0, tunnelUp = 0, carrierDown = 0, carrierUp = 0;
        var live = new List<(AppInfo App, NetCounts Counts)>();
        foreach (var (app, c) in counts)
        {
            physDown += c.Down;
            physUp += c.Up;
            bool carrier = app is not null && Carriers.Contains(app.Exe);
            if (carrier)
            {
                carrierDown += c.Down;
                carrierUp += c.Up;
            }
            else
            {
                tunnelDown += c.TunnelDown;
                tunnelUp += c.TunnelUp;
            }
            var state = !present ? NetState.Away : app is not null && app.Id == front?.Id ? NetState.Front : NetState.Background;
            if (app is null)
            {
                _chunkOtherDown += c.Down;
                _chunkOtherUp += c.Up;
                _chunkOtherTunnelDown += c.TunnelDown;
                _chunkOtherTunnelUp += c.TunnelUp;
                _chunkOtherLan += c.LanDown + c.LanUp;
                if (state == NetState.Background) { _chunkOtherBgDown += c.InternetDown; _chunkOtherBgUp += c.InternetUp; }
                if (state == NetState.Away) { _chunkOtherAwayDown += c.InternetDown; _chunkOtherAwayUp += c.InternetUp; }
                continue;
            }

            if (!_chunk.TryGetValue(app.Id, out var a)) _chunk[app.Id] = a = new AppChunk { App = app };
            a.Down[(int)state] += c.InternetDown;
            a.Up[(int)state] += c.InternetUp;
            a.PhysDown += c.Down;
            a.PhysUp += c.Up;
            a.TunnelDown += c.TunnelDown;
            a.TunnelUp += c.TunnelUp;
            a.LanDown += c.LanDown;
            a.LanUp += c.LanUp;
            if (state == NetState.Background && gameInFront) a.GameDown += c.InternetDown;
            live.Add((app, c));
        }

        // The connection's own speed: what crossed the network card, plus what went through a tunnel without a VPN app
        // carrying it here (a VPN app's bytes are the tunnel's, counted once).
        long wireDown = physDown + tunnelDown - Math.Min(carrierDown, tunnelDown);
        long wireUp = physUp + tunnelUp - Math.Min(carrierUp, tunnelUp);
        _rates.Add(wireDown / seconds);

        // Live: each app's speed, a VPN's as what it adds on top of the tunnel's traffic.
        var apps = new List<NetAppLive>();
        foreach (var (app, c) in live)
        {
            bool carrier = Carriers.Contains(app.Exe);
            long d = carrier ? Math.Max(0, c.Down - tunnelDown) : c.InternetDown;
            long u = carrier ? Math.Max(0, c.Up - tunnelUp) : c.InternetUp;
            if (d + u > 0)
                apps.Add(new NetAppLive { Exe = app.Exe, Name = name(app), Path = app.Path, Down = d / seconds, Up = u / seconds });
        }
        apps.Sort((x, y) => (y.Down + y.Up).CompareTo(x.Down + x.Up));
        if (apps.Count > LiveApps) apps.RemoveRange(LiveApps, apps.Count - LiveApps);
        Live = new NetLive { Time = now, Down = wireDown / seconds, Up = wireUp / seconds, Apps = apps };

        TrackTransfers(now, counts, seconds);
    }

    /// <summary>The most apps the live list sends.</summary>
    internal const int LiveApps = 15;

    private void TrackTransfers(long now, IReadOnlyList<(AppInfo? App, NetCounts Counts)> counts, double seconds)
    {
        foreach (var (app, c) in counts)
        {
            if (app is null || Carriers.Contains(app.Exe)) continue;
            long bytes = c.InternetDown;
            double rate = bytes / seconds;
            if (!_transfers.TryGetValue(app.Id, out var t))
            {
                if (rate < TransferStartRate) continue;
                _transfers[app.Id] = t = new Transfer { App = app, Start = now - (long)Math.Ceiling(seconds) };
            }
            t.Bytes += bytes;
            if (rate >= TransferMoveRate)
            {
                t.Sec += (int)Math.Round(seconds);
                t.LastMoving = now;
            }
        }
        foreach (var (id, t) in _transfers.ToList())
            if (now - t.LastMoving >= TransferGapSeconds) EndTransfer(id, t);
    }

    private void EndTransfer(long app, Transfer t)
    {
        _transfers.Remove(app);
        if (t.Bytes < MinTransferBytes) return;
        // One that turns out to be a VPN carrying another app's download isn't a download of its own.
        if (Carriers.Contains(t.App.Exe)) return;
        try { db.InsertNetTransfer(new NetTransfer(t.Start, app, t.LastMoving, t.Bytes, Math.Max(1, t.Sec))); }
        catch (Exception ex) { Log.Error("network", ex); }
    }

    /// <summary>
    /// Writes what came in since the last write: each app into its hour (<paramref name="hour"/>) and day, and the minute
    /// <paramref name="minute"/> as it stands. Inside the tracker's transaction.
    /// </summary>
    public void Write(long minute, long hour, bool closing)
    {
        FindCarriers();
        long down = _chunkOtherDown + _chunkOtherTunnelDown, up = _chunkOtherUp + _chunkOtherTunnelUp;
        long bgDown = _chunkOtherBgDown, bgUp = _chunkOtherBgUp, awayDown = _chunkOtherAwayDown, awayUp = _chunkOtherAwayUp, lan = _chunkOtherLan;
        long tunnelDown = _chunk.Values.Where(a => !Carriers.Contains(a.App.Exe)).Sum(a => a.TunnelDown) + _chunkOtherTunnelDown;
        long tunnelUp = _chunk.Values.Where(a => !Carriers.Contains(a.App.Exe)).Sum(a => a.TunnelUp) + _chunkOtherTunnelUp;

        foreach (var a in _chunk.Values)
        {
            // A VPN keeps only what it adds to the traffic it carried (its own overhead), spread over where it stood.
            if (Carriers.Contains(a.App.Exe))
            {
                Shrink(a.Down, Math.Min(a.PhysDown, tunnelDown));
                Shrink(a.Up, Math.Min(a.PhysUp, tunnelUp));
                tunnelDown -= Math.Min(a.PhysDown, tunnelDown);
                tunnelUp -= Math.Min(a.PhysUp, tunnelUp);
            }
            var use = new NetAppUse
            {
                Ts = hour, App = a.App.Id, Down = a.TotalDown, Up = a.TotalUp,
                BgDown = a.Down[(int)NetState.Background], BgUp = a.Up[(int)NetState.Background],
                AwayDown = a.Down[(int)NetState.Away], AwayUp = a.Up[(int)NetState.Away],
                GameDown = Math.Min(a.GameDown, a.TotalDown), Lan = a.LanDown + a.LanUp,
            };
            down += use.Down;
            up += use.Up;
            bgDown += use.BgDown;
            bgUp += use.BgUp;
            awayDown += use.AwayDown;
            awayUp += use.AwayUp;
            lan += use.Lan;
            _minAppDown[a.App.Id] = _minAppDown.GetValueOrDefault(a.App.Id) + use.Down;
            if (!use.IsEmpty) db.AddNetAppUse(use);
        }

        _minDown += down;
        _minUp += up;
        _minBgDown += bgDown;
        _minBgUp += bgUp;
        _minAwayDown += awayDown;
        _minAwayUp += awayUp;
        _minLan += lan;
        ClearChunk();

        if (_minDown + _minUp + _minLan > 0)
        {
            long? top = null;
            if (_minAppDown.Count > 0)
            {
                var (app, bytes) = _minAppDown.MaxBy(kv => kv.Value);
                if (bytes >= 1_000_000) top = app;
            }
            db.WriteNetMinute(new NetMinute(minute, _minDown, _minUp, _minBgDown, _minBgUp, _minAwayDown, _minAwayUp, _minLan, Steady(), top));
        }

        if (closing)
            foreach (var (id, t) in _transfers.ToList()) EndTransfer(id, t);
    }

    /// <summary>A new minute starts: what was written stays written.</summary>
    public void NewMinute()
    {
        _minDown = _minUp = _minBgDown = _minBgUp = _minAwayDown = _minAwayUp = _minLan = 0;
        _minAppDown.Clear();
        _rates.Clear();
    }

    /// <summary>The speed the download held for most of the minute: three seconds in four were at least this fast.</summary>
    private long? Steady()
    {
        if (_rates.Count < MinSteadySeconds) return null;
        var sorted = _rates.Order().ToList();
        long steady = (long)sorted[sorted.Count / 4];
        return steady > 0 ? steady : null;
    }

    /// <summary>
    /// A VPN app moves, through the network card, about what its tunnel carried for the other apps: find it the first time
    /// (one with no tunnel traffic of its own, within 0.9 to 1.4 times the tunnel's download), and remember it, so its
    /// bytes count once from then on.
    /// </summary>
    private void FindCarriers()
    {
        long tunnel = _chunk.Values.Where(a => !Carriers.Contains(a.App.Exe)).Sum(a => a.TunnelDown) + _chunkOtherTunnelDown;
        if (tunnel < MinTunnelBytes) return;
        var candidate = _chunk.Values
            .Where(a => !Carriers.Contains(a.App.Exe) && a.TunnelDown + a.TunnelUp <= (a.PhysDown + a.PhysUp) / 10)
            .MaxBy(a => a.PhysDown);
        if (candidate is null) return;
        // Its own bytes don't count towards the tunnel it carries.
        if (candidate.PhysDown < tunnel * 0.9 || candidate.PhysDown > tunnel * 1.4) return;
        Carriers.Add(candidate.App.Exe);
        Log.Write("network", $"{candidate.App.Exe} carries other apps' traffic (a VPN): counted once from now on");
        try { db.SetMeta(CarriersKey, string.Join('|', Carriers)); }
        catch (Exception ex) { Log.Error("network", ex); }
    }

    private static void Shrink(long[] byState, long by)
    {
        long total = byState.Sum();
        if (total <= 0 || by <= 0) return;
        double keep = Math.Max(0, 1 - (double)by / total);
        for (int i = 0; i < byState.Length; i++) byState[i] = (long)Math.Round(byState[i] * keep);
    }

    private void ClearChunk()
    {
        _chunk.Clear();
        _chunkOtherDown = _chunkOtherUp = _chunkOtherLan = _chunkOtherTunnelDown = _chunkOtherTunnelUp = 0;
        _chunkOtherBgDown = _chunkOtherBgUp = _chunkOtherAwayDown = _chunkOtherAwayUp = 0;
    }

    /// <summary>History cleared: start again from nothing (the VPNs found stay found).</summary>
    public void Clear()
    {
        ClearChunk();
        NewMinute();
        _transfers.Clear();
    }
}
