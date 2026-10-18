using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Export;
using Rigsight.Core.Reports;
using Rigsight.Core.Stability;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>The period an export covers.</summary>
public enum ExportPeriod { Today, Yesterday, Last7, Last30, All, Custom }

/// <summary>One thing that can be exported: a tick box in the Export box, and the file it makes.</summary>
public sealed partial class ExportItem(string key, string name, string file, ExportTable? table = null) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public string File { get; } = file;
    /// <summary>The table of history it is, for the ones that are.</summary>
    public ExportTable? Table { get; } = table;
    [ObservableProperty] private bool _isOn;
}

/// <summary>
/// The Export box over the window (Settings → History → Export…): what to export, each its own file, for which period,
/// and into which folder. Nothing leaves the PC: the files are written and their folder is opened.
/// </summary>
public sealed partial class ExportViewModel : ObservableObject
{
    public const string SnapshotKey = "snapshot", ReportKey = "report", CrashesKey = "crash-stories", FullKey = "full";
    public const string FullFile = "RigSight full copy.zip";

    private readonly ReportService _reports;
    private readonly SettingsModel _settings;
    private readonly LiveData _live;

    public ExportViewModel(ReportService reports, SettingsModel settings, LiveData live)
    {
        (_reports, _settings, _live) = (reports, settings, live);
        ToShare =
        [
            new(SnapshotKey, "PC snapshot", ReadableExport.SnapshotFile) { IsOn = true },
            new(ReportKey, "Report for the period", ReadableExport.ReportFile),
            new(CrashesKey, "Crashes with their context", ReadableExport.CrashesFile),
        ];
        Tables =
        [
            new("apps", "App usage", DataExport.FileName(ExportTable.AppUsage), ExportTable.AppUsage) { IsOn = true },
            new("sessions", "Sessions", DataExport.FileName(ExportTable.Sessions), ExportTable.Sessions),
            new("temps", "Temperatures, load and power", DataExport.FileName(ExportTable.Temperatures), ExportTable.Temperatures) { IsOn = true },
            new("fans", "Fans", DataExport.FileName(ExportTable.Fans), ExportTable.Fans),
            new("internet", "Internet", DataExport.FileName(ExportTable.Internet), ExportTable.Internet),
            new("memory", "Memory", DataExport.FileName(ExportTable.Memory), ExportTable.Memory),
            new("drives", "Drive space", DataExport.FileName(ExportTable.DriveSpace), ExportTable.DriveSpace),
            new("crashes", "Crashes", DataExport.FileName(ExportTable.Crashes), ExportTable.Crashes),
            new("timeline", "Timeline changes", DataExport.FileName(ExportTable.Timeline), ExportTable.Timeline),
        ];
        Everything = [new(FullKey, "Full copy with the database", FullFile)];
        foreach (var item in All) item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ExportItem.IsOn)) Picked(); };
    }

    public IReadOnlyList<ExportItem> ToShare { get; }
    public IReadOnlyList<ExportItem> Tables { get; }
    public IReadOnlyList<ExportItem> Everything { get; }
    private IEnumerable<ExportItem> All => ToShare.Concat(Tables).Concat(Everything);
    private List<ExportItem> On => [.. All.Where(i => i.IsOn)];

    [ObservableProperty] private bool _isOpen;

    /// <summary>Writing the files: the button turns, and nothing can be changed meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport), nameof(CanChange))]
    private bool _isBusy;

    public bool CanChange => !IsBusy;

    /// <summary>What went wrong with the last export, said in the box (null: nothing did).</summary>
    [ObservableProperty] private string? _error;

    // ── The period ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText), nameof(IsToday), nameof(IsYesterday), nameof(IsLast7), nameof(IsLast30), nameof(IsAll), nameof(IsCustom))]
    private ExportPeriod _period = ExportPeriod.Last7;

    /// <summary>The custom period's first and last day (both in it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText))]
    private DateTime _customFrom = DateTime.Today.AddDays(-6);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText))]
    private DateTime _customTo = DateTime.Today;

    /// <summary>The first day there is history for (the calendar offers no day before it); null with none yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText))]
    private DateTime? _firstDay;

    public bool IsToday { get => Period == ExportPeriod.Today; set { if (value) Period = ExportPeriod.Today; } }
    public bool IsYesterday { get => Period == ExportPeriod.Yesterday; set { if (value) Period = ExportPeriod.Yesterday; } }
    public bool IsLast7 { get => Period == ExportPeriod.Last7; set { if (value) Period = ExportPeriod.Last7; } }
    public bool IsLast30 { get => Period == ExportPeriod.Last30; set { if (value) Period = ExportPeriod.Last30; } }
    public bool IsAll { get => Period == ExportPeriod.All; set { if (value) Period = ExportPeriod.All; } }
    public bool IsCustom { get => Period == ExportPeriod.Custom; set { if (value) Period = ExportPeriod.Custom; } }

    /// <summary>The period's first day and the day after its last, as the history is asked for it.</summary>
    public (DateTime From, DateTime To) Bounds => BoundsOf(Period, DateTime.Today, FirstDay, CustomFrom, CustomTo);

    internal static (DateTime From, DateTime To) BoundsOf(ExportPeriod period, DateTime today, DateTime? firstDay, DateTime customFrom, DateTime customTo)
    {
        var (from, to) = period switch
        {
            ExportPeriod.Today => (today, today),
            ExportPeriod.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            ExportPeriod.Last7 => (today.AddDays(-6), today),
            ExportPeriod.Last30 => (today.AddDays(-29), today),
            ExportPeriod.All => (firstDay ?? today, today),
            _ => customFrom <= customTo ? (customFrom.Date, customTo.Date) : (customTo.Date, customFrom.Date),
        };
        // A period reaching back past the first day recorded starts there: that is all there is.
        if (period is ExportPeriod.Last7 or ExportPeriod.Last30 && firstDay is { } first && first > from && first <= to) from = first;
        return (from, to.AddDays(1));
    }

    /// <summary>The period in words, under its name: "Saturday, 10 October", "4 Oct to 10 Oct · 7 days".</summary>
    public string PeriodText => TextOf(Period, DateTime.Today, FirstDay, CustomFrom, CustomTo);

    internal static string TextOf(ExportPeriod period, DateTime today, DateTime? firstDay, DateTime customFrom, DateTime customTo)
    {
        var (from, end) = BoundsOf(period, today, firstDay, customFrom, customTo);
        var to = end.AddDays(-1);
        int days = (int)(end - from).TotalDays;
        var culture = CultureInfo.CurrentCulture;
        if (days == 1) return from.ToString("dddd, d MMMM", culture);
        string Short(DateTime d) => d.ToString(from.Year == to.Year ? "d MMM" : "d MMM yyyy", culture);
        if (period == ExportPeriod.All) return $"Everything since {Short(from)} · {days} days";
        bool short30 = period == ExportPeriod.Last30 && days < 30, short7 = period == ExportPeriod.Last7 && days < 7;
        return $"{Short(from)} to {Short(to)} · {days} days{(short30 || short7 ? ", all there is" : "")}";
    }

    partial void OnPeriodChanged(ExportPeriod value)
    {
        Error = null;
        Estimate();
    }
    partial void OnCustomFromChanged(DateTime value) => Estimate();
    partial void OnCustomToChanged(DateTime value) => Estimate();

    /// <summary>The custom period picked in the calendar: its two days, in either order.</summary>
    public void PickCustom(DateTime a, DateTime b)
    {
        (CustomFrom, CustomTo) = a <= b ? (a.Date, b.Date) : (b.Date, a.Date);
        Period = ExportPeriod.Custom;
    }

    // ── Text, or files for a spreadsheet ──

    /// <summary>
    /// Every file as text laid out to be read as it is (the choice a new install starts on), or the tables as CSV for
    /// a spreadsheet with the report and the crashes as pages for a browser. The full copy is the same either way.
    /// </summary>
    public bool AsText
    {
        get => !_settings.Current.ExportAsCsv;
        set
        {
            if (value == AsText) return;
            _settings.Update(s => s.ExportAsCsv = !value);
            OnPropertyChanged(nameof(AsText));
            OnPropertyChanged(nameof(AsCsv));
            OnPropertyChanged(nameof(FormatText));
            Estimate();
        }
    }

    public bool AsCsv { get => !AsText; set { if (value) AsText = false; } }

    public string FormatText => AsText ? "Laid out to read in Notepad or paste into a post" : "Tables for Excel, the report and crashes as web pages";

    /// <summary>The file an item makes in the format picked ("App usage.txt", "Report.html").</summary>
    public string FileOf(ExportItem item) => FileOf(item, AsText);

    internal static string FileOf(ExportItem item, bool asText) => item.Key switch
    {
        ReportKey => asText ? ReadableExport.ReportTextFile : ReadableExport.ReportFile,
        CrashesKey => asText ? ReadableExport.CrashesTextFile : ReadableExport.CrashesFile,
        _ when item.Table is not null => Path.ChangeExtension(item.File, asText ? ".txt" : ".csv"),
        _ => item.File,
    };

    // ── The folder ──

    /// <summary>The folder the export's own folder is made in: the one picked last, or Documents.</summary>
    public string Parent => _settings.Current.ExportFolder is { Length: > 0 } picked && Directory.Exists(picked)
        ? picked : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>The export's own folder, named for the day ("…\Rigsight export 2026-10-10").</summary>
    public string Folder => Path.Combine(Parent, $"RigSight export {DateTime.Today:yyyy-MM-dd}");

    /// <summary>Asks for a folder (null: none picked). Tests give their own.</summary>
    internal Func<string, string?> PickFolder { get; set; } = start =>
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save the export in", InitialDirectory = start };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    };

    /// <summary>Shows a folder in File Explorer. Tests give their own.</summary>
    internal Action<string> ShowFolder { get; set; } = folder =>
    {
        try { Process.Start(new ProcessStartInfo(RigsightPaths.Explorer, $"\"{folder}\"") { UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { Log.Error("export", ex); }
    };

    [RelayCommand]
    private void ChangeFolder()
    {
        if (PickFolder(Parent) is not { Length: > 0 } picked) return;
        _settings.Update(s => s.ExportFolder = picked);
        OnPropertyChanged(nameof(Parent));
        OnPropertyChanged(nameof(Folder));
    }

    // ── What it comes to ──

    /// <summary>"3 files, about 1.2 MB", or that nothing is picked.</summary>
    [ObservableProperty] private string _summary = "";

    public bool CanExport => !IsBusy && All.Any(i => i.IsOn);

    private void Picked()
    {
        // What went wrong last time was about the choice before this one.
        Error = null;
        OnPropertyChanged(nameof(CanExport));
        Estimate();
    }

    private int _estimate;

    /// <summary>Says how many files and about how big, from how many rows each table has in the period.</summary>
    private async void Estimate()
    {
        if (!IsOpen) return;
        var on = On;
        if (on.Count == 0)
        {
            Summary = "Nothing picked yet.";
            return;
        }
        string files = on.Count == 1 ? "1 file" : $"{on.Count} files";
        Summary = files;
        int id = ++_estimate;
        var (from, to) = Bounds;
        int sensors = _live.AllSensors.Count;
        bool asText = AsText;
        long bytes = await _reports.QueryAsync((db, _) => on.Sum(item => SizeOf(db, item, from, to, sensors, asText)));
        if (id == _estimate && bytes > 0) Summary = $"{files}, about {Units.Data(bytes)}";
    }

    private static long SizeOf(RigsightDb db, ExportItem item, DateTime from, DateTime to, int sensors, bool asText) => item.Key switch
    {
        SnapshotKey => 600 + sensors * 72L,
        ReportKey => asText ? 3_500 : 9_000 + db.CountRows("system_minute", "ts", TimeUtil.ToUnix(from), TimeUtil.ToUnix(to)) / 4 + 2_000,
        CrashesKey => (asText ? 300 : 4_500) + db.CountRows("crashes", "ts", TimeUtil.ToUnix(from), TimeUtil.ToUnix(to)) * (asText ? 600 : 900),
        // The database packs to about a third, and the tables beside it to about a fifth of the same again.
        FullKey => File.Exists(RigsightPaths.Database) ? new FileInfo(RigsightPaths.Database).Length * 55 / 100 : 0,
        // Lined-up columns are padded: about half as much again as the same table with commas.
        _ => DataExport.EstimateBytes(db, item.Table!.Value, from, to) * (asText ? 3 : 2) / 2,
    };

    // ── Opening, exporting ──

    public async void Open()
    {
        Error = null;
        IsOpen = true;
        OnPropertyChanged(nameof(Folder));
        FirstDay = await _reports.FirstDayAsync() ?? FirstDay;
        Estimate();
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsBusy) IsOpen = false;
    }

    private static readonly string Version = typeof(ExportViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    [RelayCommand]
    private async Task Export()
    {
        if (!CanExport) return;
        var on = On;
        var (from, to) = Bounds;
        string folder = Folder, period = PeriodText;
        bool asText = AsText;
        IsBusy = true;
        Error = null;
        try
        {
            Directory.CreateDirectory(folder);
            // What only the window knows (the readings now) is put together here; the rest is read off its thread.
            if (on.Any(i => i.Key == SnapshotKey)) await File.WriteAllTextAsync(Path.Combine(folder, ReadableExport.SnapshotFile), SnapshotNow(), new UTF8Encoding(true));
            if (on.FirstOrDefault(i => i.Key == ReportKey) is { } reportItem)
            {
                var report = (to - from).TotalDays <= 1 ? await _reports.BuildAsync(ReportRange.Day, from) : await _reports.BuildCustomAsync(from, to);
                report ??= new Report { From = from, To = to };
                string title = (to - from).TotalDays <= 1 ? from.ToString("dddd, d MMMM yyyy") : $"{from:d MMM yyyy} to {to.AddDays(-1):d MMM yyyy}";
                await File.WriteAllTextAsync(Path.Combine(folder, FileOf(reportItem, asText)),
                    asText ? ReadableExport.ReportText(report, title, Version) : ReadableExport.Report(report, title, Version), new UTF8Encoding(true));
            }
            string? failed = await _reports.QueryAsync((db, s) =>
            {
                try
                {
                    foreach (var item in on)
                    {
                        string path = Path.Combine(folder, FileOf(item, asText));
                        if (item.Table is { } table) DataExport.WriteFile(db, table, from, to, s, path, asText);
                        else if (item.Key == CrashesKey)
                        {
                            var stories = Stories(db, s, from, to);
                            File.WriteAllText(path, asText ? ReadableExport.CrashesText(stories, period, DateTime.Now, Version) : ReadableExport.Crashes(stories, period, DateTime.Now, Version), new UTF8Encoding(true));
                        }
                        else if (item.Key == FullKey) DataExport.WriteFullCopy(db, s, Path.Combine(folder, item.File), RigsightPaths.SettingsFile, Version);
                    }
                    return "";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    Log.Error("export", ex);
                    return ex.Message;
                }
            });
            if (failed is null) Error = "The history couldn't be opened. Try again in a moment.";
            else if (failed.Length > 0) Error = "Couldn't finish: " + failed;
            else
            {
                Log.Write("export", $"{on.Count} file(s) ({string.Join(", ", on.Select(i => i.Key))}) as {(asText ? "text" : "CSV")} for {period}");
                ShowFolder(folder);
                IsOpen = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("export", ex);
            Error = "Couldn't save there: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>This PC as it is now: every reading with its lowest and highest since the agent started.</summary>
    internal string SnapshotNow()
    {
        var readings = _live.AllSensors.Select(x => new SnapshotReading(x.HardwareName, KindName(x.Kind), x.DisplayName, x.FormattedValue, x.FormattedMin, x.FormattedMax));
        var drives = _live.Drives.Select(d => string.Join("  ·  ", new[] { d.Name, d.Temperature?.FormattedValue, d.HealthText }.Where(t => !string.IsNullOrWhiteSpace(t))));
        return ReadableExport.Snapshot(DateTime.Now, Version, PcInfo.Text, readings, drives);
    }

    private static string KindName(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "Temperatures",
        SensorKind.Voltage => "Voltages",
        SensorKind.Fan => "Fan speeds",
        SensorKind.Load => "Load",
        SensorKind.Clock => "Clock speeds",
        SensorKind.Power => "Power",
        SensorKind.Control => "Fan control",
        SensorKind.Data or SensorKind.SmallData => "Data",
        SensorKind.Throughput => "Throughput",
        SensorKind.Level => "Levels",
        _ => kind.ToString(),
    };

    /// <summary>The changes that could have to do with a crash: the PC's own, not an app updating itself.</summary>
    private static readonly ChangeKind[] Telling = [ChangeKind.Driver, ChangeKind.WindowsUpdate, ChangeKind.Windows, ChangeKind.Hardware, ChangeKind.Firmware, ChangeKind.Setting, ChangeKind.Fans];

    private static List<CrashStory> Stories(RigsightDb db, Core.Settings.RigsightSettings s, DateTime from, DateTime to)
    {
        long f = TimeUtil.ToUnix(from), t = TimeUtil.ToUnix(to);
        var apps = db.LoadApps();
        var byId = apps.ToDictionary(a => a.Id);
        var byExe = apps.GroupBy(a => a.Exe, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        string Name(AppRow a) => s.AppNames.TryGetValue(a.Exe, out var alias) ? alias : Core.Apps.AppCatalog.KnownName(a.Exe) ?? a.Name;
        var context = db.GetCrashContext(f, t);
        var changes = db.GetChanges(f - 7 * 86400, t).Where(c => Telling.Contains(c.Kind)).ToList();
        var crashes = db.GetCrashes(f, t).Where(e => !s.IsCrashMuted(e.AppExe)).ToList();
        var findings = CrashCases.Of(db, crashes, s);
        Dictionary<DateTime, Core.Reports.ChangeResult> results = changes.Any(Core.Reports.ChangeEffects.IsMajor) ? Core.Reports.ChangeEffects.Read(db, s) : [];
        return [.. crashes.Select(e =>
        {
            var at = TimeUtil.FromUnix(e.Ts);
            context.TryGetValue(e.Id, out var ctx);
            return new CrashStory(e,
                e.AppExe.Length == 0 ? null : byExe.TryGetValue(e.AppExe, out var row) ? Name(row) : null,
                ctx,
                ctx?.FrontApp is long front && byId.TryGetValue(front, out var app) ? Name(app) : null,
                [.. changes.Where(c => c.Time <= at && c.Time >= at.AddDays(-7)).TakeLast(6)])
            {
                Finding = findings.GetValueOrDefault(e.Id),
                Results = results,
                InLastMinutes = e.Kind is CrashKind.AppCrash or CrashKind.AppHang or CrashKind.GpuDriverReset && db.GetCloseUp(e.Ts, e.AppExe) is { } close
                    ? [.. CloseUpWords.Find(close.Points, e.Ts, id => byId.TryGetValue(id, out var a) ? Name(a) : "An app",
                        e.AppExe.Length > 0 && byExe.TryGetValue(e.AppExe, out var crashed) ? crashed.Id : null).Select(x => x.Text)]
                    : [],
            };
        })];
    }
}
