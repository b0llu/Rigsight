using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.App;

/// <summary>
/// What the look-ups say when a reading is missing or the model asks oddly (live answers, 31 Oct to 2 Nov 2026): a
/// graphics card that gives no power reading, a name sent as a number or a list, and a look-up that fails. Each answers
/// in words the model can use, and none ends the question.
/// </summary>
public sealed class AiLookUpGapsTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"rigsight-gaps-{Guid.NewGuid():N}.db");
    private static readonly DateTime At = new(2026, 11, 2, 21, 30, 0);

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

    [Fact]
    public void A_card_that_gives_no_power_reading_has_no_blank_watts_in_the_minutes()
    {
        // An RTX 3050 with an i7-14700T: 42 of 42 minutes said "power CPU 13 W, GPU  W", even with the card near full load.
        using (var w = RigsightDb.OpenWriter(_db))
            for (int i = -3; i <= 3; i++)
                w.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(At) + i * 60, CpuTempMax = 60, CpuLoad = 30, GpuTempMax = 70, GpuLoad = 97, CpuPower = 13 });

        string said = Tools().Run("get_moment", """{"time":"2026-11-02 21:30"}""");

        Assert.DoesNotContain("GPU  W", said);
        Assert.DoesNotContain("W, GPU", said);
        Assert.Equal(7, said.Split("power CPU 13 W").Length - 1);
        Assert.Contains("The graphics card's power wasn't read in any of these minutes", said);
    }

    [Fact]
    public void Minutes_with_both_watts_say_both_and_no_missing_note()
    {
        using (var w = RigsightDb.OpenWriter(_db))
            w.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(At), CpuTempMax = 60, GpuTempMax = 70, CpuPower = 45, GpuPower = 180 });

        string said = Tools().Run("get_moment", """{"time":"2026-11-02 21:30"}""");

        Assert.Contains("power CPU 45 W, GPU 180 W", said);
        Assert.DoesNotContain("wasn't read", said);
    }

    [Fact]
    public void Energy_read_from_the_CPU_alone_says_so_and_never_graphics_card_0_Wh()
    {
        var report = new Rigsight.Core.Reports.Report { EnergyWh = 600, CpuEnergyWh = 600, GpuEnergyWh = null, PoweredMinutes = 10 * 60 };
        string text = AiTools.Energy(report, new DateTime(2026, 11, 1), new DateTime(2026, 11, 2));
        Assert.Contains("ENERGY (CPU only: the graphics card's power wasn't read", text);
        Assert.DoesNotContain("graphics card 0", text);
        Assert.DoesNotContain("CPU and graphics card", text);
    }

    [Fact]
    public void A_report_leaves_out_a_part_never_read_rather_than_count_it_as_0()
    {
        using (var w = RigsightDb.OpenWriter(_db))
            for (int i = 0; i < 60; i++) w.WriteMinute(new SystemMinute { Ts = TimeUtil.ToUnix(At.Date.AddHours(20)) + i * 60, CpuTemp = 50, CpuPower = 20, ActiveSec = 60 });
        using var db = RigsightDb.OpenReader(_db)!;

        var report = Rigsight.Core.Reports.ReportBuilder.Build(db, Rigsight.Core.Reports.ReportRange.Day, At, new RigsightSettings());

        Assert.Equal(20, report.CpuEnergyWh!.Value, 1);
        Assert.Null(report.GpuEnergyWh);
    }

    private static void TwoDashboards(RigsightSettings s) => s.CustomPages.AddRange(
    [
        new CustomPageConfig { Name = "Gaming", Tiles = [new TileConfig { Kind = "gpu-gauge" }] },
        new CustomPageConfig { Name = "My day", Tiles = [new TileConfig { Kind = "crashes" }] },
    ]);

    [Fact]
    public void The_dashboards_look_up_lists_every_tile_a_dashboard_can_have()
    {
        // 2026-11-05: asked for internet and crash tiles, the AI said it couldn't see the list of tiles to add.
        foreach (var tools in new[] { Tools(TwoDashboards), Tools(_ => { }) })
        {
            var text = tools.Run("get_dashboard", "{}");
            Assert.Contains("Tiles a dashboard can have", text);
            Assert.Contains("Connection today (Whether the internet dropped today)", text);
            Assert.Contains("Crashes (Crashes in the last 30 days, and the latest one)", text);
            Assert.Equal(Rigsight.Core.Settings.DashboardTiles.All.Count, Rigsight.Core.Settings.DashboardTiles.All.Count(t => text.Contains(t.Title + " (")));
        }
        Assert.Contains("Gaming: GPU temperature", Tools(TwoDashboards).Run("get_dashboard", "{}"));
    }

    [Fact]
    public void A_dashboard_name_sent_as_a_number_or_a_list_is_answered_not_thrown()
    {
        // "What tiles would make a useful dashboard for how I use this PC?" (31 Oct): the model asked for get_dashboard and
        // the app never sent the next round. A cast to text throws on anything but text, and nothing caught it.
        var tools = Tools(TwoDashboards);
        Assert.StartsWith("No dashboard is called \"5\"", tools.Run("get_dashboard", """{"name":5}"""));
        Assert.Contains("Gaming: GPU temperature", tools.Run("get_dashboard", """{"name":["Gaming"]}"""));
        Assert.Contains("Gaming: GPU temperature", tools.Run("get_dashboard", """{"name":{"x":1}}"""));
    }

    [Fact]
    public void The_reading_line_and_its_source_take_odd_arguments_too()
    {
        // Both run in the round itself, before and after the look-up: a throw there ends the question the same way.
        var args = new System.Text.Json.Nodes.JsonObject { ["name"] = 5, ["from"] = new System.Text.Json.Nodes.JsonArray(1), ["query"] = true };
        Assert.Contains("5", Ollama.Reviewing("get_game", args));
        Assert.NotEmpty(Ollama.Reviewing("search_web", args));
        Assert.NotEmpty(Ollama.Reviewing("get_report", args));
        Assert.Equal("5", AiArgs.Text(args["name"]));
        Assert.Equal("true", AiArgs.Text(args["query"]));
        Assert.Null(AiArgs.Text(args["from"]));
        Assert.Null(AiArgs.Text(null));
    }

    [Fact]
    public void A_look_up_that_fails_says_so_in_words_and_is_tried_again_next_time()
    {
        int asked = 0;
        var tools = Tools();
        tools.SensorsNow = () => ++asked == 1 ? throw new InvalidOperationException("Collection was modified") : "  GPU: Core 64 °C\n";

        string first = tools.Run("get_sensors_now", "{}");
        Assert.StartsWith("Error: RigSight couldn't read this just now (InvalidOperationException)", first);
        Assert.Contains("Core 64 °C", tools.Run("get_sensors_now", "{}"));
    }
}
