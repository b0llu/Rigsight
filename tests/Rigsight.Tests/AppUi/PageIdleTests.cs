using System.Diagnostics;
using System.Windows.Threading;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// Once a page is open and filled, the window has nothing left to do: work that keeps the UI thread from ever going
/// idle shows as a page that answers late. (A turning arc put inside the search box's template did that for seconds on
/// every page with a search box, with nothing on screen to show for it.)
/// </summary>
[Collection("UI")]
public sealed class PageIdleTests(AppHost host) : IClassFixture<AppHost>
{
    public static TheoryData<string> Pages => [.. AppHost.Pages];

    [Theory]
    [MemberData(nameof(Pages))]
    public void An_open_page_leaves_the_window_idle(string page)
    {
        host.Show(page, 600);
        var watch = Stopwatch.StartNew();
        long worst = 0;
        while (watch.ElapsedMilliseconds < 1000)
        {
            var one = Stopwatch.StartNew();
            Ui.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            worst = Math.Max(worst, one.ElapsedMilliseconds);
            Thread.Sleep(5);
        }
        Assert.True(worst < 500, $"{page}: the window was busy for {worst} ms with nothing to do");
    }
}
