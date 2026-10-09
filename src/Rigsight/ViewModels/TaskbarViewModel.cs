using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A common reading offered with one click ("CPU temperature").</summary>
public sealed record TaskbarQuickPick(string Label, SensorItem Sensor);

/// <summary>A sensor in the taskbar: what it is, the part it's about (its colour mark), and its live reading for the preview.</summary>
public sealed class TaskbarSensorRow(string id, string name, string hardware, SensorItem? sensor)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Hardware { get; } = sensor is null ? "Not found on this PC right now" : hardware;
    public SensorItem? Sensor { get; } = sensor;
    public bool Found => Sensor is not null;
    public TrayPart Part { get; } = TrayParts.PartOf(sensor?.HardwareType);
    public string PartLabel => TrayParts.Label(Part);

    /// <summary>The word under it on the strip (after its part's first, see <see cref="TrayParts.ShortLabel"/>).</summary>
    public string Short => Sensor is null ? "" : TrayParts.ShortLabel(Sensor.Kind, Sensor.Name, Part, Sensor.CustomLabel);

    /// <summary>The part's colour, as the taskbar icon marks it.</summary>
    public Brush Mark => TaskbarViewModel.MarkBrush(Part, light: false);
}

/// <summary>A part on the taskbar strip, in the preview: its readings side by side, its name under them in its colour.</summary>
public sealed record TaskbarStripPart(IReadOnlyList<TaskbarSensorRow> Rows, Brush NameBrush, string Name)
{
    /// <summary>Each reading with the word under it: the part's name under the first, its own under the rest.</summary>
    public IReadOnlyList<TaskbarStripReading> Readings =>
        [.. TrayParts.StripLabels([.. Rows.Select(r => r.Short)], Name).Select((label, i) => new TaskbarStripReading(Rows[i], label, i == 0, NameBrush))];

    public string Tooltip => string.Join('\n', Rows.Select(r => r.Name).Prepend(Rows[0].Hardware));
}

/// <summary>A reading on the strip, in the preview: its number and the word under it (bold for the part's name).</summary>
public sealed record TaskbarStripReading(TaskbarSensorRow Row, string Label, bool First, Brush LabelBrush)
{
    public double LabelOpacity => First ? 1 : 0.8;
    public System.Windows.FontWeight LabelWeight => First ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
}

/// <summary>One taskbar icon in the preview: its readings (one, or two stacked), its part's colour mark and name.</summary>
public sealed record TaskbarPreviewIcon(IReadOnlyList<TaskbarSensorRow> Rows, Brush Mark, string Label)
{
    /// <summary>What hovering the real icon says: the hardware, then each reading.</summary>
    public string Tooltip => string.Join('\n', Rows.Select(r => r.Name).Prepend(Rows[0].Hardware));

    /// <summary>How tall each number may be in the preview (twice the real size): the whole icon, or half each.</summary>
    public double NumberHeight => Rows.Count == 1 ? 30 : 15;
}

