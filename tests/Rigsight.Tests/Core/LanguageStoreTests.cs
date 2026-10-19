using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rigsight.Core;
using Rigsight.Core.Updates;

namespace Rigsight.Tests.Core;

/// <summary>
/// Getting a language pack: this version's from its release, or, when that can't be had, the newest earlier release's
/// (only the newest text stays English), and never anything that isn't checked. One test at a time: the test servers
/// are given through the process's environment.
/// </summary>
[Collection("LanguageStore")]
public sealed class LanguageStoreTests : IDisposable
{
    private static readonly Version Current = ReleaseFeed.Current;
    private static readonly string Earlier = new Version(Current.Major, Current.Minor > 0 ? Current.Minor - 1 : 0, 0).ToString(3);
    private static readonly string Oldest = "0.0.1";

    public LanguageStoreTests()
    {
        Assert.True(Current > new Version(0, 1, 0), $"these tests need a version above 0.1.0 (this is {Current})");
        LanguageStore.Remove("es");
        try { File.Delete(LanguageStore.NextPackPath("es")); } catch (IOException) { }
    }

    public void Dispose()
    {
        try { File.Delete(LanguageStore.NextPackPath("es")); } catch (IOException) { }
        Environment.SetEnvironmentVariable("RIGSIGHT_LANGUAGE_FEED", null);
        Environment.SetEnvironmentVariable("RIGSIGHT_LANGUAGE_LIST", null);
        LanguageStore.Remove("es");
    }

