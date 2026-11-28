using System.Globalization;
using Rigsight.Core.Data;

namespace Rigsight.Core.Reports;

/// <summary>A part's share of a day in the Power chart's hover: its energy (Wh) and its share of the day in whole percent.</summary>
public sealed record PowerDayPart(double Wh, int Percent);

/// <summary>An app in front that day, and the CPU and GPU energy in its minutes (Wh).</summary>
public sealed record PowerDayApp(long App, double Wh);

/// <summary>
/// What the Power chart's hover says about one day (1.22.0, the approved mock-up's Energy each day): the day, the time in
/// games (or "So far today"), the energy big, each part with its share of the day (from the unrounded figures), the day
/// against the usual for its weekday, its estimated cost at the person's price, and the apps in front with their energy.
/// A part that gives no power reading, a usual there's none of yet, and a cost without a price are left out.
/// </summary>
public sealed record PowerDayFacts(string Title, string Sub, string Big, string BigWord, PowerDayPart? Cpu, PowerDayPart? Gpu,
    string? AgainstLabel, string? Against, string? Cost, IReadOnlyList<PowerDayApp> Apps);

/// <summary>Builds <see cref="PowerDayFacts"/> (pure: the tests read it).</summary>
public static class PowerDayHover
{
    /// <summary>The apps named in the hover, at most, and the least energy one needs to be named (Wh).</summary>
    public const int AppsShown = 3;
    public const double AppMinWh = 5;

    /// <param name="usualWh">The day's usual (see <see cref="PowerReport.UsualOf"/>); null with none yet.</param>
    /// <param name="games">The apps that are games, for the time in games.</param>
    /// <param name="price">The person's electricity price a kWh and its currency; null with none set.</param>
    public static PowerDayFacts Of(PowerBucket b, DateTime day, double? usualWh, DateTime now, IReadOnlySet<long> games, (double PerKwh, string Code)? price)
    {
        bool today = day.Date == now.Date;
        string title = day.ToString(TimeText.Fit("ddd d MMM"), CultureInfo.CurrentCulture);
        int gameMinutes = (b.Apps ?? []).Where(a => games.Contains(a.App)).Sum(a => a.Minutes);
        string sub = today ? Lang.T("So far today")
            : gameMinutes > 0 ? Lang.T($"{Units.Duration(gameMinutes * 60.0)} in games") : Lang.T("No games played");

        string word = b.CpuWh is not null && b.GpuWh is not null ? Lang.T("CPU and GPU") : b.CpuWh is not null ? "CPU" : "GPU";
        var (cpu, gpu) = Shares(b.CpuWh, b.GpuWh);

        string? label = null, against = null;
        if (today)
        {
            label = Lang.T($"Against your usual {day.ToString("dddd", CultureInfo.CurrentCulture)}");
            against = Lang.T("Day not over");
        }
        else if (usualWh is double usual && usual > 0)
        {
            label = Lang.T($"Against your usual {day.ToString("dddd", CultureInfo.CurrentCulture)}");
            against = Against(b.Wh, usual);
        }

        string? cost = price is var (perKwh, code) && perKwh > 0
            ? Lang.T($"{Money.Format(b.Wh / 1000 * perKwh, code)} at {Money.Format(perKwh, code)} a kWh") : null;

        var apps = (b.Apps ?? []).Where(a => a.Wh >= AppMinWh).OrderByDescending(a => a.Wh).Take(AppsShown).Select(a => new PowerDayApp(a.App, a.Wh)).ToList();
        return new PowerDayFacts(title, sub, PowerReport.Kwh(b.Wh), word, cpu, gpu, label, against, cost, apps);
    }

    /// <summary>
    /// Each part's share of the day in whole percent, from the unrounded figures, the two adding up to 100 (the CPU's
    /// rounded, the GPU's the rest); a part alone is all of it; a part with no reading is null.
    /// </summary>
    public static (PowerDayPart? Cpu, PowerDayPart? Gpu) Shares(double? cpuWh, double? gpuWh)
    {
        double all = (cpuWh ?? 0) + (gpuWh ?? 0);
        if (cpuWh is double c && gpuWh is double g)
        {
            int cp = all > 0 ? (int)Math.Round(c / all * 100) : 0;
            return (new(c, cp), new(g, all > 0 ? 100 - cp : 0));
        }
        return (cpuWh is double oc ? new(oc, all > 0 ? 100 : 0) : null, gpuWh is double og ? new(og, all > 0 ? 100 : 0) : null);
    }

    /// <summary>"0.40 kWh more", "0.40 kWh less", or "About the same" within 0.1 kWh or 5% of the usual (as the page's own line).</summary>
    public static string Against(double wh, double usualWh)
    {
        double diff = wh - usualWh;
        return Math.Abs(diff) < Math.Max(100, usualWh * 0.05) ? Lang.T("About the same")
            : diff > 0 ? Lang.T($"{PowerReport.Kwh(diff)} more") : Lang.T($"{PowerReport.Kwh(-diff)} less");
    }
}
