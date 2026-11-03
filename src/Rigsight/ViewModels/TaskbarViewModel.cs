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
/// A line in the "Add a reading" list: a part's heading, or a sensor to add (greyed with a tick when already in the
/// taskbar).
/// </summary>
public sealed record TaskbarAddItem(string? Heading, SensorItem? Sensor, bool Added)
{
    public bool IsHeading => Heading is not null;
    public bool CanAdd => !Added;
    public string TypeLabel => Sensor is null ? "" : Units.TypeLabel(Sensor.Kind);

    /// <summary>A tick when it's in the taskbar, else "+".</summary>
    public string Glyph => Added ? "" : "";
}

/// <summary>A look a reading can have, in its Look picker: its name, and whether it's the one in use.</summary>
public sealed record TaskbarLookChoice(TaskbarSensorRow Row, TrayLook Look, bool Selected)
{
    public string Name => TrayParts.LookName(Look);

    /// <summary>Its picture's text up at the top, as on a strip where something draws under the number.</summary>
    public bool TextTop => Look is not (TrayLook.Number or TrayLook.Ring);

    /// <summary>Its picture's tile: the reading's own on the strip (on the dark picture).</summary>
    public Brush Tile => TaskbarViewModel.TileBrush(Row, light: false, gray: false, icon: false);

    /// <summary>"on" for the look in use (outlined).</summary>
    public string? Tag => Selected ? "on" : null;
}

/// <summary>
/// A sensor in the taskbar: what it is, the part it's about (its colour), its live reading for the preview, its look on
/// the strip and its own colours.
/// </summary>
public sealed partial class TaskbarSensorRow(string id, string name, string hardware, SensorItem? sensor) : ObservableObject
{
    /// <summary>How it draws on the strip (see <see cref="TrayLook"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LookName), nameof(Looks), nameof(ShownLook), nameof(TextTop), nameof(LookButtonName))]
    private TrayLook _look;

    /// <summary>The look as it draws: a filled graph in place of a fill-up look it can't have.</summary>
    public TrayLook ShownLook => Sensor is null ? TrayLook.Number : TrayParts.LookFor(Sensor.Kind, Look);

    public string LookName => TrayParts.LookName(ShownLook);

    /// <summary>The looks it can have, for its picker.</summary>
    public IReadOnlyList<TaskbarLookChoice> Looks =>
        Sensor is null ? [] : [.. TrayParts.LooksFor(Sensor.Kind).Select(l => new TaskbarLookChoice(this, l, l == ShownLook))];

    /// <summary>Whether the readings show as the strip (the only style with looks): its Look button shows then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLookButton))]
    private bool _inStrip;

    public bool ShowLookButton => InStrip && Found;

    /// <summary>In two rows, a box of its own (else it shares one with the next in the list, see <see cref="TrayParts.Boxes"/>).</summary>
    [ObservableProperty]
    private bool _alone;

    /// <summary>Whether the readings show in two rows: its "On its own" switch shows then.</summary>
    [ObservableProperty]
    private bool _inBoxes;

    public string AloneName => Lang.T($"{Name} on its own");

    /// <summary>Told when "On its own" is switched, to save it.</summary>
    internal Action<TaskbarSensorRow, bool>? AlonePicked;

    partial void OnAloneChanged(bool value) => AlonePicked?.Invoke(this, value);

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Hardware { get; } = sensor is null ? Lang.T("Not found on this PC right now") : hardware;
    public SensorItem? Sensor { get; } = sensor;
    public bool Found => Sensor is not null;
    public TrayPart Part { get; } = TrayParts.PartOf(sensor?.HardwareType);
    public string PartLabel => TrayParts.Label(Part);

    /// <summary>The word under it on the strip (after its part's first, see <see cref="TrayParts.ShortLabel"/>).</summary>
    public string Short => Sensor is null ? "" : TrayParts.ShortLabel(Sensor.Kind, Sensor.Name, Part, Sensor.CustomLabel);

    /// <summary>Whether <see cref="Short"/> is a name given on All sensors (it wins in two rows too).</summary>
    public bool OwnShort => Sensor?.CustomLabel?.Trim() is { Length: > 0 and <= TrayParts.MaxShortLabel };