    private static byte[] PackFile(string forApp, int version = 1, string crashes = "Fallos") => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        code = "es",
        name = "Español",
        version,
        forApp,
        strings = new Dictionary<string, string> { ["Crashes"] = crashes },
    }));

    private static object Asset(string url, byte[] body, string? sha = null) => new
    {
        name = LanguageStore.AssetName("es"),
        size = body.Length,
        browser_download_url = url,
        digest = "sha256:" + (sha ?? Convert.ToHexStringLower(SHA256.HashData(body))),
    };

    /// <summary>A GitHub stand-in: "/tag" is this version's release, "/list" the list, "/files/..." the assets.</summary>
    private static TestHttpServer GitHub(Func<TestHttpServer, object?> tag, Func<TestHttpServer, object[]> list, Dictionary<string, byte[]> files)
    {
        TestHttpServer? self = null;
        var server = new TestHttpServer(async (path, _, client, ct) =>
        {
            if (path == "/tag" && tag(self!) is { } release) await TestHttpServer.Send(client, 200, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(release)), ct: ct);
            else if (path == "/list") await TestHttpServer.Send(client, 200, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(list(self!))), ct: ct);
            else if (files.TryGetValue(path, out var body)) await TestHttpServer.Send(client, 200, body, ct: ct);
            else await TestHttpServer.Send(client, 404, Encoding.UTF8.GetBytes("{}"), ct: ct);
        });
        self = server;
        Environment.SetEnvironmentVariable("RIGSIGHT_LANGUAGE_FEED", server.Url("/tag"));
        Environment.SetEnvironmentVariable("RIGSIGHT_LANGUAGE_LIST", server.Url("/list"));
        return server;
    }

    [Fact]
    public async Task This_versions_pack_is_downloaded_checked_and_kept()
    {
        var now = PackFile(Current.ToString(3));
        using var gh = GitHub(s => new { tag_name = "v" + Current.ToString(3), assets = new[] { Asset(s.Url("/files/now"), now) } }, _ => [],
            new() { ["/files/now"] = now });

        var pack = await LanguageStore.DownloadAsync("es");

        Assert.Equal(Current.ToString(3), pack.ForApp);
        Assert.Equal("Fallos", LanguageStore.Find("es")!.Strings["Crashes"]);
    }

    [Fact]
    public async Task With_no_pack_on_this_release_the_newest_earlier_one_is_used()
    {
        var earlier = PackFile(Earlier, crashes: "Fallos (antes)");
        var oldest = PackFile(Oldest, crashes: "Fallos (viejo)");
        using var gh = GitHub(
            _ => new { tag_name = "v" + Current.ToString(3), assets = Array.Empty<object>() },
            s =>
            [
                new { tag_name = "v" + Current.ToString(3), assets = Array.Empty<object>() },
                new { tag_name = "v" + Oldest, assets = new[] { Asset(s.Url("/files/oldest"), oldest) } },
                new { tag_name = "v" + Earlier, assets = new[] { Asset(s.Url("/files/earlier"), earlier) } },
            ],
            new() { ["/files/earlier"] = earlier, ["/files/oldest"] = oldest });

        var pack = await LanguageStore.DownloadAsync("es");

        Assert.Equal("Fallos (antes)", pack.Strings["Crashes"]);
        Assert.Equal(Earlier, LanguageStore.Find("es")!.ForApp);
    }

    [Fact]
    public async Task A_pack_that_fails_its_check_is_never_kept_and_an_earlier_one_is_tried()
    {
        var now = PackFile(Current.ToString(3));
        var earlier = PackFile(Earlier, crashes: "Fallos (antes)");
        using var gh = GitHub(
            s => new { tag_name = "v" + Current.ToString(3), assets = new[] { Asset(s.Url("/files/now"), now, sha: new string('0', 64)) } },
            s => [new { tag_name = "v" + Earlier, assets = new[] { Asset(s.Url("/files/earlier"), earlier) } }],
            new() { ["/files/now"] = now, ["/files/earlier"] = earlier });

        var pack = await LanguageStore.DownloadAsync("es");

        Assert.Equal("Fallos (antes)", pack.Strings["Crashes"]);
    }

    [Fact]
    public async Task A_working_pack_is_never_replaced_by_an_older_one()
    {
        // This version's pack is here already; GitHub now only gives an earlier one.
        Directory.CreateDirectory(LanguageStore.Folder);
        File.WriteAllBytes(LanguageStore.PackPath("es"), PackFile(Current.ToString(3), crashes: "Fallos (aquí)"));
        var earlier = PackFile(Earlier, crashes: "Fallos (antes)");
        using var gh = GitHub(
            _ => null,
            s => [new { tag_name = "v" + Earlier, assets = new[] { Asset(s.Url("/files/earlier"), earlier) } }],
            new() { ["/files/earlier"] = earlier });

        var pack = await LanguageStore.DownloadAsync("es");

        Assert.Equal("Fallos (aquí)", pack.Strings["Crashes"]);
        Assert.Equal("Fallos (aquí)", LanguageStore.Find("es")!.Strings["Crashes"]);
    }

    [Fact]
    public async Task With_no_pack_anywhere_it_says_so_and_nothing_is_written()
    {
        using var gh = GitHub(_ => null, _ => [], []);

        await Assert.ThrowsAnyAsync<IOException>(() => LanguageStore.DownloadAsync("es"));

        Assert.False(File.Exists(LanguageStore.PackPath("es")));
        Assert.Null(LanguageStore.Find("es"));
    }

    [Fact]
    public void A_broken_pack_on_disk_leaves_the_app_English()
    {
        Directory.CreateDirectory(LanguageStore.Folder);
        File.WriteAllText(LanguageStore.PackPath("es"), "{ not a pack");
        Assert.Null(LanguageStore.Find("es"));
        Assert.Null(LanguageStore.Find("en"));
        Assert.Null(LanguageStore.Find(null));
    }

    [Fact]
    public void Earlier_releases_are_newest_first_without_drafts_pre_releases_or_later_ones()
    {
        var body = PackFile(Earlier);
        var list = JsonSerializer.SerializeToElement(new object[]
        {
            new { tag_name = "v" + Oldest, assets = new[] { Asset("o", body) } },
            new { tag_name = "v" + Earlier, assets = new[] { Asset("e", body) } },
            new { tag_name = "v" + Current.ToString(3), assets = new[] { Asset("c", body) } },
            new { tag_name = "v99.0.0", assets = new[] { Asset("later", body) } },
            new { tag_name = "v0.0.2", draft = true, assets = new[] { Asset("d", body) } },
            new { tag_name = "v0.0.3", prerelease = true, assets = new[] { Asset("p", body) } },
        });
        Assert.Equal(["e", "o"], LanguageStore.Earlier(list, LanguageStore.AssetName("es"), Current).Select(x => x.Asset.Url));
    }

    [Fact]
    public async Task An_update_brings_its_pack_along_and_the_updated_app_starts_with_it()
    {
        // Spanish in use, made for this version; an update to a later one is downloading.
        Directory.CreateDirectory(LanguageStore.Folder);
        File.WriteAllBytes(LanguageStore.PackPath("es"), PackFile(Current.ToString(3), crashes: "Fallos (ahora)"));
        var target = new Version(Current.Major, Current.Minor + 1, 0);
        var next = PackFile(target.ToString(3), crashes: "Fallos (nuevo)");
        using var gh = GitHub(s => new { tag_name = "v" + target.ToString(3), assets = new[] { Asset(s.Url("/files/next"), next) } }, _ => [],
            new() { ["/files/next"] = next });
        Assert.True(await LanguageStore.PrepareForUpdateAsync("es", target));
        // Asked again (the app and the agent both ask): it's there already, nothing is fetched.
        int fetched = gh.Requests;
        Assert.True(await LanguageStore.PrepareForUpdateAsync("es", target));
        Assert.Equal(fetched, gh.Requests);

        // Before the update is installed, this version keeps its own pack: the next one waits.
        Assert.True(File.Exists(LanguageStore.NextPackPath("es")));
        LanguageStore.PromoteNext("es");
        Assert.Equal("Fallos (ahora)", LanguageStore.Find("es")!.Strings["Crashes"]);
        Assert.True(File.Exists(LanguageStore.NextPackPath("es")));
    }

    [Fact]
    public void The_pack_set_aside_goes_in_place_once_its_version_runs()
    {
        // As the updated app finds it: the pack downloaded with the update is for this version.
        Directory.CreateDirectory(LanguageStore.Folder);
        File.WriteAllBytes(LanguageStore.PackPath("es"), PackFile(Earlier, crashes: "Fallos (antes)"));
        File.WriteAllBytes(LanguageStore.NextPackPath("es"), PackFile(Current.ToString(3), crashes: "Fallos (nuevo)"));

        LanguageStore.PromoteNext("es");

        Assert.Equal("Fallos (nuevo)", LanguageStore.Find("es")!.Strings["Crashes"]);
        Assert.False(File.Exists(LanguageStore.NextPackPath("es")));
    }

    [Fact]
    public void A_pack_set_aside_that_is_older_or_broken_is_thrown_away()
    {
        Directory.CreateDirectory(LanguageStore.Folder);
        File.WriteAllBytes(LanguageStore.PackPath("es"), PackFile(Current.ToString(3), version: 3, crashes: "Fallos (aquí)"));
        File.WriteAllBytes(LanguageStore.NextPackPath("es"), PackFile(Current.ToString(3), version: 2, crashes: "Fallos (viejo)"));
        LanguageStore.PromoteNext("es");
        Assert.Equal("Fallos (aquí)", LanguageStore.Find("es")!.Strings["Crashes"]);
        Assert.False(File.Exists(LanguageStore.NextPackPath("es")));

        File.WriteAllText(LanguageStore.NextPackPath("es"), "{ broken");
        LanguageStore.PromoteNext("es");
        Assert.Equal("Fallos (aquí)", LanguageStore.Find("es")!.Strings["Crashes"]);
        Assert.False(File.Exists(LanguageStore.NextPackPath("es")));
    }

    [Fact]
    public async Task When_the_updates_pack_cant_be_had_it_says_so_and_the_warning_shows_after_the_update()
    {
        Directory.CreateDirectory(LanguageStore.Folder);
        File.WriteAllBytes(LanguageStore.PackPath("es"), PackFile(Earlier));
        using var gh = GitHub(_ => null, _ => [], []);
        using var _ = Lang.UseHere(LanguageStore.Find("es"));

        Assert.False(await LanguageStore.PrepareForUpdateAsync("es", new Version(Current.Major, Current.Minor + 1, 0)));
        Assert.False(File.Exists(LanguageStore.NextPackPath("es")));
        // The pack in use is older than this version: Settings shows the warning.
        Assert.True(LanguageStore.NewestMissing);
    }

    [Fact]
    public async Task In_English_an_update_needs_no_pack()
    {
        using var _ = Lang.UseHere(null);
        Assert.True(await LanguageStore.PrepareForUpdateAsync("en", new Version(Current.Major, Current.Minor + 1, 0)));
        Assert.True(await LanguageStore.PrepareForUpdateAsync(null, new Version(Current.Major, Current.Minor + 1, 0)));
        Assert.False(LanguageStore.NewestMissing);
    }
}
