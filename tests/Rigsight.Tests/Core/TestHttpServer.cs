using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Rigsight.Tests.Core;

/// <summary>
/// A tiny HTTP/1.1 server on the loopback address (a raw socket, so it needs no URL reservation or admin rights, and a
/// test controls every byte: a short body, a stall mid-download…). Each connection serves one request and closes.
/// </summary>
internal sealed class TestHttpServer : IDisposable
{
    public delegate Task Handler(string path, int request, Stream client, CancellationToken ct);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Handler _handler;
    private int _requests;

    public TestHttpServer(Handler handler, IPAddress? address = null)
    {
        _handler = handler;
        _listener = new TcpListener(address ?? IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoop();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Requests => Volatile.Read(ref _requests);
    public string Url(string path = "/") => $"http://127.0.0.1:{Port}{path}";

    /// <summary>Serves the same bytes to every request.</summary>
    public static TestHttpServer Serving(byte[] body) => new((_, _, client, ct) => Send(client, 200, body, ct: ct));

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await _listener.AcceptSocketAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Serve(socket));
        }
    }

    private async Task Serve(Socket socket)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            try
            {
                string path = await ReadRequestPath(stream);
                int n = Interlocked.Increment(ref _requests);
                await _handler(path, n, stream, _cts.Token);
                await stream.FlushAsync();
                socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // The client went away, or the server is stopping.
            }
        }
    }

    private static async Task<string> ReadRequestPath(Stream stream)
    {
        var head = new StringBuilder();
        var buffer = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n"))
        {
            if (await stream.ReadAsync(buffer) == 0) throw new IOException("closed");
            head.Append((char)buffer[0]);
        }
        return head.ToString().Split(' ')[1];
    }

    /// <summary>Writes a response. <paramref name="contentLength"/> can claim a different size than the body.</summary>
    public static async Task Send(Stream client, int status, byte[] body, long? contentLength = null, CancellationToken ct = default)
    {
        await SendHead(client, status, contentLength ?? body.Length, ct);
        await client.WriteAsync(body, ct);
    }

    public static async Task SendHead(Stream client, int status, long contentLength, CancellationToken ct = default)
    {
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Length: {contentLength}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n";
        await client.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await client.FlushAsync(ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
