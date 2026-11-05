using Rigsight.Core.Stability;

namespace Rigsight.Core;

/// <summary>
/// What RigSight's notifications say, in one place (rewritten 2026-11-05, mock-up WACp8McmbKiexv12GwViVB tab 5, the
/// user's rules): the title says what happened in plain words, the most useful fact goes under it, RigSight reports what
/// it saw and never more, nothing is said twice, temperatures carry their unit (78°C: a notification is read away from
/// the app), and no "Click for the full picture." (a click on any notification opens its page).
/// </summary>
public static class NoticeWords
{
    /// <summary>A temperature with its unit, in the unit picked in Settings: "78°C", "172°F".</summary>
    public static string Temp(double celsius) => Units.TempShort(celsius) + (Units.Fahrenheit ? "F" : "C");

    /// <summary>A reading over its limit: its name ("CPU", "GPU", "GPU hot spot"), the reading and the limit, in °C.</summary>
    public readonly record struct Hot(string Part, double Value, double Limit);

    /// <summary>
    /// The temperature alert: "Your GPU is running hot" / "It has been at 86°C for over 10 seconds, above your 83°C limit,
    /// while Cyberpunk 2077 was running."; several at once: "Your CPU and GPU are running hot" / "CPU 91°C and GPU 86°C,
    /// above your limits for over 10 seconds while Cyberpunk 2077 was running."
    /// </summary>
    public static (string Title, string Body) Running(IReadOnlyList<Hot> over, int sustainSeconds, string? app)
    {
        // The hot spot is the GPU's: the title names the parts, not the readings.
        var parts = over.Select(h => h.Part == "GPU hot spot" ? "GPU" : h.Part).Distinct().ToList();
        string title = parts.Count == 1 ? Lang.T($"Your {parts[0]} is running hot") : Lang.T("Your CPU and GPU are running hot");
        if (over.Count == 1)
        {
            var h = over[0];
            string value = Temp(h.Value), limit = Temp(h.Limit);
            bool spot = h.Part == "GPU hot spot";
            string body = (spot, app is null) switch
            {
                (false, true) => Lang.T($"It has been at {value} for over {sustainSeconds} seconds, above your {limit} limit."),
                (false, false) => Lang.T($"It has been at {value} for over {sustainSeconds} seconds, above your {limit} limit, while {app} was running."),
                (true, true) => Lang.T($"Its hot spot has been at {value} for over {sustainSeconds} seconds, above your {limit} limit."),
                _ => Lang.T($"Its hot spot has been at {value} for over {sustainSeconds} seconds, above your {limit} limit, while {app} was running."),
            };
            return (title, body);
        }
        var named = over.Select(h => $"{Lang.T(h.Part)} {Temp(h.Value)}").ToList();
        string readings = named.Count == 2 ? Lang.T($"{named[0]} and {named[1]}") : Lang.T($"{string.Join(", ", named.Take(named.Count - 1))} and {named[^1]}");
        return (title, app is null ? Lang.T($"{readings}, above your limits for over {sustainSeconds} seconds.")
            : Lang.T($"{readings}, above your limits for over {sustainSeconds} seconds while {app} was running."));
    }

    /// <summary>The CPU and GPU peaks as a sentence: "The CPU peaked at 71°C and the GPU at 68°C." (empty: neither known).</summary>
    public static string Peaks(double? cpu, double? gpu) => (cpu, gpu) switch
    {
        ({ } c, { } g) => Lang.T($"The CPU peaked at {Temp(c)} and the GPU at {Temp(g)}."),
        ({ } c, null) => Lang.T($"The CPU peaked at {Temp(c)}."),
        (null, { } g) => Lang.T($"The GPU peaked at {Temp(g)}."),
        _ => "",
    };

