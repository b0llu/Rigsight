using Rigsight.Core.Updates;

namespace Rigsight.Tests.Core;

/// <summary>When each updater downloads and installs. This copy is 1.13.0 throughout.</summary>
public sealed class UpdatePolicyTests
{
    private static readonly Version Current = new(1, 13, 0);
    private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0);

    private static Version? V(string? v) => v is null ? null : Version.Parse(v);

    private static InstallAttempt? Attempt(string? version, double hoursAgo) =>
        version is null ? null : new InstallAttempt(Version.Parse(version), Now.AddHours(-hoursAgo));

    // The agent's updater: downloads in the background, installs only a download waiting from an earlier run, only at
    // startup, never under an open window, and never the same version twice.
    [Theory]
    // Nothing newer
    [InlineData(null, null, null, 0, true, true, false, UpdateStep.UpToDate)]
    [InlineData("1.13.0", null, null, 0, true, true, false, UpdateStep.UpToDate)]
    [InlineData("1.12.0", null, null, 0, true, true, false, UpdateStep.UpToDate)]
    // Newer, not downloaded yet: downloaded when allowed, never installed in the same run
    [InlineData("1.14.0", null, null, 0, false, true, false, UpdateStep.Download)]
    [InlineData("1.14.0", null, null, 0, true, true, false, UpdateStep.Download)]
    [InlineData("1.14.0", null, null, 0, true, false, false, UpdateStep.Offer)]
    [InlineData("1.14.0", null, "1.14.0", 100, true, true, false, UpdateStep.Download)]
    // Downloaded: installed at startup only
    [InlineData("1.14.0", "1.14.0", null, 0, false, true, false, UpdateStep.Wait)]
    [InlineData("1.14.0", "1.14.0", null, 0, false, false, true, UpdateStep.Wait)]
    [InlineData("1.14.0", "1.14.0", null, 0, true, true, false, UpdateStep.Install)]
    [InlineData("1.14.0", "1.14.0", null, 0, true, false, false, UpdateStep.Install)]
    // An earlier try of another version doesn't count
    [InlineData("1.14.0", "1.14.0", "1.13.5", 1, true, true, false, UpdateStep.Install)]
    [InlineData("1.14.0", "1.14.0", "1.13.0", 1, true, true, false, UpdateStep.Install)]
    // The same version tried before: never again, however long ago
    [InlineData("1.14.0", "1.14.0", "1.14.0", 1, true, true, false, UpdateStep.TriedBefore)]
    [InlineData("1.14.0", "1.14.0", "1.14.0", 1000, true, true, false, UpdateStep.TriedBefore)]
    [InlineData("1.14.0", "1.14.0", "1.14", 1000, true, true, false, UpdateStep.TriedBefore)]
    // A window open right before: left alone
    [InlineData("1.14.0", "1.14.0", null, 0, true, true, true, UpdateStep.WindowOpen)]
    // The download is what's installed and checked against the last try
    [InlineData("1.15.0", "1.14.0", "1.14.0", 1, true, true, false, UpdateStep.TriedBefore)]
    [InlineData("1.15.0", "1.14.0", "1.15.0", 1, true, true, false, UpdateStep.Install)]
    public void Background(string? latest, string? downloaded, string? tried, double hoursAgo, bool atStartup, bool mayDownload, bool appOpen, UpdateStep expected) =>
        Assert.Equal(expected, UpdatePolicy.Decide(UpdateRules.Background, Current, V(latest), V(downloaded), Attempt(tried, hoursAgo),
            atStartup, mayDownload, appOpen, askedNow: false, Now));

    // The system's updater task: downloads if needed and installs at every run, under an open window only if someone
    // pressed "Update now", and tries a version that didn't take again after 20 hours.
    [Theory]
    // Nothing newer
    [InlineData(null, null, 0, false, false, UpdateStep.UpToDate)]
    [InlineData("1.13.0", null, 0, false, false, UpdateStep.UpToDate)]
    [InlineData("1.12.0", "1.12.0", 1, true, false, UpdateStep.UpToDate)]
    // Newer
    [InlineData("1.14.0", null, 0, false, false, UpdateStep.Install)]
    [InlineData("1.14.0", null, 0, false, true, UpdateStep.Install)]
    [InlineData("1.14.0", "1.13.5", 1, false, false, UpdateStep.Install)]
    [InlineData("1.14.0", "1.13.0", 1, false, false, UpdateStep.Install)]
    // An open window holds it back, unless someone asked
    [InlineData("1.14.0", null, 0, true, false, UpdateStep.WindowOpen)]
    [InlineData("1.14.0", null, 0, true, true, UpdateStep.Install)]
    // The same version tried before: left for 20 hours, whoever asks and whatever is open
    [InlineData("1.14.0", "1.14.0", 1, false, false, UpdateStep.TriedBefore)]
    [InlineData("1.14.0", "1.14.0", 1, true, true, UpdateStep.TriedBefore)]
    [InlineData("1.14.0", "1.14", 19.9, false, false, UpdateStep.TriedBefore)]
    [InlineData("1.14.0", "1.14.0", 20, false, false, UpdateStep.Install)]
    [InlineData("1.14.0", "1.14.0", 30, false, false, UpdateStep.Install)]
    [InlineData("1.14.0", "1.14.0", 30, true, false, UpdateStep.WindowOpen)]
    public void System_task(string? latest, string? tried, double hoursAgo, bool appOpen, bool askedNow, UpdateStep expected) =>
        Assert.Equal(expected, UpdatePolicy.Decide(UpdateRules.SystemTask, Current, V(latest), downloaded: null, Attempt(tried, hoursAgo),
            atStartup: false, mayDownload: true, appOpen, askedNow, Now));

    // Neither the time of the run nor being allowed to download changes what the system task does.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void System_task_installs_at_any_run(bool atStartup, bool mayDownload) =>
        Assert.Equal(UpdateStep.Install, UpdatePolicy.Decide(UpdateRules.SystemTask, Current, V("1.14.0"), downloaded: null, attempt: null,
            atStartup, mayDownload, appOpen: false, askedNow: false, Now));

    [Theory]
    [InlineData(null, false)]
    [InlineData("1.12.0", true)]
    [InlineData("1.13.0", true)]
    [InlineData("1.14.0", false)]
    public void An_attempt_worked_once_this_copy_is_that_version_or_newer(string? tried, bool worked) =>
        Assert.Equal(worked, UpdatePolicy.Worked(Attempt(tried, 1), Current));
}
