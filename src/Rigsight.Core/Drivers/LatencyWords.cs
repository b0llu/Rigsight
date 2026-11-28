using Rigsight.Core.Data;

namespace Rigsight.Core.Drivers;

/// <summary>
/// What the Latency page says (issue #15, approved mock-up 2026-11-24), built once here so the page, its export and the
/// taskbar's hover say it the same way. Facts only: no verdict on whether the PC is fit for sound or games, and no other
/// tool named or compared with.
/// </summary>
public static class LatencyWords
{
    /// <summary>
    /// A delay as said on the page: one decimal from 1 ms (as the Timeline says it, "6.8 ms"), two under it ("0.42 ms"),
    /// "under 0.01 ms" for the shortest.
    /// </summary>
    public static string Ms(double us) => us >= 100_000 ? Lang.T($"{us / 1000.0:N0} ms") : us >= 1000 ? DriverWords.Ms(us) : us < 10 ? Lang.T("under 0.01 ms") : Lang.T($"{us / 1000.0:0.00} ms");

    /// <summary>The figure alone, for the big number beside its small "ms": "6.8", "0.42", "0.01".</summary>
    public static string Number(double us) => us >= 1000 ? $"{Math.Round(us / 1000.0, 1):0.0}" : $"{Math.Max(0.01, us / 1000.0):0.00}";

    /// <summary>Who a driver is, at the start of a sentence, "" (no driver matched) said too.</summary>
    public static string Who(string file, DriverPart? part) => file.Length == 0 ? Lang.T("Something RigSight couldn't match to a driver") : DriverWords.Who(file, part);

    /// <summary>The grey line under a driver's name: its file, with what a shared one is used by, or the model of a named part.</summary>
    public static string File(string file, DriverPart? part)
    {
        if (file.Length == 0) return Lang.T("No driver file");
        if (part?.Model is { Length: > 0 } model && part.Say is not null) return $"{file} · {model}";
        return DriverWords.SharedBy(file, part) is { } by ? $"{file} · {by}" : file;
    }

    /// <summary>A Segoe Fluent Icons glyph for the kind of driver.</summary>
    public static string Icon(string file, DriverPart? part) => part?.Kind == PartKind.Gpu ? "" : part?.Kind is PartKind.Ethernet or PartKind.WiFi ? ""
        : DriverWords.AreaOf(file, part) switch
        {
            DriverArea.Storage => "",
            DriverArea.Network => "",
            DriverArea.Graphics => "",
            DriverArea.Usb => "",
            DriverArea.Sound => "",
            DriverArea.Power => "",
            DriverArea.Input => "",
            DriverArea.Bluetooth => "",
            DriverArea.Windows => "",
            DriverArea.Device => "",
            _ => "",
        };

    /// <summary>"Driver delays went over 1 ms 2,500 times, 1,964 of them your GPU's driver." The driver with the most is named when it had most of them.</summary>
    public static string OverLine(int total, LatencyDriver? most)
    {
        if (total == 0) return Lang.T("No driver kept a CPU core busy for 1 ms or more.");
        string all = total == 1 ? Lang.T("A driver delay went over 1 ms once.") : Lang.T($"Driver delays went over 1 ms {total:N0} times.");
        if (most is null || most.Long * 2 < total || total == 1) return all;
        string who = Mid(Who(most.Driver, most.Part));
        return most.Long == total ? Lang.T($"Driver delays went over 1 ms {total:N0} times, all of them from {who}.") : Lang.T($"Driver delays went over 1 ms {total:N0} times, {most.Long:N0} of them from {who}.");
    }

    /// <summary>Who, in the middle of a sentence: "your GPU", "Windows' graphics system" (a name that starts with Windows keeps its capital).</summary>
    public static string Mid(string who) => who.StartsWith("Windows", StringComparison.Ordinal) || who.Length == 0 ? who : char.ToLower(who[0], System.Globalization.CultureInfo.CurrentCulture) + who[1..];

