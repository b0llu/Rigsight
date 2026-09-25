using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Rigsight.Core.Updates;

namespace Rigsight.Tests.Core;

/// <summary>
/// The update folder of this test run (never the real one), downloads from a local server, and the test feed variable.
/// One at a time: they share the folder, the stall timeout and the environment.
/// </summary>
[Collection("Core statics")]
public sealed class UpdateStoreTests : IDisposable
{
    private readonly TimeSpan _stall = UpdateStore.StallTimeout;

    public UpdateStoreTests()
    {
        Clean();
        UpdateStore.StallTimeout = TimeSpan.FromMilliseconds(400);
    }

    public void Dispose()
    {
        UpdateStore.StallTimeout = _stall;
        Clean();
    }

    private static void Clean()
    {
        if (Directory.Exists(UpdateStore.Folder)) Directory.Delete(UpdateStore.Folder, recursive: true);
    }

    private static readonly byte[] Setup = MakeSetup();

    private static byte[] MakeSetup()
    {
        var bytes = new byte[700_000];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    private static Release ReleaseOf(byte[] bytes, string url, Version? version = null) =>
        new(version ?? new Version(99, 0, 0), "Rigsight-Setup-99.0.0.exe", url, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "https://github.com");

    private static string[] Leftovers() =>
        Directory.Exists(UpdateStore.Folder) ? [.. Directory.GetFiles(UpdateStore.Folder).Select(f => Path.GetFileName(f))] : [];

    // ---- Downloads ----

    [Fact]
    public async Task A_download_is_kept_when_it_matches()
    {
        using var server = TestHttpServer.Serving(Setup);
        var release = ReleaseOf(Setup, server.Url("/Rigsight-Setup-99.0.0.exe"));
        var reports = new List<long>();
        await UpdateStore.DownloadAsync(release, new SyncProgress(reports.Add));

        Assert.Equal(Setup, File.ReadAllBytes(UpdateStore.SetupPath(release)));
        Assert.True(await ReleaseFeed.MatchesAsync(UpdateStore.SetupPath(release), release));
        Assert.Equal(["Rigsight-Setup-99.0.0.exe"], Leftovers());
        Assert.Equal(Setup.Length, reports[^1]);
        Assert.True(reports.SequenceEqual(reports.Order()));
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task A_download_replaces_an_older_copy_of_the_same_file()
    {
        using var server = TestHttpServer.Serving(Setup);
        var release = ReleaseOf(Setup, server.Url());
        Directory.CreateDirectory(UpdateStore.Folder);
        File.WriteAllText(UpdateStore.SetupPath(release), "old");
        await UpdateStore.DownloadAsync(release);
        Assert.Equal(Setup, File.ReadAllBytes(UpdateStore.SetupPath(release)));
    }

    [Fact]
    public async Task A_short_download_is_thrown_away()
    {
        using var server = TestHttpServer.Serving(Setup[..1000]);
        var release = ReleaseOf(Setup, server.Url());
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateStore.DownloadAsync(release));
        Assert.Empty(Leftovers());
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task A_longer_download_is_thrown_away()
    {
        using var server = TestHttpServer.Serving([.. Setup, 1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateStore.DownloadAsync(ReleaseOf(Setup, server.Url())));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task A_download_with_the_wrong_hash_is_thrown_away()
    {
        var tampered = (byte[])Setup.Clone();
        tampered[12345] ^= 0xFF;
        using var server = TestHttpServer.Serving(tampered);
        var release = ReleaseOf(Setup, server.Url());
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateStore.DownloadAsync(release));
        Assert.Contains("SHA-256", e.Message);
        Assert.Empty(Leftovers());
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task A_failed_download_leaves_a_good_installer_already_there_alone()
    {
        using var server = TestHttpServer.Serving(Setup[..10]);
        var release = ReleaseOf(Setup, server.Url());
        Directory.CreateDirectory(UpdateStore.Folder);
        File.WriteAllBytes(UpdateStore.SetupPath(release), Setup);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateStore.DownloadAsync(release));
        Assert.True(await ReleaseFeed.MatchesAsync(UpdateStore.SetupPath(release), release));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task An_error_from_the_server_fails_at_once(int status)
    {
        using var server = new TestHttpServer((_, _, c, ct) => TestHttpServer.Send(c, status, "no"u8.ToArray(), ct: ct));
        await Assert.ThrowsAsync<HttpRequestException>(() => UpdateStore.DownloadAsync(ReleaseOf(Setup, server.Url())));
        Assert.Equal(1, server.Requests);
        Assert.Empty(Leftovers());
    }

    /// <summary>Sends the headers and part of the installer, then nothing.</summary>
    private static async Task Stall(Stream c, CancellationToken ct)
    {
        await TestHttpServer.SendHead(c, 200, Setup.Length, ct);
        await c.WriteAsync(Setup.AsMemory(0, 100_000), ct);
        await c.FlushAsync(ct);
        await Task.Delay(Timeout.Infinite, ct);
    }

    [Fact]
    public async Task A_stuck_download_is_retried_twice_then_times_out()
    {
        using var server = new TestHttpServer((_, _, c, ct) => Stall(c, ct));
        var release = ReleaseOf(Setup, server.Url());
        await Assert.ThrowsAsync<TimeoutException>(() => UpdateStore.DownloadAsync(release));
        Assert.Equal(3, server.Requests);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task A_download_stuck_before_the_headers_times_out_too()
    {
        using var server = new TestHttpServer((_, _, _, ct) => Task.Delay(Timeout.Infinite, ct));
        await Assert.ThrowsAsync<TimeoutException>(() => UpdateStore.DownloadAsync(ReleaseOf(Setup, server.Url())));
        Assert.Equal(3, server.Requests);
    }

    [Fact]
    public async Task A_stuck_download_that_works_the_next_time_succeeds()
    {
        using var server = new TestHttpServer((_, n, c, ct) => n == 1 ? Stall(c, ct) : TestHttpServer.Send(c, 200, Setup, ct: ct));
        var release = ReleaseOf(Setup, server.Url());
        await UpdateStore.DownloadAsync(release);
        Assert.Equal(2, server.Requests);
        Assert.Equal(["Rigsight-Setup-99.0.0.exe"], Leftovers());
        Assert.True(await ReleaseFeed.MatchesAsync(UpdateStore.SetupPath(release), release));
    }

    [Fact]
    public async Task A_slow_download_that_keeps_moving_is_not_a_stall()
    {
        // Each piece arrives within the stall timeout, but the whole takes several times longer.
        using var server = new TestHttpServer(async (_, _, c, ct) =>
        {
            await TestHttpServer.SendHead(c, 200, Setup.Length, ct);
            foreach (var chunk in Setup.Chunk(Setup.Length / 6 + 1))
            {
                await c.WriteAsync(chunk, ct);
                await c.FlushAsync(ct);
                await Task.Delay(200, ct);
            }
        });
        await UpdateStore.DownloadAsync(ReleaseOf(Setup, server.Url()));
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task Cancelling_stops_at_once_without_retrying()
    {
        using var server = new TestHttpServer((_, _, c, ct) => Stall(c, ct));
        UpdateStore.StallTimeout = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateStore.DownloadAsync(ReleaseOf(Setup, server.Url()), null, cts.Token));
        Assert.Equal(1, server.Requests);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task An_already_cancelled_download_does_nothing()
    {
        using var server = TestHttpServer.Serving(Setup);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateStore.DownloadAsync(ReleaseOf(Setup, server.Url()), null, new CancellationToken(true)));
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task Another_process_partial_download_is_not_touched()
    {
        using var server = TestHttpServer.Serving(Setup);
        var release = ReleaseOf(Setup, server.Url());
        Directory.CreateDirectory(UpdateStore.Folder);
        var theirs = UpdateStore.SetupPath(release) + ".99999.part";
        File.WriteAllText(theirs, "partial");
        await UpdateStore.DownloadAsync(release);
        Assert.Equal("partial", File.ReadAllText(theirs));
    }

    // ---- The day's check, the install attempt ----

    [Fact]
    public void Nothing_saved_reads_as_nothing()
    {
        Assert.Null(UpdateStore.ReadCheck());
        Assert.Null(UpdateStore.ReadAttempt());
        UpdateStore.ClearAttempt();
        UpdateStore.Tidy(null);
    }

    [Fact]
    public void An_install_attempt_is_remembered_until_cleared()
    {
        UpdateStore.WriteAttempt(new Version(1, 2, 3));
        var attempt = UpdateStore.ReadAttempt()!;
        Assert.Equal(new Version(1, 2, 3), attempt.Version);
        Assert.InRange(attempt.At, DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1));
        UpdateStore.ClearAttempt();
        Assert.Null(UpdateStore.ReadAttempt());
        UpdateStore.ClearAttempt();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public void Broken_state_files_read_as_nothing(string content)
    {
        Directory.CreateDirectory(UpdateStore.Folder);
        File.WriteAllText(Path.Combine(UpdateStore.Folder, "latest.json"), content);
        File.WriteAllText(Path.Combine(UpdateStore.Folder, "attempt.json"), content);
        Assert.Null(UpdateStore.ReadCheck());
        Assert.Null(UpdateStore.ReadAttempt());
    }

    private static void SaveCheck(DateTime day, Release? latest)
    {
        Directory.CreateDirectory(UpdateStore.Folder);
        File.WriteAllText(Path.Combine(UpdateStore.Folder, "latest.json"), JsonSerializer.Serialize(new CheckResult(day, latest)));
    }

    [Fact]
    public void A_saved_check_reads_back_with_its_release()
    {
        var release = ReleaseOf(Setup, "https://example/x.exe");
        SaveCheck(DateTime.Today, release);
        Assert.Equal(new CheckResult(DateTime.Today, release), UpdateStore.ReadCheck());
        SaveCheck(DateTime.Today, null);
        Assert.Equal(new CheckResult(DateTime.Today, null), UpdateStore.ReadCheck());
    }

    [Fact]
    public async Task A_downloaded_newer_installer_is_found()
    {
        var release = ReleaseOf(Setup, "https://example/x.exe");
        SaveCheck(DateTime.Today, release);
        Assert.Null(await UpdateStore.DownloadedAsync());
        File.WriteAllBytes(UpdateStore.SetupPath(release), Setup);
        Assert.Equal(release, await UpdateStore.DownloadedAsync());
        File.WriteAllBytes(UpdateStore.SetupPath(release), Setup[..^1]);
        Assert.Null(await UpdateStore.DownloadedAsync());
    }

    [Fact]
    public async Task An_installer_for_this_version_or_older_is_never_offered()
    {
        var release = ReleaseOf(Setup, "https://example/x.exe", ReleaseFeed.Current);
        SaveCheck(DateTime.Today, release);
        File.WriteAllBytes(UpdateStore.SetupPath(release), Setup);
        Assert.Null(await UpdateStore.DownloadedAsync());
    }

    [Fact]
    public void Tidy_keeps_only_the_wanted_installer()
    {
        Directory.CreateDirectory(UpdateStore.Folder);
        string[] files =
        [
            "Rigsight-Setup-0.5.0.exe", "Rigsight-Setup-0.6.0.exe", "Rigsight-Setup-99.0.0.exe", "Rigsight-Setup-99.0.0.exe.1234.part",
            "latest.json", "attempt.json", "prompted", "notes.txt",
        ];
        foreach (var f in files) File.WriteAllText(Path.Combine(UpdateStore.Folder, f), f);

        UpdateStore.Tidy(ReleaseOf(Setup, ""));
        Assert.Equal(["Rigsight-Setup-99.0.0.exe", "attempt.json", "latest.json", "notes.txt", "prompted"], Leftovers().Order(StringComparer.Ordinal));

        UpdateStore.Tidy(null);
        Assert.Equal(["attempt.json", "latest.json", "notes.txt", "prompted"], Leftovers().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Tidy_keeps_the_installer_whatever_the_case_of_its_name()
    {
        Directory.CreateDirectory(UpdateStore.Folder);
        File.WriteAllText(Path.Combine(UpdateStore.Folder, "rigsight-setup-99.0.0.EXE"), "x");
        UpdateStore.Tidy(ReleaseOf(Setup, ""));
        Assert.Single(Leftovers());
    }

    [Fact]
    public void Tidy_skips_a_file_in_use()
    {
        Directory.CreateDirectory(UpdateStore.Folder);
        var busy = Path.Combine(UpdateStore.Folder, "Rigsight-Setup-1.0.0.exe.77.part");
        var old = Path.Combine(UpdateStore.Folder, "Rigsight-Setup-1.0.0.exe");
        File.WriteAllText(old, "x");
        using (new FileStream(busy, FileMode.Create, FileAccess.Write, FileShare.None))
            UpdateStore.Tidy(null);
        Assert.True(File.Exists(busy));
        Assert.False(File.Exists(old));
    }

    // ---- The day's check against a local feed (Debug builds only read the feed variable) ----

#if DEBUG
    private sealed class Feed : IDisposable
    {
        private readonly string? _before = Environment.GetEnvironmentVariable("RIGSIGHT_UPDATE_FEED");
        public TestHttpServer Server { get; }

        public Feed(string json)
        {
            Server = new TestHttpServer((_, _, c, ct) => TestHttpServer.Send(c, 200, System.Text.Encoding.UTF8.GetBytes(json), ct: ct));
            Environment.SetEnvironmentVariable("RIGSIGHT_UPDATE_FEED", Server.Url("/releases/latest"));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("RIGSIGHT_UPDATE_FEED", _before);
            Server.Dispose();
        }
    }

    private const string FeedJson = """
        { "tag_name": "v99.0.0", "html_url": "https://github.com/b0llu/Rigsight/releases/tag/v99.0.0",
          "assets": [{ "name": "Rigsight-Setup-99.0.0.exe", "size": 5, "digest": "sha256:ABCD", "browser_download_url": "http://x/setup.exe" }] }
        """;

    [Fact]
    public void Debug_builds_follow_the_test_feed_variable()
    {
        var feedUrl = typeof(ReleaseFeed).GetProperty("FeedUrl", BindingFlags.NonPublic | BindingFlags.Static)!;
        var before = Environment.GetEnvironmentVariable("RIGSIGHT_UPDATE_FEED");
        try
        {
            Environment.SetEnvironmentVariable("RIGSIGHT_UPDATE_FEED", null);
            Assert.Equal("https://api.github.com/repos/b0llu/Rigsight/releases/latest", feedUrl.GetValue(null));
            Environment.SetEnvironmentVariable("RIGSIGHT_UPDATE_FEED", "");
            Assert.Equal("https://api.github.com/repos/b0llu/Rigsight/releases/latest", feedUrl.GetValue(null));
            Environment.SetEnvironmentVariable("RIGSIGHT_UPDATE_FEED", "http://127.0.0.1:1/feed");
            Assert.Equal("http://127.0.0.1:1/feed", feedUrl.GetValue(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RIGSIGHT_UPDATE_FEED", before);
        }
    }

    [Fact]
    public async Task The_latest_release_is_fetched_once_a_day()
    {
        using var feed = new Feed(FeedJson);
        var first = await UpdateStore.LatestAsync();
        Assert.Equal(new Version(99, 0, 0), first!.Version);
        Assert.Equal("abcd", first.Sha256);
        Assert.Equal(new CheckResult(DateTime.Today, first), UpdateStore.ReadCheck());

        Assert.Equal(first, await UpdateStore.LatestAsync());
        Assert.Equal(1, feed.Server.Requests);

        Assert.Equal(first, await UpdateStore.LatestAsync(force: true));
        Assert.Equal(2, feed.Server.Requests);
    }

    [Fact]
    public async Task Yesterdays_check_is_asked_again()
    {
        SaveCheck(DateTime.Today.AddDays(-1), null);
        using var feed = new Feed(FeedJson);
        Assert.NotNull(await UpdateStore.LatestAsync());
        Assert.Equal(1, feed.Server.Requests);
    }

    [Fact]
    public async Task No_usable_release_is_remembered_for_the_day_too()
    {
        using var feed = new Feed("""{ "tag_name": "v99.0.0", "assets": [] }""");
        Assert.Null(await UpdateStore.LatestAsync());
        Assert.Null(await UpdateStore.LatestAsync());
        Assert.Equal(1, feed.Server.Requests);
        Assert.Equal(new CheckResult(DateTime.Today, null), UpdateStore.ReadCheck());
    }

    [Fact]
    public async Task A_feed_that_cannot_be_read_throws_and_saves_nothing()
    {
        using var feed = new Feed("<html>rate limited</html>");
        await Assert.ThrowsAnyAsync<JsonException>(() => UpdateStore.LatestAsync(force: true));
        Assert.Null(UpdateStore.ReadCheck());
    }
#endif

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
