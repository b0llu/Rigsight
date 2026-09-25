using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using Rigsight.Core.Protocol;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.AppUi;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// Nothing the app throws away stays in memory: pages replaced by a theme change, a deleted dashboard, the processes
/// of an app that was closed again. And going round every page and theme doesn't make the heap grow.
/// </summary>
[Collection("UI")]
public sealed class MemoryLeakTests(AppHost host) : IClassFixture<AppHost>
{
    private static Dictionary<string, FrameworkElement> Views(MainWindow window) =>
        (Dictionary<string, FrameworkElement>)typeof(MainWindow).GetField("_pages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    /// <summary>Full collections, letting the UI thread finish what it has queued in between (unloading, animations).</summary>
    private static void Collect()
    {
        for (int i = 0; i < 4; i++)
        {
            Ui.Pump(30);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private void Theme(string theme)
    {
        Ui.Run(() => host.Shell.Settings.Update(s => s.Theme = theme));
        Assert.True(Ui.WaitFor(() => ThemeManager.IsLight == (theme == ThemeManager.Light)));
        Ui.Pump(50);
    }

    private void VisitEveryPage(int settleMs)
    {
        foreach (var page in AppHost.Pages) host.Show(page, settleMs);
        Ui.Run(() => host.Shell.CurrentPage = "home");
    }

    // A separate method, so no reference to a view outlives it on the test's stack.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<(string Page, WeakReference View)> WeakViews() =>
        Ui.Run(() => Views(host.Window).Select(kv => (kv.Key, new WeakReference(kv.Value))).ToList());

    [Fact]
    [Trait("Category", "Perf")]
    public void Pages_replaced_by_a_theme_change_are_freed()
    {
        Ui.TakeProblems();
        try
        {
            for (int round = 0; round < 3; round++)
            {
                VisitEveryPage(150);
                var views = WeakViews();
                Assert.Equal(AppHost.Pages.Length, views.Count);
                Theme(round % 2 == 0 ? ThemeManager.Light : ThemeManager.Dark);
                Collect();
                var alive = views.Where(v => v.View.IsAlive).Select(v => v.Page).ToList();
                // The page on screen (home) is built again, never the same object.
                Assert.True(alive.Count == 0, $"still in memory after a theme change: {string.Join(", ", alive)}");
                Assert.True(Ui.Run(() => Views(host.Window).Count) <= 1);
            }
        }
        finally
        {
            Theme(ThemeManager.Dark);
        }
        Ui.AssertNoProblems("theme changes");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Going_round_every_page_and_theme_does_not_grow_the_heap()
    {
        Ui.TakeProblems();
        var sw = Stopwatch.StartNew();
        try
        {
            void Cycle(int i)
            {
                VisitEveryPage(20);
                host.Agent.Tick(i);
                host.Agent.Procs(40 + i % 20);
                Theme(ThemeManager.Light);
                Theme(ThemeManager.Dark);
            }
            for (int i = 0; i < 3; i++) Cycle(i); // warm up: everything cached once, JIT done
            // The heap after each round. A single reading moves a few MB either way (caches, the GC's own state), so
            // the first five rounds are compared with the last five: a real leak (a page, a chart, a list kept per
            // round) grows it steadily, by far more than that.
            var heap = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                Cycle(i);
                Collect();
                heap.Add(GC.GetTotalMemory(forceFullCollection: true) / 1048576.0);
            }
            double Median(IEnumerable<double> values) => values.Order().ElementAt(2);
            double growthMb = Median(heap.TakeLast(5)) - Median(heap.Take(5));
            File.WriteAllText(Path.Combine(TestEnvironment.DataDir, "perf-heap.txt"),
                $"heap per round (MB): {string.Join(" ", heap.Select(h => h.ToString("0.0")))}; growth {growthMb:0.00} MB; {sw.Elapsed.TotalSeconds:0} s");
            // Budget: measured -1.9 to +0.3 MB on the development PC; a few MB at least, for the GC's own swings.
            Assert.True(growthMb < 6, $"the heap grew {growthMb:0.00} MB over 20 rounds of every page in both themes: {string.Join(" ", heap.Select(h => h.ToString("0.0")))}");
        }
        finally
        {
            Theme(ThemeManager.Dark);
        }
        Ui.AssertNoProblems("rounds of every page and theme");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (WeakReference Page, WeakReference View, WeakReference Tile) MakeAndDeleteDashboard()
    {
        return Ui.Run(() =>
        {
            var shell = host.Shell;
            shell.NewPageCommand.Execute(null);
            var page = shell.CustomPages[^1];
            page.SensorToAdd = shell.Live.CpuTemp;
            page.AddSensorTileCommand.Execute(null);
            Ui.Pump(100);
            var view = Views(host.Window)[page.NavKey];
            var tile = page.Tiles[^1];
            shell.ConfirmDelete = _ => true;
            page.DeletePageCommand.Execute(null);
            return (new WeakReference(page), new WeakReference(view), new WeakReference(tile));
        });
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_deleted_dashboard_is_freed()
    {
        var (page, view, tile) = MakeAndDeleteDashboard();
        host.Agent.Tick(3);
        host.Agent.Broadcast(Fixtures.Hello(host.Agent.Settings)); // live data and settings keep flowing
        Ui.Pump(200);
        Collect();
        Assert.False(page.IsAlive, "the dashboard's view model is still in memory");
        Assert.False(view.IsAlive, "the dashboard's page is still in memory");
        Assert.False(tile.IsAlive, "a tile of the dashboard is still in memory");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> OpenAndClose(LiveData live, ProcRow row, int times)
    {
        var children = new List<WeakReference>();
        for (int i = 0; i < times; i++)
        {
            live.ToggleProcessesCommand.Execute(row);
            for (int k = 0; k < 3; k++) // a few lists with the detail while it's open
            {
                live.ApplyProcs(Procs(detail: true));
                children.AddRange(row.Children.Select(c => new WeakReference(c)));
            }
            live.ToggleProcessesCommand.Execute(row);
            live.ApplyProcs(Procs(detail: false));
        }
        return children;
    }

    private static List<ProcInfo> Procs(bool detail)
    {
        var chrome = Kit.Proc("chrome.exe", 4000, count: 30, window: true);
        if (detail) chrome.Processes = Kit.Detail(30);
        return [chrome, Kit.Proc("game.exe", 8000, window: true), Kit.Proc("svchost.exe", 1600, count: 90)];
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Processes_of_an_app_closed_again_are_freed()
    {
        var (_, live) = Kit.Greeted();
        var sent = new List<string?>();
        List<WeakReference> children = [];
        Ui.Run(() =>
        {
            live.ProcessDetailChanged += sent.Add;
            live.ApplyProcs(Procs(detail: false));
            var chrome = live.Procs.Single(p => p.Exe == "chrome.exe");
            children = OpenAndClose(live, chrome, 200);
            Assert.Empty(chrome.Children);
        });
        Assert.Equal(200 * 3 * 30, children.Count);
        Assert.Equal(400, sent.Count);
        Assert.Null(sent[^1]);
        Collect();
        int alive = children.Count(c => c.IsAlive);
        Assert.True(alive == 0, $"{alive} process rows of a closed app are still in memory");
        GC.KeepAlive(live);
    }
}
