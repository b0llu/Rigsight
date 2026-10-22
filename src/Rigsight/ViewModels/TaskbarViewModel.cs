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

/// <summary>
/// A sensor in the taskbar: what it is, the part it's about (its colour mark), its live reading for the preview, and
/// whether the strip draws it as a graph.
/// </summary>
public sealed partial class TaskbarSensorRow(string id, string name, string hardware, SensorItem? sensor) : ObservableObject
{
    /// <summary>On the strip as a graph of the last two minutes, not its number.</summary>
    [ObservableProperty]
    private bool _graph;

    /// <summary>Told when the switch is flipped, to save it (the page sets it from settings before listening).</summary>
    internal Action<TaskbarSensorRow, bool>? GraphPicked;

    partial void OnGraphChanged(bool value) => GraphPicked?.Invoke(this, value);

    /// <summary>Whether the readings show as the strip (the only way a graph is drawn): the Graph switch shows then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowShapeSwitch))]
    private bool _inStrip;

    public bool ShowShapeSwitch => InStrip && Found;


    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Hardware { get; } = sensor is null ? Lang.T("Not found on this PC right now") : hardware;
    public SensorItem? Sensor { get; } = sensor;
    public bool Found => Sensor is not null;
    public TrayPart Part { get; } = TrayParts.PartOf(sensor?.HardwareType);
    public string PartLabel => TrayParts.Label(Part);

    /// <summary>The word under it on the strip (after its part's first, see <see cref="TrayParts.ShortLabel"/>).</summary>
    public string Short => Sensor is null ? "" : TrayParts.ShortLabel(Sensor.Kind, Sensor.Name, Part, Sensor.CustomLabel);

    /// <summary>The number's colour picked (null: as RigSight colours it; <see cref="TrayColors.Heat"/>; or a code).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberDot))]
    private string? _numberColor;

    /// <summary>The name's colour picked, which its mark and graph take too (null: its part's).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark))]
    private string? _nameColor;

    public bool IsTemperature => Sensor?.Kind == SensorKind.Temperature;

    /// <summary>Its name's colour (its part's unless one was picked), as the taskbar marks it.</summary>
    public Brush Mark => TaskbarViewModel.NameBrush(this, light: false, gray: false);

    /// <summary>The number's colour on its Colors button: by heat (blue, green, amber), the one picked, or the text's.</summary>
    public Brush NumberDot => TaskbarViewModel.NumberDot(this);

    public string ColorsName => Lang.T($"Colors for {Name}");
}

/// <summary>A part on the taskbar strip, in the preview: its readings side by side, its name under them in its colour.</summary>
public sealed record TaskbarStripPart(IReadOnlyList<TaskbarSensorRow> Rows, Brush NameBrush, string Name, IReadOnlyList<Brush> LabelBrushes)
{
    /// <summary>Each reading with the word under it (the part's name under the first, its own under the rest), in its name's colour.</summary>
    public IReadOnlyList<TaskbarStripReading> Readings =>
        [.. TrayParts.StripLabels([.. Rows.Select(r => r.Short)], Name).Select((label, i) => new TaskbarStripReading(Rows[i], label, i == 0, LabelBrushes[i]))];

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
    public string CountText => Sensors.Count switch { 0 => "", 1 => Lang.T("1 in the taskbar"), var n => Lang.T($"{n} in the taskbar") };

