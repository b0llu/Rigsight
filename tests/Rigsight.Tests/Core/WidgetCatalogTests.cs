using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

/// <summary>What widgets are made of: layouts, readings, what each layout can show, and settings kept tidy.</summary>
public class WidgetCatalogTests
{
    [Theory]
    [InlineData(WidgetStyle.Pill, WidgetLayout.Bar, "CpuTemp,GpuTemp,Ram")]
    [InlineData(WidgetStyle.Compact, WidgetLayout.Tiles, "CpuTemp,GpuTemp")]
    [InlineData(WidgetStyle.Fps, WidgetLayout.Tiles, "Fps")]
    [InlineData(WidgetStyle.Gauges, WidgetLayout.Gauges, "CpuTemp,GpuTemp")]
    [InlineData(WidgetStyle.Graph, WidgetLayout.Graph, "CpuTemp,GpuTemp")]
    [InlineData(WidgetStyle.NowPlaying, WidgetLayout.NowPlaying, "")]
    [InlineData(WidgetStyle.Today, WidgetLayout.Today, "")]
    public void Built_in_widgets_are_a_layout_and_their_own_readings(WidgetStyle style, WidgetLayout layout, string items)
    {
        var w = new WidgetConfig { Style = style };
        Assert.Equal(layout, WidgetCatalog.LayoutOf(w));
        Assert.Equal(items, string.Join(",", WidgetCatalog.ItemsOf(w).Select(i => i.Id)));
        Assert.False(string.IsNullOrEmpty(WidgetCatalog.Description(style)));
    }

    [Fact]
    public void The_built_in_list_has_no_custom_and_puts_fps_first()
    {
        Assert.DoesNotContain(WidgetStyle.Custom, WidgetCatalog.BuiltIn);
        Assert.Equal(WidgetStyle.Fps, WidgetCatalog.Listed[0]);
        Assert.Equal(WidgetCatalog.BuiltIn.Order(), WidgetCatalog.Listed.Order());
        Assert.Equal(WidgetCatalog.BuiltIn, WidgetConfig.Defaults().Select(w => w.Style));
        Assert.All(WidgetConfig.Defaults(), w => Assert.Equal(w.Style.ToString(), w.Id));
    }

    [Theory]
    [InlineData("Fps", OverlayMetric.Fps)]
    [InlineData("GpuMemory", OverlayMetric.GpuMemory)]
    [InlineData("Session", null)]   // the app and time: not a reading
    [InlineData("3", null)]         // numbers aren't names
    [InlineData("fps", null)]       // written as saved
    [InlineData("sensor:/cpu/0/temperature/0", null)]
    [InlineData("", null)]
    public void Readings_are_known_by_name(string id, OverlayMetric? metric) => Assert.Equal(metric, WidgetCatalog.Metric(id));

    [Theory]
    [InlineData("sensor:/lpc/it8686e/fan/0", "/lpc/it8686e/fan/0")]
    [InlineData("sensor:", null)]
    [InlineData("CpuTemp", null)]
    public void Sensors_are_known_by_their_prefix(string id, string? sensor) => Assert.Equal(sensor, WidgetCatalog.Sensor(id));

    [Fact]
    public void Each_layout_shows_what_fits_it()
    {
        Assert.True(WidgetCatalog.Allows(WidgetLayout.Bar, OverlayMetric.Fps));
        Assert.True(WidgetCatalog.Allows(WidgetLayout.Tiles, OverlayMetric.Clock));
        Assert.False(WidgetCatalog.Allows(WidgetLayout.Bar, OverlayMetric.Session));
        Assert.True(WidgetCatalog.Allows(WidgetLayout.Gauges, OverlayMetric.GpuLoad));
        Assert.False(WidgetCatalog.Allows(WidgetLayout.Gauges, OverlayMetric.CpuPower)); // no full circle for watts
        Assert.True(WidgetCatalog.Allows(WidgetLayout.Graph, OverlayMetric.Ram));
        Assert.False(WidgetCatalog.Allows(WidgetLayout.Graph, OverlayMetric.Fps)); // no five minutes of it kept
        Assert.False(WidgetCatalog.Allows(WidgetLayout.NowPlaying, OverlayMetric.CpuTemp));
        Assert.True(WidgetCatalog.AllowsSensor(WidgetLayout.Tiles, SensorKind.Fan));
        Assert.True(WidgetCatalog.AllowsSensor(WidgetLayout.Gauges, SensorKind.Temperature));
        Assert.False(WidgetCatalog.AllowsSensor(WidgetLayout.Gauges, SensorKind.Fan));
        Assert.False(WidgetCatalog.AllowsSensor(WidgetLayout.Graph));
        Assert.All(WidgetCatalog.Offered(WidgetLayout.Graph), m => Assert.Contains(m, WidgetCatalog.GraphMetrics));
    }

    [Fact]
    public void Cleaning_keeps_known_readings_once_within_the_layouts_share()
    {
        var w = new WidgetConfig
        {
            Style = WidgetStyle.Custom, Layout = WidgetLayout.Gauges,
            Items =
            [
                new() { Id = "CpuTemp", Label = "  Chip  " }, new() { Id = "CpuTemp" }, new() { Id = "CpuPower" }, new() { Id = "Nope" },
                new() { Id = "GpuTemp", Label = new string('x', 40) }, new() { Id = "sensor:/a" }, new() { Id = "Ram" }, new() { Id = "GpuLoad" }, new() { Id = "CpuLoad" },
            ],
        };
        WidgetCatalog.Clean(w);
        Assert.Equal(["CpuTemp", "GpuTemp", "sensor:/a", "Ram"], w.Items!.Select(i => i.Id)); // four gauges at most
        Assert.Equal("Chip", w.Items![0].Label);
        Assert.Equal(OverlaySettings.MaxLabelLength, w.Items[1].Label!.Length);
    }