    /// <summary>"Too soon to say what's usual. It needs 7 days of driver history, and has 3 so far."</summary>
    public static string TooSoon(int days) => days == 1
        ? Lang.T($"Too soon to say what's usual. It needs {DriverRules.UsualDays} days of driver history, and has 1 so far.")
        : days == 0 ? Lang.T($"Too soon to say what's usual. It needs {DriverRules.UsualDays} days of driver history, and has less than a day so far.")
        : Lang.T($"Too soon to say what's usual. It needs {DriverRules.UsualDays} days of driver history, and has {days} so far.");

    /// <summary>"Apps waited at most 0.09 ms to run, usually under 0.01 ms."</summary>
    public static string WakeLine(LatencyWake w) => w.OverMs > 0
        ? (w.OverMs == 1 ? Lang.T($"Apps waited at most {Ms(w.HighestUs)} to run, usually {Ms(w.MedianUs)}, and 1 check took 1 ms or more.")
            : Lang.T($"Apps waited at most {Ms(w.HighestUs)} to run, usually {Ms(w.MedianUs)}, and {w.OverMs:N0} checks took 1 ms or more."))
        : Lang.T($"Apps waited at most {Ms(w.HighestUs)} to run, usually {Ms(w.MedianUs)}.");

    /// <summary>
    /// Where Windows dropped readings, as stretches of minutes (minutes up to 5 apart are one): one stretch said with its
    /// day and times, several counted.
    /// </summary>
    public static string? LostLine(IReadOnlyList<long> lostMinutes)
    {
        if (lostMinutes.Count == 0) return null;
        var sorted = lostMinutes.Order().ToList();
        var stretches = new List<(long From, long To)>();
        foreach (long m in sorted)
        {
            if (stretches.Count > 0 && m - stretches[^1].To <= 5 * 60) stretches[^1] = (stretches[^1].From, m);
            else stretches.Add((m, m));
        }
        if (stretches.Count == 1)
        {
            DateTime from = TimeUtil.FromUnix(stretches[0].From), to = TimeUtil.FromUnix(stretches[0].To + 60);
            return Lang.T($"Windows dropped some readings on {from:dddd} between {TimeText.Time(from)} and {TimeText.Time(to)}, so a longer delay then could be missing.");
        }
        return Lang.T($"Windows dropped some readings {stretches.Count} times in these days, so a longer delay then could be missing.");
    }

    /// <summary>"Recording since 22 November, with no readings dropped."</summary>
    public static string Recording(DateTime since) => Lang.T($"Recording since {since:d MMMM}, with no readings dropped.");

    /// <summary>Recording all through the period, nothing dropped.</summary>
    public static string NothingDropped => Lang.T("No readings were dropped in these days.");

    /// <summary>How the check runs right now, said under it.</summary>
    public static string HowItRuns(int rate) => rate switch
    {
        0 => Lang.T("How long Windows takes to run an app once it's woken. Paused while Battery Saver is on."),
        2 => Lang.T("How long Windows takes to run an app once it's woken. RigSight checks this twice a second on battery, using almost no CPU, though the extra wake-ups can cost a little battery. Checks are moments apart, so a short pause between two can be missed."),
        _ => Lang.T("How long Windows takes to run an app once it's woken. RigSight checks this 8 times a second, 64 while a game runs, sound plays or this page is open, using almost no CPU, though on a laptop the extra wake-ups can cost a little battery. Checks are moments apart, so a short pause between two can be missed."),
    };

    /// <summary>The line in place of the check while it's off.</summary>
    public static string Off => Lang.T("The app wake-up check is off. Driver delays are still recorded.");

    /// <summary>The check off while RigSight runs without admin rights: driver delays aren't being recorded either.</summary>
    public static string OffNoAdmin => Lang.T("The app wake-up check is off.");
}
