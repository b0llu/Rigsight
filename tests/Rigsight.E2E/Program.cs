using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;

namespace Rigsight.E2E;

/// <summary>
/// End-to-end check of a built Rigsight (normally the Release publish that goes into the installer). The agent and the
/// app run as a test copy: RIGSIGHT_DATA_DIR points them at a folder of generated history, which also gives them their
/// own pipe and names, so the real Rigsight on this PC is never touched (see RigsightPaths.IsTestInstance). The agent
/// runs without admin rights (no UAC prompt), so it reads fewer sensors than an installed one.
///
/// Steps: start the agent → measure it with no window open → open the app and time it → visit every page (CPU per
/// page, screenshots) → idle on Home → cycle through all pages many times watching memory and handles for leaks →
/// switch between every pair of pages quickly, timing how long the window takes to answer (stutters) →
/// close the app and check the agent gives its memory back → ask the agent to quit and check it saved cleanly →
/// read the log for errors. Every measure is checked against a budget, and against the baseline of the last accepted
/// run (tests/Rigsight.E2E/baseline.json) so a slow creep shows up too.
///
///   Rigsight.E2E --bin &lt;folder with Rigsight.exe and Rigsight.Agent.exe&gt; [--out &lt;folder&gt;] [--quick] [--update-baseline]
///                [--installed]   (also measure the installed agent already running, without touching it)
/// Exit code 0 = everything within budget, 1 = a check failed, 2 = couldn't run.
/// </summary>
internal static class Program
{
    private static readonly List<Metric> Metrics = [];
    private static readonly List<string> Failures = [];
    private static readonly List<string> Notes = [];

    [STAThread]
    private static int Main(string[] args)
    {
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        bool quick = args.Contains("--quick");
        bool admin = args.Contains("--admin");
        string bin = Path.GetFullPath(Arg("--bin") ?? Path.Combine(RepoRoot(), "bin", "Release"));
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string data = Path.Combine(Path.GetTempPath(), "rigsight-e2e", stamp);
        string output = Path.GetFullPath(Arg("--out") ?? Path.Combine(RepoRoot(), "TestResults", "e2e", stamp));
        // With admin rights the agent reads every sensor (like an installed one), so it's measured against its own baseline.
        // On a copy of this PC's own history (--real-data) the pages hold what they really hold: measured against a
        // baseline of its own. (A year of made-up history passed with page switches of 15 ms while the real pages froze
        // the window for a fifth of a second.) The made-up year stays for what a long history costs.
        string realDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rigsight", "rigsight.db");
        bool real = args.Contains("--real-data");
        if (real && !File.Exists(realDb))
        {
            Console.WriteLine("  --real-data: no history of this PC's own was found; using the made-up year.");
            real = false;
        }
        string baselineFile = Path.Combine(RepoRoot(), "tests", "Rigsight.E2E", $"baseline{(real ? "-real" : "")}{(admin ? "-admin" : "")}.json");

        // Before anything reads RigsightPaths: this process shares the test copy's names (to signal its agent to quit).
        Environment.SetEnvironmentVariable("RIGSIGHT_DATA_DIR", data);
        Directory.CreateDirectory(output);

        var agentExe = Path.Combine(bin, RigsightPaths.AgentExe);
        var appExe = Path.Combine(bin, RigsightPaths.AppExe);
        if (!File.Exists(agentExe) || !File.Exists(appExe))
        {
            Console.Error.WriteLine($"Rigsight.exe and Rigsight.Agent.exe not found in {bin}");
            return 2;
        }
        // Builds before 0.5.13 don't keep a test copy apart: pointed at test data, they'd still talk to the real agent.
        var version = new Version(FileVersionInfo.GetVersionInfo(appExe).ProductVersion!.Split('+')[0]);
        if (bin.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase) && version < new Version(0, 5, 13))
        {
            Console.Error.WriteLine($"The installed build (v{version}) can't run as a separate test copy (0.5.13 or later can); use --installed to measure it as it runs.");
            return 2;
        }

        Console.WriteLine($"Rigsight end-to-end check{(quick ? " (quick)" : "")}{(admin ? ", agent with admin rights" : "")}");
        Console.WriteLine($"  build:  {bin} (v{FileVersionInfo.GetVersionInfo(appExe).ProductVersion})");
        Console.WriteLine($"  data:   {data}");
        Console.WriteLine($"  report: {output}");
        Console.WriteLine($"  PC:     {Environment.ProcessorCount} logical processors");

