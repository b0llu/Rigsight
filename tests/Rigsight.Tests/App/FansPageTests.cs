using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Fans page, on the PC the test agent was captured on (and the seeded history made for it): an RTX card's two fans
/// standing still while it's cool, four board fans at steady speeds, and four board headers with nothing plugged in.
/// </summary>
[Collection("UI")]
public sealed class FansPageTests
{
    private static (FansViewModel Vm, LiveData Live) Page(AgentMessage? tick = null)
    {
        SharedData.EnsureSeeded();
        var (settings, live) = Kit.Greeted(hello: Fixtures.Hello());
        Ui.Run(() => live.ApplyTick(tick ?? Fixtures.Tick()));
        var vm = Ui.Run(() => new FansViewModel(new ReportService(settings), live, settings));
        Kit.Wait(vm.RefreshAsync);
        return (vm, live);
    }

    /// <summary>The captured tick with some sensors' readings changed.</summary>
    private static AgentMessage Tick(params (string Id, float Value)[] set)
    {
        var tick = Fixtures.Tick();
        var ids = Fixtures.Hello().Hardware!.SelectMany(h => h.Sensors).Select(s => s.Id).ToList();
        foreach (var (id, value) in set) tick.Values![ids.IndexOf(id)] = value;
        return tick;
    }

    private const string GpuFan1 = "/gpu-nvidia/0/fan/1", GpuFan2 = "/gpu-nvidia/0/fan/2", BoardFan1 = "/lpc/it8686e/0/fan/0";

    [Fact]
    public void A_cards_fans_are_one_row_and_empty_headers_are_left_out()
    {
        var (vm, _) = Page();
        Ui.Run(() =>
        {
            // However many fans a card has, one row: its fans turn as one.
            var gpu = vm.Fans[0];
            Assert.True(gpu.IsGroup);
            Assert.Equal([GpuFan1, GpuFan2], gpu.Sensors.Select(s => s.Id));
            Assert.Equal(("GPU fans", "2 fans · resting while cool", "Graphics card"), (gpu.Title, gpu.Note, gpu.Place));
            Assert.Equal(2, gpu.Series.Count);

            // The board's fans that spin, in the board's order; the four headers that never did are counted instead.
            var board = vm.Fans.Skip(1).ToList();
            Assert.Equal(["Fan #1", "Fan #2", "Fan #3", "Fan #5"], board.Select(f => f.Title));
            Assert.All(board, f => Assert.Equal("Motherboard", f.Place));
            Assert.All(board, f => Assert.False(f.IsGroup));
            Assert.Equal(4, vm.EmptyHeaders);
            Assert.Equal("4 fan headers free", vm.EmptyHeadersText);

            // The first row is picked, its fans on the chart.
            Assert.Same(gpu, vm.Selected);
            Assert.Equal(gpu.Series, vm.ChartSeries);
            Assert.True(vm.HasFans);
        });
    }

    [Fact]
    public void Each_row_says_what_its_fans_follow_and_their_fastest_moment()
    {
        var (vm, _) = Page();
        Ui.Run(() =>
        {
            var gpu = vm.Fans[0];
            Assert.Equal("Speeds up with your GPU", gpu.FollowsText);
            // The seeded card rests under 50°: it stood still some of today, and spun fastest when it was hottest.
            if (gpu.HasFastest)
            {
                Assert.NotEqual("", gpu.FastestLine);
                Assert.EndsWith(" RPM", gpu.FastestText);
            }
            // Board fans at a fixed speed (±15 rpm) run steady once an hour is recorded (the seeded day is shorter
            // just after midnight).
            Assert.All(vm.Fans.Skip(1), f => Assert.Contains(f.FollowsText, new[] { "Runs at a steady speed", "" }));
            Assert.All(vm.Fans.Skip(1), f => Assert.Equal("", f.StoppedText));
        });
    }

    [Fact]
    public void A_cards_fans_rest_while_it_is_cool_and_need_a_look_when_they_stop_while_it_is_hot()
    {
        var (vm, live) = Page();
        var gpu = Ui.Run(() => vm.Fans[0]);
        Ui.Run(() =>
        {
            Assert.Equal(0, gpu.Value);
            Assert.Equal((FanState.Resting, "Resting while cool", false), (gpu.State, gpu.StateText, gpu.NeedsLook));
        });

        string gpuTemp = Ui.Run(() => live.GpuTemp!.Id);
        Ui.Run(() => live.ApplyTick(Tick((gpuTemp, 84), (GpuFan1, 0), (GpuFan2, 0))));
        Ui.Run(() => Assert.Equal((FanState.StoppedHot, "Stopped while hot", true), (gpu.State, gpu.StateText, gpu.NeedsLook)));

        Ui.Run(() => live.ApplyTick(Tick((gpuTemp, 84), (GpuFan1, 1800), (GpuFan2, 1900))));
        Ui.Run(() =>
        {
            Assert.Equal(FanState.Spinning, gpu.State);
            Assert.Equal(1850, gpu.Value); // the card's fans together
            Assert.Equal("1850 RPM", gpu.ValueText);
        });
    }

