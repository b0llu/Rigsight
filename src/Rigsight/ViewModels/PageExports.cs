using Rigsight.Core;
using Rigsight.Core.Export;
using Rigsight.Core.Games;
using Rigsight.Core.Reports;
using Rigsight.Controls;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// What each page's Export box offers: only what that page shows, in its words, for the periods the page itself has,
/// starting on the one it shows, and narrowed the way the page is (the app or game picked, the category, the crash
/// filter, the searches, the kind of change, the drives, the readings). Every file follows the period picked in the box,
/// or is what the page shows now (said beside it). Processes and All sensors have none: they're live, with no history.
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
        "crashes" => Crashes(shell.Crashes, shell.Reports, shell.Settings),
        "timeline" => Timeline(shell.Timeline),
        "temperatures" => Temperatures(shell.Live),
        "fans" => Fans(shell.Fans, shell.Reports, shell.Live, shell.Settings),
        "memory" => Memory(shell.Live),
        "storage" => Storage(shell.Storage),
        "network" => Network(shell.Network),
        _ => shell.FindCustomPage(page) is { } custom ? Dashboard(custom, shell.Settings) : null,
    };

    /// <summary>Whether a page has an Export button.</summary>
    public static bool Has(string page) => page is "home" or "reports" or "apps" or "games" or "crashes" or "timeline" or "temperatures" or "fans"
        or "memory" or "storage" or "network" || page.StartsWith("custom:", StringComparison.Ordinal);

    private static string From(string page) => Lang.T($"From the {page} page. Nothing leaves this PC.");

    private static readonly Func<PageExportRun, Task<ExportFilter>> NoFilter = _ => Task.FromResult(ExportFilter.None);

    private static Func<PageExportRun, Task<ExportFilter>> Now(Func<PageExportRun, ExportFilter> filter) => run => Task.FromResult(filter(run));

    private static PageExportItem Table(ExportTable table, string name, string about, bool on = true) =>
        new(name, about, PageItemKind.Table, on) { Table = table };

    // ── Periods ──

    private static PeriodChoice Unit(string label, ReportRange unit, DateTime anchor) =>
        new(label, () => ReportBuilder.Bounds(unit, anchor)) { Unit = unit, Anchor = anchor };

    private static PeriodChoice Custom() => new(Lang.T("Custom"), null) { Unit = ReportRange.Custom };

    /// <summary>The period units a page with the shared period picker offers, around its own day.</summary>
    private static List<PeriodChoice> Units(DateTime anchor, bool all, bool custom)
    {
        var list = new List<PeriodChoice>
        {
            Unit(Lang.T("Day"), ReportRange.Day, anchor),
            Unit(Lang.T("Week"), ReportRange.Week, anchor),
            Unit(Lang.T("Month"), ReportRange.Month, anchor),
            Unit(Lang.T("Year"), ReportRange.Year, anchor),
        };
        if (all) list.Add(Unit(Lang.T("All time"), ReportRange.All, anchor));
        if (custom) list.Add(Custom());
        return list;
    }

    /// <summary>Which of the choices is a page's unit (the first when it has none of them).</summary>
    private static int IndexOf(IReadOnlyList<PeriodChoice> list, ReportRange unit) => Math.Max(0, list.ToList().FindIndex(p => p.Unit == unit));

    private static PeriodChoice Days(string label, int back, int days)
    {
        var today = DateTime.Today;
        return new(label, () => (today.AddDays(-back), today.AddDays(days - back))) { Unit = days == 1 ? ReportRange.Day : null, Anchor = today.AddDays(-back) };
    }

    private static List<PeriodChoice> Rolling()
    {
        var today = DateTime.Today;
        return
        [
            Days(Lang.T("Last 7 days"), 6, 7),
            Days(Lang.T("Last 30 days"), 29, 30),
            new(Lang.T("Last 12 months"), () => (today.AddYears(-1).AddDays(1), today.AddDays(1))),
            Unit(Lang.T("All time"), ReportRange.All, today),
        ];
    }

    /// <summary>A page's custom range, when it is on one.</summary>
    private static (DateTime, DateTime)? CustomOf(ReportRange unit, DateTime from, DateTime to) =>
        unit == ReportRange.Custom && to > from ? (from, to) : null;

    // ── What a page narrows to ──

    private static ExportScope OneOf(string name, IReadOnlyList<ScopeOption> options, Func<ExportScope, string> words, Func<ExportScope, bool>? narrows = null) =>
        new(name, false, options, words, narrows);

    /// <summary>A search typed on the page: the files hold only what matches it, as the page does, unless it's unticked.</summary>
    private static ExportScope Search(string typed) =>
        new(Lang.T("Search"), true, [new ScopeOption(Lang.T($"Only the ones matching “{typed}”"), true, true)],
            s => s.Picked.Any() ? Lang.T("as on the page") : Lang.T("everything in the period"), s => s.Picked.Any(), allowsNone: true);

    private static ExportScope? SearchOf(string? typed) => TextMatch.Words(typed).Length > 0 ? Search(typed!.Trim()) : null;

    private static bool On(ExportScope? scope) => scope is not null && scope.Picked.Any();

    // ── The pages ──

    /// <summary>Home: today, or the periods its recap card offers.</summary>
    private static PageExportSpec Home()
    {
        var today = DateTime.Today;
        PeriodChoice Recap(string label, RecapPeriod period)
        {
            var (unit, anchor) = HomeViewModel.Bounds(period, today);
            return Unit(label, unit, anchor);
        }
        return new(Lang.T("Export from Home"), Lang.T("What Home shows, as files. Nothing leaves this PC."),
            [
                new(Lang.T("The report"), Lang.T("What stood out, your peaks, the apps you used and what changed, as one page."), PageItemKind.Report, true),
                Table(ExportTable.AppUsage, Lang.T("App usage"), Lang.T("Each app's active, background and minimized time.")),
                new(Lang.T("PC snapshot"), Lang.T("Every reading right now, with its lowest and highest."), PageItemKind.Snapshot, false) { IsNow = true },
            ],
            [
                Unit(Lang.T("Today"), ReportRange.Day, today),
                Recap(Lang.T("Yesterday"), RecapPeriod.Yesterday),
                Recap(Lang.T("Last week"), RecapPeriod.LastWeek),
                Recap(Lang.T("Last month"), RecapPeriod.LastMonth),
                Recap(Lang.T("Last year"), RecapPeriod.LastYear),
            ], 0, null, [], NoFilter);
    }

    private static PageExportSpec Reports(ReportsViewModel vm)
    {
        var periods = Units(vm.Anchor, all: false, custom: true);
        return new(Lang.T("Export this report"), From(Lang.T("Reports")),
            [
                new(Lang.T("The report"), Lang.T("Everything on this page in one file, with time, peaks and what stood out, against your usual."), PageItemKind.Report, true),
                Table(ExportTable.Temperatures, Lang.T("Your day, minute by minute"), Lang.T("The app in front, temperatures, load and power, every minute."), on: false),
                Table(ExportTable.AppUsage, Lang.T("Where the time went"), Lang.T("Each app's time each day, with its category."), on: false),
            ],
            periods, IndexOf(periods, vm.Range), CustomOf(vm.Range, vm.CustomFrom, vm.CustomTo), [], NoFilter, FirstDay: vm.FirstDay);
    }

    /// <summary>Apps: all of them, the category the list is filtered to, or the app picked; and the list's search.</summary>
    private static PageExportSpec Apps(AppsViewModel vm)
    {
        var periods = Units(vm.Anchor, all: true, custom: true);
        var category = vm.Filter?.Category;
        string categoryName = vm.Filter?.Label ?? "";
        var picked = vm.Selected;
        string appName = picked is null ? "" : vm.SelectedAlias;
        var options = new List<ScopeOption> { new(Lang.T("All apps"), null, false) };
        if (category is { } c) options.Add(new(categoryName, c, false));
        if (picked is not null) options.Add(new(appName, picked.Exe, false));
        // The app when one was picked; else the category the list shows; else every app.
        (picked is not null && vm.PickedByUser ? options[^1] : category is not null ? options[1] : options[0]).IsOn = true;
        var apps = OneOf(Lang.T("Apps"), options, s => s.Picked.FirstOrDefault() switch
        {
            string => Lang.T($"only {appName}, the app picked on the page"),
            Core.Settings.AppCategory => Lang.T($"only the {categoryName} category, as the list is filtered"),
            _ => Lang.T("every app used in the period"),
        });
        var search = SearchOf(vm.Search);
        var scopes = new List<ExportScope> { apps };
        if (search is not null) scopes.Add(search);
        return new(Lang.T("Export app usage"), From(Lang.T("Apps")),
            [
                Table(ExportTable.AppUsage, Lang.T("Time in each app"), Lang.T("Active, background and minimized time, each day.")),
                Table(ExportTable.Sessions, Lang.T("Sessions"), Lang.T("Each stretch an app was in front, with its CPU and GPU temperature.")),
                Table(ExportTable.Internet, Lang.T("Internet by app"), Lang.T("What each app downloaded and uploaded, each day."), on: false),
            ],
            periods, IndexOf(periods, vm.Unit), CustomOf(vm.Unit, vm.CustomFrom, vm.CustomTo), scopes,
            Now(_ => new ExportFilter
            {
                Apps = apps.Picked.FirstOrDefault() is string exe ? new HashSet<string>([exe], StringComparer.OrdinalIgnoreCase) : null,
                Category = apps.Picked.FirstOrDefault() as Core.Settings.AppCategory?,
                AppWords = On(search) ? TextMatch.Words(vm.Search) : null,
            }), FirstDay: vm.FirstDay);
    }

    /// <summary>Games: the games the library shows (its filter and search), or the one picked.</summary>
    private static PageExportSpec Games(GamesViewModel vm)
    {
        var library = vm.Library;
        var shown = vm.Cards.Select(c => c.Entry).ToList();
        bool narrowed = vm.Ready && shown.Count != library.Count;
        // What "all" is, read at export: the library the page has then (read first if it hasn't been yet).
        IReadOnlyList<GameEntry> All() => narrowed ? [.. vm.Cards.Select(c => c.Entry)] : vm.Library;
        int count = (narrowed ? shown : library).Count;
        var one = vm.Opened?.Entry ?? vm.HeroGame;
        ExportScope? games = one is null ? null
            : OneOf(Lang.T("Games"), [new(narrowed ? Lang.T("Games shown") : Lang.T("All games"), null, vm.Opened is null), new(one.Name, one, vm.Opened is not null)],
                s => s.Picked.FirstOrDefault() is GameEntry g ? Lang.T($"only {g.Name}")
                    : narrowed ? (count == 1 ? Lang.T("the 1 game the library shows") : Lang.T($"the {count} games the library shows"))
                    : count == 1 ? Lang.T("the 1 game in the library") : Lang.T($"all {count} games in the library"),
                // The library as filtered on the page narrows the files too.
                s => narrowed || s.Picked.FirstOrDefault() is not null);
        async Task<IReadOnlyList<GameEntry>> PickedAsync()
        {
            if (!vm.Ready) await vm.LoadAsync();
            return games?.Picked.FirstOrDefault() is GameEntry g ? [g] : All();
        }
        return new(Lang.T("Export your games"), From(Lang.T("Games")),
            [
                new(Lang.T("Your library"), Lang.T("Each game, its launcher, play time, last played and size on disk."), PageItemKind.Sheet, true)
                {
                    IsNow = true, BytesPerDay = 6_000,
                    SheetAsync = async run => { var list = await PickedAsync(); return (_, _) => ExportSheets.Library(list, run.Now); },
                },
                new(Lang.T("Play sessions"), Lang.T("Each time you played, with how long, how it ended, fps and 1% low, graphics card heat and power."), PageItemKind.Sheet, true)
                {
                    BytesPerDay = 600,
                    SheetAsync = async run => { var list = await PickedAsync(); return (db, s) => ExportSheets.Plays(db, list, s, run.From, run.To, run.Now); },
                },
            ],
            Rolling(), 3, null, games is null ? [] : [games], NoFilter);
    }

    /// <summary>Crashes: the list as the page builds it (grouped, its filter, search and muting), for whichever period is picked.</summary>
    private static PageExportSpec Crashes(CrashesViewModel vm, ReportService reports, SettingsModel settings)
    {
        var periods = Units(vm.Anchor, all: true, custom: true);
        var show = OneOf(Lang.T("Show"),
            [new(Lang.T("All"), null, vm.Filter is not ("Apps" or "Pc")), new(Lang.T("Apps & games"), "Apps", vm.Filter == "Apps"), new(Lang.T("PC problems"), "Pc", vm.Filter == "Pc")],
            s => (s.Picked.FirstOrDefault() as string) switch
            {
                "Apps" => Lang.T("app and game crashes and freezes"),
                "Pc" => Lang.T("blue screens, sudden shut-offs, graphics driver resets and times several things failed at once"),
                _ => Lang.T("every problem in the period"),
            });
        var search = SearchOf(vm.SearchText);
        var scopes = new List<ExportScope> { show };
        if (search is not null) scopes.Add(search);
        bool grouped = vm.Grouped, showMuted = vm.ShowMuted;
        return new(Lang.T("Export crashes"), From(Lang.T("Crashes")),
            [
                Table(ExportTable.Crashes, Lang.T("Crash list"), Lang.T("Each crash, freeze, blue screen, shut-off and driver reset, one per line.")),
                new(Lang.T("Crashes with their context"), Lang.T("Each one with what was running, the readings before it and what changed that week."), PageItemKind.Stories, true),
            ],
            periods, IndexOf(periods, vm.Unit), CustomOf(vm.Unit, vm.CustomFrom, vm.CustomTo), scopes,
            async run =>
            {
                string filter = show.Picked.FirstOrDefault() as string ?? "All";
                var words = On(search) ? TextMatch.Words(vm.SearchText) : [];
                var ids = await CrashesViewModel.ShownIdsAsync(reports, settings.Current, run.From, run.To, filter, words, grouped, showMuted);
                return new ExportFilter { Crashes = e => ids.Contains(e.Id) };
            }, FromInstall: false, FirstDay: vm.Since);
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
        var search = SearchOf(vm.Search);
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
            Now(_ =>
            {
                string? key = kinds?.Picked.FirstOrDefault() as string;
                return new ExportFilter
                {
                    ChangeKinds = key is null ? null : TimelineViewModel.KindsOf(key)?.ToHashSet(),
                    ResultsOnly = key == TimelineViewModel.ResultsKey,
                    ChangeWords = On(search) ? TextMatch.Words(vm.Search) : null,
                };
            }), FromInstall: false);
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
            Unit(Lang.T("Day"), ReportRange.Day, day),
            new(Lang.T("Week"), () => ChartRange.Rolling(ChartRange.Week, day)),
            new(Lang.T("Month"), () => ChartRange.Rolling(ChartRange.Month, day)),
            new(Lang.T("Year"), () => ChartRange.Rolling(ChartRange.Year, day)),
            Custom(),
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
        // The readings in the chart's legend (the ones this PC has) start ticked, in its colours; load and power are there to add.
        bool[] inChart = [live.CpuTemp is not null, live.GpuTemp is not null, live.GpuHotSpot is not null, live.GpuMemJunction is not null, false, false];
        // No readings now (the agent isn't running): the history still has them, so the four temperatures start ticked.
        if (!inChart.Any(on => on)) inChart = [true, true, true, true, false, false];
        var readings = new ExportScope(Lang.T("Readings"), true,
            [
                new(Lang.T("CPU temperature"), ExportReadings.CpuTemp, inChart[0], "CpuBrush"),
                new(Lang.T("GPU temperature"), ExportReadings.GpuTemp, inChart[1], "GpuBrush"),
                new(Lang.T("GPU hot spot"), ExportReadings.GpuHotSpot, inChart[2], "PurpleBrush"),
                new(Lang.T("GPU memory"), ExportReadings.GpuMemory, inChart[3], "PinkBrush"),
                new(Lang.T("CPU and GPU load"), ExportReadings.Load, false),
                new(Lang.T("CPU and GPU power"), ExportReadings.Power, false),
            ],
            s =>
            {
                int on = s.Options.Count(o => o.IsOn), all = s.Options.Count;
                return on == 0 ? Lang.T("none picked") : s.AllPicked ? Lang.T("all of them")
                    : s.Options.Select((o, i) => o.IsOn == inChart[i]).All(same => same) ? Lang.T("the ones in the chart, as on the page") : Lang.T($"{on} of {all}");
            });
        return new(Lang.T("Export temperatures"), From(Lang.T("Temperatures")),
            [
                Table(ExportTable.Temperatures, Lang.T("History"), Lang.T("Each minute's average, highest and lowest of each reading.")),
                new(Lang.T("Readings now"), Lang.T("Every reading right now, with its lowest and highest."), PageItemKind.Snapshot, false) { IsNow = true },
            ],
            periods, picked, live.IsChartCustom ? live.ChartPeriod : null, [readings],
            Now(_ => new ExportFilter { Readings = readings.Picked.OfType<ExportReadings>().Aggregate(ExportReadings.None, (a, b) => a | b) }),
            FirstDay: live.ChartHistoryStart);
    }

    private static PageExportSpec Fans(FansViewModel vm, ReportService reports, LiveData live, SettingsModel settings)
    {
        var periods = Units(vm.Anchor, all: false, custom: false);
        var (unitShown, anchorShown) = (vm.Unit, vm.Anchor);
        return new(Lang.T("Export fans"), From(Lang.T("Fans")),
            [
                Table(ExportTable.Fans, Lang.T("Fan speeds"), Lang.T("Every fan, every minute the PC was on.")),
                new(Lang.T("Summary"), Lang.T("Quiet time, fastest, what made them work hardest, how often they started."), PageItemKind.Sheet, false)
                {
                    BytesPerDay = 300,
                    // The figures for the period picked: the page's own when it's the one on screen (and read), else worked
                    // out the way the page works them out.
                    SheetAsync = async run =>
                    {
                        var (unit, anchor) = (run.Choice?.Unit ?? unitShown, run.Choice?.Anchor ?? anchorShown);
                        bool onScreen = unit == vm.Unit && anchor == vm.Anchor && vm.Loaded && !vm.IsLoading;
                        var facts = onScreen ? vm.SummaryFacts() : await FansViewModel.SummaryAsync(reports, live, settings, unit, anchor);
                        return (_, _) => ExportSheets.Summary(Lang.T("Fans summary"), Lang.T("What the Fans page says about the period."), facts, run.From, run.To);
                    },
                },
            ],
            periods, IndexOf(periods, vm.Unit), null, [],
            // The fans the page lists (a header that never spun isn't there); none read yet: every fan recorded.
            Now(_ => vm.Fans.Count == 0 ? ExportFilter.None
                : new ExportFilter { FanSensors = vm.Fans.SelectMany(c => c.Sensors).Select(x => x.Id).ToHashSet(StringComparer.Ordinal) }),
            FirstDay: vm.FirstDay);
    }

    /// <summary>Memory: its hour-by-hour history, and the list of apps using memory now as the page lists it (its search and switch).</summary>
    private static PageExportSpec Memory(LiveData live) => new(
        Lang.T("Export memory"), From(Lang.T("Memory")),
        [
            Table(ExportTable.Memory, Lang.T("Biggest memory users"), Lang.T("What each app held, hour by hour, as average and peak.")),
            new(Lang.T("Using memory right now"), Lang.T("Each app and its processes, now and what it usually holds, as the page lists them."), PageItemKind.Sheet, false)
            {
                IsNow = true, BytesPerDay = 8_000,
                Sheet = run =>
                {
                    var rows = live.ProcsView.Cast<Models.ProcRow>().Select(p => new MemoryNowRow(p.Name, p.Exe, p.Count, p.MemMB, p.UsualMB)).ToList();
                    return (_, _) => ExportSheets.MemoryNow(rows, run.Now);
                },
            },
        ],
        [Days(Lang.T("Today"), 0, 1), Days(Lang.T("Yesterday"), 1, 1), Days(Lang.T("Last 7 days"), 6, 7)], 0, null, [], NoFilter);

    private static PageExportSpec Storage(StorageViewModel vm)
    {
        var drives = new ExportScope(Lang.T("Drives"), true,
            [.. vm.Volumes.Select(v => { string letter = v.Root.TrimEnd('\\').TrimEnd(':'); return new ScopeOption(Lang.T($"the {letter} drive"), v.Root.TrimEnd('\\'), true); })],
            s =>
            {
                int on = s.Options.Count(o => o.IsOn), all = s.Options.Count;
                return on == 0 ? Lang.T("none picked") : on == all ? Lang.T("all of them, as on the page") : Lang.T($"{on} of {all}");
            });
        return new(Lang.T("Export drive space"), From(Lang.T("Storage")),
            [
                Table(ExportTable.DriveSpace, Lang.T("Drive space"), Lang.T("How full each drive was on each day.")),
                new(Lang.T("Space you could free"), Lang.T("The list on this page, with sizes. Nothing is deleted."), PageItemKind.Sheet, false)
                {
                    IsNow = true, BytesPerDay = 1_000,
                    SheetAsync = async run =>
                    {
                        await vm.CleanupReadyAsync();
                        var rows = vm.Cleanup.Select(c => new SpaceRow(c.Title, c.Description, c.Size, c.Path)).ToList();
                        return (_, _) => ExportSheets.SpaceToFree(rows, run.Now);
                    },
                },
            ],
            [Days(Lang.T("Last 7 days"), 6, 7), Days(Lang.T("Last 30 days"), 29, 30), Unit(Lang.T("All time"), ReportRange.All, DateTime.Today)], 1, null,
            drives.Options.Count > 1 ? [drives] : [],
            Now(_ => drives.AllPicked ? ExportFilter.None : new ExportFilter { Drives = drives.Picked.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) }));
    }

    /// <summary>Network: its period, or the bar picked on its chart (the apps list then shows that bar's apps); all use or the Background list.</summary>
    private static PageExportSpec Network(NetworkViewModel vm)
    {
        var periods = Units(vm.Anchor, all: false, custom: false);
        int picked = IndexOf(periods, vm.Unit);
        if (vm.PickedBar is { } bar)
        {
            periods.Insert(0, new(Lang.T("The bar picked"), () => bar));
            picked = 0;
        }
        var count = OneOf(Lang.T("Internet use"), [new(Lang.T("All of it"), null, !vm.BackgroundOnly), new(Lang.T("Background only"), true, vm.BackgroundOnly)],
            s => s.Picked.FirstOrDefault() is true ? Lang.T("only what moved in the background, as the list shows it") : Lang.T("everything each app moved"));
        return new(Lang.T("Export internet use"), From(Lang.T("Network")),
            [
                // The bar picked is an hour on a day's chart: then the hours first.
                Table(ExportTable.Internet, Lang.T("Internet by app"), Lang.T("What each app downloaded and uploaded, and how much in the background, each day."), on: vm.PickedBar is null || vm.Unit != ReportRange.Day),
                Table(ExportTable.InternetHours, Lang.T("Usage each hour"), Lang.T("What each app downloaded and uploaded in each hour."), on: vm.PickedBar is not null && vm.Unit == ReportRange.Day),
            ],
            periods, picked, null, [count], Now(_ => new ExportFilter { BackgroundOnly = count.Picked.FirstOrDefault() is true }), FirstDay: vm.FirstDay);
    }

    /// <summary>
    /// A dashboard: one line per kind of data its tiles show, never one per tile (eight at most), each saying which
    /// tiles it comes from.
    /// </summary>
    private static PageExportSpec Dashboard(CustomPageViewModel page, SettingsModel settings)
    {
        var groups = new List<(string Key, List<string> Tiles)>();
        foreach (var tile in page.Tiles)
        {
            string? key = tile.Kind switch
            {
                // Every reading kept by the minute is in the one file: temperatures, load, power, RAM and video memory in use.
                "cpu-gauge" or "gpu-gauge" or "temp-chart" => "minutes",
                "fans" => "fans",
                "drives" => "drives",
                "top-memory" => "memory",
                "today" or "most-used" or "yesterday" => "day",
                "insights" or "peaks" => "report",
                "crashes" => "crashes",
                "sensor" => tile.SensorRef == "key:" + KeySensors.GpuFan || tile.Sensor?.Kind == SensorKind.Fan ? "fans" : "minutes",
                _ when tile.Kind.StartsWith("net-", StringComparison.Ordinal) => "internet",
                _ => null,
            };
            if (key is null) continue;
            if (groups.FirstOrDefault(g => g.Key == key) is { Key: not null } found) found.Tiles.Add(tile.Title);
            else groups.Add((key, [tile.Title]));
        }
        var order = new[] { "minutes", "fans", "memory", "drives", "internet", "day", "report", "crashes" };
        var items = groups.OrderBy(g => Array.IndexOf(order, g.Key)).Select(g =>
        {
            string tiles = string.Join(", ", g.Tiles.Distinct());
            string from = g.Tiles.Distinct().Count() == 1 ? Lang.T($"From the {tiles} tile.") : Lang.T($"From the {tiles} tiles.");
            return g.Key switch
            {
                "minutes" => Table(ExportTable.Temperatures, Lang.T("Readings each minute"), $"{Lang.T("Temperature, load, power and memory in use, each minute.")} {from}"),
                "fans" => Table(ExportTable.Fans, Lang.T("Fans"), $"{Lang.T("Every fan, every minute.")} {from}"),
                "memory" => Table(ExportTable.Memory, Lang.T("Memory by app"), $"{Lang.T("What each app held, hour by hour.")} {from}"),
                "drives" => Table(ExportTable.DriveSpace, Lang.T("Drives"), $"{Lang.T("How full each drive was on each day.")} {from}"),
                "internet" => Table(ExportTable.InternetHours, Lang.T("Internet"), $"{Lang.T("What each app downloaded and uploaded, by hour.")} {from}"),
                "day" => Table(ExportTable.AppUsage, Lang.T("Your day"), $"{Lang.T("Each app's active time.")} {from}"),
                "report" => new PageExportItem(Lang.T("What stood out"), $"{Lang.T("The report for the period.")} {from}", PageItemKind.Report, true),
                _ => Table(ExportTable.Crashes, Lang.T("Crashes"), $"{Lang.T("Each crash and shut-off, one per line.")} {from}"),
            };
        }).ToList();
        string name = page.Name;
        var today = DateTime.Today;
        return new(Lang.T($"Export {name}"), Lang.T("The readings on this dashboard's tiles. Nothing leaves this PC."), items,
            [Unit(Lang.T("Today"), ReportRange.Day, today), Unit(Lang.T("Yesterday"), ReportRange.Day, today.AddDays(-1)), Days(Lang.T("Last 7 days"), 6, 7), Days(Lang.T("Last 30 days"), 29, 30)],
            0, null, [],
            // Crashes as the Crashes tile counts them: muted apps left out.
            Now(_ => new ExportFilter { Crashes = e => !settings.Current.IsCrashMuted(e.AppExe) }));
    }
}
