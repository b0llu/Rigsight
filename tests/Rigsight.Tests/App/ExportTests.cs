using System.IO;
using System.IO.Compression;
using Rigsight.Core.Data;
using Rigsight.Core.Export;
using Rigsight.Core.Reports;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// Exporting: the tables as CSV, the full copy, the files made to be read, and the Export box that picks among them
/// for a period and a folder. Read from the run's seeded history; written into folders of the test's own.
/// </summary>
[Collection("UI")]
public sealed class ExportTests
{
    private static readonly DateTime Today = new(2026, 10, 10);
    private static readonly DateTime First = new(2026, 9, 14);

    // ── The period ──

    [Fact]
    public void A_period_runs_from_its_first_day_to_the_day_after_its_last()
    {
        (DateTime, DateTime) Of(ExportPeriod p, DateTime? first = null) => ExportViewModel.BoundsOf(p, Today, first ?? First, new(2026, 10, 7), new(2026, 10, 1));
        Assert.Equal((Today, Today.AddDays(1)), Of(ExportPeriod.Today));
        Assert.Equal((Today.AddDays(-1), Today), Of(ExportPeriod.Yesterday));
        Assert.Equal((new DateTime(2026, 10, 4), Today.AddDays(1)), Of(ExportPeriod.Last7));
        // Thirty days reach back past the first day recorded: it starts there.
        Assert.Equal((First, Today.AddDays(1)), Of(ExportPeriod.Last30));
        Assert.Equal((new DateTime(2026, 9, 11), Today.AddDays(1)), Of(ExportPeriod.Last30, new DateTime(2026, 1, 1)));
        Assert.Equal((First, Today.AddDays(1)), Of(ExportPeriod.All));
        // The custom days in either order, both in it.
        Assert.Equal((new DateTime(2026, 10, 1), new DateTime(2026, 10, 8)), Of(ExportPeriod.Custom));
        // No history yet: today.
        Assert.Equal((Today, Today.AddDays(1)), ExportViewModel.BoundsOf(ExportPeriod.All, Today, null, Today, Today));
    }

    [Fact]
    public void The_period_is_said_in_words()
    {
        string Of(ExportPeriod p, DateTime? first = null) => ExportViewModel.TextOf(p, Today, first ?? First, new(2026, 10, 1), new(2026, 10, 7));
        Assert.Equal("Saturday, 10 October", Of(ExportPeriod.Today));
        Assert.Equal("Friday, 9 October", Of(ExportPeriod.Yesterday));
        Assert.Equal("4 Oct to 10 Oct · 7 days", Of(ExportPeriod.Last7));
        Assert.Equal("14 Sep to 10 Oct · 27 days, all there is", Of(ExportPeriod.Last30));
        Assert.Equal("11 Sep to 10 Oct · 30 days", Of(ExportPeriod.Last30, new DateTime(2025, 12, 1)));
        Assert.Equal("Everything since 14 Sep · 27 days", Of(ExportPeriod.All));
        Assert.Equal("Everything since 1 Dec 2025 · 314 days", Of(ExportPeriod.All, new DateTime(2025, 12, 1)));
        Assert.Equal("1 Oct to 7 Oct · 7 days", Of(ExportPeriod.Custom));
        Assert.Equal("Wednesday, 7 October", ExportViewModel.TextOf(ExportPeriod.Custom, Today, First, new(2026, 10, 7), new(2026, 10, 7)));
    }

    // ── The tables ──

