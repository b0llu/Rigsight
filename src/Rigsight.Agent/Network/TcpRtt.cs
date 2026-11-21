using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Rigsight.Agent.Network;

/// <summary>
/// The round-trip time Windows itself measures on an app's TCP connection (its extended statistics, path part): the
/// sum and count of the round trips it timed, read again each check, so the new ones give the check's figure. Nothing is
/// sent: the app's own traffic is what's timed. Collection is switched on per connection, which needs admin rights (the
/// agent has them where the trace runs at all). IPv4 only; an IPv6 server is checked the other ways.
/// </summary>
internal static class TcpRtt
{
    /// <summary>The round trips timed on a connection so far: their sum and count (ms), and its smoothed time.</summary>
    public readonly record struct Reading(uint SumMs, uint Count, uint SmoothedMs, ushort LocalPort);

    /// <summary>Connections whose collection is on, by local port and server: switched on once each.</summary>
    private static readonly HashSet<(ushort Local, IPEndPoint Server)> Collecting = [];
    private static readonly Lock CollectingLock = new();

    /// <summary>The established connection to <paramref name="server"/> owned by one of <paramref name="pids"/>, read; null with none or when it can't be.</summary>
    public static Reading? Read(IPEndPoint server, IReadOnlySet<int> pids)
    {
        if (server.AddressFamily != AddressFamily.InterNetwork) return null;
        if (Find(server, pids) is not { } row) return null;
        var key = ((ushort)IPAddress.NetworkToHostOrder((short)(row.LocalPort & 0xFFFF)), server);
        bool fresh;
        lock (CollectingLock) fresh = Collecting.Add(key);
        if (fresh)
        {
            byte enable = 1;
            uint set = SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsPath, ref enable, 0, 1, 0);
            if (set != 0) { lock (CollectingLock) Collecting.Remove(key); return null; }
        }
        var rod = new uint[RodFields];
        uint got = GetPerTcpConnectionEStats(ref row, TcpConnectionEstatsPath, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, rod, 0, (uint)(RodFields * 4));
        if (got != 0) return null;
        return new Reading(rod[SumRttField], rod[CountRttField], rod[SmoothedRttField], key.Item1);
    }

    /// <summary>Forgets the connections switched on that are gone (their ports are reused by others).</summary>
    public static void Forget(IPEndPoint server)
    {
        lock (CollectingLock) Collecting.RemoveWhere(c => c.Server.Equals(server));
    }

    private static MibTcpRow? Find(IPEndPoint server, IReadOnlySet<int> pids)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
        if (size <= 0) return null;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidConnections, 0) != 0) return null;
            int count = Marshal.ReadInt32(buffer);
            uint address = BitConverter.ToUInt32(server.Address.GetAddressBytes());
            uint port = (uint)(ushort)IPAddress.HostToNetworkOrder((short)server.Port);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (int i = 0; i < count; i++)
            {
                var r = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buffer + 4 + i * rowSize);
                if (r.State == Established && r.RemoteAddr == address && (r.RemotePort & 0xFFFF) == port && pids.Contains((int)r.OwningPid))
                    return new MibTcpRow { State = r.State, LocalAddr = r.LocalAddr, LocalPort = r.LocalPort, RemoteAddr = r.RemoteAddr, RemotePort = r.RemotePort };
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ── Windows' own (iphlpapi) ──

    private const int AfInet = 2, TcpTableOwnerPidConnections = 4;
    private const uint Established = 5;
    private const int TcpConnectionEstatsPath = 3;

    // TCP_ESTATS_PATH_ROD_v0: 40 ULONGs; SmoothedRtt, SumRtt and CountRtt at these places.
    private const int RodFields = 40, SmoothedRttField = 27, SumRttField = 31, CountRttField = 32;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid; }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcpConnectionEStats(ref MibTcpRow row, int estatsType, ref byte rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcpConnectionEStats(ref MibTcpRow row, int estatsType, IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize, [Out] uint[] rod, uint rodVersion, uint rodSize);
}