/// <summary>
/// The Taskbar page: readings shown next to the clock, an icon each or grouped by part, each icon marked with its
/// part's colour (the agent draws them, see its TrayReadings; both group them with <see cref="TrayParts"/>). As many
/// as the user likes, in their order.
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
        OnStyleChanged();
        LoadSensors();
    }

    /// <summary>Every sensor on this PC, for the picker.</summary>
    public IReadOnlyList<SensorItem> AllSensors => _live.PickableSensors;

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

    /// <summary>A strip in the taskbar, an icon each, or an icon per part (see <see cref="TrayStyle"/>).</summary>
    public TrayStyle Style
    {
        get => _settings.Current.TrayStyle;
        set
        {
            if (value == Style) return;
            _settings.Update(s => s.TrayStyle = value);
            OnStyleChanged();
        }
    }

    private void OnStyleChanged()
    {
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(nameof(InStrip));
        OnPropertyChanged(nameof(Separate));
        OnPropertyChanged(nameof(Grouped));
        OnPropertyChanged(nameof(ShowStrip));
        OnPropertyChanged(nameof(ShowIcons));
        OnPropertyChanged(nameof(PreviewIcons));
        OnPropertyChanged(nameof(StripNote));
        OnPropertyChanged(nameof(Colors));
        OnPropertyChanged(nameof(Gray));
        OnPropertyChanged(nameof(PreviewStrip));
    }

    /// <summary>The parts' colours, or grayscale (the taskbar's text colour and grey).</summary>
    public bool Gray => _settings.Current.TrayGrayscale;

    /// <summary>"Color" or "Grayscale", for the colors switch.</summary>
    public string Colors
    {
        get => Gray ? "Grayscale" : "Color";
        set
        {
            bool gray = value == "Grayscale";
            if (gray == Gray) return;
            _settings.Update(s => s.TrayGrayscale = gray);
            OnStyleChanged();
        }
    }

    // The three ways, for the style cards (radio buttons).
    public bool InStrip { get => Style == TrayStyle.Strip; set { if (value) Style = TrayStyle.Strip; } }
    public bool Separate { get => Style == TrayStyle.Icons; set { if (value) Style = TrayStyle.Icons; } }
    public bool Grouped { get => Style == TrayStyle.Grouped; set { if (value) Style = TrayStyle.Grouped; } }

    /// <summary>The strip needs Windows 11's taskbar; on Windows 10 the readings show as icons.</summary>
    public static bool StripSupported => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Whether the preview shows the strip (else the icons, as they'll be).</summary>
    public bool ShowStrip => InStrip && StripSupported;
    public bool ShowIcons => !ShowStrip;

    /// <summary>Under the styles, when the strip was picked on Windows 10.</summary>
    public string StripNote => InStrip && !StripSupported ? "The strip needs Windows 11, so here the readings show as an icon each." : "";

    /// <summary>The parts on the strip, as the agent lays them out (all of a part's readings, the temperature first).</summary>
    public IReadOnlyList<TaskbarStripPart> PreviewStrip
    {
        get
        {
            var found = Sensors.Where(r => r.Found).ToList();
            var parts = TrayParts.Group(found, r => (TrayParts.GroupOf(r.Part, r.Id), r.Sensor!.Kind), int.MaxValue);
            var names = TrayParts.Names([.. parts.Select(p => p[0].Part)]);
            return [.. parts.Select((rows, i) => new TaskbarStripPart(rows, MarkBrush(rows[0].Part, TaskbarLight, Gray), names[i]))];
        }
    }

    /// <summary>The icons as the taskbar will show them, grouped as the agent groups them (readings not found left out).</summary>
    public IReadOnlyList<TaskbarPreviewIcon> PreviewIcons
    {
        get
        {
            var found = Sensors.Where(r => r.Found).ToList();
            var icons = Style == TrayStyle.Grouped
                ? TrayParts.Group(found, r => (TrayParts.GroupOf(r.Part, r.Id), r.Sensor!.Kind))
                : [.. found.Select(r => new List<TaskbarSensorRow> { r })];
            return [.. icons.Select(rows => new TaskbarPreviewIcon(rows, MarkBrush(rows[0].Part, TaskbarLight, Gray), rows[0].PartLabel))];
        }
    }

    private static readonly Dictionary<(TrayPart, bool), Brush> Marks = [];

    /// <summary>A part's colour mark as a brush (deeper on a light taskbar, as the real icon has it; grey in grayscale).</summary>
    public static Brush MarkBrush(TrayPart part, bool light, bool gray = false)
    {
        if (gray) part = TrayPart.Other;
        if (Marks.TryGetValue((part, light), out var brush)) return brush;
        var (r, g, b) = TrayParts.Color(part, light);
        brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return Marks[(part, light)] = brush;
    }

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
        OnPropertyChanged(nameof(PreviewIcons));
        OnPropertyChanged(nameof(PreviewStrip));
        OnPropertyChanged(nameof(AllSensors));
        AddSensorCommand.NotifyCanExecuteChanged();
    }
}
