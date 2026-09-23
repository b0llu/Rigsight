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
    private const string AppQuery =
        "*[System[(EventID=1000 and Provider[@Name='Application Error']) or (EventID=1002 and Provider[@Name='Application Hang'])]]";

    private const string SystemQuery =
        "*[System[(EventID=41 and Provider[@Name='Microsoft-Windows-Kernel-Power']) or " +
        "(EventID=6008 and Provider[@Name='EventLog']) or (EventID=4101 and Provider[@Name='Display'])]]";

    /// <summary>All crashes logged since <paramref name="since"/> (oldest first).</summary>
    public static List<CrashEvent> ReadSince(DateTime since)
    {
        var events = new List<CrashEvent>();
        var shutdownTimes = new List<(DateTime Logged, DateTime Happened)>();
        var kernelPower = new List<(DateTime Logged, uint Bugcheck, bool Sleep)>();

        foreach (var record in Query("Application", AppQuery, since))
        {
            using (record)
            {
                if (ParseApp(record) is { } e) events.Add(e);
            }
        }

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
                        if (ParseShutdownTime(record) is { } happened) shutdownTimes.Add((logged, happened));
                        break;
                    case 41:
                        var data = Named(record);
                        uint.TryParse(data.GetValueOrDefault("BugcheckCode"), out var bugcheck);
                        bool sleep = data.GetValueOrDefault("SleepInProgress") is { } s && s != "0" && s != "false" ||
                                     int.TryParse(data.GetValueOrDefault("SystemSleepTransitionsToOn"), out var t) && t > 0;
                        kernelPower.Add((logged, bugcheck, sleep));
                        break;
                }
            }
        }

        // Kernel-Power 41 is logged at the *next* boot; event 6008 says when the PC actually went down.
        foreach (var (logged, bugcheck, sleep) in kernelPower)
        {
            var match = shutdownTimes.Where(s => Math.Abs((s.Logged - logged).TotalMinutes) < 5).Select(s => (DateTime?)s.Happened).FirstOrDefault();
            events.Add(new CrashEvent
            {
                Ts = TimeUtil.ToUnix(match ?? logged),
                Kind = bugcheck != 0 ? CrashKind.SystemCrash : CrashKind.UnexpectedShutdown,
                AppExe = "",
                Code = bugcheck != 0 ? $"0x{bugcheck:X}" : null,
                DuringSleep = sleep,
                Detail = match is null ? $"Noticed at startup {logged:g}" : $"Found at next startup ({logged:g})",
            });
        }

        return [.. events.OrderBy(e => e.Ts)];
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
            };
        }

        string? path = record.Properties.Select(p => p.Value?.ToString()).FirstOrDefault(v => v is not null && v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && v.Contains('\\'));
        return new CrashEvent { Ts = time, Kind = CrashKind.AppHang, AppExe = exe, AppPath = path };
    }

    private static DateTime? ParseShutdownTime(EventRecord record)
    {
        // Properties: [0] time, [1] date, formatted for the PC's locale and sprinkled with direction marks.
        var time = Clean(Prop(record, 0));
        var date = Clean(Prop(record, 1));
        if (DateTime.TryParse($"{date} {time}", CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var dt)) return dt;
        foreach (var fmt in new[] { "dd/MM/yyyy HH:mm:ss", "MM/dd/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "d/M/yyyy H:mm:ss", "M/d/yyyy h:mm:ss tt" })
            if (DateTime.TryParseExact($"{date} {time}", fmt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt)) return dt;
        return null;

        static string Clean(string? s) => NonPrintable().Replace(s ?? "", "").Trim();
    }

    private static IEnumerable<EventRecord> Query(string log, string xpath, DateTime since)
    {
        var withTime = xpath.Replace("*[System[", $"*[System[TimeCreated[@SystemTime>='{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}'] and (") .Replace("]]", ")]]");
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

    private static string? Prop(EventRecord r, int index) =>
        index < r.Properties.Count ? r.Properties[index].Value?.ToString() : null;

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