    [Fact]
    public void A_cell_is_quoted_only_when_it_has_to_be()
    {
        Assert.Equal("", DataExport.Cell(null));
        Assert.Equal("Google Chrome", DataExport.Cell("Google Chrome"));
        Assert.Equal("\"Halo, Infinite\"", DataExport.Cell("Halo, Infinite"));
        Assert.Equal("\"The \"\"best\"\" app\"", DataExport.Cell("The \"best\" app"));
        Assert.Equal("\"two\nlines\"", DataExport.Cell("two\nlines"));
        // Numbers with a point whatever the PC's language, and no noise after it.
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("71.5", DataExport.Cell(71.5));
            Assert.Equal("72", DataExport.Cell(72.0));
            Assert.Equal("1234", DataExport.Cell(1234L));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void Every_table_is_written_with_its_headings_and_as_many_cells_in_each_row()
    {
        SharedData.EnsureSeeded();
        using var db = RigsightDb.OpenReader()!;
        var settings = SeedData.QuietSettings();
        var (from, to) = (DateTime.Today.AddDays(-400), DateTime.Today.AddDays(1));
        foreach (var table in Enum.GetValues<ExportTable>())
        {
            using var writer = new StringWriter();
            int rows = DataExport.Write(db, table, from, to, settings, writer);
            var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            var columns = DataExport.Columns(table);
            Assert.Equal(string.Join(",", columns.Select(c => DataExport.Cell(c.Name))), lines[0]);
            // The seeded history has no multi-line text: a line is a row.
            Assert.Equal(rows + 1, lines.Length);
            Assert.All(lines.Skip(1).Take(200), line => Assert.Equal(columns.Count, Cells(line).Count));
            Assert.True(DataExport.EstimateBytes(db, table, from, to) > 0);
            Assert.EndsWith(".csv", DataExport.FileName(table));
        }
        // What the history is sure to have: minutes with a time and a temperature, and apps by their names.
        using var temps = new StringWriter();
        Assert.True(DataExport.Write(db, ExportTable.Temperatures, from, to, settings, temps) > 100);
        var minute = Cells(temps.ToString().Split(Environment.NewLine)[1]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", minute[0]);
        using var apps = new StringWriter();
        Assert.True(DataExport.Write(db, ExportTable.AppUsage, from, to, settings, apps) > 0);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", Cells(apps.ToString().Split(Environment.NewLine)[1])[0]);
        // A period with nothing in it: the headings alone.
        using var empty = new StringWriter();
        Assert.Equal(0, DataExport.Write(db, ExportTable.Sessions, new DateTime(2001, 1, 1), new DateTime(2001, 1, 2), settings, empty));
    }

    /// <summary>A CSV line's cells (quotes taken off).</summary>
    private static List<string> Cells(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    [Fact]
    public void A_table_as_text_is_lined_up_under_its_headings()
    {
        var lines = DataExport.TextTable(["App", "In use (min)", "Peak CPU (°C)", "Kind"],
        [
            ["Google Chrome", 178.5, 76.0, "Browsing"],
            ["Apex Legends", 94.0, null, ""],
            ["A name\nover two lines", 3.25, 71.5, "Other"],
        ]).ToList();
        Assert.Equal(
        [
            "App                    In use (min)  Peak CPU (°C)  Kind",
            "---------------------  ------------  -------------  --------",
            "Google Chrome                 178.5             76  Browsing",
            "Apex Legends                     94              –  –",
            "A name over two lines          3.25           71.5  Other",
        ], lines);
        // Nothing in it: the headings and their rule.
        Assert.Equal(["App  Kind", "---  ----"], DataExport.TextTable(["App", "Kind"], []));

        SharedData.EnsureSeeded();
        using var db = RigsightDb.OpenReader()!;
        var (from, to) = (DateTime.Today.AddDays(-400), DateTime.Today.AddDays(1));
        foreach (var table in Enum.GetValues<ExportTable>())
        {
            using var csv = new StringWriter();
            using var text = new StringWriter();
            int rows = DataExport.Write(db, table, from, to, SeedData.QuietSettings(), csv);
            Assert.Equal(rows, DataExport.WriteText(db, table, from, to, SeedData.QuietSettings(), text));
            var written = text.ToString().Split(Environment.NewLine);
            // What it is, what it holds and the period on top; then the table.
            Assert.Equal(Path.GetFileNameWithoutExtension(DataExport.FileName(table)), written[0]);
            Assert.Contains(rows == 1 ? "1 row" : $"{rows:N0} rows", written[2]);
            Assert.Equal("", written[3]);
            Assert.StartsWith(DataExport.Columns(table)[0].Name, written[4]);
            Assert.Matches("^[- ]+$", written[5]);
            // Every line of the table is as wide as the rule at most: the columns hold.
            Assert.All(written.Skip(6).Take(200), line => Assert.True(line.Length <= written[5].Length));
        }
    }

    [Fact]
    public void The_report_and_the_crashes_can_be_text_too()
    {
        var day = new DateTime(2026, 10, 9);
        var report = new Report
        {
            Range = ReportRange.Day, From = day, To = day.AddDays(1), HasData = true, ActiveSec = 5 * 3600, OnSec = 6 * 3600, GamingSec = 5400,
            CpuTempPeak = new Peak(76, day.AddHours(14), "Apex Legends"),
            Apps = [new AppStat { Name = "Apex Legends", ActiveSec = 5400, CpuTempMax = 76, MemMax = 4100 }, new AppStat { Name = "Barely", ActiveSec = 20 }],
            Insights = [new Insight("", "You gamed for 1h 30m.")],
        };
        var text = ReadableExport.ReportText(report, "Friday, 9 October 2026", "0.21.0").Split(Environment.NewLine);
        Assert.Equal(["Rigsight report", "Friday, 9 October 2026"], text.Take(2));
        Assert.Contains("THE PERIOD", text);
        Assert.Contains("  Active    5h 00m", text);
        Assert.Contains("  - You gamed for 1h 30m.", text);
        Assert.Contains("APPS YOU USED", text);
        Assert.Contains(text, l => l.StartsWith("  Apex Legends  ", StringComparison.Ordinal) && l.Contains("76°"));
        Assert.DoesNotContain(text, l => l.Contains("Barely"));
        Assert.DoesNotContain(text, l => l.Contains('<')); // text, not a page
        Assert.Contains("Nothing was recorded in this period.", ReadableExport.ReportText(new Report { From = day, To = day.AddDays(1) }, "Empty", "0.21.0"));

        Assert.StartsWith($"Rigsight crashes{Environment.NewLine}Last 7 days: none recorded.", ReadableExport.CrashesText([], "Last 7 days", day, "0.21.0"));
    }

    [Fact]
    public void The_full_copy_holds_the_database_every_table_the_settings_and_a_read_me()
    {
        SharedData.EnsureSeeded();
        string folder = TestEnvironment.NewFolder("export-full");
        string zip = Path.Combine(folder, ExportViewModel.FullFile), settingsFile = Path.Combine(folder, "settings.json");
        File.WriteAllText(settingsFile, "{}");
        using (var db = RigsightDb.OpenReader()!)
            DataExport.WriteFullCopy(db, SeedData.QuietSettings(), zip, settingsFile, "9.9.9");
        Assert.False(File.Exists(zip + ".db.tmp")); // the copy beside it is cleared away
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("rigsight.db", names);
        Assert.Contains("settings.json", names);
        Assert.Contains("Read me.txt", names);
        foreach (var table in Enum.GetValues<ExportTable>()) Assert.Contains(DataExport.FileName(table), names);

        // The database in it opens, and holds the history.
        string copy = Path.Combine(folder, "copy.db");
        archive.GetEntry("rigsight.db")!.ExtractToFile(copy);
        using (var reopened = RigsightDb.OpenReader(copy)!) Assert.NotNull(reopened.FirstDataTime());

        // The read-me names every file and every column.
        string readMe = DataExport.ReadMe(new DateTime(2026, 10, 10, 18, 5, 0), "9.9.9");
        Assert.StartsWith("Rigsight full copy, made 10 October 2026, 18:05 by Rigsight 9.9.9.", readMe);
        foreach (var table in Enum.GetValues<ExportTable>())
        {
            Assert.Contains(DataExport.FileName(table), readMe);
            Assert.All(DataExport.Columns(table), c => Assert.Contains($"  {c.Name}: {c.What}", readMe));
        }
    }

    // ── The files made to be read ──

    [Fact]
    public void The_snapshot_lists_the_pc_and_every_reading_by_part()
    {
        var at = new DateTime(2026, 10, 10, 18, 5, 0);
        string text = ReadableExport.Snapshot(at, "0.20.1", "CPU: Ryzen 7 5700X3D\nGPU: RTX 3080 Ti\n",
            [
                new("AMD Ryzen 7 5700X3D", "Temperatures", "Core (Tctl/Tdie)", "62 °C", "41 °C", "77 °C"),
                new("AMD Ryzen 7 5700X3D", "Load", "CPU Total", "6 %", "1 %", "100 %"),
                new("NVIDIA GeForce RTX 3080 Ti", "Temperatures", "GPU Core", "48 °C", "44 °C", "68 °C"),
            ], ["Samsung SSD 980  ·  43 °C  ·  Good"]);
        var lines = text.Split(Environment.NewLine);
        Assert.Equal("Rigsight PC snapshot", lines[0]);
        Assert.Equal("Saturday, 10 October 2026, 6:05 PM  ·  Rigsight 0.20.1", lines[1]);
        Assert.Contains("  CPU: Ryzen 7 5700X3D", lines);
        Assert.Contains("  Samsung SSD 980  ·  43 °C  ·  Good", lines);
        Assert.Contains("AMD Ryzen 7 5700X3D", lines);
        Assert.Contains("    Core (Tctl/Tdie)  62 °C    41 °C  to  77 °C", lines);
        Assert.Contains("    CPU Total           6 %      1 %  to  100 %", lines);
        Assert.True(Array.IndexOf(lines, "NVIDIA GeForce RTX 3080 Ti") > Array.IndexOf(lines, "AMD Ryzen 7 5700X3D"));
        // No agent, no readings: it says so instead of an empty list.
        Assert.Contains("No sensor readings: the background agent wasn't running.", ReadableExport.Snapshot(at, "0.20.1", "CPU: x", [], []));
    }

    [Fact]
    public void The_report_page_tells_the_period_and_stands_alone()
    {
        var day = new DateTime(2026, 10, 9);
        var report = new Report
        {
            Range = ReportRange.Day, From = day, To = day.AddDays(1), HasData = true, ActiveSec = 5 * 3600, OnSec = 6 * 3600, GamingSec = 5400,
            CpuTempPeak = new Peak(76, day.AddHours(14), "Apex Legends"),
            Apps = [new AppStat { Name = "Tom & Jerry <Game>", ActiveSec = 5400, CpuTempMax = 76, MemMax = 4100 }, new AppStat { Name = "Barely", ActiveSec = 20 }],
            Insights = [new Insight("", "You gamed for 1h 30m.")],
            Temps = [.. Enumerable.Range(0, 30).Select(i => new TempPoint(day.AddHours(10).AddMinutes(i), 50 + i % 5, 45 + i % 3))],
        };
        string html = ReadableExport.Report(report, "Friday, 9 October 2026", "0.20.1");
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<title>Friday, 9 October 2026</title>", html);
        Assert.Contains("You gamed for 1h 30m.", html);
        Assert.Contains("Tom &amp; Jerry &lt;Game&gt;", html); // names can't break the page
        Assert.DoesNotContain("Barely", html); // under a minute isn't listed
        Assert.Contains("<svg", html);
        Assert.DoesNotContain("http", html.Replace("http-equiv", "")); // nothing fetched from anywhere
        Assert.Contains("Made by Rigsight 0.20.1", html);

        // Too little to draw a line through: no drawing. Nothing recorded at all: it says so.
        Assert.Null(ReadableExport.TempChart([new TempPoint(day, 50, 40), new TempPoint(day.AddMinutes(1), 51, 41)]));
        Assert.Contains("Nothing was recorded in this period.", ReadableExport.Report(new Report { From = day, To = day.AddDays(1) }, "Empty", "0.20.1"));
    }

    // ── The box ──

    private static (ExportViewModel Vm, SettingsModel Settings, List<string> Shown) Box(string folder)
    {
        SharedData.EnsureSeeded();
        var (settings, live) = Kit.Greeted();
        var shown = new List<string>();
        var vm = Ui.Run(() => new ExportViewModel(new ReportService(settings), settings, live) { PickFolder = _ => folder, ShowFolder = shown.Add });
        return (vm, settings, shown);
    }

    [Fact]
    public void The_box_says_what_it_comes_to_and_follows_the_ticks()
    {
        var (vm, _, _) = Box(TestEnvironment.NewFolder("export-says"));
        Ui.Run(() =>
        {
            Assert.Equal(["PC snapshot", "App usage", "Temperatures, load and power"], vm.ToShare.Concat(vm.Tables).Concat(vm.Everything).Where(i => i.IsOn).Select(i => i.Name));
            Assert.Equal(ExportPeriod.Last7, vm.Period);
            vm.Open();
            Assert.True(vm.IsOpen);
        });
        Assert.True(Ui.WaitFor(() => vm.Summary.StartsWith("3 files, about ", StringComparison.Ordinal)));
        Ui.Run(() =>
        {
            Assert.True(vm.CanExport);
            foreach (var item in vm.ToShare.Concat(vm.Tables)) item.IsOn = false;
            Assert.Equal("Nothing picked yet.", vm.Summary);
            Assert.False(vm.CanExport);
            vm.Everything[0].IsOn = true;
            Assert.True(vm.CanExport);
            Assert.StartsWith("1 file", vm.Summary);

            // The calendar's two days, clicked in either order, make it a custom period.
            var changes = Kit.Changes(vm, () => vm.PickCustom(DateTime.Today, DateTime.Today.AddDays(-2)));
            Assert.Equal((ExportPeriod.Custom, DateTime.Today.AddDays(-2), DateTime.Today), (vm.Period, vm.CustomFrom, vm.CustomTo));
            Assert.True(vm.IsCustom);
            Assert.Contains(nameof(ExportViewModel.PeriodText), changes);
            Assert.EndsWith("· 3 days", vm.PeriodText);

            vm.CloseCommand.Execute(null);
            Assert.False(vm.IsOpen);
        });
    }

    [Fact]
    public void The_calendar_takes_a_first_day_then_a_last()
    {
        var (vm, _, _) = Box(TestEnvironment.NewFolder("export-calendar"));
        Ui.TakeProblems();
        var window = Ui.Run(() =>
        {
            vm.Open();
            var w = new System.Windows.Window
            {
                Content = new Views.ExportBox { DataContext = vm }, Left = -32000, Top = -32000, Width = 900, Height = 700,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            };
            w.Show();
            return w;
        });
        try
        {
            Ui.Pump(200);
            Ui.Run(() =>
            {
                var box = (Views.ExportBox)window.Content;
                var cal = (System.Windows.Controls.Calendar)box.FindName("Cal");
                var popup = (System.Windows.Controls.Primitives.Popup)box.FindName("RangePopup");
                var hint = (System.Windows.Controls.TextBlock)box.FindName("RangeHint");
                vm.FirstDay = DateTime.Today.AddDays(-20);
                box.OpenCalendar();
                Assert.True(popup.IsOpen);
                Assert.Equal("Click the first day, then the last", hint.Text);
                // It opens on the custom days it has, and offers no day without history.
                Assert.Equal([vm.CustomFrom, vm.CustomTo], new[] { cal.SelectedDates.Min(), cal.SelectedDates.Max() });
                Assert.True(cal.BlackoutDates.Contains(DateTime.Today.AddDays(-21)) || DateTime.Today.AddDays(-21).Month != DateTime.Today.AddDays(-20).Month);
                Assert.Equal(ExportPeriod.Last7, vm.Period); // opening it changes nothing yet

                // A click leaves one day picked: the first. The next closes the period, whichever way round.
                cal.SelectedDates.Clear();
                cal.SelectedDates.Add(DateTime.Today.AddDays(-2));
                Assert.Equal("Now click the last day", hint.Text);
                Assert.True(popup.IsOpen);
                Assert.Equal(ExportPeriod.Last7, vm.Period);
                cal.SelectedDates.Clear();
                cal.SelectedDates.Add(DateTime.Today.AddDays(-5));
                Assert.False(popup.IsOpen);
                Assert.Equal((ExportPeriod.Custom, DateTime.Today.AddDays(-5), DateTime.Today.AddDays(-2)), (vm.Period, vm.CustomFrom, vm.CustomTo));

                // Opened again it shows that period; days dragged over are a period at once.
                box.OpenCalendar();
                Assert.Equal(4, cal.SelectedDates.Count);
                cal.SelectedDates.Clear();
                cal.SelectedDates.AddRange(DateTime.Today.AddDays(-1), DateTime.Today);
                Assert.False(popup.IsOpen);
                Assert.Equal((DateTime.Today.AddDays(-1), DateTime.Today), (vm.CustomFrom, vm.CustomTo));
            });
            Ui.AssertNoProblems("the Export box");
        }
        finally
        {
            Ui.Run(() => { vm.CloseCommand.Execute(null); window.Close(); });
        }
    }

    [Fact]
    public void Exporting_writes_each_picked_file_into_the_days_folder_and_shows_it()
    {
        string parent = TestEnvironment.NewFolder("export-run");
        var (vm, settings, shown) = Box(parent);
        Ui.Run(() =>
        {
            // The folder is the one picked, remembered, with the export's own folder made in it.
            vm.ChangeFolderCommand.Execute(null);
            Assert.Equal(parent, settings.Current.ExportFolder);
            Assert.Equal(Path.Combine(parent, $"Rigsight export {DateTime.Today:yyyy-MM-dd}"), vm.Folder);
            vm.Open();
            foreach (var item in vm.ToShare.Concat(vm.Tables).Concat(vm.Everything)) item.IsOn = true;
            vm.Period = ExportPeriod.All;
            // A new install writes text; picked once, the spreadsheet files are remembered.
            Assert.True(vm.AsText);
            Assert.Equal("App usage.txt", vm.FileOf(vm.Tables[0]));
            vm.AsCsv = true;
            Assert.True(settings.Current.ExportAsCsv);
            Assert.Equal(("App usage.csv", "Report.html", "PC snapshot.txt"), (vm.FileOf(vm.Tables[0]), vm.FileOf(vm.ToShare[1]), vm.FileOf(vm.ToShare[0])));
        });
        Kit.Wait(() => vm.ExportCommand.ExecuteAsync(null));
        Ui.Run(() =>
        {
            Assert.Null(vm.Error);
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsOpen);
            Assert.Equal([vm.Folder], shown);
        });
        var files = Directory.GetFiles(vm.Folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(new[]
        {
            "App usage.csv", "Crashes.csv", "Crashes.html", "Drive space.csv", "Fans.csv", "Internet.csv", "Memory.csv", "PC snapshot.txt", "Report.html",
            "Rigsight full copy.zip", "Sessions.csv", "Temperatures, load and power.csv", "Timeline changes.csv",
        }.Order(), files);
        Assert.StartsWith("Rigsight PC snapshot", File.ReadAllText(Path.Combine(vm.Folder, "PC snapshot.txt")));
        Assert.StartsWith("<!doctype html>", File.ReadAllText(Path.Combine(vm.Folder, "Report.html")));
        Assert.StartsWith("<!doctype html>", File.ReadAllText(Path.Combine(vm.Folder, "Crashes.html")));
        // Excel reads the degree sign and names right only with the mark at the file's start.
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(Path.Combine(vm.Folder, "App usage.csv")).Take(3).Select(b => (int)b));
        Assert.True(new FileInfo(Path.Combine(vm.Folder, "Temperatures, load and power.csv")).Length > 1000);

        // The same as text files: every one a .txt, laid out, and none of them a page or a list of commas.
        foreach (var file in Directory.GetFiles(vm.Folder)) File.Delete(file);
        Ui.Run(() =>
        {
            vm.AsText = true;
            vm.Everything[0].IsOn = false;
            vm.Open();
        });
        Kit.Wait(() => vm.ExportCommand.ExecuteAsync(null));
        Ui.Run(() => Assert.Null(vm.Error));
        var texts = Directory.GetFiles(vm.Folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(new[]
        {
            "App usage.txt", "Crashes.txt", "Crashes with their context.txt", "Drive space.txt", "Fans.txt", "Internet.txt", "Memory.txt", "PC snapshot.txt", "Report.txt",
            "Sessions.txt", "Temperatures, load and power.txt", "Timeline changes.txt",
        }.Order(), texts);
        var usage = File.ReadAllLines(Path.Combine(vm.Folder, "App usage.txt"));
        Assert.Equal("App usage", usage[0]);
        Assert.StartsWith("Day ", usage[4]);
        Assert.Matches("^[- ]+$", usage[5]);
        Assert.StartsWith("Rigsight report", File.ReadAllText(Path.Combine(vm.Folder, "Report.txt")));
        Assert.StartsWith("Rigsight crashes", File.ReadAllText(Path.Combine(vm.Folder, "Crashes with their context.txt")));
    }

    [Fact]
    public void A_folder_that_cant_be_written_is_said_in_the_box()
    {
        // A file where the export's folder should be made.
        string parent = TestEnvironment.NewFolder("export-fails");
        var (vm, _, shown) = Box(parent);
        Ui.Run(() =>
        {
            vm.ChangeFolderCommand.Execute(null);
            File.WriteAllText(vm.Folder, "in the way");
            vm.Open();
        });
        Kit.Wait(() => vm.ExportCommand.ExecuteAsync(null));
        Ui.Run(() =>
        {
            Assert.StartsWith("Couldn't save there: ", vm.Error);
            Assert.True(vm.IsOpen);
            Assert.False(vm.IsBusy);
            Assert.Empty(shown);
        });
    }
}
