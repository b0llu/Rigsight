using Rigsight.Core.Games;

namespace Rigsight.Tests.Core;

/// <summary>What Steam downloaded, from its own download log (content_log.txt), as the AI's internet look-up names it.</summary>
public class SteamDownloadsTests
{
    private static readonly Dictionary<string, string> Names = new() { ["4115450"] = "Phantom Blade Zero", ["2073850"] = "THE FINALS" };

    // Lines as Steam writes them, from the user's own log (29 Oct 2026), with the ones around them that aren't downloads.
    private static readonly string[] Log =
    [
        "[2026-10-29 17:01:52] AppID 2073850 state changed : Update Required,Fully Installed,Update Queued,Update Running,",
        "[2026-10-29 17:02:02] AppID 2073850 update started : download 0/1742478960, store 0/0, reuse 0/65769253287, delta 0/2260629616, stage 0/69283779387 ",
        "[2026-10-29 17:02:02] Downloading 2328 chunks for depot 2073851 (6652010567937901044)",
        "[2026-10-29 17:07:02] AppID 2073850 finished update, 1 mounted depots (BuildID 26159200) : 2073851 (6652010567937901044),",
        "[2026-10-29 17:07:32] stats: (Invalid, 0) : 1742478960 Bytes, 67 sec (209.04 Mbps). 824 Hits / 771 Misses (52 %, 53 % bytes), score 0.0000",
        "[2026-10-29 18:04:48] AppID 4115450 update started : download 0/148285013680, store 0/0, reuse 0/0, delta 0/0, stage 0/150884679876 ",
        "[2026-10-29 18:05:19] AppID 4115450 update started : download 790872048/148285013680, store 0/0, reuse 0/0, delta 0/0, stage 1343715875/150884679876 ",
        "[2026-10-29 19:35:15] AppID 4115450 starting commit from \"E:\\SteamLibrary\\steamapps\\downloading\\4115450\" to \"E:\\SteamLibrary\\steamapps\\common\\PBZ\" : 393 updated, 0 moved, 0 deleted files",
        "[2026-10-29 19:35:16] AppID 4115450 finished update, 1 mounted depots (BuildID 26266538) : 4115451 (7450216563795868763),",
    ];

    [Fact]
    public void Names_each_download_with_its_size_start_and_finish()
    {
        var all = SteamDownloads.Parse(Log, Names);
        Assert.Equal(2, all.Count);
        var update = all[0];
        Assert.Equal(("THE FINALS", 1_742_478_960L, false), (update.Name, update.Bytes, update.NewInstall));
        Assert.Equal((new DateTime(2026, 10, 29, 17, 2, 2), new DateTime(2026, 10, 29, 17, 7, 2), DownloadEnd.Finished), (update.Started, update.Ended, update.How));
        var install = all[1];
        Assert.Equal(("Phantom Blade Zero", 148_285_013_680L, true), (install.Name, install.Bytes, install.NewInstall));
        // From the first line, not the progress line after it; ended where Steam says it finished.
        Assert.Equal((new DateTime(2026, 10, 29, 18, 4, 48), new DateTime(2026, 10, 29, 19, 35, 16), DownloadEnd.Finished), (install.Started, install.Ended, install.How));
    }

    [Fact]
    public void A_check_with_nothing_to_download_is_not_a_download()
    {
        var all = SteamDownloads.Parse(
        [
            "[2026-10-20 09:00:00] AppID 570 update started : download 0/0, store 0/0, reuse 0/0, delta 0/0, stage 0/0 ",
            "[2026-10-20 09:00:05] AppID 570 finished update, 1 mounted depots (BuildID 1) : 571 (1),",
        ], Names);
        Assert.Empty(all);
    }

    [Fact]
    public void A_download_with_no_finish_stays_open_and_an_unknown_app_keeps_its_number()
    {
        var all = SteamDownloads.Parse(["[2026-10-30 07:40:00] AppID 999 update started : download 0/5000000000, store 0/0, reuse 0/0, delta 0/0, stage 0/5000000000 "], Names);
        var only = Assert.Single(all);
        Assert.Equal((DownloadEnd.Unknown, new DateTime(2026, 10, 30, 7, 40, 0)), (only.How, only.Ended));
        Assert.Null(only.Name);
        Assert.Equal("999", only.AppId);
    }

    [Fact]
    public void A_second_download_of_the_same_game_after_the_first_finished_is_its_own()
    {
        var all = SteamDownloads.Parse(
        [
            "[2026-10-01 10:00:00] AppID 2073850 update started : download 0/100, store 0/0, reuse 0/50, delta 0/0, stage 0/100 ",
            "[2026-10-01 10:05:00] AppID 2073850 finished update, 1 mounted depots (BuildID 1) : 1 (1),",
            "[2026-10-08 10:00:00] AppID 2073850 update started : download 0/300, store 0/0, reuse 0/50, delta 0/0, stage 0/300 ",
            "[2026-10-08 10:09:00] AppID 2073850 finished update, 1 mounted depots (BuildID 2) : 1 (2),",
        ], Names);
        Assert.Equal([100L, 300L], all.Select(d => d.Bytes));
        Assert.All(all, d => Assert.False(d.NewInstall));
    }

