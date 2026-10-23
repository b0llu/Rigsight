using System.Diagnostics;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Export;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>What one thing in a page's Export box is made from.</summary>
public enum PageItemKind { Table, Report, Stories, Snapshot, Sheet }

/// <summary>The period, filter and moment an export is made for.</summary>
public sealed record PageExportRun(DateTime From, DateTime To, ExportFilter Filter, DateTime Now);

/// <summary>One thing a page's Export box can save, in the page's own words, with a line on what the file holds.</summary>
public sealed partial class PageExportItem(string name, string about, PageItemKind kind, bool on) : ObservableObject
{
    public string Name { get; } = name;
    public string About { get; } = about;
    public PageItemKind Kind { get; } = kind;
    /// <summary>The table of history it is, for a <see cref="PageItemKind.Table"/>.</summary>
    public ExportTable? Table { get; init; }
    /// <summary>
    /// For a <see cref="PageItemKind.Sheet"/>: called on the window's thread (what the page shows is read there), it
    /// gives what makes the file from the history, run off it.
    /// </summary>
    public Func<PageExportRun, Func<RigsightDb, RigsightSettings, ExportSheet>>? Sheet { get; init; }
    /// <summary>What's on the page now (a list, the readings): the period doesn't change it.</summary>
    public bool IsNow { get; init; }
    /// <summary>About how big, in bytes per day of the period (a sheet's own guess).</summary>
    public long BytesPerDay { get; init; } = 2_000;

    [ObservableProperty] private bool _isOn = on;
    /// <summary>"about 30 KB", or "right now" for what's on the page now.</summary>
    [ObservableProperty] private string _sizeText = "";
}

/// <summary>One of a page's own periods in its Export box ("Day", "Last 24 hours"); no bounds: Custom.</summary>
public sealed partial class PeriodChoice(string label, Func<(DateTime From, DateTime To)>? bounds) : ObservableObject
{
    public string Label { get; } = label;
    public Func<(DateTime From, DateTime To)>? Bounds { get; } = bounds;
    public bool IsCustom => Bounds is null;
    [ObservableProperty] private bool _isOn;
}

/// <summary>One choice in a scope: an app, "All", a reading, a drive.</summary>
public sealed partial class ScopeOption(string label, object? value, bool on, string? dot = null) : ObservableObject
{
    public string Label { get; } = label;
    public object? Value { get; } = value;
    /// <summary>The legend colour's resource key, for a reading.</summary>
    public string? Dot { get; } = dot;
    public bool HasDot => Dot is not null;
    [ObservableProperty] private bool _isOn = on;
}

/// <summary>
/// What a page narrows its export to, the way the page is narrowed: one of a few (the app picked or all of them, a
/// crash filter), or any of several (readings, drives). The line beside its name says what is picked now.
/// </summary>
public sealed partial class ExportScope : ObservableObject
{
    private readonly Func<ExportScope, string> _words;

    public ExportScope(string name, bool isMulti, IReadOnlyList<ScopeOption> options, Func<ExportScope, string> words)
    {
        (Name, IsMulti, Options, _words) = (name, isMulti, options, words);
        GroupName = "Scope" + Guid.NewGuid().ToString("N")[..8];
        foreach (var o in options) o.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ScopeOption.IsOn)) Changed(); };
        _wordsText = words(this);
    }

    public string Name { get; }
    public bool IsMulti { get; }
    public bool IsSingle => !IsMulti;
    /// <summary>More than one choice: "Pick all" is worth having.</summary>
    public bool HasMany => Options.Count > 1;
    public IReadOnlyList<ScopeOption> Options { get; }
    /// <summary>The radio group of a one-of scope, its own in the window.</summary>
    public string GroupName { get; }

    [ObservableProperty] private string _wordsText;
    [ObservableProperty] private string _allText = "";

    public IEnumerable<object?> Picked => Options.Where(o => o.IsOn).Select(o => o.Value);
    public bool AllPicked => Options.All(o => o.IsOn);

    /// <summary>Said when anything in it is picked or left out (the box reads the new filter).</summary>
    public event Action? Picks;

    private void Changed()
    {
        WordsText = _words(this);
        AllText = AllPicked ? Lang.T("Clear") : Lang.T("Pick all");
        Picks?.Invoke();
    }

    [RelayCommand]
    private void PickAll()
    {
        bool on = !AllPicked;
        foreach (var o in Options) o.IsOn = on;
    }

    internal void Ready() => AllText = AllPicked ? Lang.T("Clear") : Lang.T("Pick all");
}

