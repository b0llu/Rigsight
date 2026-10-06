using Rigsight.Core.Data;
using Rigsight.Core.Reports;

namespace Rigsight.Core.Ask;

internal sealed partial class AskRun
{
    private static string MetricName(AskMetric m) => m switch
    {
        AskMetric.CpuLoad => "CPU load",
        AskMetric.GpuLoad => "GPU load",
        AskMetric.CpuPower => "CPU power draw",
        AskMetric.GpuPower => "GPU power draw",
        AskMetric.Power => "The CPU and GPU together",
        AskMetric.CpuVolt => "CPU voltage",
        AskMetric.GpuVolt => "GPU voltage",
        AskMetric.CpuClock => "The CPU's clock",
        AskMetric.GpuClock => "The GPU's clock",
        AskMetric.CpuTemp => "CPU temperature",
        AskMetric.GpuTemp => "GPU temperature",
        AskMetric.HotSpot => "The GPU hot spot",
        _ => "Memory in use",
    };

    /// <summary>A minute's reading: its average and, where the minute keeps one, its highest moment.</summary>
    private static (double? Average, double? High) ReadingOf(SystemMinute m, AskMetric metric) => metric switch
    {
        AskMetric.CpuLoad => (m.CpuLoad, m.CpuLoad),
        AskMetric.GpuLoad => (m.GpuLoad, m.GpuLoad),
        AskMetric.CpuPower => (m.CpuPower, m.CpuPower),
        AskMetric.GpuPower => (m.GpuPower, m.GpuPower),
        AskMetric.Power => m.CpuPower is null && m.GpuPower is null ? (null, null) : ((m.CpuPower ?? 0) + (m.GpuPower ?? 0), (m.CpuPower ?? 0) + (m.GpuPower ?? 0)),
        AskMetric.CpuVolt => (m.CpuVoltMax, m.CpuVoltMax),
        AskMetric.GpuVolt => (m.GpuVoltMax, m.GpuVoltMax),
        AskMetric.CpuClock => (m.CpuClock, m.CpuClock),
        AskMetric.GpuClock => (m.GpuClock, m.GpuClock),
        AskMetric.CpuTemp => (m.CpuTemp, m.CpuTempMax ?? m.CpuTemp),
        AskMetric.GpuTemp => (m.GpuTemp, m.GpuTempMax ?? m.GpuTemp),
        AskMetric.HotSpot => (m.GpuHotMax, m.GpuHotMax),
        _ => (m.RamUsed, m.RamUsed),
    };

    private static string Reading(AskMetric metric, double v) => metric switch
    {
        AskMetric.CpuLoad or AskMetric.GpuLoad => $"{v:0}%",
        AskMetric.CpuPower or AskMetric.GpuPower or AskMetric.Power => $"{v:0} W",
        AskMetric.CpuVolt or AskMetric.GpuVolt => $"{v:0.000} V",
        AskMetric.CpuClock or AskMetric.GpuClock => Units.Format(SensorKind.Clock, v),
        AskMetric.CpuTemp or AskMetric.GpuTemp or AskMetric.HotSpot => Temp(v),
        _ => $"{v:0.0} GB",
    };

    /// <summary>A reading's figures over some minutes (null: it wasn't recorded in them).</summary>
    private sealed record Figures(double Average, double High, DateTime HighAt, long? HighApp, double Low, int Minutes);

    private Figures? FiguresOf(AskMetric metric, AskPeriod p)
    {
        var minutes = MinutesOf(p);
        if (_q.AppId is long app) minutes = [.. minutes.Where(m => m.FgApp == app)];
        // "…while gaming": the minutes a game was in front.
        else if (_q.Games) minutes = [.. minutes.Where(m => m.FgApp is long front && _apps.TryGetValue(front, out var row) && CategoryOf(row) == Settings.AppCategory.Game)];
        var read = minutes.Select(m => (Minute: m, Value: ReadingOf(m, metric))).Where(x => x.Value.Average is not null).ToList();
        if (read.Count == 0) return null;
        var top = read.MaxBy(x => x.Value.High)!;
        return new(read.Average(x => x.Value.Average!.Value), top.Value.High!.Value, TimeUtil.FromUnix(top.Minute.Ts), top.Minute.FgApp,
            read.Min(x => x.Value.Average!.Value), read.Count);
    }

