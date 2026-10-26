using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Core;

/// <summary>
/// What a close-up says (what a minute's average hides; what came straight after) and the evidence only an opened crash
/// adds: graphics memory well above the app's own usual.
/// </summary>
public sealed class CloseUpWordsTests
{
    private static readonly DateTime Crash = new(2026, 6, 10, 21, 42, 30);
    private static long CrashTs => TimeUtil.ToUnix(Crash);

    /// <summary>Readings every 2 seconds for the 5 minutes before the crash (and <paramref name="after"/> seconds after), from a function of seconds before it.</summary>
    private static List<CloseUpPoint> Seconds(Func<int, CloseUpPoint> at, int after = 0)
    {
        var list = new List<CloseUpPoint>();
        for (int s = -300; s <= after; s += 2)
        {
            var p = at(s);
            p.T = (CrashTs + s) * 1000;
            list.Add(p);
        }
        return list;
    }

    private static CloseUpPoint Calm(int s) => new() { CpuTemp = 44, GpuTemp = 38, GpuHot = 47, CpuLoad = 3, GpuLoad = 2, CpuPower = 22, GpuPower = 18, Vram = 1100, VramTotal = 12288, Here = true };

    private static CloseUpPoint Game(int s) => new()
    {
        CpuTemp = 70, GpuTemp = 72, GpuLoad = 97, CpuLoad = 40, CpuPower = 80, VramTotal = 12288, Here = true, Front = 7,
        // The hot spot rising from 85 to 91 in the last 30 seconds; the power jumping for the last 10; memory climbing for the last 40.
        GpuHot = s < -30 ? 85 : 85 + 6.0 * (s + 30) / 30,
        GpuPower = s < -10 ? 200 : 280,
        Vram = s < -40 ? 10_300 : 10_300 + 1900.0 * (s + 40) / 40,
    };

    [Fact]
    public void The_seconds_say_what_a_minutes_average_hides()
    {
        var said = CloseUpWords.Find(Seconds(Game), CrashTs, _ => "Apex Legends");
        Assert.Contains(said, s => s.Warn && s.Text.StartsWith("GPU hot spot rose from 85") && s.Text.Contains("in the last ") && s.Text.Contains("The minute's average showed only"));
        Assert.Contains(said, s => s.Warn && s.Text.StartsWith("GPU power jumped to about 280 W for the last"));
        Assert.Contains(said, s => s.Text.StartsWith("Graphics memory climbed from 10.1 GB to 11.9 GB of 12 GB in the last "));
        Assert.DoesNotContain(said, s => s.IsCalm);
    }

    [Fact]
    public void A_rise_that_ends_cool_is_not_said()
    {
        // The desktop's hot spot jumping 55° to 65°: seen on the user's PC in the first real run, and meaningless.
        var points = Seconds(s => { var p = Calm(s); p.GpuHot = s < -30 ? 55 : 65; return p; });
        Assert.DoesNotContain(CloseUpWords.Find(points, CrashTs, _ => "x"), x => x.Warn);
    }

    [Fact]
    public void Steady_seconds_say_nothing_was_climbing()
    {
        var said = CloseUpWords.Find(Seconds(s => Game(-300)), CrashTs, _ => "Apex Legends");
        var only = Assert.Single(said);
        Assert.True(only.IsCalm);
    }

    [Fact]
    public void After_an_app_crash_the_graphics_card_going_quiet_and_the_app_back_in_front_are_said()
    {
        var points = Seconds(s => s <= 0 ? Game(-300) : new CloseUpPoint { GpuLoad = 3, Here = true, Front = s >= 100 ? 7 : 1 }, after: 120);
        var said = CloseUpWords.Find(points, CrashTs, id => id == 7 ? "Apex Legends" : "Desktop", crashedApp: 7);
        var after = Assert.Single(said, s => s.Text.StartsWith("Right after,"));
        Assert.Contains("the GPU went from 97% to 3% at once", after.Text);
        Assert.Contains($"Apex Legends was back in front {CrashFindings.Words(100)} later", after.Text);
    }

