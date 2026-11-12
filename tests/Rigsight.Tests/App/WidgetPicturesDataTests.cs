using System.Reflection;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Widgets page's pictures show what the desktop shows, from the app's own readings (the user's test copy,
/// 2026-11-12: its Fans tile had no graphics card fans, and tiles from the records stayed a heading until a change).
/// </summary>
[Collection("UI")]
public sealed class WidgetPicturesDataTests
{
    private const string Gpu1 = "/gpu-nvidia/0/fan/1", Gpu2 = "/gpu-nvidia/0/fan/2", Board1 = "/lpc/it8686e/0/fan/0", Board2 = "/lpc/it8686e/0/fan/1",
        Board4 = "/lpc/it8686e/0/fan/3", GpuCore = "/gpu-nvidia/0/temperature/0";

    /// <summary>The user's PC, cut down: an RTX 3080 Ti whose fans stand still at idle, and a board chip with three headers.</summary>
    private static AgentMessage Hello() => new()
    {
        T = "hello",
        IsAdmin = true,
        Keys = new(),
        Hardware =
        [
            new HardwareMeta
            {
                Name = "NVIDIA GeForce RTX 3080 Ti", Type = "GpuNvidia",
                Sensors = [new() { Id = GpuCore, Name = "GPU Core", Kind = SensorKind.Temperature }, new() { Id = Gpu1, Name = "GPU Fan 1", Kind = SensorKind.Fan }, new() { Id = Gpu2, Name = "GPU Fan 2", Kind = SensorKind.Fan }],
            },
            new HardwareMeta
            {
                Name = "ITE IT8686E", Type = "SuperIO",
                Sensors = [new() { Id = Board1, Name = "Fan #1", Kind = SensorKind.Fan }, new() { Id = Board2, Name = "Fan #2", Kind = SensorKind.Fan }, new() { Id = Board4, Name = "Fan #4", Kind = SensorKind.Fan }],
            },
        ],
    };

    private static LiveData Greeted()
    {
        var start = SeedData.QuietSettings();
        start.SensorLabels[Board1] = "Front intake";
        var (_, live) = Kit.Greeted(start, Hello());
        // The card's fans and Fan #2 stand still (all day so far: nothing in today's range), Fan #1 turns.
        Ui.Run(() => live.ApplyTick(new AgentMessage { T = "tick", Time = TimeUtil.NowUnixMs(), Values = [44f, 0f, 0f, 1210f, 0f, 0f] }));
        return live;
    }

    [Fact]
    public void The_fans_tile_lists_the_Fans_page_rows_with_the_card_at_rest()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            var fans = WidgetPictures.FansFrom(live)!;
            Assert.Equal(["GPU fans", "Front intake"], fans.Select(f => f.Name));
            Assert.Equal([0, 1210], fans.Select(f => f.Rpm));
            // The picture draws those rows.
            Assert.Equal(fans.Select(f => (f.Name, f.Rpm)), WidgetPictures.DataFrom(live).Pc!.Fans!.Select(f => (f.Name, f.Rpm)));

            // A header that spun in the records' last 90 days is listed, stopped or not.
            var facts = new PcFacts { Holds = PcNeeds.Fans, FansSpun = [Board2] };
            Assert.Equal(["GPU fans", "Front intake", "Fan #2"], WidgetPictures.FansFrom(live.AllSensors, false, facts)!.Select(f => f.Name));
            // Before the first readings, nothing (never "No spinning fans").
            Assert.Null(WidgetPictures.FansFrom(live.AllSensors, true, facts));
            // A board alone whose fans stand still: nothing until the records say none spun, then none.
            var board = live.AllSensors.Where(s => s.HardwareType == "SuperIO" && s.Id != Board1).ToList();
            Assert.Null(WidgetPictures.FansFrom(board, false, null));
            Assert.Empty(WidgetPictures.FansFrom(board, false, new PcFacts { Holds = PcNeeds.Fans })!);
        });
    }

    [Fact]
    public void A_sensor_reading_has_the_name_the_desktop_gives_it()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            var d = WidgetPictures.DataFrom(live);
            Assert.Equal("Front intake", d.Readings[Board1].Label);
            Assert.Equal(SensorWords.Show("GPU Fan 1"), d.Readings[Gpu1].Label);
        });
    }

    [Fact]
    public void Rivatuner_and_each_apps_internet_are_as_the_agent_says()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            try
            {
                WidgetPictures.RtssRunning = true;
                Assert.True(WidgetPictures.DataFrom(live).RtssRunning);
                WidgetPictures.RtssRunning = false;
                Assert.False(WidgetPictures.DataFrom(live).RtssRunning);

                // A quiet moment, nothing moving: with admin rights each app's internet is still read.
                live.ApplyTick(new AgentMessage { T = "tick", Time = TimeUtil.NowUnixMs(), Net = new NetLive { Time = TimeUtil.NowUnix() } });
                WidgetPictures.AgentIsAdmin = true;
                Assert.True(WidgetPictures.DataFrom(live).Pc!.NetPerApp);
                WidgetPictures.AgentIsAdmin = false;
                Assert.False(WidgetPictures.DataFrom(live).Pc!.NetPerApp);
            }
            finally
            {
                (WidgetPictures.RtssRunning, WidgetPictures.AgentIsAdmin) = (false, false);
            }
        });
    }

    [Fact]
    public void Every_picture_drawn_before_the_records_are_in_is_drawn_again()
    {
        SharedData.EnsureSeeded();
        typeof(WidgetPictures).GetField("_facts", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
        static WidgetConfig Box(string kind) => new() { Style = WidgetStyle.Custom, Id = "box-" + kind, Tiles = [new WidgetTile { Kind = kind, W = 6, H = 4 }] };
        var settings = SeedData.QuietSettings();
        int most = 0, week = 0;
        WidgetPictures.KeepFacts([Box(WidgetTiles.MostUsed)], settings, [], () => Interlocked.Increment(ref most));
        WidgetPictures.KeepFacts([Box(WidgetTiles.Week)], settings, [], () => Interlocked.Increment(ref week));
        var until = DateTime.UtcNow.AddSeconds(60);
        while ((Volatile.Read(ref most) == 0 || Volatile.Read(ref week) == 0) && DateTime.UtcNow < until) Thread.Sleep(50);
        Assert.True(most > 0, "the first picture wasn't told");
        Assert.True(week > 0, "the second picture wasn't told");
    }
}