        Process? agent = null, app = null;
        try
        {
            Step(real ? "Copying this PC's own history (the copy is deleted after the run)" : "Generating a year of history");
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(data);
            if (real)
            {
                // A whole copy as of now, taken while the installed agent goes on writing to the real one.
                using var conn = new SqliteConnection($"Data Source={realDb};Mode=ReadOnly;Pooling=False");
                conn.Open();
                using var copy = conn.CreateCommand();
                copy.CommandText = "VACUUM INTO $to";
                copy.Parameters.AddWithValue("$to", RigsightPaths.Database);
                copy.ExecuteNonQuery();
            }
            else SeedData.Generate(RigsightPaths.Database, SeedProfile.Typical);
            var settings = SeedData.QuietSettings();
            settings.CustomPages.Add(Dashboard());
            SettingsStore.Save(settings);
            long seededMinutes = Count(RigsightPaths.Database, "system_minute");
            long seedEnd = Scalar(RigsightPaths.Database, "SELECT max(ts) FROM system_minute");
            Console.WriteLine($"  {seededMinutes:N0} minutes of history, {new FileInfo(RigsightPaths.Database).Length / 1048576.0:0} MB, in {sw.Elapsed.TotalSeconds:0.0} s");

            // ── Agent ──
            Step(admin ? "Starting the agent (test copy, with admin rights: accept the Windows prompt)" : "Starting the agent (test copy, no admin)");
            sw.Restart();
            agent = admin ? StartElevated(agentExe, data, bin)
                : Process.Start(new ProcessStartInfo(agentExe, ["--no-elevate", "--data-dir", data]) { UseShellExecute = false, WorkingDirectory = bin })!;
            if (!WaitFor(() => LogHas("Sensors ready"), 60_000)) throw new Exception("The agent didn't finish finding sensors within a minute");
            // The agent starts itself again at once (with the small garbage-collector setting the app has too): the one
            // measured from here on is the process that says it's starting, not the one launched.
            if (LogLine("[agent] Starting") is { } started && System.Text.RegularExpressions.Regex.Match(started, @"process (\d+)") is { Success: true } pid
                && int.Parse(pid.Groups[1].Value) != agent.Id)
            {
                agent.Dispose();
                agent = Process.GetProcessById(int.Parse(pid.Groups[1].Value));
            }
            Record("agent.startup_ms", sw.ElapsedMilliseconds, "ms", "Agent start until its sensors are ready", budget: 15_000);
            Console.WriteLine($"  {LogLine("Sensors ready")}");

            // The agent hands back discovery's memory right after; give it a moment to settle.
            Thread.Sleep(5000);
            Step($"Agent alone, no window open ({(quick ? 20 : 30)} s)");
            var alone = Sample(quick ? 20 : 30, agent);
            Record("agent.idle.cpu_pct_core", alone[agent].CpuPctCore, "% of one core", "Agent CPU with the app closed", budget: 1.0);
            Record("agent.idle.private_mb", alone[agent].PrivateEndMb, "MB", "Agent memory (private) with the app closed", budget: 80);
            Record("agent.idle.working_set_mb", alone[agent].WorkingSetEndMb, "MB", "Agent working set with the app closed", budget: 150);
            Record("agent.idle.handles", alone[agent].HandlesEnd, "", "Agent handles", budget: 1500);
            Record("agent.idle.gdi", alone[agent].GdiEnd, "", "Agent GDI objects", budget: 150);
            Record("agent.idle.threads", alone[agent].ThreadsEnd, "", "Agent threads", budget: 40);
            double agentAloneMb = alone[agent].PrivateEndMb;

            // ── App ──
            Step("Opening the app, as from the tray");
            var opened = OpenToHome(appExe, bin);
            app = opened.App;
            var ui = opened.Ui;
            Record("app.startup_ms", opened.ShownMs, "ms", "App start until Home is on screen", budget: 6000);
            Record("app.home.filled_ms", opened.FilledMs, "ms", "App start until Home has its history", budget: 6000);
            Console.WriteLine($"  Home on screen after {opened.ShownMs} ms, with its history after {opened.FilledMs} ms");
            foreach (var line in opened.FalseEmpties) Fail($"Home said \"{line}\" while it was still loading (a loader belongs there)");
            if (!WaitFor(() => ui.HasLiveReadings(), 20_000)) Fail("Home never showed a live reading (no data from the agent?)");

            Step("Visiting every page");
            string[] pages = ["Home", "Reports", "Apps", "Processes", "Crashes", "Everything", "Temperatures", "Fans", "Memory", "Storage", "Network", "All sensors", "Widgets", "Overlay", "Taskbar", "Settings"];
            var pageCpu = new Dictionary<string, double>();
            foreach (var page in pages)
            {
                // Settings is in the menu its row at the foot of the sidebar opens.
                if (page == "Settings" && ui.Has("Settings and more"))
                {
                    ui.Press("Settings and more");
                    Thread.Sleep(600);
                }
                if (!ui.Has(page)) { Notes.Add($"No \"{page}\" in the sidebar (skipped)"); continue; }
                double before = CpuMs(app!);
                ui.Press(page);
                Thread.Sleep(2500);
                ui.ScrollToEnd();
                Thread.Sleep(500);
                double ms = CpuMs(app!) - before;
                pageCpu[page] = ms;
                ui.Screenshot(Path.Combine(output, $"page-{page.Replace(' ', '-').ToLowerInvariant()}.png"));
                Console.WriteLine($"  {page,-14} {ms,6:0} ms CPU");
            }
            foreach (var (page, ms) in pageCpu)
                Record($"app.page.{Key(page)}.cpu_ms", ms, "ms CPU", $"Opening {page} (first visit, 3 s)", budget: page is "Reports" or "Apps" or "Crashes" or "Everything" ? 3000 : 2000);

            ui.Press("Home");
            Thread.Sleep(3000);
            Step($"App open on Home ({(quick ? 20 : 30)} s)");
            var open = Sample(quick ? 20 : 30, app!, agent);
            Record("app.home.cpu_pct_core", open[app!].CpuPctCore, "% of one core", "App CPU while open on Home", budget: 3.0);
            Record("app.home.private_mb", open[app!].PrivateEndMb, "MB", "App memory (private) on Home", budget: 250);
            Record("app.home.working_set_mb", open[app!].WorkingSetEndMb, "MB", "App working set on Home", budget: 300);
            Record("agent.live.cpu_pct_core", open[agent].CpuPctCore, "% of one core", "Agent CPU while the app is open (every sensor each second)", budget: 4.0);
            Record("agent.live.private_mb", open[agent].PrivateEndMb, "MB", "Agent memory while the app is open", budget: 150);

            ui.Press("Temperatures");
            Thread.Sleep(3000);
            Step($"App open on Temperatures ({(quick ? 15 : 20)} s)");
            var temps = Sample(quick ? 15 : 20, app!);
            Record("app.temperatures.cpu_pct_core", temps[app!].CpuPctCore, "% of one core", "App CPU on Temperatures (live chart)", budget: 5.0);

            ui.Press("Memory");
            Thread.Sleep(3000);
            Step($"App open on Memory ({(quick ? 15 : 20)} s)");
            var memory = Sample(quick ? 15 : 20, app!, agent);
            Record("app.memory.cpu_pct_core", memory[app!].CpuPctCore, "% of one core", "App CPU on Memory (process list every 2 s)", budget: 5.0);
            Record("agent.memory_page.cpu_pct_core", memory[agent].CpuPctCore, "% of one core", "Agent CPU while Memory is open", budget: 6.0);

            // ── Leaks ──
            int cycles = quick ? 6 : 8;
            Step($"Cycling through every page {cycles} times (leaks)");
            var points = new List<ProcessSnapshot>();
            var heaps = new List<double>();
            for (int c = 0; c < cycles; c++)
            {
                foreach (var page in pageCpu.Keys)
                {
                    ui.Press(page);
                    Thread.Sleep(350);
                }
                ui.Press("Home");
                Thread.Sleep(1500);
                var s = ProcessSnapshot.Take(app!);
                points.Add(s);
                double heap = HeapMb();
                heaps.Add(heap);
                Console.WriteLine($"  cycle {c + 1,2}: {heap,6:0.0} MB in use after a full collection, {s.PrivateMb,6:0.0} MB private, {s.Handles} handles, {s.Gdi} GDI, {s.User} USER");
            }
            // The first cycles warm caches (icons, fonts, page views): judge the growth over the second half.
            var half = points.Skip(points.Count / 2).ToList();
            // What the app still holds after a full collection (the true measure of a leak), then the process's memory
            // as Windows sees it, which rises and falls with the collector's timing, so only judged overall.
            Record("app.leak.heap_mb_per_cycle", Slope(heaps.Skip(heaps.Count / 2)), "MB/cycle", "App memory kept per full page cycle, after collecting (after warm-up)", budget: 0.5);
            Record("app.leak.private_mb_overall", points[^1].PrivateMb - points.Take(3).Min(p => p.PrivateMb), "MB", "App memory (private) at the end, more than the early rounds", budget: 25);
            Record("app.leak.handles_per_cycle", Slope(half.Select(p => (double)p.Handles)), "handles/cycle", "App handle growth per cycle", budget: 5);
            Record("app.leak.gdi_per_cycle", Slope(half.Select(p => (double)p.Gdi)), "GDI/cycle", "App GDI growth per cycle", budget: 2);
            Record("app.leak.user_per_cycle", Slope(half.Select(p => (double)p.User)), "USER/cycle", "App USER object growth per cycle", budget: 2);
            Record("app.after_cycles.private_mb", points[^1].PrivateMb, "MB", "App memory after all the cycles", budget: 350);

            // ── Stutters ──
            // Every page to every other page, quickly, while another thread asks the window for an answer every 20 ms: a
            // page that freezes the window, when opened or a moment later as it finishes loading, shows as a slow answer.
            // (Crashes once rebuilt its whole list on each visit: a quarter-second freeze just after opening it.)
            int rounds = quick ? 1 : 2;
            Step($"Switching between every pair of pages, {rounds}× (stutters)");
            var switches = new List<(string From, string To, double Ms)>();
            // The sidebar's items, found once: finding one by name searches the whole window, which would be timed too.
            // Settings is not among them: it is a line of the menu at the sidebar's foot, there only while the menu is open
            // (opening it is timed with the first visits above, and it is in every round of the cycling).
            var nav = pageCpu.Keys.Where(p => p != "Settings").ToDictionary(p => p, ui.NavItem);
            using (var watch = new WindowWatch(app!.MainWindowHandle))
            {
                for (int r = 0; r < rounds; r++)
                    foreach (var from in nav.Keys)
                        foreach (var to in nav.Keys)
                        {
                            if (from == to) continue;
                            nav[from].Select();
                            Thread.Sleep(120);
                            var t = Stopwatch.GetTimestamp();
                            nav[to].Select();
                            WindowWatch.Answer(app!.MainWindowHandle);
                            switches.Add((from, to, Stopwatch.GetElapsedTime(t).TotalMilliseconds));
                        }
                ui.Press("Home");
                Thread.Sleep(1000);
                var answers = watch.Stop();
                var ordered = switches.OrderByDescending(x => x.Ms).ToList();
                Console.WriteLine($"  {switches.Count} switches: median {ordered[ordered.Count / 2].Ms:0} ms, slowest {ordered[0].Ms:0} ms ({ordered[0].From} → {ordered[0].To})");
                foreach (var g in switches.Where(x => x.Ms > 100).GroupBy(x => (x.From, x.To)).Take(10))
                    Console.WriteLine($"    {g.Key.From} → {g.Key.To}: {string.Join(", ", g.Select(x => $"{x.Ms:0} ms"))}");
                Console.WriteLine($"  window answered {answers.Count} times: longest wait {answers.Max():0} ms, over 100 ms {answers.Count(a => a > 100)} times");
                Record("app.switch.median_ms", ordered[ordered.Count / 2].Ms, "ms", "Switching pages, the usual time until the window answers", budget: 80);
                Record("app.switch.slow_count", switches.Count(x => x.Ms > 100), "switches", "Page switches that took over 100 ms", budget: 3);
                Record("app.switch.freeze_ms", answers.Max(), "ms", "Longest the window froze while switching pages", budget: 200);
            }

            // ── Close the app ──
            Step("Closing the app");
            sw.Restart();
            ui.Close();
            if (!app!.WaitForExit(10_000)) Fail("The app didn't exit within 10 s of closing its window");
            Record("app.close_ms", sw.ElapsedMilliseconds, "ms", "Closing the window until the app has exited", budget: 3000);
            // The agent hands back the live view's memory 15 s after the last window closes.
            Thread.Sleep(25_000);
            var after = ProcessSnapshot.Take(agent);
            Record("agent.after_app.private_mb", after.PrivateMb, "MB", "Agent memory 25 s after the app closed", budget: agentAloneMb + 25);
            Record("agent.after_app.growth_mb", after.PrivateMb - agentAloneMb, "MB", "…more than before the app was opened", budget: 20);

            // ── Open it again: what opening from the tray is on every day but the first ──
            Step("Opening the app again");
            var kept = KeptIcons();
            if (kept.Count == 0) Fail("No app icons were kept from the first time the app was open (they'd be read from every program again on each start)");
            var again = OpenToHome(appExe, bin);
            app = again.App;
            Record("app.reopen.filled_ms", again.FilledMs, "ms", "Opening the app again until Home has its history", budget: 5000);
            Console.WriteLine($"  Home on screen after {again.ShownMs} ms, with its history after {again.FilledMs} ms");
            foreach (var line in again.FalseEmpties) Fail($"Opened again, Home said \"{line}\" while it was still loading");
            Thread.Sleep(1500);
            // An icon read from its program is written to its kept file: one written again was read again.
            int reread = KeptIcons().Count(now => kept.TryGetValue(now.Key, out var before) && before != now.Value);
            if (reread > 0) Fail($"{reread} app icon(s) were read from their programs again on the second open, not from the kept pictures");
            again.Ui.Close();
            if (!app.WaitForExit(10_000)) Fail("The app didn't exit within 10 s of closing its window (second time)");

            // ── And once with the history held up: on a quick PC Home has its history before anything can look at it
            // (both opens above found it filled at the first look), so what it shows while it waits is seen here. ──
            Step("Opening the app with the history held up (what Home shows while it waits)");
            var held = OpenToHome(appExe, bin, slowMs: 1500);
            app = held.App;
            Console.WriteLine($"  Home looked at {held.LooksWhileLoading} time(s) while loading; with its history after {held.FilledMs} ms");
            foreach (var line in held.FalseEmpties) Fail($"Home said \"{line}\" while it was still loading (a loader belongs there)");
            if (held.LooksWhileLoading == 0)
            {
                // A build from before the hold-up existed opens as quickly as ever: nothing to look at, and no failure of its own.
                if (version >= new Version(0, 21, 1)) Fail("Home was never seen while loading, so what it shows then wasn't checked");
                else Notes.Add($"v{version} can't hold its history up: what Home shows while loading wasn't checked");
            }
            held.Ui.Close();
            if (!app.WaitForExit(10_000)) Fail("The app didn't exit within 10 s of closing its window (third time)");

            // ── Quit the agent ──
            Step("Asking the agent to quit");
            sw.Restart();
            // As the installer does (the quit signal); an elevated agent's signal is only open to administrators, so
            // that one is asked over the pipe, as the app's "Quit" does.
            if (admin) SendToAgent("quit");
            else using (var quit = EventWaitHandle.OpenExisting(RigsightPaths.AgentQuitEvent)) quit.Set();
            if (!agent.WaitForExit(15_000)) Fail("The agent didn't exit within 15 s of being asked to quit");
            Record("agent.quit_ms", sw.ElapsedMilliseconds, "ms", "Quit request until the agent has exited", budget: 8000);

            // ── What was written ──
            Step("Checking the database and the log");
            var integrity = ScalarText(RigsightPaths.Database, "PRAGMA integrity_check");
            if (integrity != "ok") Fail($"Database integrity check: {integrity}");
            long written = Scalar(RigsightPaths.Database, $"SELECT count(*) FROM system_minute WHERE ts > {seedEnd}");
            if (written == 0) Fail("The agent recorded no new minutes of history during the run");
            else Console.WriteLine($"  agent recorded {written} new minute(s); database integrity ok");
            if (Count(RigsightPaths.Database, "system_minute") < seededMinutes) Fail("History was lost during the run");
            CheckLog();

            if (args.Contains("--installed")) MeasureInstalled(quick ? 15 : 30);
        }
        catch (Exception ex)
        {
            Fail("The run stopped: " + ex.Message);
            Console.Error.WriteLine(ex);
        }
        finally
        {
            try { if (app is { HasExited: false }) app.Kill(); } catch { }
            try { if (agent is { HasExited: false }) agent.Kill(); } catch { }
            try { if (agent is { HasExited: false }) { SendToAgent("quit"); agent.WaitForExit(10_000); } } catch { } // elevated: can't be killed from here
            if (agent is { HasExited: false }) Console.WriteLine($"  ! The test agent (process {agent.Id}) is still running: end it in Task Manager.");
            try { File.Copy(RigsightPaths.LogFile, Path.Combine(output, "rigsight.log"), overwrite: true); } catch { }
        }

