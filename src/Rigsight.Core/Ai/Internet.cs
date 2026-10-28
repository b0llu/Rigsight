using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Rigsight.Core.Ai;

/// <summary>
/// Whether the PC is offline, so a question that can't go out says "no internet" at once instead of "isn't answering"
/// (people kept asking again without knowing why). Never used for Ollama on this PC, which needs no internet.
/// </summary>
public static class Internet
{
    public static string NoInternet(bool inSettings = false) => inSettings
        ? Lang.T("Your PC isn't connected to the internet. Once it is, press Connect again.")
        : Lang.T("Your PC isn't connected to the internet. Connect, then ask again.");

    /// <summary>Before sending: Windows has no network at all (Wi-Fi off, cable out, flight mode).</summary>
    public static bool IsOff()
    {
        try { return !NetworkInterface.GetIsNetworkAvailable(); }
        catch (NetworkInformationException) { return false; }
    }

    /// <summary>
    /// After a request failed: offline when there's no network, or when the service's name couldn't be looked up and
    /// neither can Windows' own connection-test address. A mistyped custom address alone isn't "no internet".
    /// </summary>
    public static async Task<bool> ExplainsAsync(Exception failure)
    {
        if (IsOff()) return true;
        if (failure is not HttpRequestException { InnerException: SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NetworkUnreachable or SocketError.NetworkDown } })
            return false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Dns.GetHostAddressesAsync("www.msftconnecttest.com", cts.Token);
            return false;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return true; }
    }
}
