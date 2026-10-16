using System.Windows;
using System.Windows.Controls;
using Rigsight.Core.Protocol;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// One app, one row: the live list of running apps (Memory's "Using memory right now", the Processes page) never shows
/// an app twice, however its figures move, apps come and go and the window is resized while it is kept sorted. The
/// same app was seen on two rows with the same figures (Microsoft Defender, Google Chrome, 2026-10-11 and 2026-10-15).
/// </summary>
[Collection("UI")]
public sealed class ProcessRowsTests
{
    private static readonly string[] Exes =
        [.. Enumerable.Range(0, 40).Select(i => $"app{i}.exe"), "MsMpEng.exe", "chrome.exe", "Discord.exe", "svchost.exe"];

    private static List<ProcInfo> Batch(Random rng, HashSet<string> running)
    {
        // Now and then an app leaves or one comes; the rest move their memory, often past each other.
        foreach (var exe in Exes)
            if (rng.NextDouble() < 0.05) { if (!running.Remove(exe)) running.Add(exe); }
        return [.. running.Select(exe => Kit.Proc(exe, 50 + rng.Next(0, 800)))];
    }

    private static void AssertOneRowEach(LiveData live, System.ComponentModel.ICollectionView view, ListBox list, int round)
    {
        var inView = view.Cast<ProcRow>().ToList();
        Assert.True(inView.Distinct().Count() == inView.Count, $"Round {round}: the sorted view holds an app twice ({string.Join(", ", inView.GroupBy(p => p.Exe).Where(g => g.Count() > 1).Select(g => g.Key))}).");
        Assert.True(inView.Count == live.Procs.Count(p => view.Filter?.Invoke(p) ?? true), $"Round {round}: the view has {inView.Count} rows, the list {live.Procs.Count}.");
        var shown = Visuals.Descendants<ListBoxItem>(list).Where(c => c.IsVisible).Select(c => c.DataContext).OfType<ProcRow>().ToList();
        Assert.True(shown.Distinct().Count() == shown.Count, $"Round {round}: two rows on screen show {string.Join(", ", shown.GroupBy(p => p.Exe).Where(g => g.Count() > 1).Select(g => g.Key))}.");
        // Each row on screen shows the app the list has in its place: rows left in an old order were the first sign
        // (on coming back to the page, before an app showed twice).
        var stale = Visuals.Descendants<ListBoxItem>(list).Where(c => c.IsVisible)
            .Select(c => (Row: c.DataContext as ProcRow, At: list.ItemContainerGenerator.IndexFromContainer(c)))
            .Where(x => x.At < 0 || x.At >= inView.Count || !ReferenceEquals(inView[x.At], x.Row)).ToList();
        Assert.True(stale.Count == 0, $"Round {round}: {stale.Count} rows on screen are out of step with the list ({string.Join(", ", stale.Select(x => $"{x.Row?.Exe} at {x.At}"))}).");
    }

    [Fact]
    public void No_app_is_shown_on_two_rows_while_the_list_moves_and_the_window_is_resized()
    {
        var (settings, live) = Kit.Greeted();
        var vm = Ui.Run(() =>
        {
            var made = new MemoryViewModel(new ReportService(settings), live);
            live.ApplyTick(Pc.Tick(1_760_000_000_000, 40, ("/ram/data/0", 16), ("/ram/data/1", 16)));
            made.SetShown(true);
            return made;
        });
        var window = Ui.Run(() =>
        {
            var w = new Window
            {
                Content = new Views.MemoryView { DataContext = vm }, Left = -32000, Top = -32000, Width = 1500, Height = 1100,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            };
            w.Show();
            return w;
        });
        try
        {
            var rng = new Random(7);
            var running = new HashSet<string>(Exes.Take(30));
            var list = Ui.Run(() => (ListBox)((Views.MemoryView)window.Content).FindName("LiveList"));
            for (int round = 0; round < 400; round++)
            {
                var batch = Batch(rng, running);
                Ui.Run(() =>
                {
                    live.ApplyProcs(batch);
                    // A resize now and then, as when the window is moved between screens or snapped (the first sighting
                    // came right after one), and sometimes in the same turn as the readings.
                    if (round % 7 == 0) window.Height = 700 + rng.Next(0, 500);
                    if (round % 31 == 0) window.Width = 1100 + rng.Next(0, 600);
                    if (round % 53 == 0) { vm.SetShown(false); vm.SetShown(true); }
                });
                Ui.Pump(round % 5 == 0 ? 30 : 2);
                Ui.Run(() => AssertOneRowEach(live, live.ProcsView, list, round));
            }
        }
        finally
        {
            Ui.Run(window.Close);
        }
    }