        CompareWithBaseline(baselineFile);
        WriteReport(output, bin, quick, admin);
        if (args.Contains("--update-baseline"))
        {
            if (Failures.Count > 0) Console.WriteLine("\nBaseline NOT updated: the run has failures.");
            else
            {
                SaveBaseline(baselineFile, bin);
                Console.WriteLine($"\nBaseline updated: {baselineFile}");
            }
        }
        try { Directory.Delete(data, recursive: true); } catch { }

        Console.WriteLine(Failures.Count == 0 ? "\nPASSED" : $"\nFAILED ({Failures.Count}):\n  " + string.Join("\n  ", Failures));
        return Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>A dashboard ("Everything") with one tile of each kind.</summary>
    private static CustomPageConfig Dashboard()
    {
        var page = new CustomPageConfig { Name = "Everything", Grid = CustomPageConfig.CurrentGrid };
        string[] kinds = ["cpu-gauge", "gpu-gauge", "temp-chart", "fans", "drives", "top-memory", "sensor", "today", "most-used", "insights", "peaks", "yesterday", "crashes"];
        for (int i = 0; i < kinds.Length; i++)
            page.Tiles.Add(new TileConfig { Kind = kinds[i], Sensor = kinds[i] == "sensor" ? "key:cpuLoad" : null, X = i % 2 * 6, Y = i / 2 * 4, W = 6, H = 4 });
        return page;
    }

