namespace Rigsight.Core.Data;

// Ping to the server of each game, call or picked app (1.14.0): a row a minute per app while it's checked, beside the
// router and the provider in net_ping. Its own table, so nothing about the line's own checks changes.
public sealed partial class RigsightDb
{
    private void EnsureNetServerSchema() => Exec("""
        CREATE TABLE IF NOT EXISTS net_server(ts INTEGER NOT NULL, exe TEXT NOT NULL, server TEXT NOT NULL, kind TEXT NOT NULL,
            sent INTEGER NOT NULL, ms INTEGER, max INTEGER, lost INTEGER NOT NULL, PRIMARY KEY(ts, exe)) WITHOUT ROWID;
        """);

    // The app reads a database the agent made, which may be from before the servers were checked.
    private bool? _hasNetServer;
    private bool HasNetServer => _hasNetServer ??= HasTable("net_server");

    /// <summary>A minute of checks to one app's server (the agent's writer only).</summary>
    public void WriteNetServer(NetServerMinute m)
    {
        using var cmd = Cmd("""
            INSERT OR REPLACE INTO net_server(ts, exe, server, kind, sent, ms, max, lost) VALUES($ts, $exe, $server, $kind, $sent, $ms, $max, $lost)
            """, ("$ts", m.Ts), ("$exe", m.Exe), ("$server", m.Server), ("$kind", m.Kind), ("$sent", m.Sent), ("$ms", m.Ms), ("$max", m.Max), ("$lost", m.Lost));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every app's server minutes from <paramref name="from"/> to <paramref name="to"/> (unix seconds), oldest first.</summary>
    public List<NetServerMinute> GetNetServers(long from, long to)
    {
        var list = new List<NetServerMinute>();
        if (!HasNetServer) return list;
        using var cmd = Cmd("SELECT ts, exe, server, kind, sent, ms, max, lost FROM net_server WHERE ts >= $f AND ts < $t ORDER BY ts",
            ("$f", from), ("$t", to));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new NetServerMinute(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetInt32(7)));
        return list;
    }

    /// <summary>
    /// An app's usual ping to servers on the same network as <paramref name="server"/> (its first three numbers: one
    /// region of one service): the middle half of its minutes' figures over the 7 days before <paramref name="before"/>.
    /// Null until there are an hour of minutes to go on (a new server, or one used a few times, has no usual yet).
    /// </summary>
    public (int Low, int High)? NetServerUsual(string exe, string server, long before)
    {
        if (!HasNetServer) return null;
        string prefix = NetServerPrefix(server);
        var values = new List<int>();
        using (var cmd = Cmd("SELECT ms FROM net_server WHERE exe = $exe AND server LIKE $p AND ts >= $f AND ts < $t AND ms IS NOT NULL AND sent >= 6",
            ("$exe", exe), ("$p", prefix + "%"), ("$f", before - 7 * 86400), ("$t", before)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) values.Add(r.GetInt32(0));
        return NetServerUsualOf(values);
    }

    internal static (int Low, int High)? NetServerUsualOf(List<int> values)
    {
        if (values.Count < 60) return null;
        values.Sort();
        return (values[values.Count / 4], values[values.Count * 3 / 4]);
    }

    /// <summary>"104.29.141." of "104.29.141.190" (an IPv6 address: its first four groups).</summary>
    internal static string NetServerPrefix(string server)
    {
        if (server.Contains(':')) return string.Join(':', server.Split(':').Take(4)) + ":";
        int last = server.LastIndexOf('.');
        return last > 0 ? server[..(last + 1)] : server;
    }

    private void PruneNetServers(long before)
    {
        if (!HasNetServer) return;
        using var cmd = Cmd("DELETE FROM net_server WHERE ts < $t", ("$t", before));
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// A minute of checks to the server one app was talking to: <see cref="Kind"/> is game, call, remote or picked; the
/// milliseconds are the minute's middle and highest answer, null when none came back.
/// </summary>
public sealed record NetServerMinute(long Ts, string Exe, string Server, string Kind, int Sent, int? Ms, int? Max, int Lost);
