using System.Globalization;
using System.Net;
using System.Text;
using Rigsight.Core.Apps;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Export;

/// <summary>One sensor as the PC snapshot lists it: its reading now, and the lowest and highest since the agent started.</summary>
public sealed record SnapshotReading(string Part, string Kind, string Name, string Now, string Lowest, string Highest);

/// <summary>A crash with what was going on around it, for the crashes file.</summary>
public sealed record CrashStory(CrashEvent Crash, string? App, CrashContext? Context, string? AppInFront, IReadOnlyList<SystemChange> ChangesBefore)
{
    /// <summary>What most likely happened and how sure that is, as every page says it (null: said from the crash's own entry alone).</summary>
    public CrashFinding? Finding { get; init; }

    /// <summary>What the big changes before it made better or worse, by day (only where they did): said beside each change.</summary>
    public IReadOnlyDictionary<DateTime, Reports.ChangeResult> Results { get; init; } = new Dictionary<DateTime, Reports.ChangeResult>();

    /// <summary>"NVIDIA graphics driver 618.10 (10 Oct; Crimson Desert runs 10° hotter on the GPU since driver 618.10; likely the driver)".</summary>
    public string Changed => string.Join("; ", ChangesBefore.Select(ch => Results.TryGetValue(ch.Time.Date, out var r) && r.Main == ch
        ? Lang.T($"{ch.Shown} ({ch.Time:d MMM}; {r.Said}; {Wording.Running(r.Chip)})")
        : $"{ch.Shown} ({ch.Time:d MMM})"));

    /// <summary>What stood out in its close-up, a reading every two seconds, where one was kept (see <see cref="CloseUp"/>).</summary>
    public IReadOnlyList<string> InLastMinutes { get; init; } = [];

    /// <summary>The finding as rows: what happened, how sure, what it rests on, what was ruled out, what can't be told, what to try.</summary>
    public IEnumerable<(string Label, string? Value)> Found()
    {
        if (Finding is not { } f)
        {
            var said = CrashExplainer.Explain(Crash, App);
            yield return (Lang.T("Likely reason"), said.Reason);
            yield return (Lang.T("What to try"), said.Advice);
            yield break;
        }
        yield return (Lang.T("What happened"), $"{f.Head} {f.Text}");
        yield return (Lang.T("How sure"), f.HowSure);
        yield return (Lang.T("What points to this"), f.Points.Count == 0 ? null : string.Join("; ", f.Points.Select(p => p.Detail.Length > 0 ? $"{p.Title} ({p.Detail.TrimEnd('.')})" : p.Title)));
        yield return (Lang.T("What it wasn't"), f.RuledOut.Count == 0 ? null : string.Join("; ", f.RuledOut.Select(p => $"{p.Title} ({p.Detail.TrimEnd('.')})")));
        yield return (Lang.T("Could not be checked"), f.NotChecked.Count == 0 ? null : string.Join("; ", f.NotChecked.Select(p => $"{p.Title} ({p.Detail.TrimEnd('.')})")));
        yield return (Lang.T("Still unknown"), f.CantTell);
        yield return (Lang.T("In the last minutes"), InLastMinutes.Count == 0 ? null : string.Join(" ", InLastMinutes));
        yield return (Lang.T("What to try"), f.Advice);
    }
}

/// <summary>
/// The exports made to be read and passed on, each one file that needs nothing else: this PC as it is now (plain text,
/// for a forum post or a support request), a period as the Reports page tells it and the crashes with what was going
/// on around each (a web page any browser opens, and prints to PDF).
/// </summary>
public static class ReadableExport
{
    public const string SnapshotFile = "PC snapshot.txt", ReportFile = "Report.html", CrashesFile = "Crashes.html";
    public const string ReportTextFile = "Report.txt", CrashesTextFile = "Crashes with their context.txt";

    // ── The same two as plain text ──

    private static void Heading(StringBuilder sb, string text)
    {
        sb.AppendLine();
        sb.AppendLine(text.ToUpperInvariant());
    }

    private static string Foot(DateTime made, string version)
    {
        string when = made.ToString(TimeText.Fit("d MMMM yyyy, h:mm tt"), CultureInfo.InvariantCulture);
        return Lang.T($"Made by RigSight {version} on {when}. Everything here was recorded on this PC.");
    }