    [Fact]
    public void A_built_in_widget_back_on_its_own_readings_is_saved_as_its_default()
    {
        var w = new WidgetConfig { Style = WidgetStyle.Pill, Items = WidgetCatalog.Items(OverlayMetric.CpuTemp, OverlayMetric.GpuTemp, OverlayMetric.Ram) };
        WidgetCatalog.Clean(w);
        Assert.Null(w.Items);
        w.Items = [];
        WidgetCatalog.Clean(w);
        Assert.Null(w.Items); // nothing left: its own again
        w.Items = WidgetCatalog.Items(OverlayMetric.CpuTemp, OverlayMetric.GpuTemp, OverlayMetric.Ram, OverlayMetric.Fps);
        WidgetCatalog.Clean(w);
        Assert.Equal(4, w.Items!.Count);
    }

    [Fact]
    public void A_widget_of_your_own_always_shows_something()
    {
        var w = new WidgetConfig { Style = WidgetStyle.Custom, Layout = WidgetLayout.Tiles, Items = [new() { Id = "Session" }] };
        WidgetCatalog.Clean(w);
        Assert.Equal(["CpuTemp", "GpuTemp", "Ram"], w.Items!.Select(i => i.Id));
        w.Items = null;
        WidgetCatalog.Clean(w);
        Assert.NotEmpty(w.Items!);
    }

    [Fact]
    public void Settings_from_before_keep_every_widget_as_it_was()
    {
        // A 0.7 file: widgets had no identifier, name, layout or readings.
        var s = SettingsStore.Deserialize("""
            { "SettingsVersion": 8, "Widgets": [
              { "Style": "Pill", "Enabled": true, "X": 100, "Y": 40, "Theme": "Grey" },
              { "Style": "Compact", "Enabled": false } ] }
            """);
        Assert.Equal(WidgetCatalog.BuiltIn, s.Widgets.Select(w => w.Style));
        var pill = s.Widgets.Single(w => w.Style == WidgetStyle.Pill);
        Assert.Equal(("Pill", true, 100, WidgetTheme.Grey), (pill.Id, pill.Enabled, pill.X, pill.Theme));
        Assert.Null(pill.Items);
        Assert.All(s.Widgets, w => Assert.Equal((w.Style.ToString(), (string?)null, (WidgetLayout?)null), (w.Id, w.Name, w.Layout)));
    }

    [Fact]
    public void Widgets_of_your_own_get_an_identifier_a_name_and_a_layout_each()
    {
        var s = SettingsStore.Deserialize("""
            { "SettingsVersion": 8, "Widgets": [
              { "Style": "Custom", "Id": "custom-aaaa0001", "Name": "  Temps  ", "Layout": "Graph", "Items": [ { "Id": "CpuTemp" } ] },
              { "Style": "Custom", "Id": "custom-aaaa0001", "Name": "", "Layout": "NowPlaying" },
              { "Style": "Custom", "Id": "Pill", "Name": "Sneaky" },
              { "Style": "Pill", "Id": "custom-zz", "Name": "Renamed", "Layout": "Graph", "Enabled": true }
            ] }
            """);
        var mine = s.Widgets.Where(w => w.Style == WidgetStyle.Custom).ToList();
        Assert.Equal(3, mine.Count);
        Assert.Equal(3, mine.Select(w => w.Id).Distinct().Count());
        Assert.All(mine, w => Assert.StartsWith("custom-", w.Id));
        Assert.Equal("custom-aaaa0001", mine[0].Id);
        Assert.Equal(("Temps", WidgetLayout.Graph), (mine[0].Name, mine[0].Layout));
        Assert.Equal(("My widget", WidgetLayout.Bar), (mine[1].Name, mine[1].Layout)); // Now playing isn't a layout of one's own
        Assert.NotEmpty(mine[1].Items!);
        // Built-in ones keep their own identity whatever the file says; the user's own come after them.
        var pill = s.Widgets.Single(w => w.Style == WidgetStyle.Pill);
        Assert.Equal(("Pill", (string?)null, (WidgetLayout?)null, true), (pill.Id, pill.Name, pill.Layout, pill.Enabled));
        Assert.Equal(WidgetCatalog.BuiltIn, s.Widgets.Take(WidgetCatalog.BuiltIn.Count).Select(w => w.Style));
    }

    [Fact]
    public void There_is_a_limit_to_widgets_of_your_own()
    {
        var json = "{ \"SettingsVersion\": 8, \"Widgets\": [" + string.Join(",", Enumerable.Range(0, 40).Select(i => $$"""{ "Style": "Custom", "Id": "custom-{{i:0000}}" }""")) + "] }";
        var s = SettingsStore.Deserialize(json);
        Assert.Equal(WidgetCatalog.MaxCustom, s.Widgets.Count(w => w.Style == WidgetStyle.Custom));
    }
}
