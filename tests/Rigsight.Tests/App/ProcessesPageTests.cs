using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Processes page: its list (sorting, searching, each app's internet), the four totals, the menus, the questions
/// before a part of Windows is ended, and ending itself. Nothing here ends a real process: what ends them is replaced
/// by a stand-in that only notes what it was asked.
/// </summary>
[Collection("UI")]
public sealed class ProcessesPageTests
{
    private static readonly long T0 = TimeUtil.NowUnixMs();
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static ProcInfo App(string exe, string name, double memMB, int count = 1, bool window = false, double cpu = 0, double disk = 0, string? path = null)
    {
        var p = Kit.Proc(exe, memMB, count, window, cpu, name);
        p.Disk = disk;
        p.Path = path ?? $@"C:\Apps\{exe}";
        return p;
    }

    private static List<ProcInfo> Apps() =>
    [
        App("chrome.exe", "Google Chrome", 4000, count: 30, window: true, cpu: 3.2, disk: 600_000),
        App("game.exe", "Dota 2", 8000, window: true, cpu: 25, disk: 2_700_000),
        App("svchost.exe", "Windows service", 1600, count: 90, cpu: 0.4, path: Path.Combine(Windows, "System32", "svchost.exe")),
        App("Discord.exe", "Discord", 800, count: 4, window: true, cpu: 0.6),
        App("explorer.exe", "Windows Explorer", 240, window: true, cpu: 0.2, path: Path.Combine(Windows, "explorer.exe")),
        App("csrss.exe", "Client Server Runtime Process", 6, count: 2, path: Path.Combine(Windows, "System32", "csrss.exe")),
        App("updater.exe", "Adobe Updater", 180),
    ];

    /// <summary>What stands in for ending processes: it notes what it was asked and answers as the test says.</summary>
    private sealed class Ender
    {
        public Dictionary<string, int[]> Running { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Discord.exe"] = [11, 12, 13, 14], ["game.exe"] = [20], ["updater.exe"] = [30], ["explorer.exe"] = [40], ["csrss.exe"] = [50, 51], ["chrome.exe"] = [60, 61],
        };
        public Dictionary<int, KillResult> Says { get; } = [];
        public Elevation Elevation { get; set; } = Elevation.Done;
        /// <summary>Whether taskkill, run with admin rights, gets the better of what was refused.</summary>
        public bool AdminEnds { get; set; } = true;
        public List<int> Killed { get; } = [];
        public List<int> Elevated { get; } = [];
        public List<string> Started { get; } = [];

        public TaskEnder Build() => new()
        {
            PidsOf = exe => Running.GetValueOrDefault(exe) ?? [],
            Kill = (pid, exe) =>
            {
                if (Running.GetValueOrDefault(exe)?.Contains(pid) != true) return KillResult.Gone;
                var said = Says.GetValueOrDefault(pid, KillResult.Ended);
                if (said == KillResult.Ended)
                {
                    Killed.Add(pid);
                    Running[exe] = [.. Running[exe].Where(p => p != pid)];
                }
                return said;
            },
            Elevate = asked =>
            {
                var pids = asked.Select(a => a.Pid).ToList();
                Elevated.AddRange(pids);
                if (Elevation == Elevation.Done && AdminEnds)
                    foreach (var exe in Running.Keys.ToList()) Running[exe] = [.. Running[exe].Where(p => !pids.Contains(p))];
                return Elevation;
            },
            IsRunning = (pid, exe) => Running.GetValueOrDefault(exe)?.Contains(pid) == true,
            Start = exe =>
            {
                Started.Add(exe);
                Running[exe] = [99];
            },
            RestartPause = TimeSpan.Zero,
        };
    }

    private sealed record PageKit(ProcessesViewModel Vm, LiveData Live, Ender Ender, List<(string Cmd, string? Arg)> Sent, List<string> Copied, List<string> Opened, List<EndQuestion> Asked)
    {
        public EndAnswer Answer { get; set; } = EndAnswer.End;
        public ProcRow Row(string exe) => Ui.Run(() => Live.Procs.Single(p => p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)));
        public List<string> Order => Ui.Run(() => Vm.Apps.Cast<ProcRow>().Select(p => p.Exe).ToList());
    }

    /// <summary>The page on a PC with 32 GB of memory, half of it in use, and the apps above running.</summary>
    private static PageKit Page(bool shown = true)
    {
        var (settings, live) = Kit.Greeted();
        var ender = new Ender();
        var sent = new List<(string, string?)>();
        var kit = Ui.Run(() =>
        {
            var vm = new ProcessesViewModel(new ReportService(settings), live, (cmd, arg) => sent.Add((cmd, arg))) { Ender = ender.Build() };
            var made = new PageKit(vm, live, ender, sent, [], [], []);
            vm.SetClipboard = made.Copied.Add;
            vm.OpenLocation = made.Opened.Add;
            vm.Ask = question =>
            {
                made.Asked.Add(question);
                return Task.FromResult(made.Answer);
            };
            live.ApplyTick(Pc.Tick(T0, 40, ("/ram/data/0", 16), ("/ram/data/1", 16), ("/cpu/load/0", 37.4f)));
            live.ApplyProcs(Apps(), disk: 5_000_000);
            if (shown) vm.SetShown(true);
            return made;
        });
        return kit;
    }

    private static AgentMessage Net(long time, params (string Exe, string Name, double Down, double Up)[] apps) => new()
    {
        T = "tick", Time = time,
        Net = new NetLive
        {
            Time = time / 1000, Down = apps.Sum(a => a.Down), Up = apps.Sum(a => a.Up),
            Apps = [.. apps.Select(a => new NetAppLive { Exe = a.Exe, Name = a.Name, Down = a.Down, Up = a.Up })],
        },
    };

    // ── The list ──

    [Fact]
    public void The_list_starts_by_memory_and_every_heading_sorts_it()
    {
        var page = Page();
        Assert.Equal(["game.exe", "chrome.exe", "svchost.exe", "Discord.exe", "explorer.exe", "updater.exe", "csrss.exe"], page.Order);
        Ui.Run(() =>
        {
            var vm = page.Vm;
            Assert.Equal((ProcessesViewModel.ByMemory, false), (vm.SortKey, vm.SortFlipped));
            Assert.Equal(("↓", true), (vm.MemoryHeading.Arrow, vm.MemoryHeading.IsActive));
            Assert.All(new[] { vm.NameHeading, vm.CpuHeading, vm.InternetHeading, vm.DiskHeading }, h => Assert.Equal(("↕", false), (h.Arrow, h.IsActive)));
            Assert.Equal(["MEMORY"], vm.Tiles.Where(t => t.IsActive).Select(t => t.Label));

            vm.SortByCommand.Execute(ProcessesViewModel.ByCpu);
        });
        Assert.Equal(["game.exe", "chrome.exe", "Discord.exe", "svchost.exe", "explorer.exe", "updater.exe", "csrss.exe"], page.Order);
        Ui.Run(() =>
        {
            Assert.Equal(("↓", "↕"), (page.Vm.CpuHeading.Arrow, page.Vm.MemoryHeading.Arrow));
            Assert.Equal(["CPU"], page.Vm.Tiles.Where(t => t.IsActive).Select(t => t.Label));
            // The heading it is sorted by, pressed again: the other way round. Apps level on it stay by memory.
            page.Vm.SortByCommand.Execute(ProcessesViewModel.ByCpu);
            Assert.True(page.Vm.SortFlipped);
            Assert.Equal("↑", page.Vm.CpuHeading.Arrow);
        });
        Assert.Equal(["updater.exe", "csrss.exe", "explorer.exe", "svchost.exe", "Discord.exe", "chrome.exe", "game.exe"], page.Order);

        Ui.Run(() => page.Vm.SortByCommand.Execute(ProcessesViewModel.ByName));
        Assert.Equal(["updater.exe", "csrss.exe", "Discord.exe", "game.exe", "chrome.exe", "explorer.exe", "svchost.exe"], page.Order); // Adobe … Windows service
        Ui.Run(() => page.Vm.SortByCommand.Execute(ProcessesViewModel.ByName));
        Assert.Equal("svchost.exe", page.Order[0]);

        Ui.Run(() => page.Vm.SortByCommand.Execute(ProcessesViewModel.ByDisk));
        Assert.Equal(["game.exe", "chrome.exe", "svchost.exe"], page.Order.Take(3)); // the two using the disk, then by memory
        Assert.False(Ui.Run(() => page.Vm.SortFlipped)); // another column starts biggest first again
    }

    [Fact]
    public void A_total_sorts_by_its_column_biggest_first()
    {
        var page = Page();
        Ui.Run(() =>
        {
            page.Vm.SortByCommand.Execute(ProcessesViewModel.ByCpu);
            page.Vm.SortByCommand.Execute(ProcessesViewModel.ByCpu);
            Assert.True(page.Vm.SortFlipped);
            // Clicking the total of the column it is already sorted by doesn't turn it round.
            page.Vm.SortByTotalCommand.Execute(ProcessesViewModel.ByCpu);
            Assert.Equal((ProcessesViewModel.ByCpu, false), (page.Vm.SortKey, page.Vm.SortFlipped));
            page.Vm.SortByTotalCommand.Execute(ProcessesViewModel.ByDisk);
            Assert.Equal((ProcessesViewModel.ByDisk, false, "↓"), (page.Vm.SortKey, page.Vm.SortFlipped, page.Vm.DiskHeading.Arrow));
        });
        Assert.Equal("game.exe", page.Order[0]);
    }

    /// <summary>
    /// The agent's list as on a real PC: every figure moves from one list to the next, and since it sends only the
    /// apps holding the most memory (forty of sixty here), some apps leave the list and others come into it each time.
    /// </summary>
    private static List<ProcInfo> Moving(int round)
    {
        var random = new Random(1000 + round);
        return [.. Enumerable.Range(0, 60).Select(i => App($"app{i:00}.exe", $"App {i:00}", Math.Round(100 + random.NextDouble() * 900, 1),
            cpu: Math.Round(random.NextDouble() * 20, 1), disk: Math.Round(random.NextDouble() * 5_000_000))).OrderByDescending(p => p.MemMB).Take(40)];
    }

    private static void AssertInOrder<T>(PageKit page, Func<ProcRow, T> by, bool descending, string when) where T : IComparable<T>
    {
        var keys = Ui.Run(() => page.Vm.Apps.Cast<ProcRow>().Select(by).ToList());
        Assert.Equal(40, keys.Count);
        var sorted = descending ? keys.OrderDescending().ToList() : keys.Order().ToList();
        Assert.True(sorted.SequenceEqual(keys), $"{when}: {string.Join(", ", keys)}");
    }

    [Theory]
    [InlineData(ProcessesViewModel.ByMemory, false)]
    [InlineData(ProcessesViewModel.ByMemory, true)]
    [InlineData(ProcessesViewModel.ByCpu, false)]
    [InlineData(ProcessesViewModel.ByCpu, true)]
    [InlineData(ProcessesViewModel.ByDisk, false)]
    [InlineData(ProcessesViewModel.ByDisk, true)]
    [InlineData(ProcessesViewModel.ByInternet, false)]
    [InlineData(ProcessesViewModel.ByInternet, true)]
    public void The_list_stays_in_order_as_figures_change_and_apps_come_and_go(string column, bool flipped)
    {
        var page = Page(shown: false);
        Func<ProcRow, double> by = column switch
        {
            ProcessesViewModel.ByCpu => p => p.Cpu, ProcessesViewModel.ByDisk => p => p.Disk, ProcessesViewModel.ByInternet => p => p.Net, _ => p => p.MemMB,
        };
        // As the app does it: the list is there before the page is first opened, and the page is left open.
        Ui.Run(() =>
        {
            page.Live.ApplyProcs(Moving(0));
            page.Live.ApplyProcs(Moving(1));
            page.Vm.SetShown(true);
            if (column != ProcessesViewModel.ByMemory) page.Vm.SortByCommand.Execute(column);
            if (flipped) page.Vm.SortByCommand.Execute(column);
        });
        for (int round = 2; round < 8; round++)
        {
            int r = round;
            Ui.Run(() =>
            {
                var random = new Random(r);
                page.Live.ApplyTick(Net(T0 + r * 1000, [.. Moving(r).Select(p => (p.Exe, p.Name, Math.Round(random.NextDouble() * 900_000), 0.0))]));
                page.Live.ApplyProcs(Moving(r));
            });
            Ui.Pump(); // live sorting moves the rows once data binding runs
            AssertInOrder(page, by, descending: !flipped, $"{column}, list {round}");
        }

        // Left and come back to: in order again at once, and kept so from then on.
        Ui.Run(() =>
        {
            page.Vm.SetShown(false);
            page.Live.ApplyProcs(Moving(20));
            page.Vm.SetShown(true);
        });
        AssertInOrder(page, by, descending: !flipped, $"{column}, shown again");
        Ui.Run(() => page.Live.ApplyProcs(Moving(21)));
        Ui.Pump();
        AssertInOrder(page, by, descending: !flipped, $"{column}, after coming back");
    }

    [Fact]
    public void The_list_follows_the_figures_only_while_the_page_is_shown()
    {
        var page = Page();
        Ui.Run(() =>
        {
            var next = Apps();
            next[3].MemMB = 9000; // Discord
            page.Live.ApplyProcs(next);
        });
        Ui.Pump(); // live sorting moves the row once data binding runs
        Assert.Equal("Discord.exe", page.Order[0]);

        Ui.Run(() =>
        {
            page.Vm.SetShown(false);
            page.Live.ApplyProcs(Apps());
        });
        Ui.Pump();
        Assert.Equal("Discord.exe", page.Order[0]); // not kept in order while it isn't on screen
        Ui.Run(() => page.Vm.SetShown(true));
        Assert.Equal(["game.exe", "chrome.exe", "svchost.exe", "Discord.exe"], page.Order.Take(4));
    }

    [Fact]
    public void Search_and_the_windows_switch_are_this_pages_own()
    {
        var page = Page();
        Ui.Run(() =>
        {
            page.Vm.Search = "chrome";
            Assert.False(page.Vm.NoneMatch);
        });
        Assert.Equal(["chrome.exe"], page.Order);
        Ui.Run(() =>
        {
            // The Memory page's list and its search box know nothing of it.
            Assert.Equal("", page.Live.ProcSearch);
            Assert.Equal(7, page.Live.ProcsView.Cast<ProcRow>().Count());
            page.Vm.Search = "windows serv"; // every word, anywhere in the name
        });
        Assert.Equal(["svchost.exe"], page.Order);
        Ui.Run(() => page.Vm.Search = "SVCHOST"); // or its program's name
        Assert.Equal(["svchost.exe"], page.Order);
        Ui.Run(() =>
        {
            page.Vm.Search = "no such app";
            Assert.True(page.Vm.NoneMatch);
            page.Vm.Search = "";
            Assert.False(page.Vm.NoneMatch);
            page.Vm.OnlyWindowed = true;
            Assert.False(page.Live.OnlyWindowedApps);
        });
        Assert.Equal(["game.exe", "chrome.exe", "Discord.exe", "explorer.exe"], page.Order);
        Ui.Run(() => page.Vm.Search = "updater");
        Assert.Empty(page.Order);
        Assert.True(Ui.Run(() => page.Vm.NoneMatch));
    }

    [Fact]
    public void Figures_of_nothing_are_a_dash()
    {
        var page = Page();
        Ui.Run(() =>
        {
            var game = page.Row("game.exe");
            Assert.Equal(("7.8 GB", "25%", "–", "2.6 MB/s"), (game.MemText, game.CpuCell, game.NetCell, game.DiskCell));
            var updater = page.Row("updater.exe");
            Assert.Equal(("180 MB", "–", "–", "–"), (updater.MemText, updater.CpuCell, updater.NetCell, updater.DiskCell));
            Assert.Equal("586 KB/s", page.Row("chrome.exe").DiskCell);
            Assert.Equal("–", ProcRow.RateCell(1023));
            Assert.Equal("1 KB/s", ProcRow.RateCell(1024));
            Assert.Equal(("–", "0.1%"), (ProcRow.CpuCellOf(0.04), ProcRow.CpuCellOf(0.05)));
        });
    }

    [Fact]
    public void Each_apps_internet_is_joined_from_the_internets_own_readings()
    {
        var page = Page();
        Ui.Run(() =>
        {
            // The agent names its apps as Windows spells them; two entries of one app add up.
            page.Live.ApplyTick(Net(T0 + 1000, ("CHROME.EXE", "Google Chrome", 299_488, 20_000), ("discord.exe", "Discord", 9000, 9000), ("discord.exe", "Discord", 1000, 1000),
                ("gone.exe", "Gone", 50_000, 0)));
            Assert.Equal(319_488, page.Row("chrome.exe").Net);
            Assert.Equal("312 KB/s", page.Row("chrome.exe").NetCell);
            Assert.Equal(20_000, page.Row("Discord.exe").Net);
            Assert.Equal("–", page.Row("game.exe").NetCell);
            page.Vm.SortByCommand.Execute(ProcessesViewModel.ByInternet);
        });
        // The two using it, then the rest by memory.
        Assert.Equal(["chrome.exe", "Discord.exe", "game.exe", "svchost.exe"], page.Order.Take(4));

        Ui.Run(() => page.Live.ApplyTick(Net(T0 + 2000, ("discord.exe", "Discord", 900_000, 0))));
        Ui.Pump();
        Assert.Equal(["Discord.exe", "game.exe"], page.Order.Take(2));
        Assert.Equal(0, page.Row("chrome.exe").Net); // no longer among the apps moving anything

        // Not worked out for a page that isn't on screen.
        Ui.Run(() =>
        {
            page.Vm.SetShown(false);
            page.Live.ApplyTick(Net(T0 + 3000, ("chrome.exe", "Google Chrome", 5_000_000, 0)));
            Assert.Equal(0, page.Row("chrome.exe").Net);
            page.Vm.SetShown(true);
            Assert.Equal(5_000_000, page.Row("chrome.exe").Net);
        });
        Assert.Equal("chrome.exe", page.Order[0]);
    }

    // ── The totals ──

    [Fact]
    public void The_totals_say_the_figure_now_and_the_app_using_the_most()
    {
        var page = Page();
        Ui.Run(() =>
        {
            var (memory, cpu, internet, disk) = (page.Vm.Tiles[0], page.Vm.Tiles[1], page.Vm.Tiles[2], page.Vm.Tiles[3]);
            Assert.Equal(["MEMORY", "CPU", "INTERNET", "DISK"], page.Vm.Tiles.Select(t => t.Label));
            Assert.Equal(["PurpleBrush", "CpuBrush", "CoolBrush", "AccentBrush"], page.Vm.Tiles.Select(t => t.Brush));
            Assert.Equal([ProcessesViewModel.ByMemory, ProcessesViewModel.ByCpu, ProcessesViewModel.ByInternet, ProcessesViewModel.ByDisk], page.Vm.Tiles.Select(t => t.Key));

            Assert.Equal(("16.0 GB", "of 32 GB", "Most: Dota 2"), (memory.Value, memory.Note, memory.Most));
            Assert.Same(page.Live.RamUsed!.History, memory.History);
            Assert.Equal(("37%", "Most: Dota 2"), (cpu.Value, cpu.Most));
            Assert.Same(page.Live.CpuLoad!.History, cpu.History);
            // No readings of the internet (the agent has no admin rights): a dash, and nothing named.
            Assert.Equal(("–", "Nothing using it"), (internet.Value, internet.Most));
            Assert.Equal(("4.8 MB/s", "Most: Dota 2"), (disk.Value, disk.Most));
            Assert.Equal(1, disk.History!.Count);

            long version = disk.Version;
            page.Live.ApplyTick(Net(T0 + 1000, ("discord.exe", "Discord", 2_000_000, 100_000), ("chrome.exe", "Google Chrome", 400_000, 0)));
            Assert.Equal(("2.4 MB/s", "Most: Discord"), (internet.Value, internet.Most));
            Assert.Equal(1, internet.History!.Count);
            Assert.True(disk.Version > version); // the lines are drawn again
            page.Live.ApplyTick(Net(T0 + 2000, ("chrome.exe", "Google Chrome", 500, 0)));
            Assert.Equal(("500 B/s", "Nothing using it"), (internet.Value, internet.Most)); // a trickle is nobody using it
            Assert.Equal(2, internet.History.Count);

            // Nothing reading or writing; every app at rest.
            var quiet = Apps();
            foreach (var p in quiet) (p.Disk, p.Cpu) = (0, 0);
            page.Live.ApplyProcs(quiet, disk: 0);
            Assert.Equal(("0 B/s", "Nothing using it"), (disk.Value, disk.Most));
            Assert.Equal("Nothing using it", cpu.Most);
            Assert.Equal(2, disk.History.Count);

            // An agent from before disk use was measured.
            page.Live.ApplyProcs(Apps());
            Assert.Equal("–", disk.Value);
            Assert.Equal(2, disk.History.Count);
        });
    }

    // ── Holding still ──

    [Fact]
    public void The_list_holds_still_under_an_open_menu_and_catches_up_after()
    {
        var page = Page();
        Ui.Run(() =>
        {
            page.Vm.SetMenuOpen(true);
            Assert.True(page.Live.HoldProcs);
            var next = Apps();
            next[3].MemMB = 9000;
            next.RemoveAt(1); // the game closed
            next.Add(App("new.exe", "New", 50));
            page.Live.ApplyProcs(next, disk: 1);
            page.Live.ApplyTick(Net(T0 + 1000, ("updater.exe", "Adobe Updater", 9_000_000, 0)));
        });
        Ui.Pump();
        Assert.Equal(["game.exe", "chrome.exe", "svchost.exe", "Discord.exe", "explorer.exe", "updater.exe", "csrss.exe"], page.Order);
        Ui.Run(() =>
        {
            Assert.Equal(800, page.Row("Discord.exe").MemMB);
            Assert.Equal(0, page.Row("updater.exe").Net);
            // A row can't be opened or closed under the menu either.
            page.Vm.ToggleCommand.Execute(page.Row("Discord.exe"));
            Assert.False(page.Row("Discord.exe").IsExpanded);

            page.Vm.SetMenuOpen(false);
            Assert.False(page.Live.HoldProcs);
            Assert.Equal(9000, page.Row("Discord.exe").MemMB);
            Assert.DoesNotContain(page.Live.Procs, p => p.Exe == "game.exe");
        });
        Ui.Pump();
        Assert.Equal(["Discord.exe", "chrome.exe"], page.Order.Take(2));
        Assert.Contains("new.exe", page.Order);
    }

    // ── Opening an app ──

    [Fact]
    public void An_opened_app_asks_for_its_processes_and_keeps_their_rows()
    {
        var page = Page();
        string? asked = "none";
        Ui.Run(() =>
        {
            page.Live.ProcessDetailChanged += apps => asked = apps;
            var updater = page.Row("updater.exe");
            // An app with a single process opens too: its box of history is under it.
            page.Vm.ToggleCommand.Execute(updater);
            Assert.True(updater.IsExpanded);
            Assert.Equal("updater.exe", asked);
            Assert.NotNull(updater.History);
            page.Vm.ToggleCommand.Execute(updater);
            Assert.False(updater.IsExpanded);
            Assert.Null(asked);

            var discord = page.Row("Discord.exe");
            page.Vm.ToggleCommand.Execute(discord);
            var list = Apps();
            list[3].Processes = [new() { Pid = 11, Label = "Discord", MemMB = 400, Cpu = 0.4, Disk = 2048 }, new() { Pid = 12, Label = "Voice", MemMB = 200 }, new() { Pid = 13, Label = "GPU process", MemMB = 150 }];
            page.Live.ApplyProcs(list);
            var rows = discord.Children.ToList();
            Assert.Equal([11, 12, 13], rows.Select(c => c.Pid));
            Assert.Equal(("Discord", "PID 11", "400 MB", "0.4%", "2 KB/s"), (rows[0].Label, rows[0].PidText, rows[0].MemText, rows[0].CpuCell, rows[0].DiskCell));
            Assert.Equal(("–", "–"), (rows[1].CpuCell, rows[1].DiskCell));

            // The same processes two seconds on: the same rows (and the same list), saying the new figures.
            var same = discord.Children;
            list = Apps();
            list[3].Processes = [new() { Pid = 11, Label = "Discord", MemMB = 450 }, new() { Pid = 12, Label = "Voice · General", MemMB = 200 }, new() { Pid = 13, Label = "GPU process", MemMB = 150 }];
            var changes = Kit.Changes(rows[1], () => page.Live.ApplyProcs(list));
            Assert.Same(same, discord.Children);
            Assert.Equal("450 MB", rows[0].MemText);
            Assert.Equal("Voice · General", rows[1].Label);
            Assert.Equal([nameof(ProcChild.Label)], changes);

            // One gone, one new, another order: the ones still there keep their rows.
            list = Apps();
            list[3].Processes = [new() { Pid = 13, Label = "GPU process", MemMB = 500 }, new() { Pid = 11, Label = "Discord", MemMB = 450 }, new() { Pid = 15, Label = "Crash reporter", MemMB = 20 }];
            page.Live.ApplyProcs(list);
            Assert.NotSame(same, discord.Children);
            Assert.Same(rows[2], discord.Children[0]);
            Assert.Same(rows[0], discord.Children[1]);
            Assert.Equal(15, discord.Children[2].Pid);
        });
    }

    [Fact]
    public void The_box_under_an_app_says_what_is_on_record_and_leaves_out_what_is_not()
    {
        var now = new DateTime(2026, 6, 15, 15, 20, 0);
        var row = new ProcRow("Discord.exe");
        var info = App("Discord.exe", "Discord", 1100, count: 4);
        info.Started = TimeUtil.ToUnix(now.Date.AddHours(9).AddMinutes(10));
        row.Update(info);

        // Before anything is read: what the running app tells by itself, and a loader for the rest.
        var history = new ProcHistory();
        history.Fill(row, now);
        Assert.True(history.IsLoading);
        Assert.Equal([new ProcFact("Running since", "9:10 AM")], history.Glance);
        Assert.False(history.HasChart);
        Assert.Equal("History", history.ToggleText);

        var past = new AppPast
        {
            Hours = [.. Enumerable.Range(0, 16).Select(h => h < 9 ? (double?)null : 500 + (h - 9) * 80)],
            UsualMB = 520, FrontSec = 22 * 60, BackSec = 5 * 3600 + 40 * 60, NetBytes = 96 << 20, Crashes = 0, FirstSeen = new DateTime(2026, 3, 14),
        };
        history.Fill(row, past, now);
        Assert.False(history.IsLoading);
        Assert.Equal(["Running since 9:10 AM", "Usual memory 520 MB", "In the background today 5 h 40 min"], history.Glance.Select(f => $"{f.Label} {f.Value}"));
        Assert.Equal(
            ["Running since 9:10 AM", "Usual memory 520 MB", "In front today 22 min", "In the background today 5 h 40 min", "Internet today 96 MB", "Crashes (30 days) None",
             "First seen 14 Mar 2026"], history.Facts.Select(f => $"{f.Label} {f.Value}"));
        Assert.DoesNotContain(history.Facts, f => f.Label.Contains("Disk")); // nothing records it
        Assert.True(history.HasChart);
        Assert.Equal(520, history.UsualMB);
        Assert.Equal(16, history.Hours.Count);
        Assert.Equal(1100, history.Hours[^1]); // the hour now is what it holds at this moment
        Assert.Equal(580, history.Hours[10]);
        history.IsOpen = true;
        Assert.Equal("Hide", history.ToggleText);

        // Read again and nothing changed: the page keeps the lists it has built its rows from.
        var (facts, glance) = (history.Facts, history.Glance);
        history.Fill(row, past, now);
        Assert.Same(facts, history.Facts);
        Assert.Same(glance, history.Glance);

        // A small app Windows started yesterday, with nothing kept about its memory: only what there is.
        var small = new ProcRow("helper.exe");
        var helper = App("helper.exe", "Helper", 12);
        helper.Started = TimeUtil.ToUnix(now.Date.AddDays(-1).AddHours(19).AddMinutes(40));
        small.Update(helper);
        var bare = new ProcHistory();
        bare.Fill(small, new AppPast { Crashes = 2 }, now);
        Assert.Equal(["Running since Yesterday, 7:40 PM", "Crashes (30 days) 2"], bare.Facts.Select(f => $"{f.Label} {f.Value}"));
        Assert.Equal(["Running since"], bare.Glance.Select(f => f.Label));
        Assert.False(bare.HasChart);
        Assert.Null(bare.UsualMB);

        // Nothing could be read at all, and no start time from an older agent: an empty box, not dashes.
        var none = new ProcHistory();
        none.Fill(new ProcRow("x.exe"), null, now);
        Assert.Empty(none.Facts);
        Assert.False(none.IsLoading);

        Assert.Equal("12 Jun, 8:05 AM", ProcHistory.Since(new DateTime(2026, 6, 12, 8, 5, 0), now));
        Assert.Equal(("None", "Under a minute", "59 min", "1 h 0 min"), (ProcHistory.Time(0), ProcHistory.Time(40), ProcHistory.Time(3599), ProcHistory.Time(3600)));
    }

    [Fact]
    public void The_box_is_read_when_an_app_is_first_opened()
    {
        SharedData.EnsureSeeded();
        var page = Page();
        var chrome = page.Row("chrome.exe");
        Kit.Wait(() =>
        {
            page.Vm.ToggleCommand.Execute(chrome);
            Assert.True(chrome.History!.IsLoading);
            return page.Vm.LoadHistoryAsync(chrome);
        });
        Assert.True(Ui.WaitFor(() => !chrome.History!.IsLoading));
        Ui.Run(() =>
        {
            // The seeded history knows Chrome: its crashes at the least, and when it was first seen.
            Assert.Contains(chrome.History!.Facts, f => f.Label == "Crashes (30 days)");
            Assert.Contains(chrome.History.Facts, f => f.Label == "First seen");
            Assert.False(chrome.History.IsOpen);
            page.Vm.ToggleHistoryCommand.Execute(chrome);
            Assert.True(chrome.History.IsOpen);
            page.Vm.ToggleHistoryCommand.Execute(chrome);
            Assert.False(chrome.History.IsOpen);
        });
    }

    // ── The menus ──

    private static string Line(ProcMenuEntry e) => e.Kind switch
    {
        ProcMenuKind.Separator => "---",
        ProcMenuKind.Header => $"# {e.Text}",
        ProcMenuKind.Note => $"({e.Text})",
        _ => $"{e.Text}{(e.Hint is null ? "" : $" [{e.Hint}]")}{(e.Enabled ? "" : " (off)")}{(e.Danger && e.Enabled ? " !" : "")}",
    };

    private static List<string> Menu(PageKit page, params string[] exes) => Ui.Run(() => page.Vm.MenuFor([.. exes.Select(page.Row)]).Select(Line).ToList());

    [Fact]
    public void An_apps_menu_opens_its_place_copies_its_path_and_ends_it()
    {
        var page = Page();
        Assert.Equal(["Open file location", "Copy path", "---", "End task [4 processes] !"], Menu(page, "Discord.exe"));
        Assert.Equal(["Open file location", "Copy path", "---", "End task !"], Menu(page, "game.exe")); // one process: nothing to count
        Assert.Empty(Ui.Run(() => page.Vm.MenuFor([])));

        Ui.Run(() =>
        {
            var menu = page.Vm.MenuFor([page.Row("Discord.exe")]);
            Assert.All(menu.Where(e => e.Kind == ProcMenuKind.Item), e => Assert.NotEqual("", e.Glyph));
            menu[0].Run!();
            Assert.Equal([@"C:\Apps\Discord.exe"], page.Opened);
            Assert.Null(page.Vm.Message);
            menu[1].Run!();
            Assert.Equal([@"C:\Apps\Discord.exe"], page.Copied);
            Assert.Equal(("Path copied", false), (page.Vm.Message, page.Vm.MessageWarns));
            page.Vm.ClearMessage();
            Assert.Null(page.Vm.Message);

            // The clipboard held by another program for a moment: said, not thrown.
            page.Vm.SetClipboard = _ => throw new System.Runtime.InteropServices.COMException("busy");
            menu[1].Run!();
            Assert.Equal(("Couldn't copy. Try again.", true), (page.Vm.Message, page.Vm.MessageWarns));
        });
    }

    [Fact]
    public void An_app_without_a_known_path_cant_be_opened_or_copied()
    {
        var page = Page();
        Ui.Run(() =>
        {
            var list = Apps();
            list[6].Path = null;
            page.Live.ApplyProcs(list);
        });
        Assert.Equal(["Open file location (off)", "Copy path (off)", "---", "End task !"], Menu(page, "updater.exe"));
    }

    [Fact]
    public void A_part_of_Windows_says_a_question_follows_and_Explorer_can_be_restarted()
    {
        var page = Page();
        Assert.Equal(["Open file location", "Copy path", "---", "Restart", "End task… !"], Menu(page, "explorer.exe"));
        Assert.Equal(["Open file location", "Copy path", "---", "End task… [2 processes] !"], Menu(page, "csrss.exe"));
    }

    [Fact]
    public void The_service_hosts_row_cant_be_ended_as_one()
    {
        var page = Page();
        Assert.Equal(["Open file location", "Copy path", "---", "End task [90 processes] (off)", "(Open it and end one service at a time.)"], Menu(page, "svchost.exe"));
        // Among several picked: nothing is ended, and the menu says which one is in the way.
        Assert.Equal(["# 3 apps picked", "Copy names", "---", "End 3 apps [5.6 GB] (off)", "(Windows service can't be ended as one. Open it and end one service at a time.)"],
            Menu(page, "chrome.exe", "svchost.exe", "updater.exe"));

        // One service of it, once the row is open: ended after the general question.
        Ui.Run(() =>
        {
            var row = page.Row("svchost.exe");
            var audio = new ProcChild(new ProcDetail { Pid = 1234, Label = "Windows Audio", MemMB = 38 });
            Assert.Equal(["Open file location", "Copy path", "---", "End this process… [PID 1234] !"], page.Vm.MenuFor(row, audio).Select(Line));
            var question = ProcessesViewModel.QuestionFor([new EndTarget(row, audio)])!;
            Assert.Equal(("End Windows Audio?", "This is part of Windows. Ending it may make Windows or other apps stop working."), (question.Title, question.Body));
            Assert.False(question.NeedsTick);
            Assert.False(question.CanRestart);
        });

        // Even asked directly, the whole row is never ended.
        Kit.Wait(() => page.Vm.EndAsync([new EndTarget(page.Row("svchost.exe"))]));
        Kit.Wait(() => page.Vm.EndAsync([new EndTarget(page.Row("Discord.exe")), new EndTarget(page.Row("svchost.exe"))]));
        Assert.Empty(page.Ender.Killed);
        Assert.Empty(page.Asked);
        Assert.Empty(page.Sent);
    }

    [Fact]
    public void A_processs_menu_ends_only_that_process()
    {
        var page = Page();
        Ui.Run(() =>
        {
            var row = page.Row("chrome.exe");
            var tab = new ProcChild(new ProcDetail { Pid = 61, Label = "YouTube", MemMB = 912 });
            Assert.Equal(["Open file location", "Copy path", "---", "End this process [PID 61] !"], page.Vm.MenuFor(row, tab).Select(Line));
        });
    }

    [Fact]
    public void Several_picked_are_named_together()
    {
        var page = Page();
        Assert.Equal(["# 3 apps picked", "Copy names", "---", "End 3 apps [11.9 GB] !"], Menu(page, "chrome.exe", "game.exe", "updater.exe"));
        Assert.Equal(["# 2 apps picked", "Copy names", "---", "End 2 apps… [4.1 GB] !"], Menu(page, "chrome.exe", "explorer.exe")); // one of them is asked about
        Ui.Run(() =>
        {
            page.Vm.MenuFor([page.Row("chrome.exe"), page.Row("game.exe")])[1].Run!();
            Assert.Equal([$"Google Chrome{Environment.NewLine}Dota 2"], page.Copied);
            Assert.Equal("Names copied", page.Vm.Message);
        });
    }

    // ── The questions ──

    private static EndQuestion Question(PageKit page, params string[] exes) => Ui.Run(() => ProcessesViewModel.QuestionFor([.. exes.Select(e => new EndTarget(page.Row(e)))])!);

    [Fact]
    public void Ordinary_apps_are_not_asked_about()
    {
        var page = Page();
        Assert.Null(Ui.Run(() => ProcessesViewModel.QuestionFor([new EndTarget(page.Row("Discord.exe")), new EndTarget(page.Row("game.exe"))])));
    }

    [Fact]
    public void Each_part_of_Windows_has_its_own_question()
    {
        var page = Page();
        var explorer = Question(page, "explorer.exe");
        Assert.Equal(("End Windows Explorer?", "The taskbar, Start menu and desktop icons disappear until it starts again."), (explorer.Title, explorer.Body));
        Assert.True(explorer.CanRestart);
        Assert.False(explorer.CancelIsDefault); // "Restart it" is the button in front
        Assert.False(explorer.NeedsTick);
        Assert.True(explorer.CanEnd);

        var csrss = Question(page, "csrss.exe");
        Assert.Equal("End Client Server Runtime Process?", csrss.Title);
        Assert.Equal("Windows depends on this process. Ending it can make Windows stop or shut down, and anything unsaved in any app would be lost.", csrss.Body);
        Assert.True(csrss.NeedsTick);
        Assert.True(csrss.CancelIsDefault);
        Assert.False(csrss.CanRestart);
        // The red button stays off until the box is ticked.
        Assert.False(csrss.CanEnd);
        Assert.Contains(nameof(EndQuestion.CanEnd), Kit.Changes(csrss, () => csrss.Ticked = true));
        Assert.True(csrss.CanEnd);

        // Several, some of them parts of Windows: each of those with its sentence; the ordinary ones aren't listed.
        var several = Question(page, "chrome.exe", "Discord.exe", "explorer.exe");
        Assert.Equal(("End 3 apps?", "One of them is part of Windows:"), (several.Title, several.Intro));
        Assert.Null(several.Body);
        Assert.Equal([new EndQuestionLine("Windows Explorer", "The taskbar, Start menu and desktop icons disappear until it starts again.")], several.Lines);
        Assert.False(several.CanRestart);
        Assert.False(several.NeedsTick);
        var grave = Question(page, "chrome.exe", "explorer.exe", "csrss.exe");
        Assert.Equal("Some of them are part of Windows:", grave.Intro);
        Assert.Equal(["Windows Explorer", "Client Server Runtime Process"], grave.Lines.Select(l => l.Name));
        Assert.True(grave.NeedsTick);
    }

    [Fact]
    public void Rigsights_own_window_and_agent_say_what_stops()
    {
        var page = Page();
        Ui.Run(() =>
        {
            var list = Apps();
            list.Add(App("Rigsight.exe", "Rigsight", 120, path: @"C:\Program Files\Rigsight\Rigsight.exe"));
            list.Add(App("Rigsight.Agent.exe", "Rigsight Agent", 22, path: @"C:\Program Files\Rigsight\Rigsight.Agent.exe"));
            list.Add(App("dwm.exe", "Desktop Window Manager", 260, path: Path.Combine(Windows, "System32", "dwm.exe")));
            page.Live.ApplyProcs(list);
        });
        Assert.Equal("This window closes.", Question(page, "Rigsight.exe").Body);
        Assert.Equal("Nothing is recorded until it starts again.", Question(page, "Rigsight.Agent.exe").Body);
        Assert.Equal("The screen goes black for a moment, then Windows starts it again.", Question(page, "dwm.exe").Body);
        Assert.Equal("End task… !", Menu(page, "Rigsight.exe")[^1]);
    }

    [Fact]
    public void The_question_over_the_window_gives_its_answer_back_once()
    {
        var box = Ui.Run(() => new EndQuestionViewModel());
        Ui.Run(() =>
        {
            var asked = box.AskAsync(new EndQuestion { Title = "End csrss?", NeedsTick = true });
            Assert.True(box.IsOpen);
            // Not ticked: "End anyway" does nothing.
            box.AnswerCommand.Execute("End");
            Assert.True(box.IsOpen);
            Assert.False(asked.IsCompleted);
            box.Question!.Ticked = true;
            box.AnswerCommand.Execute("End");
            Assert.False(box.IsOpen);
            Assert.Null(box.Question);
            Assert.Equal(EndAnswer.End, asked.Result);

            var second = box.AskAsync(new EndQuestion { Title = "End Windows Explorer?", CanRestart = true });
            box.AnswerCommand.Execute("Restart");
            Assert.Equal(EndAnswer.Restart, second.Result);

            // Esc, or a click beside the box.
            var third = box.AskAsync(new EndQuestion { Title = "End dwm?" });
            box.Cancel();
            Assert.Equal(EndAnswer.Cancel, third.Result);
            Assert.False(box.IsOpen);
            box.Cancel(); // nothing open: nothing happens

            // Asked again while one is up: the one before counts as cancelled.
            var fourth = box.AskAsync(new EndQuestion { Title = "One" });
            var fifth = box.AskAsync(new EndQuestion { Title = "Two" });
            Assert.Equal(EndAnswer.Cancel, fourth.Result);
            Assert.Equal("Two", box.Question!.Title);
            box.AnswerCommand.Execute("End");
            Assert.Equal(EndAnswer.End, fifth.Result);
        });
    }

    // ── Ending ──

    private static void End(PageKit page, params string[] exes)
    {
        var targets = Ui.Run(() => exes.Select(e => new EndTarget(page.Row(e))).ToList());
        Kit.Wait(() => page.Vm.EndAsync(targets));
    }

    [Fact]
    public void An_ordinary_app_ends_at_once_and_the_Timeline_is_told()
    {
        var page = Page();
        End(page, "Discord.exe");
        Assert.Empty(page.Asked);
        Assert.Equal([11, 12, 13, 14], page.Ender.Killed); // every process of it
        Assert.Empty(page.Ender.Elevated);
        Ui.Run(() =>
        {
            Assert.Equal(("Ended Discord · 800 MB freed", false), (page.Vm.Message, page.Vm.MessageWarns));
            Assert.Equal([(TaskEnded.Command, (string?)"Discord.exe|800|Discord")], page.Sent);
            Assert.Equal("Discord ended from Processes", TaskEnded.Parse(page.Sent[0].Arg, DateTime.Now)!.Title);
            Assert.False(page.Live.HoldProcs);

            // Its row stays dimmed until the agent's list no longer has it.
            var discord = page.Row("Discord.exe");
            Assert.True(discord.IsEnding);
            var list = Apps();
            list.RemoveAt(3);
            page.Live.ApplyProcs(list);
            Assert.DoesNotContain(page.Live.Procs, p => p.Exe == "Discord.exe");
        });
    }

    [Fact]
    public void The_agent_is_asked_for_a_fresh_list_once_something_was_ended()
    {
        var page = Page();
        int asked = 0;
        Ui.Run(() => page.Live.ProcessDetailChanged += _ => asked++);
        End(page, "updater.exe");
        Assert.Equal(1, asked);
    }

    [Fact]
    public void An_app_that_had_closed_by_itself_is_said_so()
    {
        var page = Page();
        page.Ender.Running.Remove("updater.exe");
        End(page, "updater.exe");
        Ui.Run(() =>
        {
            Assert.Equal(("Adobe Updater had already closed.", false), (page.Vm.Message, page.Vm.MessageWarns));
            Assert.Empty(page.Sent); // nothing was ended: nothing for the Timeline
        });

        // Its processes went between being listed and being ended.
        page.Ender.Says[20] = KillResult.Gone;
        End(page, "game.exe");
        Assert.Equal("Dota 2 had already closed.", Ui.Run(() => page.Vm.Message));
    }

    [Fact]
    public void What_Windows_refuses_is_tried_with_admin_rights()
    {
        var page = Page();
        page.Ender.Says[12] = page.Ender.Says[13] = KillResult.Denied;
        End(page, "Discord.exe");
        Assert.Equal([11, 14], page.Ender.Killed);
        Assert.Equal([12, 13], page.Ender.Elevated); // one prompt for both
        Ui.Run(() =>
        {
            Assert.Equal("Ended Discord · 800 MB freed", page.Vm.Message);
            Assert.Single(page.Sent);
        });
    }

    [Fact]
    public void A_permission_prompt_turned_down_is_said_and_the_row_comes_back()
    {
        var page = Page();
        page.Ender.Says[20] = KillResult.Denied;
        page.Ender.Elevation = Elevation.Cancelled;
        End(page, "game.exe");
        Ui.Run(() =>
        {
            Assert.Equal(("Couldn't end Dota 2. Permission wasn't given.", true), (page.Vm.Message, page.Vm.MessageWarns));
            Assert.Empty(page.Sent);
            Assert.False(page.Row("game.exe").IsEnding);
            Assert.False(page.Live.HoldProcs);
        });
    }

    [Fact]
    public void What_Windows_refuses_with_admin_rights_too_is_said()
    {
        var page = Page();
        page.Ender.Says[30] = KillResult.Denied;
        page.Ender.AdminEnds = false; // a protected process
        End(page, "updater.exe");
        Assert.Equal([30], page.Ender.Elevated);
        Ui.Run(() =>
        {
            Assert.Equal(("Couldn't end Adobe Updater. Windows refused.", true), (page.Vm.Message, page.Vm.MessageWarns));
            Assert.False(page.Row("updater.exe").IsEnding);
        });

        // The same when admin rights couldn't be asked for at all, or ending fails some other way.
        page.Ender.Elevation = Elevation.Failed;
        End(page, "updater.exe");
        Assert.Equal("Couldn't end Adobe Updater. Windows refused.", Ui.Run(() => page.Vm.Message));
        page.Ender.Says[20] = KillResult.Failed;
        End(page, "game.exe");
        Assert.Equal("Couldn't end Dota 2. Windows refused.", Ui.Run(() => page.Vm.Message));
        Assert.Empty(page.Sent);
    }

    [Fact]
    public void One_process_is_ended_by_its_ID_and_named_with_its_app()
    {
        var page = Page();
        ProcChild tab = null!;
        Ui.Run(() =>
        {
            var chrome = page.Row("chrome.exe");
            page.Vm.ToggleCommand.Execute(chrome);
            var list = Apps();
            list[0].Processes = [new() { Pid = 61, Label = "YouTube", MemMB = 912 }, new() { Pid = 60, Label = "Google Chrome", MemMB = 400 }];
            page.Live.ApplyProcs(list);
            tab = chrome.Children[0];
        });
        Kit.Wait(() => page.Vm.EndAsync([new EndTarget(page.Row("chrome.exe"), tab)]));
        Assert.Equal([61], page.Ender.Killed); // not the rest of Chrome
        Ui.Run(() =>
        {
            Assert.Equal("Ended YouTube · 912 MB freed", page.Vm.Message);
            Assert.Equal("chrome.exe|912|Google Chrome · YouTube", page.Sent.Single().Arg);
            Assert.True(tab.IsEnding);
            Assert.False(page.Row("chrome.exe").IsEnding);
            // Two lists on and still there: it wasn't ended after all, and its row comes back.
            var list = Apps();
            list[0].Processes = [new() { Pid = 61, Label = "YouTube", MemMB = 912 }, new() { Pid = 60, Label = "Google Chrome", MemMB = 400 }];
            page.Live.ApplyProcs(list);
            Assert.True(tab.IsEnding);
            page.Live.ApplyProcs(list);
            Assert.False(tab.IsEnding);
        });

        // A process named as its app is (its main one) is just the app in words.
        var main = Ui.Run(() => new EndTarget(page.Row("chrome.exe"), new ProcChild(new ProcDetail { Pid = 60, Label = "Google Chrome", MemMB = 400 })));
        Assert.Equal(("Google Chrome", "Google Chrome", 400.0), (main.Name, main.LongName, main.MemMB));
    }

    [Fact]
    public void Several_are_ended_together_and_what_went_wrong_is_named()
    {
        var page = Page();
        End(page, "Discord.exe", "game.exe", "updater.exe");
        Assert.Equal([11, 12, 13, 14, 20, 30], page.Ender.Killed);
        Ui.Run(() =>
        {
            Assert.Equal("Ended 3 apps · 8.8 GB freed", page.Vm.Message);
            Assert.Equal(3, page.Sent.Count);
        });

        // One refused, the others ended: both said.
        var again = Page();
        again.Ender.Says[20] = KillResult.Denied;
        again.Ender.AdminEnds = false;
        End(again, "Discord.exe", "game.exe", "updater.exe");
        Ui.Run(() =>
        {
            Assert.Equal(("Ended 2 of 3 apps. Couldn't end Dota 2. Windows refused.", true), (again.Vm.Message, again.Vm.MessageWarns));
            Assert.Equal(["Discord.exe|800|Discord", "updater.exe|180|Adobe Updater"], again.Sent.Select(s => s.Arg));
            Assert.False(again.Row("game.exe").IsEnding);
            Assert.True(again.Row("Discord.exe").IsEnding);
        });

        // All of them already closed.
        var gone = Page();
        gone.Ender.Running.Clear();
        End(gone, "Discord.exe", "game.exe");
        Assert.Equal("They had already closed.", Ui.Run(() => gone.Vm.Message));
    }

    [Fact]
    public void A_part_of_Windows_is_ended_only_after_the_question()
    {
        var page = Page();
        page.Answer = EndAnswer.Cancel;
        End(page, "explorer.exe");
        Assert.Equal("End Windows Explorer?", page.Asked.Single().Title);
        Assert.Empty(page.Ender.Killed);
        Ui.Run(() =>
        {
            Assert.Null(page.Vm.Message);
            Assert.False(page.Live.HoldProcs);
            Assert.False(page.Row("explorer.exe").IsEnding);
        });

        page.Answer = EndAnswer.End;
        End(page, "explorer.exe");
        Assert.Equal([40], page.Ender.Killed);
        Assert.Empty(page.Ender.Started); // ended, as asked: not started again
        Assert.Equal("Ended Windows Explorer · 240 MB freed", Ui.Run(() => page.Vm.Message));

        // Several with one of them a part of Windows: one question for all, then all of them.
        page.Asked.Clear();
        End(page, "Discord.exe", "csrss.exe");
        Assert.Equal("End 2 apps?", page.Asked.Single().Title);
        Assert.Equal([40, 11, 12, 13, 14, 50, 51], page.Ender.Killed);
    }

    [Fact]
    public void The_list_holds_still_while_the_question_is_up()
    {
        var page = Page();
        var answer = new TaskCompletionSource<EndAnswer>();
        Task ending = null!;
        Ui.Run(() =>
        {
            page.Vm.Ask = _ => answer.Task;
            ending = page.Vm.EndAsync([new EndTarget(page.Row("explorer.exe"))]);
            Assert.True(page.Live.HoldProcs);
            var next = Apps();
            next[3].MemMB = 9000;
            page.Live.ApplyProcs(next);
            Assert.Equal(800, page.Row("Discord.exe").MemMB);
            answer.SetResult(EndAnswer.Cancel);
        });
        Kit.Wait(() => ending);
        Ui.Run(() =>
        {
            Assert.False(page.Live.HoldProcs);
            Assert.Equal(9000, page.Row("Discord.exe").MemMB);
        });
    }

    [Fact]
    public void Windows_Explorer_is_restarted_from_its_menu_or_from_the_question()
    {
        var page = Page();
        Kit.Wait(() => page.Vm.MenuFor([page.Row("explorer.exe")]).Single(e => e.Text == "Restart").Run!());
        Assert.Empty(page.Asked); // "Restart" says what it does: nothing to ask
        Assert.Equal([40], page.Ender.Killed);
        Assert.Equal(["explorer.exe"], page.Ender.Started);
        Ui.Run(() =>
        {
            Assert.Equal(("Restarted Windows Explorer", false), (page.Vm.Message, page.Vm.MessageWarns));
            Assert.False(page.Row("explorer.exe").IsEnding); // it is back at once
            Assert.Empty(page.Sent);
        });

        // "Restart it", the button in front of the question after "End task…".
        page.Answer = EndAnswer.Restart;
        End(page, "explorer.exe");
        Assert.Equal([40, 99], page.Ender.Killed);
        Assert.Equal(["explorer.exe", "explorer.exe"], page.Ender.Started);

        // Refused: said, and nothing started.
        page.Ender.Says[99] = KillResult.Denied;
        page.Ender.AdminEnds = false;
        Kit.Wait(() => page.Vm.RestartAsync(new EndTarget(page.Row("explorer.exe"))));
        Assert.Equal(2, page.Ender.Started.Count);
        Assert.Equal(("Couldn't restart Windows Explorer. Windows refused.", true), Ui.Run(() => (page.Vm.Message, page.Vm.MessageWarns)));
    }

    // ── What does the ending, on its own ──

    [Fact]
    public void Windows_starting_Explorer_again_by_itself_is_left_at_that()
    {
        var ender = new Ender();
        var tasks = ender.Build();
        // Ended, and already back by the time it is looked for.
        tasks.Kill = (pid, _) =>
        {
            ender.Killed.Add(pid);
            ender.Running["explorer.exe"] = [77];
            return KillResult.Ended;
        };
        Assert.Equal(EndOutcome.Ended, tasks.Restart("explorer.exe"));
        Assert.Equal([40], ender.Killed);
        Assert.Empty(ender.Started);

        // Not running at all: started.
        var none = new Ender();
        none.Running.Remove("explorer.exe");
        Assert.Equal(EndOutcome.Ended, none.Build().Restart("explorer.exe"));
        Assert.Equal(["explorer.exe"], none.Started);
    }

    [Fact]
    public void Each_target_gets_its_own_outcome_from_one_prompt()
    {
        var ender = new Ender { AdminEnds = true };
        ender.Says[12] = KillResult.Denied; // one of Discord's four
        ender.Says[20] = KillResult.Denied; // the game
        ender.Says[30] = KillResult.Gone;   // the updater went by itself
        ender.Says[60] = KillResult.Failed; // one of Chrome's two
        var outcomes = ender.Build().End([("Discord.exe", null), ("game.exe", null), ("updater.exe", null), ("chrome.exe", null), ("chrome.exe", 61), ("nothing.exe", null)]);
        // Chrome as a whole: one of its two wouldn't end. Its other one, asked for by itself afterwards, has gone by then.
        Assert.Equal([EndOutcome.Ended, EndOutcome.Ended, EndOutcome.Gone, EndOutcome.Refused, EndOutcome.Gone, EndOutcome.Gone], outcomes);
        Assert.Equal([12, 20], ender.Elevated);
        Assert.Equal([11, 13, 14, 61], ender.Killed);

        // The prompt turned down: what needed it wasn't ended, what didn't was.
        var cancelled = new Ender { Elevation = Elevation.Cancelled };
        cancelled.Says[20] = KillResult.Denied;
        Assert.Equal([EndOutcome.Ended, EndOutcome.Cancelled], cancelled.Build().End([("updater.exe", null), ("game.exe", null)]));

        // Nothing refused: nothing asked of Windows.
        var plain = new Ender();
        Assert.Equal([EndOutcome.Ended], plain.Build().End([("game.exe", null)]));
        Assert.Empty(plain.Elevated);
    }

    [Fact]
    public void This_windows_own_process_is_ended_last()
    {
        int self = Environment.ProcessId;
        var order = new List<int>();
        var tasks = new Ender().Build();
        tasks.PidsOf = _ => [self, 5, 6];
        tasks.Kill = (pid, _) =>
        {
            order.Add(pid);
            return KillResult.Ended;
        };
        tasks.End([("Rigsight.exe", null)]);
        Assert.Equal([5, 6, self], order);
    }

    [Fact]
    public void This_windows_own_process_is_ended_after_every_other_app_picked_with_it()
    {
        int self = Environment.ProcessId;
        var order = new List<int>();
        var tasks = new Ender().Build();
        tasks.PidsOf = exe => exe == "Rigsight.exe" ? [self] : [5, 6];
        tasks.Kill = (pid, _) =>
        {
            order.Add(pid);
            return KillResult.Ended;
        };
        var outcomes = tasks.End([("Rigsight.exe", null), ("chrome.exe", null)]);
        Assert.Equal([5, 6, self], order);
        Assert.Equal([EndOutcome.Ended, EndOutcome.Ended], outcomes);
    }

    [Fact]
    public void With_admin_rights_a_process_is_only_ended_if_it_is_still_the_exe_it_was_listed_as()
    {
        string system = Environment.SystemDirectory;
        // One exe: Windows' own taskkill, told the name the IDs must still carry (the prompt can stay open for long,
        // and an ID is given to another program once its process has gone).
        var one = TaskEnder.ElevatedCommand([(10, "steamservice.exe"), (11, "steamservice.exe")])!.Value;
        Assert.Equal((Path.Combine(system, "taskkill.exe"), "/F /PID 10 /PID 11 /FI \"IMAGENAME eq steamservice.exe\""), one);

        // Several: one command line, so one prompt, each exe with its own name to match.
        var two = TaskEnder.ElevatedCommand([(10, "a b (x86).exe"), (20, "B.exe"), (11, "A B (x86).exe")])!.Value;
        Assert.Equal(Path.Combine(system, "cmd.exe"), two.File);
        string taskkill = $"\"{Path.Combine(system, "taskkill.exe")}\"";
        Assert.Equal($"/c \"{taskkill} /F /PID 10 /PID 11 /FI \"IMAGENAME eq a b (x86).exe\" & {taskkill} /F /PID 20 /FI \"IMAGENAME eq B.exe\"\"", two.Arguments);

        // A name with anything in it a command line could read as its own is never put on one.
        foreach (string odd in new[] { "a&b.exe", "a\"b.exe", "100%.exe", "a^b.exe", "a|b.exe", "a>b.exe", "", "a\tb.exe" })
        {
            Assert.False(TaskEnder.PlainName(odd), odd);
            Assert.Null(TaskEnder.ElevatedCommand([(10, odd)]));
        }
        Assert.Equal("/F /PID 20 /FI \"IMAGENAME eq ok.exe\"", TaskEnder.ElevatedCommand([(10, "a&b.exe"), (20, "ok.exe")])!.Value.Arguments);
    }

    [Fact]
    public void A_process_that_is_not_there_is_gone_and_nothing_is_touched()
    {
        // The real thing, asked only about what doesn't exist: no process on this PC is ended by a test.
        var real = new TaskEnder();
        Assert.Empty(real.PidsOf("rigsight-no-such-program.exe"));
        Assert.False(real.IsRunning(0x7FFFFFF0, "rigsight-no-such-program.exe"));
        Assert.Equal(KillResult.Gone, real.Kill(0x7FFFFFF0, "rigsight-no-such-program.exe"));
        // An ID in use by another program than the one listed (this test's own process, under another name): not ended.
        Assert.Equal(KillResult.Gone, real.Kill(Environment.ProcessId, "rigsight-no-such-program.exe"));
        Assert.False(real.IsRunning(Environment.ProcessId, "rigsight-no-such-program.exe"));
        Assert.Equal([EndOutcome.Gone], real.End([("rigsight-no-such-program.exe", null)]));

        string me = Path.GetFileName(Environment.ProcessPath)!;
        Assert.Contains(Environment.ProcessId, real.PidsOf(me));
        Assert.True(real.IsRunning(Environment.ProcessId, me));
        Assert.Equal("chrome", TaskEnder.ProcessName("chrome.exe"));
        Assert.Equal("Rigsight.Agent", TaskEnder.ProcessName("Rigsight.Agent.EXE"));
        Assert.Equal("System", TaskEnder.ProcessName("System"));
    }

    // ── The Timeline ──

    [Fact]
    public void What_was_ended_is_a_line_on_the_Timeline_with_a_kind_of_its_own()
    {
        var at = DateTime.Today.AddHours(15).AddMinutes(12);
        var ended = TaskEnded.Parse(TaskEnded.Format("Discord", "Discord.exe", 1126), at)! with { Id = 1 };
        var driver = new SystemChange(DateTime.Today.AddHours(9), ChangeKind.Driver, "NVIDIA graphics driver 616.92") { Id = 2, Subject = "nvidia" };
        var timeline = Ui.Run(() => new TimelineViewModel(new ReportService(Kit.OfflineSettings()), _ => { }));
        Ui.Run(() =>
        {
            timeline.Apply(new TimelineData([driver, ended], [], []));
            var entry = timeline.Days[0].Entries[0]; // newest first
            Assert.Equal(("Discord ended from Processes", "It held 1.1 GB.", "3:12 PM"), (entry.Title, entry.Detail, entry.TimeText));
            Assert.False(entry.IsKey);
            Assert.NotEqual("", entry.Icon);
            Assert.NotEqual(timeline.Days[0].Entries[1].Icon, entry.Icon);
            Assert.Contains(timeline.Filters, f => f is { Key: "ended", Name: "Ended from Processes", Count: 1 });
        });
    }

    // ── The page itself ──

    [Fact]
    public void The_page_shows_the_totals_the_list_and_an_opened_app()
    {
        SharedData.EnsureSeeded();
        var page = Page(shown: false);
        Ui.TakeProblems();
        var window = Ui.Run(() =>
        {
            var view = new Views.ProcessesView { DataContext = page.Vm };
            var w = new System.Windows.Window
            {
                Content = view, Left = -32000, Top = -32000, Width = 1300, Height = 860,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            };
            w.Show();
            return w;
        });
        try
        {
            var chrome = page.Row("chrome.exe");
            Ui.Run(() =>
            {
                page.Live.ApplyTick(Net(T0 + 1000, ("chrome.exe", "Google Chrome", 299_488, 20_000)));
                page.Vm.ToggleCommand.Execute(chrome);
            });
            // What the seeded history says about it is read first; the test then puts figures of its own in the box.
            Assert.True(Ui.WaitFor(() => chrome.History is { IsLoading: false }));
            Ui.Run(() =>
            {
                var list = Apps();
                list[0].Processes = [new() { Pid = 61, Label = "YouTube", MemMB = 912 }, new() { Pid = 60, Label = "Google Chrome", MemMB = 400 }];
                page.Live.ApplyProcs(list, disk: 5_000_000);
                chrome.History!.Fill(chrome, new AppPast
                {
                    Hours = [.. Enumerable.Range(0, 12).Select(h => (double?)(3000 + h * 90))], UsualMB = 2700, FrontSec = 3 * 3600, BackSec = 2 * 3600, Crashes = 1,
                }, DateTime.Now);
                chrome.History.IsOpen = true;
            });
            Ui.Pump(400);
            Ui.Run(() =>
            {
                var view = (Views.ProcessesView)window.Content;
                var texts = Visuals.Descendants<System.Windows.Controls.TextBlock>(view).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                foreach (var expected in new[] { "Processes", "MEMORY", "CPU", "INTERNET", "DISK", "Most: Dota 2", "Running now", "APP", "Google Chrome", "30 processes", "MEMORY TODAY", "312 KB/s" })
                    Assert.Contains(expected, texts);
                // The app is open: its two processes are under it, and its box is opened into the chart and the facts.
                // Built for the open app only: the other rows stay as light as a row of the Memory page.
                Assert.Single(Visuals.Descendants<Controls.HourLine>(view));
                Assert.Equal("Now · 3.9 GB", Visuals.Descendants<Controls.HourLine>(view).Single(c => c.IsVisible).HoverText(11));
                Assert.Equal("3 AM · 3.2 GB", Visuals.Descendants<Controls.HourLine>(view).Single(c => c.IsVisible).HoverText(3));
                Assert.Equal(4, Visuals.Descendants<Controls.Sparkline>(view).Count(s => s.IsVisible));
                // The column headings end where the rows' figures end.
                var list = Visuals.Descendants<System.Windows.Controls.ListBox>(view).Single();
                var head = (System.Windows.FrameworkElement)view.FindName("LiveHead");
                var figure = Visuals.Descendants<System.Windows.Controls.TextBlock>(list).First(t => t.IsVisible && t.Text == "7.8 GB");
                var heading = Visuals.Descendants<System.Windows.Controls.Button>(head).Single(b => b.DataContext == page.Vm.MemoryHeading);
                double figureRight = figure.TranslatePoint(new System.Windows.Point(figure.ActualWidth, 0), view).X;
                double headingRight = heading.TranslatePoint(new System.Windows.Point(heading.ActualWidth, 0), view).X;
                Assert.Equal(figureRight, headingRight, 1.0);
            });
            Ui.AssertNoProblems("the Processes page");
            Assert.True(Ui.Run(() => page.Live.Procs.Count > 0));
        }
        finally
        {
            Ui.Run(() =>
            {
                window.Close();
                page.Vm.SetShown(false);
            });
        }
    }
}