    [Fact]
    public void Too_few_readings_say_nothing()
    {
        var said = CloseUpWords.Find(Seconds(Game).TakeLast(2).ToList(), CrashTs, _ => "x");
        Assert.Empty(said);
    }

    // ── Graphics memory against the app's usual: normal, borderline, abnormal, missing, no earlier sessions ──

    [Theory]
    [InlineData(10_200, 10_000, 60, false)] // normal: about its usual
    [InlineData(10_900, 10_000, 60, false)] // borderline: 900 MB more, under the gigabyte
    [InlineData(11_900, 10_000, 60, true)]  // abnormal: well above, and nearly full
    [InlineData(11_900, 10_000, 10, false)] // too little of its usual on record
    [InlineData(11_900, 0, 0, false)]       // no earlier sessions of it
    [InlineData(6_000, 3_000, 60, false)]   // well above its usual, but plenty left: not said
    public void Graphics_memory_is_said_only_well_above_the_apps_own_usual_and_nearly_full(double used, double usual, int minutes, bool said)
    {
        var at = new GpuMemoryAtCrash(used, 12_288, usual, minutes, UpClose: true);
        Assert.Equal(said, at.WellAbove);
        var point = at.Point("Apex Legends");
        Assert.Equal(said, point is not null);
        if (point is not null)
        {
            Assert.Equal(("warn", "GPU memory was possibly running out, with 11.6 of 12 GB used"), (point.Kind, point.Title));
            Assert.StartsWith("In the last 30 seconds. Apex Legends usually uses about 9.8 GB on this PC.", point.Detail);
        }
    }

    [Fact]
    public void Graphics_memory_from_the_history_is_the_apps_own_usual_and_only_on_an_opened_crash()
    {
        using var t = new TestDb("vram");
        var db = t.Db;
        long app = db.UpsertApp("r5apex.exe", "Apex Legends", null, AppCategory.Game);
        // Earlier evenings in Apex, about 10 GB; then the crash's minute at 11.9 GB.
        long day = CrashTs - 3 * 86400;
        for (int i = 0; i < 90; i++)
            t.Exec("INSERT INTO system_minute(ts, fg_app, active_sec, vram_used, vram_total) VALUES($ts, $app, 60, $v, 12288)",
                ("$ts", day + i * 60), ("$app", app), ("$v", 10_000 + i % 5 * 20));
        long minute = CrashTs / 60 * 60;
        t.Exec("INSERT INTO system_minute(ts, fg_app, active_sec, vram_used, vram_total) VALUES($ts, $app, 60, 11900, 12288)", ("$ts", minute), ("$app", app));
        db.InsertCrashes([new CrashEvent { Ts = CrashTs, Kind = CrashKind.AppCrash, AppExe = "r5apex.exe", Module = "r5apex.exe" }]);
        var crash = db.GetCrashes(CrashTs - 1, CrashTs + 1).Single();

        var memory = CrashCases.GpuMemory(db, crash, app, null)!;
        Assert.Equal((11_900, 90, false), (memory.UsedMb, memory.UsualMinutes, memory.UpClose));
        Assert.InRange(memory.UsualMb, 10_000, 10_080);

        var listed = CrashCases.Of(db, [crash], new RigsightSettings())[crash.Id];
        var opened = CrashCases.Of(db, [crash], new RigsightSettings(), upClose: true)[crash.Id];
        Assert.DoesNotContain(listed.Points, p => p.Title.StartsWith("GPU memory was possibly"));
        Assert.Contains(opened.Points, p => p.Title == "GPU memory was possibly running out, with 11.6 of 12 GB used" && p.Detail.Contains("In its last minute"));
        // The same answer either way: only what it rests on grows.
        Assert.Equal((listed.Head, listed.Confidence, listed.Advice), (opened.Head, opened.Confidence, opened.Advice));
    }
}
