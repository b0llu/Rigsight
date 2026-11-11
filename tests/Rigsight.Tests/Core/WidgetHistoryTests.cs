using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

/// <summary>
/// The agent reads the last five minutes only for readings a widget draws as lines (the user, 2026-11-11: a box's
/// sparklines and its temperature graph showed in the app's preview and were empty on the desktop).
/// </summary>
public class WidgetHistoryTests
{
    [Fact]
    public void A_box_needs_the_history_of_its_graph_tiles_and_readings_shown_as_graphs()
    {
        var box = new WidgetConfig
        {
            Style = WidgetStyle.Custom, Id = "custom-1",
            Tiles =
            [
                new() { Kind = WidgetTiles.Reading, Item = nameof(OverlayMetric.CpuTemp), Look = WidgetTiles.LineGraph },
                new() { Kind = WidgetTiles.Reading, Item = nameof(OverlayMetric.GpuLoad), Look = WidgetTiles.BarGraph },
                new() { Kind = WidgetTiles.Reading, Item = nameof(OverlayMetric.Ram), Look = WidgetTiles.Ring },
                new() { Kind = WidgetTiles.Graph, Items = WidgetCatalog.Items(OverlayMetric.GpuTemp, OverlayMetric.GpuHotSpot) },
                new() { Kind = WidgetTiles.Clock },
            ],
        };
        Assert.Equal(
            [OverlayMetric.CpuTemp, OverlayMetric.GpuLoad, OverlayMetric.GpuTemp, OverlayMetric.GpuHotSpot],
            WidgetTiles.HistoryMetrics(box));
        // A graph tile with its readings left as they come draws CPU and GPU.
        box.Tiles = [new() { Kind = WidgetTiles.Graph }];
        Assert.Equal([OverlayMetric.CpuTemp, OverlayMetric.GpuTemp], WidgetTiles.HistoryMetrics(box));
        // Nothing drawn as lines: nothing read.
        box.Tiles = [new() { Kind = WidgetTiles.Reading, Item = nameof(OverlayMetric.CpuTemp), Look = WidgetTiles.Number }];
        Assert.Empty(WidgetTiles.HistoryMetrics(box));
    }

    [Fact]
    public void An_older_graph_widget_still_needs_its_readings_history() =>
        Assert.Contains(OverlayMetric.CpuTemp, WidgetTiles.HistoryMetrics(new WidgetConfig { Style = WidgetStyle.Graph, Id = "Graph" }));
}
