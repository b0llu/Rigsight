using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Rigsight.Core;

namespace Rigsight.Agent.Network;

/// <summary>Where a packet went: nowhere (this PC to itself), the local network, or the internet directly or through a tunnel.</summary>
internal enum Route { Self, Lan, Internet, Tunnel }

/// <summary>
/// This PC's own addresses, and which of them sit on a virtual adapter (a VPN's tunnel). Built from the adapters as they
/// are when made: <see cref="AddressBook.Read"/> again when they change. Read on the trace's thread, so never changed
/// after it's made.
/// </summary>
internal sealed unsafe class AddressBook
{
    private readonly HashSet<uint> _own4 = [], _tunnel4 = [];
    private readonly HashSet<(ulong, ulong)> _own6 = [], _tunnel6 = [];

    public static readonly AddressBook Empty = new();

    /// <summary>Whether a network card (not a virtual adapter) is connected: no card up is a cable or Wi-Fi drop.</summary>
    public bool CardConnected { get; private init; }

    /// <summary>The adapters as they are now.</summary>
    public static AddressBook Read()
    {
        var book = new AddressBook();
        bool card = false;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                bool hardware = NetAdapters.IsHardware(ni);
                if (hardware && ni.OperationalStatus == OperationalStatus.Up) card = true;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    book.Add(a.Address, tunnel: !hardware);
            }
        }
        catch (Exception ex)
        {
            Log.Error("network", ex);
            card = true; // unknown: never report a drop from a failed read
        }
        return new AddressBook(book) { CardConnected = card };
    }

    private AddressBook() { }

    private AddressBook(AddressBook from)
    {
        _own4 = from._own4;
        _tunnel4 = from._tunnel4;
        _own6 = from._own6;
        _tunnel6 = from._tunnel6;
    }

    /// <summary>For tests: a book with these addresses (the tunnel ones on a virtual adapter).</summary>
    internal static AddressBook Of(IEnumerable<string> own, IEnumerable<string>? tunnel = null)
    {
        var book = new AddressBook { CardConnected = true };
        foreach (var a in own) book.Add(IPAddress.Parse(a), tunnel: false);
        foreach (var a in tunnel ?? []) book.Add(IPAddress.Parse(a), tunnel: true);
        return book;
    }

    private void Add(IPAddress address, bool tunnel)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            uint a = BitConverter.ToUInt32(bytes); // as the events carry it: network order, read on a little-endian PC
            _own4.Add(a);
            if (tunnel) _tunnel4.Add(a);
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var key = (BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8));
            _own6.Add(key);
            if (tunnel) _tunnel6.Add(key);
        }
    }

    // ── Classifying (the trace's thread) ─────────────────────────────────

    /// <summary>An IPv4 packet's route from its two addresses (either may be this PC's: the events don't say which).</summary>
    public Route Classify4(uint a, uint b)
    {
        bool aOwn = _own4.Contains(a) || IsLoopback4(a), bOwn = _own4.Contains(b) || IsLoopback4(b);
        if (aOwn && bOwn) return Route.Self;
        uint local = aOwn ? a : b, remote = aOwn ? b : a;
        if (IsLocal4(remote)) return Route.Lan;
        return _tunnel4.Contains(local) ? Route.Tunnel : Route.Internet;
    }

    public Route Classify6(byte* a, byte* b)
    {
        bool aOwn = Own6(a), bOwn = Own6(b);
        if (aOwn && bOwn) return Route.Self;
        byte* local = aOwn ? a : b, remote = aOwn ? b : a;
        // An IPv4 address in IPv6 form (::ffff:a.b.c.d) is judged as IPv4.
        if (IsMapped4(remote) ? IsLocal4(*(uint*)(remote + 12)) : IsLocal6(remote)) return Route.Lan;
        bool tunnel = IsMapped4(local) ? _tunnel4.Contains(*(uint*)(local + 12)) : _tunnel6.Contains((*(ulong*)local, *(ulong*)(local + 8)));
        return tunnel ? Route.Tunnel : Route.Internet;
    }

    private bool Own6(byte* p) => IsMapped4(p)
        ? _own4.Contains(*(uint*)(p + 12)) || IsLoopback4(*(uint*)(p + 12))
        : _own6.Contains((*(ulong*)p, *(ulong*)(p + 8))) || IsLoopback6(p);

    private static bool IsLoopback4(uint a) => (byte)a == 127;

    /// <summary>Private ranges (10/8, 172.16/12, 192.168/16), link-local, multicast and broadcast: not the internet.</summary>
    internal static bool IsLocal4(uint a)
    {
        byte b0 = (byte)a, b1 = (byte)(a >> 8);
        return a == 0 || b0 == 10 || b0 == 127 || b0 == 192 && b1 == 168 || b0 == 172 && b1 is >= 16 and < 32
               || b0 == 169 && b1 == 254 || b0 >= 224;
    }

    private static bool IsLoopback6(byte* p)
    {
        for (int i = 0; i < 15; i++) if (p[i] != 0) return false;
        return p[15] is 0 or 1;
    }

    private static bool IsMapped4(byte* p)
    {
        for (int i = 0; i < 10; i++) if (p[i] != 0) return false;
        return p[10] == 0xff && p[11] == 0xff;
    }

    /// <summary>Link-local (fe80::/10), unique local (fc00::/7) and multicast (ff00::/8).</summary>
    private static bool IsLocal6(byte* p) => p[0] == 0xfe && (p[1] & 0xc0) == 0x80 || (p[0] & 0xfe) == 0xfc || p[0] == 0xff;
}
