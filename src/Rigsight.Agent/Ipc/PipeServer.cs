using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using Rigsight.Core;
using Rigsight.Core.Protocol;

namespace Rigsight.Agent.Ipc;

/// <summary>
/// Serves live data to the Rigsight app over a named pipe (one JSON message per line). The pipe is
/// restricted to the current user, and each client gets its own bounded send queue so a slow
/// client can never stall the agent.
/// </summary>
/// <param name="pipeName">The pipe to serve on: <see cref="RigsightPaths.PipeName"/>, or a test's own.</param>
internal sealed class PipeServer(Func<AgentMessage> buildHello, Action<UiMessage> onMessage, string? pipeName = null) : IDisposable
{
    private sealed class Client(NamedPipeServerStream pipe)
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        public Channel<byte[]> Outbox { get; } = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    }

    private readonly CancellationTokenSource _cts = new();
    private readonly List<Client> _clients = [];
    private readonly Lock _lock = new();
    // The instance waiting for the next app, closed straight away by Dispose (cancelling the wait alone leaves it
    // open for a moment, when an app could still connect to an agent that's shutting down).
    private NamedPipeServerStream? _listening;

    public int ClientCount
    {
        get { lock (_lock) return _clients.Count; }
    }

    public void Start() => _ = Task.Run(AcceptLoop);

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(pipeName ?? RigsightPaths.PipeName, PipeDirection.InOut, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreateSecurity());
                Volatile.Write(ref _listening, pipe);
                if (_cts.IsCancellationRequested) throw new OperationCanceledException();
                await pipe.WaitForConnectionAsync(_cts.Token);
                Volatile.Write(ref _listening, null);
                var client = new Client(pipe);
                lock (_lock)
                {
                    // Connected just as Dispose ran (it has already closed the others): close this one too.
                    if (_cts.IsCancellationRequested) throw new OperationCanceledException();
                    _clients.Add(client);
                }
                _ = Task.Run(() => RunClient(client));
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                Log.Error("pipe", ex);
                await Task.Delay(1000);
            }
        }
    }

    private static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private async Task RunClient(Client client)
    {
        var writer = Task.Run(() => WriteLoop(client));
        try
        {
            Enqueue(client, buildHello());
            using var reader = new StreamReader(client.Pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (!_cts.IsCancellationRequested && client.Pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                try
                {
                    if (ProtocolJson.Deserialize<UiMessage>(line) is { } msg) onMessage(msg);
                }
                catch (Exception ex)
                {
                    Log.Error("pipe", ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Client went away.
        }
        finally
        {
            lock (_lock) _clients.Remove(client);
            client.Outbox.Writer.TryComplete();
            try { client.Pipe.Dispose(); } catch { }
            await writer;
        }
    }

    private static async Task WriteLoop(Client client)
    {
        try
        {
            await foreach (var bytes in client.Outbox.Reader.ReadAllAsync())
            {
                await client.Pipe.WriteAsync(bytes);
                await client.Pipe.FlushAsync();
            }
        }
        catch
        {
            // Broken pipe: RunClient cleans up.
        }
    }

    private static void Enqueue(Client client, AgentMessage message) =>
        client.Outbox.Writer.TryWrite(Encode(message));

    private static byte[] Encode(AgentMessage message) =>
        Encoding.UTF8.GetBytes(ProtocolJson.Serialize(message) + "\n");

    public void Broadcast(AgentMessage message)
    {
        Client[] clients;
        lock (_lock)
        {
            if (_clients.Count == 0) return;
            clients = [.. _clients];
        }
        var bytes = Encode(message);
        foreach (var c in clients) c.Outbox.Writer.TryWrite(bytes);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Volatile.Read(ref _listening)?.Dispose(); } catch { }
        lock (_lock)
        {
            foreach (var c in _clients)
            {
                c.Outbox.Writer.TryComplete();
                try { c.Pipe.Dispose(); } catch { }
            }
            _clients.Clear();
        }
    }
}
