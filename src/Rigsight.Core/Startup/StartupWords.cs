using Rigsight.Core.Reports;

namespace Rigsight.Core.Startup;

/// <summary>
/// The Start-up page's words (approved mock-up 2026-11-27), in one place for the page, Home and the export: the three parts,
/// the kinds, how a start compares with its usual, and Windows' own reports. Figures, never a verdict or a cause.
/// </summary>
public static class StartupWords
{
    // ── The three parts and the kinds ──

    public static string BeforeWindows => Lang.T("Before Windows");
    public static string WindowsStarting => Lang.T("Windows starting");
    public static string AfterSignIn => Lang.T("Getting ready after sign-in");

    /// <summary>A part's name: 0 before Windows, 1 Windows starting, 2 getting ready after sign-in.</summary>
    public static string Part(int part) => part switch { 0 => BeforeWindows, 1 => WindowsStarting, _ => AfterSignIn };

    /// <summary>The kind, in a hover's grey line.</summary>
    public static string Kind(StartKind kind) => kind switch
    {
        StartKind.Fast => Lang.T("Fast Startup"),
        StartKind.Hibernate => Lang.T("Waking from hibernation"),
        _ => Lang.T("Full start"),
    };

    /// <summary>The opened start's chip.</summary>
    public static string Chip(StartKind kind, bool afterUpdate) => afterUpdate ? Lang.T("RESTART AFTER AN UPDATE") : kind switch
    {
        StartKind.Fast => Lang.T("FAST STARTUP"),
        StartKind.Hibernate => Lang.T("WAKING FROM HIBERNATION"),
        _ => Lang.T("FULL START"),
    };

    // ── Lengths ──

    /// <summary>A start's length: "48s", "1m 21s", "2h 05m".</summary>
    public static string Length(double seconds) => Units.DurationExact(seconds);

    /// <summary>A part's length: to a tenth of a second under 10 seconds ("8.7s"), else as a start's.</summary>
    public static string PartLength(double seconds) => seconds < 9.95 ? Lang.T($"{seconds:0.0}s") : Length(seconds);

    /// <summary>A usual as a spread: "48s to 51s".</summary>
    public static string Range(Usual u) => Lang.T($"{Length(u.Lo)} to {Length(u.Hi)}");

    /// <summary>A part's usual: "usual 8.5 to 8.8s" under 10 seconds, else "usual 17s to 19s".</summary>
    public static string PartUsual(Usual u) => u.Hi < 9.95 ? Lang.T($"usual {u.Lo:0.0} to {u.Hi:0.0}s") : Lang.T($"usual {AppRange(u)}");

    /// <summary>An app's usual launch: "8.2 to 9.6s" under 10 seconds, else "17s to 19s".
    /// A low end under 10 seconds keeps its tenth ("0.4s to 12s", never "0s to 12s").</summary>
    public static string AppRange(Usual u) => u.Hi < 9.95 ? Lang.T($"{u.Lo:0.0} to {u.Hi:0.0}s") : Lang.T($"{PartLength(u.Lo)} to {Length(u.Hi)}");

    // ── Against the usual ──

    /// <summary>The opened start's line beside its length.</summary>
    public static string Verdict(StartView s) => s.Verdict switch
    {
        StartVerdict.AfterUpdate => Lang.T("Restart after an update, not counted in your usual"),
        StartVerdict.NotRecorded => NotRecordedWhy,
        StartVerdict.Pending => PendingWhy,
        StartVerdict.Partial => PartialWhy,
        StartVerdict.Longer when s.Usual is { } u => Lang.T($"{Length(s.Measure!.Value - u.Hi)} longer than your usual {Range(u)}"),
        StartVerdict.Quicker when s.Usual is { } u => Lang.T($"Quicker than your usual {Range(u)}"),
        StartVerdict.Within when s.Usual is { } u => Lang.T($"Within your usual {Range(u)}"),
        StartVerdict.Above when s.Usual is { } u => Lang.T($"A little longer than your usual {Range(u)}"),
        _ => TooSoon(s.Kind, s.KindCount),
    };

    /// <summary>A hover's word beside the length (a partial start's figure is what's on record so far).</summary>
    public static string Against(StartView s) => s.Verdict switch
    {
        StartVerdict.AfterUpdate => Lang.T("to start"),
        StartVerdict.Pending => Lang.T("so far"),
        StartVerdict.Partial => Lang.T("on record"),
        StartVerdict.Longer when s.Usual is { } u => Lang.T($"against your usual {Range(u)}"),
        StartVerdict.Quicker when s.Usual is { } u => Lang.T($"quicker than your usual {Range(u)}"),
        StartVerdict.Within when s.Usual is { } u => Lang.T($"within your usual {Range(u)}"),
        StartVerdict.Above when s.Usual is { } u => Lang.T($"a little longer than your usual {Range(u)}"),
        _ => "",
    };

