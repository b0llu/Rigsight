using System.Diagnostics;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Core.Updates;

namespace Rigsight.Agent;

/// <summary>
/// Runs an installer the app downloaded, with the agent's admin rights, so updating needs no UAC prompt.
/// Any program running as the user can ask for this over the pipe, so nothing about the request is
/// trusted: the file is copied somewhere only administrators can write (next to the agent, in Program
/// Files), and that copy runs only if it is byte for byte the installer of GitHub's latest release,
/// looked up by the agent itself. The worst a stranger could do is install the real latest Rigsight.
/// </summary>
internal static class UpdateInstaller
{
    private static string Folder => Path.Combine(AppContext.BaseDirectory, "update");
    private static int _running;

    /// <summary>Whether the installer was started.</summary>
    public static async Task<bool> RunAsync(string? downloaded, string arguments = ReleaseFeed.SetupArguments)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return false;
        try
        {
            if (string.IsNullOrEmpty(downloaded) || !File.Exists(downloaded)) return false;
            var release = await ReleaseFeed.GetLatestAsync();
            if (!ReleaseFeed.IsNewer(release)) return false;

            Directory.CreateDirectory(Folder);
            string setup = Path.Combine(Folder, release!.AssetName);
            File.Copy(downloaded, setup, overwrite: true);
            if (!await ReleaseFeed.MatchesAsync(setup, release))
            {
                Log.Write("update", $"{downloaded} isn't the {release.Version} installer; not running it");
                File.Delete(setup);
                return false;
            }

            Log.Write("update", $"Installing {release.Version}");
            // Recorded first: the installer stops every Rigsight process, this one included. If this copy is
            // still older afterwards, the app says the update didn't install (and it isn't retried at every start).
            UpdateStore.WriteAttempt(release.Version);
            Process.Start(new ProcessStartInfo(setup, arguments) { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("update", ex);
            return false;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>After an update: the installer that ran is no longer needed.</summary>
    public static void Cleanup()
    {
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch
        {
            // Still in use by the installer that just started this agent: next start tidies it up.
        }
    }
}

/// <summary>
/// The agent's updater, run as a short-lived "Rigsight.Agent.exe --update" process so the always-running
/// agent never keeps an HTTP stack in memory. It checks GitHub (at most once a day, sharing the answer
/// with the app), downloads a newer installer if automatic updates are on, and, right after Windows starts,
/// installs one that was downloaded earlier and never installed.
/// </summary>
internal static class BackgroundUpdater
{
    public const int UpToDate = 0, Failed = 1, Downloaded = 2, Installing = 3, Available = 4;

    private static bool AppIsOpen()
    {
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(RigsightPaths.AppExe));
        foreach (var p in running) p.Dispose();
        return running.Length > 0;
    }

    /// <param name="atStartup">Just after sign-in, with the window closed: a waiting update may be installed now.</param>
    /// <param name="download">False while the app is open: it shows the download itself.</param>
    public static async Task<int> RunAsync(bool atStartup, bool download)
    {
        var settings = SettingsStore.Load();
        var attempt = UpdateStore.ReadAttempt();
        if (attempt is not null && attempt.Version <= ReleaseFeed.Current) UpdateStore.ClearAttempt(); // it worked

        // Windows may still be connecting at sign-in: give GitHub a few tries.
        Release? latest = null;
        for (int i = 0; ; i++)
        {
            try
            {
                latest = await UpdateStore.LatestAsync(force: atStartup);
                break;
            }
            catch (Exception ex) when (i < (atStartup ? 5 : 1))
            {
                Log.Write("update", $"Check failed, retrying: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                Log.Write("update", $"Check failed: {ex.Message}");
                return Failed;
            }
        }

        bool newer = ReleaseFeed.IsNewer(latest);
        UpdateStore.Tidy(newer ? latest : null);
        if (!newer) return UpToDate;

        if (await UpdateStore.DownloadedAsync() is { } ready)
        {
            // Downloaded in an earlier session and still not installed: install it now, once. A version that
            // was already tried and didn't take is left to the app, which offers "Try again" and the download page.
            // The window is checked again right before: opened in the meantime, it's left alone (the app offers the update).
            if (atStartup && settings.AutoUpdate && ReleaseFeed.Normalize(attempt?.Version ?? new Version()) != ready.Version
                && !AppIsOpen() && await UpdateInstaller.RunAsync(UpdateStore.SetupPath(ready), ReleaseFeed.BackgroundArguments))
                return Installing;
            return Downloaded;
        }
        if (!settings.AutoUpdate || !download) return Available;

        try
        {
            await UpdateStore.DownloadAsync(latest!);
            Log.Write("update", $"Downloaded {latest!.Version}");
            return Downloaded;
        }
        catch (Exception ex)
        {
            // Tried again at the next check; meanwhile the app offers the download itself.
            Log.Write("update", $"Download failed: {ex.Message}");
            return Available;
        }
    }
}
