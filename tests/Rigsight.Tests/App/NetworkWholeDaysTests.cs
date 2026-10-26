using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Network page's biggest hours and days as their rows say them (the bar in the chart's colours, the top apps), the
/// connection's squares for a week, month or year and what hovering them says, and durations in the app's one wording.
/// </summary>
[Collection("UI")]
public sealed class NetworkWholeDaysTests
{
    private const long MB = 1L << 20, GB = 1L << 30;
    private const long Steam = 1, Chrome = 2, Stremio = 3, Claude = 4;

    private static readonly Dictionary<long, string> Names = new()
    {
        [Steam] = "Steam", [Chrome] = "Google Chrome", [Stremio] = "Stremio", [Claude] = "Claude Code",
    };

    private static NetAppUse Use(long app, long down) => new() { App = app, Down = down };

    [Fact]
    public void A_biggest_days_bar_has_the_charts_colours_and_grey_for_every_other_app()
    {
        // Tue 13 Oct 2026: Steam 50 GB and Google Chrome 9 GB have colours of their own on the chart; Stremio hasn't.
        var day = new DateTime(2026, 10, 13);
        var s = new NetStretch(day, day.AddDays(1), 88 * GB, [Use(Steam, 50 * GB), Use(Stremio, 27 * GB), Use(Chrome, 9 * GB), Use(Claude, 2 * GB)]);
        var row = NetworkViewModel.BiggestRow(s, ReportRange.Month, [Steam, Chrome], Names);

        Assert.Equal("Tue 13 Oct", row.When);
        Assert.Equal("88 GB", row.Size);
        var keys = Rigsight.Controls.NetBarsChart.AppBrushKeys;
        Assert.Equal([keys[0], keys[1], Rigsight.Controls.NetBarsChart.OtherBrushKey], row.Segments.Select(p => p.BrushKey));
        Assert.Equal(["Steam · 50 GB", "Google Chrome · 9.0 GB", "Every other app · 29 GB"], row.Segments.Select(p => p.Tip));
        Assert.Equal(NetworkViewModel.BarRoom, row.Segments.Sum(p => p.Width), 6);
        Assert.Equal(NetworkViewModel.BarRoom * 50 / 88, row.Segments[0].Width, 6);
        // The top three by name, coloured or not.
        Assert.Equal("Steam 50 GB · Stremio 27 GB · Google Chrome 9.0 GB", row.AppsText);
    }

    [Fact]
    public void A_biggest_hour_says_its_hour_and_without_apps_is_the_pc()
    {
        var hour = new DateTime(2026, 10, 15, 11, 0, 0);
        var row = NetworkViewModel.BiggestRow(new NetStretch(hour, hour.AddHours(1), 960 * MB, [Use(Chrome, 919 * MB)]), ReportRange.Day, [Chrome], Names);
        Assert.Equal("11 AM – 12 PM", row.When);
        Assert.Equal("Google Chrome 919 MB", row.AppsText);
        Assert.Equal(["Google Chrome · 919 MB", "Every other app · 41 MB"], row.Segments.Select(p => p.Tip));

        // A standard account: no app's use is read, the bar is the PC's.
        row = NetworkViewModel.BiggestRow(new NetStretch(hour, hour.AddHours(1), 960 * MB, []), ReportRange.Day, [], Names);
        Assert.Equal("This PC · 960 MB", Assert.Single(row.Segments).Tip);
        Assert.False(row.HasAppsText);
        // The same row read again is the same row (the card keeps it).
        Assert.Equal(row, NetworkViewModel.BiggestRow(new NetStretch(hour, hour.AddHours(1), 960 * MB, []), ReportRange.Day, [], Names));
    }

    private static NetDrop Drop(DateTime at, int seconds) => new(TimeUtil.ToUnix(at), TimeUtil.ToUnix(at) + seconds, NetDropKind.Internet);

    [Fact]
    public void Hovering_a_day_says_how_it_went_and_how_much_was_recorded()
    {
        var today = new DateTime(2026, 10, 15);
        var fri = new DateTime(2026, 10, 2);
        Assert.Equal("Fri 2 Oct\nUp all day\nRecorded 9h 40m", NetworkViewModel.DayTip(new NetDayLink(fri, NetDayState.Up, 580, []), today));
        Assert.Equal("Fri 2 Oct\nDropped once at 6:12 PM, 43m\nRecorded 9h 40m",
            NetworkViewModel.DayTip(new NetDayLink(fri, NetDayState.Dropped, 580, [Drop(fri.AddHours(18).AddMinutes(12), 43 * 60)]), today));
        Assert.Equal("Fri 2 Oct\nDropped twice\n9:00 AM, 2m\n6:12 PM, 1h 05m\nRecorded 9h 40m",
            NetworkViewModel.DayTip(new NetDayLink(fri, NetDayState.Dropped, 580, [Drop(fri.AddHours(9), 120), Drop(fri.AddHours(18).AddMinutes(12), 65 * 60)]), today));
        Assert.Equal("Fri 2 Oct\nNot recorded", NetworkViewModel.DayTip(new NetDayLink(fri, NetDayState.NotRecorded, 0, []), today));
        Assert.Equal("Fri 16 Oct\nStill to come", NetworkViewModel.DayTip(new NetDayLink(today.AddDays(1), NetDayState.ToCome, 0, []), today));
        // Today isn't over: no drops so far isn't "all day".
        Assert.Equal("Thu 15 Oct\nNo drops so far\nRecorded 43m", NetworkViewModel.DayTip(new NetDayLink(today, NetDayState.Up, 43, []), today));
    }

