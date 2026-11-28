using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Export;
using Rigsight.Core.Parts;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Converters;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>The GPU or CPU page's history for a period, with the names and files of the apps it mentions.</summary>
public sealed record PartPage(PartReport Report, IReadOnlyDictionary<long, string> Names, IReadOnlyDictionary<long, string?> Paths);

/// <summary>
/// A live tile at the top of the GPU or CPU page: its caps label beside a coloured icon square (theme brushes by name), the
/// figure and its unit, a grey line, and the last minute as a line in its colour.
/// </summary>
public sealed partial class PartTile(string caption, string glyph, string ink, string back, SensorKind kind) : ObservableObject
{
    public string Caption { get; } = caption;
    public string Glyph { get; } = glyph;
    public string Ink { get; } = ink;
    public string Back { get; } = back;
    public SensorKind Kind { get; } = kind;
    public double? Minimum => Kind == SensorKind.Load ? 0 : null;
    public double? Maximum => Kind == SensorKind.Load ? 100 : null;

    [ObservableProperty] private string _value = "—";
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private HistoryBuffer? _source;
    [ObservableProperty] private long _version;
}

/// <summary>A thread's bar in a core's tile (0 to 100).</summary>
public sealed partial class ThreadBar : ObservableObject
{
    [ObservableProperty] private double _value;
}

/// <summary>A core's tile on the CPU page: its number, load, a bar per thread, its clock, a shade that deepens with load, the busiest outlined.</summary>
public sealed partial class CoreTileView : ObservableObject
{
    public CoreTileView(int number, int threads)
    {
        Caption = Lang.T($"CORE {number}");
        for (int i = 0; i < Math.Max(1, threads); i++) Threads.Add(new ThreadBar());
    }

    public string Caption { get; }
    public ObservableCollection<ThreadBar> Threads { get; } = [];
    [ObservableProperty] private string _load = "";
    [ObservableProperty] private string _foot = "";
    [ObservableProperty] private Brush? _shade;
    [ObservableProperty] private bool _isTop;
}

/// <summary>A group of core tiles: PERFORMANCE CORES / EFFICIENT CORES with their count on a hybrid CPU, one group without a heading otherwise.</summary>
public sealed record CoreGroupView(string Heading, string Count, int Columns, IReadOnlyList<CoreTileView> Cores)
{
    public bool HasHeading => Heading.Length > 0;
}

/// <summary>A line of "What works it hardest": an app's icon, its name, the part's load and temperature, power and time in front.</summary>
public sealed record PartAppView(string Name, string? Path, string Letter, string Figures, string Power, string Time)
{
    public bool HasIcon => Path is not null;
}

/// <summary>
/// The GPU page and the CPU page (1.23.0, approved mock-up section 10): live tiles first (the part's load, heat, clock,
/// power and more, each with its last minute), on the CPU page each core right now; then the period, one chart of the
/// part's temperature, load, power or clock that keeps the time with a game in front apart from everything else, and the
/// apps that work it hardest (on the GPU page with its graphics driver, opening the one-driver view inside Timeline).
/// Live readings are the ones the app already gets each second, followed only while the page is open; nothing new is kept.
/// </summary>
public sealed partial class PartViewModel : ObservableObject
{
    private readonly ReportService _reports;
    private readonly SettingsModel _settings;

    public LiveData Live { get; }
    public Part Part { get; }
    public bool IsGpu => Part == Part.Gpu;
    public bool IsCpu => Part == Part.Cpu;

    /// <summary>Opens the graphics card's driver inside Timeline (its key), the GPU page's "Every version".</summary>
    public Action<string>? OpenDriver { get; init; }

    public PartViewModel(Part part, ReportService reports, LiveData live, SettingsModel settings)
    {
        Part = part;
        _reports = reports;
        _settings = settings;
        Live = live;
    }

    public string Title => IsGpu ? Lang.T("GPU") : Lang.T("CPU");

    // ── The subtitle: the part's name, and the GPU's driver or the CPU's cores ──

    private DriverDeviceLine? _driver;

    public string Subtitle
    {
        get
        {
            string name = IsGpu ? Live.GpuName : Live.CpuName;
            string more = IsGpu ? _driver?.Short ?? "" : CpuCores.Count(CpuTopology.Cores);
            return more.Length > 0 ? $"{name} · {more}" : name;
        }
    }

    /// <summary>The graphics driver line under "What works it hardest" ("Graphics driver 621.24, since 13 Nov, was 621.01").</summary>
    public string DriverText => _driver?.Long ?? "";
    public bool HasDriver => IsGpu && _driver is not null;

    [RelayCommand]
    private void OpenDriverVersions()
    {
        if (_driver is { } d) OpenDriver?.Invoke(d.Key);
    }

    private sealed record DriverDeviceLine(string Key, string Short, string Long);