    /// <summary>"Too soon to say. It needs 5 full starts and has 2 so far."</summary>
    public static string TooSoon(StartKind kind, int has) => (kind, has) switch
    {
        (StartKind.Fast, 0) => Lang.T($"Too soon to say. It needs {StartupReport.UsualNeeds} Fast Startups and has none so far."),
        (StartKind.Fast, 1) => Lang.T($"Too soon to say. It needs {StartupReport.UsualNeeds} Fast Startups and has 1 so far."),
        (StartKind.Fast, _) => Lang.T($"Too soon to say. It needs {StartupReport.UsualNeeds} Fast Startups and has {has} so far."),
        (StartKind.Hibernate, 0) => Lang.T($"Too soon to say. It needs {StartupReport.UsualNeeds} starts from hibernation and has none so far."),
        (StartKind.Hibernate, 1) => Lang.T($"Too soon to say. It needs {StartupReport.UsualNeeds} starts from hibernation and has 1 so far."),
        (StartKind.Hibernate, _) => Lang.T($"Too soon to say. It needs {StartupReport.UsualNeeds} starts from hibernation and has {has} so far."),
        (_, 0) => Lang.T($"Too soon to say. It needs {StartupReport.UsualNeeds} full starts and has none so far."),
        (_, 1) => Lang.T($"Too soon to say. It needs {StartupReport.UsualNeeds} full starts and has 1 so far."),
        _ => Lang.T($"Too soon to say. It needs {StartupReport.UsualNeeds} full starts and has {has} so far."),
    };

    /// <summary>A start with none of its parts on record (Windows' logs no longer had them, or never wrote them).</summary>
    public static string NotRecordedWhy => Lang.T("How long this start took isn't on record");

    /// <summary>This start, minutes old: its parts are still being read.</summary>
    public static string PendingWhy => Lang.T("The rest of this start shows here in a few minutes");

    /// <summary>A start with only some of its parts on record.</summary>
    public static string PartialWhy => Lang.T("Only part of this start is on record");

    /// <summary>The dashed box's tooltip on the opened start.</summary>
    public static string UsualBox(StartKind kind, Usual u) => kind switch
    {
        StartKind.Fast => Lang.T($"The dashed box is your usual Fast Startup, {Range(u)}"),
        StartKind.Hibernate => Lang.T($"The dashed box is your usual start from hibernation, {Range(u)}"),
        _ => Lang.T($"The dashed box is your usual full start, {Range(u)}"),
    };

    // ── The apps that start with Windows ──

    /// <summary>"Steam took 19s to start, against its usual 8.2 to 9.6s".</summary>
    public static string AppLonger(AppView a) => Lang.T($"{a.Name} took {PartLength(a.Seconds)} to start, against its usual {AppRange(a.Usual!)}");

    /// <summary>A hover's line about an app that took longer: "Steam, usual 8.2 to 9.6s" (its figure at the right).</summary>
    public static string AppShort(AppView a) => Lang.T($"{a.Name}, usual {AppRange(a.Usual!)}");

    /// <summary>An app's usual launch beside its name in Starts with Windows: "usually 9.1s".</summary>
    public static string AppUsually(Usual u) => Lang.T($"usually {PartLength((u.Lo + u.Hi) / 2)}");

    /// <summary>None of the start's apps took longer than its usual.</summary>
    public static string NoAppLonger => Lang.T("No app took longer than usual to start.");

    /// <summary>None of the start's apps has a usual yet: "Too soon to say. Each app needs 5 starts on record, and has 2 so far."</summary>
    public static string AppsTooSoon(int has) => has <= 1
        ? Lang.T($"Too soon to say. Each app needs {StartupReport.UsualNeeds} starts on record, and has 1 so far.")
        : Lang.T($"Too soon to say. Each app needs {StartupReport.UsualNeeds} starts on record, and has {has} so far.");

    /// <summary>"6 more" under the opened start's list of apps.</summary>
    public static string More(int n) => n == 1 ? Lang.T("1 more") : Lang.T($"{n} more");

    // ── When ──

    /// <summary>"Today, 7:47 AM", "Yesterday, 1:22 PM", "Tue 24 Nov, 1:22 PM".</summary>
    public static string When(DateTime t, DateTime today)
    {
        string time = TimeText.Time(t);
        return t.Date == today ? Lang.T($"Today, {time}") : t.Date == today.AddDays(-1) ? Lang.T($"Yesterday, {time}") : $"{t:ddd d MMM}, {time}";
    }

