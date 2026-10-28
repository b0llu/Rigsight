using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Rigsight.Core.Ai;
using static Rigsight.Tests.App.RelayFailureTests;

namespace Rigsight.Tests.App;

/// <summary>
/// "Just paste" (the user's pick, 2026-10-27): what a pasted key or address is, the saved keys, connecting to a service
/// or a server of their own, and the Anthropic header that made every Anthropic key fail.
/// </summary>
public sealed class JustPasteCoreTests
{
    [Theory]
    [InlineData("sk-or-v1-3f9cabc", "OpenRouter")]
    [InlineData("sk-ant-usr-1eB-3g8wYvVJ", "Anthropic")]
    [InlineData("sk-ant-api03-abc", "Anthropic")]
    [InlineData("sk-proj-abc", "OpenAI")]
    [InlineData("sk-svcacct-abc", "OpenAI")]
    [InlineData("sk-admin-abc", "OpenAI")]
    // Made up, and split so GitHub's secret scanning doesn't take it for a real key (it refused the 1.0.0 push).
    [InlineData("sk-abcdefghij1234567890T3Blb" + "kFJabcdefghij1234567890", "OpenAI")]
    // DeepSeek's keys are a plain "sk-" with no OpenAI marker: never guessed as OpenAI, so it can't be sent there.
    [InlineData("sk-a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6", null)]
    [InlineData("sk-abc", null)]
    [InlineData("AIzaSyD-example", "Google Gemini")]
    [InlineData("xai-abc", "xAI")]
    [InlineData("gsk_abc", "Groq")]
    [InlineData("  sk-or-v1-x  ", "OpenRouter")]
    [InlineData("http://192.168.1.20:8000", null)]
    [InlineData("hello there", null)]
    [InlineData("", null)]
    public void A_keys_start_says_its_service(string text, string? service) => Assert.Equal(service, KeyDetect.Service(text));

    [Theory]
    [InlineData("sk-a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6", true)]
    [InlineData("sk-proj-abc", false)]
    [InlineData("sk-or-v1-abc", false)]
    [InlineData("sk-ant-api03-abc", false)]
    [InlineData("gsk_abc", false)]
    public void A_plain_sk_key_is_one_to_ask_about(string text, bool plain) => Assert.Equal(plain, KeyDetect.IsPlainSk(text));

    [Theory]
    [InlineData("http://x", true)]
    [InlineData("https://llm.example.com/v1", true)]
    [InlineData("localhost:1234", true)]
    [InlineData("192.168.1.20:8000", true)]
    [InlineData("192.168.1.20:8000/v1", true)]
    [InlineData("sk-or-v1-abc", false)]
    [InlineData("AIzaSy:123", false)]
    [InlineData("not an address", false)]
    public void An_address_is_told_from_a_key(string text, bool address) => Assert.Equal(address, KeyDetect.IsAddress(text));