    /// <summary>A game session's end: "You played Dota 2 for 2h 14m" / "The CPU peaked at 71°C and the GPU at 68°C."</summary>
    public static (string Title, string Body) Session(string game, double activeSec, double? cpuPeak, double? gpuPeak) =>
        (Lang.T($"You played {game} for {Units.Duration(activeSec)}"), Peaks(cpuPeak, gpuPeak));

    /// <summary>
    /// The day's recap: "Yesterday: 6h 12m active" / "Mostly Dota 2, for 3h 05m. The CPU peaked at 78°C and the GPU at
    /// 72°C." A day of the last week by its name, an older one by its date.
    /// </summary>
    public static (string Title, string Body) Recap(DateTime day, DateTime today, double activeSec, string? top, double topSec, double? cpuPeak, double? gpuPeak)
    {
        var names = Lang.Translating ? System.Globalization.CultureInfo.GetCultureInfo(Lang.Code) : System.Globalization.CultureInfo.InvariantCulture;
        string active = Units.Duration(activeSec), weekday = day.ToString("dddd", names), date = day.ToString("d MMMM", names);
        string title = Wording.Capital((today.Date - day.Date).Days switch
        {
            1 => Lang.T($"Yesterday: {active} active"),
            < 7 => Lang.T($"{weekday}: {active} active"),
            _ => Lang.T($"{date}: {active} active"),
        });
        string mostly = top is null ? "" : Lang.T($"Mostly {top}, for {Units.Duration(topSec)}.");
        return (title, string.Join(" ", new[] { mostly, Peaks(cpuPeak, gpuPeak) }.Where(s => s.Length > 0)));
    }

    /// <summary>
    /// An app or game that crashed or froze, under its title ("Dota 2 crashed"): its cause as its case says it, then how
    /// long it had run ("Inside the NVIDIA driver, 48m into the game."); with nothing to go on, how long it had run and
    /// that there isn't enough to say why. A notification never states a cause nothing was read for.
    /// </summary>
    public static string AppDown(CrashFinding? finding, bool game, double activeSec)
    {
        string time = Units.Duration(activeSec);
        string cause = (finding?.Verdict ?? "").TrimEnd('.');
        if (finding is null or { Confidence: Confidence.NotEnough } || cause.Length == 0)
        {
            string ran = Lang.T($"It had been running for {time}.");
            return finding is { Confidence: Confidence.NotEnough } ? $"{ran} {Lang.T("There isn't enough on record to say why.")}" : ran;
        }
        return game ? Lang.T($"{cause}, {time} into the game.") : Lang.T($"{cause}, after {time} of use.");
    }

    /// <summary>The PC-wide problem's title, a sentence: "Your PC shut off suddenly", "Windows crashed with a blue screen".</summary>
    public static string PcDownTitle(CrashKind kind) => kind switch
    {
        CrashKind.SystemCrash => Lang.T("Windows crashed with a blue screen"),
        CrashKind.GpuDriverReset => Lang.T("The graphics driver was reset"),
        _ => Lang.T("Your PC shut off suddenly"),
    };

    /// <summary>
    /// Under it, when and why, without saying what happened twice: "It happened on Wednesday at 9:14 PM, likely a power
    /// cut." With no finding, when and Windows' own reason.
    /// </summary>
    public static string PcDownBody(DateTime at, DateTime now, CrashFinding? finding, string? reason)
    {
        string clock = TimeText.At(at, "h:mm tt");
        string when = at.Date == now.Date ? Lang.T($"at {clock}") : at.Date == now.Date.AddDays(-1) ? Lang.T($"yesterday at {clock}")
            : Lang.T($"on {at:dddd} at {clock}");
        if (finding is null) return $"{Lang.T($"It happened {when}.")}{(string.IsNullOrEmpty(reason) ? "" : " " + reason)}";
        string head = Lang.T($"It happened {when}") + Wording.CauseAfter(finding.Confidence, finding.Why) + ".";
        return finding.Confidence == Confidence.NotEnough ? $"{head} {Lang.T("There isn't enough on record to say why.")}" : head;
    }
}
