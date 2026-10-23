using Rigsight.Core;
using Rigsight.Core.Export;
using Rigsight.Core.Games;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;
using Rigsight.Controls;

namespace Rigsight.ViewModels;

/// <summary>
/// What each page's Export box offers: only what that page shows, in its words, for the periods the page itself has,
/// starting on the one it shows, and narrowed the way the page is (the app or game picked, the crash filter and search,
/// the kind of change, the drives, the readings). Processes and All sensors have none: they're live, with no history.
/// </summary>
public static class PageExports
{
    /// <summary>The box for a page (its sidebar key), or null for a page with nothing to export.</summary>
    public static PageExportSpec? For(ShellViewModel shell, string page) => page switch
    {
        "home" => Home(),
        "reports" => Reports(shell.ReportsPage),
        "apps" => Apps(shell.Apps),
        "games" => Games(shell.Games),
        "crashes" => Crashes(shell.Crashes),
        "timeline" => Timeline(shell.Timeline),
        "temperatures" => Temperatures(shell.Live),
        "fans" => Fans(shell.Fans),
        "memory" => Memory(shell.Live),
        "storage" => Storage(shell.Storage),
        "network" => Network(shell.Network),
        _ => shell.FindCustomPage(page) is { } custom ? Dashboard(custom) : null,
    };

    /// <summary>Whether a page has an Export button.</summary>
    public static bool Has(string page) => page is "home" or "reports" or "apps" or "games" or "crashes" or "timeline" or "temperatures" or "fans"
        or "memory" or "storage" or "network" || page.StartsWith("custom:", StringComparison.Ordinal);

    private static string From(string page) => Lang.T($"From the {page} page. Nothing leaves this PC.");

    private static readonly Func<(DateTime From, DateTime To), ExportFilter> NoFilter = _ => ExportFilter.None;

    private static PageExportItem Table(ExportTable table, string name, string about, bool on = true) =>
        new(name, about, PageItemKind.Table, on) { Table = table };

    // ── Periods ──

    /// <summary>The period units a page with the shared period picker offers, around its own day.</summary>
    private static List<PeriodChoice> Units(DateTime anchor, bool all, bool custom)
    {
        var list = new List<PeriodChoice>
        {
            new(Lang.T("Day"), () => ReportBuilder.Bounds(ReportRange.Day, anchor)),
            new(Lang.T("Week"), () => ReportBuilder.Bounds(ReportRange.Week, anchor)),
            new(Lang.T("Month"), () => ReportBuilder.Bounds(ReportRange.Month, anchor)),
            new(Lang.T("Year"), () => ReportBuilder.Bounds(ReportRange.Year, anchor)),
        };
        if (all) list.Add(new(Lang.T("All time"), () => ReportBuilder.Bounds(ReportRange.All, anchor)));
        if (custom) list.Add(new(Lang.T("Custom"), null));
        return list;
    }

    /// <summary>Which of <see cref="Units"/> a page's unit is.</summary>
    private static int UnitIndex(ReportRange unit, IReadOnlyList<PeriodChoice> list) =>
        unit switch
        {
            ReportRange.Day => 0,
            ReportRange.Week => 1,
            ReportRange.Month => 2,
            ReportRange.Year => 3,
            ReportRange.All => list.ToList().FindIndex(p => p.Label == Lang.T("All time")),
            _ => list.Count - 1,
        };

    private static List<PeriodChoice> Days(bool week = true)
    {
        var today = DateTime.Today;
        var list = new List<PeriodChoice>
        {
            new(Lang.T("Today"), () => (today, today.AddDays(1))),
            new(Lang.T("Yesterday"), () => (today.AddDays(-1), today)),
        };
        if (week) list.Add(new(Lang.T("Last 7 days"), () => (today.AddDays(-6), today.AddDays(1))));
        return list;
    }

    private static List<PeriodChoice> Rolling()
    {
        var today = DateTime.Today;
        return
        [
            new(Lang.T("Last 7 days"), () => (today.AddDays(-6), today.AddDays(1))),
            new(Lang.T("Last 30 days"), () => (today.AddDays(-29), today.AddDays(1))),
            new(Lang.T("Last 12 months"), () => (today.AddYears(-1).AddDays(1), today.AddDays(1))),
            new(Lang.T("All time"), () => ReportBuilder.Bounds(ReportRange.All, today)),
        ];
    }

