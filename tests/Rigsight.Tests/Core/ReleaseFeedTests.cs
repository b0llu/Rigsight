using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rigsight.Core.Updates;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Core;

public sealed class ReleaseFeedTests
{
    private const string Hash = "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08";

    /// <summary>A release as GitHub's API returns it (trimmed to the fields that matter, plus some that don't).</summary>
    private static string Feed(string? tag, string assets, bool withPage = true) => $$"""
        {
          "url": "https://api.github.com/repos/b0llu/Rigsight/releases/1",
          {{(withPage ? "\"html_url\": \"https://github.com/b0llu/Rigsight/releases/tag/v0.6.0\"," : "")}}
          "id": 1, "draft": false, "prerelease": false, "name": "Rigsight 0.6.0", "body": "Notes",
          {{(tag is null ? "" : $"\"tag_name\": {tag},")}}
          "assets": {{assets}}
        }
        """;

    private static string Asset(string name, string? digest = "\"sha256:" + Hash + "\"", long size = 12_345_678) => $$"""
        {
          "url": "https://api.github.com/repos/b0llu/Rigsight/releases/assets/9", "id": 9, "name": "{{name}}",
          "content_type": "application/x-msdownload", "state": "uploaded", "size": {{size}},
          {{(digest is null ? "" : $"\"digest\": {digest},")}}
          "download_count": 3, "browser_download_url": "https://github.com/b0llu/Rigsight/releases/download/v0.6.0/{{name}}"
        }
        """;