    /// <summary>The period as the Reports page tells it, laid out as text: totals, peaks, what stood out, every app, the crashes.</summary>
    /// <param name="times">With the period's times (active, on, in games): off where they aren't to be shared.</param>
    public static string ReportText(Report r, string title, string version, bool times = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Lang.T("RigSight report"));
        sb.AppendLine(title);
        if (!r.HasData)
        {
            sb.AppendLine();
            sb.AppendLine(Lang.T("Nothing was recorded in this time."));
            return sb.AppendLine().AppendLine(Foot(r.BuiltAt, version)).ToString();
        }
        var totals = new List<object?[]>();
        if (times)
        {
            totals.Add([Lang.T("Active"), Units.Duration(r.ActiveSec)]);
            totals.Add([Lang.T("PC on"), Units.Duration(r.OnSec)]);
            if (r.GamingSec >= 60) totals.Add([Lang.T("In games"), Units.Duration(r.GamingSec)]);
        }
        if (r.Net is { } net && net.Down + net.Up > 0) totals.Add([Lang.T("Internet"), Lang.T($"{Units.Data(net.Down)} downloaded, {Units.Data(net.Up)} uploaded")]);
        void Peak(string name, Peak? p, Func<double, string> say) { if (p is not null) totals.Add([name, $"{say(p.Value)}  ({PeakLine(p)})"]); }
        Peak(Lang.T("CPU peak"), r.CpuTempPeak, v => Units.TempShort(v));
        Peak(Lang.T("GPU peak"), r.GpuTempPeak, v => Units.TempShort(v));
        if (totals.Count > 0)
        {
            Heading(sb, Lang.T("The period"));
            int name = totals.Max(t => ((string)t[0]!).Length);
            foreach (var t in totals) sb.AppendLine($"  {((string)t[0]!).PadRight(name)}  {t[1]}");
        }

