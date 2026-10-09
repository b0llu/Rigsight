using Rigsight.Agent.Network;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>Internet use as the tracker records it: per app (in front, in the background, away), per minute, big downloads, VPNs.</summary>
public class TrackerNetworkTests
{
    private const long MB = 1_000_000;

    private static NetAppUse Day(TrackerRig rig, string exe) =>
        rig.Db.GetNetAppDays(0, long.MaxValue).Where(u => u.App == rig.AppId(exe)).Aggregate(new NetAppUse(), (a, u) =>
        {
            a.Down += u.Down; a.Up += u.Up; a.BgDown += u.BgDown; a.BgUp += u.BgUp; a.AwayDown += u.AwayDown; a.AwayUp += u.AwayUp;
            a.GameDown += u.GameDown; a.Lan += u.Lan;
            return a;
        });

    private static NetDay Total(TrackerRig rig) => rig.Db.GetNetDays(0, long.MaxValue).Single();

    [Fact]
    public void An_app_downloading_behind_the_one_in_front_counts_as_background()
    {
        using var rig = new TrackerRig();
        rig.Net["steam.exe"] = new NetCounts { Down = MB, Up = 10_000 };
        rig.Use("chrome.exe", 120);
        rig.Tracker.Flush(closeAllSessions: true);

        var steam = Day(rig, "steam.exe");
        Assert.Equal(120 * MB, steam.Down);
        Assert.Equal(120 * MB, steam.BgDown);
        Assert.Equal(0, steam.AwayDown);
        Assert.Equal(120 * 10_000, steam.Up);
        Assert.Equal(120 * MB, Total(rig).Down);
        Assert.Equal(120 * MB, Total(rig).BgDown);
    }

    [Fact]
    public void An_agent_restarting_within_a_minute_keeps_what_the_minute_already_had()
    {
        // The minute is written whole: the agent that starts again (an update, a crash) used to write only its own part
        // of it over what the one before had written.
        using var rig = new TrackerRig();
        rig.Net["steam.exe"] = new NetCounts { Down = MB, Up = 10_000 };
        rig.Use("chrome.exe", 20);
        rig.Restart();
        rig.Use("chrome.exe", 20);
        rig.Tracker.Flush(closeAllSessions: true);

        var minute = Assert.Single(rig.Db.GetNetMinutes(0, long.MaxValue));
        Assert.Equal(40 * MB, minute.Down);
        Assert.Equal(40 * 10_000, minute.Up);
        Assert.Equal(40 * MB, minute.BgDown);
        Assert.Equal(40 * MB, Total(rig).Down);
        Assert.Equal(40 * MB, Day(rig, "steam.exe").Down);

        // Only the minute it started in: the next one starts from nothing.
        rig.Use("chrome.exe", 60);
        rig.Tracker.Flush(closeAllSessions: true);
        var minutes = rig.Db.GetNetMinutes(0, long.MaxValue);
        Assert.Equal(2, minutes.Count);
        Assert.Equal(100 * MB, minutes.Sum(m => m.Down));
    }

    [Fact]
    public void The_app_in_front_downloading_is_not_background()
    {
        using var rig = new TrackerRig();
        rig.Net["chrome.exe"] = new NetCounts { Down = 2 * MB };
        rig.Use("chrome.exe", 60);
        rig.Tracker.Flush(closeAllSessions: true);

        var chrome = Day(rig, "chrome.exe");
        Assert.Equal(120 * MB, chrome.Down);
        Assert.Equal(0, chrome.BgDown + chrome.AwayDown);
    }

    [Fact]
    public void With_nobody_at_the_PC_it_is_away_whatever_is_in_front()
    {
        using var rig = new TrackerRig();
        rig.Net["chrome.exe"] = new NetCounts { Down = MB };
        rig.Away("chrome.exe", 90);
        rig.Tracker.Flush(closeAllSessions: true);

        var chrome = Day(rig, "chrome.exe");
        Assert.Equal(90 * MB, chrome.AwayDown);
        Assert.Equal(0, chrome.BgDown);
        Assert.Equal(90 * MB, Total(rig).AwayDown);
    }

