using System.Windows.Controls;
using Rigsight.Controls;
using Rigsight.Core.Reports;
using Rigsight.Tests.App;
using Rigsight.Tests.Support;
using Rigsight.Views;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// The Fans page's Today card (approved mock-up 2026-11-14) in the real window: full width after the even row, the
/// strip of the day and its three facts, a fact's stretch marked, and a week's and a year's bars. Pictures go to
/// screens\fans-today-*.png.
/// </summary>
[Collection("UI")]
public sealed class FansTodayLookTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");

    [Fact]
    public void The_today_card_spans_the_page_under_the_even_row()
    {
        Ui.TakeProblems();
        host.Show("fans", 1500);
        var fans = host.Shell.Fans;
        Ui.WaitFor(() => Ui.Run(() => fans.HasToday), 10_000);
        var page = Ui.Run(() => Visuals.Descendants<FansView>(host.Window).Single());
        var scroller = Ui.Run(() => Visuals.Descendants<ScrollViewer>(page).First());
        var strip = Ui.Run(() => Visuals.Descendants<FanStrip>(page).Single());
        Ui.Run(() =>
        {
            scroller.UpdateLayout();
            scroller.ScrollToBottom();
            // As wide as the page's cards: the full row, not a third of it.
            var card = Visuals.Ancestors<SmoothBorder>(strip).First();
            Assert.True(card.ActualWidth > page.ActualWidth * 0.8, $"card {card.ActualWidth} of {page.ActualWidth}");
            Assert.True(strip.ActualHeight >= 100);
        });
        Ui.Pump(500);
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "fans-today-1-day.png")));
        foreach (var id in new[] { "loud", "starts", "sil" })
        {
            Ui.Run(() => strip.Mark = id);
            Ui.Pump(200);
            Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, $"fans-today-2-mark-{id}.png")));
        }
        Ui.Run(() => strip.Mark = null);

        foreach (var (unit, name) in new[] { (ReportRange.Week, "week"), (ReportRange.Month, "month"), (ReportRange.Year, "year") })
        {
            Ui.Run(() => fans.Unit = unit);
            Kit.Wait(fans.RefreshAsync);
            Ui.Pump(600);
            Ui.Run(() => { scroller.UpdateLayout(); scroller.ScrollToBottom(); });
            Ui.Pump(300);
            Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, $"fans-today-3-{name}.png")));
        }
        Ui.Run(() => fans.Unit = ReportRange.Day);
        Kit.Wait(fans.RefreshAsync);
        Ui.AssertNoProblems("fans today card");
    }
}