    [Fact]
    public void A_year_is_twelve_rows_each_as_long_as_its_month()
    {
        var from = new DateTime(2026, 1, 1);
        var links = NetReportBuilder.DayLinks(from, from.AddYears(1), new DateTime(2026, 10, 15), [], []);
        var rows = NetworkViewModel.YearRows(links, new DateTime(2026, 10, 15));
        Assert.Equal(["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"], rows.Select(m => m.Month));
        Assert.Equal([31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31], rows.Select(m => m.Days.Count));
        Assert.Equal(NetDayState.NotRecorded, rows[9].Days[14].State); // 15 Oct
        Assert.Equal(NetDayState.ToCome, rows[9].Days[15].State);      // 16 Oct
        Assert.StartsWith("Fri 16 Oct\n", rows[9].Days[15].Tip);
    }

    [Fact]
    public void The_connection_line_of_a_week_month_and_year()
    {
        var week = new DateTime(2026, 9, 28);
        var r = new NetReport
        {
            Range = ReportRange.Week, From = week, To = week.AddDays(7), Minutes = 1,
            Drops = [Drop(new DateTime(2026, 10, 2, 18, 12, 0), 43 * 60)],
            Unrecorded = [(week.AddHours(13), week.AddHours(20.5))],
        };
        // The drops are listed under the strip: the line says only what wasn't recorded, and of which period.
        Assert.Equal(("Dropped once", "7h 30m not recorded that week"), NetworkViewModel.DropsText(r, "that week"));
        Assert.Equal([new NetDropRow("Fri 2 Oct, 6:12 PM", "43m")], NetworkViewModel.DropList(r.Drops));

        var year = new NetReport
        {
            Range = ReportRange.Year, From = new DateTime(2026, 1, 1), To = new DateTime(2027, 1, 1), Minutes = 1,
            Drops = [Drop(new DateTime(2026, 9, 26, 21, 40, 0), 4 * 60), Drop(new DateTime(2026, 10, 2, 18, 12, 0), 43 * 60)],
            RecordedSince = new DateTime(2026, 9, 23),
        };
        Assert.Equal(("Dropped twice", "Longest 43m on Fri 2 Oct · recorded since 23 Sep"), NetworkViewModel.DropsText(year, "this year"));
        year.Drops = [];
        Assert.Equal(("No drops", "The internet stayed up this year · recorded since 23 Sep"), NetworkViewModel.DropsText(year, "this year"));
    }

    [Theory]
    [InlineData(ReportRange.Day)]
    [InlineData(ReportRange.Week)]
    [InlineData(ReportRange.Month)]
    [InlineData(ReportRange.Year)]
    public void The_page_lays_out_the_biggest_and_the_connection_for_each_period(ReportRange unit)
    {
        SharedData.EnsureSeeded();
        var (settings, live) = Kit.Greeted(hello: Fixtures.Hello());
        var vm = Ui.Run(() => new NetworkViewModel(new ReportService(settings), live));
        Ui.Run(() => vm.Unit = unit);
        Kit.Wait(vm.RefreshAsync);
        Ui.TakeProblems();
        var window = Ui.Run(() =>
        {
            var w = new System.Windows.Window
            {
                Content = new Views.NetworkView { DataContext = vm }, Left = -32000, Top = -32000, Width = 1400, Height = 2000,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            };
            w.Show();
            return w;
        });
        try
        {
            Ui.Pump(300);
            Ui.AssertNoProblems($"Network page, {unit}");
            Ui.Run(() =>
            {
                Assert.Equal(unit is ReportRange.Week or ReportRange.Month, vm.HasDays);
                Assert.Equal(unit == ReportRange.Year, vm.HasMonths);
                Assert.Equal(unit == ReportRange.Day, vm.Strip.Count == 96);
                // The seeded drop early today is listed under a week's and a month's strip only.
                Assert.Equal(unit is ReportRange.Week or ReportRange.Month, vm.HasDropRows);
                if (unit == ReportRange.Week) Assert.Equal(("Mon", "Sun", 7), (vm.DaysFrom, vm.DaysTo, vm.Days.Count));
                if (unit == ReportRange.Year) Assert.Equal(12, vm.Months.Count);
                // For a look at it: RIGSIGHT_NET_PNG names a folder.
                if (Environment.GetEnvironmentVariable("RIGSIGHT_NET_PNG") is { Length: > 0 } folder)
                    Ui.SavePng((System.Windows.FrameworkElement)window.Content, System.IO.Path.Combine(folder, $"network-{unit}.png"));
            });
        }
        finally
        {
            Ui.Run(window.Close);
        }
    }

    [Fact]
    public void Durations_are_written_as_everywhere_else()
    {
        Assert.Equal("7h 30m", NetworkViewModel.Took(7.5 * 3600));
        Assert.Equal("43m", NetworkViewModel.Took(43 * 60));
        Assert.Equal("40s", NetworkViewModel.Took(40));
        Assert.Equal("1s", NetworkViewModel.Took(0)); // a drop is never "0s"
    }
}
