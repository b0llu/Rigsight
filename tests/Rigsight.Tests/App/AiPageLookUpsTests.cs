using System.Reflection;
using System.Text.Json.Nodes;
using Rigsight.Core.Ai;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>Two weeks of made-up history, made once for the class.</summary>
public sealed class PageHistory : IDisposable
{
    public string Db { get; } = Path.Combine(Path.GetTempPath(), $"rigsight-pages-{Guid.NewGuid():N}.db");
    public string Empty { get; } = Path.Combine(Path.GetTempPath(), $"rigsight-pages-empty-{Guid.NewGuid():N}.db");

    public PageHistory()
    {
        SeedData.Generate(Db, SeedProfile.Small);
        using (RigsightDb.OpenWriter(Empty)) { }
    }

    public void Dispose()
    {
        foreach (var f in new[] { Db, Empty }.SelectMany(p => new[] { p, p + "-wal", p + "-shm" }))
            try { File.Delete(f); } catch (IOException) { }
    }
}

/// <summary>
/// The look-ups for the pages that had none: Home's today, All sensors now, Rewind and the person's dashboards. Each
/// answers from the history (or the app's live readings), says plainly when there's nothing, and keeps to what
/// Settings › Ask RigSight lets it send.
/// </summary>
public sealed class AiPageLookUpsTests(PageHistory history) : IClassFixture<PageHistory>
{
    private static string Run(AiTools tools, string name, JsonObject? args = null)
    {
        var run = typeof(AiTools).GetMethod("RunPages", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try { return (string?)run.Invoke(tools, [name, args ?? []]) ?? "(none)"; }
        catch (TargetInvocationException ex) when (ex.InnerException is FormatException f) { return "Error: " + f.Message; }
    }

    private static List<string> Names()
    {
        var defs = (IEnumerable<JsonObject>)typeof(AiTools).GetMethod("PageDefinitions", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        return [.. defs.Select(d => (string)d["function"]!["name"]!)];
    }

    private AiTools Tools(Action<RigsightSettings>? set = null, bool empty = false)
    {
        var s = new RigsightSettings();
        set?.Invoke(s);
        return new AiTools(s, empty ? history.Empty : history.Db);
    }

    [Fact]
    public void The_four_page_look_ups_are_defined_and_an_unknown_one_is_not_theirs()
    {
        Assert.Equal(["get_today", "get_sensors_now", "get_rewind", "get_drive_scan", "get_widgets", "get_overlay", "get_taskbar", "get_dashboard"], Names());
        Assert.Equal("(none)", Run(Tools(), "get_passwords"));
    }

    [Fact]
    public void An_app_asked_about_as_a_game_is_said_to_be_an_app()
    {
        // Real PC, 2026-10-27: "What is Freedom to Stream?" was looked up as a game and answered "no such game".
        string said = Tools().Run("get_game", """{"name":"Discord"}""");
        Assert.StartsWith("'Discord' isn't a game: it's an app on this PC (discord.exe", said);
        Assert.Contains("get_apps", said);
        Assert.StartsWith("No game called", Tools().Run("get_game", """{"name":"Nothing Like It"}"""));
    }

    [Fact]
    public void Today_says_PC_health_and_todays_report()
    {
        string said = Run(Tools(), "get_today");
        Assert.StartsWith("Today so far", said);
        Assert.Contains("PC health", said);
        Assert.Contains("Fullest drive", said);
    }

    [Fact]
    public void Today_uses_the_apps_drive_health_when_it_has_it_and_leaves_crashes_out_when_not_shared()
    {
        var tools = Tools(s => s.Ai.SetSends(AiPages.Crashes, false));
        tools.DrivesNow = () => "  Samsung SSD 980: health Good, 97% of its rated life left\n";
        string said = Run(tools, "get_today");
        Assert.Contains("Samsung SSD 980: health Good", said);
        Assert.DoesNotContain("No PC problems", said);
        Assert.Contains("switched these off in Settings › Ask RigSight: Crashes", said);
    }

    [Fact]
    public void Today_on_an_empty_history_still_answers()
    {
        string said = Run(Tools(empty: true), "get_today");
        Assert.StartsWith("Today so far", said);
        Assert.DoesNotContain("Error", said);
    }

    [Fact]
    public void Sensors_now_takes_the_apps_live_readings_first_and_never_keeps_them()
    {
        int looks = 0;
        var tools = Tools();
        tools.SensorsNow = () => $"Every reading right now, by part:\n  CPU: Package {++looks * 10} °C\n";
        Assert.Contains("Package 10 °C", Run(tools, "get_sensors_now"));
        Assert.Contains("Package 20 °C", Run(tools, "get_sensors_now"));
    }

    [Fact]
    public void Sensors_now_without_the_app_reads_the_last_recorded_minute_and_says_how_old_it_is()
    {
        string said = Run(Tools(), "get_sensors_now");
        Assert.Contains("last minute recorded", said);
        Assert.Contains("CPU:", said);
        Assert.Contains("ago", said);
    }

    [Fact]
    public void Sensors_now_on_an_empty_history_says_nothing_was_recorded()
    {
        Assert.StartsWith("No readings", Run(Tools(empty: true), "get_sensors_now"));
    }

    [Fact]
    public void Sensors_now_isnt_sent_with_all_sensors_switched_off()
    {
        var tools = Tools(s => s.Ai.SetSends(AiPages.Sensors, false));
        tools.SensorsNow = () => "CPU 60 °C";
        // Through Run, where the page switches are checked (the others call the look-ups straight).
        string said = tools.Run("get_sensors_now", "{}");
        Assert.DoesNotContain("60 °C", said);
        Assert.StartsWith("Not shared", said);
        Assert.Contains("\"All sensors\"", said);
    }

    [Fact]
    public void Rewind_of_this_year_so_far_has_time_apps_and_heat()
    {
        string said = Run(Tools(), "get_rewind", new() { ["period"] = "year" });
        Assert.Contains($"Rewind of {DateTime.Today.Year}", said);
        Assert.Contains("In use", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Top apps", said);
        Assert.Contains("Hottest", said);
    }

    [Fact]
    public void Rewind_of_this_month_by_name_and_of_a_month_with_nothing()
    {
        Assert.Contains("Rewind of", Run(Tools(), "get_rewind", new() { ["period"] = "month", ["when"] = DateTime.Today.ToString("yyyy-MM") }));
        Assert.StartsWith("Nothing was recorded", Run(Tools(), "get_rewind", new() { ["period"] = "month", ["when"] = "2020-01" }));
        Assert.Contains("hasn't started", Run(Tools(), "get_rewind", new() { ["period"] = "year", ["when"] = (DateTime.Today.Year + 1).ToString() }));
    }

    [Fact]
    public void Rewind_turns_a_wrong_period_or_date_into_words_the_model_can_fix()
    {
        Assert.StartsWith("Error: 'period'", Run(Tools(), "get_rewind", new() { ["period"] = "week" }));
        Assert.StartsWith("Error: 'when'", Run(Tools(), "get_rewind", new() { ["period"] = "month", ["when"] = "October" }));
    }

    [Fact]
    public void Rewind_leaves_out_apps_when_they_arent_shared()
    {
        string said = Run(Tools(s => s.Ai.SetSends(AiPages.Apps, false)), "get_rewind", new() { ["period"] = "year" });
        Assert.DoesNotContain("Top apps", said);
        Assert.Contains("switched these off in Settings › Ask RigSight: Apps", said);
    }

    private static void TwoDashboards(RigsightSettings s)
    {
        s.CustomPages.Add(new CustomPageConfig
        {
            Name = "Gaming",
            Tiles = [new TileConfig { Kind = "gpu-gauge" }, new TileConfig { Kind = "sensor", Sensor = "key:gpuPower", Y = 1 }, new TileConfig { Kind = "crashes", Y = 2 }, new TileConfig { Kind = "net-today", Y = 3 }],
        });
        s.CustomPages.Add(new CustomPageConfig { Name = "Empty one" });
    }

    [Fact]
    public void Dashboards_without_a_name_lists_them_with_their_tiles()
    {
        string said = Run(Tools(TwoDashboards), "get_dashboard");
        Assert.Contains("Gaming: GPU temperature, GPU power, Crashes, Internet today", said);
        Assert.Contains("Empty one: no tiles", said);
        Assert.Equal("The person hasn't made any dashboards.", Run(Tools(), "get_dashboard"));
    }

    [Fact]
    public void A_dashboard_by_name_says_what_its_tiles_show()
    {
        var tools = Tools(TwoDashboards);
        tools.SensorsNow = () => "  GPU: Core 64 °C, Power 220 W\n";
        string said = Run(tools, "get_dashboard", new() { ["name"] = "gaming" });
        Assert.Contains("Power 220 W", said);
        Assert.Contains("Crashes:", said);
        Assert.Contains("Internet today", said);
        Assert.Contains("has no tiles", Run(tools, "get_dashboard", new() { ["name"] = "Empty one" }));
    }

    [Fact]
    public void A_dashboard_that_doesnt_exist_names_the_ones_that_do()
    {
        string said = Run(Tools(TwoDashboards), "get_dashboard", new() { ["name"] = "Work" });
        Assert.StartsWith("No dashboard is called \"Work\"", said);
        Assert.Contains("Gaming, Empty one", said);
    }

    [Fact]
    public void A_dashboards_crashes_and_internet_keep_to_the_switches()
    {
        string said = Run(Tools(s => { TwoDashboards(s); s.Ai.ShareCrashes = false; s.Ai.ShareNetwork = false; }), "get_dashboard", new() { ["name"] = "Gaming" });
        Assert.Contains("Crashes: left out", said);
        Assert.Contains("Internet: left out", said);
    }

    [Fact]
    public void Widgets_overlay_and_taskbar_say_what_is_on_and_only_the_readings_that_place_can_show()
    {
        var settings = new RigsightSettings();
        settings.Widgets.Add(new WidgetConfig { Style = WidgetStyle.Custom, Id = "custom-1", Name = "My widget 1", Layout = WidgetLayout.Bar, Enabled = true,
            Items = WidgetCatalog.Items(OverlayMetric.GpuHotSpot, OverlayMetric.Ram) });
        settings.TraySensors = ["/gpu/0/temperature/0"];
        var tools = new AiTools(settings, history.Db) { SensorName = id => id == "/gpu/0/temperature/0" ? "GPU Core" : null };

        string widgets = Run(tools, "get_widgets");
        Assert.Contains("My widget 1 (GPU hot spot, RAM in use)", widgets);
        Assert.Contains("Screen time today", widgets);
        Assert.Contains("1% low (needs RivaTuner)", widgets);

        // The overlay has none of the widgets-only readings (2026-10-26).
        string overlay = Run(tools, "get_overlay");
        Assert.Contains("Frame time (needs RivaTuner)", overlay);
        Assert.DoesNotContain("Screen time today", overlay);

        // The taskbar shows only readings it has a sensor for: never a frame reading.
        string taskbar = Run(tools, "get_taskbar");
        Assert.Contains("Shown: GPU Core.", taskbar);
        Assert.Contains("GPU hot spot", taskbar);
        Assert.DoesNotContain("1% low", taskbar);
        Assert.DoesNotContain("Frame time", taskbar);
    }
}
