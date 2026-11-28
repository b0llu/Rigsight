using System.Globalization;
using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>Which period the Power page shows, as the other pages' pickers name them.</summary>
/// <summary>The period shown; Custom is a range of the person's own (2026-11-24: every page that looks back has one).</summary>
public enum PowerPeriod { Day, Week, Month, Year, Custom }

/// <summary>
/// The Power page's facts for one period (1.18.0, approved mock-up 2026-11-23): the CPU and GPU energy, against the
/// period before (or, for a day, the same weekday's usual) with what changed in play time, the share that went with games,
/// the chart's stretches, how each part ran, the apps in front and, for a game, its GPU power against its usual at the same
/// frame rate. Built once here; the page, its export and the AI's look-up read this.
/// </summary>
public sealed class PowerReport
{
    public required PowerPeriod Period { get; init; }
    public required DateTime From { get; init; }
    public required DateTime To { get; init; }

    /// <summary>Null when the part gave no power reading in the period (not 0: "not read" isn't "used none").</summary>
    public double? CpuWh { get; init; }
    public double? GpuWh { get; init; }
    public bool HasCpu => CpuWh is not null;
    public bool HasGpu => GpuWh is not null;
    public bool HasAny => HasCpu || HasGpu;
    public double Wh => (CpuWh ?? 0) + (GpuWh ?? 0);

    public required IReadOnlyList<PowerBucket> Buckets { get; init; }
    public required IReadOnlyList<PowerApp> Apps { get; init; }
    public required PowerParts Parts { get; init; }

    /// <summary>"28% more than the 7 days before." with "You played 16h 42m more." when play time explains it; empty when there's nothing to set it against.</summary>
    public string Change { get; init; } = "";
    public string Reason { get; init; } = "";

    /// <summary>The share of the energy that went with a game in front (0 to 100).</summary>
    public int GamesPercent => Wh > 0 ? (int)Math.Round(Math.Clamp(Parts.GameWh / Wh, 0, 1) * 100) : 0;

    /// <summary>A game drawing clearly more (or less) GPU power than usual at the same frame rate; null when none did, or it can't be told like for like.</summary>
    public string? GameLine { get; init; }
    public bool GameLineWarm { get; init; }

    /// <summary>
    /// Since 1.22.0, for the chart's hover: each day's usual energy (Wh), the same weekday's average over the 4 weeks before
    /// it that were recorded, by the day's local midnight; days with none are left out. Only for a chart of days.
    /// </summary>
    public IReadOnlyDictionary<DateTime, double> DayUsualWh { get; init; } = new Dictionary<DateTime, double>();

    /// <summary>How many whole days the period covers (for "about N kWh a day"); 0 for a day.</summary>
    public int Days { get; init; }

    /// <summary>The chart's steps: hours for a day (or a custom range of 2 days or less), months for a year (or one past 92 days), days otherwise.</summary>
    public PowerStep Step { get; init; }

    /// <summary>A custom range this long or shorter is drawn by the hour, as a day is; up to <see cref="CustomDaysMax"/> days by the day; past it by the month.</summary>
    public static readonly TimeSpan CustomHoursMax = ReportBuilder.DayLikeLimit;
    public const int CustomDaysMax = 92;

    public static PowerStep StepOf(PowerPeriod period, DateTime from, DateTime to) => period switch
    {
        PowerPeriod.Day => PowerStep.Hour,
        PowerPeriod.Year => PowerStep.Month,
        PowerPeriod.Custom when to - from <= CustomHoursMax => PowerStep.Hour,
        PowerPeriod.Custom when (to - from).TotalDays > CustomDaysMax => PowerStep.Month,
        _ => PowerStep.Day,
    };

    // ── Building ─────────────────────────────────────────────────────────

    /// <summary>A game's power is set against its own usual: at least this much like-for-like time, both sides.</summary>
    public const int MatchedMinutes = 60;
    /// <summary>…and said only from this much difference.</summary>
    public const double GameChange = 0.10;

