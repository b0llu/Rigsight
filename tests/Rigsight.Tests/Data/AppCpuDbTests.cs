using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Data;

/// <summary>Each app's CPU and GPU time by the hour and the day: written a minute at a time, added up, pruned and cleared.</summary>
public sealed class AppCpuDbTests
{
    private static readonly DateTime Day = new(2026, 6, 15);
    private static long At(DateTime t) => TimeUtil.ToUnix(t);

    [Fact]
    public void Minutes_add_up_into_their_hour_and_their_day()
    {
        using var t = new TestDb();
        long app = t.Db.UpsertApp("game.exe", "Game", null, AppCategory.Game);
        long nine = At(Day.AddHours(9)), ten = At(Day.AddHours(10));
        t.Db.AddAppCpuUse(new AppCpuUse(nine, app, 1500, 4000, 1000, 50, 60, 60 * 800));
        t.Db.AddAppCpuUse(new AppCpuUse(nine, app, 500, 0, 0, 0, 60, 60 * 1200));
        t.Db.AddAppCpuUse(new AppCpuUse(ten, app, 2000, 0, 0, 0, 60, 60 * 1000));
        // A minute it only ran: no hour row (nothing to draw), but its day knows it ran.
        t.Db.AddAppCpuUse(new AppCpuUse(ten, app, 0, 0, 0, 0, 60, 60 * 1000));
        t.Db.AddAppCpuUse(new AppCpuUse(nine, AppCpuUse.AllApps, 9000));

        using var r = t.Reader();
        var hours = r.GetAppCpuHours(app, At(Day), At(Day.AddDays(1)));
        Assert.Equal([(nine, 2000L, 4000L, 1000L, 50L), (ten, 2000L, 0L, 0L, 0L)], hours.Select(h => (h.Ts, h.CpuMs, h.GpuMs, h.FgCpuMs, h.FgSec)));
        var day = Assert.Single(r.GetAppCpuDays(app, At(Day), At(Day.AddDays(1))));
        Assert.Equal((At(Day), 4000L, 4000L, 240L), (day.Ts, day.CpuMs, day.GpuMs, day.RunSec));
        Assert.Equal(1000, day.MemMbAvg!.Value, 6);
        Assert.Equal(9000, Assert.Single(r.GetAppCpuDays(AppCpuUse.AllApps, 0, long.MaxValue / 2)).CpuMs);
        Assert.Equal(2, r.GetAppCpuDay(At(Day)).Count); // the app and every process together
        Assert.Equal(At(Day), r.FirstAppCpuDay());
        Assert.Equal(4000L, r.GetAppCpuTotalsByDay(At(Day), At(Day.AddDays(1)))[(At(Day), app)]);

        // Totals from the days, and from the hours for a stretch that isn't whole days.
        Assert.Equal(4000, r.GetAppCpuTotals(At(Day), At(Day.AddDays(1))).Single(u => u.App == app).CpuMs);
        var front = r.GetAppCpuTotals(nine, ten, byHour: true).Single(u => u.App == app);
        Assert.Equal((2000L, 1000L, 50L), (front.CpuMs, front.FgCpuMs, front.FgSec));
    }

    [Fact]
    public void Old_days_go_with_everything_else_and_clearing_takes_them_all()
    {
        using var t = new TestDb();
        long app = t.Db.UpsertApp("game.exe", "Game", null, AppCategory.Game);
        foreach (int d in new[] { -40, -31, -30, -1 })
            t.Db.AddAppCpuUse(new AppCpuUse(At(Day.AddDays(d).AddHours(20)), app, 1000, 0, 0, 0, 60, 6000));
        // The cutoff falls in the evening of the day 30 days back: that day keeps its whole day, its hours before go.
        t.Db.Prune(At(Day.AddDays(-30).AddHours(21)));
        Assert.Equal([Day.AddDays(-30), Day.AddDays(-1)], t.Db.GetAppCpuDays(app, 0, long.MaxValue / 2).Select(d => TimeUtil.FromUnix(d.Ts)));
        Assert.Equal([Day.AddDays(-1).AddHours(20)], t.Db.GetAppCpuHours(app, 0, long.MaxValue / 2).Select(h => TimeUtil.FromUnix(h.Ts)));

        t.Db.ClearHistory();
        Assert.Empty(t.Db.GetAppCpuDays(app, 0, long.MaxValue / 2));
        Assert.Empty(t.Db.GetAppCpuHours(app, 0, long.MaxValue / 2));
        Assert.Null(t.Db.FirstAppCpuDay());
    }
}