    [Fact]
    public void A_board_fan_that_stops_says_so_without_a_warning_when_its_chip_is_unknown()
    {
        var (vm, live) = Page();
        var fan = Ui.Run(() => vm.Fans.Single(f => f.Sensor.Id == BoardFan1));
        Ui.Run(() =>
        {
            Assert.Equal(FanState.Spinning, fan.State);
            Assert.Contains(fan.Note, new[] { "Spinning", "Steady speed" }); // "steady" once an hour of it is recorded
        });
        Ui.Run(() => live.ApplyTick(Tick((BoardFan1, 0))));
        Ui.Run(() => Assert.Equal((FanState.Stopped, "Stopped", false), (fan.State, fan.Note, fan.NeedsLook)));
    }

    [Fact]
    public void A_renamed_fan_shows_its_new_name_in_the_list_and_on_the_chart()
    {
        var (vm, _) = Page();
        Ui.Run(() =>
        {
            var fan = vm.Fans.Single(f => f.Sensor.Id == BoardFan1);
            fan.Sensor.Label = "Front intake";
            Assert.Equal("Front intake", fan.Title);
            Assert.Equal("Front intake", fan.Series[0].Label);
        });
    }

    [Fact]
    public void A_stray_few_rpm_on_an_empty_header_is_not_a_fan()
    {
        // A header with nothing on it reading half an rpm (as the test agent's jitter does), a fan dipping to 50.
        var (vm, _) = Page(Tick(("/lpc/it8792e/0/fan/0", 0.5f), (BoardFan1, 50)));
        Ui.Run(() =>
        {
            Assert.DoesNotContain(vm.Fans, f => f.Sensor.Id == "/lpc/it8792e/0/fan/0");
            Assert.Equal(4, vm.EmptyHeaders);
            var fan = vm.Fans.Single(f => f.Sensor.Id == BoardFan1);
            Assert.Equal((0.0, FanState.Stopped, "0 RPM"), (fan.Value, fan.State, fan.ValueText));
        });
    }

    [Fact]
    public void A_second_fan_chip_gets_a_heading_of_its_own()
    {
        // The board's second chip names its fans "Fan #1"… too: with one plugged in, its rows go under their own heading.
        var (vm, _) = Page(Tick(("/lpc/it8792e/0/fan/0", 1200)));
        Ui.Run(() =>
        {
            var second = vm.Fans.Single(f => f.Sensor.Id == "/lpc/it8792e/0/fan/0");
            Assert.Equal(("Fan #1", "Motherboard (chip 2)"), (second.Title, second.Place));
            Assert.All(vm.Fans.Where(f => f.Sensor.Id.StartsWith("/lpc/it8686e", StringComparison.Ordinal)), f => Assert.Equal("Motherboard", f.Place));
            Assert.Equal(3, vm.EmptyHeaders);
        });
    }

    [Fact]
    public void The_top_row_answers_how_the_fans_are_doing()
    {
        var (vm, _) = Page();
        Ui.Run(() =>
        {
            Assert.Equal(("All OK", true), (vm.StatusText, vm.StatusOk));
            Assert.Equal("6 fans · 4 fan headers free", vm.StatusNote); // a card's two fans count as two
            // The seeded card stands still under 50°: quiet time today, when the day has had some.
            if (vm.Fans[0].Facts is { StoppedMinutes: > 0 }) Assert.NotNull(vm.QuietText);
            if (vm.FastestText is not null) Assert.EndsWith(" RPM", vm.FastestText);
        });
    }

    [Fact]
    public void Each_row_has_its_facts_its_curve_and_its_last_30_days()
    {
        var (vm, _) = Page();
        Ui.Run(() =>
        {
            var gpu = vm.Fans[0].Detail!;
            Assert.Equal("Your GPU's heat", gpu.FollowsValue);
            Assert.Equal("SPEED AT EACH GPU TEMPERATURE", gpu.CurveTitle);
            Assert.Equal("WHAT MAKES THEM SPIN", gpu.AppsTitle);
            Assert.Equal("LAST 30 DAYS · GPU FANS", gpu.DaysTitle);
            Assert.Equal(30, gpu.Days.Count);
            Assert.Equal(DateTime.Today, gpu.Days[^1].Day);
            Assert.False(gpu.IsSteady);

            var board = vm.Fans.Single(f => f.Sensor.Id == BoardFan1).Detail!;
            Assert.Equal("SPEED AT EACH CPU TEMPERATURE", board.CurveTitle);
            Assert.Contains(board.FollowsValue, new[] { "A set speed", "Learning" });
        });
    }