    /// <summary>A page's custom range, when it is on one.</summary>
    private static (DateTime, DateTime)? CustomOf(ReportRange unit, DateTime from, DateTime to) =>
        unit == ReportRange.Custom && to > from ? (from, to) : null;

    // ── One of a few: "All apps" or the one picked ──

    private static ExportScope OneOf(string name, IReadOnlyList<ScopeOption> options, Func<ExportScope, string> words) => new(name, false, options, words);

    /// <summary>A search typed on the page: the file holds only what matches it, as the page does, unless it's unticked.</summary>
    private static ExportScope Search(string typed) =>
        new(Lang.T("Search"), true, [new ScopeOption(Lang.T($"Only the ones matching “{typed}”"), true, true)],
            s => s.Picked.Any() ? Lang.T("as on the page") : Lang.T("everything in the period"));

    private static bool On(ExportScope? scope) => scope is not null && scope.Picked.Any();

    // ── The pages ──

    private static PageExportSpec Home() => new(
        Lang.T("Export today"), Lang.T("What Home shows, as files. Nothing leaves this PC."),
        [
            new(Lang.T("Today's report"), Lang.T("What stood out, your peaks, the apps you used and what changed, as one page."), PageItemKind.Report, true),
            Table(ExportTable.AppUsage, Lang.T("App usage"), Lang.T("Each app's active, background and minimized time.")),
            new(Lang.T("PC snapshot"), Lang.T("Every reading right now, with its lowest and highest."), PageItemKind.Snapshot, false) { IsNow = true },
        ],
        Days(week: false), 0, null, [], NoFilter);

    private static PageExportSpec Reports(ReportsViewModel vm)
    {
        var periods = Units(vm.Anchor, all: false, custom: true);
        return new(Lang.T("Export this report"), From(Lang.T("Reports")),
            [
                new(Lang.T("The report"), Lang.T("Everything on this page in one file: time, peaks, what stood out, against your usual."), PageItemKind.Report, true),
                Table(ExportTable.Temperatures, Lang.T("Your day, minute by minute"), Lang.T("The app in front, temperatures, load and power, every minute."), on: false),
                Table(ExportTable.AppUsage, Lang.T("Where the time went"), Lang.T("Each app's time each day, with its category."), on: false),
            ],
            periods, UnitIndex(vm.Range, periods), CustomOf(vm.Range, vm.CustomFrom, vm.CustomTo), [], NoFilter);
    }

    private static PageExportSpec Apps(AppsViewModel vm)
    {
        var periods = Units(vm.Anchor, all: true, custom: true);
        ExportScope? apps = null;
        if (vm.Selected is { } picked)
        {
            string name = vm.SelectedAlias;
            apps = OneOf(Lang.T("Apps"), [new(Lang.T("All apps"), null, false), new(name, picked.Exe, true)],
                s => s.Picked.FirstOrDefault() is null ? Lang.T("every app used in the period") : Lang.T($"only {name}, the app picked on the page"));
        }
        return new(Lang.T("Export app usage"), From(Lang.T("Apps")),
            [
                Table(ExportTable.AppUsage, Lang.T("Time in each app"), Lang.T("Active, background and minimized time, each day.")),
                Table(ExportTable.Sessions, Lang.T("Sessions"), Lang.T("Each stretch an app was in front, with its CPU and GPU temperature.")),
                Table(ExportTable.Internet, Lang.T("Internet by app"), Lang.T("What each app downloaded and uploaded, each day."), on: false),
            ],
            periods, UnitIndex(vm.Unit, periods), CustomOf(vm.Unit, vm.CustomFrom, vm.CustomTo), apps is null ? [] : [apps],
            _ => apps?.Picked.FirstOrDefault() is string exe ? new ExportFilter { Apps = new HashSet<string>([exe], StringComparer.OrdinalIgnoreCase) } : ExportFilter.None);
    }

