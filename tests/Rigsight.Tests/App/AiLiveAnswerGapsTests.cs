using System.Globalization;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Data;
using Rigsight.Core.Export;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;

namespace Rigsight.Tests.App;

/// <summary>
/// What the look-ups sent in ten live answers (3 to 6 Nov 2026) that the model then got wrong: a dashboard asked about by
/// its name with no tiles to add, a week's calm said with two minutes of history, dates and numbers in the PC's region,
/// WebView2 with no app named, and a crash "4 seconds after starting" in an app that "had been running 4m".
/// </summary>
public sealed class AiLiveAnswerGapsTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"rigsight-live-gaps-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }

    private AiTools Tools(Action<RigsightSettings>? set = null)
    {
        var s = new RigsightSettings();
        set?.Invoke(s);
        return new AiTools(s, _db);
    }

    // ── 1. Dashboards ────────────────────────────────────────────────────

    [Fact]
    public void A_dashboard_looked_up_by_its_name_lists_the_tiles_that_could_be_added_too()
    {
        using (RigsightDb.OpenWriter(_db)) { }
        var tools = Tools(s => s.CustomPages.Add(new CustomPageConfig { Name = "Gaming", Tiles = [new TileConfig { Kind = "gpu-gauge" }] }));
        tools.SensorsNow = () => "  GPU: Core 64 °C\n";

        string said = tools.Run("get_dashboard", """{"name":"Gaming"}""");

        Assert.Contains("Core 64 °C", said);
        Assert.Contains("Tiles a dashboard can have", said);
        Assert.Contains("Connection today (Whether the internet dropped today)", said);
    }

    // ── 2. Recording that began inside the period ────────────────────────

    [Fact]
    public void Today_two_minutes_after_recording_began_says_so_and_calls_nothing_usual()
    {
        // 5 Nov 2026: "No PC problems in the last 7 days" with 2 minutes of history, read as a calm week.
        var began = DateTime.Now.AddMinutes(-2);
        using (var w = RigsightDb.OpenWriter(_db))
            for (int i = 0; i < 2; i++) w.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(began) + i * 60, CpuTemp = 50, CpuTempMax = 52, GpuTemp = 40, ActiveSec = 60 });

        string said = Tools().Run("get_today", "{}");

        Assert.Contains("RECORDING BEGAN ", said);
        Assert.Contains($"at {began.ToString("h:mm tt", CultureInfo.InvariantCulture)}, so there's 2m of history", said);
        Assert.Contains("There's no usual for this PC to compare with yet", said);
        Assert.Contains("Windows' own crash records haven't been read yet", said);
        Assert.DoesNotContain("No PC problems", said);
        Assert.DoesNotContain("as usual", said);
        // Said once, at the top: the report under it doesn't say it again.
        Assert.Equal(1, said.Split("RECORDING BEGAN").Length - 1);
    }

    [Fact]
    public void Once_Windows_crash_logs_are_read_no_problems_stands_with_where_it_comes_from()
    {
        using (var w = RigsightDb.OpenWriter(_db))
        {
            w.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(DateTime.Now.AddMinutes(-5)), CpuTemp = 50, ActiveSec = 60 });
            w.SetMeta(RigsightDb.CrashScannedKey, TimeUtil.ToUnix(DateTime.Now).ToString(CultureInfo.InvariantCulture));
        }

        string said = Tools().Run("get_today", "{}");

        Assert.Contains("No PC problems", said);
        Assert.Contains("Crashes, freezes and shut-offs come from Windows' own records, which reach back before recording began.", said);
    }

    [Fact]
    public void A_report_for_days_before_recording_began_says_nothing_was_recorded_in_it()
    {
        using (var w = RigsightDb.OpenWriter(_db))
            w.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(DateTime.Today.AddHours(-1)), CpuTemp = 50, ActiveSec = 60 });
        string day = DateTime.Today.AddDays(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        string said = Tools().Run("get_report", $$"""{"from":"{{day}}","to":"{{day}}"}""");

        Assert.StartsWith("RECORDING BEGAN on ", said);
        Assert.Contains("after this period: nothing was recorded in it", said);
    }

    // ── 3. One format whatever the region ────────────────────────────────

    [Fact]
    public void Look_up_text_is_written_one_way_on_a_Spanish_PC()
    {
        var at = new DateTime(2026, 11, 4, 21, 59, 0);
        using (var w = RigsightDb.OpenWriter(_db))
            w.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(at), CpuTempMax = 60, GpuTempMax = 70, CpuPower = 45, RamUsed = 2.4, ActiveSec = 60 });
        var before = CultureInfo.CurrentCulture;
        var spanish = CultureInfo.GetCultureInfo("es-ES");
        string said;
        try
        {
            CultureInfo.CurrentCulture = spanish;
            said = Tools().Run("get_moment", """{"time":"2026-11-04 21:59"}""");
            // The thread's own formats come back after the look-up.
            Assert.Same(spanish, CultureInfo.CurrentCulture);
        }
        finally { CultureInfo.CurrentCulture = before; }

        Assert.Contains("Around Wednesday 4 Nov 2026, 9:59 PM", said);
        Assert.Contains("  9:59 PM: ", said);
        Assert.Contains("memory 2.4 GB", said);
        Assert.DoesNotContain("2,4", said);
        Assert.DoesNotContain("p. m.", said);
        Assert.DoesNotContain("mié", said);
    }

    [Fact]
    public void The_fixed_formats_are_English_whatever_the_region()
    {
        var c = AiText.Culture;
        var t = new DateTime(2026, 11, 4, 22, 13, 0);
        Assert.Equal("Wed 4 Nov 10:13 PM", t.ToString("ddd d MMM h:mm tt", c));
        Assert.Equal("10:13:00 PM", t.ToString("T", c));
        Assert.Equal("2.4 GB", string.Format(c, "{0:0.0} GB", 2.4));
        Assert.Equal("1,234", 1234.ToString("N0", c));
        // The clock picked in Settings doesn't reach the look-ups either: 24-hour there is still 12-hour here.
        Assert.Equal("10:13 PM", AiText.Fixed(() => TimeText.Time(t)));
    }

    // ── 4. A helper's host apps ──────────────────────────────────────────

    [Fact]
    public void A_helper_like_WebView2_is_named_by_the_apps_that_started_it()
    {
        static RunningNow.Proc P(int pid, string exe, int parent, long created) => new(pid, created, exe, 0, 10, 1, parent);
        var procs = new[]
        {
            P(100, "Widgets.exe", 4, 1), P(200, "msedgewebview2.exe", 100, 2), P(201, "msedgewebview2.exe", 200, 3), P(202, "msedgewebview2.exe", 200, 3),
            P(300, "ms-teams.exe", 4, 1), P(400, "msedgewebview2.exe", 300, 5),
            P(500, "msedgewebview2.exe", 999, 5),                                // its app has closed
            P(700, "notepad.exe", 4, 50), P(600, "msedgewebview2.exe", 700, 10), // a later program took a closed one's id
            P(800, "chrome.exe", 4, 1), P(801, "chrome.exe", 800, 2),           // not a helper
        };

        var hosts = RunningNow.HelperHosts(procs, p => Path.GetFileNameWithoutExtension(p.Exe));

        Assert.Equal(["Widgets", "ms-teams"], hosts["msedgewebview2.exe"]);
        Assert.False(hosts.ContainsKey("chrome.exe"));

        var app = new RunningApp("msedgewebview2.exe", "Microsoft Edge WebView2", @"C:\Program Files (x86)\Microsoft\EdgeWebView\msedgewebview2.exe", 25, 900, 1, 0,
            HasWindow: false, InFront: false, AppCategory.Other, UsedBy: ["Widgets", "Microsoft Teams"]);
        string text = new RunningSnapshot(DateTime.Now, [app]).Text();
        Assert.Contains("used by Widgets, Microsoft Teams", text);
        Assert.DoesNotContain("no window, working in the background", text);
    }

    // ── 5. Two run times in one crash record ─────────────────────────────

    private static CrashStory Story(double? ran, double? used) => new(
        new CrashEvent
        {
            Ts = TimeUtil.ToUnix(new DateTime(2026, 11, 5, 21, 12, 0)), Kind = CrashKind.AppCrash, AppExe = "Medal.exe", Module = "velopack_nodeffi_win_x64_msvc.node",
            Facts = ran is null ? null : new CrashFacts { RanSec = ran },
        },
        "Medal", used is null ? null : new CrashContext(60, 50, null, used), null, []);

    private static string Line(CrashStory s) => ReadableExport.CrashesText([s], "Today", DateTime.Now, "1.5.0", tight: true)
        .Split('\n').Select(l => l.TrimEnd('\r')).SingleOrDefault(l => l.StartsWith("  Had been running", StringComparison.Ordinal)) ?? "(none)";

    [Fact]
    public void A_crash_seconds_after_starting_in_an_app_used_for_minutes_says_which_is_which()
    {
        Assert.Equal("  Had been running: 4 seconds, as Windows recorded the program that crashed; Medal itself had been in use for 4m", Line(Story(4, 240)));
        Assert.Equal("  Had been running: 4 seconds", Line(Story(4, 30)));
        Assert.Equal("  Had been running: 4m", Line(Story(null, 240)));
        Assert.Equal("  Had been running: 5m", Line(Story(300, 280)));
        Assert.Equal("(none)", Line(Story(null, null)));
        Assert.Contains("<dt>Had been running</dt><dd>4 seconds, as Windows recorded the program that crashed; Medal itself had been in use for 4m</dd>",
            ReadableExport.Crashes([Story(4, 240)], "Today", DateTime.Now, "1.5.0"));
    }
}
