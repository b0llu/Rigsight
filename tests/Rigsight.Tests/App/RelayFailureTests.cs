using System.Net;
using System.Net.Sockets;
using System.Text;
using Rigsight.Core.Ai;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.App;

/// <summary>
/// RigSight AI when the relay misbehaves: refused, reset, cut off mid-answer, silent, nonsense, odd JSON, errors in the
/// stream, endless look-ups, a broken web search. Each case says what the person sees, and none may throw.
/// A fake relay on a local port answers as each test says, down to closing or resetting the connection.
/// </summary>
public sealed class RelayFailureTests
{
    private static readonly string NoHistory = Path.Combine(Path.GetTempPath(), "rigsight-no-such.db");
    private static AiTools Tools() => new(new RigsightSettings(), NoHistory);

    // ── Questions left ────────────────────────────────────────────────────
    [Fact]
    public async Task A_relay_that_refuses_the_connection_is_not_answering()
    {
        using var _ = Point("http://127.0.0.1:9");
        var (status, problem) = await Relay.StatusAsync();
        Assert.Null(status);
        // Only checking: no question was asked, so nothing about one being counted.
        Assert.Equal("Couldn't check your questions right now. Try again in a minute.", problem);
    }

    [Theory]
    [InlineData(500, "text/html", "<html>Internal error</html>")]
    [InlineData(502, "text/html", "<html>Bad gateway</html>")]
    [InlineData(200, "text/html", "<html>Just a moment…</html>")]
    [InlineData(200, "application/json", "")]
    [InlineData(200, "application/json", "null")]
    [InlineData(200, "application/json", "[]")]
    [InlineData(200, "application/json", "{\"questions_left\":\"lots\"}")]
    [InlineData(200, "application/json", "{\"questions_left\":1.5,\"licence\":\"yes\",\"packs\":{}}")]
    public async Task Questions_left_from_a_nonsense_answer_is_a_problem_not_a_crash(int code, string type, string body)
    {
        await using var relay = new FakeRelay((_, s) => FakeRelay.Reply(s, code, type, body));
        using var _ = Point(relay.Url);
        var (status, problem) = await Relay.StatusAsync();
        // Either it read something sensible, or it says what went wrong; never a crash and never both empty.
        Assert.True(status is not null || problem is not null, $"no status and no problem for {code} {body}");
    }