    [Fact]
    public void Downloading_while_a_game_is_in_front_is_noted()
    {
        using var rig = new TrackerRig(apps: ("eldenring.exe", "Elden Ring", AppCategory.Game));
        rig.Net["steam.exe"] = new NetCounts { Down = MB };
        rig.Use("eldenring.exe", 60, fullscreen: true);
        rig.Tracker.Flush(closeAllSessions: true);

        Assert.Equal(60 * MB, Day(rig, "steam.exe").GameDown);
    }

    [Fact]
    public void The_local_network_is_kept_apart_from_the_internet()
    {
        using var rig = new TrackerRig();
        rig.Net["explorer.exe"] = new NetCounts { LanDown = 5 * MB, LanUp = MB };
        rig.Use("chrome.exe", 60);
        rig.Tracker.Flush(closeAllSessions: true);

        var day = Total(rig);
        Assert.Equal(0, day.Down + day.Up);
        Assert.Equal(360 * MB, day.Lan);
        Assert.Equal(360 * MB, Day(rig, "explorer.exe").Lan);
    }

    [Fact]
    public void An_app_left_out_of_tracking_counts_in_the_totals_but_not_by_name()
    {
        var settings = new RigsightSettings();
        settings.Tracking.ExcludedApps.Add("secret.exe");
        using var rig = new TrackerRig(settings: settings);
        rig.Net["secret.exe"] = new NetCounts { Down = MB };
        rig.Use("chrome.exe", 60);
        rig.Tracker.Flush(closeAllSessions: true);

        Assert.Equal(60 * MB, Total(rig).Down);
        Assert.False(rig.HasApp("secret.exe"));
    }

    [Fact]
    public void A_VPN_carrying_another_apps_download_is_counted_once()
    {
        // Steam's download goes through the tunnel; the VPN moves the same bytes (plus a little) through the network card.
        using var rig = new TrackerRig();
        rig.Net["steam.exe"] = new NetCounts { TunnelDown = 10 * MB, TunnelUp = 100_000 };
        rig.Net["warp-svc.exe"] = new NetCounts { Down = 10_500_000, Up = 150_000 };
        rig.Use("chrome.exe", 180);
        rig.Tracker.Flush(closeAllSessions: true);

        Assert.Equal(1800 * MB, Day(rig, "steam.exe").Down);
        var warp = Day(rig, "warp-svc.exe");
        Assert.InRange(warp.Down, 85 * MB, 95 * MB); // its own 0.5 MB/s on top, not the 10 MB/s it carried
        Assert.InRange(Total(rig).Down, 1885 * MB, 1895 * MB);
        Assert.Contains("warp-svc.exe", rig.Db.GetMeta("net_carriers") ?? "");
    }

    [Fact]
    public void A_VPN_found_once_is_counted_once_after_a_restart_too()
    {
        using var rig = new TrackerRig();
        rig.Net["steam.exe"] = new NetCounts { TunnelDown = 5 * MB };
        rig.Net["warp-svc.exe"] = new NetCounts { Down = 5_200_000 };
        rig.Use("chrome.exe", 60);
        rig.Restart();
        rig.Use("chrome.exe", 60);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.InRange(Day(rig, "warp-svc.exe").Down, 20 * MB, 28 * MB);
    }

    [Fact]
    public void Two_apps_without_a_tunnel_are_never_taken_for_a_VPN()
    {
        using var rig = new TrackerRig();
        rig.Net["steam.exe"] = new NetCounts { Down = 10 * MB };
        rig.Net["chrome.exe"] = new NetCounts { Down = 10 * MB };
        rig.Use("code.exe", 120);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Equal(2400 * MB, Total(rig).Down);
        Assert.Null(rig.Db.GetMeta("net_carriers"));
    }

    [Fact]
    public void A_steady_download_gives_the_minute_its_speed()
    {
        using var rig = new TrackerRig(FakeClock.At(10, 0, 0));
        rig.Net["steam.exe"] = new NetCounts { Down = 2 * MB };
        rig.Use("chrome.exe", 61);
        rig.Tracker.Flush(closeAllSessions: true);

        var minute = rig.Db.GetNetMinutes(0, long.MaxValue).First();
        Assert.Equal(2 * MB, minute.Steady);
        Assert.Equal(rig.AppId("steam.exe"), minute.App);
        Assert.Equal(2 * MB, Total(rig).Best);
    }

