using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Rigsight.Core.Data;

namespace Rigsight.Core.Stability;

/// <summary>
/// Reads crashes from the Windows event logs (the same records Event Viewer shows, which are
/// tedious to dig through by hand). Only a handful of well-known event IDs are queried.
/// </summary>
public static partial class CrashLogReader
{
    /// <summary>An app crashing or freezing, as Windows logs it (public for the agent's watch on it).</summary>
    public const string AppQuery =
        "*[System[(EventID=1000 and Provider[@Name='Application Error']) or (EventID=1002 and Provider[@Name='Application Hang'])]]";

    private const string SystemQuery =
        "*[System[(EventID=41 and Provider[@Name='Microsoft-Windows-Kernel-Power']) or " +
        "(EventID=6008 and Provider[@Name='EventLog']) or (EventID=4101 and Provider[@Name='Display'])]]";

    private const string PowerQuery =
        "*[System[(EventID=12 and Provider[@Name='Microsoft-Windows-Kernel-General']) or " +
        "((EventID=41 or EventID=42 or EventID=107) and Provider[@Name='Microsoft-Windows-Kernel-Power']) or " +
        "(EventID=1 and Provider[@Name='Microsoft-Windows-Power-Troubleshooter'])]]";

    /// <summary>
    /// When the PC started, went to sleep and woke since <paramref name="since"/>, oldest first, with what Windows noted
    /// at a start about how it had gone down (see <see cref="PowerLog"/>). Null when the log couldn't be read.
    /// </summary>
    public static List<PowerEvent>? ReadPower(DateTime since)
    {
        try
        {
            var events = new List<PowerEvent>();
            foreach (var record in Query("System", PowerQuery, since))
            {
                using (record)
                {
                    if (record.TimeCreated is not { } time) continue;
                    PowerEventKind? kind = record.Id switch
                    {
                        12 => PowerEventKind.Started,
                        41 => uint.TryParse(Named(record).GetValueOrDefault("BugcheckCode"), out var bugcheck) && bugcheck != 0 ? PowerEventKind.Crashed : PowerEventKind.ShutOff,
                        42 => PowerEventKind.Sleep,
                        107 or 1 => PowerEventKind.Wake,
                        _ => null,
                    };
                    if (kind is { } k) events.Add(new PowerEvent(time, k));
                }
            }
            return [.. events.OrderBy(e => e.Time)];
        }
        catch (Exception ex)
        {
            Log.Error("crashes", ex);
            return null;
        }
    }