    /// <summary>The usual ones, one click each (those already in the taskbar, or missing on this PC, left out).</summary>
    public IReadOnlyList<TaskbarQuickPick> QuickPicks =>
    [
        .. new (string Label, SensorItem? Sensor)[]
            {
                (Lang.T("CPU temperature"), _live.CpuTemp), (Lang.T("GPU temperature"), _live.GpuTemp), (Lang.T("CPU load"), _live.CpuLoad),
                (Lang.T("GPU load"), _live.GpuLoad), (Lang.T("RAM in use"), _live.RamLoad), (Lang.T("GPU power"), _live.GpuPower),
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

    /// <summary>Where the strip sits: next to the clock, or at the taskbar's left end (see <see cref="TraySide"/>).</summary>
    public TraySide Side
    {
        get => _settings.Current.TraySide;
        set
        {
            if (value == Side) return;
            _settings.Update(s => s.TraySide = value);
            OnStyleChanged();
        }
    }

    /// <summary>"Left" or "Right", for the side switch.</summary>
    public string SideText { get => Side.ToString(); set => Side = value == nameof(TraySide.Left) ? TraySide.Left : TraySide.Right; }

    public bool OnLeft => Side == TraySide.Left;

    /// <summary>Whether Windows has the taskbar's icons aligned left (its own setting): then the left end is full.</summary>
    public bool IconsAlignedLeft { get; } = ReadIconsAlignedLeft();

    private static bool ReadIconsAlignedLeft()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("TaskbarAl") is int v && v == 0; // missing: centred, Windows 11's own default
        }
        catch { return false; }
    }

    /// <summary>Under the side switch, when the left side was picked but Windows' icons fill that end.</summary>
    public string SideNote => ShowStrip && OnLeft && IconsAlignedLeft
        ? Lang.T("Your taskbar's icons are aligned left, so the readings stay next to the clock. Centre them in Windows' taskbar settings to use the left side.")
        : "";

    [RelayCommand]
    private void ShowAsGraph(TaskbarSensorRow row) => SetGraph(row, true);

    [RelayCommand]
    private void ShowAsNumber(TaskbarSensorRow row) => SetGraph(row, false);

    private void SetGraph(TaskbarSensorRow row, bool on)
    {
        row.Graph = on; // from a command; from the row's own pair it already is (and this is its call)
        if (_settings.Current.TrayGraphs.Contains(row.Id) == on) return; // already saved (or read from settings)
        _settings.Update(s =>
        {
            s.TrayGraphs.Remove(row.Id);
            if (on) s.TrayGraphs.Add(row.Id);
        });
        OnPropertyChanged(nameof(PreviewStrip));
    }

