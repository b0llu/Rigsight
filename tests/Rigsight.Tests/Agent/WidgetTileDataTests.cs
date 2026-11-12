using Rigsight.Agent;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Tests.Data;

namespace Rigsight.Tests.Agent;

/// <summary>
/// What the desktop widgets' tiles are given to show, against the pages they mirror (the user's test copy, 2026-11-12:
/// the Fans tile left out the graphics card's fans, a box's sensor readings were never read).
/// </summary>
public class WidgetTileDataTests
{
    private sealed record Fan(string Id, string Name, string Type, string Hardware, double? Rpm, bool Spun = false);

    private static List<IReadOnlyList<Fan>> Rows(params Fan[] fans) =>
        FanList.Rows(fans, f => f.Type, f => f.Hardware, f => f.Rpm >= FanList.MinSpinRpm || f.Spun);

    [Fact]
    public void The_fans_tile_lists_the_Fans_page_rows()
    {
        // The user's PC: an RTX 3080 Ti whose two fans stand still at idle, a board chip with four headers (one never
        // used), a second chip with nothing plugged in, and an AIO cooler's pump.
        var rows = Rows(
            new("/lpc/it8686e/0/fan/0", "Fan #1", "SuperIO", "ITE IT8686E", 1210),
            new("/lpc/it8686e/0/fan/1", "Fan #2", "SuperIO", "ITE IT8686E", 0, Spun: true), // stopped now, spun yesterday
            new("/lpc/it8686e/0/fan/3", "Fan #4", "SuperIO", "ITE IT8686E", 0),
            new("/lpc/it8792e/0/fan/0", "Fan #1", "SuperIO", "ITE IT8792E", 0),
            new("/gpu-nvidia/0/fan/1", "GPU Fan 1", "GpuNvidia", "NVIDIA GeForce RTX 3080 Ti", 0),
            new("/gpu-nvidia/0/fan/2", "GPU Fan 2", "GpuNvidia", "NVIDIA GeForce RTX 3080 Ti", 0),
            new("/aio/fan/0", "Pump", "Cooler", "Kraken X63", 2100));

        // The card first, as one row however still its fans stand; then the cooler; then the board's headers that have spun.
        Assert.Equal(["GPU Fan 1+GPU Fan 2", "Pump", "Fan #1", "Fan #2"], rows.Select(r => string.Join("+", r.Select(f => f.Name))));
        Assert.Equal(["GPU fans", "Pump", "Fan #1", "Fan #2"], rows.Select(r => FanList.Title(r.Count, r[0].Name)));
        // 0 rpm as the page shows it ("0 rpm", not left out and not "—").
        Assert.Equal(0, FanList.Speed(rows[0].Select(f => f.Rpm)));
        Assert.Equal(Units.Format(SensorKind.Fan, 0), Units.Format(SensorKind.Fan, FanList.Speed(rows[0].Select(f => f.Rpm))));
    }

    [Fact]
    public void A_rows_speed_is_read_as_the_Fans_page_reads_it()
    {
        Assert.Equal(1500, FanList.Speed([1400, 1600]));    // a card's fans turn as one: their average
        Assert.Equal(0, FanList.Speed([40]));               // a stray few rpm from a header is standing still
        Assert.Equal(1200, FanList.Speed([1200, null]));    // a fan not read is left out of the average
        Assert.Null(FanList.Speed([null, null]));           // none read: no reading ("—")
        Assert.Null(FanList.Speed([]));
    }

    [Fact]
    public void A_card_with_one_fan_keeps_its_own_name_and_two_cards_two_rows()
    {
        var rows = Rows(
            new("/gpu-nvidia/0/fan/1", "GPU Fan 1", "GpuNvidia", "RTX 4070", 0),
            new("/gpu-amd/0/fan/0", "GPU Fan", "GpuAmd", "RX 7800 XT", 900));
        Assert.Equal(["GPU Fan 1", "GPU Fan"], rows.Select(r => FanList.Title(r.Count, r[0].Name)));
        Assert.Empty(Rows(new Fan("/lpc/fan/0", "Fan #1", "SuperIO", "Board", 0)));
    }