    private static PageExportSpec Games(GamesViewModel vm)
    {
        var library = vm.Library;
        var one = vm.Opened?.Entry ?? vm.HeroGame;
        ExportScope? games = one is null ? null
            : OneOf(Lang.T("Games"), [new(Lang.T("All games"), null, vm.Opened is null), new(one.Name, one, vm.Opened is not null)],
                s => s.Picked.FirstOrDefault() is GameEntry g ? Lang.T($"only {g.Name}") : library.Count == 1 ? Lang.T("the 1 game in the library") : Lang.T($"all {library.Count} games in the library"));
        IReadOnlyList<GameEntry> Picked() => games?.Picked.FirstOrDefault() is GameEntry g ? [g] : library;
        return new(Lang.T("Export your games"), From(Lang.T("Games")),
            [
                new(Lang.T("Your library"), Lang.T("Each game, its launcher, play time, last played and size on disk."), PageItemKind.Sheet, true)
                {
                    IsNow = true, BytesPerDay = 6_000,
                    Sheet = run => { var list = Picked(); return (_, _) => ExportSheets.Library(list, run.Now); },
                },
                new(Lang.T("Play sessions"), Lang.T("Each time you played: how long, how it ended, fps and 1% low, graphics card heat and power."), PageItemKind.Sheet, true)
                {
                    BytesPerDay = 600,
                    Sheet = run => { var list = Picked(); return (db, s) => ExportSheets.Plays(db, list, s, run.From, run.To, run.Now); },
                },
            ],
            Rolling(), 3, null, games is null ? [] : [games], NoFilter);
    }

    private static PageExportSpec Crashes(CrashesViewModel vm)
    {
        var periods = Units(vm.Anchor, all: true, custom: true);
        var show = OneOf(Lang.T("Show"),
            [new(Lang.T("All"), "All", vm.Filter == "All"), new(Lang.T("Apps & games"), "Apps", vm.Filter == "Apps"), new(Lang.T("PC problems"), "Pc", vm.Filter == "Pc")],
            s => (s.Picked.FirstOrDefault() as string) switch
            {
                "Apps" => Lang.T("app and game crashes and freezes"),
                "Pc" => Lang.T("blue screens, sudden shut-offs and graphics driver resets"),
                _ => Lang.T("every problem in the period"),
            });
        var search = vm.SearchText is { Length: > 0 } typed && TextMatch.Words(typed).Length > 0 ? Search(typed.Trim()) : null;
        var page = vm.Shown;
        var scopes = new List<ExportScope> { show };
        if (search is not null) scopes.Add(search);
        return new(Lang.T("Export crashes"), From(Lang.T("Crashes")),
            [
                Table(ExportTable.Crashes, Lang.T("Crash list"), Lang.T("Each crash, freeze, blue screen, shut-off and driver reset, one per line.")),
                new(Lang.T("Crashes with their context"), Lang.T("Each one with what was running, the readings before it and what changed that week."), PageItemKind.Stories, true),
            ],
            periods, vm.Unit == ReportRange.All ? periods.FindIndex(p => p.Label == Lang.T("All time")) : UnitIndex(vm.Unit, periods),
            CustomOf(vm.Unit, vm.CustomFrom, vm.CustomTo), scopes,
            period =>
            {
                string filter = show.Picked.FirstOrDefault() as string ?? "All";
                bool searching = On(search);
                if (filter == "All" && !searching) return ExportFilter.None;
                // The period on the page: exactly the crashes its list shows. Another one: the same filter and search, crash by crash.
                if (period == page)
                {
                    var ids = vm.ShownIds(filter, searching);
                    return new ExportFilter { Crashes = e => ids.Contains(e.Id) };
                }
                var words = searching ? TextMatch.Words(vm.SearchText) : [];
                return new ExportFilter { Crashes = e => CrashHas(e, filter, words) };
            }, FromInstall: false);
    }

    /// <summary>A crash on its own against the Crashes page's filter and search (for a period other than the page's).</summary>
    internal static bool CrashHas(CrashEvent e, string filter, string[] words)
    {
        bool system = e.Kind is CrashKind.SystemCrash or CrashKind.UnexpectedShutdown or CrashKind.GpuDriverReset;
        if (filter == "Apps" && system || filter == "Pc" && !system) return false;
        if (words.Length == 0) return true;
        string? app = e.AppExe.Length > 0 ? Core.Apps.AppCatalog.KnownName(e.AppExe) ?? Core.Apps.AppCatalog.FallbackName(e.AppExe) : null;
        return TextMatch.Has(words, CrashExplainer.Explain(e, app).Title, app, e.AppExe, e.Module, e.Code, e.Detail is { } d ? SavedText.Show(d) : null);
    }