    [Fact]
    public void A_fan_at_a_set_speed_says_how_steady_it_is_and_what_a_curve_would_do()
    {
        // A board fan at 1,630 rpm (±15) for two hours, whatever the CPU did.
        var sensor = new Rigsight.Models.SensorItem(new Rigsight.Core.Protocol.SensorMeta { Id = "/lpc/x/fan/0", Name = "Fan #2", Kind = Rigsight.Core.SensorKind.Fan }, "Board", "SuperIO");
        var card = new FanCard([sensor]);
        var rnd = new Random(4);
        long t0 = Rigsight.Core.Data.TimeUtil.ToUnix(DateTime.Today.AddHours(9));
        var fan = Enumerable.Range(0, 120).Select(i => new Rigsight.Core.Data.FanMinute(t0 + i * 60, 1, 1630 + rnd.Next(-15, 16), 1650)).ToList();
        var minutes = fan.ToDictionary(f => f.Ts, f => new Rigsight.Core.Data.SystemMinute { Ts = f.Ts, CpuTemp = 45 + f.Ts % 20, GpuTemp = 40 });
        card.Facts = Rigsight.Core.Reports.FanAnalysis.Of(false, fan, minutes);
        card.MinutesOn = fan.Count;
        var detail = FansViewModel.DetailOf(card, [fan], minutes, [], [], new Dictionary<long, Rigsight.Core.Data.AppRow>(), new Rigsight.Core.Settings.RigsightSettings(), DateTime.Today);
        Assert.True(detail.IsSteady);
        Assert.Equal("A set speed", detail.FollowsValue);
        Assert.StartsWith("About 1", detail.FollowsNote);
        Assert.Equal(("Never", "Steadiness"), (detail.StillValue, detail.ThirdLabel));
        Assert.Matches(@"^Within \d%$", detail.ThirdValue);
        Assert.Equal("same speed at every temperature", detail.LineLabel);
        Assert.Equal(2, detail.Line.Count);
        Assert.Equal(120, detail.Points.Count);
        Assert.Null(detail.SilentBelow);
    }

    [Fact]
    public void A_zero_rpm_card_shows_where_it_goes_silent()
    {
        // Still under 52°, turning above it: the chart shades what's below.
        var sensors = new[] { "/gpu-nvidia/0/fan/1", "/gpu-nvidia/0/fan/2" }
            .Select(id => new Rigsight.Models.SensorItem(new Rigsight.Core.Protocol.SensorMeta { Id = id, Name = "GPU Fan", Kind = Rigsight.Core.SensorKind.Fan }, "RTX", "GpuNvidia")).ToList();
        var card = new FanCard(sensors);
        long t0 = Rigsight.Core.Data.TimeUtil.ToUnix(DateTime.Today.AddHours(9));
        var minutes = Enumerable.Range(0, 200).ToDictionary(i => t0 + i * 60L, i => new Rigsight.Core.Data.SystemMinute { Ts = t0 + i * 60L, GpuTemp = 40 + i % 30, CpuTemp = 50 });
        var fan = minutes.Values.Select(m => new Rigsight.Core.Data.FanMinute(m.Ts, 1, m.GpuTemp < 52 ? 0 : (int)(900 + (m.GpuTemp!.Value - 52) * 60), 0)).ToList();
        card.Facts = Rigsight.Core.Reports.FanAnalysis.Of(true, fan, minutes);
        var detail = FansViewModel.DetailOf(card, [fan, fan], minutes, [], [], new Dictionary<long, Rigsight.Core.Data.AppRow>(), new Rigsight.Core.Settings.RigsightSettings(), DateTime.Today);
        Assert.Equal(52, detail.SilentBelow);
        Assert.Equal("Silent below about 52°", detail.FollowsNote);
    }

    [Fact]
    public void A_refresh_keeps_the_rows_and_the_pick()
    {
        var (vm, _) = Page();
        var (rows, picked) = Ui.Run(() =>
        {
            vm.Selected = vm.Fans[2];
            return (vm.Fans.ToList(), vm.Selected);
        });
        Kit.Wait(vm.RefreshAsync);
        Ui.Run(() =>
        {
            Assert.Equal(rows, vm.Fans);
            Assert.Same(picked, vm.Selected);
        });
    }

    [Fact]
    public void A_cards_facts_put_its_fans_together()
    {
        var a = new FanFacts(FanFollows.Gpu, 200, 90, new FanPeak(1900, 100, 7));
        var b = new FanFacts(FanFollows.Gpu, 210, 80, new FanPeak(2050, 160, 7));
        var merged = FanCard.Merge([a, b]);
        Assert.Equal(FanFollows.Gpu, merged.Follows);
        Assert.Equal(80, merged.StoppedMinutes); // the card rested while all its fans stood still
        Assert.Equal(new FanPeak(2050, 160, 7), merged.Fastest);
        Assert.Same(a, FanCard.Merge([a]));
    }
}

/// <summary>The Temperatures page's "At rest" rows.</summary>
public sealed class RestRowTests
{
    [Fact]
    public void A_row_shows_today_and_the_usual_range()
    {
        Assert.Equal(("47°", "usually 44–45°"), (new RestRow("CPU", new RestReading(47.2, 60, 44.1, 45.4)).TodayText,
            new RestRow("CPU", new RestReading(47.2, 60, 44.1, 45.4)).UsualText));
        Assert.Equal("usually 36°", new RestRow("GPU", new RestReading(48, 60, 36.1, 36.3)).UsualText);
        Assert.Equal(("—", ""), (new RestRow("GPU", new RestReading(null, 0, null, null)).TodayText, new RestRow("GPU", new RestReading(null, 0, null, null)).UsualText));
    }
}