    [Fact]
    public async Task Questions_left_cut_off_halfway_is_a_problem()
    {
        await using var relay = new FakeRelay(async (_, s) =>
        {
            await FakeRelay.Write(s, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 400\r\n\r\n{\"questions_left\":");
            FakeRelay.Reset(s);
        });
        using var _ = Point(relay.Url);
        var (status, problem) = await Relay.StatusAsync();
        Assert.Null(status);
        Assert.NotNull(problem);
    }

    // ── Activating a key ──────────────────────────────────────────────────
    [Theory]
    [InlineData(200, "null")]
    [InlineData(200, "<html>ok</html>")]
    [InlineData(404, "<html>Not found</html>")]
    [InlineData(400, "{\"error\":\"bad_request\"}")]
    [InlineData(403, "{\"error\":\"licence\",\"reason\":\"something_new\"}")]
    [InlineData(413, "")]
    public async Task Activating_against_a_strange_answer_always_says_something(int code, string body)
    {
        await using var relay = new FakeRelay((_, s) => FakeRelay.Reply(s, code, "application/json", body));
        using var _ = Point(relay.Url);
        var (status, problem, _) = await Relay.ActivateAsync("RS-AAAA-BBBB-CCCC");
        Assert.True(status is not null || problem is not null, $"Activate showed nothing for {code} {body}");
    }

    [Fact]
    public async Task A_huge_key_is_sent_and_refused_without_trouble()
    {
        await using var relay = new FakeRelay((r, s) => FakeRelay.Reply(s, 403, "application/json", """{"error":"licence","reason":"not_found"}"""));
        using var _ = Point(relay.Url);
        var (status, problem, reason) = await Relay.ActivateAsync(new string('X', 100_000));
        Assert.Null(status);
        Assert.Equal("not_found", reason);
        Assert.Contains("wasn't found", problem);
    }

    // ── Asking ────────────────────────────────────────────────────────────
    [Fact]
    public async Task An_answer_cut_off_before_its_end_is_not_shown_as_the_answer()
    {
        await using var relay = new FakeRelay(async (_, s) =>
        {
            await FakeRelay.SseStart(s);
            await FakeRelay.Write(s, Words("Your PC shut off because the power supply"));
            s.Close(); // no finish, no [DONE]: the connection just ends
        });
        using var _ = Point(relay.Url);
        var reply = await Relay.AskAsync([], "Why did my PC turn off?", Tools());
        Assert.Null(reply.Text);
        // The relay may have counted it (the app stopped it, not the relay), so it never says it wasn't.
        Assert.Contains("stopped before it finished", reply.Problem);
    }

    // 2026-10-27: a round that thought until its limit is asked again by the relay without thinking; the wait says so.
    [Fact]
    public async Task When_the_relay_asks_again_without_thinking_the_wait_says_its_writing_and_the_answer_comes()
    {
        await using var relay = new FakeRelay(async (_, s) =>
        {
            await FakeRelay.SseStart(s);
            await FakeRelay.Write(s, "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n");
            await FakeRelay.Write(s, "data: {\"rigsight\":{\"step\":\"writing\"}}\n\n");
            await FakeRelay.Write(s, Words("Your PC shut off 18 times this month."));
            await FakeRelay.Write(s, "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
            s.Close();
        });
        using var _ = Point(relay.Url);
        var steps = new List<string>();
        var reply = await Relay.AskAsync([], "How was my PC last month?", Tools(), step: st => steps.Add(st.Text));
        Assert.Equal("Your PC shut off 18 times this month.", reply.Text);
        Assert.Contains("Writing your answer…", steps);
    }

    // 2026-10-27: a fifth round refused for size said "wasn't counted", but the relay keeps a question it already answered rounds of.
    [Theory]
    [InlineData(400, """{"error":"unavailable","reason":"bad_request","detail":"too long"}""")]
    [InlineData(503, """{"error":"unavailable","reason":"stopped","detail":"too long"}""")]
    public async Task A_round_refused_for_size_after_the_first_never_says_it_wasnt_counted(int code, string body)
    {
        int asked = 0;
        await using var relay = new FakeRelay(async (_, s) =>
        {
            if (Interlocked.Increment(ref asked) > 1) { await FakeRelay.Reply(s, code, "application/json", body); return; }
            await FakeRelay.SseStart(s);
            await FakeRelay.Write(s, "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"get_today\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n");
            s.Close();
        });
        using var _ = Point(relay.Url);
        var reply = await Relay.AskAsync([], "Compare every month this year", Tools());
        Assert.Null(reply.Text);
        Assert.Equal("This answer grew too big to finish, so RigSight AI stopped it. Try asking about one part at a time.", reply.Problem);
    }

    [Fact]
    public async Task An_answer_reset_halfway_says_so()
    {
        await using var relay = new FakeRelay(async (_, s) =>
        {
            await FakeRelay.SseStart(s);
            await FakeRelay.Write(s, Words("Half an answer"));
            FakeRelay.Reset(s);
        });
        using var _ = Point(relay.Url);
        var reply = await Relay.AskAsync([], "Why?", Tools());
        Assert.Null(reply.Text);
        Assert.NotNull(reply.Problem);
    }

    [Fact]
    public async Task An_error_inside_the_stream_says_the_question_wasnt_counted()
    {
        await using var relay = new FakeRelay(async (_, s) =>
        {
            await FakeRelay.SseStart(s);
            await FakeRelay.Write(s, Words("Starting to"));
            await FakeRelay.Write(s, "data: {\"error\":{\"message\":\"upstream failed\"}}\n\n");
            s.Close();
        });
        using var _ = Point(relay.Url);
        var reply = await Relay.AskAsync([], "Why?", Tools());
        Assert.Null(reply.Text);
        Assert.Contains("wasn't counted", reply.Problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("data: [DONE]\n\n")]
    [InlineData(": processing\n\n: processing\n\ndata: [DONE]\n\n")]
    [InlineData("<html>Just a moment…</html>")]
    public async Task An_empty_or_strange_stream_is_a_problem(string stream)
    {
        await using var relay = new FakeRelay(async (_, s) => { await FakeRelay.SseStart(s); await FakeRelay.Write(s, stream); s.Close(); });
        using var _ = Point(relay.Url);
        var reply = await Relay.AskAsync([], "Why?", Tools());
        Assert.Null(reply.Text);
        Assert.NotNull(reply.Problem);
    }

    [Fact]
    public async Task Garbage_lines_in_the_stream_are_skipped()
    {
        await using var relay = new FakeRelay(async (_, s) =>
        {
            await FakeRelay.SseStart(s);
            await FakeRelay.Write(s, "data: {not json\n\nevent: ping\n\n" + Words("All fine.") + "data: {\"choices\":[]}\n\n" + Finish() + "data: [DONE]\n\n");
            s.Close();
        });
        using var _ = Point(relay.Url);
        var reply = await Relay.AskAsync([], "Is it fine?", Tools());
        Assert.Equal("All fine.", reply.Text);
    }

    [Fact]
    public async Task A_relay_that_goes_quiet_mid_answer_gives_up_by_itself()
    {
        var was = Relay.QuietLimit;
        Relay.QuietLimit = TimeSpan.FromSeconds(2);
        try
        {
            await using var relay = new FakeRelay(async (_, s) =>
            {
                await FakeRelay.SseStart(s);
                await FakeRelay.Write(s, Words("Thinking about"));
                await Task.Delay(TimeSpan.FromSeconds(30)); // says nothing more, keeps the connection open
            });
            using var _ = Point(relay.Url);
            var ask = Relay.AskAsync([], "Why?", Tools());
            var first = await Task.WhenAny(ask, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(ask, first);
            Assert.Null(ask.Result.Text);
            Assert.NotNull(ask.Result.Problem);
        }
        finally { Relay.QuietLimit = was; }
    }

    [Fact]
    public async Task Keep_alive_comments_keep_a_slow_answer_going()
    {
        var was = Relay.QuietLimit;
        Relay.QuietLimit = TimeSpan.FromSeconds(2);
        try
        {
            await using var relay = new FakeRelay(async (_, s) =>
            {
                await FakeRelay.SseStart(s);
                for (int i = 0; i < 4; i++) { await FakeRelay.Write(s, ": processing\n\n"); await Task.Delay(1000); }
                await FakeRelay.Write(s, Words("Slow but sure.") + Finish() + "data: [DONE]\n\n");
                s.Close();
            });
            using var _ = Point(relay.Url);
            var reply = await Relay.AskAsync([], "Why?", Tools());
            Assert.Equal("Slow but sure.", reply.Text);
        }
        finally { Relay.QuietLimit = was; }
    }

    [Theory]
    [InlineData(402, """{"error":"out_of_questions","reason":"free"}""", "used its free questions")]
    [InlineData(403, """{"error":"licence","reason":"refunded"}""", "refunded")]
    [InlineData(426, """{"error":"update_needed"}""", "latest RigSight")]
    [InlineData(503, """{"error":"unavailable","reason":"busy"}""", "busy")]
    [InlineData(400, """{"error":"unavailable","reason":"bad_request"}""", "wasn't counted")]
    [InlineData(429, "<html>Too many requests</html>", "couldn't answer")]
    public async Task A_refusal_on_a_later_round_still_says_why(int code, string body, string says)
    {
        int round = 0;
        await using var relay = new FakeRelay(async (r, s) =>
        {
            if (r.Path == "/v1/ask" && round++ == 0) { await FakeRelay.SseStart(s); await FakeRelay.Write(s, Call("get_crashes", "{}") + Finish("tool_calls") + "data: [DONE]\n\n"); s.Close(); }
            else await FakeRelay.Reply(s, code, "application/json", body);
        });
        using var _ = Point(relay.Url);
        var reply = await Relay.AskAsync([], "Why?", Tools());
        Assert.Null(reply.Text);
        Assert.Contains(says, reply.Problem);
    }

    [Theory]
    [InlineData("get_crashes", "{not json")]
    [InlineData("get_passwords", "{}")]
    [InlineData("", "")]
    public async Task A_broken_or_unknown_look_up_doesnt_stop_the_answer(string name, string args)
    {
        int round = 0;
        await using var relay = new FakeRelay(async (r, s) =>
        {
            await FakeRelay.SseStart(s);
            await FakeRelay.Write(s, round++ == 0 ? Call(name, args) + Finish("tool_calls") + "data: [DONE]\n\n" : Words("Done.") + Finish() + "data: [DONE]\n\n");
            s.Close();
        });
        using var _ = Point(relay.Url);
        var reply = await Relay.AskAsync([], "Why?", Tools());
        Assert.Equal("Done.", reply.Text);
    }

    [Fact]
    public async Task Look_ups_without_end_stop_after_the_last_round()
    {
        int asks = 0;
        await using var relay = new FakeRelay(async (r, s) =>
        {
            asks++;
            await FakeRelay.SseStart(s);
            await FakeRelay.Write(s, Call("get_crashes", "{}") + Finish("tool_calls") + "data: [DONE]\n\n");
            s.Close();
        });
        using var _ = Point(relay.Url);
        var reply = await Relay.AskAsync([], "Why?", Tools());
        Assert.Null(reply.Text);
        // The real relay answers on its last round (MAX_ROUNDS) instead; only a relay that never does ends here.
        Assert.Equal("RigSight AI couldn't finish this answer. Ask again, or ask about one part of it.", reply.Problem);
        Assert.Equal(12, asks);
    }

    // "Needed too many steps" meant nothing to someone who asked a simple question (2026-10-27): own key's last round
    // allows no look-ups, so the model answers from what it found.
    [Fact]
    public async Task Own_keys_last_round_allows_no_look_ups_and_answers()
    {
        var bodies = new List<string>();
        await using var server = new FakeRelay(async (r, s) =>
        {
            lock (bodies) bodies.Add(r.Body);
            bool last = r.Body.Contains("\"tool_choice\":\"none\"");
            await FakeRelay.SseStart(s);
            await FakeRelay.Write(s, (last ? Words("From what I found: 3 shut-offs.") + Finish() : Call("get_crashes", "{}") + Finish("tool_calls")) + "data: [DONE]\n\n");
            s.Close();
        });
        var reply = await OwnKey.AskAsync("Test", server.Url, "key", "model", [], "Why?", Tools());
        Assert.Equal("From what I found: 3 shut-offs.", reply.Text);
        Assert.Equal(OwnKey.MaxRounds, bodies.Count);
        Assert.Contains(OwnKey.LastRound[..40], bodies[^1]);
        Assert.DoesNotContain("tool_choice", bodies[0]);
    }

    [Theory]
    [InlineData(500, "<html>error</html>")]
    [InlineData(200, "not json")]
    [InlineData(200, """{"text":"Error: search failed","failed":true}""")]
    [InlineData(0, "")] // reset
    public async Task A_broken_web_search_still_answers_from_the_records(int code, string body)
    {
        int round = 0;
        await using var relay = new FakeRelay(async (r, s) =>
        {
            if (r.Path == "/v1/search")
            {
                if (code == 0) FakeRelay.Reset(s);
                else await FakeRelay.Reply(s, code, "application/json", body);
                return;
            }
            await FakeRelay.SseStart(s);
            await FakeRelay.Write(s, round++ == 0 ? Call("search_web", "{\"query\":\"RTX 4070 max temperature\"}") + Finish("tool_calls") + "data: [DONE]\n\n"
                : Words("From your records only.") + Finish() + "data: [DONE]\n\n");
            s.Close();
        });
        using var _ = Point(relay.Url);
        var reply = await Relay.AskAsync([], "Is my GPU too hot?", Tools());
        Assert.Equal("From your records only.", reply.Text);
        Assert.Contains("Web search wasn't working", reply.Note);
    }

    [Fact]
    public async Task One_question_id_for_all_its_rounds()
    {
        var ids = new List<string>();
        int round = 0;
        await using var relay = new FakeRelay(async (r, s) =>
        {
            ids.Add(System.Text.Json.Nodes.JsonNode.Parse(r.Body)?["question_id"]?.ToString() ?? "");
            await FakeRelay.SseStart(s);
            await FakeRelay.Write(s, round++ < 2 ? Call("get_crashes", "{}") + Finish("tool_calls") + "data: [DONE]\n\n" : Words("Done.") + Finish() + "data: [DONE]\n\n");
            s.Close();
        });
        using var _ = Point(relay.Url);
        await Relay.AskAsync([], "Why?", Tools());
        Assert.Equal(3, ids.Count);
        Assert.Single(ids.Distinct());
    }

    [Fact]
    public async Task A_mistyped_relay_address_is_a_problem_not_a_crash()
    {
        using var _ = Point("not a url");
        var reply = await Relay.AskAsync([], "Why?", Tools());
        Assert.NotNull(reply.Problem);
        var (status, problem) = await Relay.StatusAsync();
        Assert.Null(status);
        Assert.NotNull(problem);
        var (_, activateProblem, _) = await Relay.ActivateAsync("RS-AAAA-BBBB-CCCC");
        Assert.NotNull(activateProblem);
    }

    [Fact]
    public async Task A_long_answer_comes_through_whole()
    {
        string big = string.Concat(Enumerable.Repeat("The fans ran fast. ", 20_000));
        await using var relay = new FakeRelay(async (_, s) =>
        {
            await FakeRelay.SseStart(s);
            foreach (var part in big.Chunk(500)) await FakeRelay.Write(s, Words(new string(part)));
            await FakeRelay.Write(s, Finish() + "data: [DONE]\n\n");
            s.Close();
        });
        using var _ = Point(relay.Url);
        var reply = await Relay.AskAsync([], "Why?", Tools());
        Assert.Equal(big.Trim(), reply.Text);
    }

    // ── The fake relay ────────────────────────────────────────────────────
    private static IDisposable Point(string url)
    {
        Relay.TestRoot.Value = url;
        return new Undo(() => Relay.TestRoot.Value = null);
    }

    private sealed class Undo(Action undo) : IDisposable { public void Dispose() => undo(); }

    private static string Esc(string s) => System.Text.Json.JsonSerializer.Serialize(s);
    private static string Words(string words) => $"data: {{\"choices\":[{{\"delta\":{{\"content\":{Esc(words)}}}}}]}}\n\n";
    private static string Finish(string why = "stop") => $"data: {{\"choices\":[{{\"delta\":{{}},\"finish_reason\":\"{why}\"}}]}}\n\n";
    private static string Call(string name, string args) =>
        $"data: {{\"choices\":[{{\"delta\":{{\"tool_calls\":[{{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{{\"name\":{Esc(name)},\"arguments\":{Esc(args)}}}}}]}}}}]}}\n\n";

    internal sealed record FakeRequest(string Method, string Path, string Body)
    {
        /// <summary>The request's headers by name (any case).</summary>
        public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>A relay on a local port that answers each request as the test says, at the level of the connection.</summary>
    internal sealed class FakeRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public FakeRelay(Func<FakeRequest, NetworkStream, Task> handle)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                var stream = client.GetStream();
                                var req = await Read(stream);
                                await handle(req, stream);
                            }
                            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
                        }
                    });
                }
            });
        }

        public string Url { get; }

        private static async Task<FakeRequest> Read(NetworkStream s)
        {
            var head = new List<byte>();
            var one = new byte[1];
            while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
            {
                if (await s.ReadAsync(one) == 0) break;
                head.Add(one[0]);
            }
            var lines = Encoding.ASCII.GetString([.. head]).Split("\r\n");
            var first = lines[0].Split(' ');
            int length = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(p => int.Parse(p[1].Trim())).FirstOrDefault();
            var body = new byte[length];
            int got = 0;
            while (got < length) { int n = await s.ReadAsync(body.AsMemory(got)); if (n == 0) break; got += n; }
            var headers = lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);
            return new FakeRequest(first[0], first.Length > 1 ? first[1] : "", Encoding.UTF8.GetString(body, 0, got)) { Headers = headers };
        }

        public static Task Write(NetworkStream s, string text) => s.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask().ContinueWith(_ => s.FlushAsync()).Unwrap();

        public static async Task Reply(NetworkStream s, int code, string type, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            await Write(s, $"HTTP/1.1 {code} X\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(bytes);
            s.Close();
        }

        /// <summary>A streamed reply with no length: it ends when the connection closes, as the relay's does.</summary>
        public static Task SseStart(NetworkStream s) => Write(s, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n");

        /// <summary>Drops the connection with a reset instead of an orderly close.</summary>
        public static void Reset(NetworkStream s)
        {
            s.Socket.LingerState = new LingerOption(true, 0);
            s.Socket.Close();
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
            _stop.Dispose();
        }
    }
}
