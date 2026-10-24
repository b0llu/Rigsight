using Rigsight.Core.Ai;
using Rigsight.ViewModels;
using static Rigsight.Tests.App.RelayFailureTests;

namespace Rigsight.Tests.App;

/// <summary>
/// Settings › Ask RigSight → Ollama's picker (approved mock-up 2026-10-24): which models are picked for a card, how each
/// fits, the card's name as people say it, and the download through Ollama's own /api/pull with its progress line.
/// </summary>
public sealed class OllamaPickerTests
{
    [Theory]
    [InlineData(24.0, "gemma4:26b", "gpt-oss:20b", null)]
    [InlineData(20.0, "gpt-oss:20b", "gemma4:12b", "gemma4:26b")]
    [InlineData(16.0, "gemma4:12b", "qwen3.5:9b", "gpt-oss:20b")]
    [InlineData(12.0, "gemma4:12b", "qwen3.5:9b", "gpt-oss:20b")]
    [InlineData(10.0, "qwen3.5:9b", "qwen3.5:4b", "gemma4:12b")]
    [InlineData(8.0, "qwen3.5:4b", null, "qwen3.5:9b")]
    [InlineData(4.0, "qwen3.5:4b", null, "qwen3.5:9b")]
    [InlineData(null, "qwen3.5:4b", null, "qwen3.5:9b")]
    public void The_picks_follow_the_cards_memory(double? vram, string best, string? smaller, string? bigger)
    {
        var (b, s, g) = OllamaPicks.For(vram);
        Assert.Equal(best, b.Tag);
        Assert.Equal(smaller, s?.Tag);
        Assert.Equal(bigger, g?.Tag);
    }

    [Fact]
    public void Every_pick_and_its_size_is_the_one_checked_on_ollama_com()
    {
        Assert.Equal(["qwen3.5:4b", "qwen3.5:9b", "gemma4:12b", "gpt-oss:20b", "gemma4:26b"], OllamaPicks.All.Select(p => p.Tag));
        Assert.Equal([3.5, 6.6, 8, 14, 18], OllamaPicks.All.Select(p => p.Gb));
        Assert.Equal("8 GB download", OllamaPicks.All[2].SizeText);
    }

    [Fact]
    public void A_model_fits_partly_fits_or_is_too_big_and_unknown_without_the_card()
    {
        // What it takes while answering, not the download: 8 GB needs 11.2 GB, 10 GB needs 13.5.
        Assert.Equal(11.2, OllamaPicks.NeedGb(8), 1);
        Assert.Equal(OllamaFit.Fits, OllamaPicks.Fit(8, 12));
        Assert.Equal(OllamaFit.PartFits, OllamaPicks.Fit(10, 12));
        Assert.Equal(OllamaFit.PartFits, OllamaPicks.Fit(14, 12));
        Assert.Equal(OllamaFit.TooBig, OllamaPicks.Fit(18, 8));
        Assert.Equal(OllamaFit.Unknown, OllamaPicks.Fit(8, null));
        // Fitting gets no pill: the section is already picked for this PC; only a catch is said.
        Assert.Equal("", OllamaPicks.FitText(OllamaFit.Fits, "RTX 3080 Ti"));
        Assert.Equal("May be slow", OllamaPicks.FitText(OllamaFit.PartFits, "RTX 3080 Ti"));
        Assert.Equal("", OllamaPicks.FitText(OllamaFit.Unknown, "RTX 3080 Ti"));
        Assert.Contains("Only part of it fits", OllamaPicks.FitWhy(OllamaFit.PartFits));
    }

    [Fact]
    public void The_card_is_the_separate_one_named_as_people_say_it()
    {
        Assert.Equal("NVIDIA GeForce RTX 3080 Ti", OllamaPicks.MainCard(["Intel(R) UHD Graphics 770", "NVIDIA GeForce RTX 3080 Ti"]));
        Assert.Equal("AMD Radeon RX 7900 XTX", OllamaPicks.MainCard(["Microsoft Basic Display Adapter", "AMD Radeon RX 7900 XTX"]));
        Assert.Equal("Intel(R) UHD Graphics 770", OllamaPicks.MainCard(["Intel(R) UHD Graphics 770"]));
        Assert.Null(OllamaPicks.MainCard([]));
        Assert.Equal("RTX 3080 Ti", OllamaPicks.ShortCard("NVIDIA GeForce RTX 3080 Ti"));
        Assert.Equal("RX 7900 XTX", OllamaPicks.ShortCard("AMD Radeon RX 7900 XTX"));
    }

    [Fact]
    public void The_progress_line_says_how_much_how_fast_and_how_long()
    {
        long gb = 1_000_000_000;
        Assert.Equal("3.1 of 8 GB", AiSettingsViewModel.DownloadText((long)(3.1 * gb), 8 * gb, 0));
        Assert.Equal("3.1 of 8 GB · 18 MB/s · about 5 min left", AiSettingsViewModel.DownloadText((long)(3.1 * gb), 8 * gb, 18_000_000));
        Assert.EndsWith("less than a minute left", AiSettingsViewModel.DownloadText(8 * gb - 1_000_000, 8 * gb, 18_000_000));
    }

