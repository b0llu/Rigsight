using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Models;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>The Memory page's app list: rows, sorting, bars, shares, and opening an app to see its processes.</summary>
[Collection("UI")]
public sealed class ProcessListTests
{
    private static readonly long T0 = TimeUtil.NowUnixMs();

    private static List<ProcInfo> Apps() =>
    [
        Kit.Proc("chrome.exe", 4000, count: 30, window: true, cpu: 3.2),
        Kit.Proc("game.exe", 8000, window: true, cpu: 25),
        Kit.Proc("svchost.exe", 1600, count: 90),
        Kit.Proc("discord.exe", 800, count: 7, window: true),
    ];

    /// <summary>Live data for a PC with 32 GB of memory, half of it in use.</summary>
    private static LiveData Greeted()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() => live.ApplyTick(Pc.Tick(T0, 40, ("/ram/data/0", 16), ("/ram/data/1", 16))));
        return live;
    }

    private static List<string> Sorted(LiveData live) => [.. live.ProcsView.Cast<ProcRow>().Select(p => p.Exe)];

    [Fact]
    public void Apps_are_added_updated_in_place_and_removed()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            live.ApplyProcs(Apps());
            Assert.Equal(4, live.Procs.Count);
            var chrome = live.Procs.Single(p => p.Exe == "chrome.exe");
            Assert.Equal(30, chrome.Count);

            var next = Apps();
            next[0].MemMB = 5000;
            next[0].Exe = "Chrome.EXE"; // the same app, however Windows spells it
            next.RemoveAt(2);
            next.Add(Kit.Proc("steam.exe", 300));
            live.ApplyProcs(next);
            Assert.Same(chrome, live.Procs.Single(p => p.Exe == "chrome.exe"));
            Assert.Equal(5000, chrome.MemMB);
            Assert.DoesNotContain(live.Procs, p => p.Exe == "svchost.exe");
            Assert.Contains(live.Procs, p => p.Exe == "steam.exe");
            Assert.Equal(4, live.Procs.Count);

            live.ApplyProcs([]);
            Assert.Empty(live.Procs);
            Assert.Empty(live.TopMemory);
        });
    }

    [Fact]
    public void Parts_of_windows_are_not_apps_and_compressed_memory_is_shown_with_the_system()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            var procs = Apps();
            procs.Add(Kit.Proc("Memory Compression", 2048));
            procs.Add(Kit.Proc("System", 500));
            procs.Add(Kit.Proc("Registry", 90));
            live.ApplyProcs(procs);
            Assert.Equal(4, live.Procs.Count);
            Assert.Equal(2, live.Compressed.Value);
            // 16 GB in use, 2 of it compressed, 16 free.
            Assert.Equal(14, live.MemAppsWidth.Value);
            Assert.Equal(2, live.MemCompressedWidth.Value);
            Assert.Equal(16, live.MemFreeWidth.Value);
            Assert.Equal("14.0 GB", live.MemAppsText);

            // Task Manager's name for it works too; gone means nothing compressed.
            live.ApplyProcs([Kit.Proc("MemCompression", 1024)]);
            Assert.Equal(1, live.Compressed.Value);
            live.ApplyProcs([]);
            Assert.Null(live.Compressed.Value);
            Assert.Equal(0, live.MemCompressedWidth.Value);
        });
    }

    [Fact]
    public void Compressed_memory_never_exceeds_what_is_in_use()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            live.ApplyProcs([Kit.Proc("Memory Compression", 64 * 1024)]);
            Assert.Equal(16, live.MemCompressedWidth.Value);
            Assert.Equal(0, live.MemAppsWidth.Value);
        });
    }

    [Fact]
    public void Memory_split_waits_for_the_ram_readings()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.ApplyProcs([Kit.Proc("Memory Compression", 1024)]);
            Assert.Equal("—", live.MemAppsText);
            Assert.Equal(1, live.MemFreeWidth.Value);
        });
    }

    [Fact]
    public void The_list_is_sorted_by_memory_biggest_first()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            live.SetProcessSorting(true);
            live.ApplyProcs(Apps());
            Assert.Equal(["game.exe", "chrome.exe", "svchost.exe", "discord.exe"], Sorted(live));
            var next = Apps();
            next[3].MemMB = 9000;
            live.ApplyProcs(next);
        });
        // Live sorting moves the row once data binding runs.
        Ui.Pump();
        Ui.Run(() =>
        {
            Assert.Equal(["discord.exe", "game.exe", "chrome.exe", "svchost.exe"], Sorted(live));
            live.SetProcessSorting(false);
            live.SetProcessSorting(false); // twice is harmless
            var next = Apps();
            live.ApplyProcs(next);
        });
        Ui.Pump();
        // Not sorted live while the Memory page is closed: the order stays until it's shown again.
        Assert.Equal(["discord.exe", "game.exe", "chrome.exe", "svchost.exe"], Ui.Run(() => Sorted(live)));
        Ui.Run(() => live.SetProcessSorting(true));
        Assert.Equal(["game.exe", "chrome.exe", "svchost.exe", "discord.exe"], Ui.Run(() => Sorted(live)));
    }

    [Fact]
    public void Top_memory_is_the_six_biggest_and_only_replaced_when_it_changes()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            var procs = Enumerable.Range(1, 10).Select(i => Kit.Proc($"app{i}.exe", i * 100)).ToList();
            live.ApplyProcs(procs);
            Assert.Equal(["app10.exe", "app9.exe", "app8.exe", "app7.exe", "app6.exe", "app5.exe"], live.TopMemory.Select(p => p.Exe));
            var list = live.TopMemory;
            procs[0].MemMB = 150; // below the top six: no new list
            live.ApplyProcs(procs);
            Assert.Same(list, live.TopMemory);
            procs[0].MemMB = 5000;
            live.ApplyProcs(procs);
            Assert.NotSame(list, live.TopMemory);
            Assert.Equal("app1.exe", live.TopMemory[0].Exe);
        });
    }

    [Fact]
    public void The_biggest_app_fills_its_bar_and_the_rest_compare_to_it()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            live.ApplyProcs(Apps());
            var bars = live.Procs.ToDictionary(p => p.Exe, p => p.Bar);
            Assert.Equal(100, bars["game.exe"]);
            Assert.Equal(50, bars["chrome.exe"]);
            Assert.Equal(20, bars["svchost.exe"]);
            Assert.Equal(10, bars["discord.exe"]);
        });
    }

    [Fact]
    public void Showing_only_windowed_apps_measures_bars_against_the_biggest_shown()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            var procs = Apps();
            procs.Add(Kit.Proc("service.exe", 20_000)); // bigger than anything with a window
            live.ApplyProcs(procs);
            Assert.Equal(40, live.Procs.Single(p => p.Exe == "game.exe").Bar);
            live.OnlyWindowedApps = true;
            Assert.Equal(["game.exe", "chrome.exe", "discord.exe"], Sorted(live));
            Assert.Equal(100, live.Procs.Single(p => p.Exe == "game.exe").Bar);
            Assert.Equal(50, live.Procs.Single(p => p.Exe == "chrome.exe").Bar);
            // Hidden rows never overflow their bar.
            Assert.Equal(100, live.Procs.Single(p => p.Exe == "service.exe").Bar);
            // New lists keep the filter.
            live.ApplyProcs(procs);
            Assert.Equal(3, live.ProcsView.Cast<ProcRow>().Count());
            live.OnlyWindowedApps = false;
            Assert.Equal(5, live.ProcsView.Cast<ProcRow>().Count());
            Assert.Equal(40, live.Procs.Single(p => p.Exe == "game.exe").Bar);
        });
    }

    [Fact]
    public void No_windowed_apps_or_no_memory_leaves_every_bar_empty()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            live.ApplyProcs([Kit.Proc("a.exe", 0), Kit.Proc("b.exe", 0)]);
            Assert.All(live.Procs, p => Assert.Equal(0, p.Bar));
            live.ApplyProcs([Kit.Proc("a.exe", 500), Kit.Proc("b.exe", 100)]);
            live.OnlyWindowedApps = true;
            Assert.All(live.Procs, p => Assert.Equal(0, p.Bar));
            Assert.Empty(live.ProcsView.Cast<ProcRow>());
        });
    }

    [Fact]
    public void Memory_share_is_out_of_all_the_pcs_memory()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            live.ApplyProcs([Kit.Proc("a.exe", 32 * 1024 / 4), Kit.Proc("tiny.exe", 10), Kit.Proc("b.exe", 1638.4)]);
            var rows = live.Procs.ToDictionary(p => p.Exe);
            Assert.Equal(25, rows["a.exe"].MemShare, 6);
            Assert.Equal("25% of your memory", rows["a.exe"].MemShareText);
            Assert.Equal("5% of your memory", rows["b.exe"].MemShareText);
            Assert.Equal("Under 0.1% of your memory", rows["tiny.exe"].MemShareText);
        });
    }

    [Fact]
    public void Memory_share_is_zero_until_the_memory_size_is_known()
    {
        var (_, live) = Kit.Greeted();
        Ui.Run(() =>
        {
            live.ApplyProcs(Apps());
            Assert.All(live.Procs, p => Assert.Equal(0, p.MemShare));
        });
    }

    // ── A row's own texts ────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(0.04, "0%")]
    [InlineData(0.06, "0.1%")]
    [InlineData(5.26, "5.3%")]
    [InlineData(9.94, "9.9%")]
    [InlineData(10, "10%")]
    [InlineData(55.6, "56%")]
    [InlineData(100, "100%")]
    public void Cpu_text(double cpu, string expected) => Assert.Equal(expected, ProcRow.FormatCpu(cpu));

    [Fact]
    public void Row_texts_follow_its_readings()
    {
        Ui.Run(() =>
        {
            var row = new ProcRow("x.exe");
            Assert.Equal("x.exe", row.Name);
            var changed = Kit.Changes(row, () => row.Update(Kit.Proc("x.exe", 1536, count: 3, cpu: 12, name: "X App")));
            Assert.Equal("X App", row.Name);
            Assert.Equal("1.5 GB", row.MemText);
            Assert.Equal("12%", row.CpuText);
            Assert.Equal("3 processes", row.CountText);
            Assert.True(row.CanExpand);
            foreach (var p in new[] { nameof(ProcRow.MemText), nameof(ProcRow.CpuText), nameof(ProcRow.CountText), nameof(ProcRow.CanExpand) })
                Assert.Contains(p, changed);
            row.Update(Kit.Proc("x.exe", 900, count: 1));
            Assert.Equal("900 MB", row.MemText);
            Assert.Equal("1 process", row.CountText);
            Assert.False(row.CanExpand);
        });
    }

    [Fact]
    public void A_new_path_brings_a_new_icon_and_a_missing_one_the_program_icon()
    {
        Ui.Run(() =>
        {
            var row = new ProcRow("x.exe");
            Assert.NotNull(row.Icon); // no path: Windows' program icon
            var p = Kit.Proc("x.exe", 1);
            p.Path = @"C:\definitely\not\here.exe";
            var changed = Kit.Changes(row, () => row.Update(p));
            Assert.Contains(nameof(ProcRow.Icon), changed);
            Assert.Same(Services.IconCache.Program, row.Icon);
            changed = Kit.Changes(row, () => row.Update(p));
            Assert.DoesNotContain(nameof(ProcRow.Icon), changed);
        });
    }

    // ── Opening an app to see its processes ──────────────────────────────

    private static (LiveData Live, List<string?> Asked) Expandable()
    {
        var live = Greeted();
        var asked = new List<string?>();
        Ui.Run(() =>
        {
            live.ProcessDetailChanged += apps => asked.Add(apps);
            live.ApplyProcs(Apps());
        });
        return (live, asked);
    }

    private static List<ProcInfo> AppsWithDetail(string exe)
    {
        var procs = Apps();
        var app = procs.Single(p => p.Exe == exe);
        app.Processes = Kit.Detail(app.Count, app.MemMB / app.Count);
        return procs;
    }

    [Fact]
    public void A_closed_row_shows_neither_children_nor_a_spinner()
    {
        var (live, _) = Expandable();
        Ui.Run(() => Assert.All(live.Procs, p => Assert.Equal((false, false, false), (p.IsExpanded, p.IsLoading, p.ShowChildren))));
    }

    [Fact]
    public void Opening_asks_the_agent_and_spins_until_the_processes_arrive()
    {
        var (live, asked) = Expandable();
        Ui.Run(() =>
        {
            var chrome = live.Procs.Single(p => p.Exe == "chrome.exe");
            var changed = Kit.Changes(chrome, () => live.ToggleProcessesCommand.Execute(chrome));
            Assert.Equal(["chrome.exe"], asked);
            Assert.Equal("chrome.exe", live.ExpandedApps);
            Assert.True(chrome.IsExpanded);
            Assert.True(chrome.IsLoading);
            Assert.False(chrome.ShowChildren);
            Assert.Contains(nameof(ProcRow.IsLoading), changed);

            // A list without the detail yet: still loading.
            live.ApplyProcs(Apps());
            Assert.True(chrome.IsLoading);

            live.ApplyProcs(AppsWithDetail("chrome.exe"));
            Assert.False(chrome.IsLoading);
            Assert.True(chrome.ShowChildren);
            Assert.Equal(30, chrome.Children.Count);
            Assert.Equal("PID 100", chrome.Children[0].PidText);

            // The detail stops coming for a moment: what's shown stays.
            live.ApplyProcs(Apps());
            Assert.Equal(30, chrome.Children.Count);
        });
    }

    [Fact]
    public void Closing_drops_the_children_and_tells_the_agent()
    {
        var (live, asked) = Expandable();
        Ui.Run(() =>
        {
            var chrome = live.Procs.Single(p => p.Exe == "chrome.exe");
            live.ToggleProcessesCommand.Execute(chrome);
            live.ApplyProcs(AppsWithDetail("chrome.exe"));
            live.ToggleProcessesCommand.Execute(chrome);
            Assert.False(chrome.IsExpanded);
            // Not kept for next time: reopening shows the spinner until fresh detail arrives.
            Assert.Empty(chrome.Children);
            Assert.False(chrome.ShowChildren);
            Assert.False(chrome.IsLoading);
            Assert.Equal(["chrome.exe", null], asked);
            Assert.Null(live.ExpandedApps);

            // Detail that still arrives for a closed app is ignored.
            live.ApplyProcs(AppsWithDetail("chrome.exe"));
            Assert.Empty(chrome.Children);

            live.ToggleProcessesCommand.Execute(chrome);
            Assert.True(chrome.IsLoading);
            Assert.Empty(chrome.Children);
            live.ApplyProcs(AppsWithDetail("chrome.exe"));
            Assert.True(chrome.ShowChildren);
        });
    }

    [Fact]
    public void Several_open_apps_are_asked_for_together_in_list_order()
    {
        var (live, asked) = Expandable();
        Ui.Run(() =>
        {
            var chrome = live.Procs.Single(p => p.Exe == "chrome.exe");
            var discord = live.Procs.Single(p => p.Exe == "discord.exe");
            var svchost = live.Procs.Single(p => p.Exe == "svchost.exe");
            live.ToggleProcessesCommand.Execute(discord);
            live.ToggleProcessesCommand.Execute(chrome);
            live.ToggleProcessesCommand.Execute(svchost);
            Assert.Equal("chrome.exe|svchost.exe|discord.exe", live.ExpandedApps);
            Assert.Equal("chrome.exe|svchost.exe|discord.exe", asked[^1]);
            live.ToggleProcessesCommand.Execute(svchost);
            Assert.Equal("chrome.exe|discord.exe", asked[^1]);
        });
    }

    [Fact]
    public void An_app_with_one_process_does_not_open()
    {
        var (live, asked) = Expandable();
        Ui.Run(() =>
        {
            var game = live.Procs.Single(p => p.Exe == "game.exe");
            live.ToggleProcessesCommand.Execute(game);
            Assert.False(game.IsExpanded);
            Assert.Empty(asked);
        });
    }

    [Fact]
    public void An_open_app_down_to_one_process_can_still_be_closed()
    {
        var (live, asked) = Expandable();
        Ui.Run(() =>
        {
            var discord = live.Procs.Single(p => p.Exe == "discord.exe");
            live.ToggleProcessesCommand.Execute(discord);
            var procs = Apps();
            procs[3].Count = 1;
            live.ApplyProcs(procs);
            Assert.False(discord.CanExpand);
            live.ToggleProcessesCommand.Execute(discord);
            Assert.False(discord.IsExpanded);
            Assert.Equal(["discord.exe", null], asked);
        });
    }

    [Fact]
    public void An_open_app_that_quits_leaves_the_detail_request()
    {
        var (live, _) = Expandable();
        Ui.Run(() =>
        {
            live.ToggleProcessesCommand.Execute(live.Procs.Single(p => p.Exe == "chrome.exe"));
            live.ToggleProcessesCommand.Execute(live.Procs.Single(p => p.Exe == "discord.exe"));
            live.ApplyProcs([.. Apps().Where(p => p.Exe != "chrome.exe")]);
            Assert.Equal("discord.exe", live.ExpandedApps);
            // It comes back as a new, closed row.
            live.ApplyProcs(Apps());
            Assert.False(live.Procs.Single(p => p.Exe == "chrome.exe").IsExpanded);
        });
    }

    [Fact]
    public void Child_rows_show_their_process()
    {
        var child = new ProcChild(new ProcDetail { Pid = 4242, Label = "GPU process", MemMB = 2048, Cpu = 0.04 });
        Assert.Equal("GPU process", child.Label);
        Assert.Equal("PID 4242", child.PidText);
        Assert.Equal("2.0 GB", child.MemText);
        Assert.Equal("0%", child.CpuText);
    }

    [Fact]
    public void Fake_agent_process_lists_apply_cleanly()
    {
        var live = Greeted();
        Ui.Run(() =>
        {
            live.ApplyProcs(Fixtures.Procs(60));
            Assert.Equal(60, live.Procs.Count);
            Assert.Equal(6, live.TopMemory.Count);
            Assert.Equal("cyberpunk2077.exe", live.TopMemory[0].Exe);
            Assert.Equal(100, live.Procs.Max(p => p.Bar));
            live.ToggleProcessesCommand.Execute(live.Procs.Single(p => p.Exe == "chrome.exe"));
            live.ApplyProcs(Fixtures.Procs(40, "chrome.exe"));
            Assert.Equal(40, live.Procs.Count);
            Assert.Equal(38, live.Procs.Single(p => p.Exe == "chrome.exe").Children.Count);
        });
    }
}
