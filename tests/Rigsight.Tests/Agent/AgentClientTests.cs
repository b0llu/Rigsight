using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Rigsight.Agent.Ipc;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Agent;

/// <summary>The app's side of the pipe against the agent's real server: connecting, messages on the UI thread, reconnecting.</summary>
[Collection("UI")]
public sealed class AgentClientTests : IDisposable
{
    private readonly string _name = Wait.PipeName();
    private readonly ConcurrentQueue<UiMessage> _atAgent = new();
    private readonly ConcurrentQueue<AgentMessage> _atApp = new();
    private readonly ConcurrentQueue<bool> _changes = new();
    private readonly List<IDisposable> _cleanup = [];
    private int _offUiThread;
    private int _hellos;

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_cleanup)) d.Dispose();
    }

    private PipeServer StartServer()
    {
        var server = new PipeServer(() => new AgentMessage { T = "hello", Version = $"v{Interlocked.Increment(ref _hellos)}" }, _atAgent.Enqueue, _name);
        server.Start();
        _cleanup.Add(server);
        return server;
    }

    private AgentClient StartClient()
    {
        var client = new AgentClient(Ui.Dispatcher, _name);
        client.MessageReceived += m =>
        {
            if (!Ui.Dispatcher.CheckAccess()) Interlocked.Increment(ref _offUiThread);
            _atApp.Enqueue(m);
        };
        client.ConnectionChanged += c =>
        {
            if (!Ui.Dispatcher.CheckAccess()) Interlocked.Increment(ref _offUiThread);
            _changes.Enqueue(c);
        };
        client.Start();
        _cleanup.Add(client);
        return client;
    }

    [Fact]
    public void Connects_and_gets_the_hello_on_the_UI_thread()
    {
        var server = StartServer();
        var client = StartClient();
        Assert.True(Ui.WaitFor(() => _atApp.Any(m => m.T == "hello")), "no hello");
        Assert.True(client.IsConnected);
        Assert.Equal([true], _changes);
        Assert.Equal(1, server.ClientCount);
        Assert.Equal(0, _offUiThread);
    }

    [Fact]
    public void Broadcasts_arrive_in_order()
    {
        var server = StartServer();
        StartClient();
        Assert.True(Ui.WaitFor(() => _atApp.Any(m => m.T == "hello")));
        // Faster than the app reads, the agent drops the oldest: whatever arrives is in order and ends with the newest.
        for (int i = 1; i <= 100; i++) server.Broadcast(new AgentMessage { T = "tick", Time = i });
        Assert.True(Ui.WaitFor(() => _atApp.Any(m => m.Time == 100)));
        var times = _atApp.Where(m => m.T == "tick").Select(m => m.Time).ToList();
        Assert.Equal(times.Order(), times);
        Assert.Equal(times.Count, times.Distinct().Count());
        Assert.True(times.Count >= 64, $"{times.Count} ticks");

        // At a real pace (once a second, or even 50 a second) nothing is lost.
        _atApp.Clear();
        for (int i = 1; i <= 50; i++)
        {
            server.Broadcast(new AgentMessage { T = "tick", Time = 1000 + i });
            Thread.Sleep(20);
        }
        Assert.True(Ui.WaitFor(() => _atApp.Count == 50));
        Assert.Equal(Enumerable.Range(1001, 50).Select(i => (long)i), _atApp.Select(m => m.Time));
        Assert.Equal(0, _offUiThread);
    }

    [Fact]
    public void Sending_needs_a_connection()
    {
        var client = new AgentClient(Ui.Dispatcher, _name);
        _cleanup.Add(client);
        Assert.False(client.IsConnected);
        Assert.False(client.Send(new UiMessage { T = "cmd", Cmd = "pause" }));
        client.SendCommand("pause"); // quietly nothing
    }

    [Fact]
    public void Sent_messages_and_commands_reach_the_agent()
    {
        StartServer();
        var client = StartClient();
        Assert.True(Ui.WaitFor(() => client.IsConnected));
        Assert.True(client.Send(new UiMessage { T = "settings", Settings = new RigsightSettings { Theme = "light" } }));
        client.SendCommand("pause", "30");
        client.SendCommand("resume");
        Assert.True(Wait.For(() => _atAgent.Count == 3));
        var list = _atAgent.ToList();
        Assert.Equal("light", list[0].Settings!.Theme);
        Assert.Equal(("cmd", "pause", "30"), (list[1].T, list[1].Cmd, list[1].Arg));
        Assert.Equal(("cmd", "resume", (string?)null), (list[2].T, list[2].Cmd, list[2].Arg));
    }

    [Fact]
    public void Many_sends_from_many_threads_arrive_whole()
    {
        StartServer();
        var client = StartClient();
        Assert.True(Ui.WaitFor(() => client.IsConnected));
        Parallel.For(0, 400, i => client.SendCommand("n", i.ToString()));
        Assert.True(Wait.For(() => _atAgent.Count == 400), $"{_atAgent.Count} arrived");
        Assert.Equal(Enumerable.Range(0, 400).Select(i => i.ToString()).Order(), _atAgent.Select(m => m.Arg!).Order());
    }

    [Fact]
    public void Waits_for_an_agent_that_starts_later()
    {
        var client = StartClient();
        Thread.Sleep(2000);
        Assert.False(client.IsConnected);
        StartServer();
        Assert.True(Ui.WaitFor(() => client.IsConnected && _atApp.Any(m => m.T == "hello"), 10_000));
    }

    [Fact]
    public void Reconnects_after_the_agent_restarts()
    {
        var first = StartServer();
        var client = StartClient();
        Assert.True(Ui.WaitFor(() => _atApp.Count(m => m.T == "hello") == 1));

        first.Dispose();
        Assert.True(Ui.WaitFor(() => !client.IsConnected), "didn't notice the agent left");
        Assert.True(Ui.WaitFor(() => _changes.Count == 2));
        Assert.Equal([true, false], _changes);
        Assert.False(client.Send(new UiMessage { T = "cmd", Cmd = "x" }));

        var second = StartServer();
        Assert.True(Ui.WaitFor(() => client.IsConnected && _atApp.Count(m => m.T == "hello") == 2, 10_000), "didn't reconnect");
        Assert.Equal("v2", _atApp.Last(m => m.T == "hello").Version);
        Assert.Equal([true, false, true], _changes);
        Assert.True(client.Send(new UiMessage { T = "cmd", Cmd = "back" }));
        Assert.True(Wait.For(() => _atAgent.Any(m => m.Cmd == "back")));
        Assert.Equal(1, second.ClientCount);
    }

    [Fact]
    public void A_bad_line_from_the_agent_is_skipped()
    {
        using var server = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = StartClient();
        server.WaitForConnection();
        var bytes = Encoding.UTF8.GetBytes("garbage\n{\"T\":\n\n" + ProtocolJson.Serialize(new AgentMessage { T = "hello", Version = "ok" }) + "\n");
        server.Write(bytes);
        server.Flush();
        Assert.True(Ui.WaitFor(() => _atApp.Any(m => m.Version == "ok")));
        Assert.Single(_atApp);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public void Disposed_it_stops_reconnecting()
    {
        var client = StartClient();
        client.Dispose();
        var server = StartServer();
        Thread.Sleep(3500);
        Assert.Equal(0, server.ClientCount);
        Assert.Empty(_atApp);
    }

    [Fact]
    public void Disposed_while_connected_the_agent_sees_it_leave()
    {
        var server = StartServer();
        var client = StartClient();
        Assert.True(Wait.For(() => server.ClientCount == 1));
        client.Dispose();
        Assert.True(Wait.For(() => server.ClientCount == 0));
    }
}