    /// <summary>"Today", "Yesterday", "Tue 24 Nov".</summary>
    public static string Day(DateTime day, DateTime today) =>
        day.Date == today ? Lang.T("Today") : day.Date == today.AddDays(-1) ? Lang.T("Yesterday") : day.ToString("ddd d MMM");

    // ── What Windows reported ──

    /// <summary>The opened start's line about something Windows named as slower than its usual.</summary>
    public static string Reported(SlowItem item)
    {
        string by = Length(item.SlowerMs / 1000.0);
        return item.Event switch
        {
            101 => Lang.T($"{item.Name} took {by} longer than its own usual to start"),
            102 => Lang.T($"The {item.Name} driver took {by} longer than its own usual to start"),
            103 => Lang.T($"The {item.Name} service took {by} longer than its own usual to start"),
            109 => Lang.T($"{item.Name} took {by} longer than its own usual to get ready"),
            _ => Lang.T($"{WindowsPart(item.Event)} took {by} longer than its usual"),
        };
    }

    /// <summary>The grey line under it: why Windows names it, for what it named.</summary>
    public static string? ReportedWhy(SlowItem item) => item.Event switch
    {
        101 => Lang.T("Windows names an app when it starts slower than it usually does"),
        102 => Lang.T("Windows names a driver when it starts slower than it usually does"),
        103 => Lang.T("Windows names a service when it starts slower than it usually does"),
        _ => null,
    };

    /// <summary>A hover's short line: "Hex started slower than its usual".</summary>
    public static string ReportedShort(SlowItem item) => item.Name.Length > 0
        ? Lang.T($"{item.Name} started slower than its usual")
        : Lang.T($"{WindowsPart(item.Event)} was slower than its usual");

    /// <summary>"+21s".</summary>
    public static string Slower(SlowItem item) => $"+{Length(item.SlowerMs / 1000.0)}";

    /// <summary>A part of Windows' own start that Windows names by itself (events 104 to 110 without a name).</summary>
    private static string WindowsPart(int id) => id switch
    {
        104 => Lang.T("Windows' loading of start-up files"),
        105 => Lang.T("Windows' own start"),
        106 => Lang.T("Windows' loading of files in the background"),
        107 => Lang.T("Applying this PC's group policy"),
        108 => Lang.T("Applying your group policy"),
        110 => Lang.T("Windows' session start"),
        _ => Lang.T("A part of Windows' start"),
    };

    // ── Counts ──

    public static string Starts(int n) => n == 1 ? Lang.T("1 start") : Lang.T($"{n} starts");

    public static string AfterUpdates(int n) => n == 0 ? Lang.T("None after an update") : n == 1 ? Lang.T("1 after an update") : Lang.T($"{n} after an update");

    /// <summary>A week's hover line: "5 starts", "5 starts, 1 after an update".</summary>
    public static string WeekStarts(int n, int updates) => updates == 0 ? Starts(n)
        : n == 1 ? Lang.T("1 start, after an update")
        : updates == 1 ? Lang.T($"{n} starts, 1 after an update") : Lang.T($"{n} starts, {updates} after an update");

    /// <summary>"Week of 24 Nov".</summary>
    public static string WeekOf(DateTime start) => Lang.T($"Week of {start:d MMM}");

    // ── The period's sentence ──

    /// <summary>"the 7 days before" and the others, for "Full starts took 3s longer than in …".</summary>
    public static string Before(ReportRange unit, DateTime from, DateTime to) => unit switch
    {
        ReportRange.Day => Lang.T("the day before"),
        ReportRange.Week => Lang.T("the 7 days before"),
        ReportRange.Month => Lang.T("the 30 days before"),
        ReportRange.Year => Lang.T("the 12 months before"),
        _ => (int)Math.Round((to - from).TotalDays) is var d && d <= 1 ? Lang.T("the day before") : Lang.T($"the {d} days before"),
    };

    /// <summary>The Home line, when the day's first full start was clearly longer than its usual.</summary>
    public static string HomeLine(double seconds, Usual u) => Lang.T($"The PC took {Length(seconds)} to start, against your usual {Range(u)}");

    /// <summary>The What to try steps for a longer start: the startup apps, then each app that took longer, then each app Windows named.</summary>
    public static string TryStartupApps => Lang.T("Turn off the apps you don't need the moment Windows starts. They still open when you start them yourself.");
    public static string TryAppUpdate(string app) => Lang.T($"See whether {app} has an update. Windows reported it took longer than usual to start.");
    public static string TryAppLonger(string app) => Lang.T($"See whether {app} has an update. It took longer than usual to start.");
}