        if (r.Insights.Count > 0)
        {
            Heading(sb, Lang.T("What stood out"));
            foreach (var i in r.Insights) sb.AppendLine("  - " + i.Text);
        }
        if (PcUsual.CardRows(r) is { Count: > 0 } usual)
        {
            Heading(sb, Lang.T("Against your usual"));
            var rows = usual.Select(u => new object?[] { u.Label, u.Now, $"{u.Usual} ({u.UsualDetail})", u.MeansDetail is null ? u.Means : $"{u.Means}: {u.MeansDetail}" }).ToList();
            foreach (var line in DataExport.TextTable([Lang.T("What"), Lang.T("This period"), Lang.T("Your usual"), Lang.T("What it means")], rows)) sb.AppendLine("  " + line);
        }
        var apps = r.Apps.Where(a => a.ActiveSec >= 60).ToList();
        if (apps.Count > 0)
        {
            Heading(sb, Lang.T("Apps you used"));
            var rows = apps.Select(a => new object?[]
            {
                a.Name, AppCatalog.Label(a.Category), Units.Duration(a.ActiveSec), a.BackgroundSec > 0 ? Units.Duration(a.BackgroundSec) : null,
                a.CpuTempMax is double c ? Units.TempShort(c) : null, a.GpuTempMax is double g ? Units.TempShort(g) : null, a.MemMax is double m ? Units.Megabytes(m) : null,
            }).ToList();
            foreach (var line in DataExport.TextTable([Lang.T("App"), Lang.T("Kind"), Lang.T("In use"), Lang.T("Background"), Lang.T("CPU peak"), Lang.T("GPU peak"), Lang.T("Memory peak")], rows)) sb.AppendLine("  " + line);
        }
        if (r.Crashes.Count > 0)
        {
            Heading(sb, Lang.T("Crashes and sudden shut-offs"));
            foreach (var c in r.Crashes.OrderBy(c => c.Ts)) sb.AppendLine($"  {TimeText.At(TimeUtil.FromUnix(c.Ts), "d MMM, h:mm tt")}  {CrashExplainer.Explain(c).Title}");
        }
        return sb.AppendLine().AppendLine(Foot(r.BuiltAt, version)).ToString();
    }

    /// <summary>The crashes, each with what was going on, as text.</summary>
    public static string CrashesText(IReadOnlyList<CrashStory> stories, string period, DateTime made, string version)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Lang.T("RigSight crashes"));
        sb.AppendLine(stories.Count == 0 ? Lang.T($"{period}, none recorded.") : Lang.T($"{period}, {stories.Count} recorded, latest first."));
        foreach (var s in stories.OrderByDescending(s => s.Crash.Ts))
        {
            var said = CrashExplainer.Explain(s.Crash, s.App);
            sb.AppendLine();
            sb.AppendLine(said.Title);
            sb.AppendLine(TimeUtil.FromUnix(s.Crash.Ts).ToString(TimeText.Fit("dddd, d MMMM yyyy, h:mm tt")));
            var rows = new List<(string Label, string? Value)>(s.Found());
            if (s.Context is { NoReadings: false } c)
            {
                if (c.CpuBefore is not null || c.GpuBefore is not null)
                    rows.Add((Lang.T("Just before"), JustBefore(c)));
                if (c.SessionSec is double sec && sec >= 60) rows.Add((Lang.T("Had been running"), Units.Duration(sec)));
            }
            else if (s.Context is { NoReadings: true }) rows.Add((Lang.T("Just before"), Wording.NotRecorded));
            rows.Add((Lang.T("In front"), s.AppInFront));
            if (s.ChangesBefore.Count > 0) rows.Add((Lang.T("Changed in the 7 days before"), s.Changed));
            rows.Add((Lang.T("Module"), s.Crash.Module));
            rows.Add((Lang.T("Code"), s.Crash.Code));
            var shown = rows.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToList();
            int label = shown.Count == 0 ? 0 : shown.Max(x => x.Label.Length);
            foreach (var (l, v) in shown) sb.AppendLine($"  {l.PadRight(label)}  {v}");
        }
        return sb.AppendLine().AppendLine(Foot(made, version)).ToString();
    }

    /// <summary>"CPU up to 81°, GPU up to 76°": the readings in the minutes before a crash.</summary>
    private static string JustBefore(CrashContext c) =>
        string.Join(", ", new[] { c.CpuBefore is double cpu ? Lang.T($"CPU up to {Units.TempShort(cpu)}") : null, c.GpuBefore is double gpu ? Lang.T($"GPU up to {Units.TempShort(gpu)}") : null }.Where(x => x is not null));

    // ── This PC, right now ──

    /// <param name="pc">The PC in a few lines (CPU, graphics card, memory, Windows).</param>
    /// <param name="notes">Lines about the drives and anything else worth a line each.</param>
    public static string Snapshot(DateTime at, string version, string pc, IEnumerable<SnapshotReading> readings, IEnumerable<string> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Lang.T("RigSight PC snapshot"));
        sb.AppendLine($"{at.ToString(TimeText.Fit("dddd, d MMMM yyyy, h:mm tt"), CultureInfo.InvariantCulture)}  ·  RigSight {version}");
        sb.AppendLine();
        sb.AppendLine(Lang.T("THIS PC"));
        foreach (var line in pc.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) sb.AppendLine("  " + line);
        var extra = notes.ToList();
        if (extra.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Lang.T("DRIVES"));
            foreach (var line in extra) sb.AppendLine("  " + line);
        }
        var all = readings.ToList();
        if (all.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine(Lang.T("No sensor readings because the background agent wasn't running."));
            return sb.ToString();
        }
        sb.AppendLine();
        sb.AppendLine(Lang.T("READINGS  (now, then the lowest and highest since RigSight started)"));
        int name = Math.Min(44, all.Max(r => r.Name.Length)), now = all.Max(r => r.Now.Length), low = all.Max(r => r.Lowest.Length);
        foreach (var part in all.GroupBy(r => r.Part))
        {
            sb.AppendLine();
            sb.AppendLine(part.Key);
            foreach (var kind in part.GroupBy(r => r.Kind))
            {
                sb.AppendLine("  " + kind.Key);
                foreach (var r in kind)
                    sb.AppendLine(("    " + Lang.T($"{r.Name.PadRight(name)}  {r.Now.PadLeft(now)}    {r.Lowest.PadLeft(low)}  to  {r.Highest}")).TrimEnd());
            }
        }
        return sb.ToString();
    }

    // ── A period, as a page ──

    private const string Style = """
        :root{color-scheme:light dark;--bg:#fff;--fg:#111;--muted:#5f6368;--line:#e3e3e3;--card:#f6f6f6;--cpu:#3b6fe0;--gpu:#0e9fb5}
        @media (prefers-color-scheme:dark){:root{--bg:#0b0b0b;--fg:#f2f2f2;--muted:#a3a3a3;--line:#242424;--card:#151515;--cpu:#5b8cff;--gpu:#22d3ee}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
        main{max-width:920px;margin:0 auto;padding:32px 20px 56px}h1{font-size:26px;margin:0}h2{font-size:16px;margin:34px 0 10px}
        .sub{color:var(--muted)}.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px;margin-top:20px}
        .tile{background:var(--card);border-radius:10px;padding:12px 14px}.tile b{display:block;font-size:22px;font-weight:600}
        .tile span{font-size:12px;color:var(--muted)}table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}
        th{text-align:right;font-size:11px;letter-spacing:.05em;color:var(--muted);font-weight:600;padding:6px 8px;border-bottom:1px solid var(--line)}
        td{text-align:right;padding:7px 8px;border-bottom:1px solid var(--line)}th:first-child,td:first-child{text-align:left;padding-left:0}
        .bar{height:5px;border-radius:3px;background:var(--cpu);margin-top:4px}ul{margin:0;padding-left:18px}li{margin:5px 0}
        .scroll{overflow-x:auto}svg{display:block;width:100%;height:auto}.legend{font-size:12px;color:var(--muted);margin-top:4px}
        .legend i{display:inline-block;width:9px;height:9px;border-radius:50%;margin:0 6px 0 14px}.legend i:first-child{margin-left:0}
        .crash{border:1px solid var(--line);border-radius:10px;padding:14px 16px;margin:12px 0}.crash h3{margin:0 0 2px;font-size:15px}
        .crash dl{display:grid;grid-template-columns:max-content 1fr;gap:3px 16px;margin:10px 0 0}.crash dt{color:var(--muted)}.crash dd{margin:0}
        footer{margin-top:40px;font-size:12px;color:var(--muted)}@media print{main{padding:0}}
        """;

    private static string H(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static StringBuilder Page(string title, string subtitle)
    {
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang=\"{Lang.Code}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>{H(title)}</title><style>{Style}</style></head><body><main>");
        sb.Append($"<h1>{H(title)}</h1><div class=\"sub\">{H(subtitle)}</div>");
        return sb;
    }

    private static string End(StringBuilder sb, DateTime made, string version)
    {
        sb.Append($"<footer>{H(Foot(made, version))}</footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string PeakLine(Peak? p) => p is null ? "" : p.App is null ? p.Time.ToString(TimeText.Fit("d MMM, h:mm tt")) : $"{TimeText.At(p.Time, "d MMM, h:mm tt")} · {p.App}";

    /// <summary>The period as the Reports page tells it: totals, peaks, what stood out, temperatures drawn, every app, the crashes.</summary>
    public static string Report(Report r, string title, string version)
    {
        var sb = Page(title, r.HasData ? Lang.T($"Active {Units.Duration(r.ActiveSec)}, PC on for {Units.Duration(r.OnSec)}") : Lang.T("Nothing was recorded in this time."));
        if (!r.HasData) return End(sb, r.BuiltAt, version);

        sb.Append("<div class=\"tiles\">");
        void Tile(string label, string value, string note = "") => sb.Append($"<div class=\"tile\"><span>{H(label)}</span><b>{H(value)}</b><span>{H(note)}</span></div>");
        Tile(Lang.T("ACTIVE"), Units.Duration(r.ActiveSec));
        Tile(Lang.T("PC ON"), Units.Duration(r.OnSec));
        if (r.GamingSec >= 60) Tile(Lang.T("IN GAMES"), Units.Duration(r.GamingSec));
        if (r.CpuTempPeak is { } cpu) Tile(Lang.T("CPU PEAK"), Units.TempShort(cpu.Value), PeakLine(cpu));
        if (r.GpuTempPeak is { } gpu) Tile(Lang.T("GPU PEAK"), Units.TempShort(gpu.Value), PeakLine(gpu));
        if (r.Net is { } net && net.Down + net.Up > 0) Tile(Lang.T("INTERNET"), Units.Data(net.Down), Lang.T($"{Units.Data(net.Up)} uploaded"));
        sb.Append("</div>");

        if (r.Insights.Count > 0)
        {
            sb.Append($"<h2>{H(Lang.T("What stood out"))}</h2><ul>");
            foreach (var i in r.Insights) sb.Append($"<li>{H(i.Text)}</li>");
            sb.Append("</ul>");
        }
        if (PcUsual.CardRows(r) is { Count: > 0 } usual)
        {
            sb.Append($"<h2>{H(Lang.T("Against your usual"))}</h2><table><tr><th>{H(Lang.T("What"))}</th><th>{H(Lang.T("This period"))}</th><th>{H(Lang.T("Your usual"))}</th><th>{H(Lang.T("What it means"))}</th></tr>");
            foreach (var u in usual)
                sb.Append($"<tr><td>{H(u.Label)}</td><td>{H(u.Now)}</td><td>{H(u.Usual)} <span style=\"color:var(--muted)\">{H(u.UsualDetail)}</span></td><td>{H(u.MeansDetail is null ? u.Means : $"{u.Means}: {u.MeansDetail}")}</td></tr>");
            sb.Append("</table>");
        }

        if (TempChart(r.Temps) is { } chart)
        {
            sb.Append($"<h2>{H(Lang.T("Temperatures"))}</h2>").Append(chart);
            sb.Append("<div class=\"legend\"><i style=\"background:var(--cpu)\"></i>CPU<i style=\"background:var(--gpu)\"></i>GPU</div>");
        }

        var apps = r.Apps.Where(a => a.ActiveSec >= 60).ToList();
        if (apps.Count > 0)
        {
            double top = Math.Max(1, apps.Max(a => a.ActiveSec));
            sb.Append($"<h2>{H(Lang.T("Apps you used"))}</h2><div class=\"scroll\"><table><thead><tr><th>{H(Lang.T("APP"))}</th><th>{H(Lang.T("IN USE"))}</th><th>{H(Lang.T("BACKGROUND"))}</th><th>{H(Lang.T("CPU PEAK"))}</th><th>{H(Lang.T("GPU PEAK"))}</th><th>{H(Lang.T("MEMORY PEAK"))}</th></tr></thead><tbody>");
            foreach (var a in apps)
                sb.Append($"<tr><td>{H(a.Name)} <span class=\"sub\">· {H(AppCatalog.Label(a.Category))}</span><div class=\"bar\" style=\"width:{Math.Max(1, a.ActiveSec / top * 100).ToString("0", CultureInfo.InvariantCulture)}%\"></div></td>")
                    .Append($"<td>{H(Units.Duration(a.ActiveSec))}</td><td>{H(a.BackgroundSec > 0 ? Units.Duration(a.BackgroundSec) : "–")}</td>")
                    .Append($"<td>{H(Units.TempShort(a.CpuTempMax))}</td><td>{H(Units.TempShort(a.GpuTempMax))}</td><td>{H(a.MemMax is double m ? Units.Megabytes(m) : "–")}</td></tr>");
            sb.Append("</tbody></table></div>");
        }

        if (r.Crashes.Count > 0)
        {
            sb.Append($"<h2>{H(Lang.T("Crashes and sudden shut-offs"))}</h2><ul>");
            foreach (var c in r.Crashes.OrderBy(c => c.Ts))
                sb.Append($"<li>{H(TimeUtil.FromUnix(c.Ts).ToString(TimeText.Fit("d MMM, h:mm tt")))}: {H(CrashExplainer.Explain(c).Title)}</li>");
            sb.Append("</ul>");
        }
        return End(sb, r.BuiltAt, version);
    }

    /// <summary>The period's CPU and GPU temperatures as a drawing (null with too little to draw).</summary>
    internal static string? TempChart(IReadOnlyList<TempPoint> points)
    {
        var have = points.Where(p => p.Cpu is not null || p.Gpu is not null).ToList();
        if (have.Count < 3) return null;
        const double W = 900, Ht = 220, L = 34, B = 20, T = 8;
        double lo = Math.Floor(have.Min(p => Math.Min(p.Cpu ?? 999, p.Gpu ?? 999)) / 10) * 10;
        double hi = Math.Ceiling(have.Max(p => Math.Max(p.Cpu ?? 0, p.Gpu ?? 0)) / 10) * 10;
        if (hi - lo < 10) hi = lo + 10;
        long t0 = have[0].Time.Ticks, t1 = Math.Max(have[^1].Time.Ticks, t0 + 1);
        var inv = CultureInfo.InvariantCulture;
        double X(DateTime t) => L + (W - L) * (t.Ticks - t0) / (double)(t1 - t0);
        double Y(double v) => T + (Ht - T - B) * (1 - (v - lo) / (hi - lo));
        // A stretch with nothing recorded breaks the line instead of being drawn across.
        var typical = have.Zip(have.Skip(1), (a, b) => (b.Time - a.Time).Ticks).OrderBy(x => x).ElementAt(have.Count / 2);
        string Line(Func<TempPoint, double?> of)
        {
            var d = new StringBuilder();
            DateTime? last = null;
            foreach (var p in have)
            {
                if (of(p) is not double v) { last = null; continue; }
                bool join = last is { } l && (p.Time - l).Ticks <= typical * 3;
                d.Append(join ? 'L' : 'M').Append(X(p.Time).ToString("0.#", inv)).Append(' ').Append(Y(v).ToString("0.#", inv));
                last = p.Time;
            }
            return d.ToString();
        }
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {W} {Ht}\" role=\"img\" aria-label=\"{H(Lang.T("CPU and GPU temperature over the period"))}\">");
        for (double v = lo; v <= hi + 0.1; v += (hi - lo) / 4)
        {
            string y = Y(v).ToString("0.#", inv);
            sb.Append($"<line x1=\"{L}\" x2=\"{W}\" y1=\"{y}\" y2=\"{y}\" stroke=\"var(--line)\"/>");
            sb.Append($"<text x=\"0\" y=\"{y}\" dy=\"4\" font-size=\"11\" fill=\"var(--muted)\">{H(Units.TempShort(v))}</text>");
        }
        bool days = have[^1].Time - have[0].Time > TimeSpan.FromHours(36);
        sb.Append($"<text x=\"{L}\" y=\"{Ht - 4}\" font-size=\"11\" fill=\"var(--muted)\">{H(have[0].Time.ToString(days ? "d MMM" : TimeText.Fit("h:mm tt")))}</text>");
        sb.Append($"<text x=\"{W}\" y=\"{Ht - 4}\" font-size=\"11\" fill=\"var(--muted)\" text-anchor=\"end\">{H(have[^1].Time.ToString(days ? "d MMM" : TimeText.Fit("h:mm tt")))}</text>");
        sb.Append($"<path d=\"{Line(p => p.Cpu)}\" fill=\"none\" stroke=\"var(--cpu)\" stroke-width=\"1.6\"/>");
        sb.Append($"<path d=\"{Line(p => p.Gpu)}\" fill=\"none\" stroke=\"var(--gpu)\" stroke-width=\"1.6\"/>");
        return sb.Append("</svg>").ToString();
    }

    // ── The crashes, each with what was going on ──

    public static string Crashes(IReadOnlyList<CrashStory> stories, string period, DateTime made, string version)
    {
        var sb = Page(Lang.T("Crashes"), stories.Count == 0 ? Lang.T($"{period}, none recorded.") : Lang.T($"{period}, {stories.Count} recorded, latest first."));
        foreach (var s in stories.OrderByDescending(s => s.Crash.Ts))
        {
            var said = CrashExplainer.Explain(s.Crash, s.App);
            sb.Append($"<div class=\"crash\"><h3>{H(said.Title)}</h3><div class=\"sub\">{H(TimeUtil.FromUnix(s.Crash.Ts).ToString(TimeText.Fit("dddd, d MMMM yyyy, h:mm tt")))}</div><dl>");
            void Row(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) sb.Append($"<dt>{H(label)}</dt><dd>{H(value)}</dd>"); }
            foreach (var (label, value) in s.Found()) Row(label, value);
            if (s.Context is { NoReadings: false } c)
            {
                if (c.CpuBefore is not null || c.GpuBefore is not null)
                    Row(Lang.T("Just before"), JustBefore(c));
                if (c.SessionSec is double sec && sec >= 60) Row(Lang.T("Had been running"), Units.Duration(sec));
            }
            else if (s.Context is { NoReadings: true }) Row(Lang.T("Just before"), Wording.NotRecorded);
            Row(Lang.T("In front"), s.AppInFront);
            if (s.ChangesBefore.Count > 0) Row(Lang.T("Changed in the 7 days before"), s.Changed);
            Row(Lang.T("Module"), s.Crash.Module);
            Row(Lang.T("Code"), s.Crash.Code);
            sb.Append("</dl></div>");
        }
        return End(sb, made, version);
    }
}
