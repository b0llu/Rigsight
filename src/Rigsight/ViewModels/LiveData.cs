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
        ProcsView.Filter = o => o is ProcRow p && Listed(p);
    }

    public ObservableCollection<HardwareNode> Hardware { get; } = [];

    /// <summary>What the All sensors page lists: per card, a header, the rows that pass the filters, and an end marker.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SensorsEmptyText))]
    private List<object> _sensorRows = [];

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

    /// <summary>Every graphics processor, the main one first (the one the GPU readings above, and history, are of).</summary>
    public ObservableCollection<GpuView> Gpus { get; } = [];

    /// <summary>The GPU the Temperatures page shows (its arrows switch between them).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpuPositionText))]
    private GpuView? _selectedGpu;

    public bool HasSeveralGpus => Gpus.Count > 1;
    public string GpuPositionText => SelectedGpu is null ? "" : $"GPU {Gpus.IndexOf(SelectedGpu) + 1} of {Gpus.Count}";

    [RelayCommand]
    private void NextGpu() => MoveGpu(1);

    [RelayCommand]
    private void PreviousGpu() => MoveGpu(-1);

    private void MoveGpu(int step)
    {
        if (Gpus.Count < 2 || SelectedGpu is null) return;
        SelectedGpu = Gpus[(Gpus.IndexOf(SelectedGpu) + step + Gpus.Count) % Gpus.Count];
    }

    [ObservableProperty] private SensorItem? _ramLoad;
    [ObservableProperty] private SensorItem? _ramUsed;
    [ObservableProperty] private SensorItem? _ramAvailable;
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private string _ramTotalText = "";

    public ObservableCollection<DriveSummary> Drives { get; } = [];
    public ObservableCollection<SensorItem> Fans { get; } = [];
    public ObservableCollection<SensorItem> BoardTemps { get; } = [];

    /// <summary>The Temperatures page's "At rest" rows: each chip settled and idle today, against its usual (see <see cref="Core.Reports.RestTemps"/>).</summary>
    public ObservableCollection<RestRow> Rest { get; } = [];

    /// <summary>Fills <see cref="Rest"/> from the days' temperatures at rest (heat_day).</summary>
    public void LoadRest(IEnumerable<Core.Data.HeatDay> days)
    {
        var (cpu, gpu) = Core.Reports.RestTemps.Of(days, DateTime.Today);
        Rest.Clear();
        Rest.Add(new RestRow("CPU", cpu));
        if (Gpus.Count > 0 || gpu.Today is not null || gpu.UsualLow is not null) Rest.Add(new RestRow("GPU", gpu));
    }
    public ObservableCollection<ChartSeries> TempSeries { get; } = [];

    /// <summary>
    /// What the temperature graph's line is drawn from: each minute's average ("Avg"), its highest reading ("High") or
    /// its lowest ("Low").
    /// </summary>
    public string ChartPlot
    {
        get => _settings.Current.ChartPlot is "High" or "Low" ? _settings.Current.ChartPlot : "Avg";
        set
        {
            if (value == ChartPlot) return;
            _settings.Update(s => s.ChartPlot = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChartLegendLabel));
            UpdatePeaks();
        }
    }

    /// <summary>What the choice is between, in front of it: a point of the line is a minute, an hour on a week or a month, a day on a year.</summary>
    public string ChartPlotLabel => IsChartYear ? "Each day's" : IsChartLong ? "Each hour's" : "Each minute's";

    /// <summary>There's a choice to make: under an hour every reading is drawn as it was.</summary>
    public bool CanChartPlot => ChartWindowSeconds is 0 or >= 3600;
    /// <summary>
    /// 300, 3600, 21600 or 86400 seconds up to now, 0 for one calendar day (<see cref="ChartDay"/>), or the week
    /// (604800), month (2592000) or year (31536000) that day is in: a week and a month are drawn hour by hour, a year
    /// day by day.
    /// </summary>
    public int ChartWindowSeconds
    {
        get => _settings.Current.ChartWindowSeconds;
        set
        {
            _settings.Update(s => s.ChartWindowSeconds = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanChartPlot));
            OnPropertyChanged(nameof(ChartPlotLabel));
            OnPropertyChanged(nameof(IsChartDay));
            OnPropertyChanged(nameof(IsChartLong));
            OnPropertyChanged(nameof(IsChartPaged));
            OnPropertyChanged(nameof(IsChartMonth));
            OnPropertyChanged(nameof(IsChartYear));
            OnPropertyChanged(nameof(ChartDayLabel));
            OnPropertyChanged(nameof(CanChartNextDay));
            OnPropertyChanged(nameof(CanChartPreviousDay));
            UpdatePeaks();
            ChartRangeChanged?.Invoke();
        }
    }

    public bool IsChartDay => ChartWindowSeconds == 0;

    /// <summary>A week, a month or a year: the chart is drawn from hours (a year: from days), not minutes.</summary>
    public bool IsChartLong => ChartWindowSeconds > 86400;

    /// <summary>The picker offers months (or years) to choose from, as on Reports, not days.</summary>
    public bool IsChartMonth => ChartUnit == Core.Reports.ReportRange.Month;
    public bool IsChartYear => ChartUnit == Core.Reports.ReportRange.Year;

    /// <summary>How long one point of the chart's older history stands for: a minute, an hour (week, month) or a day (year).</summary>
    public int ChartStepSeconds => ChartUnit switch { Core.Reports.ReportRange.Year => 86400, Core.Reports.ReportRange.Day => 60, _ => 3600 };

    /// <summary>A day, a week, a month or a year: one on the calendar, picked with the arrows and the date (the others end now).</summary>
    public bool IsChartPaged => IsChartDay || IsChartLong;

    /// <summary>What the arrows step by: the day, the Monday-to-Sunday week, the month or the year <see cref="ChartDay"/> is in.</summary>
    public Core.Reports.ReportRange ChartUnit => ChartWindowSeconds switch
    {
        604800 => Core.Reports.ReportRange.Week,
        2592000 => Core.Reports.ReportRange.Month,
        31536000 => Core.Reports.ReportRange.Year,
        _ => Core.Reports.ReportRange.Day,
    };

    /// <summary>The day, week or month the chart shows, from its first midnight to the one after its last day.</summary>
    public (DateTime From, DateTime To) ChartPeriod => Core.Reports.ReportBuilder.Bounds(ChartUnit, ChartDay);

    private DateTime _chartDay = DateTime.Today;

    /// <summary>
    /// The day the temperature chart shows in day mode (today: midnight to now; earlier: the whole day); for a week or
    /// a month, a day in the one shown.
    /// </summary>
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
            UpdatePeaks();
            ChartRangeChanged?.Invoke();
        }
    }

    /// <summary>Midnight passed with the window open: a chart on today moves to the new day; an earlier day keeps its place.</summary>
    public void NewDay(DateTime was)
    {
        if (_chartDay == was) { ChartDay = DateTime.Today; return; }
        OnPropertyChanged(nameof(ChartDayLabel));
        OnPropertyChanged(nameof(CanChartNextDay));
    }

    /// <summary>First day with minute history (the date picker starts there).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChartPreviousDay))]
    private DateTime? _chartHistoryStart;

    /// <summary>"Today", "Yesterday", "Last week", "5–11 Oct", "September 2026"…</summary>
    public string ChartDayLabel => ReportsViewModel.PeriodText(ChartUnit, ChartDay);
    // Forward while the one shown is over; back while it began after the history did.
    public bool CanChartNextDay => ChartPeriod.To <= DateTime.Today;
    public bool CanChartPreviousDay => ChartHistoryStart is not { } f || ChartPeriod.From > f;

    [RelayCommand]
    private void ChartPreviousDay()
    {
        if (CanChartPreviousDay) ChartDay = Core.Reports.ReportBuilder.Previous(ChartUnit, ChartDay);
    }

    [RelayCommand]
    private void ChartNextDay()
    {
        // Never past today (the 31st of last month steps to today, in this month).
        if (CanChartNextDay) ChartDay = Core.Reports.ReportBuilder.Next(ChartUnit, ChartDay);
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

    /// <summary>What's typed in the Memory page's search box: the live list shows the apps it matches.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoProcsMatch))]
    private string _procSearch = "";
    private string[] _procWords = [];

    /// <summary>Something is searched for and no app running matches it.</summary>
    public bool NoProcsMatch => _procWords.Length > 0 && !Procs.Any(Listed);

    /// <summary>Whether an app is in the live list: by the switch, and by what's searched for (its name, its program's
    /// name, or one of its processes where those are open).</summary>
    private bool Listed(ProcRow p) => (!OnlyWindowedApps || p.HasWindow)
        && (_procWords.Length == 0 || Core.TextMatch.Has(_procWords, [p.Name, p.Exe, .. p.Children.Select(c => c.Label)]));

    partial void OnProcSearchChanged(string value)
    {
        _procWords = Core.TextMatch.Words(value);
        ProcsView.Refresh();
        UpdateBars();
        OnPropertyChanged(nameof(NoProcsMatch));
    }

    partial void OnOnlyWindowedAppsChanged(bool value)
    {
        ProcsView.Refresh();
        UpdateBars();
        OnPropertyChanged(nameof(NoProcsMatch));
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
        if (hello.NetHistory is { Count: > 0 } net)
        {
            _netHistory.Clear();
            foreach (var n in net) AddNet(n);
        }
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
        var selectedGpu = SelectedGpu is { } was ? (was.Name, Gpus.Where(g => g.Name == was.Name).ToList().IndexOf(was)) : default;
        Gpus.Clear();
        Hardware.Clear();
        CpuThreads.Clear();
        Drives.Clear();
        Fans.Clear();
        BoardTemps.Clear();
        TempSeries.Clear();

        var s = _settings.Current;
        var named = new Dictionary<string, int>();
        foreach (var hw in hello.Hardware!)
        {
            int nth = named[hw.Name] = named.GetValueOrDefault(hw.Name) + 1;
            string key = nth == 1 ? hw.Name : $"{hw.Name} #{nth}";
            var node = new HardwareNode(hw.Name, hw.Type) { Key = key, IsExpanded = !s.CollapsedHardware.Contains(key) };
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
            node.BuildKeys();
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
        // Each GPU found the way the agent finds its main one (the same code), so the first is the one it records.
        var candidates = new List<KeySensors.Candidate>();
        int flatIndex = 0;
        for (int hw = 0; hw < hello.Hardware!.Count; hw++)
            foreach (var meta in hello.Hardware[hw].Sensors)
                candidates.Add(new KeySensors.Candidate(flatIndex++, hello.Hardware[hw].Type, hello.Hardware[hw].Name, meta.Name, meta.Kind, hw));
        foreach (var gpu in KeySensors.Gpus(candidates, hello.PreferredGpus))
            Gpus.Add(new GpuView(gpu.Name, gpu.Integrated, key => gpu.Keys.TryGetValue(key, out int i) && i < _flat.Count ? _flat[i] : null));
        // The same GPU as before a rebuild (by name, and which of several alike), else the main one.
        SelectedGpu = selectedGpu.Name is { } keepName ? Gpus.Where(g => g.Name == keepName).ElementAtOrDefault(selectedGpu.Item2) ?? Gpus.FirstOrDefault() : Gpus.FirstOrDefault();
        OnPropertyChanged(nameof(HasSeveralGpus));
        GpuName = Gpus.FirstOrDefault()?.Name ?? "GPU";
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

        // A line's colour says which part it is, so none is green, amber or red: those say how a reading is doing (the
        // numbers above the chart), and a green or amber line under them read as "good" or "warning".
        // Longer chart windows come from the minute history: CPU and GPU as the minute's average, the
        // hot spot and memory (only stored as the minute's highest) as that.
        // Hovering a minute says its average, highest and lowest, as far as they were kept.
        void AddSeries(string label, SensorItem? sensor, string colorKey, Func<Rigsight.Core.Data.SystemMinute, double?> fromMinute,
            Func<Rigsight.Core.Data.SystemMinute, (double? Avg, double? High, double? Low)> stats)
        {
            if (sensor is null) return;
            var series = new ChartSeries(label, sensor, colorKey, fromMinute, stats);
            series.LoadMinutes(_minutes, _minuteStep);
            TempSeries.Add(series);
        }
        AddSeries("CPU", CpuTemp, "CpuColor", m => m.CpuTemp, m => (m.CpuTemp, m.CpuTempMax, m.CpuTempMin));
        AddSeries("GPU", GpuTemp, "GpuColor", m => m.GpuTemp, m => (m.GpuTemp, m.GpuTempMax, m.GpuTempMin));
        // The hot spot's and the memory's line is their average too, where it was kept; before that, their highest.
        AddSeries("GPU hot spot", GpuHotSpot, "PurpleColor", m => m.GpuHotAvg ?? m.GpuHotMax, m => (m.GpuHotAvg, m.GpuHotMax, null));
        AddSeries("GPU memory", GpuMemJunction, "PinkColor", m => m.GpuMemAvg ?? m.GpuMemMax, m => (m.GpuMemAvg, m.GpuMemMax, null));
        UpdatePeaks();

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

    // ── The internet ──────────────────────────────────────────────────────

    /// <summary>The internet this second: the connection's speed and each app's (null: not read, as without admin rights).</summary>
    public NetLive? Net { get; private set; }

    /// <summary>The connection's speed over the last minute, oldest first (bytes a second).</summary>
    public IReadOnlyList<NetLive> NetHistory => _netHistory;
    private readonly List<NetLive> _netHistory = [];

    /// <summary>How many seconds the live network chart shows.</summary>
    public const int NetHistorySeconds = 60;

    private void AddNet(NetLive net)
    {
        if (_netHistory.Count > 0 && _netHistory[^1].Time >= net.Time) return;
        _netHistory.Add(new NetLive { Time = net.Time, Down = net.Down, Up = net.Up });
        while (_netHistory.Count > NetHistorySeconds) _netHistory.RemoveAt(0);
    }

    /// <summary>
    /// The agent has gone (quit, crashed, restarting): the last readings aren't "right now" any more. Every reading
    /// shows as none (a gap on its chart, as it is) and the internet's speeds and apps go, where they used to stay
    /// frozen on whatever the last second happened to be.
    /// </summary>
    public void AgentGone()
    {
        Net = null;
        _netHistory.Clear();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var item in _flat) item.Push(now, null);
        if (_flat.Count > 0) UpdateDerived();
        Tick++;
    }

    public void ApplyTick(AgentMessage tick)
    {
        if (tick.Net is { } net)
        {
            Net = net;
            AddNet(net);
        }
        if (tick.Values is { } values && values.Length == _flat.Count)
        {
            for (int i = 0; i < values.Length; i++)
                _flat[i].Push(tick.Time, values[i]);
            foreach (var node in Hardware) node.RefreshKeys();
            UpdateGlance();
            UpdateDerived();
            RaisePeaks(tick.Time);
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
    private int _minuteStep = 60;
    private List<DriveHealthInfo> _driveHealth = [];

    /// <summary>
    /// The last day of minute history, for the temperature chart's longer windows; or, with
    /// <paramref name="stepSeconds"/> 3600, the hours of its week or month.
    /// </summary>
    private IReadOnlyList<Core.Stability.PowerEvent>? _power;

    /// <summary>
    /// Why a stretch of the temperature graph has nothing recorded (its ends in Unix milliseconds), from what Windows
    /// logged about the PC starting, going down and sleeping. Unknown until that has been read, and where it doesn't say.
    /// </summary>
    public Func<long, long, Core.Stability.GapReason> ChartGapReason => (from, to) => Core.Stability.PowerLog.Reason(
        DateTimeOffset.FromUnixTimeMilliseconds(from).LocalDateTime, DateTimeOffset.FromUnixTimeMilliseconds(to).LocalDateTime, _power,
        TimeSpan.FromSeconds(Math.Max(120, _minuteStep)));

    /// <summary>What Windows logged about the PC's power over the time the graph shows (null: couldn't be read).</summary>
    public void LoadPowerEvents(IReadOnlyList<Core.Stability.PowerEvent>? events)
    {
        _power = events;
        Tick++;
    }

    public void LoadMinuteHistory(List<Rigsight.Core.Data.SystemMinute> minutes, int stepSeconds = 60)
    {
        _minutes = minutes;
        _minuteStep = stepSeconds;
        foreach (var series in TempSeries) series.LoadMinutes(minutes, stepSeconds);
        UpdatePeaks();
        Tick++;
    }

    /// <summary>
    /// What the temperature graph's legend says of each line over the day, week, month or year shown: the same thing
    /// the graph's points are (<see cref="ChartPlot"/>), for the whole period. Its highest and when ("HIGHEST  CPU
    /// 84.2 °C  Sat 12 Sep, 9 PM"), its lowest and when, or its average: finding a month's highest meant hovering along
    /// the whole graph. None on a window that ends now (the legend says the reading now), nor while the history loaded
    /// is still another range's.
    /// </summary>
    private void UpdatePeaks()
    {
        var (from, to) = ChartPeriod;
        long fromMs = new DateTimeOffset(from).ToUnixTimeMilliseconds(), toMs = new DateTimeOffset(to).ToUnixTimeMilliseconds();
        foreach (var series in TempSeries)
        {
            if (!IsChartPaged || series.StepSeconds != ChartStepSeconds) SetPeak(series, null);
            else if (ChartPlot == "Avg") SetPeak(series, series.Average(fromMs, toMs) is { } average ? (average, 0, 0) : null);
            // A stored point is named by its start ("9 PM" is 9 to 10), as the hover box names it.
            else SetPeak(series, (ChartPlot == "High" ? series.Highest(fromMs, toMs) : series.Lowest(fromMs, toMs)) is { } b
                ? (b.Value, b.At, b.Stored ? b.At - series.StepSeconds * 500L : b.At) : null);
        }
    }

    /// <summary>The word in front of the legend's figures: what they are, as the graph's choice goes.</summary>
    public string ChartLegendLabel => ChartPlot switch { "High" => "HIGHEST", "Low" => "LOWEST", _ => "AVERAGE" };

    private void SetPeak(ChartSeries series, (double Value, long At, long Named)? peak)
    {
        series.Peak = peak is { } p ? (p.Value, p.At) : null;
        series.PeakText = Units.Format(SensorKind.Temperature, peak?.Value);
        if (peak is not { At: > 0 } at) { series.PeakWhen = ""; return; }
        var local = DateTimeOffset.FromUnixTimeMilliseconds(at.Named).LocalDateTime;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        series.PeakWhen = IsChartYear ? local.ToString("ddd d MMM", culture)
            : IsChartLong ? local.ToString("ddd d MMM, h tt", culture)
            : local.ToString("h:mm tt", culture);
    }

    /// <summary>
    /// A reading that has just arrived, in a period still going on: the highest (lowest) so far if it passes it. An
    /// average waits for the history to be read again.
    /// </summary>
    private void RaisePeaks(long time)
    {
        if (!IsChartPaged || ChartPlot == "Avg" || ChartPeriod.To <= DateTime.Now) return;
        bool high = ChartPlot == "High";
        foreach (var series in TempSeries)
        {
            if (series.Sensor.Value is not double v || series.StepSeconds != ChartStepSeconds) continue;
            // With no lowest yet there may be none to say (a line whose lowest readings aren't kept).
            if (series.Peak is { } peak ? (high ? v > peak.Value : v < peak.Value) : high) SetPeak(series, (v, time, time));
        }
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
        // Sorted again only if the order moved while it wasn't kept (reading the list anew rebuilds every row on screen).
        if (on && !ProcsView.Cast<ProcRow>().Select(p => p.MemMB).SequenceEqual(ProcsView.Cast<ProcRow>().Select(p => p.MemMB).OrderDescending())) ProcsView.Refresh();
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
        if (OnlyWindowedApps || _procWords.Length > 0)
        {
            ProcsView.Refresh();
            OnPropertyChanged(nameof(NoProcsMatch));
        }
        UpdateBars();
    }

    /// <summary>
    /// Bars compare the apps with each other: the biggest one shown fills its bar and the rest are measured against
    /// it. (Next to all the memory every bar would be a sliver, since no one app comes close to filling it.)
    /// </summary>
    private void UpdateBars()
    {
        double biggest = Procs.Where(Listed).Select(p => p.MemMB).DefaultIfEmpty(0).Max();
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
        foreach (var gpu in Gpus) gpu.Refresh();
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
    [NotifyPropertyChangedFor(nameof(ShowHiddenText))]
    private int _hiddenCount;

    public string ShowHiddenText => HiddenCount == 1 ? "Show 1 hidden sensor" : $"Show {HiddenCount} hidden sensors";

    /// <summary>The sensors a widget, a dashboard, the overlay or the taskbar offers to add: all but the hidden ones
    /// (one already added stays where it is).</summary>
    [ObservableProperty] private IReadOnlyList<SensorItem> _pickableSensors = [];

    /// <summary>What the page says in place of an empty list; nothing while there are rows.</summary>
    public string? SensorsEmptyText =>
        SensorRows.Count > 0 ? null
        : _flat.Count == 0 ? "No sensors yet"
        : Core.TextMatch.Words(SearchText).Length > 0 ? "No sensors match"
        : TypeFilter != "All" ? "No sensors of this kind"
        : "Every sensor is hidden";

    // ── What stands out right now: the pane beside the list, read from every sensor, nothing to pick ──

    /// <summary>The most rows a part of the pane shows.</summary>
    internal const int GlanceRows = 4;

    /// <summary>The warmest parts, each by the reading of its that is nearest its limit, nearest first.</summary>
    public ObservableCollection<GlanceRow> Warmest { get; } = [];
    /// <summary>The main loads (CPU, GPU, video memory, memory), busiest first.</summary>
    public ObservableCollection<GlanceRow> Hardest { get; } = [];
    /// <summary>The fans that are spinning, fastest first.</summary>
    public ObservableCollection<GlanceRow> FastestFans { get; } = [];
    /// <summary>What the CPU and the graphics card draw, and (two of them) the sum.</summary>
    public ObservableCollection<GlanceRow> PowerDraw { get; } = [];
    [ObservableProperty] private string _powerNote = "";

    private void UpdateGlance()
    {
        // Warmest: a temperature is set against the limit its own part reports where it reports one (a drive's
        // "Warning Temperature"), else against the step it turns red at here. One reading a part, the nearest its
        // limit. A board chip's readings ("Temperature #3") aren't ranked unless given a name: nobody knows what an
        // unnamed one measures.
        var warm = new List<(SensorItem Sensor, double Share, string Note)>();
        foreach (var node in Hardware)
        {
            bool board = node.Badge == "BOARD";
            double? reported = node.Sensors.FirstOrDefault(x => x.Kind == SensorKind.Temperature && x.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase))?.Value;
            (SensorItem Sensor, double Share, string Note)? best = null;
            foreach (var sensor in node.Sensors)
            {
                if (!sensor.HasTempScale || sensor.IsHidden || sensor.Value is not double v || v <= 0) continue;
                if (board && sensor.CustomLabel is null) continue;
                double limit = reported is > 0 ? reported.Value : double.Parse(sensor.TempScale!.Split(',')[2], System.Globalization.CultureInfo.InvariantCulture);
                if (best is null || v / limit > best.Value.Share)
                    best = (sensor, v / limit, reported is > 0 ? $"{node.Name} · its limit is {Units.TempShort(reported)}" : node.Name);
            }
            if (best is { } found) warm.Add(found);
        }
        Fill(Warmest, warm.OrderByDescending(x => x.Share).Take(GlanceRows).Select(x => (x.Sensor, (double?)x.Share, x.Note)));
        Fill(Hardest, new[] { CpuLoad, GpuLoad, GpuVramLoad, RamLoad }.Where(x => x?.Value is not null && !x.IsHidden).Select(x => x!)
            .OrderByDescending(x => x.Value).Select(x => (x, (double?)(x.Value!.Value / 100), x.HardwareName)));
        Fill(FastestFans, _flat.Where(x => x.Kind == SensorKind.Fan && !x.IsHidden && x.Value > 0).OrderByDescending(x => x.Value).Take(GlanceRows)
            .Select(x => (x, (double?)null, x.HardwareName)));
        var power = new[] { CpuPower, GpuPower }.Where(x => x?.Value is > 0 && !x.IsHidden).Select(x => x!).OrderByDescending(x => x.Value).ToList();
        Fill(PowerDraw, power.Select(x => (x, (double?)null, x.HardwareName)));
        PowerNote = power.Count > 1 ? $"{Units.Format(SensorKind.Power, power.Sum(x => x.Value!.Value))} together" : "";
    }

    /// <summary>Rows kept and given what they now show (a list made anew each second would be built anew each second).</summary>
    private static void Fill(ObservableCollection<GlanceRow> rows, IEnumerable<(SensorItem Sensor, double? Bar, string Note)> items)
    {
        int i = 0;
        foreach (var (sensor, bar, note) in items)
        {
            if (i >= rows.Count) rows.Add(new GlanceRow());
            var row = rows[i++];
            row.Sensor = sensor;
            row.Bar = Math.Clamp(bar ?? 0, 0, 1);
            row.HasBar = bar is not null;
            row.Note = note;
        }
        while (rows.Count > i) rows.RemoveAt(rows.Count - 1);
    }

    /// <summary>A line of the pane was clicked: the list shows that sensor (its part opened, any search let go) and
    /// the page scrolls to its row, which stays lit.</summary>
    public event Action<SensorItem>? Revealed;

    private SensorItem? _lit;

    [RelayCommand]
    private void Reveal(GlanceRow? row)
    {
        if (row?.Sensor is not { } sensor || GroupOf(sensor) is not { } node) return;
        if (SearchText.Length > 0) SearchText = "";
        if (TypeFilter != "All") TypeFilter = "All";
        if (!node.IsExpanded)
        {
            node.IsExpanded = true;
            SaveCollapsed();
            RebuildRows();
        }
        if (_lit is not null) _lit.IsSelected = false;
        (_lit = sensor).IsSelected = true;
        Revealed?.Invoke(sensor);
    }

    // ── A sensor's menu (the "more" at the end of its row): where it is shown, copying its value ──

    /// <summary>The user's dashboards, for "Show it on" (the shell gives them; none in a page shown on its own).</summary>
    public Func<IReadOnlyList<CustomPageViewModel>>? Dashboards { get; set; }

    /// <summary>Where <paramref name="sensor"/> can be shown, and whether it is: the taskbar, the overlay, each widget
    /// that is on and takes its kind of reading, each dashboard.</summary>
    public IReadOnlyList<SensorPlace> PlacesFor(SensorItem sensor)
    {
        var places = new List<SensorPlace>();
        var set = _settings.Current;
        places.Add(new SensorPlace("The taskbar", set.TraySensors.Contains(sensor.Id), true,
            () => _settings.Update(x => { if (!x.TraySensors.Remove(sensor.Id)) x.TraySensors.Add(sensor.Id); })));
        bool onOverlay = set.Overlay.Sensors.Any(o => o.Id == sensor.Id);
        places.Add(new SensorPlace("The overlay", onOverlay, onOverlay || set.Overlay.Sensors.Count < OverlaySettings.MaxSensors,
            () => _settings.Update(x =>
            {
                if (x.Overlay.Sensors.RemoveAll(o => o.Id == sensor.Id) == 0 && x.Overlay.Sensors.Count < OverlaySettings.MaxSensors)
                    x.Overlay.Sensors.Add(new OverlaySensor { Id = sensor.Id });
            })));
        // Each widget that is on and can show this kind of reading (a gauge takes temperatures and percentages).
        foreach (var w in set.Widgets.Where(w => w.Enabled))
        {
            var layout = WidgetCatalog.LayoutOf(w);
            if (!WidgetCatalog.AllowsSensor(layout, sensor.Kind)) continue;
            string item = WidgetCatalog.SensorPrefix + sensor.Id, widgetId = w.Id;
            var items = WidgetCatalog.ItemsOf(w);
            bool onWidget = items.Any(i => i.Id == item);
            places.Add(new SensorPlace($"Widget · {WidgetCatalog.Title(w)}", onWidget, onWidget || items.Count < WidgetCatalog.MaxItems(layout),
                () => _settings.Update(x =>
                {
                    if (x.Widgets.FirstOrDefault(v => v.Id == widgetId) is not { } target) return;
                    // A built-in widget gets its own copy of its usual readings first, as on the Widgets page.
                    var list = WidgetCatalog.ItemsOf(target).Select(i => new WidgetItem { Id = i.Id, Label = i.Label }).ToList();
                    if (list.RemoveAll(i => i.Id == item) == 0) list.Add(new WidgetItem { Id = item });
                    target.Items = list;
                    WidgetCatalog.Clean(target);
                })));
        }
        foreach (var page in Dashboards?.Invoke() ?? [])
        {
            places.Add(new SensorPlace($"Dashboard · {page.Name}", page.Tiles.Any(t => t.SensorRef == sensor.Id), true, () =>
            {
                if (page.Tiles.FirstOrDefault(t => t.SensorRef == sensor.Id) is { } tile) page.Remove(tile);
                else
                {
                    page.SensorToAdd = sensor;
                    page.AddSensorTileCommand.Execute(null);
                }
            }));
        }
        return places;
    }

    /// <summary>Puts text on the clipboard (tests replace this).</summary>
    internal Action<string> SetClipboard { get; set; } = text => System.Windows.Clipboard.SetText(text);

    /// <summary>Copies a sensor's reading as it is shown ("49.0 °C"). False when the clipboard is held by another program.</summary>
    public bool Copy(SensorItem sensor)
    {
        try
        {
            SetClipboard(sensor.FormattedValue);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void ApplyFilter()
    {
        HiddenCount = _flat.Count(s => s.IsHidden);
        if (HiddenCount == 0 && ShowHidden) ShowHidden = false;
        var pickable = _flat.Where(s => !s.IsHidden).ToList();
        if (!pickable.SequenceEqual(PickableSensors)) PickableSensors = pickable;
        // Every word typed must be in the sensor's name (the one given to it or its own), its part's name or its tag.
        string[] words = Core.TextMatch.Words(SearchText);
        foreach (var node in Hardware)
        {
            bool any = false;
            foreach (var s in node.Sensors)
            {
                bool show = (ShowHidden || !s.IsHidden)
                            && MatchesType(s.Kind)
                            && Core.TextMatch.Has(words, s.DisplayName, s.Name, node.Name, node.Badge);
                s.IsShown = show;
                any |= show;
            }
            node.HasVisibleSensors = any;
            int hidden = node.Sensors.Count(s => s.IsHidden);
            node.Summary = (node.Sensors.Count == 1 ? "1 sensor" : $"{node.Sensors.Count} sensors") + (hidden > 0 ? $"  ·  {hidden} hidden" : "");
        }
        RebuildRows();
        UpdateGlance();
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
        return [.. Hardware.Select((node, i) => (node, key: order.IndexOf(node.Key) is int k and >= 0 ? k : order.Count + i))
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
        _settings.Update(s => s.HardwareOrder = [.. list.Select(n => n.Key)]);
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
        _settings.Update(s => s.CollapsedHardware = [.. Hardware.Where(n => !n.IsExpanded).Select(n => n.Key)]);

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

/// <summary>A line of the pane beside the All sensors list: a sensor, what is said under its name, and a bar (0 to 1).</summary>
public sealed partial class GlanceRow : ObservableObject
{
    [ObservableProperty] private SensorItem? _sensor;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private double _bar;
    [ObservableProperty] private bool _hasBar;
}

/// <summary>Somewhere a sensor can be shown (the taskbar, the overlay, a widget, a dashboard), and whether it is there now.</summary>
public sealed partial class SensorPlace(string name, bool isShown, bool canToggle, Action toggle) : ObservableObject
{
    public string Name { get; } = name;
    public bool IsShown { get; } = isShown;
    /// <summary>False when it can't be added: the place is full.</summary>
    public bool CanToggle { get; } = canToggle;

    [RelayCommand]
    private void Toggle()
    {
        if (CanToggle) toggle();
    }
}