    private static PageExportSpec Timeline(TimelineViewModel vm)
    {
        var scopes = new List<ExportScope>();
        ExportScope? kinds = null;
        if (vm.Filter is { Key: not "all" } filter)
        {
            kinds = OneOf(Lang.T("Changes"), [new(Lang.T("Everything"), null, false), new(filter.Name, filter.Key, true)],
                s => s.Picked.FirstOrDefault() is null ? Lang.T("every kind of change") : Lang.T("the kind picked on the page"));
            scopes.Add(kinds);
        }
        var search = TextMatch.Words(vm.Search).Length > 0 ? Search(vm.Search.Trim()) : null;
        if (search is not null) scopes.Add(search);
        var now = vm.Now;
        return new(Lang.T("Export the timeline"), From(Lang.T("Timeline")),
            [
                Table(ExportTable.Timeline, Lang.T("Changes"), Lang.T("What was installed, updated, removed or switched, with the version before.")),
                new(Lang.T("This PC now"), Lang.T("Windows, graphics driver, BIOS, memory, power plan, apps installed."), PageItemKind.Sheet, false)
                {
                    IsNow = true, BytesPerDay = 1_000,
                    Sheet = run => { var facts = now.Select(f => new FactRow(f.Label, f.Value, f.Since)).ToList(); return (_, _) => ExportSheets.PcNow(facts, run.Now); },
                },
            ],
            Rolling(), 3, null, scopes,
            _ =>
            {
                string? key = kinds?.Picked.FirstOrDefault() as string;
                return new ExportFilter
                {
                    ChangeKinds = key is null ? null : TimelineViewModel.KindsOf(key)?.ToHashSet(),
                    ResultsOnly = key == TimelineViewModel.ResultsKey,
                    ChangeWords = On(search) ? TextMatch.Words(vm.Search) : null,
                };
            }, FromInstall: false);
    }

    private static PageExportSpec Temperatures(LiveData live)
    {
        var day = live.ChartDay;
        int window = live.ChartWindowSeconds;
        // The page's own short window ("Last hour") when it's on one, else the last 24 hours: up to now, to the minute.
        int recent = window is 300 or 3600 or 21600 ? window : 86400;
        string recentLabel = recent switch { 300 => Lang.T("Last 5 minutes"), 3600 => Lang.T("Last hour"), 21600 => Lang.T("Last 6 hours"), _ => Lang.T("Last 24 hours") };
        var periods = new List<PeriodChoice>
        {
            new(recentLabel, () => { var now = DateTime.Now; return (new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddSeconds(-recent), now); }),
            new(Lang.T("Day"), () => ReportBuilder.Bounds(ReportRange.Day, day)),
            new(Lang.T("Week"), () => ChartRange.Rolling(ChartRange.Week, day)),
            new(Lang.T("Month"), () => ChartRange.Rolling(ChartRange.Month, day)),
            new(Lang.T("Year"), () => ChartRange.Rolling(ChartRange.Year, day)),
            new(Lang.T("Custom"), null),
        };
        int picked = window switch
        {
            0 => 1,
            ChartRange.Week => 2,
            ChartRange.Month => 3,
            ChartRange.Year => 4,
            ChartRange.Custom => 5,
            _ => 0,
        };
        // The readings in the chart's legend start ticked, in its colours; load and power are there to add.
        var readings = new ExportScope(Lang.T("Readings"), true,
            [
                new(Lang.T("CPU temperature"), ExportReadings.CpuTemp, live.CpuTemp is not null, "CpuBrush"),
                new(Lang.T("GPU temperature"), ExportReadings.GpuTemp, live.GpuTemp is not null, "GpuBrush"),
                new(Lang.T("GPU hot spot"), ExportReadings.GpuHotSpot, live.GpuHotSpot is not null, "PurpleBrush"),
                new(Lang.T("GPU memory"), ExportReadings.GpuMemory, live.GpuMemJunction is not null, "PinkBrush"),
                new(Lang.T("CPU and GPU load"), ExportReadings.Load, false),
                new(Lang.T("CPU and GPU power"), ExportReadings.Power, false),
            ],
            s =>
            {
                int on = s.Options.Count(o => o.IsOn);
                return on == 0 ? Lang.T("none picked") : s.AllPicked ? Lang.T("all of them") : s.Options.Take(4).All(o => o.IsOn) && on == 4 ? Lang.T("the ones in the chart, as on the page") : Lang.T($"{on} of 6");
            });
        return new(Lang.T("Export temperatures"), From(Lang.T("Temperatures")),
            [
                Table(ExportTable.Temperatures, Lang.T("History"), Lang.T("Each minute's average, highest and lowest of each reading.")),
                new(Lang.T("Readings now"), Lang.T("Every reading right now, with its lowest and highest."), PageItemKind.Snapshot, false) { IsNow = true },
            ],
            periods, picked, live.IsChartCustom ? live.ChartPeriod : null, [readings],
            _ => new ExportFilter { Readings = readings.Picked.OfType<ExportReadings>().Aggregate(ExportReadings.None, (a, b) => a | b) });
    }

