using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows.Threading;
using Rigsight.Core;
using Rigsight.Core.Protocol;

namespace Rigsight.Services;

/// <summary>
/// Connects to the background agent's named pipe, keeps reconnecting if it goes away, and raises
/// messages on the UI thread.
/// </summary>
public sealed class AgentClient(Dispatcher dispatcher) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipe;

    public bool IsConnected { get; private set; }

    public event Action<AgentMessage>? MessageReceived;
    public event Action<bool>? ConnectionChanged;

    public void Start() => _ = Task.Run(Loop);

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", RigsightPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(1500, _cts.Token);
                _pipe = pipe;
                SetConnected(true);

                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null) break;
                    AgentMessage? msg;
                    try { msg = ProtocolJson.Deserialize<AgentMessage>(line); }
                    catch (Exception ex) { Log.Error("client", ex); continue; }
                    if (msg is not null)
                        _ = dispatcher.BeginInvoke(() => MessageReceived?.Invoke(msg));
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                // Agent not running (yet): retry below.
            }
            catch (Exception ex)
            {
                Log.Error("client", ex);
            }

            _pipe = null;
            SetConnected(false);
            try { await Task.Delay(1500, _cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;
        IsConnected = connected;
        _ = dispatcher.BeginInvoke(() => ConnectionChanged?.Invoke(connected));
    }

    /// <summary>Queues a message for the agent. Returns false when there's no connection to send it on.</summary>
    public bool Send(UiMessage message)
    {
        var pipe = _pipe;
        if (pipe is null) return false;
        _ = WriteAsync(pipe, Encoding.UTF8.GetBytes(ProtocolJson.Serialize(message) + "\n"));
        return true;
    }

    private async Task WriteAsync(Stream pipe, byte[] bytes)
    {
        await _writeLock.WaitAsync();
        try
        {
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Disconnected; the read loop will reconnect.
        }
        catch (Exception ex)
        {
            Log.Error("client", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void SendCommand(string cmd, string? arg = null) => Send(new UiMessage { T = "cmd", Cmd = cmd, Arg = arg });

    public void Dispose()
    {
        _cts.Cancel();
        _pipe?.Dispose();
    }
}
