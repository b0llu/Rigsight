using System.Security.Cryptography;
using System.Text.Json;

namespace Rigsight.Core.Updates;

/// <summary>What the day's check found (null = no usable release).</summary>
public sealed record CheckResult(DateTime Day, Release? Latest);

/// <summary>An update install that was started: if this copy is still older afterwards, it didn't work.</summary>
public sealed record InstallAttempt(Version Version, DateTime At);

/// <summary>
/// The update folder (%LocalAppData%\Rigsight\updates), shared by the app and the agent's background updater:
/// the day's check, the downloaded installer, and the last install attempt. Whichever of them checks or
/// downloads first, the other one finds it here.
/// </summary>
public static class UpdateStore
{
    public static string Folder => Path.Combine(RigsightPaths.DataDir, "updates");
    private static string CheckFile => Path.Combine(Folder, "latest.json");
    private static string AttemptFile => Path.Combine(Folder, "attempt.json");

    public static string SetupPath(Release release) => Path.Combine(Folder, release.AssetName);

    public static CheckResult? ReadCheck() => Read<CheckResult>(CheckFile);
    public static InstallAttempt? ReadAttempt() => Read<InstallAttempt>(AttemptFile);

    public static void WriteAttempt(Version version) => Write(AttemptFile, new InstallAttempt(version, DateTime.Now));

    public static void ClearAttempt()
    {
        try { File.Delete(AttemptFile); } catch { }
    }

    /// <summary>The day's answer: from the file if already checked today, otherwise from GitHub (and saved).</summary>
    public static async Task<Release?> LatestAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && ReadCheck() is { } saved && saved.Day == DateTime.Today) return saved.Latest;
        var latest = await ReleaseFeed.GetLatestAsync(ct);
        Write(CheckFile, new CheckResult(DateTime.Today, latest));
        return latest;
    }

    /// <summary>The newer release whose installer is already downloaded and intact, if any.</summary>
    public static async Task<Release?> DownloadedAsync()
    {
        var latest = ReadCheck()?.Latest;
        return ReleaseFeed.IsNewer(latest) && await ReleaseFeed.MatchesAsync(SetupPath(latest!), latest!) ? latest : null;
    }

    /// <summary>
    /// Deletes installers other than <paramref name="keep"/>'s (older ones, and all of them once up to date), and
    /// partial downloads left by a download that was cut off. One still being written is in use and stays.
    /// </summary>
    public static void Tidy(Release? keep)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            string? keepPath = keep is null ? null : SetupPath(keep);
            foreach (var file in Directory.EnumerateFiles(Folder, "Rigsight-Setup-*"))
            {
                if (string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    /// <summary>No data for this long: the download is stuck (it happens with GitHub's download servers now and then).</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A stuck download is started again this many times before giving up.</summary>
    private const int StallRetries = 2;

    /// <summary>
    /// Downloads the release's installer, hashing it as it streams: it's kept only if its size and SHA-256 match
    /// what GitHub published (<see cref="InvalidDataException"/> otherwise). Progress is reported in bytes, a few times a second.
    /// A download that gets no data for <see cref="StallTimeout"/> starts again, twice at most, then throws
    /// <see cref="TimeoutException"/>. Anything else (no connection, an error from GitHub) fails straight away.
    /// </summary>
    public static async Task DownloadAsync(Release release, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(release, progress, ct);
                return;
            }
            catch (TimeoutException) when (attempt < StallRetries)
            {
                Log.Write("update", $"Download of {release.Version.ToString(3)} stalled, starting again");
            }
        }
    }

    private static async Task DownloadOnceAsync(Release release, IProgress<long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        string setup = SetupPath(release);
        // Unique, so the app and the agent's updater never write the same partial file.
        string part = $"{setup}.{Environment.ProcessId}.part";
        try
        {
            // Pushed back every time data arrives; if it fires, nothing came for StallTimeout.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallTimeout);
            try
            {
                await DownloadToAsync(part, release, progress, stall);
            }
            catch (OperationCanceledException) when (stall.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No data for {StallTimeout.TotalSeconds:0} s");
            }
            File.Move(part, setup, overwrite: true);
        }
        finally
        {
            try { File.Delete(part); } catch { }
        }
    }

    private static async Task DownloadToAsync(string part, Release release, IProgress<long>? progress, CancellationTokenSource stall)
    {
        var ct = stall.Token;
        using var http = ReleaseFeed.CreateClient(TimeSpan.FromMinutes(30));
        using var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long done = 0, reportedAt = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                done += read;
                stall.CancelAfter(StallTimeout);
                if (Environment.TickCount64 - reportedAt >= 250)
                {
                    progress?.Report(done);
                    reportedAt = Environment.TickCount64;
                }
            }
            progress?.Report(done);
            if (done != release.Size) throw new InvalidDataException($"Downloaded {done} bytes, expected {release.Size}");
        }
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Sha256)
            throw new InvalidDataException("The installer doesn't match GitHub's SHA-256");
    }

    private static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }
        catch (Exception ex)
        {
            Log.Error("update", ex);
        }
    }
}