    private static Release? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReleaseFeed.Parse(doc.RootElement);
    }

    [Fact]
    public void Picks_the_installer_among_the_assets()
    {
        var r = Parse(Feed("\"v0.6.0\"", $"[{Asset("Rigsight-0.6.0-portable.zip")}, {Asset("checksums.txt")}, {Asset("Rigsight-Setup-0.6.0.exe")}]"))!;
        Assert.Equal(new Version(0, 6, 0), r.Version);
        Assert.Equal("Rigsight-Setup-0.6.0.exe", r.AssetName);
        Assert.Equal("https://github.com/b0llu/Rigsight/releases/download/v0.6.0/Rigsight-Setup-0.6.0.exe", r.DownloadUrl);
        Assert.Equal(12_345_678, r.Size);
        Assert.Equal(Hash.ToLowerInvariant(), r.Sha256);
        Assert.Equal("https://github.com/b0llu/Rigsight/releases/tag/v0.6.0", r.PageUrl);
    }

    [Theory]
    [InlineData("Rigsight-Setup-0.6.0.exe")]
    [InlineData("rigsight-setup-0.6.0.EXE")]
    [InlineData("RIGSIGHT-SETUP-.exe")]
    public void The_installer_name_is_matched_in_any_case(string name) =>
        Assert.Equal(name, Parse(Feed("\"v0.6.0\"", $"[{Asset(name)}]"))!.AssetName);

    [Theory]
    [InlineData("Rigsight-0.6.0.exe")]
    [InlineData("Rigsight-Setup-0.6.0.msi")]
    [InlineData("Rigsight-Setup-0.6.0.exe.sig")]
    [InlineData("Rigsight-Setup-0.6.0.zip")]
    [InlineData("Other-Setup-0.6.0.exe")]
    [InlineData("Setup.exe")]
    public void Other_assets_are_ignored(string name) =>
        Assert.Null(Parse(Feed("\"v0.6.0\"", $"[{Asset(name)}]")));

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"sha512:abcdef\"")]
    [InlineData("\"md5:abcdef\"")]
    [InlineData("\"" + Hash + "\"")]
    public void An_installer_without_a_SHA256_is_not_offered(string? digest) =>
        Assert.Null(Parse(Feed("\"v0.6.0\"", $"[{Asset("Rigsight-Setup-0.6.0.exe", digest)}]")));

    [Fact]
    public void The_digest_prefix_is_read_in_any_case() =>
        Assert.Equal("abc123", Parse(Feed("\"v0.6.0\"", $"[{Asset("Rigsight-Setup-0.6.0.exe", "\"SHA256:ABC123\"")}]"))!.Sha256);

    [Fact]
    public void The_first_installer_decides()
    {
        // A second installer with a checksum doesn't stand in for a first one without.
        var json = Feed("\"v0.6.0\"", $"[{Asset("Rigsight-Setup-a.exe", null)}, {Asset("Rigsight-Setup-b.exe")}]");
        Assert.Null(Parse(json));
    }

    [Fact]
    public void An_asset_name_with_folders_stays_in_the_update_folder()
    {
        var r = Parse(Feed("\"v0.6.0\"", $"[{Asset(@"Rigsight-Setup-..\\..\\..\\evil.exe")}]"))!;
        Assert.Equal("evil.exe", r.AssetName);
        Assert.Equal(UpdateStore.Folder, Path.GetDirectoryName(UpdateStore.SetupPath(r)));
    }

    [Theory]
    [InlineData("\"v0.6.0\"", 0, 6, 0)]
    [InlineData("\"0.6.0\"", 0, 6, 0)]
    [InlineData("\"V1.2.3\"", 1, 2, 3)]
    [InlineData("\"v1.2\"", 1, 2, 0)]
    [InlineData("\"v1.2.3.0\"", 1, 2, 3)]
    [InlineData("\"v1.2.3.9\"", 1, 2, 3)]
    [InlineData("\"v10.20.30\"", 10, 20, 30)]
    public void Tags_are_read_as_versions(string tag, int major, int minor, int build) =>
        Assert.Equal(new Version(major, minor, build), Parse(Feed(tag, $"[{Asset("Rigsight-Setup-x.exe")}]"))!.Version);

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"latest\"")]
    [InlineData("\"v1\"")]
    [InlineData("\"v1.2.3-beta\"")]
    [InlineData("\"release-1.2.3\"")]
    public void A_release_without_a_usable_version_is_not_offered(string? tag) =>
        Assert.Null(Parse(Feed(tag, $"[{Asset("Rigsight-Setup-x.exe")}]")));

    [Fact]
    public void A_release_without_assets_is_not_offered()
    {
        Assert.Null(Parse(Feed("\"v0.6.0\"", "[]")));
        Assert.Null(Parse("""{ "tag_name": "v0.6.0" }"""));
    }

    [Fact]
    public void A_missing_page_link_is_empty() =>
        Assert.Equal("", Parse(Feed("\"v0.6.0\"", $"[{Asset("Rigsight-Setup-x.exe")}]", withPage: false))!.PageUrl);

    [Fact]
    public void GitHubs_error_reply_is_not_a_release() =>
        Assert.Null(Parse("""{ "message": "API rate limit exceeded", "documentation_url": "https://docs.github.com" }"""));

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3.0", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("vv1.2.3", "1.2.3")]
    [InlineData("V0.5.12", "0.5.12")]
    public void ParseVersion(string tag, string expected) => Assert.Equal(Version.Parse(expected), ReleaseFeed.ParseVersion(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("garbage")]
    [InlineData("1")]
    [InlineData("1.2.3-rc1")]
    [InlineData("1.2.x")]
    [InlineData("-1.2.3")]
    [InlineData("99999999999.0.0")]
    public void ParseVersion_rejects_garbage(string? tag) => Assert.Null(ReleaseFeed.ParseVersion(tag));

    [Fact]
    public void Versions_with_and_without_a_fourth_part_are_equal()
    {
        Assert.Equal(new Version(1, 2, 3), ReleaseFeed.Normalize(new Version(1, 2, 3, 0)));
        Assert.Equal(new Version(1, 2, 0), ReleaseFeed.Normalize(new Version(1, 2)));
        Assert.False(ReleaseFeed.Normalize(new Version(1, 2, 3, 0)) > new Version(1, 2, 3));
        Assert.Equal(-1, ReleaseFeed.Current.Revision);
        Assert.True(ReleaseFeed.Current.Build >= 0);
    }

    private static Release At(Version v) => new(v, "Rigsight-Setup-x.exe", "http://x", 1, "00", "");

    [Fact]
    public void Only_a_higher_version_is_newer()
    {
        var c = ReleaseFeed.Current;
        Assert.False(ReleaseFeed.IsNewer(null));
        Assert.False(ReleaseFeed.IsNewer(At(c)));
        Assert.False(ReleaseFeed.IsNewer(At(ReleaseFeed.Normalize(new Version(c.Major, c.Minor, c.Build, 0)))));
        Assert.True(ReleaseFeed.IsNewer(At(new Version(c.Major, c.Minor, c.Build + 1))));
        Assert.True(ReleaseFeed.IsNewer(At(new Version(c.Major, c.Minor + 1, 0))));
        Assert.True(ReleaseFeed.IsNewer(At(new Version(c.Major + 1, 0, 0))));
        if (c.Build > 0) Assert.False(ReleaseFeed.IsNewer(At(new Version(c.Major, c.Minor, c.Build - 1))));
        Assert.False(ReleaseFeed.IsNewer(At(new Version(0, 0, 0))));
    }

    // ---- Checking a downloaded file ----

    private static (string Path, Release Release) Installer(int size = 300_000)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        var path = Path.Combine(TestEnvironment.NewFolder("feed"), "Rigsight-Setup-9.9.9.exe");
        File.WriteAllBytes(path, bytes);
        return (path, new Release(new Version(9, 9, 9), "Rigsight-Setup-9.9.9.exe", "", size, Convert.ToHexStringLower(SHA256.HashData(bytes)), ""));
    }

    [Fact]
    public async Task A_file_matches_when_size_and_hash_do()
    {
        var (path, release) = Installer();
        Assert.True(await ReleaseFeed.MatchesAsync(path, release));
    }

    [Fact]
    public async Task A_file_of_the_wrong_size_does_not_match()
    {
        var (path, release) = Installer();
        Assert.False(await ReleaseFeed.MatchesAsync(path, release with { Size = release.Size + 1 }));
        Assert.False(await ReleaseFeed.MatchesAsync(path, release with { Size = 0 }));
    }

    [Fact]
    public async Task A_file_with_one_byte_changed_does_not_match()
    {
        var (path, release) = Installer();
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.False(await ReleaseFeed.MatchesAsync(path, release));
    }

    [Fact]
    public async Task A_missing_file_does_not_match()
    {
        var (path, release) = Installer();
        File.Delete(path);
        Assert.False(await ReleaseFeed.MatchesAsync(path, release));
    }

    [Fact]
    public async Task An_empty_installer_matches_only_an_empty_release()
    {
        var (path, release) = Installer(0);
        Assert.True(await ReleaseFeed.MatchesAsync(path, release));
    }

    [Fact]
    public async Task A_file_still_open_for_reading_can_be_checked()
    {
        var (path, release) = Installer();
        using var open = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.True(await ReleaseFeed.MatchesAsync(path, release));
    }

    // ---- The HTTP client ----

    [Fact]
    public void The_client_says_who_it_is()
    {
        using var http = ReleaseFeed.CreateClient(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), http.Timeout);
        Assert.Equal($"Rigsight/{ReleaseFeed.Current.ToString(3)}", http.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Contains(http.DefaultRequestHeaders.Accept, a => a.MediaType == "application/vnd.github+json");
    }

    [Fact]
    public async Task The_client_connects_to_127_0_0_1()
    {
        using var server = TestHttpServer.Serving("hello"u8.ToArray());
        using var http = ReleaseFeed.CreateClient(TimeSpan.FromSeconds(10));
        Assert.Equal("hello", await http.GetStringAsync(server.Url()));
    }

    [Fact]
    public async Task The_client_reaches_localhost_even_when_its_first_address_does_not_answer()
    {
        // localhost is ::1 first, then 127.0.0.1; this server only listens on the second.
        using var server = TestHttpServer.Serving("v4"u8.ToArray());
        using var http = ReleaseFeed.CreateClient(TimeSpan.FromSeconds(10));
        Assert.Equal("v4", await http.GetStringAsync($"http://localhost:{server.Port}/"));
    }

    [Fact]
    public async Task The_client_reaches_an_IPv6_only_server()
    {
        if (!Socket.OSSupportsIPv6) Assert.Skip("No IPv6 on this machine");
        using var server = new TestHttpServer((_, _, c, ct) => TestHttpServer.Send(c, 200, "v6"u8.ToArray(), ct: ct), IPAddress.IPv6Loopback);
        using var http = ReleaseFeed.CreateClient(TimeSpan.FromSeconds(10));
        Assert.Equal("v6", await http.GetStringAsync($"http://localhost:{server.Port}/"));
        Assert.Equal("v6", await http.GetStringAsync($"http://[::1]:{server.Port}/"));
    }

    [Fact]
    public async Task Nobody_listening_is_an_error()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        using var http = ReleaseFeed.CreateClient(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync($"http://127.0.0.1:{port}/"));
    }

    [Fact]
    public async Task An_unknown_host_is_an_error()
    {
        using var http = ReleaseFeed.CreateClient(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync("http://rigsight-no-such-host.invalid/"));
    }

    [Fact]
    public async Task Connecting_can_be_cancelled()
    {
        using var http = ReleaseFeed.CreateClient(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        // A TEST-NET address: nothing answers, so the connection hangs until cancelled.
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetStringAsync("http://192.0.2.1:81/", cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    // ---- Security: the released agent only ever trusts GitHub ----

    private static string SourceOf(string file) => File.ReadAllText(Path.Combine(Repo.Root, "src", "Rigsight.Core", file));

    [Fact]
    public void Released_builds_never_read_the_test_feed_variable()
    {
        var source = SourceOf(Path.Combine("Updates", "ReleaseFeed.cs"));
        // Every mention of the variable in code sits inside the Debug / test-installer-only block.
        var code = Regex.Replace(source, @"^\s*///.*$", "", RegexOptions.Multiline);
        var guarded = Regex.Matches(code, @"#if DEBUG \|\| RIGSIGHT_TEST_FEED\r?\n(.*?)#endif", RegexOptions.Singleline);
        var inside = string.Concat(guarded.Select(m => m.Groups[1].Value));
        int all = Regex.Matches(code, "RIGSIGHT_UPDATE_FEED").Count;
        Assert.True(all > 0, "the feed variable is expected in ReleaseFeed.cs");
        Assert.Equal(all, Regex.Matches(inside, "RIGSIGHT_UPDATE_FEED").Count);
        Assert.Contains("https://api.github.com/repos/b0llu/Rigsight/releases/latest", source);
        // Nothing else in the product reads it.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo.Root, "src"), "*.cs", SearchOption.AllDirectories))
            if (!file.EndsWith("ReleaseFeed.cs")) Assert.DoesNotContain("RIGSIGHT_UPDATE_FEED", File.ReadAllText(file));
    }

    [Fact]
    public void The_test_feed_symbol_is_only_defined_on_request()
    {
        var props = File.ReadAllText(Path.Combine(Repo.Root, "Directory.Build.props"));
        foreach (Match m in Regex.Matches(props, @"<DefineConstants[^>]*>[^<]*RIGSIGHT_TEST_FEED[^<]*</DefineConstants>"))
            Assert.Contains("Condition=\"'$(RigsightTestFeed)' == 'true'\"", m.Value);
        foreach (var csproj in Directory.EnumerateFiles(Path.Combine(Repo.Root, "src"), "*.csproj", SearchOption.AllDirectories))
            Assert.DoesNotContain("RIGSIGHT_TEST_FEED", File.ReadAllText(csproj));
    }

    [Fact]
    public void A_release_build_of_Core_if_present_does_not_contain_the_variable()
    {
        // The Debug build this test runs against does contain it (the check below would find it).
        Assert.True(File.ReadAllBytes(typeof(ReleaseFeed).Assembly.Location).AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes("RIGSIGHT_UPDATE_FEED")) >= 0 ||
                    !IsDebug(typeof(ReleaseFeed).Assembly));
        var dlls = new[] { Path.Combine(Repo.Root, "bin", "Release"), Path.Combine(Repo.Root, "src", "Rigsight.Core", "bin", "Release") }
            .Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "Rigsight.Core.dll", SearchOption.AllDirectories)).ToList();
        if (dlls.Count == 0) Assert.Skip("No Release build in this copy (build with -c Release to check the binary)");
        var needle = System.Text.Encoding.Unicode.GetBytes("RIGSIGHT_UPDATE_FEED");
        foreach (var dll in dlls)
            Assert.True(File.ReadAllBytes(dll).AsSpan().IndexOf(needle) < 0, $"{dll} reads RIGSIGHT_UPDATE_FEED");
    }

    private static bool IsDebug(System.Reflection.Assembly a) =>
        a.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false).OfType<System.Diagnostics.DebuggableAttribute>().Any(d => d.IsJITOptimizerDisabled);
}