    /// <summary>Signals the test app to collect all garbage, and reads the memory it still holds from its log (MB).</summary>
    private static double HeapMb()
    {
        using var signal = EventWaitHandle.OpenExisting(@"Local\Rigsight.App.Heap" + RigsightPaths.InstanceSuffix);
        int before = LogLines("[heap]").Count;
        signal.Set();
        List<string> lines = [];
        if (!WaitFor(() => (lines = LogLines("[heap]")).Count > before, 10_000)) throw new Exception("The app didn't report its memory");
        var text = lines[^1];
        return long.Parse(text[(text.IndexOf("[heap] ") + 7)..].Split(' ')[0], CultureInfo.InvariantCulture) / 1048576.0;
    }

    // ── Measuring ─────────────────────────────────────────────────────────

    private sealed record Stats(double CpuPctCore, double CpuPctPc, double PrivateEndMb, double PrivatePeakMb, double WorkingSetEndMb,
        int HandlesEnd, int GdiEnd, int UserEnd, int ThreadsEnd);

    /// <summary>Samples the processes once a second for <paramref name="seconds"/>.</summary>
    private static Dictionary<Process, Stats> Sample(int seconds, params Process[] processes)
    {
        var start = processes.ToDictionary(p => p, p => (Cpu: CpuMs(p), At: Stopwatch.GetTimestamp()));
        var peak = processes.ToDictionary(p => p, _ => 0.0);
        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            foreach (var p in processes)
            {
                p.Refresh();
                peak[p] = Math.Max(peak[p], p.PrivateMemorySize64 / 1048576.0);
            }
        }
        var result = new Dictionary<Process, Stats>();
        foreach (var p in processes)
        {
            double elapsedMs = Stopwatch.GetElapsedTime(start[p].At).TotalMilliseconds;
            double cpu = (CpuMs(p) - start[p].Cpu) / elapsedMs * 100;
            var s = ProcessSnapshot.Take(p);
            result[p] = new Stats(cpu, cpu / Environment.ProcessorCount, s.PrivateMb, peak[p], s.WorkingSetMb, s.Handles, s.Gdi, s.User, s.Threads);
            Console.WriteLine($"  {p.ProcessName,-15} CPU {cpu,6:0.000}% of one core ({cpu / Environment.ProcessorCount:0.0000}% of the PC)   " +
                              $"{s.PrivateMb,6:0.0} MB private (peak {peak[p]:0.0})   {s.WorkingSetMb,6:0.0} MB working set   {s.Handles} handles   {s.Gdi} GDI   {s.Threads} threads");
        }
        return result;
    }

    /// <summary>CPU time used so far, read with the least access Windows grants (works for elevated processes too).</summary>
    private static double CpuMs(Process p)
    {
        using var h = Query(p.Id);
        return GetProcessTimes(h, out _, out _, out long kernel, out long user) ? (kernel + user) / 10_000.0 : double.NaN;
    }

    private static Microsoft.Win32.SafeHandles.SafeProcessHandle Query(int pid) =>
        new(OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid), ownsHandle: true);

    /// <summary>The program a process runs (null if it's gone).</summary>
    private static string? ExePath(Process p)
    {
        using var h = Query(p.Id);
        var text = new StringBuilder(1024);
        int size = text.Capacity;
        return !h.IsInvalid && QueryFullProcessImageName(h, 0, text, ref size) ? text.ToString() : null;
    }

    /// <summary>
    /// Starts the agent elevated, telling it its data folder (UAC doesn't pass the environment on): through the admin
    /// helper when tools\test.ps1 -Admin has one running (no prompt), else with the Windows prompt.
    /// </summary>
    /// <summary>
    /// Opens the app the way the tray does (its program started, nothing else) and waits until Home has its history:
    /// the recap card showing its figures, or saying there are none. Gives how long until Home was on screen, how
    /// long until it had its history, and every "nothing here" line it showed on the way that wasn't so once it had.
    /// </summary>
    /// <param name="slowMs">Each read of the history is held up this long (see ReportService.SlowBy), so the wait can be looked at.</param>
    private static (Process App, AppUi Ui, long ShownMs, long FilledMs, List<string> FalseEmpties, int LooksWhileLoading) OpenToHome(string appExe, string bin, int slowMs = 0)
    {
        var sw = Stopwatch.StartNew();
        var start = new ProcessStartInfo(appExe) { UseShellExecute = false, WorkingDirectory = bin };
        if (slowMs > 0) start.Environment["RIGSIGHT_SLOW_HISTORY_MS"] = slowMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Process.Start(start)!.Dispose();
        // Rigsight.exe starts itself again with its GC setting (see Program.cs in the app): find that copy.
        Process? app = null;
        if (!WaitFor(() => (app = FindApp(appExe)) is not null, 20_000)) throw new Exception("The app's window didn't appear");
        var ui = new AppUi(app!);
        ui.MoveOffScreen();
        long shown = -1;
        int looks = 0;
        var empties = new HashSet<string>(StringComparer.Ordinal);
        List<string> texts;
        while (true)
        {
            texts = ui.Texts();
            if (shown < 0 && texts.Contains("Today so far")) shown = sw.ElapsedMilliseconds;
            if (AppUi.HomeFilled(texts)) break;
            if (shown >= 0) looks++;
            empties.UnionWith(texts.Where(AppUi.SaysNothingHere));
            if (sw.ElapsedMilliseconds > 30_000) throw new Exception("Home didn't show its history within 30 s");
            Thread.Sleep(25);
        }
        long filled = sw.ElapsedMilliseconds;
        // Still said now that the history is in: true, not a page caught half loaded.
        empties.ExceptWith(texts);
        return (app!, ui, shown < 0 ? filled : shown, filled, [.. empties], looks);
    }

    /// <summary>The app icons kept in the data folder, each with when it was written.</summary>
    private static Dictionary<string, DateTime> KeptIcons() =>
        Directory.Exists(RigsightPaths.IconCache)
            ? Directory.GetFiles(RigsightPaths.IconCache, "*.png").ToDictionary(f => Path.GetFileName(f), File.GetLastWriteTimeUtc)
            : [];

    private static Process StartElevated(string agentExe, string data, string bin)
    {
        if (StartThroughHelper(agentExe, data) is { } helped) return helped;
        try
        {
            var start = new ProcessStartInfo(agentExe, $"--data-dir \"{data}\"") { UseShellExecute = true, Verb = "runas", WorkingDirectory = bin };
            using var launched = Process.Start(start) ?? throw new Exception("The agent didn't start");
            return Process.GetProcessById(launched.Id);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new Exception("The Windows admin prompt was declined");
        }
    }

    /// <summary>
    /// The agent started by tools\admin-helper.ps1 (already an administrator, so no prompt), when it's running: a
    /// request file, answered with the new process's ID. Null when there's no helper (or it refused).
    /// </summary>
    private static Process? StartThroughHelper(string agentExe, string data)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RigsightTestAdmin");
        var alive = new FileInfo(Path.Combine(dir, "alive"));
        if (!alive.Exists || DateTime.Now - alive.LastWriteTime > TimeSpan.FromSeconds(10)) return null;

        string id = Guid.NewGuid().ToString("N");
        string request = Path.Combine(dir, $"request-{id}.json"), response = Path.Combine(dir, $"response-{id}.txt");
        string partial = Path.Combine(dir, $"writing-{id}");
        File.WriteAllText(partial, System.Text.Json.JsonSerializer.Serialize(new { Exe = agentExe, Data = data }));
        File.Move(partial, request); // whole, never half-written
        var wait = Stopwatch.StartNew();
        while (!File.Exists(response) && wait.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(100);
        if (!File.Exists(response))
        {
            File.Delete(request); // not answered: nothing may start it later, beside the one the prompt starts
            return null;
        }
        Thread.Sleep(100);
        string answer = File.ReadAllText(response).Trim();
        File.Delete(response);
        if (!int.TryParse(answer, out int pid)) throw new Exception($"The admin helper refused: {answer}");
        Console.WriteLine("  (started through the admin helper: no Windows prompt)");
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException)
        {
            // Already gone: the agent starts itself again at once (see the gen0 restart), and the helper's answer can come
            // after that. The one running says so in the log.
            Process? started = null;
            WaitFor(() => LogLine("[agent] Starting") is { } line
                && System.Text.RegularExpressions.Regex.Match(line, @"process (\d+)") is { Success: true } m
                && TryProcess(int.Parse(m.Groups[1].Value), out started), 15_000);
            return started ?? throw new Exception($"The agent the admin helper started ({pid}) stopped at once");
        }
    }

    private static bool TryProcess(int pid, out Process? process)
    {
        try { process = Process.GetProcessById(pid); return true; }
        catch (ArgumentException) { process = null; return false; }
    }

    /// <summary>A command to the test agent over its pipe, like the app sends.</summary>
    private static void SendToAgent(string cmd)
    {
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", RigsightPaths.PipeName, System.IO.Pipes.PipeDirection.InOut);
        pipe.Connect(5000);
        var bytes = Encoding.UTF8.GetBytes(Rigsight.Core.Protocol.ProtocolJson.Serialize(new Rigsight.Core.Protocol.UiMessage { T = "cmd", Cmd = cmd }) + "\n");
        pipe.Write(bytes);
        pipe.Flush();
        Thread.Sleep(300);
    }

    private static int Gui(Process p, uint kind)
    {
        using var h = Query(p.Id);
        return h.IsInvalid ? 0 : (int)GetGuiResources(h, kind);
    }

    private sealed record ProcessSnapshot(double PrivateMb, double WorkingSetMb, int Handles, int Gdi, int User, int Threads)
    {
        public static ProcessSnapshot Take(Process p)
        {
            p.Refresh();
            return new(p.PrivateMemorySize64 / 1048576.0, p.WorkingSet64 / 1048576.0, p.HandleCount,
                Gui(p, 0), Gui(p, 1), p.Threads.Count);
        }
    }

    /// <summary>Least-squares slope per step.</summary>
    private static double Slope(IEnumerable<double> values)
    {
        var y = values.ToArray();
        if (y.Length < 2) return 0;
        double mx = (y.Length - 1) / 2.0, my = y.Average(), num = 0, den = 0;
        for (int i = 0; i < y.Length; i++)
        {
            num += (i - mx) * (y[i] - my);
            den += (i - mx) * (i - mx);
        }
        return num / den;
    }

    /// <summary>
    /// The Rigsight installed on this PC, as it runs: its agent measured without touching it, then its app opened (if it
    /// isn't already), timed, measured, and closed again. Nothing is changed: opening the app only reads.
    /// </summary>
    private static void MeasureInstalled(int seconds)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rigsight");
        bool Installed(Process p) => ExePath(p)?.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) == true;
        var agent = Process.GetProcessesByName("Rigsight.Agent").FirstOrDefault(Installed);
        string appExe = Path.Combine(folder, RigsightPaths.AppExe);
        if (agent is null || !File.Exists(appExe))
        {
            Notes.Add("--installed: Rigsight isn't installed and running on this PC");
            return;
        }
        string version = FileVersionInfo.GetVersionInfo(appExe).ProductVersion?.Split('+')[0] ?? "?";
        Notes.Add($"Installed Rigsight v{version} measured as it runs (its agent has admin rights and reads every sensor).");
        bool appWasOpen = Process.GetProcessesByName("Rigsight").Any(Installed);

        if (!appWasOpen)
        {
            Step($"Installed v{version}: agent with the app closed ({seconds} s, read-only)");
            var alone = Sample(seconds, agent);
            Record("installed.agent.idle.cpu_pct_core", alone[agent].CpuPctCore, "% of one core", "Installed agent CPU with the app closed", budget: 1.0);
            Record("installed.agent.idle.private_mb", alone[agent].PrivateEndMb, "MB", "Installed agent memory (private) with the app closed", budget: 80);
            Record("installed.agent.idle.handles", alone[agent].HandlesEnd, "", "Installed agent handles", budget: 1500);
            Record("installed.agent.idle.gdi", alone[agent].GdiEnd, "", "Installed agent GDI objects", budget: 150);
        }

        Step($"Installed v{version}: {(appWasOpen ? "the app (already open)" : "opening the app")}");
        var sw = Stopwatch.StartNew();
        Process? app = null;
        if (!appWasOpen)
        {
            // Without this run's test folder: the installed app must see only its own data.
            var start = new ProcessStartInfo(appExe) { UseShellExecute = false, WorkingDirectory = folder };
            start.Environment.Remove("RIGSIGHT_DATA_DIR");
            Process.Start(start)?.Dispose();
        }
        if (!WaitFor(() => (app = Process.GetProcessesByName("Rigsight").FirstOrDefault(p => Installed(p) && p.MainWindowHandle != IntPtr.Zero)) is not null, 20_000))
        {
            Fail("The installed app's window didn't appear");
            return;
        }
        var ui = new AppUi(app!);
        if (!appWasOpen)
        {
            if (!WaitFor(() => AppUi.HomeFilled(ui.Texts()), 30_000))
                Notes.Add("The installed app didn't show Home with its history within 30 s (it may open on another start page)");
            Record("installed.app.startup_ms", sw.ElapsedMilliseconds, "ms", "Installed app start until Home has its history", budget: 6000);
            Thread.Sleep(3000);
        }
        Step($"Installed v{version}: app open ({seconds} s)");
        var open = Sample(seconds, app!, agent);
        Record("installed.app.cpu_pct_core", open[app!].CpuPctCore, "% of one core", "Installed app CPU while open", budget: 3.0);
        Record("installed.app.private_mb", open[app!].PrivateEndMb, "MB", "Installed app memory (private)", budget: 250);
        Record("installed.agent.live.cpu_pct_core", open[agent].CpuPctCore, "% of one core", "Installed agent CPU while the app is open", budget: 5.0);
        Record("installed.agent.live.private_mb", open[agent].PrivateEndMb, "MB", "Installed agent memory while the app is open", budget: 150);
        if (!appWasOpen)
        {
            ui.Close();
            if (!app!.WaitForExit(10_000)) Fail("The installed app didn't close within 10 s");
        }
    }

    // ── Checks ────────────────────────────────────────────────────────────

    private sealed record Metric(string Key, double Value, string Unit, string What, double Budget)
    {
        public double? Baseline { get; set; }
        public string Status { get; set; } = "ok";
    }

    private static void Record(string key, double value, string unit, string what, double budget)
    {
        var m = new Metric(key, Math.Round(value, 4), unit, what, budget);
        if (value > budget)
        {
            m.Status = "over budget";
            // The installed Rigsight is whatever version is on this PC, not the build being checked: shown, not failed.
            if (Informational(key)) Notes.Add($"Installed build: {what} is {Fmt(value)} {unit}, over its budget of {Fmt(budget)}");
            else Fail($"{what}: {Fmt(value)} {unit} (budget {Fmt(budget)})");
        }
        Metrics.Add(m);
    }

    private static bool Informational(string key) => key.StartsWith("installed.", StringComparison.Ordinal);

    private static void Fail(string message)
    {
        Failures.Add(message);
        Console.WriteLine("  ✗ " + message);
    }

    /// <summary>
    /// Compares with the last accepted run on this PC. A measure that got clearly worse fails even inside its budget:
    /// times and CPU by half again (plus a little, for noise), memory and handles by a fifth.
    /// </summary>
    private static void CompareWithBaseline(string file)
    {
        if (!File.Exists(file))
        {
            Notes.Add("No baseline yet: run with --update-baseline once this build is accepted.");
            return;
        }
        var baseline = JsonNode.Parse(File.ReadAllText(file))!["metrics"]!.AsObject();
        if (baseline["pc"]?.GetValue<string>() is { } pc && pc != PcId)
            Notes.Add("The baseline is from another PC; comparisons are rough.");
        foreach (var m in Metrics)
        {
            if (baseline[m.Key]?.GetValue<double>() is not double b) continue;
            m.Baseline = b;
            double allowed = m.Key switch
            {
                // CPU in a quiet app is a few ms a second and swings run to run (0.15-0.46% of a core on Home): half again,
                // plus half a percentage point, still catches a real change (a chart redrawing too often shows whole points).
                _ when m.Key.Contains("cpu_pct") => b * 1.5 + 0.5,
                _ when m.Key.Contains("cpu_ms") => b * 1.5 + 150,
                _ when m.Key.EndsWith("_ms") => b * 1.5 + 500,
                // Handles, GDI and USER objects are whole numbers over a few rounds: one late step of two is already 0.6
                // a round, with nothing leaking (a real leak is one or more per page visit: fifteen a round and up).
                _ when m.Key is "app.leak.handles_per_cycle" or "app.leak.gdi_per_cycle" or "app.leak.user_per_cycle" => Math.Max(b * 2, 0) + 1,
                _ when m.Key.Contains("per_cycle") => Math.Max(b * 2, 0) + 0.5,
                _ when m.Key.EndsWith("_mb") => b * 1.2 + 10,
                _ => b * 1.2 + 20,
            };
            if (m.Value > allowed && m.Status == "ok")
            {
                m.Status = "worse than baseline";
                if (Informational(m.Key)) Notes.Add($"Installed build: {m.What} is {Fmt(m.Value)} {m.Unit}, was {Fmt(b)} in the baseline");
                else Fail($"{m.What}: {Fmt(m.Value)} {m.Unit}, was {Fmt(b)} in the baseline");
            }
        }
    }

    /// <summary>Tells PCs apart without putting a PC's name in the (public) repository.</summary>
    private static string PcId =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..12];

    private static void SaveBaseline(string file, string bin)
    {
        var metrics = new JsonObject();
        foreach (var m in Metrics) metrics[m.Key] = m.Value;
        var root = new JsonObject
        {
            ["comment"] = "Accepted end-to-end measures (Rigsight.E2E --update-baseline). Later runs fail if a measure gets clearly worse.",
            ["version"] = FileVersionInfo.GetVersionInfo(Path.Combine(bin, RigsightPaths.AppExe)).ProductVersion,
            ["date"] = DateTime.Now.ToString("yyyy-MM-dd"),
            ["pc"] = PcId,
            ["processors"] = Environment.ProcessorCount,
            ["metrics"] = metrics,
        };
        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    /// <summary>The run's log must hold no errors, exceptions or unresolved bindings (logged by a test copy of the app).</summary>
    private static void CheckLog()
    {
        if (!File.Exists(RigsightPaths.LogFile)) return;
        var lines = File.ReadAllLines(RigsightPaths.LogFile);
        var bad = lines.Where(l => l.Contains("[binding]") || l.Contains("Exception", StringComparison.Ordinal) || l.Contains(" error", StringComparison.OrdinalIgnoreCase))
            .Distinct().ToList();
        foreach (var line in bad.Take(20)) Fail("Log: " + line);
        if (bad.Count > 20) Fail($"Log: …and {bad.Count - 20} more");
        Console.WriteLine($"  log: {lines.Length} lines, {bad.Count} problem(s)");
    }

    // ── Report ────────────────────────────────────────────────────────────

    private static void WriteReport(string output, string bin, bool quick, bool admin)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Rigsight end-to-end check — {(Failures.Count == 0 ? "PASSED" : "FAILED")}");
        md.AppendLine();
        md.AppendLine($"Build `{bin}` (v{FileVersionInfo.GetVersionInfo(Path.Combine(bin, RigsightPaths.AppExe)).ProductVersion}), " +
                      $"{DateTime.Now:yyyy-MM-dd HH:mm}, {Environment.MachineName}, {Environment.ProcessorCount} logical processors{(quick ? ", quick run" : "")}.");
        md.AppendLine(admin ? "The agent ran as a test copy with admin rights (every sensor, like an installed one)."
            : "The agent ran as a test copy without admin rights (fewer sensors than an installed one).");
        md.AppendLine();
        if (Failures.Count > 0)
        {
            md.AppendLine("## Failures");
            foreach (var f in Failures) md.AppendLine($"- {f}");
            md.AppendLine();
        }
        md.AppendLine("## Measures");
        md.AppendLine();
        md.AppendLine("| Measure | Value | Budget | Baseline | |");
        md.AppendLine("|---|---:|---:|---:|---|");
        foreach (var m in Metrics)
            md.AppendLine($"| {m.What} | {Fmt(m.Value)} {m.Unit} | {Fmt(m.Budget)} | {(m.Baseline is double b ? Fmt(b) : "")} | {(m.Status == "ok" ? "✓" : Informational(m.Key) ? "(info) " + m.Status : "✗ " + m.Status)} |");
        if (Notes.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Notes");
            foreach (var n in Notes) md.AppendLine($"- {n}");
        }
        File.WriteAllText(Path.Combine(output, "report.md"), md.ToString());
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { passed = Failures.Count == 0, failures = Failures, notes = Notes, metrics = Metrics },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\nReport: {Path.Combine(output, "report.md")}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string Fmt(double v) => v.ToString(Math.Abs(v) >= 100 ? "0" : Math.Abs(v) >= 1 ? "0.0" : "0.000", CultureInfo.InvariantCulture);

    private static string Key(string page) => page.Replace(" ", "_").ToLowerInvariant();

    private static void Step(string text) => Console.WriteLine($"\n▸ {text}");

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try { if (condition()) return true; } catch { }
            Thread.Sleep(200);
        }
        return false;
    }

    private static bool LogHas(string text) => LogLine(text) is not null;

    private static List<string> LogLines(string text)
    {
        try
        {
            using var stream = new FileStream(RigsightPaths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var list = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null) if (line.Contains(text)) list.Add(line);
            return list;
        }
        catch (IOException) { return []; }
    }

    private static string? LogLine(string text)
    {
        try
        {
            using var stream = new FileStream(RigsightPaths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null) if (line.Contains(text)) return line;
        }
        catch (IOException) { }
        return null;
    }

    private static Process? FindApp(string exe)
    {
        foreach (var p in Process.GetProcessesByName("Rigsight"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase) && p.MainWindowHandle != IntPtr.Zero) return p;
            }
            catch { }
        }
        return null;
    }

    private static long Count(string db, string table) => Scalar(db, $"SELECT count(*) FROM {table}");

    private static long Scalar(string db, string sql) => Convert.ToInt64(ScalarObject(db, sql) is DBNull or null ? 0L : ScalarObject(db, sql));

    private static string? ScalarText(string db, string sql) => ScalarObject(db, sql)?.ToString();

    private static object? ScalarObject(string db, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Rigsight.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? Directory.GetCurrentDirectory();
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(SafeHandle process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeHandle process, int flags, StringBuilder name, ref int size);
}