    private void OnStyleChanged()
    {
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(nameof(Side));
        OnPropertyChanged(nameof(SideText));
        OnPropertyChanged(nameof(OnLeft));
        OnPropertyChanged(nameof(SideNote));
        foreach (var row in Sensors) row.InStrip = ShowStrip;
        OnPropertyChanged(nameof(InStrip));
        OnPropertyChanged(nameof(Separate));
        OnPropertyChanged(nameof(Grouped));
        OnPropertyChanged(nameof(ShowStrip));
        OnPropertyChanged(nameof(ShowIcons));
        OnPropertyChanged(nameof(PreviewIcons));
        OnPropertyChanged(nameof(StripNote));
        OnPropertyChanged(nameof(Colors));
        OnPropertyChanged(nameof(Gray));
        OnPropertyChanged(nameof(Colored));
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
    public string StripNote => InStrip && !StripSupported ? Lang.T("The strip needs Windows 11, so here the readings show as an icon each.") : "";

    /// <summary>The parts on the strip, as the agent lays them out (all of a part's readings, the temperature first).</summary>
    public IReadOnlyList<TaskbarStripPart> PreviewStrip
    {
        get
        {
            var found = Sensors.Where(r => r.Found).ToList();
            var parts = TrayParts.Group(found, r => (TrayParts.GroupOf(r.Part, r.Id), r.Sensor!.Kind), int.MaxValue);
            var names = TrayParts.Names([.. parts.Select(p => p[0].Part)]);
            return [.. parts.Select((rows, i) => new TaskbarStripPart(rows, NameBrush(rows[0], TaskbarLight, Gray), names[i],
                [.. rows.Select(r => NameBrush(r, TaskbarLight, Gray))]))];
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
            return [.. icons.Select(rows => new TaskbarPreviewIcon(rows, NameBrush(rows[0], TaskbarLight, Gray), rows[0].PartLabel))];
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

    /// <summary>A reading's name colour (its mark and graph too): the one picked, made readable on this taskbar, else its part's.</summary>
    public static Brush NameBrush(TaskbarSensorRow row, bool light, bool gray) =>
        !gray && TrayColors.Parse(row.NameColor) is { } c ? Solid(TrayColors.ForTaskbar(c, light)) : MarkBrush(row.Part, light, gray);

    private static readonly Brush HeatDot = Frozen(new LinearGradientBrush(
        [new GradientStop(Color.FromRgb(0x38, 0xBD, 0xF8), 0), new GradientStop(Color.FromRgb(0x38, 0xBD, 0xF8), 0.33),
         new GradientStop(Color.FromRgb(0x34, 0xD3, 0x99), 0.33), new GradientStop(Color.FromRgb(0x34, 0xD3, 0x99), 0.66),
         new GradientStop(Color.FromRgb(0xFB, 0xBF, 0x24), 0.66), new GradientStop(Color.FromRgb(0xFB, 0xBF, 0x24), 1)], 45));

    /// <summary>A reading's number colour for its Colors button: by heat, the one picked, or the text's.</summary>
    public static Brush NumberDot(TaskbarSensorRow row) =>
        (row.NumberColor ?? (row.IsTemperature ? TrayColors.Heat : null)) switch
        {
            TrayColors.Heat => HeatDot,
            { } hex when TrayColors.Parse(hex) is { } c => Solid(c),
            _ => System.Windows.Application.Current?.TryFindResource("TextBrush") as Brush ?? Brushes.White,
        };

    /// <summary>A part's own colour as a code ("#5B8CFF"), for the picker's default.</summary>
    public static string PartHex(TrayPart part)
    {
        var (r, g, b) = TrayParts.Color(part, light: false);
        return TrayColors.Hex(r, g, b);
    }

    private static Brush Solid((byte R, byte G, byte B) c) => Frozen(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)));

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>Whether the readings show in colour, so the Colors buttons can be used (in grayscale they wait).</summary>
    public bool Colored => !Gray;

    /// <summary>The colours made with the picker's "+" for numbers, and for names and marks, in the order made.</summary>
    public IReadOnlyList<string> NumberCustomColors => _settings.Current.TrayNumberColors;
    public IReadOnlyList<string> NameCustomColors => _settings.Current.TrayNameColors;

    /// <summary>Keeps a colour made with "+" after the others of its row (the oldest go past the most kept).</summary>
    public void AddCustomColor(string kind, string hex) => _settings.Update(s =>
    {
        var list = kind == "number" ? s.TrayNumberColors : s.TrayNameColors;
        if (list.Any(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(hex);
        if (list.Count > RigsightSettings.MaxTrayCustomColors) list.RemoveRange(0, list.Count - RigsightSettings.MaxTrayCustomColors);
    });

    /// <summary>Saves a reading's colours (both null: back to RigSight's).</summary>
    public void SetColors(TaskbarSensorRow row, string? number, string? name)
    {
        row.NumberColor = number;
        row.NameColor = name;
        _settings.Update(s =>
        {
            if (number is null && name is null) s.TrayColors.Remove(row.Id);
            else s.TrayColors[row.Id] = new TrayColor { Number = number, Name = name };
        });
        OnPropertyChanged(nameof(PreviewStrip));
        OnPropertyChanged(nameof(PreviewIcons));
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
                Sensors.Add(new TaskbarSensorRow(id, item?.DisplayName ?? id, item?.HardwareShown ?? "", item));
            }
        }
        var graphs = _settings.Current.TrayGraphs;
        foreach (var row in Sensors)
        {
            row.Graph = graphs.Contains(row.Id);
            row.NumberColor = TrayColors.NumberOf(_settings.Current, row.Id);
            row.NameColor = TrayColors.NameOf(_settings.Current, row.Id);
            row.InStrip = ShowStrip;
            row.GraphPicked ??= SetGraph;
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
