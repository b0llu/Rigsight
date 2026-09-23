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

/// <summary>Live readings streamed from the agent: every sensor, per-app resource use, and current activity.</summary>
public sealed partial class LiveData : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly List<SensorItem> _flat = [];
    private readonly Dictionary<string, ProcRow> _procIndex = new(StringComparer.OrdinalIgnoreCase);

    public LiveData(SettingsModel settings)
    {
        _settings = settings;
        ProcsView = CollectionViewSource.GetDefaultView(Procs);
        ProcsView.SortDescriptions.Add(new SortDescription(nameof(ProcRow.MemMB), ListSortDirection.Descending));
        if (ProcsView is ICollectionViewLiveShaping live && live.CanChangeLiveSorting)
        {
            live.LiveSortingProperties.Add(nameof(ProcRow.MemMB));
            live.IsLiveSorting = true;
        }
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
    public int ChartWindowSeconds
    {
        get => _settings.Current.ChartWindowSeconds;
        set
        {
            _settings.Update(s => s.ChartWindowSeconds = value);
            OnPropertyChanged();
        }
    }

    public string SystemSummary => string.Join("  ·  ", new[] { CpuName, GpuName }.Where(n => n is not "CPU" and not "GPU"));

    // ── Activity ──────────────────────────────────────────────────────────
    [ObservableProperty] private ActivityInfo _activity = new();
    [ObservableProperty] private TodayInfo _today = new();
    [ObservableProperty] private ImageSource? _activityIcon;

    public string ActivityCaption => Activity switch
    {
        { Paused: true } => "TRACKING PAUSED",
        { Name: null } => "NOTHING IN FOCUS",
        { Present: false } => "AWAY",
        { Category: AppCategory.Game } => "NOW PLAYING",
        _ => "IN USE",
    };

    public string ActivitySession => Activity.SessionActiveSec > 0 ? $"for {Units.Duration(Activity.SessionActiveSec)}" : "";

    partial void OnActivityChanged(ActivityInfo? oldValue, ActivityInfo newValue)
    {
        if (oldValue?.Path != newValue.Path) ActivityIcon = IconCache.Get(newValue.Path);
        OnPropertyChanged(nameof(ActivityCaption));
        OnPropertyChanged(nameof(ActivitySession));
    }

    // ── Processes ─────────────────────────────────────────────────────────
    public ObservableCollection<ProcRow> Procs { get; } = [];
    public ICollectionView ProcsView { get; }
    [ObservableProperty] private bool _onlyWindowedApps;
    partial void OnOnlyWindowedAppsChanged(bool value) => ProcsView.Refresh();

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
        if (hello.Hardware is null) return;

        int total = hello.Hardware.Sum(h => h.Sensors.Count);
        bool sameSchema = total == _flat.Count &&
            hello.Hardware.SelectMany(h => h.Sensors).Select(s => s.Id).SequenceEqual(_flat.Select(s => s.Id));
        if (!sameSchema) Build(hello);

        // Pre-fill charts with the agent's recent history.
        if (hello.History is not null && hello.Keys is not null)
        {
            foreach (var series in hello.History)
            {
                if (!hello.Keys.TryGetValue(series.Key, out int index) || index >= _flat.Count) continue;
                var item = _flat[index];
                if (item.History.Count == 0) item.Seed(series.Times, series.Values);
            }
            Tick++;
        }
    }

    private void Build(AgentMessage hello)
    {
        _boardPruned = false;
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

        void AddSeries(string label, SensorItem? sensor, string hex)
        {
            if (sensor is not null) TempSeries.Add(new ChartSeries(label, sensor, (Color)ColorConverter.ConvertFromString(hex)));
        }
        AddSeries("CPU", CpuTemp, "#5B8CFF");
        AddSeries("GPU", GpuTemp, "#3DDC97");
        AddSeries("GPU hot spot", GpuHotSpot, "#FBBF24");
        AddSeries("GPU memory", GpuMemJunction, "#F472B6");

        HasHardware = _flat.Count > 0;
        ApplyFilter();
    }

    public void ApplyTick(AgentMessage tick)
    {
        if (tick.Values is { } values && values.Length == _flat.Count)
        {
            for (int i = 0; i < values.Length; i++)
                _flat[i].Push(tick.Time, values[i]);
            UpdateDerived();
        }
        if (tick.Activity is not null) Activity = tick.Activity;
        if (tick.Today is not null) Today = tick.Today;
        Tick++;
    }

    public void ApplyProcs(List<ProcInfo> procs)
    {
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
        double top = Procs.Count > 0 ? Procs.Max(p => p.MemMB) : 1;
        foreach (var p in Procs) p.MemShare = top > 0 ? p.MemMB / top * 100 : 0;
        if (OnlyWindowedApps) ProcsView.Refresh();
    }

    private bool _boardPruned;

    private void UpdateDerived()
    {
        // Once real values arrive, drop fan headers with nothing plugged in and sensors that read nonsense.
        if (!_boardPruned)
        {
            _boardPruned = true;
            foreach (var f in Fans.Where(f => f.Value is not > 0).ToList()) Fans.Remove(f);
            foreach (var t in BoardTemps.Where(t => t.Value is not (> 0 and < 115)).ToList()) BoardTemps.Remove(t);
        }

        if (RamUsed?.Value is double used)
        {
            var total = used + (RamAvailable?.Value ?? 0);
            RamText = $"{used:0.0} GB of {total:0.0} GB";
            RamTotalText = $"{total:0} GB";
        }

        if (GpuVramUsed?.Value is double vUsed && GpuVramTotal?.Value is double vTotal && vTotal > 0)
            GpuVramText = $"{vUsed / 1024:0.0} / {vTotal / 1024:0.0} GB";
        else if (GpuVramLoad?.Value is double vLoad)
            GpuVramText = $"{vLoad:0}%";
    }

    /// <summary>Re-applies names, hidden flags and units after settings changed.</summary>
    public void ApplySettings()
    {
        var s = _settings.Current;
        foreach (var item in _flat)
        {
            item.CustomLabel = s.SensorLabels.GetValueOrDefault(item.Id);
            item.IsHidden = s.HiddenSensors.Contains(item.Id);
            item.RefreshFormatting();
        }
        ApplyFilter();
        OnPropertyChanged(nameof(Activity));
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
        foreach (var node in Hardware.Where(n => n.HasVisibleSensors))
        {
            rows.Add(node);
            if (node.IsExpanded) rows.AddRange(node.Sensors.Where(s => s.IsShown));
            rows.Add(node.End);
        }
        SensorRows = rows;
    }

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

    [RelayCommand]
    private void ResetStats()
    {
        foreach (var s in _flat) s.ResetStats();
    }

    [RelayCommand]
    private static void ToggleHidden(SensorItem? item) => item?.ToggleHidden();

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
