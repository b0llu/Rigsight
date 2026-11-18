using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.App;

/// <summary>
/// Stretches RigSight recorded nothing in, said by every look-up over a period (audit Q062, 2026-11-17): a PC with no
/// records from 15 Oct to 7 Nov, which held every shut-off asked about, was answered "nothing was recorded at 4:48 PM".
/// </summary>
public sealed class AiRecordGapsTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"rigsight-record-gaps-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }

    private AiTools Tools(Action<RigsightSettings>? set = null)
    {
        var s = new RigsightSettings();
        set?.Invoke(s);
        return new AiTools(s, _db);
    }

    /// <summary>Half an hour every evening from 10 to 15 Oct (the last at 11:44 AM on the 15th), then from 7 to 10 Nov 2026 (the first at 1:06 AM).</summary>
    private void TwoStretches(Action<RigsightDb>? more = null)
    {
        using var w = RigsightDb.OpenWriter(_db)!;
        void Half(DateTime start) { for (int i = 0; i < 30; i++) w.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(start) + i * 60, CpuTemp = 50, CpuTempMax = 60, GpuTemp = 45, GpuTempMax = 55, RamUsed = 8, ActiveSec = 60 }); }
        for (var d = new DateTime(2026, 10, 10); d < new DateTime(2026, 10, 15); d = d.AddDays(1)) Half(d.AddHours(20));
        Half(new DateTime(2026, 10, 15, 11, 15, 0));
        Half(new DateTime(2026, 11, 7, 1, 6, 0));
        for (var d = new DateTime(2026, 11, 7); d <= new DateTime(2026, 11, 10); d = d.AddDays(1)) Half(d.AddHours(20));
        more?.Invoke(w);
    }

    private const string Gap = "We have no records from Thu 15 Oct 2026 11:45 AM to Sat 7 Nov 2026 1:06 AM (22 days 13 hours)";

    [Theory]
    [InlineData("get_temperatures")]
    [InlineData("get_report")]
    [InlineData("get_crashes")]
    [InlineData("get_on_times")]
    [InlineData("get_memory")]
    [InlineData("get_apps")]
    [InlineData("get_fans")]
    [InlineData("get_network")]
    public void Every_look_up_over_a_period_says_the_weeks_with_no_records(string lookUp)
    {
        TwoStretches();

        string said = Tools().Run(lookUp, """{"from":"2026-10-20","to":"2026-11-08"}""");

        Assert.Contains("NOT RECORDED: " + Gap + ", which covers part of this period.", said);
        Assert.Contains("can't tell which", said);
    }

    [Fact]
    public void A_game_looked_up_for_a_period_says_it_too()
    {
        TwoStretches();
        string said = Tools().Run("get_game", """{"name":"Nothing","from":"2026-10-20","to":"2026-10-25"}""");
        Assert.Contains("NOT RECORDED: " + Gap + ", which is all of this period.", said);
    }

    [Fact]
    public void An_app_crash_inside_the_stretch_shows_the_PC_was_on_so_RigSight_wasnt_recording()
    {
        TwoStretches(w =>
        {
            w.InsertCrashes([new CrashEvent { Ts = TimeUtil.ToUnix(new DateTime(2026, 10, 20, 18, 0, 0)), Kind = CrashKind.AppCrash, AppExe = "game.exe" }]);
            w.InsertChanges([new SystemChange(new DateTime(2026, 10, 22, 9, 0, 0), ChangeKind.WindowsUpdate, "2026-10 Cumulative Update")]);
        });

        string said = Tools().Run("get_temperatures", """{"from":"2026-10-20","to":"2026-11-08"}""");

        Assert.Contains("Windows' own logs show the PC was on during it (1 app crash or freeze, the first on 20 Oct; 1 driver or Windows update installed, the first on 22 Oct), so we weren't recording for at least part of it.", said);
        Assert.DoesNotContain("can't tell which", said);
    }

    [Fact]
    public void Crashes_in_the_stretch_are_said_to_have_no_readings_around_them()
    {
        TwoStretches(w => w.InsertCrashes([new CrashEvent { Ts = TimeUtil.ToUnix(new DateTime(2026, 11, 6, 16, 48, 0)), Kind = CrashKind.UnexpectedShutdown }]));

        string said = Tools().Run("get_crashes", """{"from":"2026-11-01","to":"2026-11-07"}""");

        Assert.Contains("Crashes in that time come from Windows' own logs, without our readings around them", said);
    }

    [Fact]
    public void The_moment_says_how_long_nothing_was_recorded_around_it()
    {
        TwoStretches();

        string said = Tools().Run("get_moment", """{"time":"2026-11-06 16:48"}""");

        Assert.Contains("Nothing was recorded then. " + Gap + ".", said);
    }

    [Fact]
    public void Nights_off_are_not_called_gaps()
    {
        TwoStretches();

        string said = Tools().Run("get_report", """{"from":"2026-11-08","to":"2026-11-09"}""");

        Assert.DoesNotContain("NOT RECORDED", said);
    }

    [Fact]
    public void Nothing_recorded_since_with_the_agent_stopped_says_it_isnt_recording()
    {
        var last = DateTime.Now.AddDays(-3);
        using (var w = RigsightDb.OpenWriter(_db)!)
            for (int i = 0; i < 30; i++) w.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(last) + i * 60, CpuTemp = 50, ActiveSec = 60 });
        var tools = Tools();
        tools.Recording = () => false;

        string said = tools.Run("get_temperatures", $$"""{"from":"{{DateTime.Today.AddDays(-1):yyyy-MM-dd}}","to":"{{DateTime.Today:yyyy-MM-dd}}"}""");

        Assert.Contains("We have no records since", said);
        Assert.Contains("up to now)", said);
        Assert.Contains("RigSight's background agent, which records, isn't running now", said);
    }

    [Fact]
    public void A_period_before_recording_began_says_when_the_records_begin()
    {
        TwoStretches();

        string said = Tools().Run("get_temperatures", """{"from":"2026-10-01","to":"2026-10-05"}""");

        Assert.Contains("NOT RECORDED: Our records on this PC begin Sat 10 Oct 2026 8:00 PM, after this period", said);
    }

    [Fact]
    public void The_gaps_come_from_one_pass_over_the_minutes()
    {
        TwoStretches();
        using var db = RigsightDb.OpenReader(_db)!;
        long now = TimeUtil.ToUnix(DateTime.Now);

        var gaps = db.GetMinuteGaps(TimeUtil.ToUnix(new DateTime(2026, 10, 10)), TimeUtil.ToUnix(new DateTime(2026, 11, 10, 20, 15, 0)), AiTools.GapSeconds, now);

        Assert.Equal([(TimeUtil.ToUnix(new DateTime(2026, 10, 15, 11, 45, 0)), TimeUtil.ToUnix(new DateTime(2026, 11, 7, 1, 6, 0)))], gaps.Select(g => (g.Start!.Value, g.End!.Value)));
    }

    [Fact]
    public void Long_stretches_are_said_in_days()
    {
        Assert.Equal("22 days 13 hours", AiTools.Length(22 * 86400 + 13 * 3600 + 1200));
        Assert.Equal("1 day", AiTools.Length(86400 + 60));
        Assert.Equal("9h 30m", AiTools.Length(9 * 3600 + 1800));
    }
}
