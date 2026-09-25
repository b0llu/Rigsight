using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using Rigsight.Agent.Ipc;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>A client on a test pipe: reads the agent's lines, writes its own.</summary>
internal sealed class RawClient : IDisposable
{
    public NamedPipeClientStream Pipe { get; }
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private RawClient(NamedPipeClientStream pipe)
    {
        Pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    public static RawClient Connect(string pipeName, int timeoutMs = 5000)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(timeoutMs);
        return new RawClient(pipe);
    }

    public string? ReadLine(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        return _reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The other end has closed: reading ends the stream, or (depending on timing) finds the pipe broken.</summary>
    public bool IsClosed()
    {
        try { return ReadLine() is null; }
        catch (IOException) { return true; }
    }

    public AgentMessage Read(int timeoutMs = 5000) =>
        ProtocolJson.Deserialize<AgentMessage>(ReadLine(timeoutMs) ?? throw new EndOfStreamException("the agent closed the pipe"))!;

    /// <summary>Reads until a message matching <paramref name="match"/> (skipping others).</summary>
    public AgentMessage ReadUntil(Func<AgentMessage, bool> match, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var msg = Read(Math.Max(1, timeoutMs - (int)sw.ElapsedMilliseconds));
            if (match(msg)) return msg;
        }
    }

    public void WriteLine(string line) => _writer.WriteLine(line);

    public void Send(UiMessage msg) => WriteLine(ProtocolJson.Serialize(msg));

    public void Dispose()
    {
        try { _writer.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } // the agent already left
        _reader.Dispose();
        Pipe.Dispose();
    }
}

internal static class Wait
{
    public static bool For(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    public static string PipeName() => "Rigsight.Tests." + Guid.NewGuid().ToString("N");
}

/// <summary>The agent's pipe server, on a pipe of each test's own (never the real agent's).</summary>
public sealed class PipeServerTests : IDisposable
{
    private readonly string _name = Wait.PipeName();
    private readonly ConcurrentQueue<UiMessage> _received = new();
    private int _hellos;
    private readonly PipeServer _server;

    public PipeServerTests()
    {
        _server = new PipeServer(() => new AgentMessage { T = "hello", Version = $"hello-{Interlocked.Increment(ref _hellos)}" }, _received.Enqueue, _name);
        _server.Start();
    }

    public void Dispose() => _server.Dispose();

    private RawClient Connect() => RawClient.Connect(_name);

    private static AgentMessage Tick(int n, string? padding = null) => new() { T = "tick", Time = n, Arg = padding };

    [Fact]
    public void Each_client_is_greeted_with_its_own_hello_first()
    {
        using var a = Connect();
        var hello = a.Read();
        Assert.Equal("hello", hello.T);
        Assert.StartsWith("hello-", hello.Version);
        using var b = Connect();
        var second = b.Read();
        Assert.Equal("hello", second.T);
        Assert.NotEqual(hello.Version, second.Version);
    }

    [Fact]
    public void The_hello_comes_before_anything_broadcast()
    {
        using var a = Connect();
        Assert.True(Wait.For(() => _server.ClientCount == 1));
        _server.Broadcast(Tick(1));
        Assert.Equal("hello", a.Read().T);
        Assert.Equal(1, a.Read().Time);
    }