    /// <summary>
    /// Cancelled and uninstalled partway (the user's log, 26 Sep): stopped at the cancel. A download paused while a game
    /// is played and picked up again is one download. Steam's update checks months later don't stretch an unended one.
    /// </summary>
    [Fact]
    public void A_cancelled_download_stops_a_paused_one_goes_on_and_later_checks_dont_stretch_one()
    {
        var all = SteamDownloads.Parse(
        [
            "[2026-09-26 22:35:55] AppID 3432970 update started : download 0/10597724640, store 0/0, reuse 0/0, delta 0/0, stage 0/11179547639 ",
            "[2026-09-26 22:38:07] AppID 3432970 update canceled : Disabled (Suspended)",
            "[2026-09-26 22:38:20] AppID 3432970 finished uninstall (No Error)",
            "[2026-10-02 18:00:00] AppID 2073850 update started : download 0/9000, store 0/0, reuse 0/100, delta 0/0, stage 0/9000 ",
            "[2026-10-02 18:10:00] AppID 2073850 update canceled : Disabled (Suspended)",
            "[2026-10-02 20:00:00] AppID 2073850 update started : download 4000/9000, store 0/0, reuse 0/100, delta 0/0, stage 0/9000 ",
            "[2026-10-02 20:05:00] AppID 2073850 finished update, 1 mounted depots (BuildID 3) : 1 (3),",
            "[2026-10-03 09:00:00] AppID 4115450 update started : download 0/7000, store 0/0, reuse 0/0, delta 0/0, stage 0/7000 ",
            "[2026-10-03 09:01:00] AppID 4115450 App update changed : Running Update,Downloading,Staging,",
            "[2026-12-20 10:00:00] AppID 4115450 scheduler update : Priority Auto Update, not played for 100 seconds, update disabled for 0 seconds",
        ], Names);
        Assert.Equal(3, all.Count);
        Assert.Equal((DownloadEnd.Stopped, new DateTime(2026, 9, 26, 22, 38, 7)), (all[0].How, all[0].Ended));
        Assert.Equal((DownloadEnd.Finished, new DateTime(2026, 10, 2, 18, 0, 0), new DateTime(2026, 10, 2, 20, 5, 0)), (all[1].How, all[1].Started, all[1].Ended));
        Assert.Equal((DownloadEnd.Unknown, new DateTime(2026, 10, 3, 9, 1, 0)), (all[2].How, all[2].Ended));
    }

    /// <summary>A shader cache update (the user's log, 18 Oct, 3 KB for Risk of Rain 2) isn't a download of the game.</summary>
    [Fact]
    public void A_shader_cache_update_is_not_a_game_download()
    {
        var all = SteamDownloads.Parse(
        [
            "[2026-10-18 21:46:08] AppID 632360 Shader update changed : Running Update,",
            "[2026-10-18 21:46:10] AppID 632360 update started : download 0/3296, store 0/0, reuse 0/0, delta 0/0, stage 0/3398 ",
            "[2026-10-18 21:46:10] AppID 632360 Shader update changed : Running Update,Downloading,Staging,",
            "[2026-10-18 21:46:10] AppID 632360 Shader update changed : None",
            "[2026-10-20 10:00:00] AppID 632360 App update changed : Running Update,",
            "[2026-10-20 10:00:01] AppID 632360 update started : download 0/500, store 0/0, reuse 0/900, delta 0/0, stage 0/500 ",
            "[2026-10-20 10:00:09] AppID 632360 App update changed : Running Update,Stopping,",
            "[2026-10-20 10:00:09] AppID 632360 App update changed : None",
        ], Names);
        var only = Assert.Single(all);
        Assert.Equal((500L, DownloadEnd.Stopped, new DateTime(2026, 10, 20, 10, 0, 9)), (only.Bytes, only.How, only.Ended));
    }

    /// <summary>The user's own Steam log, when there is one: the download it names on 29 Oct is the one the AI got wrong.</summary>
    [Fact]
    public void On_this_PC_the_29_Oct_download_is_named_when_Steam_still_has_it()
    {
        var day = SteamDownloads.Read(new DateTime(2026, 10, 29), new DateTime(2026, 10, 30));
        if (!day.Any(d => d.AppId == "4115450")) return; // another PC, or Steam's log has moved on
        var pbz = day.Single(d => d.AppId == "4115450");
        Assert.Equal(("Phantom Blade Zero", true, DownloadEnd.Finished), (pbz.Name, pbz.NewInstall, pbz.How));
        // Nothing from months before is still "running" on that day.
        Assert.All(day, d => Assert.True(d.Ended >= new DateTime(2026, 10, 29)));
    }
}