    [Fact]
    public void The_fans_that_spun_come_from_the_last_90_days_of_records()
    {
        using var t = new TestDb("fans-spun");
        var now = DateTime.Today.AddHours(15);
        long gpu = t.Db.FanId("/gpu-nvidia/0/fan/1", "GPU Fan 1", "NVIDIA GeForce RTX 3080 Ti"),
            board = t.Db.FanId("/lpc/it8686e/0/fan/1", "Fan #2", "ITE IT8686E"),
            empty = t.Db.FanId("/lpc/it8686e/0/fan/3", "Fan #4", "ITE IT8686E"),
            gone = t.Db.FanId("/lpc/it8686e/0/fan/4", "Fan #5", "ITE IT8686E");
        void Minute(DateTime at, params (long Fan, int Rpm)[] fans)
        {
            long ts = TimeUtil.ToUnix(at);
            t.Db.WriteMinute(new SystemMinute { Ts = ts });
            t.Db.WriteFanMinutes(ts, [.. fans.Select(f => (f.Fan, f.Rpm, f.Rpm))]);
        }
        Minute(now.Date.AddDays(-10).AddHours(20), (gpu, 0), (board, 1250), (empty, 0), (gone, 0));
        Minute(now.Date.AddDays(-120).AddHours(20), (gone, 1500)); // spun, but before the 90 days
        Minute(now.AddMinutes(-5), (gpu, 1800), (board, 0), (empty, 40)); // today, so far
        using var db = t.Reader();
        Assert.Equal(["/gpu-nvidia/0/fan/1", "/lpc/it8686e/0/fan/1"], FanList.SpunIn(db, now).Order());

        // The tile asks for them with the records it reads, and only it.
        Assert.Equal(PcNeeds.Fans, PcFacts.NeedsOf(WidgetTiles.Fans));
        var f = PcFacts.Read(db, new RigsightSettings(), now, PcNeeds.Fans);
        Assert.Equal(PcNeeds.Fans, f.Holds);
        Assert.Equal(FanList.SpunIn(db, now).Order(), f.FansSpun.Order());
        Assert.Empty(PcFacts.Read(db, new RigsightSettings(), now, PcNeeds.Crashes).FansSpun);
    }

    [Fact]
    public void A_boxs_sensor_readings_are_read_for_the_desktop()
    {
        var settings = new RigsightSettings();
        settings.Widgets.Clear();
        settings.Widgets.Add(new WidgetConfig
        {
            Id = "box", Style = WidgetStyle.Custom, Enabled = true,
            Tiles =
            [
                new WidgetTile { Kind = WidgetTiles.Reading, W = 3, H = 2, Item = WidgetCatalog.SensorPrefix + "/lpc/it8686e/0/temperature/2" },
                new WidgetTile { Kind = WidgetTiles.Line, X = 3, W = 7, H = 1, Items = [new WidgetItem { Id = nameof(OverlayMetric.CpuTemp) }, new WidgetItem { Id = WidgetCatalog.SensorPrefix + "/nvme/0/temperature/0" }] },
            ],
        });
        settings.Widgets.Add(new WidgetConfig { Id = "old", Style = WidgetStyle.Custom, Enabled = true, Items = [new WidgetItem { Id = WidgetCatalog.SensorPrefix + "/lpc/it8686e/0/fan/0" }] });
        settings.Widgets.Add(new WidgetConfig
        {
            Id = "off", Style = WidgetStyle.Custom, Enabled = false,
            Tiles = [new WidgetTile { Kind = WidgetTiles.Reading, W = 3, H = 2, Item = WidgetCatalog.SensorPrefix + "/off/0" }],
        });
        Assert.Equal(["/lpc/it8686e/0/fan/0", "/lpc/it8686e/0/temperature/2", "/nvme/0/temperature/0"], AgentContext.WidgetSensorIds(settings).Order());
    }
}
