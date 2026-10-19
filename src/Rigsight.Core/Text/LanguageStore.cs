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
    private const string ListApi = "https://api.github.com/repos/b0llu/Rigsight/releases?per_page=30";

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
        if (!string.IsNullOrEmpty(code) && code != LanguagePack.English) PromoteNext(code);
        var pack = Find(code);
        Lang.Use(pack);
        UseNames(pack);
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

    /// <summary>Windows' own region settings, as this program started with them.</summary>
    private static readonly System.Globalization.CultureInfo Region = System.Globalization.CultureInfo.CurrentCulture;

    /// <summary>
    /// Day and month names in the pack's language ("lunes", "oct."), while numbers, the clock and the order of a date
    /// keep following Windows' region settings. English (no pack) goes back to the region exactly as it was.
    /// </summary>
    private static void UseNames(LanguagePack? pack)
    {
        var culture = pack is null ? Region : NamesFor(pack, Region);
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture == Region ? null : culture;
    }

    /// <summary><paramref name="region"/> with the pack language's day and month names (see <see cref="UseNames"/>).</summary>
    public static System.Globalization.CultureInfo NamesFor(LanguagePack pack, System.Globalization.CultureInfo region)
    {
        try
        {
            var names = System.Globalization.CultureInfo.GetCultureInfo(pack.Code).DateTimeFormat;
            var culture = (System.Globalization.CultureInfo)region.Clone();
            var f = culture.DateTimeFormat;
            f.DayNames = names.DayNames;
            f.AbbreviatedDayNames = names.AbbreviatedDayNames;
            f.ShortestDayNames = names.ShortestDayNames;
            f.MonthNames = names.MonthNames;
            f.AbbreviatedMonthNames = names.AbbreviatedMonthNames;
            // A day before the month ("d MMMM") takes these: "19 de octubre" where the pack says so.
            f.MonthGenitiveNames = pack.MonthPrefix.Length == 0 ? names.MonthGenitiveNames
                : [.. names.MonthNames.Select(m => m.Length == 0 ? m : pack.MonthPrefix + m)];
            f.AbbreviatedMonthGenitiveNames = names.AbbreviatedMonthNames;
            return culture;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return region;
        }
    }

    /// <summary>
    /// The language the pages' own bindings format dates in while a pack is in use: Windows' region when it's already
    /// in the pack's language (es-MX for Spanish), else the pack's language itself.
    /// </summary>
    public static string BindingLanguage(LanguagePack pack) =>
        Region.TwoLetterISOLanguageName.Equals(pack.Code, StringComparison.OrdinalIgnoreCase) ? Region.IetfLanguageTag : pack.Code;

    /// <summary>
    /// The warning's condition: a pack is in use but not this version's, so the newest text shows in English (its
    /// download with the update and the try after it both failed, or the update was installed by hand). Settings →
    /// Language downloads it again.
    /// </summary>
    public static bool NewestMissing => Lang.Pack is { } pack && IsOlder(pack);

    /// <summary>Whether the downloaded pack was made for an older version, so a newer one may have more of the text.</summary>
    public static bool IsOlder(LanguagePack pack) =>
        ReleaseFeed.ParseVersion(pack.ForApp) is not { } made || made < ReleaseFeed.Current;

    /// <summary>How many earlier releases are tried for a pack when this version's can't be had.</summary>
    private const int EarlierTries = 5;

    /// <summary>
    /// Downloads <paramref name="code"/>'s pack, checks its size and SHA-256 against GitHub's and reads it, and only then
    /// keeps it. This version's pack first; if that can't be had (not attached, a download that failed, a file that
    /// isn't right), the newest earlier release's pack instead: only the text added since then stays English. A pack
    /// already downloaded is never replaced by an older one. Throws only when no pack could be had at all.
    /// </summary>
    public static Task<LanguagePack> DownloadAsync(string code, CancellationToken ct = default) =>
        GetAsync(code, ReleaseFeed.Current, nextPath: false, ct);

    /// <summary>
    /// An update to <paramref name="target"/> is being downloaded: the pack in use is downloaded for that version too
    /// and set aside (languages\es.next.json), so the updated app starts with it and nobody has to download it again.
    /// Nothing to do in English. Returns whether it's ready; never throws (the updated app tries once more, then
    /// Settings says the newest pack couldn't be had).
    /// </summary>
    public static async Task<bool> PrepareForUpdateAsync(string? code, Version target, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(code) || code == LanguagePack.English) return true;
        target = ReleaseFeed.Normalize(target);
        if (ForVersion(NextPackPath(code), code, target)) return true; // set aside already (the app and the agent both ask)
        try
        {
            var next = await GetAsync(code, target, nextPath: true, ct);
            return ReleaseFeed.ParseVersion(next.ForApp) == target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Write("Language", $"The {code} pack for the update to {target.ToString(3)} couldn't be had; the updated app tries again: {ex.Message}");
            return false;
        }
    }

    /// <summary>Whether the file is <paramref name="code"/>'s pack made for exactly <paramref name="version"/>.</summary>
    private static bool ForVersion(string path, string code, Version version)
    {
        try { return File.Exists(path) && LanguagePack.Load(path) is { } p && p.Code == code && ReleaseFeed.ParseVersion(p.ForApp) == version; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    /// <summary>The pack set aside for an update, until the updated app puts it in place.</summary>
    public static string NextPackPath(string code) => Path.Combine(Folder, code + ".next.json");

    /// <summary>
    /// The pack set aside for an update goes in place once this version is the one it was made for (or later), and
    /// only if it's newer than the one here. One that's for a version still to come waits; one that doesn't read goes.
    /// </summary>
    internal static void PromoteNext(string code)
    {
        string next = NextPackPath(code);
        if (!File.Exists(next)) return;
        try
        {
            var pack = LanguagePack.Load(next);
            var made = ReleaseFeed.ParseVersion(pack.ForApp);
            if (pack.Code != code) throw new InvalidDataException($"it's the \"{pack.Code}\" pack");
            if (made is null || made > ReleaseFeed.Current) return; // the update isn't installed yet
            if (Find(code) is { } here && !IsNewer(pack, here)) { File.Delete(next); return; }
            File.Move(next, PackPath(code), overwrite: true);
            Log.Write("Language", $"Put in place the {code} pack downloaded with the update: v{pack.Version} for {pack.ForApp}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Write("Language", $"The {code} pack set aside for the update couldn't be put in place: {ex.Message}");
            try { File.Delete(next); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<LanguagePack> GetAsync(string code, Version target, bool nextPath, CancellationToken ct)
    {
        using var http = ReleaseFeed.CreateClient(TimeSpan.FromSeconds(30));
        string asset = AssetName(code);
        Exception? first = null;

        // This version's release.
        try
        {
            await using var stream = await http.GetStreamAsync(FeedUrl(target.ToString(3)), ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var found = FindAsset(doc.RootElement, asset)
                ?? throw new FileNotFoundException($"Release {target.ToString(3)} has no {asset} with a checksum");
            return await FetchAsync(http, code, target, found, nextPath, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            first = ex;
            Log.Write("Language", $"{asset} for {target.ToString(3)} couldn't be had, so earlier releases are tried: {ex.Message}");
        }

        // The newest earlier releases that have one.
        List<(Version Version, (string Url, long Size, string Sha256) Asset)> earlier;
        try
        {
            await using var stream = await http.GetStreamAsync(ListUrl, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            earlier = [.. Earlier(doc.RootElement, asset, target).Take(EarlierTries)];
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"No {asset} could be had: {first.Message}; the list of releases: {ex.Message}", first);
        }
        foreach (var (version, found) in earlier)
        {
            try { return await FetchAsync(http, code, version, found, nextPath, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Write("Language", $"{asset} from {version.ToString(3)} couldn't be had either: {ex.Message}");
            }
        }
        throw new FileNotFoundException($"No release up to {target.ToString(3)} gave a usable {asset} ({earlier.Count} earlier tried): {first.Message}", first);
    }

    /// <summary>Downloads one release's pack, checks it, and keeps it unless the one already here is newer.</summary>
    private static async Task<LanguagePack> FetchAsync(HttpClient http, string code, Version release, (string Url, long Size, string Sha256) found, bool next, CancellationToken ct)
    {
        string asset = AssetName(code);
        byte[] bytes = await http.GetByteArrayAsync(found.Url, ct);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (bytes.LongLength != found.Size || hash != found.Sha256)
            throw new InvalidDataException($"{asset} came down as {bytes.LongLength} bytes, sha256 {hash}; GitHub says {found.Size} bytes, {found.Sha256}");

        var pack = LanguagePack.Read(System.Text.Encoding.UTF8.GetString(bytes));
        if (pack.Code != code) throw new InvalidDataException($"{asset} is the \"{pack.Code}\" pack");

        if (Find(code) is { } here && !IsNewer(pack, here))
        {
            Log.Write("Language", $"{asset} from {release.ToString(3)} (v{pack.Version} for {pack.ForApp}) is no newer than the one here (v{here.Version} for {here.ForApp}): kept the one here");
            return here;
        }
        Directory.CreateDirectory(Folder);
        // The app and the agent may both be fetching: each writes its own file and moves it into place.
        string path = next ? NextPackPath(code) : PackPath(code), temp = $"{path}.{Environment.ProcessId}.new";
        await File.WriteAllBytesAsync(temp, bytes, ct);
        File.Move(temp, path, overwrite: true);
        Log.Write("Language", $"Downloaded {asset} from {release.ToString(3)} for {ReleaseFeed.Current.ToString(3)}: v{pack.Version} for {pack.ForApp}, {pack.Strings.Count} texts, {bytes.LongLength} bytes, sha256 {hash}");
        return pack;
    }

    /// <summary>Whether <paramref name="a"/> was made for a later version than <paramref name="b"/>, or is a later fix of the same one.</summary>
    public static bool IsNewer(LanguagePack a, LanguagePack b)
    {
        var va = ReleaseFeed.ParseVersion(a.ForApp) ?? new Version(0, 0, 0);
        var vb = ReleaseFeed.ParseVersion(b.ForApp) ?? new Version(0, 0, 0);
        return va != vb ? va > vb : a.Version > b.Version;
    }

    /// <summary>
    /// The releases before <paramref name="current"/> that carry the asset with a checksum, newest first, from GitHub's
    /// list of releases. Drafts and pre-releases are left out.
    /// </summary>
    public static IEnumerable<(Version Version, (string Url, long Size, string Sha256) Asset)> Earlier(JsonElement releases, string asset, Version current)
    {
        if (releases.ValueKind != JsonValueKind.Array) return [];
        var list = new List<(Version, (string, long, string))>();
        foreach (var r in releases.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) continue;
            if (!r.TryGetProperty("tag_name", out var tag) || ReleaseFeed.ParseVersion(tag.GetString()) is not { } v || v >= current) continue;
            if (FindAsset(r, asset) is { } found) list.Add((v, found));
        }
        return list.OrderByDescending(x => x.Item1);
    }

    /// <summary>
    /// After an update: the pack in use was made for an older version, so this version's pack (with the new text) is
    /// downloaded and put in use. Only where updates may contact GitHub by themselves (<paramref name="mayCheck"/>:
    /// Update automatically on, and never a test copy: the caller decides); otherwise Settings offers it. Returns whether a newer pack came.
    /// Never throws: with no network the old pack stays, and its missing text stays English.
    /// </summary>
    public static async Task<bool> RefreshAsync(bool mayCheck, CancellationToken ct = default)
    {
        var pack = Lang.Pack;
        if (pack is null || !IsOlder(pack)) return false;
        // The other program (app or agent) may have fetched it already.
        if (Find(pack.Code) is { } here && IsNewer(here, pack))
        {
            Lang.Use(here);
            if (!IsOlder(here)) return true;
            pack = here;
        }
        if (!mayCheck) return false;
        try
        {
            var fresh = await DownloadAsync(pack.Code, ct);
            if (!IsNewer(fresh, pack)) return false; // only an earlier pack could be had: the one in use stays
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

    /// <summary>GitHub's list of releases, for an earlier pack. Debug builds can point it at a test server (RIGSIGHT_LANGUAGE_LIST).</summary>
    private static string ListUrl =>
#if DEBUG
        Environment.GetEnvironmentVariable("RIGSIGHT_LANGUAGE_LIST") is { Length: > 0 } url ? url :
#endif
        ListApi;
}