    private static DriverDeviceLine? LineOf(Core.Stability.DriverDevice? card)
    {
        if (card is null) return null;
        var now = card.Current;
        string version = now.Shown;
        // A start not known (there at the first look) isn't a date to say.
        bool known = now.How != Core.Stability.DriverStart.Unknown;
        string since = known ? now.Since.ToString("d MMM", CultureInfo.CurrentCulture) : "";
        string? was = card.Versions.Count > 1 ? card.Versions[1].Shown : null;
        string shortText = known ? Lang.T($"driver {version}, since {since}") : Lang.T($"driver {version}");
        string longText = (known, was) switch
        {
            (true, not null) => Lang.T($"Graphics driver {version}, since {since}, was {was}"),
            (true, null) => Lang.T($"Graphics driver {version}, since {since}"),
            (false, not null) => Lang.T($"Graphics driver {version}, was {was}"),
            _ => Lang.T($"Graphics driver {version}"),
        };
        return new DriverDeviceLine(card.Key, shortText, longText);
    }

    // ── Live: the tiles and each core, followed only while the page is on screen ──

    public ObservableCollection<PartTile> Tiles { get; } = [];
    public bool HasTiles => Tiles.Count > 0;
    public int TileColumns => Math.Max(1, Tiles.Count);

    public ObservableCollection<CoreGroupView> CoreGroups { get; } = [];
    [ObservableProperty] private string _coreSentence = "";
    public bool HasCores => IsCpu && CoreGroups.Count > 0;

    private bool _live;
    // Null: look for the tiles again (the page came on screen, the sensors were found again).
    private string? _tilesShape;
    private int _tilesLooked;
    private readonly List<TileSpec> _tileSpecs = [];

    /// <summary>A tile, its reading now, its grey line, the sensor whose history it draws (or its own, for a figure worked out from several), and the figure with its unit.</summary>
    private sealed record TileSpec(PartTile Tile, Func<double?> Read, Func<string> Sub, SensorItem? Sensor, HistoryBuffer? Own, Func<double, (string, string)> Shown);
    private readonly HistoryBuffer _clockOwn = new(120), _voltOwn = new(120);
    private List<CoreTileView> _coreTiles = [];
    private string _coresShape = "";
    private List<(SensorItem Sensor, CoreName Name)>? _coreSensors;

    /// <summary>The page came on screen (true) or went (false, also with the window minimised).</summary>
    public void SetLive(bool on)
    {
        if (on == _live) return;
        _live = on;
        if (on)
        {
            Live.PropertyChanged += OnLive;
            Live.SensorsRebuilt += OnSensorsRebuilt;
            _tilesShape = null;
            _coresShape = "";
            _coreSensors = null;
            OnPropertyChanged(nameof(Subtitle));
            UpdateLive();
        }
        else
        {
            Live.PropertyChanged -= OnLive;
            Live.SensorsRebuilt -= OnSensorsRebuilt;
        }
    }

