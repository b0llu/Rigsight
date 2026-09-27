using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A common reading offered with one click ("CPU temperature").</summary>
public sealed record TaskbarQuickPick(string Label, SensorItem Sensor);

/// <summary>A sensor in the taskbar: what it is, and its live reading for the preview.</summary>
public sealed class TaskbarSensorRow(string id, string name, string hardware, SensorItem? sensor)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Hardware { get; } = sensor is null ? "Not found on this PC right now" : hardware;
    public SensorItem? Sensor { get; } = sensor;
    public bool Found => Sensor is not null;
}

/// <summary>
/// The Taskbar page: readings shown next to the clock, an icon each or all in one (the agent draws them, see its
/// TrayReadings). As many as the user likes, in their order.
/// </summary>
public sealed partial class TaskbarViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly LiveData _live;

    public TaskbarViewModel(SettingsModel settings, LiveData live)
    {
        _settings = settings;
        _live = live;
        live.SensorsRebuilt += LoadSensors;
        settings.Changed += OnSettingsChanged;
        LoadSensors();
    }

    private void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(Combined));
        OnPropertyChanged(nameof(Separate));
        LoadSensors();
    }

    /// <summary>Every sensor on this PC, for the picker.</summary>
    public IReadOnlyList<SensorItem> AllSensors => _live.AllSensors;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSensorCommand))]
    private SensorItem? _sensorToAdd;

    public ObservableCollection<TaskbarSensorRow> Sensors { get; } = [];
    public bool HasSensors => Sensors.Count > 0;

    /// <summary>"3 in the taskbar".</summary>
    public string CountText => Sensors.Count switch { 0 => "", 1 => "1 in the taskbar", var n => $"{n} in the taskbar" };

    /// <summary>The usual ones, one click each (those already in the taskbar, or missing on this PC, left out).</summary>
    public IReadOnlyList<TaskbarQuickPick> QuickPicks =>
    [
        .. new (string Label, SensorItem? Sensor)[]
            {
                ("CPU temperature", _live.CpuTemp), ("GPU temperature", _live.GpuTemp), ("CPU load", _live.CpuLoad),
                ("GPU load", _live.GpuLoad), ("RAM in use", _live.RamLoad), ("GPU power", _live.GpuPower),
            }
            .Where(p => p.Sensor is not null && !Sensors.Any(r => r.Id == p.Sensor.Id))
            .Select(p => new TaskbarQuickPick(p.Label, p.Sensor!)),
    ];

    public bool HasQuickPicks => QuickPicks.Count > 0;

    /// <summary>Whether Windows' taskbar is light (its own setting, apart from apps'), so the preview looks like it.</summary>
    public bool TaskbarLight { get; } = ReadTaskbarLight();

    private static bool ReadTaskbarLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    [RelayCommand]
    private void AddQuick(TaskbarQuickPick pick)
    {
        _settings.Update(s =>
        {
            if (!s.TraySensors.Contains(pick.Sensor.Id)) s.TraySensors.Add(pick.Sensor.Id);
        });
        LoadSensors();
    }

    /// <summary>All readings in one icon (two at a time, taking turns) instead of an icon each.</summary>
    public bool Combined
    {
        get => _settings.Current.TrayCombined;
        set
        {
            if (value == Combined) return;
            _settings.Update(s => s.TrayCombined = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Separate));
            OnPropertyChanged(nameof(CombinedNote));
        }
    }

    public bool Separate
    {
        get => !Combined;
        set => Combined = !value;
    }

    /// <summary>What the combined icon shows first (the preview): up to two readings, stacked.</summary>
    public IReadOnlyList<TaskbarSensorRow> CombinedPreview => [.. Sensors.Where(r => r.Found).Take(2)];

    /// <summary>With more than two readings the combined icon takes turns; the rest are on hover.</summary>
    public string CombinedNote => Combined && Sensors.Count > 2
        ? $"Shows two at a time, taking turns every 3 seconds. Point at it to see all {Sensors.Count}."
        : "";

    private bool CanAddSensor() => SensorToAdd is not null && Sensors.All(r => r.Id != SensorToAdd.Id);

    [RelayCommand(CanExecute = nameof(CanAddSensor))]
    private void AddSensor()
    {
        if (SensorToAdd is not { } sensor) return;
        _settings.Update(s =>
        {
            if (!s.TraySensors.Contains(sensor.Id)) s.TraySensors.Add(sensor.Id);
        });
        SensorToAdd = null;
        LoadSensors();
    }

    [RelayCommand]
    private void RemoveSensor(TaskbarSensorRow row)
    {
        _settings.Update(s => s.TraySensors.Remove(row.Id));
        LoadSensors();
    }

    [RelayCommand]
    private void MoveSensorUp(TaskbarSensorRow row) => MoveSensor(row, -1);

    [RelayCommand]
    private void MoveSensorDown(TaskbarSensorRow row) => MoveSensor(row, 1);

    private void MoveSensor(TaskbarSensorRow row, int by)
    {
        _settings.Update(s =>
        {
            var list = s.TraySensors;
            int i = list.IndexOf(row.Id), j = i + by;
            if (i < 0 || j < 0 || j >= list.Count) return;
            (list[i], list[j]) = (list[j], list[i]);
        });
        LoadSensors();
    }

    /// <summary>Rebuilds the list from settings when it changed (or the sensors behind it did).</summary>
    public void LoadSensors()
    {
        var wanted = _settings.Current.TraySensors;
        var byId = new Dictionary<string, SensorItem>();
        foreach (var item in _live.AllSensors) byId.TryAdd(item.Id, item);
        bool same = wanted.Count == Sensors.Count && wanted.SequenceEqual(Sensors.Select(r => r.Id))
                    && Sensors.All(r => r.Sensor is { } s ? byId.GetValueOrDefault(r.Id) == s : !byId.ContainsKey(r.Id));
        if (!same)
        {
            Sensors.Clear();
            foreach (var id in wanted)
            {
                var item = byId.GetValueOrDefault(id);
                Sensors.Add(new TaskbarSensorRow(id, item?.DisplayName ?? id, item?.HardwareName ?? "", item));
            }
        }
        OnPropertyChanged(nameof(HasSensors));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(QuickPicks));
        OnPropertyChanged(nameof(HasQuickPicks));
        OnPropertyChanged(nameof(CombinedPreview));
        OnPropertyChanged(nameof(CombinedNote));
        OnPropertyChanged(nameof(AllSensors));
        AddSensorCommand.NotifyCanExecuteChanged();
    }
}
