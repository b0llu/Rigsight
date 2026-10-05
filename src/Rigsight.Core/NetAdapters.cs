using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Rigsight.Core;

/// <summary>The connection as it is now: how the PC reaches the internet, how fast its link is, and any VPN.</summary>
/// <param name="Kind">"Ethernet", "Wi-Fi", or the adapter's own name.</param>
/// <param name="Adapter">The network card's name ("Intel(R) I211 Gigabit Network Connection").</param>
/// <param name="LinkBitsPerSecond">How fast the card and router agreed to talk (not the internet's speed).</param>
/// <param name="Vpn">A VPN carrying the traffic ("Cloudflare WARP"), or null.</param>
public sealed record NetConnection(string Kind, string Adapter, long LinkBitsPerSecond, string? IPv4, string? Vpn);

/// <summary>This PC's network adapters, as Windows reports them (no admin rights needed).</summary>
public static unsafe class NetAdapters
{
    /// <summary>The connection the PC uses now (the card with a way out, a gateway), or null when nothing's connected.</summary>
    public static NetConnection? Current()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToList();
            var cards = up.Where(IsHardware).ToList();
            var main = cards.FirstOrDefault(HasGateway) ?? cards.FirstOrDefault();
            if (main is null) return null;
            string kind = main.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
                _ => main.Name,
            };
            var ipv4 = main.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            var vpn = up.Where(n => !IsHardware(n) && IsVpn(n)).Select(n => VpnName(n.Description)).FirstOrDefault();
            return new NetConnection(kind, main.Description, main.Speed, ipv4, vpn);
        }
        catch (Exception ex)
        {
            Log.Error("network", ex);
            return null;
        }
    }

    /// <summary>
    /// Bytes in and out through the PC's network cards since Windows started, by the cards' own counters (nothing to do
    /// with any trace); null when they can't be read.
    /// </summary>
    public static long? CardBytes()
    {
        try
        {
            long total = 0;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback || !IsHardware(n)) continue;
                var stats = n.GetIPStatistics();
                total += stats.BytesReceived + stats.BytesSent;
            }
            return total;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasGateway(NetworkInterface n)
    {
        try { return n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any)); }
        catch { return false; }
    }

    // A virtual adapter that is a VPN's tunnel, not one of Windows' own (Hyper-V's switch, WSL, the WAN miniports, Bluetooth).
    private static bool IsVpn(NetworkInterface n)
    {
        var d = n.Description;
        if (d.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) || d.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)
            || d.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) || d.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || d.Contains("VMware", StringComparison.OrdinalIgnoreCase) || n.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
            return false;
        try { return n.GetIPProperties().UnicastAddresses.Count > 0; }
        catch { return false; }
    }

    /// <summary>"Cloudflare WARP Interface Tunnel" → "Cloudflare WARP"; "WireGuard Tunnel" → "WireGuard".</summary>
    internal static string VpnName(string description)
    {
        var name = description;
        foreach (var tail in new[] { " Interface Tunnel", " Tunnel", " Virtual Ethernet Adapter", " Virtual Adapter", " Adapter", " Interface" })
            if (name.EndsWith(tail, StringComparison.OrdinalIgnoreCase) && name.Length > tail.Length) name = name[..^tail.Length];
        return name.Trim();
    }

    /// <summary>
    /// A network card in the PC (Windows' "hardware interface"), rather than a virtual adapter: a VPN's tunnel, Hyper-V's
    /// switch. Unknown counts as a card, so nothing is ever taken for a tunnel by mistake.
    /// </summary>
    public static bool IsHardware(NetworkInterface ni)
    {
        try
        {
            var ip = ni.GetIPProperties();
            int index = ni.Supports(NetworkInterfaceComponent.IPv4) ? ip.GetIPv4Properties().Index
                : ni.Supports(NetworkInterfaceComponent.IPv6) ? ip.GetIPv6Properties().Index : -1;
            if (index < 0) return true;
            byte* row = stackalloc byte[IfRowSize];
            new Span<byte>(row, IfRowSize).Clear();
            *(uint*)(row + 8) = (uint)index;                 // InterfaceIndex
            if (GetIfEntry2(row) != 0) return true;
            return (row[1152] & 1) != 0;                     // InterfaceAndOperStatusFlags.HardwareInterface
        }
        catch
        {
            return true;
        }
    }

    private const int IfRowSize = 1352; // MIB_IF_ROW2

    /// <summary>
    /// Whether Windows sees the internet right now (what the taskbar's network icon shows): true, false, or null when it
    /// can't say (or the PC runs a Windows 10 older than 2004, which can't be asked).
    /// </summary>
    public static bool? WindowsSeesInternet()
    {
        if (!_hintAvailable) return null;
        try
        {
            if (GetNetworkConnectivityHint(out var hint) != 0) return null;
            return hint.Level switch
            {
                3 => true,          // internet access
                1 or 2 => false,    // none, or the local network only
                _ => null,          // unknown, captive portal or limited, hidden
            };
        }
        catch (EntryPointNotFoundException)
        {
            _hintAvailable = false;
            return null;
        }
    }

    private static bool _hintAvailable = true;

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectivityHint
    {
        public int Level, Cost;
        public byte ApproachingDataLimit, OverDataLimit, Roaming;
    }

    [DllImport("iphlpapi.dll")] private static extern int GetNetworkConnectivityHint(out ConnectivityHint hint);

    [DllImport("iphlpapi.dll")] private static extern int GetIfEntry2(byte* row);
}