    public static PowerReport Build(RigsightDb db, PowerPeriod period, DateTime from, DateTime to, Func<long, string> nameOf, IReadOnlyCollection<long> games, DateTime now)
    {
        long f = Unix(from), t = Unix(to);
        var step = StepOf(period, from, to);
        var buckets = db.GetPowerBuckets(f, t, step);
        var apps = db.GetPowerApps(f, t, games);
        var parts = db.GetPowerParts(f, t, games);
        double? cpu = buckets.Any(b => b.CpuWh is not null) ? buckets.Sum(b => b.CpuWh ?? 0) : null;
        double? gpu = buckets.Any(b => b.GpuWh is not null) ? buckets.Sum(b => b.GpuWh ?? 0) : null;
        double wh = (cpu ?? 0) + (gpu ?? 0);

        var (change, reason) = cpu is null && gpu is null ? ("", "") : Compare(db, period, from, to, wh, parts.GameMinutes, apps, nameOf, games);
        var (gameLine, warm) = GamePower(db, from, to, apps, nameOf);
        // Each day's usual for its hover: the 4 weeks before the period read once, with the period's own days.
        var usual = new Dictionary<DateTime, double>();
        if (step == PowerStep.Day && buckets.Count > 0)
        {
            var byDay = db.GetPowerBuckets(Unix(from.Date.AddDays(-28)), f, PowerStep.Day).Concat(buckets)
                .GroupBy(b => TimeUtil.FromUnix(b.Start).Date).ToDictionary(g => g.Key, g => g.Sum(b => b.Wh));
            foreach (var b in buckets)
                if (UsualOf(TimeUtil.FromUnix(b.Start).Date, byDay) is double u) usual[TimeUtil.FromUnix(b.Start).Date] = u;
        }
        return new PowerReport
        {
            Period = period, Step = step, From = from, To = to, CpuWh = cpu, GpuWh = gpu, Buckets = buckets, Apps = apps, Parts = parts,
            Change = change, Reason = reason, GameLine = gameLine, GameLineWarm = warm, DayUsualWh = usual,
            Days = step == PowerStep.Hour ? 0 : Math.Max(1, (int)Math.Round((Min(to, now.Date.AddDays(1)) - from).TotalDays)),
        };
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>
    /// A day's usual energy (Wh): the average of the same weekday in the 4 weeks before it that were recorded (as a day's
    /// page compares it, see <see cref="Compare"/>); null with none of them.
    /// </summary>
    public static double? UsualOf(DateTime day, IReadOnlyDictionary<DateTime, double> whByDay)
    {
        var before = Enumerable.Range(1, 4).Select(w => whByDay.TryGetValue(day.Date.AddDays(-7 * w), out double wh) ? wh : 0).Where(wh => wh > 0).ToList();
        return before.Count == 0 ? null : before.Average();
    }
    private static long Unix(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();

    /// <summary>
    /// The period against the one before it (a day: against the same weekday's usual, the average of the 4 before it that
    /// were recorded), and, when play time moved the same way by an hour or more, how much more or less was played.
    /// </summary>
    internal static (string Change, string Reason) Compare(RigsightDb db, PowerPeriod period, DateTime from, DateTime to, double wh, int gameMinutes,
        IReadOnlyList<PowerApp> apps, Func<long, string> nameOf, IReadOnlyCollection<long> games)
    {
        if (period == PowerPeriod.Day)
        {
            var before = Enumerable.Range(1, 4).Select(w => db.GetPowerTotal(Unix(from.AddDays(-7 * w)), Unix(to.AddDays(-7 * w)), games))
                .Where(x => x.Wh is > 0).Select(x => x.Wh!.Value).ToList();
            string reason = gameMinutes >= 30 && apps.FirstOrDefault(a => a.IsGame) is { } top
                ? Lang.T($"You played {Units.Duration(gameMinutes * 60)}, mostly {nameOf(top.App)}.") : "";
            if (before.Count == 0) return ("", reason);
            double usual = before.Average(), diff = wh - usual;
            string day = from.ToString("dddd", CultureInfo.CurrentCulture);
            string change = Math.Abs(diff) < Math.Max(100, usual * 0.05) ? Lang.T($"About the same as your usual {day}.")
                : diff > 0 ? Lang.T($"{Kwh(diff)} more than your usual {day}.") : Lang.T($"{Kwh(-diff)} less than your usual {day}.");
            return (change, reason);
        }
        var span = to - from;
        var (previous, previousGames) = db.GetPowerTotal(Unix(from - span), Unix(from), games);
        // Too little recorded before to set against (a PC first used part way through it).
        if (previous is not double pw || pw < wh * 0.1) return ("", "");
        string label = period switch
        {
            PowerPeriod.Week => Lang.T("the 7 days before"),
            PowerPeriod.Month => Lang.T("the 30 days before"),
            PowerPeriod.Custom => Lang.T("the same length of time before"),
            _ => Lang.T("the 12 months before"),
        };
        double pct = (wh - pw) / pw * 100;
        string changeText = Math.Abs(pct) < 5 ? Lang.T($"About the same as {label}.")
            : pct > 0 ? Lang.T($"{Math.Round(pct):0}% more than {label}.") : Lang.T($"{Math.Round(-pct):0}% less than {label}.");
        int played = gameMinutes - previousGames;
        // Play time is said only where it went the same way as the energy: it's what the minutes show, not a cause.
        string why = Math.Abs(pct) >= 5 && Math.Abs(played) >= 60 && Math.Sign(played) == Math.Sign(pct)
            ? (played > 0 ? Lang.T($"You played {Units.Duration(played * 60)} more.") : Lang.T($"You played {Units.Duration(-played * 60)} less."))
            : "";
        return (changeText, why);
    }

    /// <summary>
    /// The period's games (the three with the most energy) against their own 60 days before it, like for like: GPU power in
    /// the same bands of frame rate, weighted by this period's minutes. Said only with an hour or more matched on both sides
    /// and a 10% difference or more; the biggest one wins.
    /// </summary>
    internal static (string? Line, bool Warm) GamePower(RigsightDb db, DateTime from, DateTime to, IReadOnlyList<PowerApp> apps, Func<long, string> nameOf)
    {
        (string? Line, bool Warm, double Size) best = (null, false, 0);
        foreach (var game in apps.Where(a => a.IsGame).Take(3))
        {
            var now = db.GetGamePowerByFps(game.App, Unix(from), Unix(to));
            var then = db.GetGamePowerByFps(game.App, Unix(from.AddDays(-60)), Unix(from));
            double weighted = 0, usual = 0;
            int matched = 0, matchedThen = 0;
            foreach (var (band, n) in now)
                if (then.TryGetValue(band, out var b) && n.Minutes >= 10 && b.Minutes >= 10)
                {
                    weighted += n.Watts * n.Minutes;
                    usual += b.Watts * n.Minutes;
                    matched += n.Minutes;
                    matchedThen += b.Minutes;
                }
            if (matched < MatchedMinutes || matchedThen < MatchedMinutes || usual <= 0) continue;
            double ratio = weighted / usual - 1;
            if (Math.Abs(ratio) < GameChange || Math.Abs(ratio) <= best.Size) continue;
            int pct = (int)Math.Round(Math.Abs(ratio) * 100);
            string name = nameOf(game.App);
            best = ratio > 0
                ? (Lang.T($"{name} drew {pct}% more GPU power than usual at the same frame rate."), true, Math.Abs(ratio))
                : (Lang.T($"{name} drew {pct}% less GPU power than usual at the same frame rate."), false, Math.Abs(ratio));
        }
        return (best.Line, best.Warm);
    }

    // ── Words ────────────────────────────────────────────────────────────

    /// <summary>Energy in kWh, as many decimals as help: "0.21 kWh", "4.93 kWh", "22.9 kWh", "612 kWh".</summary>
    public static string Kwh(double wh)
    {
        double k = wh / 1000;
        string n = k >= 100 ? k.ToString("N0", CultureInfo.CurrentCulture) : k >= 10 ? k.ToString("0.0", CultureInfo.CurrentCulture) : k.ToString("0.00", CultureInfo.CurrentCulture);
        return n + " kWh";
    }

    /// <summary>Just the number of <see cref="Kwh"/>, for the headline beside its unit.</summary>
    public static string KwhNumber(double wh) => Kwh(wh)[..^4];

    public static string Watts(double? w) => w is double v ? $"{Math.Round(v):0} W" : "–";

    public static string Clock(double? mhz) => mhz is double m ? (m / 1000).ToString("0.00", CultureInfo.CurrentCulture) + " GHz" : "–";

    public static string Heavy(int minutes) => Units.Duration(minutes * 60.0);
}