    /// <summary>
    /// All crashes logged since <paramref name="since"/> (oldest first); with <paramref name="systemOnly"/>, without
    /// the apps' (the PC going down and graphics driver resets alone).
    /// </summary>
    public static List<CrashEvent> ReadSince(DateTime since, bool systemOnly = false)
    {
        var events = new List<CrashEvent>();
        var shutdownTimes = new List<(DateTime Logged, DateTime Happened)>();
        var kernelPower = new List<(DateTime Logged, uint Bugcheck, PowerMoment Moment, CrashFacts Facts)>();

        foreach (var record in systemOnly ? [] : Query("Application", AppQuery, since))
        {
            using (record)
            {
                if (ParseApp(record) is { } e) events.Add(e);
            }
        }
        if (!systemOnly && events.Count > 0) AddReports(events, since);
        var named = new List<(DateTime Logged, string Driver)>();
        var bugchecks = new List<(DateTime Logged, string[] Params)>();

        foreach (var record in Query("System", SystemQuery, since))
        {
            using (record)
            {
                var logged = record.TimeCreated ?? DateTime.Now;
                switch (record.Id)
                {
                    case 4101:
                        events.Add(new CrashEvent
                        {
                            Ts = TimeUtil.ToUnix(logged),
                            Kind = CrashKind.GpuDriverReset,
                            Module = Prop(record, 0),
                            Detail = "Display driver stopped responding and has recovered",
                        });
                        break;
                    case 6008:
                        if (ParseShutdownTime(record) is { } read && ShutdownBefore(read, logged) is { } happened) shutdownTimes.Add((logged, happened));
                        break;
                    case 41:
                        var data = Named(record);
                        uint.TryParse(data.GetValueOrDefault("BugcheckCode"), out var bugcheck);
                        kernelPower.Add((logged, bugcheck, MomentOf(data), PowerFacts(data, bugcheck)));
                        break;
                }
            }
        }

        // Kernel-Power 41 is logged at the *next* boot; event 6008 says when the PC went down, as far as Windows knows:
        // the last time it happened to note while running, which can be half an hour before (see CrashEvent.NextStart).
        if (kernelPower.Any(k => k.Bugcheck != 0))
        {
            foreach (var record in Query("System", BugcheckQuery, since))
            {
                using (record)
                {
                    var logged = record.TimeCreated ?? DateTime.Now;
                    if (record.Id == 1019 && Text(Prop(record, 0)) is { } driver) named.Add((logged, driver));
                    else if (record.Id == 1001 && BugParams(Prop(record, 0)) is { } p) bugchecks.Add((logged, p));
                }
            }
        }
        if (!systemOnly)
            events.AddRange(ResetsFrom(ReadHangs(since), [.. kernelPower.Select(k => k.Logged)],
                [.. events.Where(e => e.Kind == CrashKind.GpuDriverReset).Select(e => e.Time)]));
        foreach (var (logged, bugcheck, moment, facts) in kernelPower)
        {
            facts.NextStart = TimeUtil.ToUnix(logged);
            if (bugcheck != 0)
            {
                // Windows writes the crash up a few seconds to a few minutes into the next start.
                bool Near(DateTime t) => Math.Abs((t - logged).TotalMinutes) < 10;
                // The one written nearest this start: two blue screens in a row each have their own.
                if (named.Where(n => Near(n.Logged)).OrderBy(n => Math.Abs((n.Logged - logged).TotalSeconds)).Select(n => n.Driver).FirstOrDefault() is { } driver)
                    (facts.Driver, facts.DriverName) = (driver, DriverFiles.Describe(driver));
                facts.BugParams ??= bugchecks.Where(b => Near(b.Logged)).OrderBy(b => Math.Abs((b.Logged - logged).TotalSeconds)).Select(b => b.Params).FirstOrDefault();
            }
            var match = shutdownTimes.Where(s => Math.Abs((s.Logged - logged).TotalMinutes) < 5).Select(s => (DateTime?)s.Happened).FirstOrDefault();
            events.Add(new CrashEvent
            {
                Ts = TimeUtil.ToUnix(match ?? logged),
                Kind = bugcheck != 0 ? CrashKind.SystemCrash : CrashKind.UnexpectedShutdown,
                AppExe = "",
                Code = bugcheck != 0 ? $"0x{bugcheck:X}" : null,
                Moment = moment,
                NextStart = TimeUtil.ToUnix(logged),
                Facts = facts,
                Detail = match is null ? $"Noticed at startup {logged:g}" : $"Found at next startup ({logged:g})",
            });
        }

        return [.. events.OrderBy(e => e.Ts)];
    }

    /// <summary>
    /// What Windows was doing when the PC went down, from Kernel-Power 41's SleepInProgress: the power state it was
    /// heading into (2–4 sleep, 5 hibernate, 6 off). Hibernate counts as shutting down: Fast Startup hibernates on
    /// every shutdown. SystemSleepTransitionsToOn only counts wake-ups since the PC started, so it says nothing here.
    /// </summary>
    internal static PowerMoment MomentOf(IReadOnlyDictionary<string, string> data)
    {
        if (string.Equals(data.GetValueOrDefault("ConnectedStandbyInProgress"), "true", StringComparison.OrdinalIgnoreCase))
            return PowerMoment.Asleep;
        return int.TryParse(data.GetValueOrDefault("SleepInProgress"), out var state) ? state switch
        {
            >= 2 and <= 4 => PowerMoment.Asleep,
            5 or 6 => PowerMoment.ShuttingDown,
            _ => PowerMoment.Running,
        } : PowerMoment.Running;
    }

    /// <summary>
    /// The graphics driver resets since <paramref name="since"/> on their own (see <see cref="ResetsFrom"/>), for a
    /// history whose earlier scans didn't look for them: with the sudden restarts and the usual reset entries of that
    /// stretch, so a hang that took the PC down, or one counted already, isn't added.
    /// </summary>
    public static List<CrashEvent> ReadResets(DateTime since)
    {
        var starts = new List<DateTime>();
        var display = new List<DateTime>();
        foreach (var record in Query("System", SystemQuery, since))
        {
            using (record)
            {
                if (record.TimeCreated is not { } logged) continue;
                if (record.Id == 41) starts.Add(logged);
                else if (record.Id == 4101) display.Add(logged);
            }
        }
        return ResetsFrom(ReadHangs(since), starts, display);
    }

