using System.IO;
using Rigsight.Core.Data;
using Rigsight.Core.Export;
using Rigsight.Core.Games;
using Rigsight.Core.Stability;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// A page's own Export box, its Core side: the tables narrowed the way a page is (an app, games, crashes, kinds of
/// change, drives, readings), the files only a page makes, and the period said in words. The box on the real pages is
/// in AppUi.PageExportWindowTests.
/// </summary>
[Collection("UI")]
public sealed class PageExportTests
{
    private static readonly (DateTime From, DateTime To) All = (DateTime.Today.AddDays(-400), DateTime.Today.AddDays(1));

    private static List<List<string>> Rows(RigsightDb db, ExportTable table, ExportFilter? filter = null)
    {
        using var writer = new StringWriter();
        DataExport.Write(db, table, All.From, All.To, SeedData.QuietSettings(), writer, filter);
        return [.. writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(',').ToList())];
    }

    [Fact]
    public void One_app_picked_keeps_only_that_apps_rows_in_every_table_by_app()
    {
        SharedData.EnsureSeeded();
        using var db = RigsightDb.OpenReader()!;
        // The app with the most time, as the Apps page picks it first.
        var usage = Rows(db, ExportTable.AppUsage);
        string exe = usage.Skip(1).GroupBy(r => r[2]).OrderByDescending(g => g.Count()).First().Key;
        var only = new ExportFilter { Apps = new HashSet<string>([exe.ToUpperInvariant()], StringComparer.OrdinalIgnoreCase) };
        foreach (var (table, program) in new[] { (ExportTable.AppUsage, 2), (ExportTable.Sessions, 3), (ExportTable.Memory, 2) })
        {
            var all = Rows(db, table);
            var mine = Rows(db, table, only);
            Assert.Equal(all[0], mine[0]);
            Assert.True(mine.Count > 1, $"{table}: none of {exe}'s rows");
            Assert.True(mine.Count < all.Count, $"{table}: other apps' rows too");
            Assert.All(mine.Skip(1), r => Assert.Equal(exe, r[program], ignoreCase: true));
        }
        // Games only: every session a game's.
        var games = Rows(db, ExportTable.Sessions, new ExportFilter { GamesOnly = true });
        Assert.All(games.Skip(1), r => Assert.Equal("Yes", r[5]));
    }

    [Fact]
    public void The_readings_picked_are_the_temperature_files_columns()
    {
        SharedData.EnsureSeeded();
        using var db = RigsightDb.OpenReader()!;
        var all = Rows(db, ExportTable.Temperatures);
        var cpu = Rows(db, ExportTable.Temperatures, new ExportFilter { Readings = ExportReadings.CpuTemp });
        Assert.Equal(all.Count, cpu.Count);
        Assert.Contains("CPU (°C)", cpu[0]);
        Assert.DoesNotContain("GPU (°C)", cpu[0]);
        Assert.DoesNotContain("CPU load (%)", cpu[0]);
        // The minute and what was in front are always there; every row as wide as its headings.
        Assert.Equal("Time", cpu[0][0]);
        Assert.Contains("App in front", cpu[0]);
        Assert.All(cpu.Skip(1).Take(100), r => Assert.Equal(cpu[0].Count, r.Count));
        Assert.Equal(all[0].Count - 11, cpu[0].Count);
        // Load and power bring their pairs (and the clock speeds with the load).
        var load = Rows(db, ExportTable.Temperatures, new ExportFilter { Readings = ExportReadings.Load | ExportReadings.Power });
        Assert.Contains("GPU power (W)", load[0]);
        Assert.Contains("CPU speed (MHz)", load[0]);
        Assert.DoesNotContain("GPU hot spot highest (°C)", load[0]);
    }

    [Fact]
    public void Drives_kinds_of_change_and_crashes_narrow_their_files()
    {
        SharedData.EnsureSeeded();
        using var db = RigsightDb.OpenReader()!;
        var drives = Rows(db, ExportTable.DriveSpace);
        string first = drives[1][1];
        var one = Rows(db, ExportTable.DriveSpace, new ExportFilter { Drives = new HashSet<string>([first], StringComparer.OrdinalIgnoreCase) });
        Assert.All(one.Skip(1), r => Assert.Equal(first, r[1]));
        Assert.True(one.Count > 1);

        // The Timeline's kind and search, change by change.
        var driver = new SystemChange(DateTime.Today, ChangeKind.Driver, "NVIDIA graphics driver 621.01") { Was = "617.42" };
        var app = new SystemChange(DateTime.Today, ChangeKind.AppUpdated, "Discord updated");
        var drivers = new ExportFilter { ChangeKinds = new HashSet<ChangeKind>([ChangeKind.Driver]) };
        Assert.True(drivers.HasChange(driver));
        Assert.False(drivers.HasChange(app));
        var search = new ExportFilter { ChangeWords = ["nvidia", "617"] };
        Assert.True(search.HasChange(driver));
        Assert.False(search.HasChange(app));
        Assert.True(ExportFilter.None.HasChange(app));

        // Crashes as the Crashes page lists them: here, none.
        Assert.Single(Rows(db, ExportTable.Crashes, new ExportFilter { Crashes = _ => false }));
        Assert.True(Rows(db, ExportTable.Crashes).Count > 1);
    }

    [Fact]
    public void The_Apps_pages_category_and_search_narrow_the_files_by_app()
    {
        SharedData.EnsureSeeded();
        using var db = RigsightDb.OpenReader()!;
        var usage = Rows(db, ExportTable.AppUsage);
        // A category the history has (its column says it), and only its apps.
        string label = usage.Skip(1).GroupBy(r => r[3]).OrderByDescending(g => g.Count()).First().Key;
        var category = Enum.GetValues<Rigsight.Core.Settings.AppCategory>().First(c => Rigsight.Core.Apps.AppCatalog.Label(c) == label);
        var one = Rows(db, ExportTable.AppUsage, new ExportFilter { Category = category });
        Assert.True(one.Count > 1);
        Assert.All(one.Skip(1), r => Assert.Equal(label, r[3]));
        Assert.True(one.Count < usage.Count || usage.Skip(1).All(r => r[3] == label));
        // The search: each word in the name or the program.
        string name = usage[1][1];
        var found = Rows(db, ExportTable.Sessions, new ExportFilter { AppWords = [name.Split(' ')[0]] });
        Assert.True(found.Count > 1);
        Assert.All(found.Skip(1), r => Assert.True(r[2].Contains(name.Split(' ')[0], StringComparison.OrdinalIgnoreCase) || r[3].Contains(name.Split(' ')[0], StringComparison.OrdinalIgnoreCase)));
        Assert.Single(Rows(db, ExportTable.Sessions, new ExportFilter { AppWords = ["no-such-app-anywhere"] }));
        Assert.True(new ExportFilter { AppWords = ["x"] }.Narrows);
        Assert.False(ExportFilter.None.Narrows);
    }

    [Fact]
    public void A_one_of_choice_keeps_one_and_says_what_is_picked()
    {
        var show = new ExportScope("Show", false, [new("All", "All", true), new("Apps & games", "Apps", false), new("PC problems", "Pc", false)],
            s => (string)s.Picked.Single()!);
        show.Options[2].IsOn = true;
        Assert.Equal(["Pc"], show.Picked);
        Assert.Equal("Pc", show.WordsText);
        var drives = new ExportScope("Drives", true, [new("the C drive", "C:", true), new("the D drive", "D:", true)], s => $"{s.Picked.Count()}");
        drives.Options[0].IsOn = false;
        Assert.Equal(["D:"], drives.Picked);
        drives.PickAllCommand.Execute(null);
        Assert.True(drives.AllPicked);
        // Something to export with: an any-of choice needs one picked (a search may be off).
        Assert.True(drives.IsComplete);
        drives.PickAllCommand.Execute(null);
        Assert.False(drives.IsComplete);
        Assert.True(drives.Narrows);
        Assert.True(show.Narrows); // a pick with a value (PC problems) narrows; All wouldn't
    }

    [Fact]
    public void A_file_made_from_a_page_reads_like_a_table_as_text_and_as_csv()
    {
        var at = new DateTime(2026, 10, 23, 11, 20, 0);
        var game = new GameEntry
        {
            Name = "Risk of Rain 2", Launcher = Launcher.Steam, ActiveSec = 11 * 3600 + 360, Sessions = 9, AppIds = [1], Exes = ["Risk of Rain 2.exe"],
            FirstPlayed = at.AddDays(-20), LastPlayed = at.AddDays(-2),
            Installed = new InstalledGame { Key = "steam:632360", Launcher = Launcher.Steam, Name = "Risk of Rain 2", InstallDir = @"D:\Steam\Risk of Rain 2", SizeBytes = 4L << 30 },
        };
        var never = new GameEntry { Name = "Mage Arena", Launcher = Launcher.Steam, OnDisk = false };
        var sheet = ExportSheets.Library([never, game], at);
        Assert.Equal("Games library", sheet.File);
        Assert.Equal(["Risk of Rain 2", "Steam", "Yes", 11.1, 9, "2026-10-03 11:20", "2026-10-21 11:20", 4.0, @"D:\Steam\Risk of Rain 2"], sheet.Rows[0]);
        Assert.Equal(["Mage Arena", "Steam", "No", null, null, null, null, null, null], sheet.Rows[1]);

        string folder = TestEnvironment.NewFolder("page-sheet");
        DataExport.WriteSheet(sheet, Path.Combine(folder, "lib.txt"), asText: true);
        DataExport.WriteSheet(sheet, Path.Combine(folder, "lib.csv"), asText: false);
        var text = File.ReadAllLines(Path.Combine(folder, "lib.txt"));
        Assert.Equal(["Games library", "Each game, its launcher, play time, last played and size on disk.", "Made 2026-10-23 11:20  ·  2 rows", ""], text.Take(4));
        Assert.StartsWith("Game", text[4]);
        var csv = File.ReadAllLines(Path.Combine(folder, "lib.csv"));
        Assert.Equal("Game,Launcher,Installed,Played (h),Sessions,First played,Last played,Size on disk (GB),Folder", csv[0]);
        Assert.Equal(@"Risk of Rain 2,Steam,Yes,11.1,9,2026-10-03 11:20,2026-10-21 11:20,4,D:\Steam\Risk of Rain 2", csv[1]);
        Assert.Equal(3, csv.Length);

        var free = ExportSheets.SpaceToFree([new SpaceRow("Recycle Bin", "6 deleted items.", 866L << 20, null)], at);
        Assert.Equal(["Recycle Bin", "6 deleted items.", 866.0, null], free.Rows[0]);
        var memory = ExportSheets.MemoryNow([new MemoryNowRow("Google Chrome", "chrome.exe", 14, 797.4, 966)], at);
        Assert.Equal(["Google Chrome", "chrome.exe", 14, 797.0, 966.0], memory.Rows[0]);
    }

    [Fact]
    public void The_plays_of_the_seeded_games_come_with_their_frame_rate_and_ending()
    {
        SharedData.EnsureSeeded();
        using var db = RigsightDb.OpenReader()!;
        var settings = SeedData.QuietSettings();
        var library = GameHistory.Library(db, [], settings);
        Assert.NotEmpty(library);
        var sheet = ExportSheets.Plays(db, library, settings, All.From, All.To, DateTime.Now);
        Assert.NotEmpty(sheet.Rows);
        Assert.All(sheet.Rows, r => Assert.Equal(sheet.Columns.Count, r.Length));
        Assert.All(sheet.Rows, r => Assert.Contains(r[2], library.Select(g => (object)g.Name)));
        // One game picked: only its plays.
        var one = ExportSheets.Plays(db, [library[0]], settings, All.From, All.To, DateTime.Now);
        Assert.All(one.Rows, r => Assert.Equal(library[0].Name, r[2]));
        Assert.True(one.Rows.Count <= sheet.Rows.Count);
        // All time is said as everything, not from the year 2000.
        Assert.StartsWith("Everything up to ", ExportSheets.Plays(db, [library[0]], settings, new DateTime(2000, 1, 1), All.To, DateTime.Now).When);
        // A period with none: none.
        Assert.Empty(ExportSheets.Plays(db, library, settings, new DateTime(2001, 1, 1), new DateTime(2001, 1, 2), DateTime.Now).Rows);
    }

    [Fact]
    public void The_period_is_said_in_words_to_the_minute_or_by_days()
    {
        var now = new DateTime(2026, 10, 23, 12, 3, 30);
        var first = new DateTime(2026, 9, 23);
        Assert.Equal("Friday, 23 October", PageExportViewModel.TextOf((now.Date, now.Date.AddDays(1)), now, first));
        Assert.Equal("17 Oct to 23 Oct · 7 days", PageExportViewModel.TextOf((now.Date.AddDays(-6), now.Date.AddDays(1)), now, first));
        // A month still going on is said up to today; a period from before the history starts, from its first day.
        Assert.Equal("1 Oct to 23 Oct · 23 days", PageExportViewModel.TextOf((new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), now, first));
        Assert.Equal("23 Sep to 23 Oct · 31 days", PageExportViewModel.TextOf((new DateTime(2026, 9, 1), new DateTime(2026, 11, 1)), now, first));
        Assert.Equal("1 Sep to 23 Oct · 53 days", PageExportViewModel.TextOf((new DateTime(2026, 9, 1), new DateTime(2026, 11, 1)), now, null));
        Assert.Equal("Everything since 23 Sep 2026", PageExportViewModel.TextOf((new DateTime(2000, 1, 1), now.Date.AddDays(1)), now, first));
        Assert.Equal("Everything recorded", PageExportViewModel.TextOf((new DateTime(2000, 1, 1), now.Date.AddDays(1)), now, null));
        // The last 24 hours: to the minute, up to now.
        Assert.StartsWith("Thu 22 Oct, 12:03", PageExportViewModel.TextOf((new DateTime(2026, 10, 22, 12, 3, 0), now), now, first));
        Assert.EndsWith(" to now", PageExportViewModel.TextOf((new DateTime(2026, 10, 22, 12, 3, 0), now), now, first));
    }
}
