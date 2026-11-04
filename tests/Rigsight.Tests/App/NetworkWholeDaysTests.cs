using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Network page for each period: the drops' words for a week and a year, the page laid out with no binding
/// problems, and durations in the app's one wording.
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

    private static NetDrop Drop(DateTime at, int seconds) => new(TimeUtil.ToUnix(at), TimeUtil.ToUnix(at) + seconds, NetDropKind.Internet);

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
        // The longest drop and what wasn't recorded: the hover of the figures line's drops.
        Assert.Equal(("Dropped once", "Longest 43m on Fri 2 Oct · 7h 30m not recorded"), NetworkViewModel.DropsText(r, "that week"));

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
    public void The_page_lays_out_each_period(ReportRange unit)
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
                Assert.True(vm.HasData);
                // The seeded drop early today, in every period that has today.
                Assert.Single(vm.Drops);
                Assert.True(vm.HasHappenings);
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