    /// <summary>One of Windows' records of a graphics hang (an error report named LiveKernelEvent): when it was written, the
    /// address its records share, its bucket (which names the driver, in the fuller record) and its code (141 or 117).</summary>
    internal sealed record HangRecord(DateTime Logged, string Key, string? Bucket, string Code);

    /// <summary>Windows' graphics-hang records since <paramref name="since"/>, oldest first.</summary>
    internal static List<HangRecord> ReadHangs(DateTime since)
    {
        var list = new List<HangRecord>();
        try
        {
            foreach (var record in Query("Application", ReportQuery, since, "EventData[Data='LiveKernelEvent']"))
            {
                using (record)
                {
                    if (record.TimeCreated is not { } time || !string.Equals(Prop(record, 2), "LiveKernelEvent", StringComparison.OrdinalIgnoreCase)) continue;
                    // 141 and 117: the graphics card took too long to answer and Windows recovered it.
                    if (Text(Prop(record, 5)) is not ("141" or "117") || Text(Prop(record, 5)) is not { } code) continue;
                    list.Add(new HangRecord(time, Text(Prop(record, 6)) ?? time.Ticks.ToString(), Text(Prop(record, 0)), code));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("crashes", ex);
        }
        return [.. list.OrderBy(h => h.Logged)];
    }

    /// <summary>
    /// The graphics driver resets in Windows' hang records: one per hang (its records share an address), at the first
    /// record's time, which on the PC this was worked out on came 0 to 2 seconds after the hang itself (once, 1 min 42 s).
    /// Left out: a hang followed within ten minutes by a sudden restart, or written up just after one (<paramref name="suddenStarts"/>,
    /// Kernel-Power 41): it took the PC down and is said on that shut-off instead. And one Windows also logged the usual way (Display 4101).
    /// </summary>
    internal static List<CrashEvent> ResetsFrom(IReadOnlyList<HangRecord> records, IReadOnlyList<DateTime> suddenStarts, IReadOnlyList<DateTime> displayResets)
    {
        var list = new List<CrashEvent>();
        foreach (var hang in records.GroupBy(r => r.Key))
        {
            var first = hang.MinBy(r => r.Logged)!;
            // A sudden restart soon after it (the PC went down with the card) or a write-up just after one: that shut-off's.
            if (suddenStarts.Any(s => first.Logged >= s.AddMinutes(-10) && first.Logged <= s.AddMinutes(15))) continue;
            if (displayResets.Any(d => Math.Abs((d - first.Logged).TotalSeconds) <= 120)) continue;
            string? driver = hang.Select(r => DriverOf(r.Bucket)).FirstOrDefault(d => d is not null);
            list.Add(new CrashEvent
            {
                Ts = TimeUtil.ToUnix(first.Logged),
                Kind = CrashKind.GpuDriverReset,
                AppExe = "",
                Module = driver,
                Code = first.Code,
                Detail = $"Windows reported that the graphics card stopped answering (LiveKernelEvent {first.Code})",
                Facts = new CrashFacts { Driver = driver, DriverName = driver is null ? null : DriverFiles.Describe(driver), ReportType = $"LiveKernelEvent {first.Code}", HangKey = hang.Key },
            });
        }
        return list;
    }

    /// <summary>The driver a hang report's bucket names ("LKD_0x141_Tdr:6_IMAGE_nvlddmkm.sys_Ampere" gives "nvlddmkm.sys").</summary>
    internal static string? DriverOf(string? bucket)
    {
        if (bucket is null) return null;
        int at = bucket.IndexOf("IMAGE_", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var rest = bucket[(at + 6)..];
        int end = rest.IndexOf(".sys", StringComparison.OrdinalIgnoreCase);
        return end > 0 ? rest[..(end + 4)] : null;
    }

    private const string DumpQuery = "*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001]]";

    /// <summary>
    /// Blue-screen dump files Windows saved (logged at the next startup): when it was logged, the bugcheck code and the file.
    /// The files themselves are admin-only, but where they are is in the (readable) System log.
    /// </summary>
    public static List<(DateTime Logged, uint Code, string Path)> ReadDumps(DateTime since)
    {
        var list = new List<(DateTime, uint, string)>();
        foreach (var record in Query("System", DumpQuery, since))
        {
            using (record)
            {
                var code = Prop(record, 0)?.Split(' ')[0]; // "0x00000116 (0x…, …)"
                var path = Prop(record, 1);
                if (string.IsNullOrWhiteSpace(path) || code is null) continue;
                var hex = code.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? code[2..] : code;
                if (!uint.TryParse(hex, NumberStyles.HexNumber, null, out var value)) continue;
                list.Add((record.TimeCreated ?? since, value, path.Trim()));
            }
        }
        return list;
    }

    /// <summary>Did <paramref name="exe"/> crash or hang in the last few minutes? Used to soften session summaries.</summary>
    public static CrashEvent? RecentAppCrash(string exe, TimeSpan window)
    {
        foreach (var record in Query("Application", AppQuery, DateTime.Now - window))
        {
            using (record)
            {
                if (ParseApp(record) is { } e && e.AppExe.Equals(exe, StringComparison.OrdinalIgnoreCase)) return e;
            }
        }
        return null;
    }

    private static CrashEvent? ParseApp(EventRecord record)
    {
        var exe = Prop(record, 0);
        if (string.IsNullOrEmpty(exe)) return null;
        var time = TimeUtil.ToUnix(record.TimeCreated ?? DateTime.Now);

        if (record.Id == 1000)
        {
            return new CrashEvent
            {
                Ts = time,
                Kind = CrashKind.AppCrash,
                AppExe = exe,
                Module = Prop(record, 3),
                Code = Prop(record, 6)?.ToLowerInvariant(),
                AppPath = Prop(record, 10),
                Facts = new CrashFacts { AppVersion = Text(Prop(record, 1)), ModuleVersion = Text(Prop(record, 4)), Offset = Text(Prop(record, 7)?.TrimStart('0')), RanSec = RanFor(Prop(record, 9), record.TimeCreated) },
                ReportId = Text(Prop(record, 12)),
            };
        }

        string? path = record.Properties.Select(p => p.Value?.ToString()).FirstOrDefault(v => v is not null && v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && v.Contains('\\'));
        return new CrashEvent
        {
            Ts = time, Kind = CrashKind.AppHang, AppExe = exe, AppPath = path,
            Facts = new CrashFacts { AppVersion = Text(Prop(record, 1)), RanSec = RanFor(Prop(record, 3), record.TimeCreated) },
            ReportId = Text(Prop(record, 6)),
        };
    }

    private static string? Text(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// How long a program had run when Windows logged its failure, from the start time in the entry: a Windows file
    /// time, written in hexadecimal ("01dc3a…") or as a plain number depending on the Windows version. Null when it
    /// isn't there or makes no sense (in the future, or more than a year before).
    /// </summary>
    internal static double? RanFor(string? started, DateTime? logged)
    {
        if (Text(started) is not { } s || logged is not { } at) return null;
        long ticks;
        if (s.Length >= 17 && s.All(char.IsAsciiDigit)) { if (!long.TryParse(s, out ticks)) return null; }
        else if (!long.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, System.Globalization.NumberStyles.HexNumber, null, out ticks)) return null;
        if (ticks <= 0 || ticks >= DateTime.MaxValue.ToFileTimeUtc()) return null;
        double sec = (at.ToUniversalTime() - DateTime.FromFileTimeUtc(ticks)).TotalSeconds;
        return sec is >= 0 and < 366 * 86400.0 ? Math.Round(sec) : null;
    }

    private const string ReportQuery = "*[System[Provider[@Name='Windows Error Reporting'] and EventID=1001]]";
    private const string DotNetQuery = "*[System[Provider[@Name='.NET Runtime'] and EventID=1026]]";
    private const string BugcheckQuery = "*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001 or EventID=1019)]]";

    /// <summary>
    /// Adds what Windows Error Reporting and the .NET runtime wrote about the same crashes: the report's kind, what a
    /// frozen app was waiting on, a .NET app's error. A report is matched by the id the crash's own entry carries.
    /// </summary>
    private static void AddReports(List<CrashEvent> events, DateTime since)
    {
        var byReport = events.Where(e => e.ReportId is not null).GroupBy(e => e.ReportId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        try
        {
            if (byReport.Count > 0)
            {
                foreach (var record in Query("Application", ReportQuery, since))
                {
                    using (record)
                    {
                        if (Prop(record, 19) is { } id && byReport.TryGetValue(id, out var e))
                            AddReport(e, Prop(record, 2), Prop(record, 10), Prop(record, 11));
                    }
                }
            }
            var crashes = events.Where(e => e.Kind == CrashKind.AppCrash).ToList();
            if (crashes.Count == 0) return;
            foreach (var record in Query("Application", DotNetQuery, since))
            {
                using (record)
                {
                    if (record.TimeCreated is not { } time || DotNetError(Prop(record, 0)) is not { } error) continue;
                    long ts = TimeUtil.ToUnix(time);
                    // The runtime writes its entry in the second or two before Windows writes the crash's.
                    var e = crashes.FirstOrDefault(c => Math.Abs(c.Ts - ts) <= 10 && c.AppExe.Equals(error.App, StringComparison.OrdinalIgnoreCase));
                    if (e is null) continue;
                    var facts = e.Facts ??= new CrashFacts();
                    (facts.Error, facts.ErrorAt) = (error.Error ?? facts.Error, error.At ?? facts.ErrorAt);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("crashes", ex); // the crashes themselves are still worth having
        }
    }

    /// <summary>
    /// A Windows Error Reporting entry for a crash: its kind ([2]), and for a hang that was a wait on another program
    /// ("AppHangXProcB1") that program and its version (the report's sixth and seventh parameters).
    /// </summary>
    internal static void AddReport(CrashEvent e, string? type, string? sixth, string? seventh)
    {
        if (Text(type) is not { } kind) return;
        var facts = e.Facts ??= new CrashFacts();
        facts.ReportType = kind;
        if (kind.StartsWith("AppHangXProc", StringComparison.OrdinalIgnoreCase) && Text(sixth) is { } other
            && other.Contains('.') && !other.Equals(e.AppExe, StringComparison.OrdinalIgnoreCase))
            (facts.WaitedOn, facts.WaitedOnVersion) = (other, Text(seventh));
    }

    /// <summary>
    /// From the .NET runtime's entry for a program it had to end: which program, the error's type where it names one
    /// ("Exception Info: System.IO.FileNotFoundException: …") and the first place in the stack.
    /// </summary>
    internal static (string App, string? Error, string? At)? DotNetError(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        string? app = null, error = null, at = null;
        foreach (var raw in message.Split('\n'))
        {
            var line = raw.Trim();
            if (app is null && line.StartsWith("Application:", StringComparison.OrdinalIgnoreCase)) app = line["Application:".Length..].Trim();
            else if (error is null && line.StartsWith("Exception Info:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = line["Exception Info:".Length..].Trim();
                int colon = rest.IndexOf(':');
                error = (colon > 0 ? rest[..colon] : rest).Trim();
                if (error.Length == 0 || error.Contains(' ')) error = null;
            }
            else if (at is null && line.StartsWith("at ", StringComparison.Ordinal))
            {
                at = line[3..].Trim();
                int paren = at.IndexOf('(');
                if (paren > 0) at = at[..paren];
            }
        }
        return string.IsNullOrEmpty(app) ? null : (app, error, at);
    }

    /// <summary>What Kernel-Power 41 says beside the crash code: a held power button, hardware errors handed over at the start, the code's four numbers.</summary>
    internal static CrashFacts PowerFacts(IReadOnlyDictionary<string, string> data, uint bugcheck)
    {
        var facts = new CrashFacts();
        // Present on Windows 10 and later; without the field nothing is claimed either way.
        if (data.TryGetValue("PowerButtonTimestamp", out var stamp))
            facts.PowerButton = stamp.Trim().TrimStart('0', 'x', 'X').Length > 0
                                || string.Equals(data.GetValueOrDefault("LongPowerButtonPressDetected"), "true", StringComparison.OrdinalIgnoreCase);
        if (int.TryParse(data.GetValueOrDefault("WHEABootErrorCount"), out int hardware)) facts.HardwareErrors = hardware;
        if (bugcheck != 0)
        {
            var p = new[] { 1, 2, 3, 4 }.Select(i => data.GetValueOrDefault($"BugcheckParameter{i}")).ToArray();
            if (p.All(v => !string.IsNullOrEmpty(v))) facts.BugParams = [.. p.Select(v => Hex(v!))];
        }
        return facts;
    }

    /// <summary>"0x0000003b (0x00000000c0000005, 0x…, 0x…, 0x…)": the four numbers, shortened.</summary>
    internal static string[]? BugParams(string? text)
    {
        if (text is null) return null;
        int open = text.IndexOf('('), close = text.IndexOf(')');
        if (open < 0 || close < open) return null;
        var parts = text[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4 ? [.. parts.Select(Hex)] : null;
    }

    private static string Hex(string v)
    {
        v = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? v[2..] : v;
        v = v.TrimStart('0').ToLowerInvariant();
        return v.Length == 0 ? "0" : v;
    }

    // ── Signs ───────────────────────────────────────────────────────────

    private const string SignQuery =
        "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger' or @Name='disk' or @Name='Ntfs' or @Name='stornvme' or @Name='nvlddmkm' or @Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and (Level=1 or Level=2 or Level=3)]]";

    /// <summary>
    /// Trouble Windows logged since <paramref name="since"/> that isn't a crash but can explain one: hardware errors,
    /// drive errors, faults the graphics driver reported, memory running out, and reports of the graphics card not
    /// answering. Several of a kind in the same minute are one, with a count.
    /// </summary>
    public static List<CrashSign> ReadSigns(DateTime since)
    {
        var found = new List<(long Minute, string Kind, string Detail)>();
        try
        {
            foreach (var record in Query("System", SignQuery, since))
            {
                using (record)
                {
                    if (record.TimeCreated is not { } time || SignOf(record.ProviderName, record.Id) is not { } sign) continue;
                    found.Add((TimeUtil.ToUnix(time) / 60 * 60, sign.Kind, sign.Detail));
                }
            }
            // Windows picks these out itself: every error report of the period used to be read here to find a handful.
            var hangs = ReadHangs(since);
            foreach (var h in hangs)
                found.Add((TimeUtil.ToUnix(h.Logged) / 60 * 60, SignKind.GraphicsHang, "Windows filed a report that the graphics card stopped answering"));
            // And the moment each hang really happened, where its report can be read (an admin agent can).
            if (hangs.Count > 0)
            {
                var reports = HangReports();
                foreach (var hang in hangs.GroupBy(h => h.Key))
                {
                    var at = hang.Select(h => reports.Where(r => Math.Abs((r.Written - h.Logged).TotalSeconds) <= 20).Select(r => (DateTime?)r.Hung).FirstOrDefault())
                        .FirstOrDefault(t => t is not null);
                    if (at is DateTime hung) found.Add((TimeUtil.ToUnix(hung), SignKind.GraphicsHangAt, "The graphics card hung"));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("crashes", ex);
        }
        return [.. found.GroupBy(f => (f.Minute, f.Kind, f.Detail)).OrderBy(g => g.Key.Minute)
            .Select(g => new CrashSign(g.Key.Minute, g.Key.Kind, g.Key.Kind == SignKind.GraphicsHang || g.Count() == 1 ? g.Key.Detail : $"{g.Key.Detail} ({g.Count()} times)"))];
    }

    /// <summary>
    /// Windows' graphics-hang reports as filed: when each was written, and the moment of the hang it gives (its
    /// EventTime). The folder is readable only with admin rights: empty without them.
    /// </summary>
    internal static List<(DateTime Written, DateTime Hung)> HangReports()
    {
        var list = new List<(DateTime, DateTime)>();
        try
        {
            var archive = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "ReportArchive");
            foreach (var dir in Directory.EnumerateDirectories(archive, "Kernel_141_*").Concat(Directory.EnumerateDirectories(archive, "Kernel_117_*")))
            {
                try
                {
                    var wer = Path.Combine(dir, "Report.wer");
                    if (!File.Exists(wer)) continue;
                    if (EventTimeOf(File.ReadLines(wer, System.Text.Encoding.Unicode)) is DateTime hung) list.Add((Directory.GetCreationTime(dir), hung));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>A report's EventTime line ("EventTime=134...", a Windows file time) as a local time.</summary>
    internal static DateTime? EventTimeOf(IEnumerable<string> lines)
    {
        foreach (var line in lines)
            if (line.StartsWith("EventTime=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(10), out long ft) && ft > 0)
                return DateTime.FromFileTime(ft);
        return null;
    }

    /// <summary>Which of Windows' own entries count as a sign, and what each says in plain words (null: routine noise).</summary>
    internal static (string Kind, string Detail)? SignOf(string? provider, int id) => provider?.ToLowerInvariant() switch
    {
        "microsoft-windows-whea-logger" => id is 1 or 18 or 20 or 46 or 47 ? (SignKind.Hardware, "The hardware reported an error it couldn't correct")
            : id is 17 or 19 ? (SignKind.Hardware, "The hardware reported an error it corrected") : null,
        "disk" => id is 7 ? (SignKind.Drive, "A drive has a bad block")
            : id is 11 ? (SignKind.Drive, "A drive's controller reported an error")
            : id is 51 ? (SignKind.Drive, "A drive failed to read or write")
            : null, // 153 is a retry that worked
        "ntfs" => id is 55 ? (SignKind.Drive, "A drive's file system was found damaged") : null,
        "stornvme" => id is 11 ? (SignKind.Drive, "An SSD's controller reported an error") : null,
        // 13 and 14 are the card's own faults; 153 is logged on healthy PCs all the time.
        "nvlddmkm" => id is 13 or 14 ? (SignKind.Graphics, "The NVIDIA driver reported a fault of the graphics card") : null,
        "microsoft-windows-resource-exhaustion-detector" => id is 2004 ? (SignKind.Memory, "Windows was running out of memory") : null,
        _ => null,
    };

    private static DateTime? ParseShutdownTime(EventRecord record)
    {
        // Properties: [0] time, [1] date, formatted for the PC's locale and sprinkled with direction marks.
        return ParseShutdownTime(Prop(record, 1), Prop(record, 0));
    }

    /// <summary>
    /// When the PC went down, held against when Windows wrote it up (the next start, so always after). The date is text
    /// in whoever-wrote-it's format, and "10/6" read the other way round is months off or still to come: of the two
    /// readings, the one that falls before the next start and nearest to it; null when neither does (the next start's
    /// time is used then).
    /// </summary>
    internal static DateTime? ShutdownBefore(DateTime read, DateTime logged)
    {
        var readings = new List<DateTime> { read };
        if (read.Day <= 12 && read.Day != read.Month) readings.Add(new DateTime(read.Year, read.Day, read.Month, read.Hour, read.Minute, read.Second, read.Kind));
        var before = readings.Where(r => r <= logged.AddMinutes(1)).ToList();
        return before.Count == 0 ? null : before.Max();
    }

    internal static DateTime? ParseShutdownTime(string? rawDate, string? rawTime)
    {
        var time = Clean(rawTime);
        var date = Clean(rawDate);
        if (DateTime.TryParse($"{date} {time}", CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var dt)) return dt;
        foreach (var fmt in new[] { "dd/MM/yyyy HH:mm:ss", "MM/dd/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "d/M/yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt" })
            if (DateTime.TryParseExact($"{date} {time}", fmt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt)) return dt;
        return null;

        static string Clean(string? s) => NonPrintable().Replace(s ?? "", "").Trim();
    }

    /// <param name="also">A further condition on the entry, outside its System part (on its data, say).</param>
    private static IEnumerable<EventRecord> Query(string log, string xpath, DateTime since, string? also = null)
    {
        var withTime = xpath.Replace("*[System[", $"*[System[TimeCreated[@SystemTime>='{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}'] and (") .Replace("]]", ")]]");
        if (also is not null) withTime = $"{withTime[..^1]} and {also}]";
        EventLogReader reader;
        try
        {
            reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, withTime));
        }
        catch (Exception ex)
        {
            Log.Error("crashes", ex);
            yield break;
        }
        using (reader)
        {
            while (true)
            {
                EventRecord? record;
                try { record = reader.ReadEvent(); }
                catch { yield break; }
                if (record is null) yield break;
                yield return record;
            }
        }
    }

    /// <summary>
    /// One of an entry's values. Windows makes the whole list again each time it is asked for, so the list of the
    /// entry being read is kept: an app crash is asked for ten of them, an error report for four.
    /// </summary>
    private static string? Prop(EventRecord r, int index)
    {
        if (!ReferenceEquals(_propsOf, r)) (_props, _propsOf) = (r.Properties, r);
        return index < _props!.Count ? _props[index].Value?.ToString() : null;
    }

    [ThreadStatic] private static EventRecord? _propsOf;
    [ThreadStatic] private static IList<EventProperty>? _props;

    private static Dictionary<string, string> Named(EventRecord r)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = XDocument.Parse(r.ToXml());
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            foreach (var d in doc.Descendants(ns + "Data"))
                if (d.Attribute("Name")?.Value is { } name) result[name] = d.Value;
        }
        catch { }
        return result;
    }

    [GeneratedRegex(@"[^ -~]")]
    private static partial Regex NonPrintable();
}