    [Fact]
    public void No_app_is_shown_on_two_rows_after_leaving_the_memory_page_and_coming_back()
    {
        var (settings, live) = Kit.Greeted();
        var page = Ui.Run<FrameworkElement>(() =>
        {
            var vm = new MemoryViewModel(new ReportService(settings), live);
            live.ApplyTick(Pc.Tick(1_760_000_000_000, 40, ("/ram/data/0", 16), ("/ram/data/1", 16)));
            return new Views.MemoryView { DataContext = vm };
        });
        LeaveAndComeBack(live, page, live.ProcsView);
    }

    [Fact]
    public void No_app_is_shown_on_two_rows_after_leaving_the_processes_page_and_coming_back()
    {
        var (settings, live) = Kit.Greeted();
        ProcessesViewModel? vm = null;
        var page = Ui.Run<FrameworkElement>(() =>
        {
            vm = new ProcessesViewModel(new ReportService(settings), live, (_, _) => { });
            live.ApplyTick(Pc.Tick(1_760_000_000_000, 40, ("/ram/data/0", 16), ("/ram/data/1", 16)));
            return new Views.ProcessesView { DataContext = vm };
        });
        LeaveAndComeBack(live, page, vm!.Apps);
    }

    private static void LeaveAndComeBack(LiveData live, FrameworkElement page, System.ComponentModel.ICollectionView view)
    {
        var window = Ui.Run(() =>
        {
            var w = new Window
            {
                Content = page, Left = -32000, Top = -32000, Width = 1500, Height = 1100,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            };
            w.Show();
            return w;
        });
        try
        {
            var rng = new Random(11);
            var running = new HashSet<string>(Exes.Take(30));
            var list = Ui.Run(() => (ListBox)page.FindName("LiveList"));
            for (int visit = 0; visit < 25; visit++)
            {
                // On the page for a while: sorted live.
                for (int i = 0; i < 6; i++)
                {
                    var batch = Batch(rng, running);
                    Ui.Run(() => live.ApplyProcs(batch));
                    Ui.Pump(5);
                }
                Ui.Run(() => AssertOneRowEach(live, view, list, visit * 100));
                // Away on another page (the view is kept, as the app keeps it): readings keep coming, unsorted.
                Ui.Run(() => window.Content = new Grid());
                Ui.Pump(5);
                for (int i = 0; i < 20; i++)
                {
                    var batch = Batch(rng, running);
                    Ui.Run(() => live.ApplyProcs(batch));
                }
                // Back: sorted live again, and a resize as the page fits its list.
                Ui.Run(() =>
                {
                    window.Content = page;
                    if (visit % 3 == 0) window.Height = 800 + rng.Next(0, 400);
                });
                Ui.Pump(30);
                Ui.Run(() => AssertOneRowEach(live, view, list, visit * 100 + 1));
                var after = Batch(rng, running);
                Ui.Run(() => live.ApplyProcs(after));
                Ui.Pump(30);
                Ui.Run(() => AssertOneRowEach(live, view, list, visit * 100 + 2));
            }
        }
        finally
        {
            Ui.Run(window.Close);
        }
    }
}
