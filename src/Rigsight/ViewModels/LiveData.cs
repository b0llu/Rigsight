using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>Live readings streamed from the agent: every sensor, per-app resource use, and today's totals.</summary>
public sealed partial class LiveData : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly List<SensorItem> _flat = [];
    private readonly Dictionary<string, SensorItem> _byKey = [];
    private readonly Dictionary<string, SensorItem> _byId = [];

    // Today's [lowest, highest] per sensor id from the agent, kept so sensors created later get them too.
    private readonly Dictionary<string, double[]> _extremes = [];
    private string? _extremesDay;
    private readonly Dictionary<string, ProcRow> _procIndex = new(StringComparer.OrdinalIgnoreCase);

    public LiveData(SettingsModel settings)
    {
        _settings = settings;
        ProcsView = CollectionViewSource.GetDefaultView(Procs);
        ProcsView.SortDescriptions.Add(new SortDescription(nameof(ProcRow.MemMB), ListSortDirection.Descending));
        if (ProcsView is ICollectionViewLiveShaping live && live.CanChangeLiveSorting)
            live.LiveSortingProperties.Add(nameof(ProcRow.MemMB));
        ProcsView.Filter = o => !OnlyWindowedApps || o is ProcRow { HasWindow: true };
    }

    public ObservableCollection<HardwareNode> Hardware { get; } = [];

    /// <summary>What the All sensors page lists: per card, a header, the rows that pass the filters, and an end marker.</summary>
    [ObservableProperty] private List<object> _sensorRows = [];

    [ObservableProperty] private bool _hasHardware;
    [ObservableProperty] private long _tick;

    // ── Dashboard picks ───────────────────────────────────────────────────
    [ObservableProperty] private string _cpuName = "CPU";
    [ObservableProperty] private SensorItem? _cpuTemp;
    [ObservableProperty] private SensorItem? _cpuDieTemp;
    [ObservableProperty] private SensorItem? _cpuLoad;
    [ObservableProperty] private SensorItem? _cpuPower;
    [ObservableProperty] private SensorItem? _cpuClock;
    [ObservableProperty] private SensorItem? _cpuVoltage;
    public ObservableCollection<SensorItem> CpuThreads { get; } = [];

    [ObservableProperty] private string _gpuName = "GPU";
    [ObservableProperty] private SensorItem? _gpuTemp;
    [ObservableProperty] private SensorItem? _gpuHotSpot;
    [ObservableProperty] private SensorItem? _gpuMemJunction;
    [ObservableProperty] private SensorItem? _gpuLoad;
    [ObservableProperty] private SensorItem? _gpuPower;
    [ObservableProperty] private SensorItem? _gpuClock;
    [ObservableProperty] private SensorItem? _gpuFan;
    [ObservableProperty] private SensorItem? _gpuVramLoad;
    [ObservableProperty] private SensorItem? _gpuVramUsed;
    [ObservableProperty] private SensorItem? _gpuVramTotal;
    [ObservableProperty] private string _gpuVramText = "";

    [ObservableProperty] private SensorItem? _ramLoad;
    [ObservableProperty] private SensorItem? _ramUsed;
    [ObservableProperty] private SensorItem? _ramAvailable;
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private string _ramTotalText = "";

    public ObservableCollection<DriveSummary> Drives { get; } = [];
    public ObservableCollection<SensorItem> Fans { get; } = [];
    public ObservableCollection<SensorItem> BoardTemps { get; } = [];
    public ObservableCollection<ChartSeries> TempSeries { get; } = [];
    /// <summary>300, 3600, 21600 or 86400 seconds up to now, or 0 for one calendar day (<see cref="ChartDay"/>).</summary>
    public int ChartWindowSeconds
    {
        get => _settings.Current.ChartWindowSeconds;
        set
        {
            _settings.Update(s => s.ChartWindowSeconds = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsChartDay));
            ChartRangeChanged?.Invoke();
        }
    }

    public bool IsChartDay => ChartWindowSeconds == 0;

    private DateTime _chartDay = DateTime.Today;

    /// <summary>The day the temperature chart shows in day mode (today: midnight to now; earlier: the whole day).</summary>
    public DateTime ChartDay
    {
        get => _chartDay;
        set
        {
            var day = value.Date > DateTime.Today ? DateTime.Today : value.Date;
            if (!SetProperty(ref _chartDay, day)) return;
            OnPropertyChanged(nameof(ChartDayLabel));
            OnPropertyChanged(nameof(CanChartNextDay));
            OnPropertyChanged(nameof(CanChartPreviousDay));
            ChartRangeChanged?.Invoke();
        }
    }

    /// <summary>First day with minute history (the date picker starts there).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChartPreviousDay))]
    private DateTime? _chartHistoryStart;

    public string ChartDayLabel => ReportsViewModel.PeriodText(Core.Reports.ReportRange.Day, ChartDay);
    public bool CanChartNextDay => ChartDay < DateTime.Today;
    public bool CanChartPreviousDay => ChartHistoryStart is not { } f || ChartDay > f;

    [RelayCommand]
    private void ChartPreviousDay()
    {
        if (CanChartPreviousDay) ChartDay = ChartDay.AddDays(-1);
    }

    [RelayCommand]
    private void ChartNextDay()
    {
        if (CanChartNextDay) ChartDay = ChartDay.AddDays(1);
    }

    /// <summary>The chart needs different minute history (another day, or back to the last 24 hours).</summary>
    public event Action? ChartRangeChanged;

    public string SystemSummary => string.Join("  ·  ", new[] { CpuName, GpuName }.Where(n => n is not "CPU" and not "GPU"));

    // ── Today ─────────────────────────────────────────────────────────────
    [ObservableProperty] private TodayInfo _today = new();

    // ── Processes ─────────────────────────────────────────────────────────
    public ObservableCollection<ProcRow> Procs { get; } = [];

    /// <summary>The six apps using the most memory (for the custom page tile).</summary>
    [ObservableProperty] private List<ProcRow> _topMemory = [];
    public ICollectionView ProcsView { get; }
    [ObservableProperty] private bool _onlyWindowedApps;
    partial void OnOnlyWindowedAppsChanged(bool value)
    {
        ProcsView.Refresh();
        UpdateBars();
    }

    // ── Sensors page filters ──────────────────────────────────────────────
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _typeFilter = "All";
    [ObservableProperty] private bool _showHidden;
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnTypeFilterChanged(string value) => ApplyFilter();
    partial void OnShowHiddenChanged(bool value) => ApplyFilter();

    // ── Agent messages ────────────────────────────────────────────────────

    public void LoadHello(AgentMessage hello)
    {
        // Today's totals arrive with the hello, before the agent has finished discovering the hardware.
        if (hello.Today is not null) Today = hello.Today;
        if (hello.Drives is not null) _driveHealth = hello.Drives;
        if (hello.Hardware is null) return;

        int total = hello.Hardware.Sum(h => h.Sensors.Count);
        bool sameSchema = total == _flat.Count &&
            hello.Hardware.SelectMany(h => h.Sensors).Select(s => s.Id).SequenceEqual(_flat.Select(s => s.Id));
        if (!sameSchema) Build(hello);
        foreach (var drive in Drives) drive.Health = _driveHealth.FirstOrDefault(h => h.Name == drive.Name);

        // Pre-fill charts with the agent's recent history.
        if (hello.History is not null && hello.Keys is not null)
        {
            foreach (var series in hello.History)
            {
                // Key sensors by key name; others (drive temperatures) as "id:<sensor id>".
                SensorItem? item = series.Key.StartsWith("id:", StringComparison.Ordinal)
                    ? _byId.GetValueOrDefault(series.Key[3..])
                    : hello.Keys.TryGetValue(series.Key, out int index) && index < _flat.Count ? _flat[index] : null;
                if (item is { History.Count: 0 }) item.Seed(series.Times, series.Values);
            }
            Tick++;
        }
    }

    private void Build(AgentMessage hello)
    {
        _ticksSinceBuild = 0;
        _flat.Clear();
        Hardware.Clear();
        CpuThreads.Clear();
        Drives.Clear();
        Fans.Clear();
        BoardTemps.Clear();
        TempSeries.Clear();

        var s = _settings.Current;
        foreach (var hw in hello.Hardware!)
        {
            var node = new HardwareNode(hw.Name, hw.Type) { IsExpanded = !s.CollapsedHardware.Contains(hw.Name) };
            foreach (var meta in hw.Sensors)
            {
                var item = new SensorItem(meta, hw.Name, hw.Type)
                {
                    CustomLabel = s.SensorLabels.GetValueOrDefault(meta.Id),
                    IsHidden = s.HiddenSensors.Contains(meta.Id),
                };
                item.UserPreferenceChanged += OnSensorPreferenceChanged;
                node.Sensors.Add(item);
                _flat.Add(item);
            }
            Hardware.Add(node);
        }

        SensorItem? K(string key) => hello.Keys is not null && hello.Keys.TryGetValue(key, out var i) && i < _flat.Count ? _flat[i] : null;
        CpuTemp = K(KeySensors.CpuTemp);
        CpuDieTemp = K(KeySensors.CpuDieTemp);
        CpuLoad = K(KeySensors.CpuLoad);
        CpuPower = K(KeySensors.CpuPower);
        CpuClock = K(KeySensors.CpuClock);
        CpuVoltage = K(KeySensors.CpuVoltage);
        GpuTemp = K(KeySensors.GpuTemp);
        GpuHotSpot = K(KeySensors.GpuHotSpot);
        GpuMemJunction = K(KeySensors.GpuMemJunction);
        GpuLoad = K(KeySensors.GpuLoad);
        GpuPower = K(KeySensors.GpuPower);
        GpuClock = K(KeySensors.GpuClock);
        GpuFan = K(KeySensors.GpuFan);
        GpuVramLoad = K(KeySensors.GpuVramLoad);
        GpuVramUsed = K(KeySensors.GpuVramUsed);
        GpuVramTotal = K(KeySensors.GpuVramTotal);
        RamLoad = K(KeySensors.RamLoad);
        RamUsed = K(KeySensors.RamUsed);
        RamAvailable = K(KeySensors.RamAvailable);
        VirtualLoad = _flat.FirstOrDefault(s => s.HardwareType == "Memory" && s.Kind == SensorKind.Load
                                                && s.HardwareName.Contains("Virtual", StringComparison.OrdinalIgnoreCase));

        CpuName = Hardware.FirstOrDefault(h => h.Type == "Cpu")?.Name ?? "CPU";
        GpuName = (Hardware.FirstOrDefault(h => h.Type is "GpuNvidia" or "GpuAmd") ?? Hardware.FirstOrDefault(h => h.IsGpu))?.Name ?? "GPU";
        OnPropertyChanged(nameof(SystemSummary));

        var cpu = Hardware.FirstOrDefault(h => h.Type == "Cpu");
        if (cpu is not null)
        {
            var threadName = new Regex(@"^CPU Core #\d+( Thread #\d+)?$");
            foreach (var t in cpu.Sensors.Where(x => x.Kind == SensorKind.Load && threadName.IsMatch(x.Name)))
                CpuThreads.Add(t);
        }

        foreach (var d in Hardware.Where(h => h.Type == "Storage"))
        {
            var temp = Find(d.Sensors, SensorKind.Temperature, "Composite Temperature", "Temperature")
                       ?? d.Sensors.FirstOrDefault(x => x.Kind == SensorKind.Temperature && !x.Name.Contains("Warning") && !x.Name.Contains("Critical"));
            Drives.Add(new DriveSummary(d.Name, temp, Find(d.Sensors, SensorKind.Level, "Life", "Remaining Life"),
                Find(d.Sensors, SensorKind.Load, "Used Space"), Find(d.Sensors, SensorKind.Factor, "Power On Hours")));
        }

        // Fans and temperatures from the motherboard's sensor chips. Fans that never spin are left out.
        foreach (var n in Hardware.Where(h => h.Type is "SuperIO" or "Motherboard" or "EmbeddedController" or "Cooler"))
        {
            foreach (var f in n.Sensors.Where(x => x.Kind == SensorKind.Fan)) Fans.Add(f);
            foreach (var t in n.Sensors.Where(x => x.Kind == SensorKind.Temperature)) BoardTemps.Add(t);
        }

        // Longer chart windows come from the minute history: CPU and GPU as the minute's average, the
        // hot spot and memory (only stored as the minute's highest) as that.
        void AddSeries(string label, SensorItem? sensor, string colorKey, Func<Rigsight.Core.Data.SystemMinute, double?> fromMinute)
        {
            if (sensor is null) return;
            var series = new ChartSeries(label, sensor, colorKey, fromMinute);
            series.LoadMinutes(_minutes);
            TempSeries.Add(series);
        }
        AddSeries("CPU", CpuTemp, "CpuColor", m => m.CpuTemp);
        AddSeries("GPU", GpuTemp, "GpuColor", m => m.GpuTemp);
        AddSeries("GPU hot spot", GpuHotSpot, "WarmColor", m => m.GpuHotMax);
        AddSeries("GPU memory", GpuMemJunction, "PinkColor", m => m.GpuMemMax);

        _byKey.Clear();
        if (hello.Keys is not null)
            foreach (var (key, index) in hello.Keys)
                if (index < _flat.Count) _byKey[key] = _flat[index];
        _byId.Clear();
        foreach (var item in _flat) _byId.TryAdd(item.Id, item);
        foreach (var (id, range) in _extremes)
            if (_byId.TryGetValue(id, out var item)) item.SetTodayRange(range[0], range[1]);

        HasHardware = _flat.Count > 0;
        ApplyFilter();
        SensorsRebuilt?.Invoke();
    }

    /// <summary>Raised when the sensor list was (re)built, so anything holding sensors can look them up again.</summary>
    public event Action? SensorsRebuilt;

    /// <summary>A sensor by id, or "key:&lt;name&gt;" for a well-known one (CPU load, GPU power…). Null until the agent has sent its sensors.</summary>
    public SensorItem? Resolve(string? reference) =>
        reference is null ? null
        : reference.StartsWith("key:", StringComparison.Ordinal) ? _byKey.GetValueOrDefault(reference[4..])
        : _byId.GetValueOrDefault(reference);

    public void ApplyTick(AgentMessage tick)
    {
        if (tick.Values is { } values && values.Length == _flat.Count)
        {
            for (int i = 0; i < values.Length; i++)
                _flat[i].Push(tick.Time, values[i]);
            UpdateDerived();
        }
        if (tick.Today is not null) Today = tick.Today;
        if (tick.Extremes is { } extremes) ApplyExtremes(extremes, tick.ExtremesDay, tick.ExtremesFull);
        Tick++;
    }

    /// <summary>Today's lowest and highest readings, which the agent tracks all day (even with the app closed).</summary>
    private void ApplyExtremes(Dictionary<string, double[]> extremes, string? day, bool full)
    {
        if (full || day != _extremesDay)
        {
            _extremes.Clear();
            _extremesDay = day;
        }
        foreach (var (id, range) in extremes)
        {
            if (range.Length != 2) continue;
            _extremes[id] = range;
            if (_byId.TryGetValue(id, out var item)) item.SetTodayRange(range[0], range[1]);
        }
    }

    private List<Rigsight.Core.Data.SystemMinute> _minutes = [];
    private List<DriveHealthInfo> _driveHealth = [];

    /// <summary>The last day of minute history, for the temperature chart's longer windows.</summary>
    public void LoadMinuteHistory(List<Rigsight.Core.Data.SystemMinute> minutes)
    {
        _minutes = minutes;
        foreach (var series in TempSeries) series.LoadMinutes(minutes);
        Tick++;
    }

    /// <summary>Asks the agent for each process of these apps (exe names joined by "|", or null for none).</summary>
    public event Action<string?>? ProcessDetailChanged;

    /// <summary>The apps opened to show their processes, as the agent's "procs-detail" argument.</summary>
    public string? ExpandedApps => Procs.Any(p => p.IsExpanded) ? string.Join('|', Procs.Where(p => p.IsExpanded).Select(p => p.Exe)) : null;

    [RelayCommand]
    private void ToggleProcesses(ProcRow row)
    {
        if (!row.IsExpanded && !row.CanExpand) return;
        row.IsExpanded = !row.IsExpanded;
        row.Children = [];
        ProcessDetailChanged?.Invoke(ExpandedApps);
    }

    /// <summary>Keep the process list sorted as memory changes, only while the Memory page shows it.</summary>
    public void SetProcessSorting(bool on)
    {
        if (ProcsView is not ICollectionViewLiveShaping { CanChangeLiveSorting: true } live || live.IsLiveSorting == on) return;
        live.IsLiveSorting = on;
        if (on) ProcsView.Refresh();
    }

    /// <summary>
    /// Memory Windows holds compressed (the "Memory Compression" process), as a sensor of its own so it gets a
    /// tile with a chart like the real ones. It's read with the process list, every couple of seconds.
    /// </summary>
    public SensorItem Compressed { get; } = new(new SensorMeta { Id = "/rigsight/compressed", Name = "Compressed", Kind = SensorKind.Data }, "Memory", "Memory");

    /// <summary>Virtual memory in use (Windows' commit charge against its limit), if the PC reports it.</summary>
    [ObservableProperty] private SensorItem? _virtualLoad;

    /// <summary>How the memory is split, as star widths for the bar under the memory card.</summary>
    [ObservableProperty] private System.Windows.GridLength _memAppsWidth = new(0, System.Windows.GridUnitType.Star);
    [ObservableProperty] private System.Windows.GridLength _memCompressedWidth = new(0, System.Windows.GridUnitType.Star);
    [ObservableProperty] private System.Windows.GridLength _memFreeWidth = new(1, System.Windows.GridUnitType.Star);
    [ObservableProperty] private string _memAppsText = "—";

    public void ApplyProcs(List<ProcInfo> procs)
    {
        // Parts of Windows itself (no ".exe": Memory Compression, Registry, System) aren't apps, and today's
        // totals leave them out too. Memory Compression holds other apps' memory, squeezed: it's shown with the
        // system's memory instead.
        // (Its process is "Memory Compression"; Task Manager's Details tab calls it MemCompression.)
        var compressed = procs.FirstOrDefault(p => p.Exe is "Memory Compression" or "MemCompression");
        Compressed.Push(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), compressed is null ? null : (float)(compressed.MemMB / 1024));
        UpdateMemorySplit();
        procs = [.. procs.Where(p => p.Exe.Contains('.'))];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in procs)
        {
            seen.Add(p.Exe);
            if (!_procIndex.TryGetValue(p.Exe, out var row))
            {
                row = new ProcRow(p.Exe);
                _procIndex[p.Exe] = row;
                row.Update(p);
                Procs.Add(row);
            }
            else
            {
                row.Update(p);
            }
        }
        for (int i = Procs.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Procs[i].Exe))
            {
                _procIndex.Remove(Procs[i].Exe);
                Procs.RemoveAt(i);
            }
        }
        // Rows update themselves; only replace the list (which rebuilds the tile's rows) when it changes.
        var top6 = Procs.OrderByDescending(p => p.MemMB).Take(6).ToList();
        if (!top6.SequenceEqual(TopMemory)) TopMemory = top6;
        double totalMB = ((RamUsed?.Value ?? 0) + (RamAvailable?.Value ?? 0)) * 1024;
        foreach (var p in Procs) p.MemShare = totalMB > 0 ? p.MemMB / totalMB * 100 : 0;
        if (OnlyWindowedApps) ProcsView.Refresh();
        UpdateBars();
    }

    /// <summary>
    /// Bars compare the apps with each other: the biggest one shown fills its bar and the rest are measured against
    /// it. (Next to all the memory every bar would be a sliver, since no one app comes close to filling it.)
    /// </summary>
    private void UpdateBars()
    {
        double biggest = Procs.Where(p => !OnlyWindowedApps || p.HasWindow).Select(p => p.MemMB).DefaultIfEmpty(0).Max();
        foreach (var p in Procs) p.Bar = biggest > 0 ? Math.Min(p.MemMB / biggest * 100, 100) : 0;
    }

    private int _ticksSinceBuild;

    /// <summary>Apps (in use, less what's compressed), compressed, available.</summary>
    private void UpdateMemorySplit()
    {
        if (RamUsed?.Value is not double used || RamAvailable?.Value is not double available) return;
        double compressed = Math.Min(Compressed.Value ?? 0, used);
        MemAppsWidth = new(used - compressed, System.Windows.GridUnitType.Star);
        MemCompressedWidth = new(compressed, System.Windows.GridUnitType.Star);
        MemFreeWidth = new(available, System.Windows.GridUnitType.Star);
        MemAppsText = $"{used - compressed:0.0} GB";
    }

    private void UpdateDerived()
    {
        // A few readings in (by then today's ranges have arrived too), drop fan headers that haven't spun
        // at all today (nothing plugged in) and board sensors that read nonsense. A fan that's merely
        // stopped right now (0 RPM fans at idle) stays.
        if (++_ticksSinceBuild == 3)
        {
            foreach (var f in Fans.Where(f => f.Value is not > 0 && f.Max is not > 0).ToList()) Fans.Remove(f);
            foreach (var t in BoardTemps.Where(t => t.Value is not (> 0 and < 115)).ToList()) BoardTemps.Remove(t);
        }

        if (RamUsed?.Value is double used)
        {
            var total = used + (RamAvailable?.Value ?? 0);
            RamText = $"{used:0.0} GB of {total:0.0} GB";
            RamTotalText = $"{total:0} GB";
        }
        UpdateMemorySplit();

        if (GpuVramUsed?.Value is double vUsed && GpuVramTotal?.Value is double vTotal && vTotal > 0)
            GpuVramText = $"{vUsed / 1024:0.0} / {vTotal / 1024:0.0} GB";
        else if (GpuVramLoad?.Value is double vLoad)
            GpuVramText = $"{vLoad:0}%";
    }

    /// <summary>Re-applies names, hidden flags and units after settings changed.</summary>
    private string? _appliedSensorSettings;

    /// <summary>Applies sensor names, hidden sensors and units, but only when one of them actually changed
    /// (settings change often, e.g. while dragging a slider, and this refreshes every sensor).</summary>
    public void ApplySettings()
    {
        var s = _settings.Current;
        string key = $"{s.UseFahrenheit}|{string.Join(",", s.SensorLabels.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value))}|{string.Join(",", s.HiddenSensors.Order())}|{_flat.Count}";
        if (key == _appliedSensorSettings) return;
        _appliedSensorSettings = key;
        foreach (var item in _flat)
        {
            item.CustomLabel = s.SensorLabels.GetValueOrDefault(item.Id);
            item.IsHidden = s.HiddenSensors.Contains(item.Id);
            item.RefreshFormatting();
        }
        ApplyFilter();
        Tick++;
    }

    private void OnSensorPreferenceChanged(SensorItem item)
    {
        _settings.Update(s =>
        {
            if (item.CustomLabel is null) s.SensorLabels.Remove(item.Id);
            else s.SensorLabels[item.Id] = item.CustomLabel;
            if (item.IsHidden) s.HiddenSensors.Add(item.Id);
            else s.HiddenSensors.Remove(item.Id);
        });
        ApplyFilter();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHiddenText), nameof(HiddenHint))]
    private int _hiddenCount;

    public string ShowHiddenText => HiddenCount == 1 ? "Show 1 hidden sensor" : $"Show {HiddenCount} hidden sensors";

    public string HiddenHint => HiddenCount == 0
        ? "Click a name to rename it. Click the eye to hide a sensor you don't care about."
        : $"{HiddenCount} sensor{(HiddenCount == 1 ? " is" : "s are")} hidden. Turn on “{ShowHiddenText}” to see {(HiddenCount == 1 ? "it" : "them")} dimmed, then click the eye again to bring {(HiddenCount == 1 ? "it" : "them")} back.";

    public void ApplyFilter()
    {
        HiddenCount = _flat.Count(s => s.IsHidden);
        if (HiddenCount == 0 && ShowHidden) ShowHidden = false;
        var query = SearchText?.Trim() ?? "";
        foreach (var node in Hardware)
        {
            bool nodeMatches = query.Length > 0 && node.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            bool any = false;
            foreach (var s in node.Sensors)
            {
                bool show = (ShowHidden || !s.IsHidden)
                            && MatchesType(s.Kind)
                            && (query.Length == 0 || nodeMatches || s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
                s.IsShown = show;
                any |= show;
            }
            node.HasVisibleSensors = any;
            int hidden = node.Sensors.Count(s => s.IsHidden);
            node.Summary = (node.Sensors.Count == 1 ? "1 sensor" : $"{node.Sensors.Count} sensors") + (hidden > 0 ? $"  ·  {hidden} hidden" : "");
        }
        RebuildRows();
    }

    private void RebuildRows()
    {
        var rows = new List<object>(_flat.Count + Hardware.Count * 2);
        foreach (var node in OrderedHardware().Where(n => n.HasVisibleSensors))
        {
            rows.Add(node);
            if (node.IsExpanded && !IsReordering) rows.AddRange(node.Sensors.Where(s => s.IsShown));
            rows.Add(node.End);
        }
        SensorRows = rows;
    }

    // ── Reordering the groups (drag a header) ──

    /// <summary>While a group is being dragged: every group shows just its header, so the whole list fits.</summary>
    [ObservableProperty] private bool _isReordering;

    partial void OnIsReorderingChanged(bool value) => RebuildRows();

    /// <summary>The groups in the order the user chose; ones not placed yet keep their usual order after them.</summary>
    private List<HardwareNode> OrderedHardware()
    {
        var order = _settings.Current.HardwareOrder;
        return [.. Hardware.Select((node, i) => (node, key: order.IndexOf(node.Name) is int k and >= 0 ? k : order.Count + i))
            .OrderBy(x => x.key).Select(x => x.node)];
    }

    /// <summary>Moves a group to where another one is (after it when coming from above, before it when from below).</summary>
    public void MoveHardware(HardwareNode node, HardwareNode target)
    {
        if (node == target) return;
        var list = OrderedHardware();
        int from = list.IndexOf(node), to = list.IndexOf(target);
        if (from < 0 || to < 0) return;
        list.RemoveAt(from);
        list.Insert(to, node);
        _settings.Update(s => s.HardwareOrder = [.. list.Select(n => n.Name)]);
        RebuildRows();
    }

    /// <summary>The group a row of the All sensors list belongs to.</summary>
    public HardwareNode? GroupOf(object? row) => row switch
    {
        HardwareNode node => node,
        SensorGroupEnd end => end.Owner,
        SensorItem item => Hardware.FirstOrDefault(n => n.Sensors.Contains(item)),
        _ => null,
    };

    [RelayCommand]
    private void ToggleExpanded(HardwareNode? node)
    {
        if (node is null) return;
        node.IsExpanded = !node.IsExpanded;
        SaveCollapsed();
        RebuildRows();
    }

    [RelayCommand]
    private void SetAllExpanded(string expand)
    {
        bool value = expand == "True";
        foreach (var n in Hardware) n.IsExpanded = value;
        SaveCollapsed();
        RebuildRows();
    }

    private void SaveCollapsed() =>
        _settings.Update(s => s.CollapsedHardware = [.. Hardware.Where(n => !n.IsExpanded).Select(n => n.Name)]);

    [RelayCommand]
    private void UnhideAll()
    {
        _settings.Update(s => s.HiddenSensors.Clear());
        foreach (var item in _flat) item.IsHidden = false;
        ApplyFilter();
    }

    private bool MatchesType(SensorKind k) => TypeFilter switch
    {
        "Temperature" => k == SensorKind.Temperature,
        "Load" => k == SensorKind.Load,
        "Fan" => k is SensorKind.Fan or SensorKind.Control,
        "Power" => k is SensorKind.Power or SensorKind.Current,
        "Clock" => k == SensorKind.Clock,
        "Voltage" => k == SensorKind.Voltage,
        _ => true,
    };

    /// <remarks>
    /// Takes object: the sensor list recycles rows, and while a row is being reused its button can
    /// briefly be bound to a card header instead of a sensor. Anything that isn't a sensor is ignored.
    /// </remarks>
    [RelayCommand]
    private static void ToggleHidden(object? item) => (item as SensorItem)?.ToggleHidden();

    public IReadOnlyList<SensorItem> AllSensors => _flat;

    private static SensorItem? Find(IEnumerable<SensorItem> sensors, SensorKind kind, params string[] names)
    {
        foreach (var name in names)
        {
            var match = sensors.FirstOrDefault(s => s.Kind == kind && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }
}