    [Fact]
    public async Task A_download_adds_up_its_parts_and_ends_on_success()
    {
        string? asked = null;
        await using var ollama = new FakeRelay(async (req, s) =>
        {
            asked = req.Path + " " + req.Body;
            await FakeRelay.Reply(s, 200, "application/x-ndjson", string.Join("\n",
                "{\"status\":\"pulling manifest\"}",
                "{\"status\":\"pulling a\",\"digest\":\"sha256:a\",\"total\":1000,\"completed\":400}",
                "{\"status\":\"pulling b\",\"digest\":\"sha256:b\",\"total\":200,\"completed\":200}",
                "{\"status\":\"pulling a\",\"digest\":\"sha256:a\",\"total\":1000,\"completed\":1000}",
                "{\"status\":\"verifying sha256 digest\"}",
                "{\"status\":\"success\"}") + "\n");
        });
        var seen = new List<OllamaPull>();
        var problem = await Ollama.PullAsync(ollama.Url, "gemma4:12b", new Sync<OllamaPull>(seen.Add));
        Assert.Null(problem);
        Assert.StartsWith("/api/pull ", asked);
        Assert.Contains("\"model\":\"gemma4:12b\"", asked);
        Assert.Equal((600L, 1200L), (seen[2].Done, seen[2].Total));
        Assert.Equal((1200L, 1200L), (seen[^1].Done, seen[^1].Total));
    }

    [Fact]
    public async Task How_much_of_a_loaded_model_sits_on_the_card_is_read_from_ollama()
    {
        await using var ollama = new FakeRelay((_, s) => FakeRelay.Reply(s, 200, "application/json",
            "{\"models\":[{\"name\":\"gpt-oss:20b\",\"model\":\"gpt-oss:20b\",\"size\":16000000000,\"size_vram\":12000000000}]}"));
        Assert.Equal(0.75, (await Ollama.OnCardAsync(ollama.Url, "gpt-oss:20b"))!.Value, 2);
        // Not loaded: nothing to say yet.
        Assert.Null(await Ollama.OnCardAsync(ollama.Url, "gemma4:12b"));
    }

    [Fact]
    public async Task Removing_a_model_asks_ollama_to_delete_it()
    {
        string? asked = null;
        await using var ollama = new FakeRelay(async (req, s) => { asked = req.Method + " " + req.Path + " " + req.Body; await FakeRelay.Reply(s, 200, "application/json", "{}"); });
        Assert.Null(await Ollama.RemoveAsync(ollama.Url, "gemma3:1b"));
        Assert.Equal("DELETE /api/delete {\"model\":\"gemma3:1b\"}", asked);
    }

    [Fact]
    public async Task Removing_a_model_ollama_hasnt_got_says_so()
    {
        await using var ollama = new FakeRelay((_, s) => FakeRelay.Reply(s, 404, "application/json", "{\"error\":\"model 'x' not found\"}"));
        Assert.Equal("Ollama no longer has x:1b.", await Ollama.RemoveAsync(ollama.Url, "x:1b"));
        Assert.Contains("stopped answering", await Ollama.RemoveAsync("http://127.0.0.1:9", "x:1b"));
    }

    [Fact]
    public async Task A_model_ollama_doesnt_have_says_so()
    {
        await using var ollama = new FakeRelay((_, s) => FakeRelay.Reply(s, 200, "application/x-ndjson",
            "{\"status\":\"pulling manifest\"}\n{\"error\":\"pull model manifest: file does not exist (not found)\"}\n"));
        Assert.Equal("Ollama has no model called nosuch:1b.", await Ollama.PullAsync(ollama.Url, "nosuch:1b"));
    }

    [Fact]
    public async Task A_download_cut_off_says_to_carry_on()
    {
        await using var ollama = new FakeRelay((_, s) => FakeRelay.Reply(s, 200, "application/x-ndjson",
            "{\"status\":\"pulling a\",\"digest\":\"sha256:a\",\"total\":1000,\"completed\":400}\n"));
        Assert.Contains("Press Download to carry on", await Ollama.PullAsync(ollama.Url, "gemma4:12b"));
    }

    [Fact]
    public async Task No_ollama_at_the_address_is_a_problem_not_a_crash()
    {
        var problem = await Ollama.PullAsync("http://127.0.0.1:9", "gemma4:12b");
        Assert.Contains("Ollama stopped answering", problem);
    }

    [Theory]
    [InlineData("http://localhost:11434", true, "localhost:11434")]
    [InlineData("localhost:11434", true, "localhost:11434")]
    [InlineData("192.168.1.20:11434/", true, "192.168.1.20:11434")]
    [InlineData("localhost 11434", false, "localhost 11434")]
    [InlineData("", false, "")]
    [InlineData("ftp://box:11434", false, "box:11434")]
    public void An_address_is_checked_before_anything_is_sent(string address, bool ok, string where)
    {
        Assert.Equal(ok, Ollama.IsAddress(address));
        Assert.Equal(where, Ollama.Where(address));
    }

    /// <summary>Progress reported straight away (Progress&lt;T&gt; posts to a context the test hasn't got).</summary>
    private sealed class Sync<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
