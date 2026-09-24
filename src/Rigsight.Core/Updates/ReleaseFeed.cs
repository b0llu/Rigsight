using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rigsight.Core.Updates;

/// <summary>A published Rigsight release: its version and the installer to download.</summary>
public sealed record Release(Version Version, string AssetName, string DownloadUrl, long Size, string Sha256, string PageUrl);

/// <summary>
/// Finds the latest release on GitHub. The installer's size and SHA-256 come from GitHub's own asset
/// metadata (the <c>digest</c> field), so a download can be checked before it's run.
/// </summary>
public static class ReleaseFeed
{
    private const string LatestApi = "https://api.github.com/repos/b0llu/Rigsight/releases/latest";

    /// <summary>Installer switches for "Restart" in the app: nothing on screen, and the app opened again at the end.</summary>
    public const string SetupArguments = "/VERYSILENT /SP- /SUPPRESSMSGBOXES /NORESTART /RELAUNCH";

    /// <summary>Installer switches for an update installed by itself as Windows starts: nothing on screen.</summary>
    public const string BackgroundArguments = "/VERYSILENT /SP- /SUPPRESSMSGBOXES /NORESTART";

    /// <summary>This build's version (major.minor.patch).</summary>
    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>
    /// Where the release comes from. Debug builds, and test installers built with RigsightTestFeed=true, can point
    /// it at a test server (RIGSIGHT_UPDATE_FEED); released builds never read it, since the elevated agent must only
    /// ever trust GitHub.
    /// </summary>
    private static string FeedUrl =>
#if DEBUG || RIGSIGHT_TEST_FEED
        Environment.GetEnvironmentVariable("RIGSIGHT_UPDATE_FEED") is { Length: > 0 } url ? url :
#endif
        LatestApi;

    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Rigsight", Current.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>The latest release, or null if it has no installer with a checksum. Throws when GitHub can't be reached.</summary>
    public static async Task<Release?> GetLatestAsync(CancellationToken ct = default)
    {
        using var http = CreateClient(TimeSpan.FromSeconds(20));
        await using var stream = await http.GetStreamAsync(FeedUrl, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    public static Release? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out var tag) || ParseVersion(tag.GetString()) is not { } version) return null;
        string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("Rigsight-Setup-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
            return new Release(version, Path.GetFileName(name), asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.GetProperty("size").GetInt64(), digest[7..].ToLowerInvariant(), page);
        }
        return null;
    }

    public static Version? ParseVersion(string? tag) =>
        Version.TryParse(tag?.TrimStart('v', 'V'), out var v) ? Normalize(v) : null;

    /// <summary>1.2.3 and 1.2.3.0 are the same version (Version alone would call the first one older).</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public static bool IsNewer(Release? release) => release is not null && release.Version > Current;

    /// <summary>Whether a file is exactly the release's installer (same size and SHA-256).</summary>
    public static async Task<bool> MatchesAsync(string path, Release release, CancellationToken ct = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != release.Size) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash) == release.Sha256;
    }
}
