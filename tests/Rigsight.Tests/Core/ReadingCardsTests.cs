using Rigsight.Core;

namespace Rigsight.Tests.Core;

/// <summary>
/// A taskbar reading's card (issue #5): which page its button opens, which readings it lists, whether "highest today"
/// means anything, and the words under its graph.
/// </summary>
public sealed class ReadingCardsTests
{
    [Theory]
    [InlineData(TrayPart.Cpu, SensorKind.Temperature, "temperatures")]
    [InlineData(TrayPart.Ram, SensorKind.Temperature, "temperatures")] // a temperature is a temperature, whatever part
    [InlineData(TrayPart.Gpu, SensorKind.Fan, "fans")]
    [InlineData(TrayPart.Board, SensorKind.Control, "fans")]
    [InlineData(TrayPart.Cpu, SensorKind.Load, "processes")]
    [InlineData(TrayPart.Gpu, SensorKind.Load, "sensors")]
    [InlineData(TrayPart.Ram, SensorKind.Load, "memory")]
    [InlineData(TrayPart.Ram, SensorKind.Data, "memory")]
    [InlineData(TrayPart.Drive, SensorKind.Load, "storage")]
    [InlineData(TrayPart.Drive, SensorKind.Throughput, "storage")]
    [InlineData(TrayPart.Network, SensorKind.Throughput, "network")]
    [InlineData(TrayPart.Cpu, SensorKind.Power, "sensors")]
    [InlineData(TrayPart.Other, SensorKind.Voltage, "sensors")]
    public void The_button_opens_the_page_the_reading_belongs_to(TrayPart part, SensorKind kind, string page) =>
        Assert.Equal(page, ReadingCards.Page(part, kind));

    [Fact]
    public void Each_page_has_its_own_button_words()
    {
        using var _ = Lang.UseHere(null);
        string[] pages = ["temperatures", "fans", "processes", "memory", "storage", "network", "sensors"];
        var words = pages.Select(ReadingCards.Button).ToList();
        Assert.Equal(pages.Length, words.Distinct().Count());
        Assert.All(words, w => Assert.StartsWith("Open ", w));
        Assert.Equal("Open Temperatures", ReadingCards.Button("temperatures"));
        Assert.Equal("Open All sensors", ReadingCards.Button("anything else"));
    }

    [Theory]
    [InlineData(TrayPart.Cpu, SensorKind.Temperature, true)]
    [InlineData(TrayPart.Cpu, SensorKind.Load, true)]
    [InlineData(TrayPart.Network, SensorKind.Throughput, true)]
    [InlineData(TrayPart.Drive, SensorKind.Load, false)]   // a drive's used space only grows
    [InlineData(TrayPart.Drive, SensorKind.Data, false)]
    [InlineData(TrayPart.Cpu, SensorKind.Voltage, false)]
    public void Highest_today_only_where_it_means_something(TrayPart part, SensorKind kind, bool shows) =>
        Assert.Equal(shows, ReadingCards.ShowsHighest(part, kind));

    [Fact]
    public void The_cpu_graphics_card_and_ram_list_their_own_key_readings()
    {
        Assert.Equal(KeySensors.CpuTemp, ReadingCards.KeyRows(TrayPart.Cpu)[0].Key);
        Assert.Contains(ReadingCards.KeyRows(TrayPart.Gpu), r => r.Key == KeySensors.GpuHotSpot);
        Assert.Equal([KeySensors.RamLoad, KeySensors.RamUsed, KeySensors.RamAvailable], ReadingCards.KeyRows(TrayPart.Ram).Select(r => r.Key));
        Assert.Empty(ReadingCards.KeyRows(TrayPart.Drive)); // other parts list their device's other sensors
        Assert.All(new[] { TrayPart.Cpu, TrayPart.Gpu, TrayPart.Ram }.SelectMany(ReadingCards.KeyRows), r => Assert.False(string.IsNullOrWhiteSpace(r.Label)));
    }

    [Theory]
    [InlineData(TrayPart.Cpu, CardApps.Cpu)]
    [InlineData(TrayPart.Gpu, CardApps.Gpu)]
    [InlineData(TrayPart.Ram, CardApps.Memory)]
    [InlineData(TrayPart.Drive, CardApps.Disk)]
    [InlineData(TrayPart.Network, CardApps.Network)]
    [InlineData(TrayPart.Board, CardApps.None)]
    [InlineData(TrayPart.Other, CardApps.None)]
    public void Each_part_lists_the_apps_that_use_it(TrayPart part, CardApps by)
    {
        var (listed, title) = ReadingCards.Apps(part);
        Assert.Equal(by, listed);
        Assert.Equal(by == CardApps.None, title.Length == 0);
    }

    [Fact]
    public void Under_the_graph_the_time_it_covers()
    {
        using var _ = Lang.UseHere(null);
        var now = new DateTime(2026, 11, 4, 9, 30, 0);
        Assert.Equal("Last 10 minutes", ReadingCards.Span(now.AddMinutes(-10), now));
        Assert.Equal("Last 10 minutes", ReadingCards.Span(now.AddMinutes(-9.7), now)); // a sample or two short
        Assert.Equal($"Since {TimeText.Time(now.AddMinutes(-3))}", ReadingCards.Span(now.AddMinutes(-3), now));
    }

    [Fact]
    public void Low_average_and_high_skip_missing_readings()
    {
        using var _ = Lang.UseHere(null);
        string F(double v) => Units.StripText(SensorKind.Temperature, v);
        Assert.Equal($"low {F(40)} · average {F(50)} · high {F(60)}", ReadingCards.LowAvgHigh(SensorKind.Temperature, [40, float.NaN, 50, 60]));
        Assert.Equal("", ReadingCards.LowAvgHigh(SensorKind.Temperature, [float.NaN]));
        Assert.Equal("", ReadingCards.LowAvgHigh(SensorKind.Load, []));
    }
}