    private AskAnswer Metric() => Metric(PeriodOr(() => Today), null);

    /// <summary>
    /// Two apps side by side ("do I play more Rematch or Dota?", "which runs hotter?"): time in each, the temperature each
    /// holds at steady load, or the memory each takes. Null for what isn't compared between apps: the first is then answered alone.
    /// </summary>
    private AskAnswer? TwoApps()
    {
        if (_q.AppId is not long one || _q.OtherAppId is not long two) return null;
        var a = new AskAnswer();
        string x = NameOf(one), y = NameOf(two);
        switch (_q.Intent)
        {
            case AskIntent.Usage or AskIntent.TopApps:
            {
                var period = PeriodOr(() => AskTime.LastDays(AppUseDays, Now));
                var r = ReportFor(period);
                double tx = r.Apps.FirstOrDefault(s => s.Id == one)?.ActiveSec ?? 0, ty = r.Apps.FirstOrDefault(s => s.Id == two)?.ActiveSec ?? 0;
                a.Lead = tx < 60 && ty < 60 ? $"Neither: {x} and {y} weren't in use {period.Label}."
                    : Math.Abs(tx - ty) < 60 ? $"**About the same** {period.Label}: {Dur(tx)} each."
                    : tx > ty ? $"**{x}**, {period.Label}: {Dur(tx)}, against {(ty < 60 ? "none" : Dur(ty))} for {y}."
                    : $"**{y}**, {period.Label}: {Dur(ty)}, against {(tx < 60 ? "none" : Dur(tx))} for {x}.";
                a.Facts.Add(new(x, tx < 60 ? "None" : Dur(tx)));
                a.Facts.Add(new(y, ty < 60 ? "None" : Dur(ty)));
                a.Links.Add(new("Open Apps", "apps"));
                return a;
            }
            case AskIntent.Temps:
            {
                var period = PeriodOr(() => AskTime.LastDays(AppHeatDays, Now));
                var r = ReportFor(period);
                var (sx, sy) = (r.Steady.FirstOrDefault(s => s.AppId == one && s.Minutes >= 20), r.Steady.FirstOrDefault(s => s.AppId == two && s.Minutes >= 20));
                a.Links.Add(new("Open Reports", "reports"));
                if (sx is null || sy is null)
                {
                    string missing = sx is null && sy is null ? $"Neither {x} nor {y} worked" : $"{(sx is null ? x : y)} didn't work";
                    a.Lead = $"**I can't say fairly.** {missing} the graphics card hard for 20 minutes or more {period.Label}, and that is what I compare games by.";
                    if ((sx ?? sy) is { } only) a.Paragraphs.Add($"{only.App} held {Temp(only.Gpu)} on the GPU at steady load.");
                    return a;
                }
                a.Lead = Math.Abs(sx.Gpu - sy.Gpu) < 2 ? $"**About the same** {period.Label}: the GPU holds {Temp(sx.Gpu)} in {x} and {Temp(sy.Gpu)} in {y} at steady load."
                    : $"**{(sx.Gpu > sy.Gpu ? x : y)} runs hotter** {period.Label}: the GPU holds {Temp(Math.Max(sx.Gpu, sy.Gpu))}, against {Temp(Math.Min(sx.Gpu, sy.Gpu))} in {(sx.Gpu > sy.Gpu ? y : x)}.";
                a.Facts.Add(new(x, Temp(sx.Gpu)));
                a.Facts.Add(new(y, Temp(sy.Gpu)));
                a.Note = "Each is the GPU's average once warmed up and at heavy load. Different games ask different things of it, so this says which is the heavier game, not that anything is wrong.";
                return a;
            }
            case AskIntent.Memory:
            {
                var period = PeriodOr(() => AskTime.LastDays(30, Now));
                var r = ReportFor(period);
                double? mx = r.Apps.FirstOrDefault(s => s.Id == one)?.MemMax, my = r.Apps.FirstOrDefault(s => s.Id == two)?.MemMax;
                a.Links.Add(new("Open Memory", "memory"));
                if (mx is null || my is null)
                {
                    a.Lead = $"{(mx is null ? x : y)} wasn't running {period.Label}, so there's nothing to set against {(mx is null ? y : x)}.";
                    return a;
                }
                a.Lead = $"**{(mx > my ? x : y)}** takes more memory {period.Label}: up to {Units.Megabytes(Math.Max(mx.Value, my.Value))}, against {Units.Megabytes(Math.Min(mx.Value, my.Value))} for {(mx > my ? y : x)}.";
                a.Facts.Add(new(x, Units.Megabytes(mx.Value)));
                a.Facts.Add(new(y, Units.Megabytes(my.Value)));
                return a;
            }
            case AskIntent.Network:
            {
                var period = PeriodOr(() => AskTime.Month(Now, Now));
                long from = TimeUtil.ToUnix(period.From.Date), to = TimeUtil.ToUnix(period.To);
                var use = _db.GetNetAppTotals(from, to);
                long dx = use.FirstOrDefault(u => u.App == one)?.Total ?? 0, dy = use.FirstOrDefault(u => u.App == two)?.Total ?? 0;
                a.Links.Add(new("Open Network", "network"));
                a.Lead = dx == 0 && dy == 0 ? $"Neither {x} nor {y} used the internet {period.Label}, as far as the records show."
                    : $"**{(dx >= dy ? x : y)}** used more data {period.Label}: {Units.Data(Math.Max(dx, dy))}, against {(Math.Min(dx, dy) == 0 ? "none" : Units.Data(Math.Min(dx, dy)))} for {(dx >= dy ? y : x)}.";
                a.Facts.Add(new(x, dx == 0 ? "None" : Units.Data(dx)));
                a.Facts.Add(new(y, dy == 0 ? "None" : Units.Data(dy)));
                return a;
            }
            case AskIntent.Crashes:
            {
                var period = PeriodOr(() => AskTime.LastDays(CrashLookBack, Now));
                var all = CrashesIn(period.From, period.To);
                int Of(long id) => _apps.TryGetValue(id, out var row) ? all.Count(c => string.Equals(c.AppExe, row.Exe, StringComparison.OrdinalIgnoreCase)) : 0;
                int cx = Of(one), cy = Of(two);
                a.Links.Add(new("Open Crashes", "crashes"));
                a.Lead = cx == 0 && cy == 0 ? $"**Neither crashed** {period.Label}: no problems on record for {x} or {y}."
                    : cx == cy ? $"**The same** {period.Label}: {Count(cx, "problem", "problems")} each."
                    : $"**{(cx > cy ? x : y)}** had more problems {period.Label}: {Math.Max(cx, cy)}, against {(Math.Min(cx, cy) == 0 ? "none" : Math.Min(cx, cy).ToString())} for {(cx > cy ? y : x)}.";
                a.Facts.Add(new(x, cx.ToString(), cx > 0 ? AskTone.Warn : AskTone.Good));
                a.Facts.Add(new(y, cy.ToString(), cy > 0 ? AskTone.Warn : AskTone.Good));
                return a;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// One reading over a time, and in one app when one is named (the minutes it was in front): its average, its
    /// highest with when that was, and its lowest; set against a second time when the question compares two.
    /// </summary>
    private AskAnswer Metric(AskPeriod period, AskPeriod? against)
    {
        var metric = _q.Metric;
        var a = new AskAnswer();
        a.Links.Add(new("Open Reports", "reports", period.IsDay ? period.From : null));
        string name = MetricName(metric);
        string scope = _q.App is { } app ? $"while {app} was in front {period.Label}" : _q.Games ? $"while a game was in front {period.Label}" : period.Label;
        if (period.Days > MinuteDays || against is { Days: > MinuteDays })
        {
            a.Lead = "Readings are kept by the minute for about a month back. Ask about a shorter, more recent time.";
            return a;
        }
        // "CPU usage of Chrome": the app's own share of the processor is what is asked, in front or not.
        if (metric == AskMetric.CpuLoad && against is null && _q.AppId is long asked && ReportFor(period).Apps.FirstOrDefault(x => x.Id == asked) is { CpuAvg: double share } itself)
        {
            a.Lead = $"**{itself.Name}** itself took **{share:0.#}%** of the processor on average {period.Label}{(itself.CpuMax is double top ? $", {top:0}% at the most" : "")}.";
            if (FiguresOf(metric, period) is { } front)
                a.Paragraphs.Add($"While it was in front, the whole processor averaged {Reading(metric, front.Average)} and reached {Reading(metric, front.High)}.");
            a.Note = "An app's share is of the whole processor, averaged over the time it was running.";
            a.FollowUps.Add($"What uses the most CPU {period.Label}?");
            return a;
        }
        if (FiguresOf(metric, period) is not { } f)
        {
            a.Lead = _q.App is not null && MinutesOf(period).Count > 0 && !MinutesOf(period).Any(m => m.FgApp == _q.AppId)
                ? $"{_q.App} wasn't in front {period.Label}, so there's nothing to read."
                : _q.Games && _q.App is null && MinutesOf(period).Count > 0 ? $"No game was in front {period.Label}, so there's nothing to read."
                : $"{name} wasn't recorded {period.Label}.";
            if (metric is AskMetric.CpuTemp or AskMetric.CpuPower or AskMetric.CpuClock) a.Note = "Some CPU readings need the agent to run with admin rights.";
            return a;
        }
        string R(double v) => Reading(metric, v);
        string high = $"{R(f.High)} ({AtIn(f.HighAt, period)}{(_q.App is null && f.HighApp is long by ? $", with {NameOf(by)} in front" : "")})";

        if (against is not null)
        {
            if (FiguresOf(metric, against) is not { } g)
            {
                a.Lead = $"{name} averaged **{R(f.Average)}** {scope}, but there's nothing on record {against.Label} to set it against.";
                return a;
            }
            a.Lead = $"{name} averaged **{R(f.Average)}** {scope}, against **{R(g.Average)}** {against.Label}.";
            a.Paragraphs.Add($"At the highest: {R(f.High)} {period.Label}, {R(g.High)} {against.Label}.");
            a.Facts.Add(new($"{period.Title}, average", R(f.Average)));
            a.Facts.Add(new($"{against.Title}, average", R(g.Average)));
            a.Note = "These are averages over the minutes the PC was on. What was running differs from one time to another, so a difference here is not a change in the PC by itself.";
            return a;
        }

        a.Lead = _q.Aggregate switch
        {
            AskAggregate.Highest => $"{name} was highest at **{R(f.High)}** {scope}: {AtIn(f.HighAt, period)}{(_q.App is null && f.HighApp is long top ? $", with {NameOf(top)} in front" : "")}.",
            AskAggregate.Lowest => $"{name} was lowest at **{R(f.Low)}** {scope}.",
            _ => $"{name} averaged **{R(f.Average)}** {scope}.",
        };
        a.Paragraphs.Add(_q.Aggregate switch
        {
            AskAggregate.Highest => $"On average it was {R(f.Average)}, and {R(f.Low)} at the lowest.",
            AskAggregate.Lowest => $"On average it was {R(f.Average)}, and {high} at the highest.",
            _ => $"The highest was {high}, the lowest {R(f.Low)}.",
        });
        a.Facts.Add(new("Average", R(f.Average)));
        a.Facts.Add(new("Highest", R(f.High)));
        a.Facts.Add(new("Lowest", R(f.Low)));

        // The app's own share of the processor, where that is what was asked ("CPU usage of Chrome").
        if (metric == AskMetric.CpuLoad && _q.AppId is long id && ReportFor(period).Apps.FirstOrDefault(x => x.Id == id) is { CpuAvg: double own } stat)
            a.Paragraphs.Add($"That is the whole processor while it was in front. {stat.Name} itself took {own:0}% on average{(stat.CpuMax is double most ? $", {most:0}% at the most" : "")}.");
        a.Note = $"Over {Dur(f.Minutes * 60.0)} of readings, one a minute{(metric is AskMetric.CpuLoad or AskMetric.GpuLoad or AskMetric.CpuPower or AskMetric.GpuPower ? "; each is that minute's average, so a few seconds' spike doesn't show" : "")}.";
        if (metric == AskMetric.Power) a.Paragraphs.Add("That is the two chips only, drawing power as their own sensors report it. The rest of the PC (board, drives, fans, screen) isn't measured.");
        if (metric is AskMetric.CpuVolt or AskMetric.GpuVolt) a.Paragraphs.Add("Voltage is kept as each minute's highest reading, so the average here is the average of those highs.");
        a.FollowUps.Add(_q.App is null ? $"What was heavy on my PC {period.Label}?" : $"How hot did {_q.App} make my PC {period.Label}?");
        if (!IsToday(period)) a.FollowUps.Add($"What about today?");
        return a;
    }
}
