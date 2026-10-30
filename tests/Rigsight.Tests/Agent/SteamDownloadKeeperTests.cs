using Rigsight.Agent.Network;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Agent;

/// <summary>
/// What Steam downloaded, saved into the history (2026-10-30): kept after Steam's log moves past it and after its game is
/// uninstalled, looked at every ten minutes at most, and not in the agent's first minutes.
/// </summary>
public class SteamDownloadKeeperTests
{
    private static readonly DateTime At = new(2026, 10, 29, 18, 4, 48);
    private static GameDownload Pbz(string? name = "Phantom Blade Zero", DownloadEnd how = DownloadEnd.Finished) =>
        new("4115450", name, At, At.AddMinutes(90), 148_285_013_680, true, how);

    private static RigsightDb NewDb() => RigsightDb.OpenWriter(Path.Combine(TestEnvironment.NewFolder("steam-downloads"), "rigsight.db"));

    [Fact]
    public void A_saved_download_keeps_its_name_after_Steam_forgets_it_and_takes_its_later_end()
    {
        using var db = NewDb();
        db.SaveSteamDownloads([Pbz(how: DownloadEnd.Unknown) with { Ended = At.AddMinutes(30) }]);
        // Later: finished, and the game uninstalled since, so Steam no longer names it.
        db.SaveSteamDownloads([Pbz(name: null)]);
        var saved = Assert.Single(db.GetSteamDownloads(TimeUtil.ToUnix(At.Date), TimeUtil.ToUnix(At.Date.AddDays(1))));
        Assert.Equal(("Phantom Blade Zero", DownloadEnd.Finished, At.AddMinutes(90), 148_285_013_680L, true), (saved.Name, saved.How, saved.Ended, saved.Bytes, saved.NewInstall));
        Assert.Empty(db.GetSteamDownloads(TimeUtil.ToUnix(At.Date.AddDays(1)), TimeUtil.ToUnix(At.Date.AddDays(2))));
    }

    [Fact]
    public void Looks_after_its_first_minutes_then_every_ten_minutes_and_only_when_the_log_changed()
    {
        using var db = NewDb();
        string log = Path.Combine(TestEnvironment.NewFolder("steam-log"), "content_log.txt");
        File.WriteAllText(log, "one");
        int reads = 0;
        // A download from now: with no history yet, only what ends from now on goes in.
        var fresh = Pbz() with { Started = DateTime.Now.AddMinutes(1), Ended = DateTime.Now.AddMinutes(30) };
        var keeper = new SteamDownloadKeeper(db, () => [log], () => { reads++; return [fresh]; });
        long t = 1_800_000_000;
        keeper.Tick(t);                                      // the agent's first minute
        keeper.Tick(t + 120);                                // still its first minutes
        Assert.Equal(0, reads);
        keeper.Tick(t + SteamDownloadKeeper.FirstAfter);     // first look
        Assert.Equal(1, reads);
        keeper.Tick(t + SteamDownloadKeeper.FirstAfter + 60); // too soon
        keeper.Tick(t + SteamDownloadKeeper.FirstAfter + SteamDownloadKeeper.Every); // time, but the log hasn't changed
        Assert.Equal(1, reads);
        File.AppendAllText(log, " two");
        keeper.Tick(t + SteamDownloadKeeper.FirstAfter + 2 * SteamDownloadKeeper.Every);
        Assert.Equal(2, reads);
        Assert.Single(db.GetSteamDownloads(0, long.MaxValue));
    }

    /// <summary>Cleared history stays cleared: Steam's log still has the old downloads, and they don't come back.</summary>
    [Fact]
    public void Clearing_the_history_empties_it_and_old_downloads_from_Steam_s_log_stay_out()
    {
        using var db = NewDb();
        db.SaveSteamDownloads([Pbz()]);
        db.ClearHistory();
        Assert.Empty(db.GetSteamDownloads(0, long.MaxValue));
        string log = Path.Combine(TestEnvironment.NewFolder("steam-log"), "content_log.txt");
        File.WriteAllText(log, "lines");
        var keeper = new SteamDownloadKeeper(db, () => [log], () => [Pbz()]);
        keeper.Tick(1_800_000_000);
        keeper.Tick(1_800_000_000 + SteamDownloadKeeper.FirstAfter);
        Assert.Empty(db.GetSteamDownloads(0, long.MaxValue));
    }

    [Fact]
    public void With_no_Steam_log_it_saves_nothing_and_never_reads()
    {
        using var db = NewDb();
        int reads = 0;
        var keeper = new SteamDownloadKeeper(db, () => [], () => { reads++; return []; });
        keeper.Tick(0);
        keeper.Tick(SteamDownloadKeeper.FirstAfter + SteamDownloadKeeper.Every);
        Assert.Equal(0, reads);
    }

    /// <summary>The AI's read: a download only the history still has comes back named (Steam's log has moved past it).</summary>
    [Fact]
    public void The_AI_reads_saved_downloads_Steam_has_forgotten()
    {
        using var db = NewDb();
        var old = Pbz() with { AppId = "1", Name = "An old game", Started = new DateTime(2020, 1, 5, 10, 0, 0), Ended = new DateTime(2020, 1, 5, 11, 0, 0) };
        db.SaveSteamDownloads([old]);
        var read = SteamDownloads.Read(db, new DateTime(2020, 1, 5), new DateTime(2020, 1, 6));
        Assert.Equal("An old game", Assert.Single(read).Name);
    }
}