    /// <summary>What it's grouped with (see <see cref="TrayParts.GroupOf"/>).</summary>
    public string Group => TrayParts.GroupOf(Part, Id);

    /// <summary>The number's colour picked (null: as RigSight colours it; <see cref="TrayColors.Heat"/>; or a code).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberDot))]
    private string? _numberColor;

    /// <summary>The name's colour picked, which its graph and its icon's tile take too (null: its part's).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark), nameof(TileDot))]
    private string? _nameColor;

    /// <summary>The tile's colour picked (null: the usual tile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileDot), nameof(HasTileColor), nameof(StripTile))]
    private string? _tileColor;

    /// <summary>Its tile on the strip, for the picture on its Look button (on the dark picture).</summary>
    public Brush StripTile => TaskbarViewModel.TileBrush(this, light: false, gray: false, icon: false);

    /// <summary>Its picture's text up at the top, as on a strip where something draws under the number.</summary>
    public bool TextTop => ShownLook is not (TrayLook.Number or TrayLook.Ring);

    public bool IsTemperature => Sensor?.Kind == SensorKind.Temperature;

    /// <summary>Its name's colour (its part's unless one was picked), as the taskbar marks it.</summary>
    public Brush Mark => TaskbarViewModel.NameBrush(this, light: false, gray: false);

    /// <summary>The number's colour on its Colors button: by heat (blue, green, amber), the one picked, or the text's.</summary>
    public Brush NumberDot => TaskbarViewModel.NumberDot(this);

    /// <summary>The tile's colour on its Colors button: the one picked, else none (an outline only).</summary>
    public Brush TileDot => TrayColors.Parse(TileColor) is { } c ? TaskbarViewModel.SolidOf(c) : Brushes.Transparent;
    public bool HasTileColor => TileColor is not null;

    public string ColorsName => Lang.T($"Colors for {Name}");
    public string LookButtonName => Lang.T($"Look for {Name}: {LookName}");

    /// <summary>Told when a look is picked, to save it.</summary>
    internal Action<TaskbarSensorRow, TrayLook>? LookPicked;

    [RelayCommand]
    private void PickLook(TrayLook look)
    {
        Look = look;
        LookPicked?.Invoke(this, look);
    }
}

/// <summary>A part on the taskbar strip, in the preview: its readings side by side, its name under them in its colour.</summary>
public sealed record TaskbarStripPart(IReadOnlyList<TaskbarStripReading> Readings, string Tooltip);

/// <summary>
/// A reading on the strip, in the preview: its number and the word under it (bold for the part's name), its look, its
/// tile (null: none), and the strip's layout (text at the top when anything draws under a number, the width).
/// </summary>
public sealed record TaskbarStripReading(TaskbarSensorRow Row, string Label, bool First, Brush LabelBrush, TrayLook Look, Brush? Tile, bool TextTop,
    TrayWidth WidthMode, bool Light, double Gap)
{
    /// <summary>The room after it: to the next reading of its part, or the next part.</summary>
    public System.Windows.Thickness Space => new(0, 0, Gap, 0);
}

/// <summary>
/// One taskbar icon in the preview: its readings (one, or two stacked), its tile (null: none), its part's name, and how
/// far it is from the next (more between parts, for the name under them).
/// </summary>
public sealed record TaskbarPreviewIcon(IReadOnlyList<TaskbarSensorRow> Rows, Brush? Tile, string Label, bool ShowLabel)
{
    public TaskbarSensorRow First => Rows[0];
    public TaskbarSensorRow? Second => Rows.Count > 1 ? Rows[1] : null;
    public bool HasSecond => Rows.Count > 1;

    /// <summary>What hovering the real icon says: the hardware, then each reading.</summary>
    public string Tooltip => string.Join('\n', Rows.Select(r => r.Name).Prepend(Rows[0].Hardware));
}

/// <summary>A reading in a box of two rows: its word (see <see cref="TrayParts.BoxLabel"/>) in its name's colour.</summary>
public sealed record TaskbarBoxLine(TaskbarSensorRow Row, string Label, Brush LabelBrush);

