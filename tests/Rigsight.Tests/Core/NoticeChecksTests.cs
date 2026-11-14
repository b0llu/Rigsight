using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Notices;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Core;

/// <summary>
/// The notifications round (2026-11-07, mock-up LWycKU9ZC8jzeVH8bVjPWm): each check sends what's new once, in its page's
/// words with the unit, and stays quiet otherwise; the recent list keeps what was sent; the words follow WORDING.md.
/// </summary>
public sealed class NoticeChecksTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 20, 41, 0);

    /// <summary>Nothing said before.</summary>
    private static (DateTime At, string? Value)? Never(string key) => null;

    private static Func<string, (DateTime At, string? Value)?> SaidOnly(Dictionary<string, (DateTime, string?)> said) =>
        key => said.TryGetValue(key, out var v) ? v : null;

    // ── Words ──

    [Fact]
    public void The_new_notifications_say_what_happened_with_the_unit_and_no_colon()
    {
        Units.Fahrenheit = false;
        var stopped = NoticeWords.FanStopped("GPU Fan 1", true, 4, new DateTime(2026, 11, 7, 20, 37, 0), 74);
        Assert.Equal("Your GPU fan stopped", stopped.Title);
        Assert.Equal("It showed 0 rpm for 4 minutes from 8:37 PM with the GPU at 74°C, though it always turns at that heat. Check it isn't blocked or unplugged.", stopped.Body);

        var hot = NoticeWords.Hotter(new UsualHeat("Crimson Desert", "GPU", 78.2, 70.6, 69.1, 72.3, 95, 410, 6, 41.2, 40.8, 300, 302, EffectTone.Worse));
        Assert.Equal("Crimson Desert ran 7°C hotter than usual on the GPU", hot.Title);
        Assert.Equal("It averaged 78°C against its usual 69–72°C, at the same power. With nothing running it sits at its usual 41°C.", hot.Body);

        var slowed = NoticeWords.Slowed(new Throttling(12, 9, 83));
        Assert.Equal("Your GPU slowed itself for 12 minutes to stay cool", slowed.Title);
        Assert.Equal("Its clocks fell about 9% once it reached 83°C.", slowed.Body);

        Assert.Equal(new NoticeText("Your C drive is almost full", "It has 8.0 GB left of 476.0 GB. Windows needs free space to install updates."),
            NoticeWords.DriveFull('C', 8L << 30, 476L << 30, windowsDrive: true));
        Assert.Equal("It has 8.0 GB left of 476.0 GB.", NoticeWords.DriveFull('D', 8L << 30, 476L << 30, windowsDrive: false).Body);

        Assert.Equal("Your PC usually has none.", NoticeWords.MoreProblems("3 graphics driver resets", "in the last 7 days", 0, 4, 1).Body);
        Assert.Equal("3 graphics driver resets in the last 7 days", NoticeWords.MoreProblems("3 graphics driver resets", "in the last 7 days", 0, 4, 1).Title);

        foreach (var t in new[] { stopped, hot, slowed })
        {
            Assert.DoesNotContain(":", t.Title);
            Assert.DoesNotContain("Click", t.Body);
        }
    }

    [Fact]
    public void A_pages_line_gets_its_unit_and_the_temperature_alert_says_the_games_usual()
    {
        Units.Fahrenheit = false;
        Assert.Equal("Crimson Desert runs 6°C hotter on the GPU since driver 581.57", NoticeWords.WithUnit("Crimson Desert runs 6° hotter on the GPU since driver 581.57"));
        Assert.Equal("78°C stays 78°C", NoticeWords.WithUnit("78°C stays 78°"));
        Assert.Equal("In this game it usually sits at 69–72°C.", NoticeWords.UsualInGame(69.2, 71.8));
        Assert.Equal("In this game it usually sits at 70°C.", NoticeWords.UsualInGame(69.8, 70.2));
    }

    [Fact]
    public void The_weekly_recap_compares_with_the_7_days_before_only_when_it_differs_by_half_an_hour()
    {
        var (title, body) = NoticeWords.Weekly(31 * 3600 + 20 * 60, "Crimson Desert", 9 * 3600 + 5 * 60, 35 * 3600 + 20 * 60);
        Assert.Equal("31h 20m active in the last 7 days", title);
        Assert.Equal("Mostly Crimson Desert, for 9 hours and 5 minutes. That's 4 hours less than the 7 days before.", body);
        Assert.Equal("Mostly Crimson Desert, for 9 hours and 5 minutes.", NoticeWords.Weekly(31 * 3600, "Crimson Desert", 9 * 3600 + 5 * 60, 31 * 3600 + 600).Body);
    }

    // ── Drives ──

    [Fact]
    public void A_failing_drive_is_a_problem_said_once()
    {
        var bad = new DriveHealthInfo { Name = "Samsung SSD 980 1TB", Status = "Bad" };
        var found = NoticeChecks.Drives([bad], new Dictionary<string, double>(), new AlertSettings(), Never);
        var c = Assert.Single(found);
        Assert.Equal(NoticeLevel.Problem, c.Level);
        Assert.Equal("Your Samsung SSD 980 1TB reports a failure", c.Text.Title);
        Assert.Equal("storage", c.Page);
        var said = new Dictionary<string, (DateTime, string?)> { [c.Key] = (Now, null) };
        Assert.Empty(NoticeChecks.Drives([bad], new Dictionary<string, double>(), new AlertSettings(), SaidOnly(said)));
        Assert.Empty(NoticeChecks.Drives([bad], new Dictionary<string, double>(), new AlertSettings { DriveHealth = false }, Never));
    }

    [Fact]
    public void Bad_sectors_are_said_the_first_time_and_again_only_when_the_count_grows()
    {
        var drive = new DriveHealthInfo { Name = "WDC WD20EZRZ", Status = "Caution", ReallocatedSectors = 3 };
        var first = Assert.Single(NoticeChecks.Drives([drive], new Dictionary<string, double>(), new AlertSettings(), Never));
        Assert.Equal(NoticeLevel.Worth, first.Level);
        Assert.Equal("It has 3 reallocated sectors. Bad sectors are an early warning, so keep a backup.", first.Text.Body);
        Assert.Equal("3", first.Value);

        var said = new Dictionary<string, (DateTime, string?)> { [first.Key] = (Now, "3") };
        Assert.Empty(NoticeChecks.Drives([drive], new Dictionary<string, double>(), new AlertSettings(), SaidOnly(said)));

        drive.PendingSectors = 2;
        var more = Assert.Single(NoticeChecks.Drives([drive], new Dictionary<string, double>(), new AlertSettings(), SaidOnly(said)));
        Assert.Equal("It now has 3 reallocated, 2 pending sectors, up from 3. Bad sectors are an early warning, so keep a backup.", more.Text.Body);
        Assert.Equal("5", more.Value);
    }

    [Fact]
    public void A_healthy_drive_says_nothing_and_a_worn_ssd_is_said_once()
    {
        var good = new DriveHealthInfo { Name = "Samsung SSD 990 PRO", Status = "Good" };
        // Its first life reading is only noted (where it stands), not said.
        var first = Assert.Single(NoticeChecks.Drives([good], new Dictionary<string, double> { ["Samsung SSD 990 PRO"] = 87 }, new AlertSettings(), Never));
        Assert.True(first.Quiet);
        Assert.Equal(("drive-life|Samsung SSD 990 PRO", "100"), (first.Key, first.Value));
        var worn = Assert.Single(NoticeChecks.Drives([good], new Dictionary<string, double> { ["Samsung SSD 990 PRO"] = 8 }, new AlertSettings(), Never), c => !c.Quiet);
        Assert.Equal("It has 8% of its rated life left. Keep a backup of what's on it.", worn.Text.Body);
    }

    [Fact]
    public void An_ssd_passing_half_and_a_quarter_of_its_life_is_said_once_each_and_opens_the_drive()
    {
        var ssd = new DriveHealthInfo { Name = "KINGSTON SA400", Status = "Good", Key = "50026B77", Life = 49 };
        var said = new Dictionary<string, (DateTime, string?)> { ["drive-life|KINGSTON SA400"] = (Now.AddDays(-40), "100") };
        var half = Assert.Single(NoticeChecks.Drives([ssd], new Dictionary<string, double>(), new AlertSettings(), SaidOnly(said), Now, _ => 8));
        Assert.False(half.Quiet);
        Assert.Equal("Your KINGSTON SA400 has half its rated life left", half.Text.Title);
        Assert.Equal("It uses about 8% a year, so at this pace that's around 6 more years.", half.Text.Body);
        Assert.Equal(("drive:50026B77", "50"), (half.Arg, half.Value));
        said[half.Key] = (Now, half.Value);
        Assert.Empty(NoticeChecks.Drives([ssd], new Dictionary<string, double>(), new AlertSettings(), SaidOnly(said), Now));
        var quarter = Assert.Single(NoticeChecks.Drives([new DriveHealthInfo { Name = "KINGSTON SA400", Status = "Good", Life = 24 }], new Dictionary<string, double>(),
            new AlertSettings(), SaidOnly(said), Now));
        Assert.Equal("Your KINGSTON SA400 has a quarter of its rated life left", quarter.Text.Title);
        Assert.Equal("It has 24% of its rated life left, by its own count.", quarter.Text.Body);
        Assert.Empty(NoticeChecks.Drives([ssd], new Dictionary<string, double>(), new AlertSettings { DriveLifeMilestones = false }, SaidOnly(said), Now));
    }

    [Fact]
    public void A_drive_at_its_own_warning_point_is_a_problem_once_a_day()
    {
        var hot = new DriveHealthInfo { Name = "Samsung SSD 980", Status = "Good", Temperature = 72, TemperatureWarning = 70 };
        var c = Assert.Single(NoticeChecks.Drives([hot], new Dictionary<string, double>(), new AlertSettings(), Never, Now));
        Assert.Equal(NoticeLevel.Problem, c.Level);
        Assert.Equal(("Your Samsung SSD 980 is running hot", "It reached 72°C, above its own 70°C warning point."), (c.Text.Title, c.Text.Body));
        var said = new Dictionary<string, (DateTime, string?)> { [c.Key] = (Now, null) };
        Assert.Empty(NoticeChecks.Drives([hot], new Dictionary<string, double>(), new AlertSettings(), SaidOnly(said), Now));
        Assert.Single(NoticeChecks.Drives([hot], new Dictionary<string, double>(), new AlertSettings(), SaidOnly(said), Now.AddDays(1)));
        // Under its warning point, or a drive that gives none: nothing.
        Assert.Empty(NoticeChecks.Drives([new DriveHealthInfo { Name = "A", Status = "Good", Temperature = 69, TemperatureWarning = 70 }], new Dictionary<string, double>(), new AlertSettings(), Never, Now));
        Assert.Empty(NoticeChecks.Drives([new DriveHealthInfo { Name = "B", Status = "Good", Temperature = 90 }], new Dictionary<string, double>(), new AlertSettings(), Never, Now));
        Assert.Empty(NoticeChecks.Drives([hot], new Dictionary<string, double>(), new AlertSettings { DriveTemperature = false }, Never, Now));
    }

    [Fact]
    public void A_chip_that_runs_hotter_than_it_used_to_is_said_at_most_once_a_month_and_only_further_up()
    {
        var row = new WearRow { Part = "cpu", Name = "CPU", State = WearState.Moved, Now = 18, Then = 14, ThenDate = new DateTime(2026, 9, 28) };
        var c = Assert.Single(NoticeChecks.Cooling([row, new WearRow { Part = "gpu", Name = "GPU", State = WearState.Steady, Now = 21, Then = 20 }], new AlertSettings(), Never, Now));
        Assert.Equal("Your CPU runs hotter under load than it used to", c.Text.Title);
        Assert.Equal("At the same load it now sits 18°C above its resting temperature, against 14°C in late September.", c.Text.Body);
        Assert.Equal(("temperatures", "wear|cpu", "18.0"), (c.Page, c.Key, c.Value));
        var said = new Dictionary<string, (DateTime, string?)> { [c.Key] = (Now, c.Value) };
        Assert.Empty(NoticeChecks.Cooling([row with { Now = 22 }], new AlertSettings(), SaidOnly(said), Now.AddDays(10)));
        Assert.Empty(NoticeChecks.Cooling([row with { Now = 19 }], new AlertSettings(), SaidOnly(said), Now.AddDays(40)));
        Assert.Single(NoticeChecks.Cooling([row with { Now = 21 }], new AlertSettings(), SaidOnly(said), Now.AddDays(40)));
        // Cooler than it used to: not a card.
        Assert.Empty(NoticeChecks.Cooling([row with { Now = 10 }], new AlertSettings(), Never, Now));
        Assert.Empty(NoticeChecks.Cooling([row], new AlertSettings { PartsChanged = false }, Never, Now));
    }

    [Fact]
    public void A_drive_is_almost_full_under_5_percent_and_20_GB_once_a_day()
    {
        long gb = 1L << 30;
        var s = new AlertSettings();
        // 8 GB of 476: both under. 30 GB of 476: over 20 GB. 15 GB of 200: 7.5%, over 5%.
        var found = NoticeChecks.Space([('C', 8 * gb, 476 * gb), ('D', 30 * gb, 476 * gb), ('E', 15 * gb, 200 * gb)], 'C', s, Never, Now);
        var c = Assert.Single(found);
        Assert.Equal("Your C drive is almost full", c.Text.Title);
        Assert.Equal("C", c.Arg);
        Assert.Equal("drive-full|C|2026-11-07", c.Key);
        var said = new Dictionary<string, (DateTime, string?)> { [c.Key] = (Now, null) };
        Assert.Empty(NoticeChecks.Space([('C', 8 * gb, 476 * gb)], 'C', s, SaidOnly(said), Now));
        Assert.Single(NoticeChecks.Space([('C', 8 * gb, 476 * gb)], 'C', s, SaidOnly(said), Now.AddDays(1)));
    }

    // ── Memory, startup, changes ──

    [Fact]
    public void An_app_is_said_only_when_its_memory_rose_for_3_hours_without_coming_back_down()
    {
        var s = new AlertSettings { MemoryGrowth = true };
        long gb = 1L << 30;
        List<(DateTime, long)> Rising() => [.. Enumerable.Range(0, 181).Select(m => (Now.AddMinutes(m - 180), (long)(2.1 * gb + m * (7.1 * gb / 180))))];
        List<(DateTime, long)> UpAndDown() => [.. Enumerable.Range(0, 181).Select(m => (Now.AddMinutes(m - 180), m is > 60 and < 120 ? 9L * gb : 3L * gb))];
        var found = NoticeChecks.Memory(new Dictionary<(string, string), List<(DateTime, long)>>
        {
            [("chrome.exe", "Google Chrome")] = Rising(),
            [("game.exe", "A game")] = UpAndDown(),
        }, s, Never, Now);
        var c = Assert.Single(found);
        Assert.Equal("Google Chrome keeps using more memory", c.Text.Title);
        Assert.Equal("It has gone from 2.1 GB to 9.2 GB in 3 hours, and hasn't come back down.", c.Text.Body);
        Assert.Equal(NoticeLevel.Recap, c.Level);
        Assert.Empty(NoticeChecks.Memory(new Dictionary<(string, string), List<(DateTime, long)>> { [("chrome.exe", "Google Chrome")] = Rising() }, new AlertSettings(), Never, Now));
    }

    [Fact]
    public void A_new_startup_app_is_said_once_in_the_timelines_words()
    {
        var change = new SystemChange(Now.AddMinutes(-30), ChangeKind.Startup, "FACEIT now starts with Windows") { Subject = "startup:FACEIT", Now = Inventory.On };
        var off = new SystemChange(Now.AddMinutes(-20), ChangeKind.Startup, "Steam no longer starts with Windows") { Subject = "startup:Steam", Now = Inventory.Off };
        var c = Assert.Single(NoticeChecks.Startup([change, off], true, Never, Now));
        Assert.Equal("FACEIT now starts with Windows", c.Text.Title);
        Assert.Equal("It was added at 8:11 PM.", c.Text.Body);
        Assert.Empty(NoticeChecks.Startup([change], false, Never, Now));
        var said = new Dictionary<string, (DateTime, string?)> { [c.Key] = (Now, null) };
        Assert.Empty(NoticeChecks.Startup([change], true, SaidOnly(said), Now));
    }

    // ── After a game ──

    [Fact]
    public void After_a_game_hotter_than_usual_and_the_gpu_slowing_give_a_summary_sentence_and_a_card_each()
    {
        Units.Fahrenheit = false;
        var usual = new PcUsual { Parts = [new UsualHeat("Crimson Desert", "GPU", 78.2, 70.6, 69.1, 72.3, 95, 410, 6, 41.2, 40.8, 300, 302, EffectTone.Worse)] };
        var (sentences, cards) = NoticeChecks.AfterGame(usual, new Throttling(12, 9, 83), "Crimson Desert", "crimsondesert.exe", new AlertSettings(), Never, Now);
        Assert.Equal(["The GPU ran 7°C hotter than usual at the same power.", "The GPU slowed itself for 12 minutes to stay cool."], sentences);
        Assert.Equal(["usual-heat|Crimson Desert|2026-11-07", "throttle|2026-11-07"], cards.Select(c => c.Key));
        Assert.Equal("crimsondesert.exe", cards[0].Arg);

        // As usual: nothing.
        var same = new PcUsual { Parts = [new UsualHeat("Crimson Desert", "GPU", 71, 70.6, 69.1, 72.3, 95, 410, 6, 41.2, 40.8, 300, 302, EffectTone.Same)] };
        var none = NoticeChecks.AfterGame(same, null, "Crimson Desert", "crimsondesert.exe", new AlertSettings(), Never, Now);
        Assert.Empty(none.Sentences);
        Assert.Empty(none.Cards);

        // Switched off, or said already today: nothing.
        Assert.Empty(NoticeChecks.AfterGame(usual, new Throttling(12, 9, 83), "Crimson Desert", "x.exe", new AlertSettings { HotterThanUsual = false, GpuSlowing = false }, Never, Now).Cards);
        var said = new Dictionary<string, (DateTime, string?)> { ["usual-heat|Crimson Desert|2026-11-07"] = (Now, null), ["throttle|2026-11-07"] = (Now, null) };
        Assert.Empty(NoticeChecks.AfterGame(usual, new Throttling(12, 9, 83), "Crimson Desert", "x.exe", new AlertSettings(), SaidOnly(said), Now).Cards);
    }

    // ── The recent list ──

    [Fact]
    public void The_recent_list_keeps_30_days_counts_shown_cards_and_remembers_what_was_said()
    {
        using var db = RigsightDb.OpenWriter(Path.Combine(TestEnvironment.NewFolder("notices"), "rigsight.db"));
        db.SaveNotice(new NoticeRecord(Now.AddDays(-40), "Recap", NoticeLevel.Recap, "Old", "Gone", "reports", null, null, null, true));
        db.SaveNotice(new NoticeRecord(Now.AddHours(-2), "Usual", NoticeLevel.Worth, "Hotter", "Body", "games", "x.exe", null, "usual-heat|X|2026-11-07", true));
        db.SaveNotice(new NoticeRecord(Now.AddHours(-1), "Space", NoticeLevel.Worth, "Full", "Body", "storage", "C", null, null, false));
        db.SaveNotice(new NoticeRecord(Now, "FanStopped", NoticeLevel.Problem, "Your GPU fan stopped", "Body", "fans", null, null, null, true));

        var list = db.GetNotices(TimeUtil.ToUnix(Now - RigsightDb.NoticesKept));
        Assert.Equal(["Your GPU fan stopped", "Full", "Hotter"], list.Select(n => n.Title));
        Assert.False(list[1].Shown);
        Assert.Equal(1, db.CountShownNotices(TimeUtil.ToUnix(Now.Date), NoticeLevel.Worth, NoticeLevel.Recap));

        Assert.Null(db.Said("drive-sectors|WDC"));
        db.MarkSaid("drive-sectors|WDC", Now, "3");
        Assert.Equal("3", db.Said("drive-sectors|WDC")?.Value);
        db.MarkSaid("drive-sectors|WDC", Now.AddDays(1), "5");
        Assert.Equal("5", db.Said("drive-sectors|WDC")?.Value);
    }

    [Fact]
    public void New_notifications_are_on_except_the_three_quiet_ones_and_five_cards_a_day()
    {
        var a = new AlertSettings();
        Assert.True(a.FanStops && a.DriveHealth && a.HotterThanUsual && a.GpuSlowing && a.FansSlower && a.MoreProblems && a.WorseChanges && a.DriveFull);
        Assert.False(a.WeeklyRecap || a.MemoryGrowth || a.NewStartupApps);
        Assert.Equal(5, a.CardsPerDay);
    }
}
