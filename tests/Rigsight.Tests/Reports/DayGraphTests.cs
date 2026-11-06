using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Reports;

/// <summary>
/// The Reports page's Day graph and Temperatures card (issue #9 and the user's ask, 2026-11-06): a day's bars are its
/// hours, each with its temperatures and what was in front.
/// </summary>
public sealed class DayGraphTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"rigsight-daygraph-{Guid.NewGuid():N}.db");
    private static readonly DateTime Day = new(2026, 11, 5);

    public void Dispose()
    {
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }

    /// <summary>
    /// 2 PM to 3 PM in Claude Code with a build working behind it (the 12 PM hour the mock-up showed), 3 PM to 4 PM
    /// playing a game with OBS recording.
    /// </summary>
    private (long Editor, long Build, long Game, long Obs) Write()
    {
        using var w = RigsightDb.OpenWriter(_db);
        long editor = w.UpsertApp("claude.exe", "Claude Code", @"C:\Apps\claude.exe", AppCategory.Development);
        long build = w.UpsertApp("VBCSCompiler.exe", "VBCSCompiler", null, AppCategory.Development);
        long game = w.UpsertApp("pbz.exe", "Phantom Blade Zero", @"C:\Games\pbz.exe", AppCategory.Game);
        long obs = w.UpsertApp("obs64.exe", "OBS Studio", null, AppCategory.Media);
        for (int m = 0; m < 120; m++)
        {
            long ts = TimeUtil.ToUnix(Day.AddHours(14).AddMinutes(m));
            bool playing = m >= 60;
            w.WriteMinute(new SystemMinute
            {
                Ts = ts, ActiveSec = 60, FgApp = playing ? game : editor,
                CpuTemp = playing ? 66 : 58, CpuTempMax = playing ? 76 : 74, CpuTempMin = playing ? 60 : 50,
                GpuTemp = playing ? 71 : 44, GpuTempMax = playing ? 74 : 47, GpuTempMin = playing ? 68 : 42,
            });
            w.WriteAppMinutes(ts, playing
                ? [new AppMinute(ts, game, 18, 92, 6000, 0), new AppMinute(ts, obs, 6, 8, 300, 0)]
                : [new AppMinute(ts, build, m < 30 ? 82 : 0, 0, 900, 0), new AppMinute(ts, editor, 6, 1, 2500, 0)]);
        }
        w.AddAppHour(new AppHour { Ts = TimeUtil.ToUnix(Day.AddHours(14)), AppId = editor, FgSec = 3600 });
        w.AddAppHour(new AppHour { Ts = TimeUtil.ToUnix(Day.AddHours(15)), AppId = game, FgSec = 3600 });
        return (editor, build, game, obs);
    }

    [Fact]
    public void A_days_bars_are_its_hours_with_their_temperatures()
    {
        Write();
        using var db = RigsightDb.OpenReader(_db)!;
        var report = ReportBuilder.Build(db, ReportRange.Day, Day, new RigsightSettings());

        Assert.Equal(24, report.Days.Count);
        var two = report.Days[14];
        Assert.Equal(Day.AddHours(14), two.Day);
        Assert.Equal(3600, two.ActiveSec);
        Assert.Equal(3600, two.ActiveByCategory[AppCategory.Development]);
        Assert.Equal((58, 74, 50), (two.CpuTempAvg!.Value, two.CpuTempMax!.Value, two.CpuTempMin!.Value));
        Assert.Equal(42, report.Days[14].GpuTempMin);
        Assert.Null(report.Days[3].CpuTempAvg);
        Assert.Equal("Phantom Blade Zero", report.Days[15].TopApp);
        Assert.Equal(new FrontApp("Phantom Blade Zero", @"C:\Games\pbz.exe", 3600, true), report.Days[15].Front.Single());
    }

    [Fact]
    public void The_temperature_page_names_what_was_in_front_from_its_own_minutes()
    {
        var (editor, _, game, _) = Write();
        using var db = RigsightDb.OpenReader(_db)!;
        long from = TimeUtil.ToUnix(Day.AddHours(14)), to = from + 2 * 3600;
        var minutes = db.GetMinutes(from, to);
        (string, string?, bool)? App(long id) => id == editor ? ("Claude Code", null, false) : id == game ? ("Phantom Blade Zero", null, true) : null;
        var front = HeatWhoReader.FrontOf(minutes, App);
        Assert.Equal([("Claude Code", 3600.0), ("Phantom Blade Zero", 3600.0)], front.Select(f => (f.Name, f.Sec)).OrderBy(f => f.Name));
        Assert.Equal("Claude Code", HeatWhoReader.FrontOf(minutes.Take(15), App).Single().Name);
        Assert.Empty(HeatWhoReader.FrontOf([], App));
    }

    [Fact]
    public void The_bars_hover_lists_kinds_as_Where_the_time_went_does()
    {
        // System, Other and anything under a minute go together at the end ("Other 18s" had a row of its own).
        var hour = new DayBucket { Day = Day.AddHours(8) };
        hour.ActiveByCategory[AppCategory.Browser] = 24 * 60;
        hour.ActiveByCategory[AppCategory.Development] = 21 * 60;
        hour.ActiveByCategory[AppCategory.System] = 3 * 60;
        hour.ActiveByCategory[AppCategory.Other] = 18;
        hour.ActiveByCategory[AppCategory.Game] = 40;
        var kinds = Rigsight.Tests.Support.Ui.Run(() => DailyBars.Kinds(hour));
        Assert.Equal(["Browsing", "Development", "Everything else"], kinds.Select(k => k.Name));
        Assert.Equal(3 * 60 + 18 + 40, kinds[^1].Sec);
    }

    [Fact]
    public void The_hover_says_what_was_in_front_in_the_Network_hovers_words()
    {
        var game = new FrontApp("Phantom Blade Zero", null, 44 * 60, true);
        var editor = new FrontApp("Claude Code", null, 18 * 60, false);
        var browser = new FrontApp("Google Chrome", null, 17 * 60, false);
        Assert.Equal("Playing Phantom Blade Zero for 44m", new HeatWho([game]).FrontWords);
        Assert.Equal("Claude Code in front for 18m, Google Chrome for 17m", new HeatWho([editor, browser]).FrontWords);
        Assert.Equal("Claude Code in front", new HeatWho([editor, browser], Timed: false).FrontWords);
        Assert.Equal("Playing Phantom Blade Zero", new HeatWho([game], Timed: false).FrontWords);
        Assert.Null(new HeatWho([]).FrontWords);
    }
}