/// <summary>
/// A box of two rows, in the taskbar's order: its readings (one or two) and their lines as drawn, on its tile (null:
/// none). The Readings list shows the readings under it, the preview the box itself. Kept while the pairs stay, its
/// words and tile changed in place (a colour picked doesn't rebuild the list under the picker).
/// </summary>
public sealed partial class TaskbarBox(int number, IReadOnlyList<TaskbarSensorRow> rows) : ObservableObject
{
    public int Number { get; } = number;
    public IReadOnlyList<TaskbarSensorRow> Rows { get; } = rows;

    [ObservableProperty]
    private IReadOnlyList<TaskbarBoxLine> _lines = [];

    [ObservableProperty]
    private Brush? _tile;

    /// <summary>"Box 1", "Box 2 · on its own", "Box 3 · one reading" (the last, with none after it to pair with).</summary>
    [ObservableProperty]
    private string _title = "";

    /// <summary>What hovering the box says: each reading's name and hardware.</summary>
    public string Tooltip => string.Join('\n', Rows.Select(r => r.Found ? $"{r.Name} · {r.Hardware}" : r.Name));
}

/// <summary>
/// The Taskbar page: readings shown in the taskbar as a strip, in two rows, an icon each or an icon per part, at either end, on tiles
/// in their parts' colours (the agent draws them, see its TaskbarStrip and TrayReadings; both group them with
/// <see cref="TrayParts"/>). As many as the user likes, in their order: the ones that don't fit wait in "+N".
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
        live.TrayWaitingChanged += () => OnPropertyChanged(nameof(WaitingText));
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
    private void AddQuick(TaskbarQuickPick pick) => Add(pick.Sensor);

    // ── Adding a reading: the search box by the Readings title, its list floating under it ──

    /// <summary>What's typed in the "Add a reading" box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddList), nameof(ShowCommon))]
    private string _addSearch = "";

    /// <summary>The common readings first, as pickers pin the most used (only while nothing is typed).</summary>
    public bool ShowCommon => AddSearch.Trim().Length == 0 && HasQuickPicks;

    /// <summary>The order the parts come in the list.</summary>
    private static readonly TrayPart[] PartOrder = [TrayPart.Cpu, TrayPart.Gpu, TrayPart.Ram, TrayPart.Drive, TrayPart.Network, TrayPart.Board, TrayPart.Other];

    /// <summary>
    /// Every sensor that matches what's typed (its name, hardware or part), under its part's heading; those already in
    /// the taskbar greyed with a tick.
    /// </summary>
    public IReadOnlyList<TaskbarAddItem> AddList
    {
        get
        {
            string q = AddSearch.Trim();
            bool Matches(SensorItem s) => q.Length == 0 || s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.HardwareShown.Contains(q, StringComparison.OrdinalIgnoreCase)
                || TrayParts.Label(TrayParts.PartOf(s.HardwareType)).Contains(q, StringComparison.OrdinalIgnoreCase)
                || Units.TypeLabel(s.Kind).Contains(q, StringComparison.OrdinalIgnoreCase);
            var inTaskbar = Sensors.Select(r => r.Id).ToHashSet();
            var list = new List<TaskbarAddItem>();
            foreach (var part in PartOrder)
            {
                var sensors = AllSensors.Where(s => TrayParts.PartOf(s.HardwareType) == part && Matches(s)).ToList();
                if (sensors.Count == 0) continue;
                list.Add(new TaskbarAddItem(TrayParts.Label(part), null, false));
                list.AddRange(sensors.Select(s => new TaskbarAddItem(null, s, inTaskbar.Contains(s.Id))));
            }
            return list;
        }
    }

    public bool AddListEmpty => AddList.Count == 0;

    [RelayCommand]
    private void AddReading(SensorItem? sensor)
    {
        if (sensor is not null) Add(sensor);
    }

    private void Add(SensorItem sensor)
    {
        if (Sensors.Any(r => r.Id == sensor.Id)) return;
        _settings.Update(s =>
        {
            if (!s.TraySensors.Contains(sensor.Id)) s.TraySensors.Add(sensor.Id);
        });
        LoadSensors();
    }

    // ── How they show ──

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

    /// <summary>Where the readings sit, in any style: next to the clock, or at the taskbar's left end (see <see cref="TraySide"/>).</summary>
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

    /// <summary>Where the preview puts them: on the left when asked and there's room (the icons centred).</summary>
    public bool PreviewOnLeft => OnLeft && !IconsAlignedLeft && StripSupported;

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
    public string SideNote => StripSupported && OnLeft && IconsAlignedLeft
        ? Lang.T("Your taskbar's icons are aligned left, so the readings stay next to the clock. Centre them in Windows' taskbar settings to use the left side.")
        : "";

    /// <summary>Each reading on a tile of its own (see <see cref="RigsightSettings.TrayTiles"/>).</summary>
    public bool Tiles => _settings.Current.TrayTiles;

    // In the taskbar's own words: "BACKGROUND" and "Tiles" mean other things on Reports and Widgets in other languages.
    public string BackgroundCaption => Lang.C("taskbar", "BACKGROUND");
    public string TilesWord => Lang.C("taskbar", "Tiles");
    public string FullNamesWord => Lang.C("taskbar", "Full");

    /// <summary>"Tiles" or "None", for the background switch.</summary>
    public string Background
    {
        get => Tiles ? "Tiles" : "None";
        set
        {
            bool tiles = value == "Tiles";
            if (tiles == Tiles) return;
            _settings.Update(s => s.TrayTiles = tiles);
            OnStyleChanged();
        }
    }

    /// <summary>How much room the strip takes (see <see cref="TrayWidth"/>).</summary>
    public TrayWidth WidthMode => _settings.Current.TrayWidth;

    /// <summary>"Compact", "Standard" or "Roomy", for the width switch.</summary>
    public string WidthText
    {
        get => WidthMode.ToString();
        set
        {
            if (!Enum.TryParse<TrayWidth>(value, out var width) || width == WidthMode) return;
            _settings.Update(s => s.TrayWidth = width);
            OnStyleChanged();
        }
    }

    /// <summary>"Full" or "Letter": two rows' names in full ("CPU", "Load") or as one letter ("C", "L").</summary>
    public string NamesText
    {
        get => _settings.Current.TrayLetterNames ? "Letter" : "Full";
        set
        {
            bool letters = value == "Letter";
            if (letters == _settings.Current.TrayLetterNames) return;
            _settings.Update(s => s.TrayLetterNames = letters);
            OnStyleChanged();
        }
    }

    private void SetAlone(TaskbarSensorRow row, bool alone)
    {
        if (_settings.Current.TrayAlone.Contains(row.Id) == alone) return; // already saved (or read from settings)
        _settings.Update(s =>
        {
            s.TrayAlone.Remove(row.Id);
            if (alone) s.TrayAlone.Add(row.Id);
        });
        RefreshBoxes();
    }

    private void SetLook(TaskbarSensorRow row, TrayLook look)
    {
        if (_settings.Current.TrayLooks.GetValueOrDefault(row.Id) == look) return; // already saved (or read from settings)
        _settings.Update(s =>
        {
            s.TrayLooks.Remove(row.Id);
            if (look != TrayLook.Number) s.TrayLooks[row.Id] = look;
        });
        OnPropertyChanged(nameof(PreviewStrip));
    }

    private void OnStyleChanged()
    {
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(nameof(Side));
        OnPropertyChanged(nameof(SideText));
        OnPropertyChanged(nameof(OnLeft));
        OnPropertyChanged(nameof(PreviewOnLeft));
        OnPropertyChanged(nameof(SideNote));
        foreach (var row in Sensors)
        {
            row.InStrip = ShowStrip;
            row.InBoxes = ShowBoxes;
        }
        OnPropertyChanged(nameof(InStrip));
        OnPropertyChanged(nameof(TwoRows));
        OnPropertyChanged(nameof(Separate));
        OnPropertyChanged(nameof(Grouped));
        OnPropertyChanged(nameof(ShowStrip));
        OnPropertyChanged(nameof(ShowBoxes));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowIcons));
        OnPropertyChanged(nameof(ShowWidth));
        OnPropertyChanged(nameof(NamesText));
        RefreshBoxes();
        OnPropertyChanged(nameof(BoxSpace));
        OnPropertyChanged(nameof(PreviewIcons));
        OnPropertyChanged(nameof(StripNote));
        OnPropertyChanged(nameof(Colors));
        OnPropertyChanged(nameof(Gray));
        OnPropertyChanged(nameof(Colored));
        OnPropertyChanged(nameof(Tiles));
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(WidthMode));
        OnPropertyChanged(nameof(WidthText));
        OnPropertyChanged(nameof(PreviewStrip));
        OnPropertyChanged(nameof(WaitingText));
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

    // The four ways, for the style cards (radio buttons).
    public bool InStrip { get => Style == TrayStyle.Strip; set { if (value) Style = TrayStyle.Strip; } }
    public bool TwoRows { get => Style == TrayStyle.TwoRows; set { if (value) Style = TrayStyle.TwoRows; } }
    public bool Separate { get => Style == TrayStyle.Icons; set { if (value) Style = TrayStyle.Icons; } }
    public bool Grouped { get => Style == TrayStyle.Grouped; set { if (value) Style = TrayStyle.Grouped; } }

    /// <summary>RigSight draws the readings in Windows 11's taskbar; on Windows 10 they show as its own tray icons.</summary>
    public static bool StripSupported => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Whether the preview shows the strip, or the boxes of two rows (else the icons, as they'll be).</summary>
    public bool ShowStrip => InStrip && StripSupported;
    public bool ShowBoxes => TwoRows && StripSupported;
    public bool ShowIcons => !ShowStrip && !ShowBoxes;

    /// <summary>The Readings list as it is, or (two rows) under the boxes they make.</summary>
    public bool ShowList => !ShowBoxes;

    /// <summary>The width switch: only the strip has graphs to make roomier or narrower.</summary>
    public bool ShowWidth => ShowStrip;

    /// <summary>The place switch: RigSight draws every style in Windows 11's taskbar, at either end.</summary>
    public bool ShowPlace => StripSupported;

    /// <summary>Under the preview: only Windows' own tray icons (Windows 10) can be tucked behind its arrow.</summary>
    public bool ShowTrayNote => !StripSupported;

    /// <summary>Under the styles, when the strip or two rows were picked on Windows 10.</summary>
    public string StripNote => StripSupported ? ""
        : InStrip ? Lang.T("The strip needs Windows 11, so here the readings show as an icon each.")
        : TwoRows ? Lang.T("Two rows need Windows 11, so here the readings show as an icon each.")
        : "";

    /// <summary>
    /// Over the list, when readings wait for room in "+N" (as the agent last said): which, and that the top of the list
    /// stays.
    /// </summary>
    public string WaitingText
    {
        get
        {
            var waiting = _live.TrayWaiting;
            var names = waiting.Select(id => Sensors.FirstOrDefault(r => r.Id == id)?.Name).OfType<string>().ToList();
            if (names.Count == 0) return "";
            string which = string.Join(", ", names);
            return names.Count == 1
                ? Lang.T($"1 reading doesn't fit in the taskbar on this screen, so it waits in the +1 tile: {which}. The readings at the top of this list stay.")
                : Lang.T($"{names.Count} readings don't fit in the taskbar on this screen, so they wait in the +{names.Count} tile: {which}. The readings at the top of this list stay.");
        }
    }

    /// <summary>The parts on the strip, as the agent lays them out (all of a part's readings, the temperature first).</summary>
    public IReadOnlyList<TaskbarStripPart> PreviewStrip
    {
        get
        {
            var found = Sensors.Where(r => r.Found).ToList();
            var parts = TrayParts.Group(found, r => (TrayParts.GroupOf(r.Part, r.Id), r.Sensor!.Kind), int.MaxValue);
            var names = TrayParts.Names([.. parts.Select(p => p[0].Part)]);
            bool textTop = found.Any(r => r.ShownLook is not (TrayLook.Number or TrayLook.Ring));
            var (_, inner, gap) = new StripSpacing(Tiles, WidthMode).Value;
            return [.. parts.Select((rows, i) =>
            {
                var labels = TrayParts.StripLabels([.. rows.Select(r => r.Short)], names[i]);
                return new TaskbarStripPart([.. rows.Select((r, j) => new TaskbarStripReading(r, labels[j], j == 0, NameBrush(r, TaskbarLight, Gray), r.ShownLook,
                    Tiles ? TileBrush(r, TaskbarLight, Gray, icon: false) : null, textTop, WidthMode, TaskbarLight,
                    j == rows.Count - 1 ? (i == parts.Count - 1 ? 0 : gap) : inner))],
                    string.Join('\n', rows.Select(r => r.Name).Prepend(rows[0].Hardware)));
            })];
        }
    }

    /// <summary>
    /// Two rows' boxes as the agent makes them (TaskbarStrip): the list's readings two to a box in its order, one "on its
    /// own" alone; each word as drawn (a part's name numbered when it comes twice), the tile the first one picked in the
    /// box, else the top's.
    /// </summary>
    public IReadOnlyList<TaskbarBox> Boxes => _boxes;
    private List<TaskbarBox> _boxes = [];

    /// <summary>Makes the boxes again when the pairs changed; else gives the ones there their words and tiles as now.</summary>
    private void RefreshBoxes()
    {
        var rows = Sensors.ToList();
        var made = TrayParts.Boxes(rows, r => r.Alone);
        bool same = made.Count == _boxes.Count && made.Zip(_boxes).All(p => p.First.SequenceEqual(p.Second.Rows));
        if (!same) _boxes = [.. made.Select((box, i) => new TaskbarBox(i + 1, box))];
        var groups = rows.Select(r => r.Group).Distinct().ToList();
        var partNames = TrayParts.Names([.. groups.Select(g => rows.First(r => r.Group == g).Part)]);
        var nameOf = groups.Select((g, i) => (g, partNames[i])).ToDictionary(x => x.g, x => x.Item2);
        bool letters = _settings.Current.TrayLetterNames;
        foreach (var box in _boxes)
        {
            var (first, n) = (box.Rows[0], box.Number);
            box.Lines = [.. box.Rows.Select((r, j) => new TaskbarBoxLine(r, !r.Found ? "" : TrayParts.BoxLabel(nameOf[r.Group], r.Group, r.Sensor!.Kind, r.Short,
                r.OwnShort, j == 0 ? null : first.Group, letters), NameBrush(r, TaskbarLight, Gray)))];
            box.Tile = Tiles ? TileBrush(box.Rows.FirstOrDefault(r => r.TileColor is not null) ?? first, TaskbarLight, Gray, icon: false) : null;
            box.Title = box.Rows.Count > 1 ? Lang.T($"Box {n}") : first.Alone ? Lang.T($"Box {n} · on its own") : Lang.T($"Box {n} · one reading");
        }
        if (!same) OnPropertyChanged(nameof(Boxes));
    }

    /// <summary>The room after each box in the preview, as the agent leaves it (closer on tiles).</summary>
    public System.Windows.Thickness BoxSpace => new(0, 0, Tiles ? 4 : 12, 0);

    /// <summary>The strip's spacing (as the agent's StripLook): inside a tile, between readings of a part, between parts.</summary>
    private readonly record struct StripSpacing(bool Tiles, TrayWidth Width)
    {
        public (double Pad, double Inner, double Gap) Value => Tiles
            ? Width switch { TrayWidth.Compact => (3, 2, 6), TrayWidth.Roomy => (6, 4, 10), _ => (5, 3, 8) }
            : Width == TrayWidth.Compact ? (0, 5, 12) : (0, 7, 16);
    }

    /// <summary>
    /// The icons as the taskbar will show them, grouped as the agent groups them (readings not found left out); the
    /// part's name under the first icon of each part only.
    /// </summary>
    public IReadOnlyList<TaskbarPreviewIcon> PreviewIcons
    {
        get
        {
            var found = Sensors.Where(r => r.Found).ToList();
            var icons = Style == TrayStyle.Grouped
                ? TrayParts.Group(found, r => (TrayParts.GroupOf(r.Part, r.Id), r.Sensor!.Kind))
                : [.. found.Select(r => new List<TaskbarSensorRow> { r })];
            return [.. icons.Select((rows, i) => new TaskbarPreviewIcon(rows, Tiles ? TileBrush(rows[0], TaskbarLight, Gray, icon: true) : null,
                rows[0].PartLabel, i == 0 || icons[i - 1][0].Part != rows[0].Part))];
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

    /// <summary>A reading's name colour (its graph and icon tile too): the one picked, made readable on this taskbar, else its part's.</summary>
    public static Brush NameBrush(TaskbarSensorRow row, bool light, bool gray) =>
        !gray && TrayColors.Parse(row.NameColor) is { } c ? Solid(TrayColors.ForTaskbar(c, light)) : MarkBrush(row.Part, light, gray);

    /// <summary>
    /// A reading's tile, as the agent draws it (TrayReading.TileOn): the colour picked, soft; else, on an icon, its name's
    /// colour, fainter; else the strip's faint neutral tile. Grayscale: grey, whatever was picked.
    /// </summary>
    public static Brush TileBrush(TaskbarSensorRow row, bool light, bool gray, bool icon)
    {
        Color Faded(Color c, int alpha) => Color.FromArgb((byte)alpha, c.R, c.G, c.B);
        if (!gray && TrayColors.Parse(row.TileColor) is { } t) return Frozen(new SolidColorBrush(Faded(Color.FromRgb(t.R, t.G, t.B), TrayColors.TileAlpha(true, light))));
        if (icon && NameBrush(row, light, gray) is SolidColorBrush name) return Frozen(new SolidColorBrush(Faded(name.Color, TrayColors.TileAlpha(false, light))));
        return Frozen(new SolidColorBrush(Faded(light ? System.Windows.Media.Colors.Black : System.Windows.Media.Colors.White, TrayColors.NeutralTileAlpha(light))));
    }

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

    internal static Brush SolidOf((byte R, byte G, byte B) c) => Solid(c);

    private static Brush Solid((byte R, byte G, byte B) c) => Frozen(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)));

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>Whether the readings show in colour, so the Colors buttons can be used (in grayscale they wait).</summary>
    public bool Colored => !Gray;

    /// <summary>The colours made with the picker's "+" for numbers, for names, and for tiles, in the order made.</summary>
    public IReadOnlyList<string> NumberCustomColors => _settings.Current.TrayNumberColors;
    public IReadOnlyList<string> NameCustomColors => _settings.Current.TrayNameColors;
    public IReadOnlyList<string> TileCustomColors => _settings.Current.TrayTileColors;

    /// <summary>Keeps a colour made with "+" after the others of its row (the oldest go past the most kept).</summary>
    public void AddCustomColor(string kind, string hex) => _settings.Update(s =>
    {
        var list = kind switch { "number" => s.TrayNumberColors, "tile" => s.TrayTileColors, _ => s.TrayNameColors };
        if (list.Any(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(hex);
        if (list.Count > RigsightSettings.MaxTrayCustomColors) list.RemoveRange(0, list.Count - RigsightSettings.MaxTrayCustomColors);
    });

    /// <summary>Saves a reading's colours (all null: back to RigSight's).</summary>
    public void SetColors(TaskbarSensorRow row, string? number, string? name, string? tile)
    {
        row.NumberColor = number;
        row.NameColor = name;
        row.TileColor = tile;
        _settings.Update(s =>
        {
            if (number is null && name is null && tile is null) s.TrayColors.Remove(row.Id);
            else s.TrayColors[row.Id] = new TrayColor { Number = number, Name = name, Tile = tile };
        });
        OnPropertyChanged(nameof(PreviewStrip));
        OnPropertyChanged(nameof(PreviewIcons));
        RefreshBoxes();
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
        var settings = _settings.Current;
        foreach (var row in Sensors)
        {
            row.Look = settings.TrayLooks.GetValueOrDefault(row.Id);
            row.NumberColor = TrayColors.NumberOf(settings, row.Id);
            row.NameColor = TrayColors.NameOf(settings, row.Id);
            row.TileColor = TrayColors.TileOf(settings, row.Id);
            row.InStrip = ShowStrip;
            row.InBoxes = ShowBoxes;
            row.AlonePicked ??= SetAlone;
            row.Alone = settings.TrayAlone.Contains(row.Id);
            row.LookPicked ??= SetLook;
        }
        OnPropertyChanged(nameof(HasSensors));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(QuickPicks));
        OnPropertyChanged(nameof(HasQuickPicks));
        OnPropertyChanged(nameof(ShowCommon));
        OnPropertyChanged(nameof(AddList));
        OnPropertyChanged(nameof(AddListEmpty));
        OnPropertyChanged(nameof(PreviewIcons));
        OnPropertyChanged(nameof(PreviewStrip));
        RefreshBoxes();
        OnPropertyChanged(nameof(WaitingText));
        OnPropertyChanged(nameof(AllSensors));
    }
}
