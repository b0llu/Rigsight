using Rigsight.Core.Reports;
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
    /// The temperature alert: "Your GPU is running hot" / "It has been at 86°C for more than 30 seconds, above your 83°C
    /// limit, with Cyberpunk 2077 running."; several at once: "Your CPU and GPU are running hot" / "CPU 91°C and GPU 86°C,
    /// above your limits for more than 30 seconds, with Cyberpunk 2077 running."
    /// </summary>
    public static (string Title, string Body) Running(IReadOnlyList<Hot> over, int sustainSeconds, string? app)
    {
        // The hot spot is the GPU's: the title names the parts, not the readings.
        var parts = over.Select(h => h.Part == "GPU hot spot" ? "GPU" : h.Part).Distinct().ToList();
        string title = parts.Count == 1 ? Lang.T($"Your {parts[0]} is running hot") : Lang.T("Your CPU and GPU are running hot");
        if (over.Count == 1)
        {
            var h = over[0];
            string value = Temp(h.Value), limit = Temp(h.Limit), held = Long(sustainSeconds);
            bool spot = h.Part == "GPU hot spot";
            string body = (spot, app is null) switch
            {
                (false, true) => Lang.T($"It has been at {value} for more than {held}, above your {limit} limit."),
                (false, false) => Lang.T($"It has been at {value} for more than {held}, above your {limit} limit, with {app} running."),
                (true, true) => Lang.T($"Its hot spot has been at {value} for more than {held}, above your {limit} limit."),
                _ => Lang.T($"Its hot spot has been at {value} for more than {held}, above your {limit} limit, with {app} running."),
            };
            return (title, body);
        }
        var named = over.Select(h => $"{Lang.T(h.Part)} {Temp(h.Value)}").ToList();
        string readings = named.Count == 2 ? Lang.T($"{named[0]} and {named[1]}") : Lang.T($"{string.Join(", ", named.Take(named.Count - 1))} and {named[^1]}");
        string time = Long(sustainSeconds);
        return (title, app is null ? Lang.T($"{readings}, above your limits for more than {time}.")
            : Lang.T($"{readings}, above your limits for more than {time}, with {app} running."));
    }

    /// <summary>The CPU and GPU peaks as a sentence: "The CPU peaked at 71°C and the GPU at 68°C." (empty: neither known).</summary>
    public static string Peaks(double? cpu, double? gpu) => (cpu, gpu) switch
    {
        ({ } c, { } g) => Lang.T($"The CPU peaked at {Temp(c)} and the GPU at {Temp(g)}."),
        ({ } c, null) => Lang.T($"The CPU peaked at {Temp(c)}."),
        (null, { } g) => Lang.T($"The GPU peaked at {Temp(g)}."),
        _ => "",
    };

    /// <summary>
    /// How long, in words, inside a notification's sentences: "48 minutes", "2 hours and 14 minutes" (the user's ruling,
    /// 2026-11-05: a notification is read as a sentence, away from the app; pages keep the short 2h 14m).
    /// </summary>
    public static string Long(double seconds) => CrashFindings.Words(seconds);

    /// <summary>A widget's timer has run its length: "Your timer is up" / "25 minutes, on Desk."</summary>
    public static (string Title, string Body) TimerUp(int minutes, string widget) =>
        (Lang.T("Your timer is up"), Lang.T($"{Long(minutes * 60.0)}, on {widget}."));

    /// <summary>A game session's end: "You played Dota 2 for 2 hours and 14 minutes" / "The CPU peaked at 71°C and the GPU at 68°C."</summary>
    public static (string Title, string Body) Session(string game, double activeSec, double? cpuPeak, double? gpuPeak) =>
        (Lang.T($"You played {Name(game)} for {Long(activeSec)}"), Peaks(cpuPeak, gpuPeak));

    /// <summary>Longest app or game name a notification's title holds whole.</summary>
    public const int NameMax = 30;

    /// <summary>
    /// An app or game's name for a notification's title, so the words after it (how long it was played, "crashed") always
    /// fit the card's two title lines: a name past <see cref="NameMax"/> letters ends at a word with "…" ("Warhammer
    /// 40,000: Space Marine 2…"). Seen 2026-11-05: "You played Phantom Blade Zero for 2h…" never said how long.
    /// </summary>
    public static string Name(string name)
    {
        name = name.Trim();
        if (name.Length <= NameMax) return name;
        int cut = name.LastIndexOf(' ', NameMax - 1);
        string head = (cut > NameMax / 2 ? name[..cut] : name[..(NameMax - 1)]).TrimEnd(' ', '-', ':', ',', '–', '—', '|', '.');
        return head + "…";
    }

    /// <summary>
    /// The day's recap: "Yesterday: 6h 12m active" / "Mostly Dota 2, for 3 hours and 5 minutes. The CPU peaked at 78°C and
    /// the GPU at 72°C." A day of the last week by its name, an older one by its date. The title is a label with its
    /// figure, so it keeps the short form; the sentence under it says time in words.
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
        string mostly = top is null ? "" : Lang.T($"Mostly {top}, for {Long(topSec)}.");
        return (title, string.Join(" ", new[] { mostly, Peaks(cpuPeak, gpuPeak) }.Where(s => s.Length > 0)));
    }

    /// <summary>
    /// An app or game that crashed or froze, under its title ("Dota 2 crashed"): its cause as its case says it, then how
    /// long it had run ("Inside the NVIDIA driver, 48 minutes into the game."); with nothing to go on, how long it had run and
    /// that there isn't enough to say why. A notification never states a cause nothing was read for.
    /// </summary>
    public static string AppDown(CrashFinding? finding, bool game, double activeSec)
    {
        string time = Long(activeSec);
        string cause = (finding?.Verdict ?? "").TrimEnd('.');
        if (finding is null or { Confidence: Confidence.NotEnough } || cause.Length == 0)
        {
            string ran = Lang.T($"It had been running for {time}.");
            return finding is { Confidence: Confidence.NotEnough } ? $"{ran} {Lang.T("There isn't enough on record to say why.")}" : ran;
        }
        return game ? Lang.T($"{cause}, {time} into the game.") : Lang.T($"{cause}, after {time} of use.");
    }

    // ── The notifications round (2026-11-07, mock-up LWycKU9ZC8jzeVH8bVjPWm, written by WORDING.md) ──

    /// <summary>A difference between two temperatures with its unit, as shown: "7°C" (each side rounded first, so it adds up).</summary>
    public static string Diff(double a, double b) => Units.ShownDiff(a, b) + (Units.Fahrenheit ? "F" : "C");

    /// <summary>A page's line with its temperatures given their unit ("runs 6° hotter" → "runs 6°C hotter"): a notification is read away from the app.</summary>
    public static string WithUnit(string line) =>
        System.Text.RegularExpressions.Regex.Replace(line, @"(\d)°(?![CF])", "$1°" + (Units.Fahrenheit ? "F" : "C"));

    /// <summary>The temperature alert's last sentence when the game's usual is known: "In this game it usually sits at 69–72°C."</summary>
    public static string UsualInGame(double low, double high) =>
        Math.Round(low) == Math.Round(high) ? Lang.T($"In this game it usually sits at {Temp(low)}.")
            : Lang.T($"In this game it usually sits at {Units.TempShort(low).TrimEnd('°')}–{Temp(high)}.");

    /// <summary>
    /// A fan reading 0 rpm where it always turns: "Your GPU fan stopped" / "It showed 0 rpm for 4 minutes from 8:37 PM with
    /// the GPU at 74°C, though it always turns at that heat. Check it isn't blocked or unplugged."
    /// </summary>
    public static NoticeText FanStopped(string name, bool gpu, int minutes, DateTime from, double? temp)
    {
        string mins = Long(minutes * 60), clock = TimeText.At(from, "h:mm tt");
        if (gpu)
            return new(Lang.T("Your GPU fan stopped"), temp is double t
                ? Lang.T($"It showed 0 rpm for {mins} from {clock} with the GPU at {Temp(t)}, though it always turns at that heat. Check it isn't blocked or unplugged.")
                : Lang.T($"It showed 0 rpm for {mins} from {clock}, though it always turns at that heat. Check it isn't blocked or unplugged."));
        return new(Lang.T($"{name} on your motherboard stopped"),
            Lang.T($"It showed 0 rpm for {mins} from {clock}, though it always turns while your PC is on. Check it isn't blocked or unplugged."));
    }

    /// <summary>
    /// A fan on a curve turning slower than usual at the same heat: "Your GPU fans spun 18% slower than usual" / "They turned
    /// at 1,650 rpm with the GPU at 72°C, against 2,010 rpm in the 7 days before. If you changed the fan curve…".
    /// </summary>
    public static NoticeText FanSlower(string name, bool gpu, double percent, double now, double was, string part, double temp, string usualLabel, string check) => gpu
        ? new(Lang.T($"Your GPU fans spun {percent:0}% slower than usual"),
            Lang.T($"They turned at {now:N0} rpm with the {part} at {Temp(temp)}, against {was:N0} rpm {usualLabel}.") + " " + check)
        : new(Lang.T($"{name} on your motherboard spun {percent:0}% slower than usual"),
            Lang.T($"It turned at {now:N0} rpm with the {part} at {Temp(temp)}, against {was:N0} rpm {usualLabel}.") + " " + check);

    /// <summary>A fan at a set speed slowing while the PC was on: "CPU Fan on your motherboard slowed to 820 rpm" / "It went from 1,200 to 820 rpm while your PC was on. …".</summary>
    public static NoticeText FanSlowed(string name, int from, int to, string check) =>
        new(Lang.T($"{name} on your motherboard slowed to {to:N0} rpm"), Lang.T($"It went from {from:N0} to {to:N0} rpm while your PC was on.") + " " + check);

    /// <summary>Slower than months ago at the same heat: "Your GPU fans now spin 12% slower than in June" / "They turn at … against … then. …".</summary>
    public static NoticeText FanOlder(string name, bool gpu, double percent, double now, double was, string part, double temp, string when, string check) => gpu
        ? new(Lang.T($"Your GPU fans now spin {percent:0}% slower than {when}"),
            Lang.T($"They turn at {now:N0} rpm with the {part} at {Temp(temp)}, against {was:N0} rpm then.") + " " + check)
        : new(Lang.T($"{name} on your motherboard now spins {percent:0}% slower than {when}"),
            Lang.T($"It turns at {now:N0} rpm with the {part} at {Temp(temp)}, against {was:N0} rpm then.") + " " + check);

    /// <summary>The GPU slowing itself to stay cool: "Your GPU slowed itself for 12 minutes to stay cool" / "Its clocks fell about 9% once it reached 83°C."</summary>
    public static NoticeText Slowed(Throttling t) =>
        new(Lang.T($"Your GPU slowed itself for {Long(t.Minutes * 60)} to stay cool"), Lang.T($"Its clocks fell about {t.DropPercent:0}% once it reached {Temp(t.FromTemp)}."));

    /// <summary>The same, as the last sentence of a game's summary: "The GPU slowed itself for 12 minutes to stay cool."</summary>
    public static string SlowedInSummary(Throttling t) => Lang.T($"The GPU slowed itself for {Long(t.Minutes * 60)} to stay cool.");

    /// <summary>
    /// A game hotter than its usual at the same power: "Crimson Desert ran 7°C hotter than usual on the GPU" / "It averaged
    /// 78°C against its usual 69–72°C, at the same power. With nothing running it sits at its usual 41°C."
    /// </summary>
    public static NoticeText Hotter(UsualHeat h)
    {
        string diff = Diff(h.Now, h.Usual), now = Temp(h.Now), span = Math.Round(h.Low) == Math.Round(h.High) ? Temp(h.Low) : $"{Units.TempShort(h.Low).TrimEnd('°')}–{Temp(h.High)}";
        string game = Name(h.App), part = h.Part;
        string body = Lang.T($"It averaged {now} against its usual {span}, at the same power.");
        if (h.RestNow is double rn && h.RestUsual is double ru)
            body += " " + (Math.Abs(rn - ru) < 1.5 ? Lang.T($"With nothing running it sits at its usual {Temp(rn)}.")
                : rn > ru ? Lang.T($"With nothing running it's only {Diff(rn, ru)} warmer than usual.") : Lang.T($"With nothing running it's only {Diff(rn, ru)} cooler than usual."));
        return new(Lang.T($"{game} ran {diff} hotter than usual on the {part}"), body);
    }

    /// <summary>The same, as the last sentence of the game's summary: "The GPU ran 7°C hotter than usual at the same power."</summary>
    public static string HotterInSummary(UsualHeat h) => Lang.T($"The {h.Part} ran {Diff(h.Now, h.Usual)} hotter than usual at the same power.");

    /// <summary>The PC's own problems past their usual: "3 graphics driver resets in the last 7 days" / "Your PC usually has none."</summary>
    public static NoticeText MoreProblems(string what, string span, int usual, double usualWeeks, double periodWeeks) =>
        new(Wording.Capital($"{what} {span}"), usual == 0 ? Lang.T("Your PC usually has none.")
            : Lang.T($"Your PC usually has {Reports.PcUsual.About(usual, usualWeeks, periodWeeks)}."));

    /// <summary>A change that made a game worse, its headline with the unit, and what it's compared with.</summary>
    public static NoticeText WorseChange(ChangeResult r)
    {
        string context = WithUnit(r.Context).Trim();
        if (context.Length > 0 && !context.EndsWith('.')) context += ".";
        return new(WithUnit(r.Said), Wording.Capital(context));
    }

    /// <summary>A drive whose own health check (SMART) reports a failure.</summary>
    public static NoticeText DriveFailing(string drive) =>
        new(Lang.T($"Your {drive} reports a failure"), Lang.T("SMART, the drive's own health check, says Bad. Back up what's on it now."));

    /// <summary>A drive whose health check turned Caution without bad sectors to name.</summary>
    public static NoticeText DriveCaution(string drive) =>
        new(Lang.T($"Your {drive} reports early warning signs"), Lang.T("SMART, the drive's own health check, says Caution. Keep a backup of what's on it."));

    /// <summary>
    /// A drive's bad sectors, the first or more than before: "Your Samsung SSD reports bad sectors" / "It now has 3 reallocated
    /// sectors, and had none before. Bad sectors are an early warning, so keep a backup."
    /// </summary>
    public static NoticeText DriveSectors(string drive, string sectors, long? before)
    {
        string head = before switch
        {
            null => Lang.T($"It has {sectors} sectors."),
            0 => Lang.T($"It now has {sectors} sectors, and had none before."),
            _ => Lang.T($"It now has {sectors} sectors, up from {before:N0}."),
        };
        return new(Lang.T($"Your {drive} reports bad sectors"), head + " " + Lang.T("Bad sectors are an early warning, so keep a backup."));
    }

    /// <summary>An SSD reporting almost all of its rated life used: "It has 8% of its rated life left. Keep a backup of what's on it."</summary>
    public static NoticeText DriveWorn(string drive, double left) =>
        new(Lang.T($"Your {drive} reports most of its rated life used"), Lang.T($"It has {left:0}% of its rated life left. Keep a backup of what's on it."));

    /// <summary>A drive at or past its own warning temperature: "Your Samsung SSD 980 is running hot" / "It reached 72°C, above its own 70°C warning point."</summary>
    public static NoticeText DriveHot(string drive, double temp, double warning) =>
        new(Lang.T($"Your {drive} is running hot"), temp > warning
            ? Lang.T($"It reached {Temp(temp)}, above its own {Temp(warning)} warning point.")
            : Lang.T($"It reached {Temp(warning)}, its own warning point."));

    /// <summary>
    /// An SSD passing half or a quarter of its rated life: "Your KINGSTON SA400 has half its rated life left" / "It uses about
    /// 8% a year, so at this pace that's around 6 more years." (without a pace yet: how much it has left).
    /// </summary>
    public static NoticeText DriveLife(string drive, int mark, double left, double? perYear) =>
        new(mark == 50 ? Lang.T($"Your {drive} has half its rated life left") : Lang.T($"Your {drive} has a quarter of its rated life left"),
            perYear is double p && p >= 0.5
                ? Lang.T($"It uses about {p:0}% a year, so at this pace that's around {Math.Max(1, Math.Round(left / p)):0} more years.")
                : Lang.T($"It has {left:0}% of its rated life left, by its own count."));

    /// <summary>
    /// A chip that sits further above its rest under the same load than in its first weeks (Wear): "Your CPU runs hotter
    /// under load than it used to" / "At the same load it now sits 18°C above its resting temperature, against 14°C in late
    /// September."
    /// </summary>
    public static NoticeText RunsHotter(string part, double gapNow, double gapThen, string when) =>
        new(Lang.T($"Your {part} runs hotter under load than it used to"),
            Lang.T($"At the same load it now sits {Units.TempGap(gapNow)}{(Units.Fahrenheit ? "F" : "C")} above its resting temperature, against {Units.TempGap(gapThen)}{(Units.Fahrenheit ? "F" : "C")} in {when}."));

    /// <summary>
    /// A drive almost full: "Your C drive is almost full" / "It has 8 GB left of 476 GB." The Windows drive also says why
    /// that matters there (updates need room); other drives don't, since it isn't true of them.
    /// </summary>
    public static NoticeText DriveFull(char letter, long freeBytes, long totalBytes, bool windowsDrive)
    {
        string body = Lang.T($"It has {Units.Bytes(freeBytes)} left of {Units.Bytes(totalBytes)}.");
        if (windowsDrive) body += " " + Lang.T("Windows needs free space to install updates.");
        return new(Lang.T($"Your {letter} drive is almost full"), body);
    }

    /// <summary>An app whose memory keeps growing: "Google Chrome keeps using more memory" / "It has gone from 2.1 GB to 9.2 GB in 3 hours, and hasn't come back down."</summary>
    public static NoticeText MemoryGrowing(string app, long fromBytes, long toBytes, double seconds) =>
        new(Lang.T($"{Name(app)} keeps using more memory"),
            Lang.T($"It has gone from {Units.Bytes(fromBytes)} to {Units.Bytes(toBytes)} in {Long(seconds)}, and hasn't come back down."));

    /// <summary>A new app starting with Windows, as the Timeline records it: its own title ("FACEIT now starts with Windows") / "It was added at 4:12 PM."</summary>
    public static NoticeText StartsWithWindows(string title, DateTime at, DateTime now)
    {
        string clock = TimeText.At(at, "h:mm tt");
        return new(title, at.Date == now.Date ? Lang.T($"It was added at {clock}.") : at.Date == now.Date.AddDays(-1) ? Lang.T($"It was added yesterday at {clock}.")
            : Lang.T($"It was added on {at:dddd} at {clock}."));
    }

    /// <summary>
    /// The last 7 days' recap: "31h 20m active in the last 7 days" / "Mostly Crimson Desert, for 9 hours and 5 minutes.
    /// That's 4 hours less than the 7 days before." The title is a label with its figure, so it keeps the short form.
    /// </summary>
    public static (string Title, string Body) Weekly(double activeSec, string? top, double topSec, double? beforeSec)
    {
        string title = Lang.T($"{Units.Duration(activeSec)} active in the last 7 days");
        var parts = new List<string>();
        if (top is not null) parts.Add(Lang.T($"Mostly {top}, for {Long(topSec)}."));
        if (beforeSec is double b && Math.Abs(activeSec - b) >= 1800)
        {
            string by = Long(Math.Round(Math.Abs(activeSec - b) / 1800) * 1800);
            parts.Add(activeSec < b ? Lang.T($"That's {by} less than the 7 days before.") : Lang.T($"That's {by} more than the 7 days before."));
        }
        return (title, string.Join(" ", parts));
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