    private static PageExportSpec Fans(FansViewModel vm)
    {
        var periods = Units(vm.Anchor, all: false, custom: false);
        return new(Lang.T("Export fans"), From(Lang.T("Fans")),
            [
                Table(ExportTable.Fans, Lang.T("Fan speeds"), Lang.T("Every fan, every minute the PC was on.")),
                new(Lang.T("Summary"), Lang.T("Quiet time, fastest, what made them work hardest, how often they started."), PageItemKind.Sheet, false)
                {
                    BytesPerDay = 300,
                    Sheet = run =>
                    {
                        // The figures as the page shows them now (for its own period).
                        var facts = new List<FactRow>
                        {
                            new(vm.Unit == ReportRange.Year ? Lang.T("Average speed") : Lang.T("Quiet time"), vm.QuietText, vm.QuietNote),
                            new(Lang.T("Made them work hardest"), vm.HardestApp, vm.HardestNote),
                            new(Lang.T("Fastest"), vm.FastestText, vm.FastestNote),
                        };
                        if (vm.LoudestHour is { } loud) facts.Add(new(vm.LoudestLabel, loud, vm.LoudestNote));
                        if (vm.StartsText is { } starts) facts.Add(new(Lang.T("Started up"), starts, vm.StartsNote));
                        if (vm.SilentText is { } silent) facts.Add(new(Lang.T("Longest silence"), silent, vm.SilentNote));
                        var (from, to) = ReportBuilder.Bounds(vm.Unit, vm.Anchor);
                        return (_, _) => ExportSheets.Summary(Lang.T("Fans summary"), Lang.T("What the Fans page says about the period."), facts, from, to);
                    },
                },
            ],
            periods, UnitIndex(vm.Unit, periods), null, [], NoFilter);
    }

    private static PageExportSpec Memory(LiveData live) => new(
        Lang.T("Export memory"), From(Lang.T("Memory")),
        [
            Table(ExportTable.Memory, Lang.T("Biggest memory users"), Lang.T("What each app held, hour by hour: average and peak.")),
            new(Lang.T("Using memory right now"), Lang.T("Each app and its processes, now and what it usually holds."), PageItemKind.Sheet, false)
            {
                IsNow = true, BytesPerDay = 8_000,
                Sheet = run =>
                {
                    var rows = live.Procs.Select(p => new MemoryNowRow(p.Name, p.Exe, p.Count, p.MemMB, p.UsualMB)).ToList();
                    return (_, _) => ExportSheets.MemoryNow(rows, run.Now);
                },
            },
        ],
        Days(), 0, null, [], NoFilter);

