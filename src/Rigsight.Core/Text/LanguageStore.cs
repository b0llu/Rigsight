using System.Security.Cryptography;
using System.Text.Json;
using Rigsight.Core.Updates;

namespace Rigsight.Core;

/// <summary>
/// Downloaded language packs (%LocalAppData%\Rigsight\languages\es.json), one folder per person like the rest of
/// their settings, read by the app and the agent alike. A pack comes from the GitHub release of the version that's
/// running, as its asset Rigsight-Language-&lt;code&gt;.json, and is checked against GitHub's SHA-256 for it before
/// it's kept. Nothing is ever downloaded unless the person picks the language.
/// </summary>
public static class LanguageStore
{
    private const string ReleaseApi = "https://api.github.com/repos/b0llu/Rigsight/releases/tags/v";

    public static string Folder => Path.Combine(RigsightPaths.DataDir, "languages");

    public static string PackPath(string code) => Path.Combine(Folder, code + ".json");

    public static string AssetName(string code) => $"Rigsight-Language-{code}.json";

    /// <summary>The pack for <paramref name="code"/> if it's downloaded and reads; null for English or when it doesn't.</summary>
    public static LanguagePack? Find(string? code)
    {
        if (string.IsNullOrEmpty(code) || code == LanguagePack.English) return null;
        string path = PackPath(code);
        if (!File.Exists(path)) return null;
        try
        {
            var pack = LanguagePack.Load(path);
            if (pack.Code != code) throw new InvalidDataException($"The file is the \"{pack.Code}\" pack");
            return pack;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Write("Language", $"The {code} pack at {path} couldn't be read, so the text stays English: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Puts the person's language in use in this program: its pack, or English when it's English or the pack isn't
    /// there. Logs what it expected and what it found.
    /// </summary>
    public static void Apply(string? code)
    {
        var pack = Find(code);
        Lang.Use(pack);
        if (pack is not null)
        {
            string fit = pack.Dropped.Count == 0 ? "" : $"; {pack.Dropped.Count} left English because they didn't fit (first: \"{pack.Dropped[0]}\")";
            Log.Write("Language", $"Using {pack.Name} ({pack.Code}) pack v{pack.Version} for {pack.ForApp}, {pack.Strings.Count} texts{fit}; this is {ReleaseFeed.Current.ToString(3)}");
        }
        else if (!string.IsNullOrEmpty(code) && code != LanguagePack.English)
        {
            Log.Write("Language", $"{code} was picked but no pack is at {PackPath(code)}: English");
        }
    }

    /// <summary>Whether the downloaded pack was made for an older version, so a newer one may have more of the text.</summary>
    public static bool IsOlder(LanguagePack pack) =>
        ReleaseFeed.ParseVersion(pack.ForApp) is not { } made || made < ReleaseFeed.Current;

    /// <summary>
    /// Downloads <paramref name="code"/>'s pack for the running version, checks its size and SHA-256 against GitHub's,
    /// reads it, and only then puts it in place of any older one. Returns the pack; throws when there's no pack for
    /// this version, GitHub can't be reached or the file isn't right.
    /// </summary>
    public static async Task<LanguagePack> DownloadAsync(string code, CancellationToken ct = default)
    {
        string version = ReleaseFeed.Current.ToString(3);
        using var http = ReleaseFeed.CreateClient(TimeSpan.FromSeconds(30));
        string asset = AssetName(code);

        (string url, long size, string sha256) found;
        await using (var stream = await http.GetStreamAsync(FeedUrl(version), ct))
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            found = FindAsset(doc.RootElement, asset)
                ?? throw new FileNotFoundException($"Release {version} has no {asset} with a checksum");
        }

        byte[] bytes = await http.GetByteArrayAsync(found.url, ct);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.LongLength != found.size || hash != found.sha256)
            throw new InvalidDataException($"{asset} came down as {bytes.LongLength} bytes, sha256 {hash}; GitHub says {found.size} bytes, {found.sha256}");

        var pack = LanguagePack.Read(System.Text.Encoding.UTF8.GetString(bytes));
        if (pack.Code != code) throw new InvalidDataException($"{asset} is the \"{pack.Code}\" pack");

        Directory.CreateDirectory(Folder);
        string path = PackPath(code), temp = path + ".new";
        await File.WriteAllBytesAsync(temp, bytes, ct);
        File.Move(temp, path, overwrite: true);
        Log.Write("Language", $"Downloaded {asset} for {version}: v{pack.Version}, {pack.Strings.Count} texts, {bytes.LongLength} bytes, sha256 {hash}");
        return pack;
    }

    /// <summary>
    /// After an update: the pack in use was made for an older version, so this version's pack (with the new text) is
    /// downloaded and put in use. Only where updates may contact GitHub by themselves (<paramref name="mayCheck"/>:
    /// Update automatically on, not a test copy); otherwise Settings offers it. Returns whether a newer pack came.
    /// Never throws: with no network the old pack stays, and its missing text stays English.
    /// </summary>
    public static async Task<bool> RefreshAsync(bool mayCheck, CancellationToken ct = default)
    {
        var pack = Lang.Pack;
        if (pack is null || !IsOlder(pack) || !mayCheck || RigsightPaths.IsTestInstance) return false;
        try
        {
            var fresh = await DownloadAsync(pack.Code, ct);
            Lang.Use(fresh);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Write("Language", $"The {pack.Code} pack is for {pack.ForApp} and this is {ReleaseFeed.Current.ToString(3)}; the newer pack couldn't be had, so the old one stays: {ex.Message}");
            return false;
        }
    }

    /// <summary>Deletes a downloaded pack (English needs none).</summary>
    public static void Remove(string code)
    {
        try { File.Delete(PackPath(code)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Language", $"Couldn't delete the {code} pack: {ex.Message}"); }
    }

    /// <summary>The asset's download address, size and SHA-256 in a GitHub release, or null if it has none with a checksum.</summary>
    public static (string Url, long Size, string Sha256)? FindAsset(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (!string.Equals(a.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase)) continue;
            string digest = a.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
            return (a.GetProperty("browser_download_url").GetString() ?? "", a.GetProperty("size").GetInt64(), digest[7..].ToLowerInvariant());
        }
        return null;
    }

    /// <summary>The release to take packs from. Debug builds can point it at a test server (RIGSIGHT_LANGUAGE_FEED).</summary>
    private static string FeedUrl(string version) =>
#if DEBUG
        Environment.GetEnvironmentVariable("RIGSIGHT_LANGUAGE_FEED") is { Length: > 0 } url ? url :
#endif
        ReleaseApi + version;
}