    private void OnLive(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveData.Tick)) UpdateLive();
    }

    private void OnSensorsRebuilt()
    {
        _tilesShape = null;
        _coresShape = "";
        _coreSensors = null;
        OnPropertyChanged(nameof(Subtitle));
    }

    private void UpdateLive()
    {
        try
        {
            UpdateTiles();
            if (IsCpu) UpdateCores();
        }
        catch (Exception ex)
        {
            Log.Write(IsGpu ? "gpu" : "cpu", $"Expected the live tiles to update, found {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool Read(SensorItem? s) => s is not null && (s.Value is not null || s.Max is not null);

    private HardwareNode? GpuNode => Live.Hardware.FirstOrDefault(h => h.Type is "GpuNvidia" or "GpuAmd" or "GpuIntel" && h.Name == Live.GpuName);
    private HardwareNode? CpuNode => Live.Hardware.FirstOrDefault(h => h.Type == "Cpu");

    private static string N0(double v) => v.ToString("N0", CultureInfo.CurrentCulture);

    private void UpdateTiles()
    {
        // The tiles this PC has a reading for, in their order; a tile with none is left out (the same parts in every tile).
        // Looked at again every 10 seconds (a reading can start or stop), at once after the sensors were found again.
        var specs = _tilesShape is null || ++_tilesLooked % 10 == 0 ? IsGpu ? GpuSpecs() : CpuSpecs() : null;
        if (specs is not null && string.Join("|", specs.Select(s => s.Tile.Caption)) is var shape && shape != _tilesShape)
        {
            _tilesShape = shape;
            _tileSpecs.Clear();
            _tileSpecs.AddRange(specs);
            Tiles.Clear();
            foreach (var s in specs) Tiles.Add(s.Tile);
            OnPropertyChanged(nameof(HasTiles));
            OnPropertyChanged(nameof(TileColumns));
            Log.Write(IsGpu ? "gpu" : "cpu", $"Live tiles: {(shape.Length == 0 ? "none (no readings)" : shape)}");
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (tile, read, sub, sensor, own, shown) in _tileSpecs)
        {
            double? v = read();
            if (own is not null)
            {
                if (own.Count == 0 || now - own.LastTime >= 500) own.Add(now, v ?? double.NaN);
                tile.Source = own;
                tile.Version++;
            }
            else if (sensor is not null)
            {
                tile.Source = sensor.History;
                tile.Version = sensor.Version;
            }
            (tile.Value, tile.Unit) = v is double x ? shown(x) : ("—", tile.Unit);
            tile.Sub = sub();
        }
    }

    private TileSpec Spec(
        string caption, string glyph, string ink, string back, SensorKind kind, SensorItem? sensor, Func<string> sub,
        Func<double, (string, string)> shown, Func<double?>? read = null, HistoryBuffer? own = null)
    {
        var existing = _tileSpecs.FirstOrDefault(s => s.Tile.Caption == caption)?.Tile;
        var tile = existing is not null && existing.Ink == ink ? existing : new PartTile(caption, glyph, ink, back, kind);
        return new TileSpec(tile, read ?? (() => sensor?.Value), sub, sensor, own, shown);
    }

    private static (string, string) Percent(double v) => ($"{Math.Round(v):0}", "%");
    private static (string, string) Degrees(double v) => ($"{Math.Round(Units.Temp(v)):0}", "°");
    private static (string, string) Mhz(double v) => (N0(Math.Round(v)), "MHz");
    private static (string, string) Watts(double v) => ($"{Math.Round(v):0}", "W");
    private static (string, string) Rpm(double v) => (N0(Math.Round(v)), "RPM");

    private List<TileSpec> GpuSpecs()
    {
        var list = new List<TileSpec>();
        var node = GpuNode;
        if (Read(Live.GpuLoad))
            list.Add(Spec(Lang.T("LOAD"), "", "GpuBrush", "GpuSoftBrush", SensorKind.Load, Live.GpuLoad,
                () => Live.Activity is { Category: AppCategory.Game, Name: { Length: > 0 } game } ? Lang.T($"In {game}") : "", Percent));
        if (Read(Live.GpuTemp))
            list.Add(Spec(Lang.T("TEMPERATURE"), "", "GpuBrush", "GpuSoftBrush", SensorKind.Temperature, Live.GpuTemp,
                () => Live.GpuHotSpot?.Value is double hot ? Lang.T($"Hot spot {Units.TempShort(hot)}") : "", Degrees));
        if (Read(Live.GpuClock))
        {
            var memory = node?.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Clock && s.Name == "GPU Memory");
            list.Add(Spec(Lang.T("CLOCK"), "", "PurpleBrush", "PurpleSoftBrush", SensorKind.Clock, Live.GpuClock,
                () => memory?.Value is double m ? Lang.T($"Memory {N0(Math.Round(m))} MHz") : "", Mhz));
        }
        // The power limit isn't read on any card yet, so the grey line stays empty (the mock-up's "Of 220 W allowed").
        if (Read(Live.GpuPower))
            list.Add(Spec(Lang.T("POWER"), "", "IndigoBrush", "IndigoSoftBrush", SensorKind.Power, Live.GpuPower, () => "", Watts));
        if (Read(Live.GpuVramUsed))
            list.Add(Spec(Lang.T("GPU MEMORY"), "", "PinkBrush", "PinkSoftBrush", SensorKind.SmallData, Live.GpuVramUsed,
                () => Live.Activity is { VramMb: double vram and >= 1, Name: { Length: > 0 } app } ? $"{app} {Units.Megabytes(vram)}" : "",
                v => Live.GpuVramTotal?.Value is double total and > 0
                    ? ((v / 1024).ToString("0.0", CultureInfo.CurrentCulture), Lang.T($"of {(total / 1024).ToString("0.#", CultureInfo.CurrentCulture)} GB"))
                    : ((v / 1024).ToString("0.0", CultureInfo.CurrentCulture), "GB")));
        if (Read(Live.GpuFan))
        {
            var control = node?.Sensors.FirstOrDefault(s => s.Kind == SensorKind.Control && s.Name.StartsWith("GPU Fan", StringComparison.Ordinal));
            list.Add(Spec(Lang.T("FAN"), "", "MutedBrush", "Surface2Brush", SensorKind.Fan, Live.GpuFan,
                () => control?.Value is double c ? $"{Math.Round(c):0}%" : "", Rpm));
        }
        return list;
    }

    private List<TileSpec> CpuSpecs()
    {
        var list = new List<TileSpec>();
        var cpu = CpuNode;
        var sensors = cpu?.Sensors.ToList() ?? [];
        if (Read(Live.CpuLoad))
            list.Add(Spec(Lang.T("LOAD"), "", "CpuBrush", "CpuSoftBrush", SensorKind.Load, Live.CpuLoad, () => Lang.T("All cores together"), Percent));
        if (Read(Live.CpuTemp))
        {
            string where = Live.CpuTemp!.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ? Lang.T("Package") : SensorWords.Show(Live.CpuTemp.Name);
            list.Add(Spec(Lang.T("TEMPERATURE"), "", "CpuBrush", "CpuSoftBrush", SensorKind.Temperature, Live.CpuTemp, () => where, Degrees));
        }
        // The clock: the library's own average of the cores, else one worked out from each core's.
        var coreClocks = sensors.Where(s => s.Kind == SensorKind.Clock && CpuCores.ClockName(s.Name) is not null).ToList();
        if (Read(Live.CpuClock) && Live.CpuClock!.Name == "Cores (Average)")
            list.Add(Spec(Lang.T("CLOCK"), "", "PurpleBrush", "PurpleSoftBrush", SensorKind.Clock, Live.CpuClock, () => Lang.T("Average of the cores"), Mhz));
        else if (coreClocks.Any(Read))
            list.Add(Spec(Lang.T("CLOCK"), "", "PurpleBrush", "PurpleSoftBrush", SensorKind.Clock, null, () => Lang.T("Average of the cores"), Mhz,
                () => coreClocks.Select(s => s.Value).OfType<double>().Where(v => v > 0).DefaultIfEmpty(double.NaN).Average() is var a && double.IsFinite(a) ? a : null, _clockOwn));
        else if (Read(Live.CpuClock))
            list.Add(Spec(Lang.T("CLOCK"), "", "PurpleBrush", "PurpleSoftBrush", SensorKind.Clock, Live.CpuClock, () => "", Mhz));
        if (Read(Live.CpuPower))
        {
            string where = Live.CpuPower!.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ? Lang.T("Package") : "";
            list.Add(Spec(Lang.T("POWER"), "", "IndigoBrush", "IndigoSoftBrush", SensorKind.Power, Live.CpuPower, () => where, Watts));
        }
        // The voltage: the highest of each core's, else the one the CPU gives.
        var vids = sensors.Where(s => s.Kind == SensorKind.Voltage && CpuCores.VidName(s.Name) is not null).ToList();
        static (string, string) Volts(double v) => (v.ToString("0.00", CultureInfo.CurrentCulture), "V");
        if (vids.Any(Read))
            list.Add(Spec(Lang.T("VOLTAGE"), "", "PinkBrush", "PinkSoftBrush", SensorKind.Voltage, null, () => Lang.T("Highest core"), Volts,
                () => vids.Select(s => s.Value).OfType<double>().Where(v => v > 0).DefaultIfEmpty(double.NaN).Max() is var m && double.IsFinite(m) ? m : null, _voltOwn));
        else if (Read(Live.CpuVoltage))
            list.Add(Spec(Lang.T("VOLTAGE"), "", "PinkBrush", "PinkSoftBrush", SensorKind.Voltage, Live.CpuVoltage, () => "", Volts));
        // The CPU's fan where the board or the person names one so ("CPU Fan", "CPU_FAN", a label with CPU in it).
        var fan = Live.Fans.FirstOrDefault(f => Read(f) && (f.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) || f.DisplayName.Contains("CPU", StringComparison.OrdinalIgnoreCase)));
        if (fan is not null)
            list.Add(Spec(Lang.T("FAN"), "", "MutedBrush", "Surface2Brush", SensorKind.Fan, fan, () => fan.DisplayName, Rpm));
        return list;
    }

    private void UpdateCores()
    {
        // The per-core sensors, found by name once after the sensors were (re)built.
        if (_coresShape.Length == 0 || _coreSensors is null)
        {
            var cpu = CpuNode;
            _coreSensors = cpu is null ? [] : [.. cpu.Sensors
                .Select(s => (Sensor: s, Name: s.Kind == SensorKind.Load ? CpuCores.LoadName(s.Name) : s.Kind == SensorKind.Clock ? CpuCores.ClockName(s.Name) : null))
                .Where(x => x.Name is not null).Select(x => (x.Sensor, x.Name!.Value))];
        }
        if (_coreSensors.Count == 0) { if (CoreGroups.Count > 0) { CoreGroups.Clear(); OnPropertyChanged(nameof(HasCores)); } return; }
        // Each core's load (its own, or its threads') and clock, by its kind and number as the sensor library names them.
        var loads = new Dictionary<(CoreKind?, int), (double? Core, SortedDictionary<int, double> Threads)>();
        var clocks = new Dictionary<(CoreKind?, int), double?>();
        foreach (var (s, n) in _coreSensors)
        {
            var key = (n.Kind, n.Core);
            if (s.Kind == SensorKind.Load)
            {
                if (!loads.TryGetValue(key, out var e)) loads[key] = e = (null, new SortedDictionary<int, double>());
                if (n.Thread is int t) { if (s.Value is double v) e.Threads[t] = v; }
                else loads[key] = (s.Value, e.Threads);
            }
            else clocks[key] = s.Value;
        }
        var now = loads.Select(kv => new CoreNow(kv.Key.Item1, kv.Key.Item2, kv.Value.Core, [.. kv.Value.Threads.Values], clocks.GetValueOrDefault(kv.Key))).ToList();
        var groups = CpuCores.Group(now, CpuTopology.Cores);
        var all = groups.SelectMany(g => g.Cores).ToList();
        string shape = string.Join("|", groups.Select(g => $"{g.Kind}:{string.Join(",", g.Cores.Select(c => c.ThreadCount))}"));
        if (shape != _coresShape)
        {
            _coresShape = shape;
            CoreGroups.Clear();
            _coreTiles = [];
            foreach (var g in groups)
            {
                var tiles = g.Cores.Select(c => new CoreTileView(c.Number, c.ThreadCount)).ToList();
                _coreTiles.AddRange(tiles);
                string heading = g.Kind switch { CoreKind.Performance => Lang.T("PERFORMANCE CORES"), CoreKind.Efficient => Lang.T("EFFICIENT CORES"), _ => "" };
                CoreGroups.Add(new CoreGroupView(heading, g.Cores.Count.ToString(CultureInfo.CurrentCulture), Math.Min(8, g.Cores.Count), tiles));
            }
            OnPropertyChanged(nameof(HasCores));
            Log.Write("cpu", $"Each core: {all.Count} cores in {groups.Count} group(s) ({shape}); Windows lists {CpuTopology.Cores.Count} cores");
        }
        var top = CpuCores.Busiest(all);
        for (int i = 0; i < all.Count && i < _coreTiles.Count; i++)
        {
            var c = all[i];
            var tile = _coreTiles[i];
            tile.Load = $"{Math.Round(c.Load):0}";
            tile.Foot = CpuCores.Foot(c);
            tile.IsTop = ReferenceEquals(c, top);
            // The shade deepens as the core gets busier (the mock-up's 0.04 to 0.24 of the CPU's blue).
            tile.Shade = ChartPaint.Brush(ChartPaint.Cpu, 0.04 + c.Load / 100 * 0.2);
            for (int t = 0; t < tile.Threads.Count; t++)
                tile.Threads[t].Value = t < c.Threads.Count ? c.Threads[t] : c.Load;
        }
        CoreSentence = CpuCores.Sentence(all);
    }

    // ── The period ──

    /// <summary>Opens on the last 7 days (the approved mock-up).</summary>
    [ObservableProperty] private ReportRange _unit = ReportRange.Week;
    [ObservableProperty] private DateTime _anchor = DateTime.Today;
    [ObservableProperty] private DateTime? _firstDay;
    [ObservableProperty] private DateTime _customFrom;
    [ObservableProperty] private DateTime _customTo;

    private (DateTime From, DateTime To) Period => PeriodPicker.Bounds(Unit, Anchor, CustomFrom, CustomTo);
    public bool IncludesToday => Period.To > DateTime.Today;
    public string RangeNote => PeriodPicker.Span(Unit, Anchor, CustomFrom, CustomTo);

    partial void OnUnitChanged(ReportRange value) { OnPropertyChanged(nameof(RangeNote)); if (!_quiet && (value != ReportRange.Custom || CustomTo > CustomFrom)) _ = RefreshAsync(); }
    partial void OnAnchorChanged(DateTime value) { OnPropertyChanged(nameof(RangeNote)); if (!_quiet) _ = RefreshAsync(); }
    partial void OnCustomFromChanged(DateTime value) => CustomChanged();
    partial void OnCustomToChanged(DateTime value) => CustomChanged();

    private void CustomChanged()
    {
        OnPropertyChanged(nameof(RangeNote));
        if (!_quiet && Unit == ReportRange.Custom && CustomTo > CustomFrom) _ = RefreshAsync();
    }

    public void NewDay(DateTime was)
    {
        var anchor = PeriodPicker.AfterMidnight(Unit, Anchor, was, DateTime.Today);
        if (anchor != Anchor) { Anchor = anchor; return; }
        OnPropertyChanged(nameof(RangeNote));
        _ = RefreshAsync();
    }

    // Set while a page that's never shown is given its period (see SummaryAsync): one read, not one per step.
    private bool _quiet;

    [ObservableProperty] private bool _isLoading;
    /// <summary>Read at least once: until then only the loader shows.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowLoading), nameof(ShowEmpty), nameof(ShowHistory))] private bool _loaded;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowHistory))] private bool _hasHistory;

    public bool ShowLoading => !Loaded;
    public bool ShowEmpty => Loaded && !HasHistory;
    public bool ShowHistory => Loaded && HasHistory;
    public string EmptyText => IncludesToday ? Lang.T("Nothing measured yet") : Wording.NotRecorded;

    private PartPage? _page;
    private int _load;
    private int _driverRead;

    public async Task RefreshAsync()
    {
        int id = ++_load;
        IsLoading = true;
        try
        {
            FirstDay ??= await _reports.FirstMinuteDayAsync();
            var (from, to) = Period;
            if (to <= from) return;
            // The graphics driver: read on the first visit and every tenth read after (it changes a few times a year).
            if (IsGpu && _driverRead++ % 10 == 0)
            {
                _driver = LineOf(await _reports.GraphicsDriverAsync());
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(DriverText));
                OnPropertyChanged(nameof(HasDriver));
            }
            // The part's name now: the last new part on record must have put it in (none known yet: every change counts).
            string name = IsGpu ? Live.GpuName : Live.CpuName;
            var page = await _reports.PartAsync(Part, Unit, from, to, name is "GPU" or "CPU" ? null : name);
            if (id != _load) return;
            _page = page;
            Fill();
            Loaded = true;
        }
        finally
        {
            if (id == _load) IsLoading = false;
        }
    }

    // ── The chart ──

    /// <summary>What the chart shows (Temperature first, as the mock-up opens).</summary>
    [ObservableProperty] private PartMetric _metric = PartMetric.Temperature;

    partial void OnMetricChanged(PartMetric value) => FillChart();

    [ObservableProperty] private IReadOnlyList<PartChartSlot> _slots = [];
    /// <summary>The driver updates and new parts in the period (1.23.0), over the chart.</summary>
    [ObservableProperty] private IReadOnlyList<PartChartMark> _marks = [];
    /// <summary>The legend's "◆ Driver update" and "Your GPU before", shown only with a mark of that kind.</summary>
    [ObservableProperty] private bool _hasDriverMarks;
    [ObservableProperty] private bool _hasOldPart;

    public string OldLegend => IsGpu ? Lang.T("Your GPU before") : Lang.T("Your CPU before");
    [ObservableProperty] private string _chartTitle = "";
    [ObservableProperty] private bool _isHourly;
    [ObservableProperty] private bool _hasTemperature;
    [ObservableProperty] private bool _hasLoad;
    [ObservableProperty] private bool _hasPower;
    [ObservableProperty] private bool _hasClock;

    /// <summary>The soft bars' name: an hour has one bar (no game in front), a day or a week a pair (everything else).</summary>
    public string RestLegend => IsHourly ? Lang.T("No game in front") : Lang.T("Everything else");
    partial void OnIsHourlyChanged(bool value) => OnPropertyChanged(nameof(RestLegend));

    /// <summary>The part's own colour, for the chart, its legend and the hover (a theme brush's name).</summary>
    public string PartBrush => IsGpu ? "GpuBrush" : "CpuBrush";

    [ObservableProperty] private IReadOnlyList<PartAppView> _apps = [];
    public bool HasApps => Apps.Count > 0;
    partial void OnAppsChanged(IReadOnlyList<PartAppView> value) => OnPropertyChanged(nameof(HasApps));

    private void Fill()
    {
        var r = _page?.Report;
        HasHistory = r is { HasAny: true };
        OnPropertyChanged(nameof(EmptyText));
        if (r is null) { Slots = []; Marks = []; Apps = []; return; }
        HasTemperature = r.Has(PartMetric.Temperature);
        HasLoad = r.Has(PartMetric.Load);
        HasPower = r.Has(PartMetric.Power);
        HasClock = r.Has(PartMetric.Clock);
        // The metric picked stays when the period has it; else the first one it has.
        bool Has(PartMetric m) => m switch { PartMetric.Temperature => HasTemperature, PartMetric.Load => HasLoad, PartMetric.Power => HasPower, _ => HasClock };
        if (!Has(Metric) && new[] { PartMetric.Temperature, PartMetric.Load, PartMetric.Power, PartMetric.Clock }.FirstOrDefault(Has) is var first && Has(first))
            Metric = first;
        else FillChart();
        Apps = AppsOf(_page!);
    }

    private void FillChart()
    {
        if (_page is not { } page) return;
        var r = page.Report;
        IsHourly = r.Step == PartStep.Hour;
        ChartTitle = PartReport.Title(Metric, r.Step);
        var metric = Metric;
        // Temperatures in the unit picked (°F too), the rest as read.
        double Shown(double v) => metric == PartMetric.Temperature ? Units.Temp(v) : v;
        PartBar? Bar(PartSide? side, bool game) =>
            side?.Of(metric) is { Average: double avg } s ? new PartBar(Shown(avg), s.Max is double h ? Shown(h) : null, game) : null;
        var list = new List<PartChartSlot>(r.Slots.Count);
        int every = Math.Max(1, (int)Math.Ceiling(r.Slots.Count / 8.0));
        for (int i = 0; i < r.Slots.Count; i++)
        {
            var s = r.Slots[i];
            int index = i;
            string axis = r.Step switch
            {
                PartStep.Hour => i % 3 == 0 ? TimeText.At(s.From, "h tt") : "",
                PartStep.Week => i == 0 || s.From.Month != r.Slots[i - 1].From.Month ? s.From.ToString("MMM", CultureInfo.CurrentCulture) : "",
                _ when r.Slots.Count <= 7 => s.From.ToString("ddd d", CultureInfo.CurrentCulture),
                _ => i % every == 0 ? s.From.ToString("d MMM", CultureInfo.CurrentCulture) : "",
            };
            var slot = s;
            if (r.Step == PartStep.Hour)
            {
                // An hour is one bar: the whole hour's figures, in the game's colour when a game was in front for most of it.
                bool game = (s.Games?.Minutes ?? 0) >= (s.Rest?.Minutes ?? 0) && s.Games is not null;
                list.Add(new PartChartSlot(axis, false, Bar(s.All, game), null, !s.Recorded && !s.Ahead, () => TipOf(slot, index, r.Step, page), s.Old));
            }
            else list.Add(new PartChartSlot(axis, true, Bar(s.Games, true), Bar(s.Rest, false), !s.Recorded && !s.Ahead, () => TipOf(slot, index, r.Step, page), s.Old));
        }
        Slots = list;
        Marks = MarksOf(page);
        HasDriverMarks = r.Marks.Any(m => m.Kind == PartMarkKind.Driver);
        HasOldPart = r.Slots.Any(s => s.Old && s.Recorded);
    }

    // ── The marks: a driver update, a new part ──

    private List<PartChartMark> MarksOf(PartPage page)
    {
        var r = page.Report;
        var list = new List<PartChartMark>(r.Marks.Count);
        foreach (var m in r.Marks)
        {
            var mark = m;
            if (m.Kind == PartMarkKind.Driver)
            {
                var (label, shortLabel) = PartMarks.DriverLabel(m);
                string? key = m.DeviceKey ?? _driver?.Key;
                list.Add(new PartChartMark(m.Slot, false, label, shortLabel, null, () => DriverTip(mark, page), key is null || OpenDriver is null ? null : () => OpenDriver(key)));
            }
            else
            {
                var (before, label, shortLabel) = PartMarks.PartLabels(Part, m, r.Step);
                list.Add(new PartChartMark(m.Slot, true, label, shortLabel, before, () => NewPartTip(mark, page), null));
            }
        }
        return list;
    }

    private Brush FullBrush => ResourceBrushConverter.Of(PartBrush) ?? ChartPaint.CpuBrush;

    private Brush SoftBrush(double opacity)
    {
        Brush soft = FullBrush.Clone();
        soft.Opacity = opacity;
        soft.Freeze();
        return soft;
    }

    private static RichIcon IconOf(PartPage page, long id)
    {
        string n = page.Names.TryGetValue(id, out var name) ? name : "";
        return IconCache.Get(page.Paths.GetValueOrDefault(id)) is { } img ? new RichIcon(Image: img) : new RichIcon(Letter: n.Length > 0 ? n[..1].ToUpperInvariant() : "?");
    }

    /// <summary>
    /// A driver mark's hover (the approved mock-up's): the driver and its version, when it came and after which, the chosen
    /// metric with a game in front before and since (average, highest, each with its dot), the games played on both sides
    /// with their icons, and what a click does.
    /// </summary>
    private RichTip DriverTip(PartMark m, PartPage page)
    {
        var facts = new List<RichFact>();
        void Side(PartSpan span, string label, Brush dot)
        {
            if (PartReport.SideWords(span.Games, Metric) is var (avg, high))
                facts.Add(new RichFact(label, avg, Wide: true, Icon: new RichIcon(Back: dot), Detail: high.Length > 0 ? high : null));
            else facts.Add(new RichFact(label, Lang.T("No games"), Wide: true, Icon: new RichIcon(Back: dot)));
        }
        Side(m.Before, PartMarks.BeforeLabel(m.Before), SoftBrush(0.45));
        Side(m.After, PartMarks.AfterLabel(m.After), FullBrush);
        var both = m.BothSides()
            .Where(id => page.Names.TryGetValue(id, out var n) && n.Length > 0)
            .Select(id => PartMarks.Then(Metric, m.Before.ByGame[id], m.After.ByGame[id]) is { } then ? new RichItem(IconOf(page, id), page.Names[id], then) : null)
            .OfType<RichItem>().ToList();
        return new RichTip(m.Title)
        {
            Sub = PartMarks.DriverSub(m),
            Facts = facts,
            Notes = both.Count > 0 ? [new RichNote(Lang.T("Played on both sides"), both)] : [],
            Foot = m.DeviceKey is not null || _driver is not null ? Lang.T("Click to see the driver's versions") : null,
        };
    }

    /// <summary>
    /// A new part's hover: which part and since when, the one before it and since when, the chosen metric with a game in
    /// front on each, and that the page's usual and What works it hardest count the new one only.
    /// </summary>
    private RichTip NewPartTip(PartMark m, PartPage page)
    {
        var facts = new List<RichFact>();
        if (PartMarks.BeforeIt(m) is { } beforeIt) facts.Add(new RichFact(Lang.T("Before it"), beforeIt, Wide: true));
        string Figure(PartSpan span) => span.Games?.Of(Metric).Average is double avg ? PartReport.Figure(Metric, avg) : Lang.T("No games");
        if (m.Was is { Length: > 0 } was)
            facts.Add(new RichFact(PartMarks.WithAGame(Part, was), Figure(m.Before), Icon: new RichIcon(Back: SoftBrush(0.4))));
        facts.Add(new RichFact(PartMarks.WithAGame(Part, m.Now), Figure(m.After), Wide: m.Was is not { Length: > 0 }, Icon: new RichIcon(Back: FullBrush)));
        return new RichTip(PartMarks.PartTitle(Part))
        {
            Sub = PartMarks.PartSub(m),
            Facts = facts,
            Foot = PartMarks.PartFoot(Part, m),
        };
    }

    /// <summary>
    /// A stretch's hover (the mock-up's histTip): its title, the time split, the averages and highests with a game in front
    /// and of everything else, the games with their icons and times, and the part's usual temperature at the foot.
    /// </summary>
    private RichTip TipOf(PartSlot s, int index, PartStep step, PartPage page)
    {
        string title = PartReport.SlotTitle(s, step);
        if (s.Ahead) return new RichTip(title) { Sub = Lang.T("Still to come") };
        if (!s.Recorded) return new RichTip(title) { Big = Wording.NotRecorded };
        Brush full = ResourceBrushConverter.Of(PartBrush) ?? ChartPaint.CpuBrush;
        Brush soft = full.Clone();
        soft.Opacity = 0.38;
        soft.Freeze();
        string Name(long id) => page.Names.TryGetValue(id, out var n) ? n : "";
        RichIcon IconOf(long id)
        {
            string n = Name(id);
            return IconCache.Get(page.Paths.GetValueOrDefault(id)) is { } img ? new RichIcon(Image: img) : new RichIcon(Letter: n.Length > 0 ? n[..1].ToUpperInvariant() : "?");
        }
        var games = s.GameTimes.Where(g => Name(g.App).Length > 0).ToList();
        string? sub;
        RichIcon? subIcon = null;
        if (step == PartStep.Hour)
        {
            if (games.Count > 0) { sub = Lang.T($"{Name(games[0].App)} in front"); subIcon = IconOf(games[0].App); }
            else sub = Lang.T("No game in front");
        }
        else sub = PartReport.TimeSplit(s);

        var facts = new List<RichFact>();
        void Side(PartSide? side, string label, Brush brush)
        {
            if (PartReport.SideWords(side, Metric) is not var (avg, high)) return;
            facts.Add(new RichFact(label, avg, Wide: true, Icon: new RichIcon(Back: brush), Detail: high.Length > 0 ? high : null));
        }
        Side(s.Games, Lang.T("With a game in front"), full);
        Side(s.Rest, step == PartStep.Hour ? Lang.T("No game in front") : Lang.T("Everything else"), soft);
        var notes = games.Count > 0 && (step != PartStep.Hour || games.Count > 1)
            ? new List<RichNote> { new(Lang.T("Games"), [.. games.Take(5).Select(g => new RichItem(IconOf(g.App), Name(g.App), Units.Duration(g.Minutes * 60.0)))]) }
            : [];
        // A driver that came in this stretch (in the update colour), and an older part's stretch (faint, not in the usual).
        var driver = page.Report.Marks.FirstOrDefault(m => m.Kind == PartMarkKind.Driver && m.Slot == index);
        return new RichTip(title)
        {
            Sub = sub,
            SubIcon = subIcon,
            Facts = facts,
            Notes = notes,
            Mark = driver is null ? null : PartMarks.DayLine(driver, step),
            Aside = s.Old ? PartMarks.OldLine(Part, s.OldName) : null,
            Foot = Metric == PartMetric.Temperature ? page.Report.Usual.Words(page.Report.UsualPart) : null,
        };
    }

    private static List<PartAppView> AppsOf(PartPage page)
    {
        var list = new List<PartAppView>();
        foreach (var a in page.Report.Apps.Take(30))
        {
            string name = page.Names.TryGetValue(a.App, out var n) ? n : "";
            if (name.Length == 0) continue;
            string figures = string.Join(" · ", new[] { a.Load is double l ? $"{Math.Round(l):0}%" : null, a.Temp is double t ? Units.TempShort(t) : null }.OfType<string>());
            string power = a.Power is double p ? $"{Math.Round(p):0} W" : "–";
            string path = page.Paths.GetValueOrDefault(a.App) ?? "";
            list.Add(new PartAppView(name, path.Length > 0 ? path : null, name[..1].ToUpperInvariant(), figures, power, Units.Duration(a.Minutes * 60.0)));
        }
        return list;
    }

    // ── Export ──

    /// <summary>The summary for a period other than the one on screen, worked out by a page of its own that's never shown.</summary>
    internal static async Task<List<FactRow>> SummaryAsync(Part part, ReportService reports, LiveData live, SettingsModel settings, ReportRange unit, DateTime anchor)
    {
        var page = new PartViewModel(part, reports, live, settings) { _quiet = true };
        (page.Unit, page.Anchor) = (unit, anchor);
        page._quiet = false;
        await page.RefreshAsync();
        return page.SummaryFacts();
    }

    /// <summary>The page's figures as facts (the Export box's summary): each metric with a game in front and otherwise, then the apps.</summary>
    internal List<FactRow> SummaryFacts()
    {
        if (_page?.Report is not { HasAny: true } r) return [new(Title, Wording.NotRecorded, "")];
        var facts = new List<FactRow>();
        // Only the part counted: never added up across a new part.
        PartSide? Total(Func<PartSlot, PartSide?> pick) => r.Slots.Where(r.Counts).Select(pick).OfType<PartSide>().Aggregate((PartSide?)null, (a, s) => a is null ? s : a + s);
        var games = Total(s => s.Games);
        var rest = Total(s => s.Rest);
        foreach (var m in new[] { PartMetric.Temperature, PartMetric.Load, PartMetric.Power, PartMetric.Clock })
        {
            if (!r.Has(m)) continue;
            string label = m switch { PartMetric.Temperature => Lang.T("Temperature"), PartMetric.Load => Lang.T("Load"), PartMetric.Power => Lang.T("Power"), _ => Lang.T("Clock") };
            if (PartReport.SideWords(games, m) is var (ga, gh)) facts.Add(new(label, ga, Lang.T($"With a game in front, {gh}")));
            if (PartReport.SideWords(rest, m) is var (ra, rh)) facts.Add(new(label, ra, Lang.T($"Everything else, {rh}")));
        }
        foreach (var a in Apps.Take(10)) facts.Add(new(a.Name, a.Figures, $"{a.Power} · {a.Time}"));
        return facts;
    }
}