    [Fact]
    public void A_bursty_minute_has_no_steady_speed()
    {
        using var rig = new TrackerRig(FakeClock.At(10, 0, 0));
        for (int i = 0; i < 30; i++)
        {
            rig.Net["steam.exe"] = new NetCounts { Down = 8 * MB };
            rig.Use("chrome.exe", 1);
            rig.Net["steam.exe"] = new NetCounts();
            rig.Use("chrome.exe", 1);
        }
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.All(rig.Db.GetNetMinutes(0, long.MaxValue), m => Assert.Null(m.Steady));
        Assert.Null(Total(rig).Best);
    }

    [Fact]
    public void A_big_download_is_recorded_once_it_ends()
    {
        using var rig = new TrackerRig();
        rig.Net["steam.exe"] = new NetCounts { Down = 1_500_000 };
        rig.Use("chrome.exe", 100);
        rig.Net["steam.exe"] = new NetCounts();
        rig.Use("chrome.exe", 40);

        var t = Assert.Single(rig.Db.GetNetTransfers(0, long.MaxValue, 10));
        Assert.Equal(rig.AppId("steam.exe"), t.App);
        Assert.Equal(150 * MB, t.Bytes);
        Assert.Equal(100, t.Sec);
        Assert.Equal(1_500_000, t.Speed);
    }

    [Fact]
    public void A_small_download_is_not_listed()
    {
        using var rig = new TrackerRig();
        rig.Net["chrome.exe"] = new NetCounts { Down = 2 * MB };
        rig.Use("code.exe", 30);
        rig.Net["chrome.exe"] = new NetCounts();
        rig.Use("code.exe", 40);
        Assert.Empty(rig.Db.GetNetTransfers(0, long.MaxValue, 10));
    }

    [Fact]
    public void A_download_still_going_when_the_agent_stops_is_kept()
    {
        using var rig = new TrackerRig();
        rig.Net["steam.exe"] = new NetCounts { Down = 2 * MB };
        rig.Use("chrome.exe", 80);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Equal(160 * MB, Assert.Single(rig.Db.GetNetTransfers(0, long.MaxValue, 10)).Bytes);
    }

    [Fact]
    public void Live_speeds_list_the_busiest_app_first()
    {
        using var rig = new TrackerRig();
        rig.Net["steam.exe"] = new NetCounts { Down = 5 * MB };
        rig.Net["discord.exe"] = new NetCounts { Down = 50_000, Up = 60_000 };
        rig.Use("chrome.exe", 3);

        var live = rig.Tracker.NetNow;
        Assert.Equal(5 * MB + 50_000, live.Down);
        Assert.Equal(["steam.exe", "discord.exe"], live.Apps.Select(a => a.Exe));
    }

    [Fact]
    public void Nothing_is_recorded_while_tracking_is_paused()
    {
        var settings = new RigsightSettings();
        settings.Tracking.PausedUntil = long.MaxValue;
        using var rig = new TrackerRig(settings: settings);
        rig.Net["steam.exe"] = new NetCounts { Down = MB };
        rig.Use("chrome.exe", 120);
        rig.Tracker.Flush(closeAllSessions: true);
        Assert.Empty(rig.Db.GetNetMinutes(0, long.MaxValue));
    }

    [Fact]
    public void An_hours_first_minute_lands_in_the_hour_before_it_ended()
    {
        // 10:59:00 to 11:00:30: the 10:59 minute is written once the clock reads 11:00, into 10:00's hour.
        using var rig = new TrackerRig(FakeClock.At(10, 59, 0));
        rig.Net["steam.exe"] = new NetCounts { Down = MB };
        rig.Use("chrome.exe", 90);
        rig.Tracker.Flush(closeAllSessions: true);

        var hours = rig.Db.GetNetAppHours(0, long.MaxValue).Where(u => u.App == rig.AppId("steam.exe")).OrderBy(u => u.Ts).ToList();
        Assert.Equal(2, hours.Count);
        Assert.Equal(TimeUtil.ToUnix(new DateTime(2026, 6, 10, 10, 0, 0)), hours[0].Ts);
        Assert.Equal(59 * MB, hours[0].Down);
        Assert.Equal(31 * MB, hours[1].Down);
    }
}
