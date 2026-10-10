using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>
/// What is on record about one running app, for the box under its row on the Processes page. Anything not recorded
/// stays null (memory is kept only for apps that had a window, held 150 MB or used 2% of the processor), and the page
/// leaves that fact out.
/// </summary>
public sealed class AppPast
{
    /// <summary>How many days back the usual is taken over.</summary>
    public const int UsualDays = 30;

    /// <summary>The fewest days with memory on record for there to be a usual.</summary>
    public const int MinUsualDays = 3;

    /// <summary>Its memory in each hour of today (MB), from midnight to the hour now; null where it wasn't recorded.</summary>
    public double?[] Hours { get; init; } = [];

    /// <summary>Its average memory over the days before today (MB); null with too few of them.</summary>
    public double? UsualMB { get; init; }

    /// <summary>Seconds in front today (in use, or with the user away), and open behind something else or minimized.</summary>
    public double? FrontSec { get; init; }
    public double? BackSec { get; init; }

    /// <summary>Bytes it moved over the network today.</summary>
    public long? NetBytes { get; init; }

    /// <summary>Its crashes and hangs over the last <see cref="UsualDays"/> days.</summary>
    public int Crashes { get; init; }

    public DateTime? FirstSeen { get; init; }

    public bool HasChart => Hours.Count(h => h is not null) >= 2;

    public static AppPast Read(RigsightDb db, string exe, DateTime now)
    {
        var today = now.Date;
        long from = TimeUtil.ToUnix(today), to = TimeUtil.ToUnix(today.AddDays(1));
        int crashes = db.GetCrashes(TimeUtil.ToUnix(today.AddDays(-UsualDays)), to).Count(c => c.AppExe.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (db.LoadApps().FirstOrDefault(a => a.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)) is not { } app) return new AppPast { Crashes = crashes };

        var hours = new double?[now.Hour + 1];
        double front = 0, back = 0;
        bool any = false;
        foreach (var h in db.GetAppHoursOf(app.Id, from, to))
        {
            any = true;
            front += h.FgSec + h.IdleSec;
            back += h.BgSec + h.MinSec;
            int at = TimeUtil.FromUnix(h.Ts).Hour;
            if (h.MemN > 0 && at < hours.Length) hours[at] = h.MemSum / h.MemN;
        }

        double sum = 0;
        long n = 0;
        var days = new HashSet<DateTime>();
        foreach (var h in db.GetAppHoursOf(app.Id, TimeUtil.ToUnix(today.AddDays(-UsualDays)), from))
        {
            if (h.MemN <= 0) continue;
            sum += h.MemSum;
            n += h.MemN;
            days.Add(TimeUtil.FromUnix(h.Ts).Date);
        }

        long net = 0;
        bool anyNet = false;
        foreach (var u in db.GetNetAppDays(from, to))
        {
            if (u.App != app.Id) continue;
            anyNet = true;
            net += u.Down + u.Up;
        }

        return new AppPast
        {
            Hours = hours,
            UsualMB = days.Count >= MinUsualDays && n > 0 ? sum / n : null,
            FrontSec = any ? front : null,
            BackSec = any ? back : null,
            NetBytes = anyNet ? net : null,
            Crashes = crashes,
            FirstSeen = app.FirstSeen > 0 ? TimeUtil.FromUnix(app.FirstSeen) : null,
        };
    }
}