/// <summary>Everything a page's Export box is opened with (see <see cref="PageExports"/>).</summary>
/// <param name="Picked">The period the page shows, of <paramref name="Periods"/>.</param>
/// <param name="Custom">The page's own custom period, when it has one (to the hour; the end not in it).</param>
/// <param name="FromInstall">The page's history starts when RigSight did (its periods are said from that day); off for crashes and
/// changes, which Windows kept from before.</param>
public sealed record PageExportSpec(string Title, string Sub, IReadOnlyList<PageExportItem> Items, IReadOnlyList<PeriodChoice> Periods, int Picked,
    (DateTime From, DateTime To)? Custom, IReadOnlyList<ExportScope> Scopes, Func<(DateTime From, DateTime To), ExportFilter> Filter, bool FromInstall = true);

/// <summary>
/// A page's own Export box: only what that page shows, in its words, for the period it shows and narrowed the way it
/// is (the app picked, the crash filter, the drives, the readings). The folder and the format are the ones the full
/// Export box uses. Nothing leaves the PC: the files are written and their folder is opened.
/// </summary>
public sealed partial class PageExportViewModel(ReportService reports, SettingsModel settings, LiveData live) : ObservableObject, Views.IDayRange
{
    /// <summary>Past this many things, the list scrolls in a box of its own height and says how many are picked.</summary>
    public const int ListMax = 4;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private IReadOnlyList<PageExportItem> _items = [];
    [ObservableProperty] private IReadOnlyList<PeriodChoice> _periods = [];
    [ObservableProperty] private IReadOnlyList<ExportScope> _scopes = [];
    [ObservableProperty] private string _pickedText = "";
    [ObservableProperty] private string _pickAllText = "";

    public bool ManyItems => Items.Count > ListMax;

    private Func<(DateTime From, DateTime To), ExportFilter> _filter = _ => ExportFilter.None;
    private bool _fromInstall = true;

    /// <summary>Writing the files: the button turns, and nothing can be changed meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport), nameof(CanChange))]
    private bool _isBusy;

    public bool CanChange => !IsBusy;

    /// <summary>What went wrong with the last export, said in the box (null: nothing did).</summary>
    [ObservableProperty] private string? _error;

    [ObservableProperty] private string _summary = "";

    public bool CanExport => !IsBusy && Items.Any(i => i.IsOn);

    // ── Opening ──