/// <summary>
/// From another thread, asks the window for an answer (an empty message) every 20 ms and notes how long each took: a
/// window that is busy (building a page, rebuilding a list) can't answer until it's done.
/// </summary>
internal sealed class WindowWatch : IDisposable
{
    private readonly List<double> _answers = [];
    private readonly Thread _thread;
    private volatile bool _running = true;

    public WindowWatch(IntPtr window)
    {
        _thread = new Thread(() =>
        {
            while (_running)
            {
                double ms = Answer(window);
                lock (_answers) _answers.Add(ms);
                Thread.Sleep(20);
            }
        }) { IsBackground = true };
        _thread.Start();
    }

    /// <summary>How long the window took to answer, in ms (up to 5 s).</summary>
    public static double Answer(IntPtr window)
    {
        var t = Stopwatch.GetTimestamp();
        SendMessageTimeout(window, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, 2 /* SMTO_ABORTIFHUNG */, 5000, out _);
        return Stopwatch.GetElapsedTime(t).TotalMilliseconds;
    }

    public List<double> Stop()
    {
        _running = false;
        _thread.Join();
        lock (_answers) return [.. _answers];
    }

    public void Dispose() { if (_running) Stop(); }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
}

/// <summary>Drives the app's window through UI Automation (what screen readers use): no mouse, no keyboard.</summary>
internal sealed class AppUi(Process app)
{
    private IntPtr _window;

