using Rigsight.Core.Apps;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Reports;

/// <summary>Turns report numbers into short, plain-language observations.</summary>
public static class InsightEngine
{
    private const double MinUseForRanking = 10 * 60; // an app needs 10 min of use to be ranked by temperature

    public static List<Insight> Generate(Report r, Report? previous, (double? Cpu, double? Gpu) baseline)
    {
        var list = new List<Insight>();
        if (!r.HasData) return list;

        string period = r.Range switch { ReportRange.Day => "day", ReportRange.Week => "week", _ => "month" };
        string prevLabel = r.Range switch
        {
            ReportRange.Day => r.From.Date == DateTime.Today ? "yesterday at this time" : "the day before",
            ReportRange.Week => "the week before",
            _ => "the month before",
        };

        // Screen time, compared with the previous period.
        if (r.ActiveSec > 0)
        {
            string text = $"You actively used your PC for {Units.Duration(r.ActiveSec)} (on for {Units.Duration(r.OnSec)}).";
            if (previous is { HasData: true } && previous.ActiveSec > 0)
            {
                double diff = r.ActiveSec - previous.ActiveSec;
                if (Math.Abs(diff) >= 15 * 60)
                    text += $" That's {Units.Duration(Math.Abs(diff))} {(diff > 0 ? "more" : "less")} than {prevLabel}.";
            }
            list.Add(new Insight("", text));
        }

        // Where the time went.
        var top = r.Apps.FirstOrDefault(a => a.ActiveSec > 0 && a.Category != AppCategory.System);
        if (top is not null && r.ActiveSec > 0)
        {
            int pct = (int)Math.Round(top.ActiveSec / r.ActiveSec * 100);
            list.Add(new Insight("", $"{top.Name} took most of your time: {Units.Duration(top.ActiveSec)} ({pct}% of active time)."));
        }

        // Gaming.
        var games = r.Apps.Where(a => a.Category == AppCategory.Game && a.ActiveSec >= 60).ToList();
        if (games.Count > 0)
        {
            double total = games.Sum(g => g.ActiveSec);
            string which = games.Count == 1 ? games[0].Name : $"{games.Count} games";
            var longest = r.Sessions.Where(s => s.IsGame).MaxBy(s => s.ActiveSec);
            string text = $"You gamed for {Units.Duration(total)} ({which}).";
            if (longest is not null && longest.ActiveSec >= 20 * 60)
                text += $" Longest session: {longest.Name}, {Units.Duration(longest.ActiveSec)} starting {longest.Start:h:mm tt}.";
            list.Add(new Insight("", text));
        }

        // Crashes.
        if (r.Crashes.Count > 0)
        {
            var latest = r.Crashes[0];
            var name = r.Apps.FirstOrDefault(a => a.Exe.Equals(latest.AppExe, StringComparison.OrdinalIgnoreCase))?.Name;
            var ex = CrashExplainer.Explain(latest, name);
            string more = r.Crashes.Count > 1 ? $" ({r.Crashes.Count} crashes in total — see the Crashes page)" : "";
            list.Add(new Insight("\uE7BA", $"{ex.Title} at {latest.Time:h:mm tt}: {ex.Culprit.ToLowerInvariant()}.{more}", InsightTone.Warn));
        }

        // Hottest moments.
        if (r.CpuTempPeak is { } cpu)
            list.Add(new Insight("",
                $"CPU peaked at {Units.TempShort(cpu.Value)} at {cpu.Time:h:mm tt}{While(r, cpu.App)}.", ToneFor(cpu.Value, 75, 88)));
        if (r.GpuTempPeak is { } gpu)
        {
            string hot = r.GpuHotPeak is { } hs ? $" (hot spot {Units.TempShort(hs.Value)})" : "";
            list.Add(new Insight("",
                $"GPU peaked at {Units.TempShort(gpu.Value)}{hot} at {gpu.Time:h:mm tt}{While(r, gpu.App)}.", ToneFor(gpu.Value, 75, 85)));
        }

        // Which app runs the hardware hottest on average (not Windows parts or Rigsight itself).
        var ranked = r.Apps.Where(a => a.ActiveSec >= MinUseForRanking && a.Category != AppCategory.System).ToList();
        var hottestGpu = ranked.Where(a => a.GpuTempAvg is not null).MaxBy(a => a.GpuTempAvg);
        var hottestCpu = ranked.Where(a => a.CpuTempAvg is not null).MaxBy(a => a.CpuTempAvg);
        if (hottestGpu is not null && ranked.Count > 1)
            list.Add(new Insight("", $"{hottestGpu.Name} ran your GPU the hottest, averaging {Units.TempShort(hottestGpu.GpuTempAvg)}.",
                ToneFor(hottestGpu.GpuTempAvg!.Value, 72, 82)));
        if (hottestCpu is not null && ranked.Count > 1 && hottestCpu != hottestGpu)
            list.Add(new Insight("", $"{hottestCpu.Name} ran your CPU the hottest, averaging {Units.TempShort(hottestCpu.CpuTempAvg)}.",
                ToneFor(hottestCpu.CpuTempAvg!.Value, 72, 82)));

        // Compared with the usual.
        if (r.CpuTempAvg is double cAvg && baseline.Cpu is double cBase && Math.Abs(cAvg - cBase) >= 3)
            list.Add(new Insight("",
                $"Your CPU averaged {DegreesDiff(cAvg - cBase)} {(cAvg > cBase ? "hotter" : "cooler")} than over the previous 7 days.",
                cAvg > cBase ? InsightTone.Warn : InsightTone.Good));
        if (r.GpuTempAvg is double gAvg && baseline.Gpu is double gBase && Math.Abs(gAvg - gBase) >= 3)
            list.Add(new Insight("",
                $"Your GPU averaged {DegreesDiff(gAvg - gBase)} {(gAvg > gBase ? "hotter" : "cooler")} than over the previous 7 days.",
                gAvg > gBase ? InsightTone.Warn : InsightTone.Good));

        // Voltage.
        if (r.CpuVoltPeak is { } volt)
            list.Add(new Insight("", $"Highest CPU core voltage was {volt.Value:0.000} V{While(r, volt.App)}."));

        // Open-but-unused apps.
        var idleHog = r.Apps
            .Where(a => a.BackgroundSec + a.MinimizedSec >= 3600 && a.ActiveSec < (a.BackgroundSec + a.MinimizedSec) / 4)
            .MaxBy(a => a.BackgroundSec + a.MinimizedSec);
        if (idleHog is not null)
            list.Add(new Insight("",
                $"{idleHog.Name} sat open in the background for {Units.Duration(idleHog.BackgroundSec + idleHog.MinimizedSec)} but you only used it for {Units.Duration(idleHog.ActiveSec)}."));

        // Memory.
        var memHog = r.Apps.Where(a => a.MemMax is not null).MaxBy(a => a.MemMax);
        if (memHog?.MemMax is double mem && mem >= 500)
            list.Add(new Insight("", $"{memHog.Name} used the most memory, peaking at {Units.Megabytes(mem)}."));

        // Away time.
        if (r.AwaySec >= 3600 && r.Range == ReportRange.Day)
            list.Add(new Insight("",
                $"Your PC sat unattended for {Units.Duration(r.AwaySec)} this {period}. Letting it sleep sooner would save power.",
                InsightTone.Warn));

        // Reassurance when everything was cool.
        if (r.CpuTempPeak is { Value: < 70 } && r.GpuTempPeak is null or { Value: < 70 })
            list.Add(new Insight("", "Temperatures stayed comfortable the whole time.", InsightTone.Good));

        return list;
    }

    /// <summary>" while playing Dota 2", " while browsing in Chrome"… (empty if no app was in front).</summary>
    private static string While(Report r, string? app) => app is null ? ""
        : " " + ActivityWords.While(app, r.Apps.FirstOrDefault(a => a.Name == app)?.Category ?? AppCategory.Other);

    private static string DegreesDiff(double celsiusDiff)
    {
        double d = Math.Abs(celsiusDiff) * (Units.Fahrenheit ? 9.0 / 5 : 1);
        return $"{d:0}°";
    }

    private static InsightTone ToneFor(double celsius, double warn, double hot) =>
        celsius >= hot ? InsightTone.Hot : celsius >= warn ? InsightTone.Warn : InsightTone.Neutral;
}