    [Fact]
    public void Broadcasts_reach_every_client_in_order()
    {
        var clients = Enumerable.Range(0, 5).Select(_ => Connect()).ToList();
        try
        {
            Assert.True(Wait.For(() => _server.ClientCount == 5), $"{_server.ClientCount} clients");
            foreach (var c in clients) Assert.Equal("hello", c.Read().T);
            for (int i = 1; i <= 20; i++) _server.Broadcast(Tick(i));
            foreach (var c in clients)
                Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), Enumerable.Range(0, 20).Select(_ => c.Read().Time));
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
        }
    }

    [Fact]
    public void Broadcasting_to_nobody_is_harmless()
    {
        for (int i = 0; i < 100; i++) _server.Broadcast(Tick(i));
        Assert.Equal(0, _server.ClientCount);
        using var a = Connect();
        Assert.Equal("hello", a.Read().T); // nothing sent before it connected is waiting for it
        _server.Broadcast(Tick(500));
        Assert.Equal(500, a.Read().Time);
    }

    [Fact]
    public void Messages_from_the_app_arrive_in_order()
    {
        using var a = Connect();
        a.Read();
        for (int i = 0; i < 50; i++) a.Send(new UiMessage { T = "cmd", Cmd = "pause", Arg = i.ToString() });
        a.Send(new UiMessage { T = "settings", Settings = new RigsightSettings { UseFahrenheit = true } });
        Assert.True(Wait.For(() => _received.Count == 51));
        var list = _received.ToList();
        Assert.Equal(Enumerable.Range(0, 50).Select(i => i.ToString()), list.Take(50).Select(m => m.Arg));
        Assert.True(list[50].Settings!.UseFahrenheit);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{\"T\":")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"T\":\"cmd\",\"Settings\":42}")]
    [InlineData("null")]
    public void A_bad_line_is_skipped_and_the_connection_stays_up(string line)
    {
        using var a = Connect();
        a.Read();
        a.WriteLine(line);
        a.Send(new UiMessage { T = "cmd", Cmd = "after" });
        Assert.True(Wait.For(() => _received.Any(m => m.Cmd == "after")));
        Assert.DoesNotContain(_received, m => m.Cmd != "after");
        Assert.Equal(1, _server.ClientCount);
        _server.Broadcast(Tick(7));
        Assert.Equal(7, a.Read().Time);
    }

    [Fact]
    public void A_failing_message_handler_doesnt_drop_the_app()
    {
        int calls = 0;
        string name = Wait.PipeName();
        using var server = new PipeServer(() => new AgentMessage { T = "hello" }, m =>
        {
            Interlocked.Increment(ref calls);
            if (m.Cmd == "boom") throw new InvalidOperationException("handler failed");
        }, name);
        server.Start();
        using var a = RawClient.Connect(name);
        a.Read();
        a.Send(new UiMessage { T = "cmd", Cmd = "boom" });
        a.Send(new UiMessage { T = "cmd", Cmd = "fine" });
        Assert.True(Wait.For(() => Volatile.Read(ref calls) == 2));
        Assert.Equal(1, server.ClientCount);
    }

    [Fact]
    public void Text_in_any_language_arrives_intact()
    {
        using var a = Connect();
        a.Read();
        const string name = "Ведьмак 3 · 原神 · Pokémon ✨ 🎮";
        _server.Broadcast(new AgentMessage { T = "navigate", Arg = name });
        Assert.Equal(name, a.Read().Arg);
        a.Send(new UiMessage { T = "cmd", Cmd = "x", Arg = name });
        Assert.True(Wait.For(() => _received.Any(m => m.Arg == name)));
    }

    [Fact]
    public void A_large_message_arrives_whole()
    {
        using var a = Connect();
        a.Read();
        string big = new('x', 3_000_000);
        _server.Broadcast(new AgentMessage { T = "big", Arg = big });
        _server.Broadcast(Tick(2));
        Assert.Equal(big, a.Read(15_000).Arg);
        Assert.Equal(2, a.Read().Time);
    }

    [Fact]
    public void An_app_that_leaves_is_forgotten()
    {
        var a = Connect();
        using var b = Connect();
        Assert.True(Wait.For(() => _server.ClientCount == 2));
        a.Dispose();
        Assert.True(Wait.For(() => _server.ClientCount == 1), $"{_server.ClientCount} clients");
        _server.Broadcast(Tick(3)); // doesn't trip over the one that left
        Assert.Equal("hello", b.Read().T);
        Assert.Equal(3, b.Read().Time);
        b.Dispose();
        Assert.True(Wait.For(() => _server.ClientCount == 0));
    }

    [Fact]
    public void Apps_come_and_go_many_times()
    {
        for (int i = 0; i < 30; i++)
        {
            using var c = Connect();
            Assert.Equal("hello", c.Read().T);
        }
        Assert.True(Wait.For(() => _server.ClientCount == 0));
        Assert.Equal(30, Volatile.Read(ref _hellos));
    }

    [Fact]
    public void An_app_that_never_reads_cant_hold_up_the_others()
    {
        using var stuck = Connect(); // connected, never reads
        using var live = Connect();
        Assert.True(Wait.For(() => _server.ClientCount == 2));
        Assert.Equal("hello", live.Read().T);

        string padding = new('p', 16 * 1024);
        var sw = Stopwatch.StartNew();
        for (int i = 1; i <= 3000; i++)
        {
            _server.Broadcast(Tick(i, padding));
            if (i % 100 == 0)
            {
                // The live app keeps up; drain it as a real app would.
                while (true)
                {
                    var m = live.Read();
                    if (m.Time == i) break;
                }
            }
        }
        // 3,000 broadcasts (48 MB for the stuck app) never wait for it.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"{sw.Elapsed.TotalSeconds:0.0} s");

        // What waits for the stuck app is bounded: 64 messages at most.
        Assert.All(Outboxes(_server), count => Assert.InRange(count, 0, 64));

        // When it finally reads, it gets the hello, then the newest messages (older ones dropped), in order.
        Assert.Equal("hello", stuck.Read().T);
        long last = 0;
        int got = 0;
        while (last != 3000)
        {
            var m = stuck.Read();
            Assert.True(m.Time > last, $"{m.Time} after {last}");
            last = m.Time;
            got++;
        }
        Assert.InRange(got, 1, 2999);
    }

    [Fact]
    public void Nine_apps_at_once_the_ninth_waits_for_a_free_slot()
    {
        var clients = Enumerable.Range(0, 8).Select(_ => Connect()).ToList();
        try
        {
            Assert.True(Wait.For(() => _server.ClientCount == 8));
            Assert.Throws<TimeoutException>(() => RawClient.Connect(_name, 500));
            clients[0].Dispose();
            using var ninth = RawClient.Connect(_name, 5000);
            Assert.Equal("hello", ninth.Read().T);
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
        }
    }

    [Fact]
    public void A_failing_hello_drops_that_app_but_serves_the_next()
    {
        int n = 0;
        string name = Wait.PipeName();
        using var server = new PipeServer(() => Interlocked.Increment(ref n) == 1 ? throw new InvalidOperationException("no hello") : new AgentMessage { T = "hello" }, _ => { }, name);
        server.Start();
        using (var first = RawClient.Connect(name))
            Assert.True(first.IsClosed());
        Assert.True(Wait.For(() => server.ClientCount == 0));
        using var second = RawClient.Connect(name);
        Assert.Equal("hello", second.Read().T);
    }

    [Fact]
    public void Dispose_closes_every_connection_and_stops_listening()
    {
        string name = Wait.PipeName();
        var server = new PipeServer(() => new AgentMessage { T = "hello" }, _ => { }, name);
        server.Start();
        using var a = RawClient.Connect(name);
        using var b = RawClient.Connect(name);
        a.Read();
        b.Read();
        server.Dispose();
        Assert.Equal(0, server.ClientCount);
        Assert.True(a.IsClosed());
        Assert.True(b.IsClosed());
        server.Broadcast(Tick(1));  // after Dispose: harmless
        server.Dispose();           // twice: harmless
        Assert.Throws<TimeoutException>(() => RawClient.Connect(name, 1000));
    }

    [Fact]
    public void Two_servers_on_different_pipes_dont_mix()
    {
        string other = Wait.PipeName();
        using var second = new PipeServer(() => new AgentMessage { T = "hello", Version = "second" }, _ => { }, other);
        second.Start();
        using var a = Connect();
        using var b = RawClient.Connect(other);
        Assert.StartsWith("hello-", a.Read().Version);
        Assert.Equal("second", b.Read().Version);
        second.Broadcast(Tick(9));
        _server.Broadcast(Tick(8));
        Assert.Equal(8, a.Read().Time);
        Assert.Equal(9, b.Read().Time);
    }

    /// <summary>How many messages wait in each client's queue.</summary>
    private static IEnumerable<int> Outboxes(PipeServer server)
    {
        var clients = (IList)typeof(PipeServer).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
        foreach (var c in clients.Cast<object>().ToArray())
        {
            var outbox = c.GetType().GetProperty("Outbox")!.GetValue(c)!;
            var reader = outbox.GetType().GetProperty("Reader")!.GetValue(outbox)!;
            yield return (int)reader.GetType().GetProperty("Count")!.GetValue(reader)!;
        }
    }
}