    public async void Open(PageExportSpec spec)
    {
        Title = spec.Title;
        Sub = spec.Sub;
        foreach (var item in spec.Items) item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PageExportItem.IsOn)) Picked(); };
        Items = spec.Items;
        OnPropertyChanged(nameof(ManyItems));
        foreach (var scope in spec.Scopes)
        {
            scope.Ready();
            scope.Picks += Picked;
        }
        Scopes = spec.Scopes;
        _filter = spec.Filter;
        _fromInstall = spec.FromInstall;
        (_customStart, _customEnd) = spec.Custom ?? (DateTime.Today.AddDays(-6), DateTime.Today.AddDays(1));
        CustomFrom = _customStart.Date;
        CustomTo = (_customEnd.TimeOfDay == TimeSpan.Zero ? _customEnd.AddDays(-1) : _customEnd).Date;
        Periods = spec.Periods;
        for (int i = 0; i < Periods.Count; i++) Periods[i].IsOn = i == spec.Picked;
        Error = null;
        IsOpen = true;
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(CanExport));
        Counted();
        PeriodChanged();
        FirstDay = await reports.FirstDayAsync() ?? FirstDay;
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsBusy) IsOpen = false;
    }

    // ── The period ──

    /// <summary>The custom period as exported: to the hour when it came from the page, whole days once picked here.</summary>
    private DateTime _customStart, _customEnd;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText))]
    private DateTime? _firstDay;

    /// <summary>The custom period's first and last day, for the calendar (both in it).</summary>
    [ObservableProperty] private DateTime _customFrom;
    [ObservableProperty] private DateTime _customTo;

    private PeriodChoice? Period => Periods.FirstOrDefault(p => p.IsOn);

    /// <summary>The period's start and the moment after its end; a custom one's own.</summary>
    public (DateTime From, DateTime To) Bounds => Period is { Bounds: { } bounds } ? bounds() : (_customStart, _customEnd);

    /// <summary>A period picked; Custom is picked by the calendar (<see cref="PickCustom"/>).</summary>
    public void PickPeriod(PeriodChoice choice)
    {
        if (choice.IsCustom || IsBusy) return;
        foreach (var p in Periods) p.IsOn = p == choice;
        PeriodChanged();
    }

    public void PickCustom(DateTime a, DateTime b)
    {
        (CustomFrom, CustomTo) = a <= b ? (a.Date, b.Date) : (b.Date, a.Date);
        (_customStart, _customEnd) = (CustomFrom, CustomTo.AddDays(1));
        foreach (var p in Periods) p.IsOn = p.IsCustom;
        PeriodChanged();
    }

    private void PeriodChanged()
    {
        Error = null;
        OnPropertyChanged(nameof(PeriodText));
        Estimate();
    }

    /// <summary>The period in words, under its name: "Friday, 23 October", "17 Oct to 23 Oct · 7 days", "Thu 22 Oct, 11 AM – now".</summary>
    public string PeriodText => TextOf(Bounds, DateTime.Now, _fromInstall ? FirstDay : null);

    internal static string TextOf((DateTime From, DateTime To) period, DateTime now, DateTime? firstDay)
    {
        var (from, to) = period;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        // All time: everything there is, whenever it starts.
        if (from.Year <= 2000) return firstDay is { } since ? Lang.T($"Everything since {since:d MMM yyyy}") : Lang.T("Everything recorded");
        if (from.TimeOfDay != TimeSpan.Zero || to.TimeOfDay != TimeSpan.Zero)
        {
            if (to < now.AddMinutes(-1)) return Report.CustomTitle(from, to);
            string start = $"{from.ToString("ddd d MMM", culture)}, {from.ToString(TimeText.Fit("h:mm tt"), culture)}";
            return Lang.T($"{start} to now");
        }
        // A period reaching back past the first day recorded, or on past today, is said as far as there is history.
        var today = now.Date;
        if (firstDay is { } first && first > from && first <= today) from = first.Date;
        var last = to.AddDays(-1) > today ? today : to.AddDays(-1);
        if (last < from) last = from;
        int days = (int)(last - from).TotalDays + 1;
        if (days == 1) return from.ToString("dddd, d MMMM", culture);
        string Short(DateTime d) => d.ToString(from.Year == last.Year ? "d MMM" : "d MMM yyyy", culture);
        string a = Short(from), b = Short(last);
        return Lang.T($"{a} to {b} · {days} days");
    }

    // ── What's picked ──

    private void Picked()
    {
        Error = null;
        OnPropertyChanged(nameof(CanExport));
        Counted();
        Estimate();
    }

    private void Counted()
    {
        int on = Items.Count(i => i.IsOn), all = Items.Count;
        PickedText = Lang.T($"{on} of {all} picked");
        PickAllText = on == all ? Lang.T("Clear") : Lang.T("Pick all");
    }

    [RelayCommand]
    private void PickAll()
    {
        bool on = Items.Any(i => !i.IsOn);
        foreach (var item in Items) item.IsOn = on;
    }

    // ── Text, or files for a spreadsheet (the full Export box's choice, kept) ──

    public bool AsText
    {
        get => !settings.Current.ExportAsCsv;
        set
        {
            if (value == AsText) return;
            settings.Update(s => s.ExportAsCsv = !value);
            OnPropertyChanged(nameof(AsText));
            OnPropertyChanged(nameof(AsCsv));
            OnPropertyChanged(nameof(FormatText));
            Estimate();
        }
    }

    public bool AsCsv { get => !AsText; set { if (value) AsText = false; } }

    public string FormatText => AsText ? Lang.T("Laid out to read in Notepad or paste into a post") : Lang.T("Tables for Excel, the report and crashes as web pages");

    // ── The folder (the full Export box's, kept) ──

    public string Parent => settings.Current.ExportFolder is { Length: > 0 } picked && Directory.Exists(picked)
        ? picked : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string Folder => Path.Combine(Parent, $"RigSight export {DateTime.Today:yyyy-MM-dd}");

    /// <summary>Asks for a folder (null: none picked). Tests give their own.</summary>
    internal Func<string, string?> PickFolder { get; set; } = start =>
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Lang.T("Save the export in"), InitialDirectory = start };
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
        settings.Update(s => s.ExportFolder = picked);
        OnPropertyChanged(nameof(Parent));
        OnPropertyChanged(nameof(Folder));
    }

    // ── How big ──

    private int _estimate;

    /// <summary>Each thing's size beside it and the total under the list, from how many rows each has in the period.</summary>
    private async void Estimate()
    {
        if (!IsOpen) return;
        int id = ++_estimate;
        var (from, to) = Bounds;
        bool asText = AsText;
        var items = Items.ToList();
        foreach (var item in items.Where(i => i.IsNow)) item.SizeText = Lang.T("right now");
        var sizes = await reports.QueryAsync((db, _) => items.Select(item => SizeOf(db, item, from, to, asText)).ToList());
        if (id != _estimate || sizes is null) return;
        for (int i = 0; i < items.Count; i++)
            if (!items[i].IsNow) items[i].SizeText = AboutSize(sizes[i]);
        var on = items.Select((item, i) => (item, i)).Where(x => x.item.IsOn).ToList();
        string files = on.Count == 1 ? Lang.T("1 file") : Lang.T($"{on.Count} files");
        long total = on.Sum(x => sizes[x.i]);
        string size = Units.Data(Math.Max(total, 1024));
        Summary = on.Count == 0 ? Lang.T("Nothing picked yet.") : Lang.T($"{files}, about {size}");
    }

    private static string AboutSize(long bytes)
    {
        string size = Units.Data(Math.Max(bytes, 1024));
        return Lang.T($"about {size}");
    }

    private long SizeOf(RigsightDb db, PageExportItem item, DateTime from, DateTime to, bool asText)
    {
        // As far back as there is history, up to now.
        var start = FirstDay is { } first && first > from ? first : from;
        double days = Math.Max(1, (Math.Min(to.Ticks, DateTime.Now.Ticks) - start.Ticks) / (double)TimeSpan.TicksPerDay);
        return item.Kind switch
        {
            PageItemKind.Table => DataExport.EstimateBytes(db, item.Table!.Value, from, to) * (asText ? 3 : 2) / 2,
            PageItemKind.Report => asText ? 3_500 : 9_000 + db.CountRows("system_minute", "ts", TimeUtil.ToUnix(from), TimeUtil.ToUnix(to)) / 4 + 2_000,
            PageItemKind.Stories => (asText ? 300 : 4_500) + db.CountRows("crashes", "ts", TimeUtil.ToUnix(from), TimeUtil.ToUnix(to)) * (asText ? 600 : 900),
            PageItemKind.Snapshot => 600 + live.AllSensors.Count * 72L,
            _ => item.IsNow ? item.BytesPerDay : (long)(item.BytesPerDay * days),
        };
    }

    // ── Exporting ──

    [RelayCommand]
    private async Task Export()
    {
        if (!CanExport) return;
        var on = Items.Where(i => i.IsOn).ToList();
        var (from, to) = Bounds;
        var run = new PageExportRun(from, to, _filter((from, to)), DateTime.Now);
        string folder = Folder, period = PeriodText;
        bool asText = AsText;
        IsBusy = true;
        Error = null;
        try
        {
            Directory.CreateDirectory(folder);
            // What only the window knows (the readings now, what a page lists) is read here; the rest off its thread.
            var sheets = on.Where(i => i.Kind == PageItemKind.Sheet).Select(i => i.Sheet!(run)).ToList();
            if (on.Any(i => i.Kind == PageItemKind.Snapshot))
                await File.WriteAllTextAsync(Path.Combine(folder, ReadableExport.SnapshotFile), ExportViewModel.SnapshotOf(live), new UTF8Encoding(true));
            if (on.Any(i => i.Kind == PageItemKind.Report))
                await ExportViewModel.WriteReportAsync(reports, from, to, Path.Combine(folder, asText ? ReadableExport.ReportTextFile : ReadableExport.ReportFile), asText);
            string? failed = await reports.QueryAsync((db, s) =>
            {
                try
                {
                    foreach (var item in on)
                    {
                        if (item.Kind == PageItemKind.Table)
                            DataExport.WriteFile(db, item.Table!.Value, from, to, s, Path.Combine(folder, Path.ChangeExtension(DataExport.FileName(item.Table.Value), asText ? ".txt" : ".csv")), asText, run.Filter);
                        else if (item.Kind == PageItemKind.Stories)
                            ExportViewModel.WriteStories(ExportViewModel.Stories(db, s, from, to, run.Filter.Crashes), period,
                                Path.Combine(folder, asText ? ReadableExport.CrashesTextFile : ReadableExport.CrashesFile), asText);
                    }
                    foreach (var make in sheets)
                    {
                        var sheet = make(db, s);
                        DataExport.WriteSheet(sheet, Path.Combine(folder, sheet.File + (asText ? ".txt" : ".csv")), asText);
                    }
                    return "";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    Log.Error("export", ex);
                    return ex.Message;
                }
            });
            if (failed is null) Error = Lang.T("The history couldn't be opened. Try again in a moment.");
            else if (failed.Length > 0) Error = Lang.T($"Couldn't finish: {failed}");
            else
            {
                Log.Write("export", $"{Title}: {on.Count} file(s) ({string.Join(", ", on.Select(i => i.Kind == PageItemKind.Table ? i.Table.ToString() : i.Kind.ToString()))}) as {(asText ? "text" : "CSV")} for {from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm}");
                ShowFolder(folder);
                IsOpen = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("export", ex);
            string why = ex.Message;
            Error = Lang.T($"Couldn't save there: {why}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