    private static PageExportSpec Storage(StorageViewModel vm)
    {
        var drives = new ExportScope(Lang.T("Drives"), true,
            [.. vm.Volumes.Select(v => { string letter = v.Root.TrimEnd('\\').TrimEnd(':'); return new ScopeOption(Lang.T($"the {letter} drive"), v.Root.TrimEnd('\\'), true); })],
            s =>
            {
                int on = s.Options.Count(o => o.IsOn), all = s.Options.Count;
                return on == 0 ? Lang.T("none picked") : on == all ? Lang.T("all of them, as on the page") : Lang.T($"{on} of {all}");
            });
        var today = DateTime.Today;
        return new(Lang.T("Export drive space"), From(Lang.T("Storage")),
            [
                Table(ExportTable.DriveSpace, Lang.T("Drive space"), Lang.T("How full each drive was on each day.")),
                new(Lang.T("Space you could free"), Lang.T("The list on this page, with sizes. Nothing is deleted."), PageItemKind.Sheet, false)
                {
                    IsNow = true, BytesPerDay = 1_000,
                    Sheet = run =>
                    {
                        var rows = vm.Cleanup.Select(c => new SpaceRow(c.Title, c.Description, c.Size, c.Path)).ToList();
                        return (_, _) => ExportSheets.SpaceToFree(rows, run.Now);
                    },
                },
            ],
            [
                new(Lang.T("Last 7 days"), () => (today.AddDays(-6), today.AddDays(1))),
                new(Lang.T("Last 30 days"), () => (today.AddDays(-29), today.AddDays(1))),
                new(Lang.T("All time"), () => ReportBuilder.Bounds(ReportRange.All, today)),
            ], 1, null, drives.Options.Count > 1 ? [drives] : [],
            _ => drives.AllPicked ? ExportFilter.None : new ExportFilter { Drives = drives.Picked.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) });
    }

    private static PageExportSpec Network(NetworkViewModel vm)
    {
        var periods = Units(vm.Anchor, all: false, custom: false);
        return new(Lang.T("Export internet use"), From(Lang.T("Network")),
            [
                Table(ExportTable.Internet, Lang.T("Internet by app"), Lang.T("What each app downloaded and uploaded, and how much in the background, each day.")),
                Table(ExportTable.InternetHours, Lang.T("Usage each hour"), Lang.T("What each app downloaded and uploaded in each hour."), on: false),
            ],
            periods, UnitIndex(vm.Unit, periods), null, [], NoFilter);
    }

    /// <summary>
    /// A dashboard: one line per kind of data its tiles show, never one per tile (eight at most), each saying which
    /// tiles it comes from.
    /// </summary>
    private static PageExportSpec Dashboard(CustomPageViewModel page)
    {
        var groups = new List<(string Key, List<string> Tiles)>();
        foreach (var tile in page.Tiles)
        {
            string? key = tile.Kind switch
            {
                "cpu-gauge" or "gpu-gauge" or "temp-chart" => "temps",
                "fans" => "fans",
                "drives" => "drives",
                "top-memory" => "memory",
                "today" or "most-used" or "yesterday" => "day",
                "insights" or "peaks" => "report",
                "crashes" => "crashes",
                "sensor" => tile.SensorRef switch
                {
                    "key:" + KeySensors.GpuFan => "fans",
                    "key:" + KeySensors.RamLoad or "key:" + KeySensors.GpuVramLoad => "memory",
                    _ => "temps",
                },
                _ when tile.Kind.StartsWith("net-", StringComparison.Ordinal) => "internet",
                _ => null,
            };
            if (key is null) continue;
            if (groups.FirstOrDefault(g => g.Key == key) is { Key: not null } found) found.Tiles.Add(tile.Title);
            else groups.Add((key, [tile.Title]));
        }
        var order = new[] { "temps", "fans", "memory", "drives", "internet", "day", "report", "crashes" };
        var items = groups.OrderBy(g => Array.IndexOf(order, g.Key)).Select(g =>
        {
            string tiles = string.Join(", ", g.Tiles.Distinct());
            string from = g.Tiles.Distinct().Count() == 1 ? Lang.T($"From the {tiles} tile.") : Lang.T($"From the {tiles} tiles.");
            return g.Key switch
            {
                "temps" => Table(ExportTable.Temperatures, Lang.T("CPU and GPU"), $"{Lang.T("Temperature, load and power each minute.")} {from}"),
                "fans" => Table(ExportTable.Fans, Lang.T("Fans"), $"{Lang.T("Every fan, every minute.")} {from}"),
                "memory" => Table(ExportTable.Memory, Lang.T("Memory"), $"{Lang.T("What each app held, hour by hour.")} {from}"),
                "drives" => Table(ExportTable.DriveSpace, Lang.T("Drives"), $"{Lang.T("How full each drive was on each day.")} {from}"),
                "internet" => Table(ExportTable.InternetHours, Lang.T("Internet"), $"{Lang.T("What each app downloaded and uploaded, by hour.")} {from}"),
                "day" => Table(ExportTable.AppUsage, Lang.T("Your day"), $"{Lang.T("Each app's active time.")} {from}"),
                "report" => new PageExportItem(Lang.T("What stood out"), $"{Lang.T("The report for the period.")} {from}", PageItemKind.Report, true),
                _ => Table(ExportTable.Crashes, Lang.T("Crashes"), $"{Lang.T("Each crash and shut-off, one per line.")} {from}"),
            };
        }).ToList();
        string name = page.Name;
        return new(Lang.T($"Export {name}"), Lang.T("The readings on this dashboard's tiles. Nothing leaves this PC."), items, Days(), 0, null, [], NoFilter);
    }
}
