namespace Rigsight.Core.Updates;

/// <summary>What an updater run does once it knows the latest release.</summary>
public enum UpdateStep
{
    /// <summary>No newer release.</summary>
    UpToDate,
    /// <summary>Newer, not downloaded, and this run may not download it: the app offers it.</summary>
    Offer,
    /// <summary>Download it; installing is left to a later run.</summary>
    Download,
    /// <summary>Downloaded, but this run doesn't install.</summary>
    Wait,
    /// <summary>This version was installed before and didn't take, too recently to try again.</summary>
    TriedBefore,
    /// <summary>A Rigsight window is open (the installer closes it) and nobody asked to update under it.</summary>
    WindowOpen,
    /// <summary>Install it, downloading it first if it isn't yet.</summary>
    Install,
}

/// <summary>How an updater differs from the other one.</summary>
/// <param name="RetryAfter">How long a version that didn't take is left before it's tried again; null: never again.</param>
/// <param name="InstallsAnytime">
/// True: every run downloads and installs. False: a run downloads, and only a run at startup installs a download from an earlier one.
/// </param>
public sealed record UpdateRules(TimeSpan? RetryAfter, bool InstallsAnytime)
{
    /// <summary>The agent's own updater: a version that didn't take is left to the app, which offers "Try again".</summary>
    public static readonly UpdateRules Background = new(RetryAfter: null, InstallsAnytime: false);

    /// <summary>The system's updater task: a version that didn't take is left for most of a day, then tried again.</summary>
    public static readonly UpdateRules SystemTask = new(RetryAfter: TimeSpan.FromHours(20), InstallsAnytime: true);
}

/// <summary>
/// When to download and when to install, for both updaters: what they look up and what they do about it is theirs,
/// the decision is here, so the two can't drift apart without a test noticing.
/// </summary>
public static class UpdatePolicy
{
    /// <summary>The last install attempt took: this copy is that version or newer.</summary>
    public static bool Worked(InstallAttempt? attempt, Version current) => attempt is not null && attempt.Version <= current;

    /// <param name="latest">The latest release's version (null: none found).</param>
    /// <param name="downloaded">The newer release whose installer is already downloaded and intact, if the caller looked.</param>
    /// <param name="atStartup">Just after sign-in, with the window closed.</param>
    /// <param name="mayDownload">False while the app is open: it shows the download itself.</param>
    /// <param name="askedNow">Someone pressed "Update now": an open window doesn't hold the update back.</param>
    public static UpdateStep Decide(UpdateRules rules, Version current, Version? latest, Version? downloaded, InstallAttempt? attempt,
        bool atStartup, bool mayDownload, bool appOpen, bool askedNow, DateTime now)
    {
        if (latest is null || latest <= current) return UpdateStep.UpToDate;
        if (downloaded is null && !rules.InstallsAnytime) return mayDownload ? UpdateStep.Download : UpdateStep.Offer;
        if (!rules.InstallsAnytime && !atStartup) return UpdateStep.Wait;

        var version = downloaded ?? latest;
        if (attempt is not null && ReleaseFeed.Normalize(attempt.Version) == version && (rules.RetryAfter is not { } wait || now - attempt.At < wait))
            return UpdateStep.TriedBefore;
        if (appOpen && !askedNow) return UpdateStep.WindowOpen;
        return UpdateStep.Install;
    }
}