    [Fact]
    public void Anthropic_requests_carry_its_version_and_key_header()
    {
        using var req = OwnKey.Request(HttpMethod.Get, "https://api.anthropic.com/v1/models", "sk-ant-usr-xyz");
        Assert.Equal("2023-06-01", req.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("sk-ant-usr-xyz", req.Headers.GetValues("x-api-key").Single());
        Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
        using var other = OwnKey.Request(HttpMethod.Get, "https://openrouter.ai/api/v1/models", "sk-or-1");
        Assert.False(other.Headers.Contains("anthropic-version"));
        using var none = OwnKey.Request(HttpMethod.Get, "http://192.168.1.20:8000/v1/models", "");
        Assert.Null(none.Headers.Authorization);
    }

    [Fact]
    public void Saved_keys_add_switch_remove_and_mask()
    {
        using var keys = Folder(out _);
        Assert.Null(SavedKeys.Active());
        var or = SavedKeys.Add("OpenRouter", "", "sk-or-v1-abcdefa41f", "", "google/gemini-3.8-flash");
        Assert.Equal("OpenRouter", or.Name);
        var an = SavedKeys.Add("Anthropic", " Work ", "sk-ant-usr-1234gAAx", "", "");
        var srv = SavedKeys.Add("Custom", "", "", "http://192.168.1.20:8000/v1", "qwen3-32b-awq");
        Assert.Equal(3, SavedKeys.All().Count);
        Assert.Equal(srv.Id, SavedKeys.Active()?.Id);
        Assert.Equal("Work", an.Name);
        Assert.Equal("Your own server", srv.Name);

        Assert.Equal("sk-or-…a41f", SavedKeys.Mask(or));
        Assert.Equal("sk-ant-…gAAx", SavedKeys.Mask(an));
        Assert.Equal("192.168.1.20:8000/v1", SavedKeys.Mask(srv));

        SavedKeys.Use(or.Id);
        Assert.Equal("sk-or-v1-abcdefa41f", KeyStore.Load());
        SavedKeys.SetModel(or.Id, "anthropic/claude-sonnet-5.5");
        SavedKeys.Rename(an.Id, "Personal");
        SavedKeys.Touch();
        var now = SavedKeys.Active()!;
        Assert.Equal("anthropic/claude-sonnet-5.5", now.Model);
        Assert.NotNull(now.LastUsed);
        Assert.Equal("Personal", SavedKeys.All().Single(k => k.Id == an.Id).Name);

        // The one in use removed: the first left takes over; the last removed: none.
        SavedKeys.Remove(or.Id);
        Assert.Equal(an.Id, SavedKeys.Active()?.Id);
        SavedKeys.Remove(an.Id);
        SavedKeys.Remove(srv.Id);
        Assert.Null(SavedKeys.Active());
        Assert.Null(KeyStore.Load());
    }

    [Fact]
    public void The_file_is_locked_not_plain_text()
    {
        using var keys = Folder(out _);
        SavedKeys.Add("OpenRouter", "", "sk-or-v1-secretsecret", "", "");
        Assert.DoesNotContain("secretsecret", Encoding.UTF8.GetString(File.ReadAllBytes(SavedKeys.File)));
    }

    [Fact]
    public void The_one_old_key_moves_into_the_saved_keys()
    {
        using var keys = Folder(out var dir);
        File.WriteAllBytes(Path.Combine(dir, "ai-key.bin"), KeyStore.Protect(Encoding.UTF8.GetBytes("sk-ant-usr-oldkey1234")));
        SavedKeys.Migrate("Anthropic", "claude-sonnet-5-5", "");
        var k = Assert.Single(SavedKeys.All());
        Assert.Equal("Anthropic", k.Service);
        Assert.Equal("claude-sonnet-5-5", k.Model);
        Assert.Equal("sk-ant-usr-oldkey1234", KeyStore.Load());
        Assert.False(File.Exists(Path.Combine(dir, "ai-key.bin")));
        Assert.True(File.Exists(SavedKeys.File));
    }

    [Fact]
    public void An_old_custom_key_keeps_its_address()
    {
        using var keys = Folder(out var dir);
        File.WriteAllBytes(Path.Combine(dir, "ai-key.bin"), KeyStore.Protect(Encoding.UTF8.GetBytes("abc123")));
        SavedKeys.Migrate("Custom (OpenAI-compatible)", "llama", "http://10.0.0.5:8000/v1");
        var k = Assert.Single(SavedKeys.All());
        Assert.Equal("Custom", k.Service);
        Assert.Equal("http://10.0.0.5:8000/v1", k.Address);
    }

    [Fact]
    public void Saving_a_key_with_none_saved_adds_one_by_its_start()
    {
        using var keys = Folder(out _);
        KeyStore.Save("sk-or-v1-newkey9999");
        var k = Assert.Single(SavedKeys.All());
        Assert.Equal("OpenRouter", k.Service);
        KeyStore.Save("sk-or-v1-replaced0000");
        Assert.Single(SavedKeys.All());
        Assert.Equal("sk-or-v1-replaced0000", KeyStore.Load());
    }

    [Fact]
    public void Anthropic_models_get_maker_context_and_the_newest_of_each_line_suggested()
    {
        var data = JsonNode.Parse("""
            [{"id":"claude-haiku-5-5","display_name":"Claude Haiku 5.5","created_at":"2026-10-07T18:00:00Z","max_input_tokens":1000000},
             {"id":"claude-sonnet-5-5","display_name":"Claude Sonnet 5.5","created_at":"2026-09-28T00:00:00Z","max_input_tokens":1000000},
             {"id":"claude-opus-5-5","display_name":"Claude Opus 5.5","created_at":"2026-09-21T16:24:00Z","max_input_tokens":1000000},
             {"id":"claude-opus-5","display_name":"Claude Opus 5","created_at":"2026-06-01T00:00:00Z","max_input_tokens":1000000},
             {"id":"claude-haiku-4-5-20251001","display_name":"Claude Haiku 4.5","created_at":"2025-10-01T00:00:00Z","max_input_tokens":200000}]
            """)!.AsArray();
        var list = OwnKey.Parse(data, "https://api.anthropic.com/v1");
        Assert.All(list, m => Assert.Equal("Anthropic", m.Maker));
        Assert.Equal("Best answers", list.Single(m => m.Id == "claude-opus-5-5").Tier);
        Assert.Equal("Balanced", list.Single(m => m.Id == "claude-sonnet-5-5").Tier);
        Assert.Equal("Cheapest", list.Single(m => m.Id == "claude-haiku-5-5").Tier);
        Assert.Null(list.Single(m => m.Id == "claude-opus-5").Tier);
        Assert.Equal("Claude Haiku 4.5", list.Single(m => m.Id.StartsWith("claude-haiku-4", StringComparison.Ordinal)).Name);
        Assert.Equal(200000, list.Last().ContextTokens);
        Assert.All(list, m => Assert.Null(m.CentsPerQuestion));
    }

    [Fact]
    public void OpenRouter_models_lose_the_maker_from_their_name_and_keep_price_and_tools_only()
    {
        var data = JsonNode.Parse("""
            [{"id":"anthropic/claude-sonnet-5.5","name":"Anthropic: Claude Sonnet 5.5","context_length":1000000,"supported_parameters":["tools"],"pricing":{"prompt":"0.000003","completion":"0.000015"}},
             {"id":"meta-llama/llama-4-maverick","name":"Meta: Llama 4 Maverick","context_length":1048576,"supported_parameters":["tools"],"pricing":{"prompt":"0.0000002","completion":"0.0000006"}},
             {"id":"some/no-tools","name":"Some: No Tools","supported_parameters":["temperature"]}]
            """)!.AsArray();
        var list = OwnKey.Parse(data, "https://openrouter.ai/api/v1");
        Assert.Equal(2, list.Count);
        var sonnet = list[0];
        Assert.Equal("Claude Sonnet 5.5", sonnet.Name);
        Assert.Equal("Anthropic", sonnet.Maker);
        Assert.Equal("Best answers", sonnet.Tier);
        Assert.True(sonnet.Tested);
        Assert.NotNull(sonnet.CentsPerQuestion);
        Assert.Equal("Meta", list[1].Maker);
        Assert.Null(list[1].Tier);
    }

    [Fact]
    public async Task A_bare_address_that_answers_only_with_v1_gets_it_added()
    {
        await using var server = new FakeRelay(async (req, s) =>
        {
            if (req.Path == "/v1/models") await FakeRelay.Reply(s, 200, "application/json", """{"data":[{"id":"qwen3-32b-awq","owned_by":"vllm","max_model_len":32768}]}""");
            else await FakeRelay.Reply(s, 404, "text/plain", "Not Found");
        });
        var r = await OwnKey.ConnectAsync("Custom", "", server.Url);
        Assert.True(r.AddedV1);
        Assert.Equal(server.Url + "/v1", r.Root);
        var m = Assert.Single(r.Models!);
        Assert.Equal(32768, m.ContextTokens);
        Assert.Null(m.Maker);
        Assert.Null(r.Problem);
        Assert.False(r.NotPrivate);
    }

    [Fact]
    public async Task A_server_without_a_model_list_asks_for_the_name()
    {
        await using var server = new FakeRelay((req, s) => FakeRelay.Reply(s, 404, "text/plain", "Not Found"));
        var r = await OwnKey.ConnectAsync("Custom", "", server.Url);
        Assert.True(r.NoModelList);
        Assert.NotNull(r.Models);
        Assert.Empty(r.Models!);
        Assert.Null(r.Problem);
    }

    [Fact]
    public async Task A_server_that_refuses_the_key_says_so()
    {
        await using var server = new FakeRelay((req, s) => FakeRelay.Reply(s, 401, "application/json", """{"error":{"message":"bad key"}}"""));
        var r = await OwnKey.ConnectAsync("Custom", "wrong", server.Url + "/v1");
        Assert.NotNull(r.Problem);
        Assert.Contains("didn't accept", r.Problem);
    }

    [Fact]
    public async Task Ollamas_port_is_sent_to_Ollamas_own_setting()
    {
        var r = await OwnKey.ConnectAsync("Custom", "", "http://localhost:11434");
        Assert.True(r.IsOllama);
        Assert.Null(r.Problem);
    }

    [Fact]
    public async Task Nothing_listening_is_a_problem_in_words()
    {
        int port;
        using (var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { l.Start(); port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        var r = await OwnKey.ConnectAsync("Custom", "", $"127.0.0.1:{port}");
        Assert.Contains("Nothing answered", r.Problem);
        Assert.Null(r.Models);
    }

    [Theory]
    [InlineData("203.0.113.7", false)]
    [InlineData("llm.example.com", false)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("172.20.0.4", true)]
    [InlineData("172.40.0.4", false)]
    [InlineData("nas.local", true)]
    public void Own_network_is_told_from_the_internet(string host, bool own) => Assert.Equal(own, OwnKey.IsOwnNetwork(host));

    [Fact]
    public async Task An_http_address_outside_the_own_network_is_flagged()
    {
        // 203.0.113.0/24 is set aside for documentation: nothing answers, and the flag still comes back.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var r = await OwnKey.ConnectAsync("Custom", "", "http://203.0.113.7:8000/v1", cts.Token);
        Assert.True(r.NotPrivate);
    }

    [Fact]
    public async Task The_look_up_check_tells_a_tool_call_from_words()
    {
        await using var calls = new FakeRelay((req, s) => FakeRelay.Reply(s, 200, "application/json",
            """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"1","type":"function","function":{"name":"get_time","arguments":"{}"}}]}}]}"""));
        Assert.True(await OwnKey.ProbeToolsAsync(calls.Url + "/v1", "", "qwen3"));
        await using var words = new FakeRelay((req, s) => FakeRelay.Reply(s, 200, "application/json",
            """{"choices":[{"message":{"role":"assistant","content":"It is 4 PM."}}]}"""));
        Assert.False(await OwnKey.ProbeToolsAsync(words.Url + "/v1", "", "mistral"));
        await using var broken = new FakeRelay((req, s) => FakeRelay.Reply(s, 500, "text/plain", "boom"));
        Assert.Null(await OwnKey.ProbeToolsAsync(broken.Url + "/v1", "", "x"));
    }

    [Fact]
    public async Task OpenRouter_credit_and_this_months_spend()
    {
        await using var server = new FakeRelay((req, s) => req.Path.EndsWith("/credits", StringComparison.Ordinal)
            ? FakeRelay.Reply(s, 200, "application/json", """{"data":{"total_credits":10,"total_usage":5.8}}""")
            : FakeRelay.Reply(s, 200, "application/json", """{"data":{"usage_monthly":0.62}}"""));
        OwnKey.TestOpenRouter.Value = server.Url;
        try
        {
            var (left, month) = await OwnKey.CreditAsync("sk-or-v1-x");
            Assert.Equal(4.2, left!.Value, 3);
            Assert.Equal(0.62, month!.Value, 3);
        }
        finally { OwnKey.TestOpenRouter.Value = null; }
    }

    /// <summary>A folder of this test's own for the saved keys, removed after.</summary>
    private static IDisposable Folder(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "rigsight-keys-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        SavedKeys.TestFolder.Value = dir;
        string d = dir;
        return new Gone(() =>
        {
            SavedKeys.TestFolder.Value = null;
            try { Directory.Delete(d, true); } catch (IOException) { }
        });
    }

    private sealed class Gone(Action undo) : IDisposable { public void Dispose() => undo(); }
}