    /// <summary>
    /// The app's window, found once and kept: asked afresh, Windows can name the Settings menu (a small window of its
    /// own while it is open) as the process's main window, and that one is gone a moment later.
    /// </summary>
    private AutomationElement Root
    {
        get
        {
            if (_window == IntPtr.Zero) _window = app.MainWindowHandle;
            return AutomationElement.FromHandle(_window);
        }
    }

    public bool Has(string name) => Root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)) is not null;

    /// <summary>Every piece of text in the window as it is now.</summary>
    public List<string> Texts()
    {
        try
        {
            return [.. Root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Select(e => { try { return e.Current.Name; } catch (ElementNotAvailableException) { return ""; } })];
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            // The window is being built or rebuilt under the question: nothing to read this time, ask again.
            return [];
        }
    }

    /// <summary>
    /// Home has its history: the recap card shows the period's figures. (Not its "No history for…" line: a Home that
    /// said so before it had read anything is the very thing looked for, and the history used here always has a recap.)
    /// </summary>
    public static bool HomeFilled(IEnumerable<string> texts) => texts.Any(t => t.StartsWith("active · PC on for", StringComparison.Ordinal));

    /// <summary>A line a card of Home shows when it has nothing: only true once the history has been read.</summary>
    public static bool SaysNothingHere(string text) =>
        text.StartsWith("No history for", StringComparison.Ordinal) || text.StartsWith("Nothing out of the ordinary", StringComparison.Ordinal)
        || text.StartsWith("Nothing has changed", StringComparison.Ordinal) || text.StartsWith("Your most-used apps show up here", StringComparison.Ordinal);

    /// <summary>The sidebar's CPU/GPU readings show a degree sign once the agent sends live values.</summary>
    /// <summary>
    /// Something on the page that only the running agent can have sent: a temperature, or the fans' line in PC health.
    /// (It used to be the CPU / GPU / RAM readout at the foot of the sidebar, which is gone.)
    /// </summary>
    public bool HasLiveReadings() =>
        Root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Any(e => e.Current.Name is { } n && (n.EndsWith('°') || n.EndsWith("spinning now", StringComparison.Ordinal)));

    public void Press(string name)
    {
        AutomationElement? Find(string n) => Root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, n));
        var e = Find(name);
        // Settings is a line of the menu its row at the foot of the sidebar opens: open that first. Asked again for a
        // few seconds, the row included: while the window is busy (a page just switched, the menu being built as a
        // window of its own) a search can come back with nothing for something that is there.
        for (int tries = 0; tries < 30 && e is null && name == "Settings"; tries++)
        {
            if (Find("Settings and more") is { } row && row.TryGetCurrentPattern(TogglePattern.Pattern, out var open)
                && ((TogglePattern)open).Current.ToggleState != ToggleState.On)
                ((TogglePattern)open).Toggle();
            Thread.Sleep(150);
            e = Find(name);
        }
        if (e is null) throw new Exception($"No \"{name}\" in the window");
        if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select)) ((SelectionItemPattern)select).Select();
        else if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
        else if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) ((TogglePattern)toggle).Toggle();
        else throw new Exception($"Can't press \"{name}\"");
    }

    /// <summary>A sidebar item, to pick its page (see <see cref="SelectionItemPattern.Select"/>).</summary>
    public SelectionItemPattern NavItem(string page)
    {
        var e = Root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, page), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton)))
            ?? throw new Exception($"No \"{page}\" in the sidebar");
        return (SelectionItemPattern)e.GetCurrentPattern(SelectionItemPattern.Pattern);
    }

    /// <summary>Scrolls the page's biggest scrolling area to the end (loads more cards on pages that page in).</summary>
    public void ScrollToEnd()
    {
        var scrollers = Root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
        var biggest = scrollers.Cast<AutomationElement>().OrderByDescending(e => e.Current.BoundingRectangle.Height).FirstOrDefault();
        if (biggest?.GetCurrentPattern(ScrollPattern.Pattern) is ScrollPattern { Current.VerticallyScrollable: true } scroll)
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
    }

    public void MoveOffScreen() => MoveWindow(app.MainWindowHandle, -32000, -32000, 1440, 900, true);

    public void Close() => PostMessage(app.MainWindowHandle, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

    /// <summary>The window as drawn (works off-screen), as PNG.</summary>
    public void Screenshot(string path)
    {
        var h = app.MainWindowHandle;
        GetWindowRect(h, out var r);
        using var bmp = new System.Drawing.Bitmap(Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            PrintWindow(h, hdc, 2);
            g.ReleaseHdc(hdc);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
