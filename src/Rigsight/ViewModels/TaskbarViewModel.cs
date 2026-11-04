using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.TaskbarDrawing;

namespace Rigsight.ViewModels;

/// <summary>A common reading offered with one click ("CPU temperature"), or two at once ("CPU load and temperature": <see cref="Second"/>).</summary>
public sealed record TaskbarQuickPick(string Label, SensorItem Sensor, SensorItem? Second = null);

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
    public string Glyph => Added ? "" : "";
}

/// <summary>
/// A look a reading can have, in its "Shows as" picker: its name, its picture (as the taskbar will draw it, at its real
/// height), and whether it's the one in use.
/// </summary>
public sealed record TaskbarLookChoice(TaskbarSensorRow Row, TrayLook Look, bool Selected, string Name, BitmapSource? Picture)
{
    /// <summary>"on" for the look in use (outlined).</summary>
    public string? Tag => Selected ? "on" : null;
}

/// <summary>A reading a look can be drawn from ("Together"): its id (null: the reading's own), its name, whether it's the one in use.</summary>
public sealed record TaskbarLookFromChoice(TaskbarSensorRow Row, string? Id, string Name, bool Selected)
{
    public string? Tag => Selected ? "on" : null;
}

/// <summary>
/// A sensor in the taskbar: what it is, the part it's about (its colour), its live reading, how it shows (its own style,
/// size and look, and the reading its look is drawn from) and its own colours.
/// </summary>
public sealed partial class TaskbarSensorRow(string id, string name, string hardware, SensorItem? sensor) : ObservableObject
{
    /// <summary>How it draws (see <see cref="TrayLook"/>): on the strip every look, on an icon its number, Fill (Level) or Ring.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LookName), nameof(Looks), nameof(ShownLook), nameof(ShowsAsText), nameof(LookButtonName))]
    private TrayLook _look;

    /// <summary>Its style: its own, else the taskbar's (see <see cref="RigsightSettings.TrayStyles"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StyleText), nameof(InBoxes), nameof(HasLooks), nameof(LookName), nameof(Looks), nameof(ShownLook),
        nameof(ShowsAsText), nameof(LookButtonName), nameof(ShowLookFrom), nameof(LookFromChoices))]
    private TrayStyle _style;

    /// <summary>Its size (see <see cref="TraySize"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(ShowsAsText))]
    private TraySize _size;

    /// <summary>The reading its look is drawn from (null: its own).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LookFromChoices), nameof(Looks), nameof(ShownLook), nameof(LookName), nameof(ShowsAsText))]
    private string? _lookFrom;

    /// <summary>The kind its look is drawn from (another reading's, with one; else its own).</summary>
    public SensorKind? LookFromKind => LookFrom is null ? null : LookFromSensor?.Invoke(LookFrom)?.Kind;

    /// <summary>The look as it draws: on the strip a filled graph in place of a fill-up look it can't have; on an icon its number, Fill or Ring.</summary>
    public TrayLook ShownLook => Sensor is null ? TrayLook.Number
        : Style is TrayStyle.Icons or TrayStyle.Grouped ? TrayParts.IconLook(LookFromKind ?? Sensor.Kind, Look)
        : TrayParts.LookFor(LookFromKind ?? Sensor.Kind, Look, Id == TrayParts.NetBoth);

    public string LookName => Style is TrayStyle.Icons or TrayStyle.Grouped ? TrayParts.IconLookName(ShownLook) : TrayParts.LookName(ShownLook);

    /// <summary>The looks it can have in its style, each with its picture, for its picker (none in two rows).</summary>
    public IReadOnlyList<TaskbarLookChoice> Looks
    {
        get
        {
            if (Sensor is null || Style == TrayStyle.TwoRows) return [];
            bool icon = Style is TrayStyle.Icons or TrayStyle.Grouped;
            var looks = icon ? TrayParts.IconLooksFor(Sensor.Kind, LookFromKind)
                : TrayParts.LooksFor(Sensor.Kind, TrayParts.IsCpuTotal(Sensor.Kind, Part, Sensor.Name), Id == TrayParts.NetBoth, LookFromKind);
            return [.. looks.Select(l => new TaskbarLookChoice(this, l, l == ShownLook, icon ? TrayParts.IconLookName(l) : TrayParts.LookName(l), LookPictureOf?.Invoke(this, l)))];
        }
    }

    /// <summary>Whether it has looks to pick in its style (two rows has none).</summary>
    public bool HasLooks => Style != TrayStyle.TwoRows && Found;

    /// <summary>"In the taskbar · Blocks · Small": how it shows, on its button.</summary>
    public string ShowsAsText
    {
        get
        {
            var parts = new List<string> { TrayParts.StyleName(Style) };
            if (HasLooks && ShownLook != TrayLook.Number) parts.Add(LookName);
            if (Size != TraySize.Auto) parts.Add(TrayParts.SizeName(Size));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Its picture as the taskbar draws it now, on its button.</summary>
    [ObservableProperty]
    private BitmapSource? _picture;

    /// <summary>The taskbar's colour behind its pictures (Windows' light or dark taskbar, as the pictures are drawn for it).</summary>
    public Brush BarBrush { get; internal set; } = Brushes.Black;

    /// <summary>Under its name in two rows: the box it's in ("Box 2", "Box 3 · on its own").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HardwareLine))]
    private string _boxTitle = "";

    /// <summary>Under its name: its hardware, and in two rows the box it's in ("NVIDIA GeForce RTX 3050 · Box 2").</summary>
    public string HardwareLine => BoxTitle.Length > 0 ? $"{Hardware} · {BoxTitle}" : Hardware;

    /// <summary>In its picker: why a size it can't have now is off, and what to do (empty: every size fits).</summary>
    [ObservableProperty]
    private string _sizeNote = "";

    /// <summary>Whether a size fits the taskbar as it is (set by the page as it measures the taskbar).</summary>
    [ObservableProperty]
    private bool _canSmall = true, _canMedium = true, _canLarge = true;

    /// <summary>"Shown as Small for now": its size doesn't fit this taskbar (empty when it does).</summary>
    [ObservableProperty]
    private string _sizeNow = "";

    /// <summary>"Strip", "TwoRows", "Icons" or "Grouped", for its style switch.</summary>
    public string StyleText
    {
        get => Style.ToString();
        set { if (Enum.TryParse<TrayStyle>(value, out var style) && style != Style) StylePicked?.Invoke(this, style); }
    }

    /// <summary>"Auto", "Small", "Medium" or "Large", for its size switch.</summary>
    public string SizeText
    {
        get => Size.ToString();
        set { if (Enum.TryParse<TraySize>(value, out var size) && size != Size) SizePicked?.Invoke(this, size); }
    }

    /// <summary>The readings its look can be drawn from: its own, or another of its part's (Together).</summary>
    public IReadOnlyList<TaskbarLookFromChoice> LookFromChoices =>
        [new(this, null, Lang.T("Its own reading"), LookFrom is null), .. (LookFromCandidates?.Invoke(this) ?? []).Select(s => new TaskbarLookFromChoice(this, s.Id, s.DisplayName, s.Id == LookFrom))];

    /// <summary>Whether its look can come from another reading: on the strip or an icon, with another reading of its part to draw from.</summary>
    public bool ShowLookFrom => HasLooks && (LookFromCandidates?.Invoke(this).Count ?? 0) > 0;

    /// <summary>In two rows, a box of its own (else it shares one with the next in the list, see <see cref="TrayParts.Boxes"/>).</summary>
    [ObservableProperty]
    private bool _alone;

    /// <summary>Whether it shows in two rows: its "On its own" switch shows then.</summary>
    public bool InBoxes => Style == TrayStyle.TwoRows;

    public string AloneName => Lang.T($"{Name} on its own");

    /// <summary>Told when "On its own", a style, a size, a look or where it's drawn from is picked, to save it.</summary>
    internal Action<TaskbarSensorRow, bool>? AlonePicked;
    internal Action<TaskbarSensorRow, TrayStyle>? StylePicked;
    internal Action<TaskbarSensorRow, TraySize>? SizePicked;
    internal Action<TaskbarSensorRow, string?>? LookFromPicked;
    internal Action<TaskbarSensorRow, TrayLook>? LookPicked;

    /// <summary>The page's: a look's picture for this reading, the readings it can draw from, a sensor by id.</summary>
    internal Func<TaskbarSensorRow, TrayLook, BitmapSource?>? LookPictureOf;
    internal Func<TaskbarSensorRow, IReadOnlyList<SensorItem>>? LookFromCandidates;
    internal Func<string, SensorItem?>? LookFromSensor;

    partial void OnAloneChanged(bool value) => AlonePicked?.Invoke(this, value);

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Hardware { get; } = sensor is null ? Lang.T("Not found on this PC right now") : hardware;
    public SensorItem? Sensor { get; } = sensor;
    public bool Found => Sensor is not null;
    public TrayPart Part { get; } = TrayParts.PartOf(sensor?.HardwareType);
    public string PartLabel => TrayParts.Label(Part);

    /// <summary>The word under it on the strip (after its part's first, see <see cref="TrayParts.ShortLabel"/>).</summary>
    public string Short => Sensor is null ? ""
        : TrayParts.IsNet(Id) ? (OwnShort ? Sensor.CustomLabel!.Trim() : Id == TrayParts.NetUp ? Lang.T("Up") : Lang.T("Down"))
        : TrayParts.ShortLabel(Sensor.Kind, Sensor.Name, Part, Sensor.CustomLabel);

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
    [NotifyPropertyChangedFor(nameof(TileDot), nameof(HasTileColor))]
    private string? _tileColor;

    public bool IsTemperature => Sensor?.Kind == SensorKind.Temperature;

    /// <summary>Its name's colour (its part's unless one was picked), as the taskbar marks it.</summary>
    public Brush Mark => TaskbarViewModel.NameBrush(this, light: false, gray: false);

    /// <summary>The number's colour on its Colors button: by heat (blue, green, amber), the one picked, or the text's.</summary>
    public Brush NumberDot => TaskbarViewModel.NumberDot(this);

    /// <summary>The tile's colour on its Colors button: the one picked, else none (an outline only).</summary>
    public Brush TileDot => TrayColors.Parse(TileColor) is { } c ? TaskbarViewModel.SolidOf(c) : Brushes.Transparent;
    public bool HasTileColor => TileColor is not null;

    public string ColorsName => Lang.T($"Colors for {Name}");
    public string LookButtonName => Lang.T($"How {Name} shows: {ShowsAsText}");

    [RelayCommand]
    private void PickLook(TrayLook look)
    {
        Look = look;
        LookPicked?.Invoke(this, look);
    }

    [RelayCommand]
    private void PickLookFrom(string? id) => LookFromPicked?.Invoke(this, id);
}

/// <summary>
/// A box of two rows, in the taskbar's order: its readings (one or two). Its title goes on its readings' rows in the list.
/// </summary>
public sealed record TaskbarBox(int Number, IReadOnlyList<TaskbarSensorRow> Rows);

/// <summary>
/// The Taskbar page: readings shown in the taskbar, each in its own style (a strip, two rows, an icon each or an icon
/// per part) and size, at either end, on tiles in their parts' colours. Its preview is the agent's own drawing (its
/// StripComposer, linked into the app) at the taskbar's real height, so what is picked is what the taskbar shows. As many
/// readings as the user likes, in their order: the ones that don't fit wait in "+N".
/// </summary>
public sealed partial class TaskbarViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly LiveData _live;

    /// <summary>The preview's drawing, and the pictures' (every reading as if on the strip, so each look has its minutes).</summary>
    private readonly StripComposer _preview = new(), _pictures = new();

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

    /// <summary>Every sensor on this PC, for the picker, and RigSight's own network speeds.</summary>
    public IReadOnlyList<SensorItem> AllSensors => [.. _live.PickableSensors, .. _live.NetSensors];

    public ObservableCollection<TaskbarSensorRow> Sensors { get; } = [];
    public bool HasSensors => Sensors.Count > 0;

    /// <summary>"3 in the taskbar".</summary>
    public string CountText => Sensors.Count switch { 0 => "", 1 => Lang.T("1 in the taskbar"), var n => Lang.T($"{n} in the taskbar") };

    /// <summary>
    /// The usual ones, one click each (those already in the taskbar, or missing on this PC, left out): single readings,
    /// a part's load and temperature together, and the network's speeds.
    /// </summary>
    public IReadOnlyList<TaskbarQuickPick> QuickPicks
    {
        get
        {
            bool In(SensorItem? s) => s is not null && Sensors.Any(r => r.Id == s.Id);
            var picks = new List<TaskbarQuickPick>();
            void One(string label, SensorItem? s) { if (s is not null && !In(s)) picks.Add(new(label, s)); }
            void Two(string label, SensorItem? temp, SensorItem? load) { if (temp is not null && load is not null && !In(temp) && !In(load)) picks.Add(new(label, temp, load)); }
            One(Lang.T("CPU temperature"), _live.CpuTemp);
            One(Lang.T("GPU temperature"), _live.GpuTemp);
            One(Lang.T("CPU load"), _live.CpuLoad);
            One(Lang.T("GPU load"), _live.GpuLoad);
            One(Lang.T("RAM in use"), _live.RamLoad);
            One(Lang.T("GPU power"), _live.GpuPower);
            Two(Lang.T("CPU load and temperature"), _live.CpuTemp, _live.CpuLoad);
            Two(Lang.T("GPU load and temperature"), _live.GpuTemp, _live.GpuLoad);
            One(Lang.T("Download speed"), _live.NetSensors[0]);
            One(Lang.T("Upload speed"), _live.NetSensors[1]);
            One(Lang.T("Download and upload"), _live.NetSensors[2]);
            return picks;
        }
    }

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
        // A pair goes in one after the other, the temperature first: in two rows they share a box.
        _settings.Update(s =>
        {
            if (!s.TraySensors.Contains(pick.Sensor.Id)) s.TraySensors.Add(pick.Sensor.Id);
            if (pick.Second is { } second && !s.TraySensors.Contains(second.Id)) s.TraySensors.Add(second.Id);
        });
        LoadSensors();
    }

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
            if (s.TraySensors.Contains(sensor.Id)) return;
            // A new reading takes the style of the last one in the list (the readings next to it look alike).
            if (s.TraySensors.Count > 0 && s.TrayStyles.GetValueOrDefault(s.TraySensors[^1], s.TrayStyle) is var last && last != s.TrayStyle)
                s.TrayStyles[sensor.Id] = last;
            s.TraySensors.Add(sensor.Id);
        });
        LoadSensors();
    }

    // ── How they show ──

    /// <summary>The style every reading without one of its own has (see <see cref="TrayStyle"/>).</summary>
    public TrayStyle Style => _settings.Current.TrayStyle;

    /// <summary>Whether every reading shows in <paramref name="style"/> (a card is ticked then; with mixed styles none is).</summary>
    private bool AllIn(TrayStyle style) => Sensors.Count == 0 ? Style == style : Sensors.All(r => r.Style == style);

    /// <summary>Every reading in one style: each one's own style goes.</summary>
    private void SetAll(TrayStyle style)
    {
        _settings.Update(s =>
        {
            s.TrayStyle = style;
            s.TrayStyles.Clear();
        });
        LoadSensors();
        OnStyleChanged();
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

    /// <summary>"Bytes" or "Bits": speeds as "12 MB/s" or as internet plans say them, "99 Mbps".</summary>
    public string SpeedsText
    {
        get => _settings.Current.TraySpeedBits ? "Bits" : "Bytes";
        set
        {
            bool bits = value == "Bits";
            if (bits == _settings.Current.TraySpeedBits) return;
            _settings.Update(s => s.TraySpeedBits = bits);
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
        Redraw();
    }

    private void SetLook(TaskbarSensorRow row, TrayLook look)
    {
        if (_settings.Current.TrayLooks.GetValueOrDefault(row.Id) == look) return; // already saved (or read from settings)
        _settings.Update(s =>
        {
            s.TrayLooks.Remove(row.Id);
            if (look != TrayLook.Number) s.TrayLooks[row.Id] = look;
        });
        Redraw();
    }

    private void SetStyleOf(TaskbarSensorRow row, TrayStyle style)
    {
        _settings.Update(s =>
        {
            s.TrayStyles.Remove(row.Id);
            if (style != s.TrayStyle) s.TrayStyles[row.Id] = style;
        });
        row.Style = style;
        RefreshBoxes();
        OnStyleChanged();
    }

    private void SetSizeOf(TaskbarSensorRow row, TraySize size)
    {
        _settings.Update(s =>
        {
            s.TraySizes.Remove(row.Id);
            if (size != TraySize.Auto) s.TraySizes[row.Id] = size;
        });
        row.Size = size;
        Redraw();
    }

    private void SetLookFrom(TaskbarSensorRow row, string? from)
    {
        _settings.Update(s =>
        {
            s.TrayLookFrom.Remove(row.Id);
            if (from is not null && from != row.Id) s.TrayLookFrom[row.Id] = from;
        });
        row.LookFrom = from;
        Redraw();
    }

    /// <summary>The readings a reading's look can be drawn from: its part's loads, temperatures, power and clocks (its own left out).</summary>
    private IReadOnlyList<SensorItem> LookFromCandidates(TaskbarSensorRow row)
    {
        if (row.Sensor is null || TrayParts.IsNet(row.Id)) return [];
        return [.. _live.PickableSensors.Where(s => s.Id != row.Id && TrayParts.GroupOf(TrayParts.PartOf(s.HardwareType), s.Id) == row.Group
                && s.Kind is SensorKind.Load or SensorKind.Temperature or SensorKind.Power or SensorKind.Clock && !TrayParts.IsCoreLoad(s.Kind, s.Name))
            .OrderBy(s => Sensors.Any(r => r.Id == s.Id) ? 0 : 1).ThenBy(s => s.Kind).Take(8)];
    }

    private SensorItem? SensorById(string id) => AllSensors.FirstOrDefault(s => s.Id == id);

    private void OnStyleChanged()
    {
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(nameof(Side));
        OnPropertyChanged(nameof(SideText));
        OnPropertyChanged(nameof(OnLeft));
        OnPropertyChanged(nameof(PreviewOnLeft));
        OnPropertyChanged(nameof(SideNote));
        OnPropertyChanged(nameof(InStrip));
        OnPropertyChanged(nameof(TwoRows));
        OnPropertyChanged(nameof(Separate));
        OnPropertyChanged(nameof(Grouped));
        OnPropertyChanged(nameof(ShowWidth));
        OnPropertyChanged(nameof(ShowNames));
        OnPropertyChanged(nameof(ShowSpeeds));
        OnPropertyChanged(nameof(NamesText));
        OnPropertyChanged(nameof(SpeedsText));
        RefreshBoxes();
        OnPropertyChanged(nameof(StripNote));
        OnPropertyChanged(nameof(Colors));
        OnPropertyChanged(nameof(Gray));
        OnPropertyChanged(nameof(Colored));
        OnPropertyChanged(nameof(Tiles));
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(WidthMode));
        OnPropertyChanged(nameof(WidthText));
        OnPropertyChanged(nameof(WaitingText));
        Redraw();
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

    // The four styles' cards: "Set all to" (ticked while every reading has that style; with mixed styles none is).
    public bool InStrip { get => AllIn(TrayStyle.Strip); set { if (value) SetAll(TrayStyle.Strip); } }
    public bool TwoRows { get => AllIn(TrayStyle.TwoRows); set { if (value) SetAll(TrayStyle.TwoRows); } }
    public bool Separate { get => AllIn(TrayStyle.Icons); set { if (value) SetAll(TrayStyle.Icons); } }
    public bool Grouped { get => AllIn(TrayStyle.Grouped); set { if (value) SetAll(TrayStyle.Grouped); } }

    /// <summary>RigSight draws the readings in Windows 11's taskbar; on Windows 10 they show as its own tray icons.</summary>
    public static bool StripSupported => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>The width switch: only the strip has graphs to make roomier or narrower.</summary>
    public bool ShowWidth => Sensors.Any(r => r.Style == TrayStyle.Strip) && StripSupported;

    /// <summary>Two rows' names switch: with a reading in two rows.</summary>
    public bool ShowNames => Sensors.Any(r => r.Style == TrayStyle.TwoRows) && StripSupported;

    /// <summary>The speeds' unit switch: with a speed in the taskbar.</summary>
    public bool ShowSpeeds => Sensors.Any(r => r.Sensor?.Kind == SensorKind.Throughput);

    /// <summary>The place switch: RigSight draws every style in Windows 11's taskbar, at either end.</summary>
    public bool ShowPlace => StripSupported;

    /// <summary>Under the preview: only Windows' own tray icons (Windows 10) can be tucked behind its arrow.</summary>
    public bool ShowTrayNote => !StripSupported;

    /// <summary>Under the styles, when the strip or two rows were picked on Windows 10.</summary>
    public string StripNote => StripSupported ? ""
        : Sensors.Any(r => r.Style is TrayStyle.Strip or TrayStyle.TwoRows) ? Lang.T("The strip and two rows need Windows 11, so here the readings show as an icon each.")
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

    // ── The preview: the agent's own drawing, at the taskbar's real height ──

    /// <summary>The taskbar's readings as the taskbar will draw them (pixel for pixel at its scale).</summary>
    [ObservableProperty]
    private BitmapSource? _previewImage;

    /// <summary>The preview's taskbar height in the page's units (48 on Windows' usual taskbar, 32 with its smaller buttons).</summary>
    [ObservableProperty]
    private double _barHeight = 48;

    /// <summary>Under the preview: the taskbar's height, when it's short (so each reading is on one line), as measured.</summary>
    [ObservableProperty]
    private string _barNote = "";

    /// <summary>The taskbar as last measured: height and width in pixels, and its scale.</summary>
    private (int Height, int Width, float Scale) _bar = (48, 1920, 1);

    /// <summary>
    /// Draws the preview again, and each reading's picture: the readings as now (their newest values, their graphs' last
    /// minutes), at the taskbar's real height and scale. The page calls this every second while it shows.
    /// </summary>
    public void Redraw()
    {
        try
        {
            _bar = TaskbarBar.Now();
            var (height, width, scale) = _bar;
            BarHeight = height / scale;
            BarNote = height / scale < WidgetRendererTwoLineHeight
                ? Lang.T($"Your taskbar is {Math.Round(height / scale)} px tall (Windows' smaller taskbar buttons), so each reading is on one line, as here.")
                : "";
            long now = Environment.TickCount64;
            var readings = Sensors.Select(Reading).ToList();
            var options = Options();
            _preview.Remember(readings, now);
            float graphWide = WidgetRenderer.StripGraphWidthFor(width, scale, WidthMode);
            if (readings.Count == 0) PreviewImage = null;
            else
            {
                var layout = _preview.Build(readings, options, graphWide, 0, height, scale, now);
                using var bmp = layout.Render(out _, out _);
                PreviewImage = TaskbarBar.ToImage(bmp, scale);
            }
            // Each reading's picture on its button, as it shows; the pictures' composer keeps every reading's minutes.
            _pictures.Remember([.. readings.Select(r => r with { Style = TrayStyle.Strip })], now);
            foreach (var row in Sensors) row.Picture = PictureOf(row, row.Look, row.Style);
            UpdateSizes();
        }
        catch (Exception ex) { Log.Write("taskbar", $"The preview couldn't be drawn: {ex.Message}"); }
    }

    /// <summary>Below this height (at 100%) the strip goes to one line (as the agent's StripTwoLineHeight).</summary>
    private const double WidgetRendererTwoLineHeight = 36;

    /// <summary>A look's picture for a reading, as the taskbar will draw it at its height, in <paramref name="style"/>.</summary>
    private BitmapSource? PictureOf(TaskbarSensorRow row, TrayLook look, TrayStyle style)
    {
        if (row.Sensor is null) return null;
        var (height, width, scale) = _bar;
        var reading = Reading(row) with { Look = look, Style = style, Alone = true };
        var layout = _pictures.Build([reading], Options(), WidgetRenderer.StripGraphWidthFor(width, scale, WidthMode), 0,
            TrayParts.SizeHeight(row.Size, height, scale), scale, Environment.TickCount64);
        using var bmp = layout.Render(out _, out _);
        return TaskbarBar.ToImage(bmp, scale);
    }

    private StripComposer.Options Options() => new(TaskbarLight, Gray, Tiles, WidthMode, _settings.Current.TrayLetterNames, _settings.Current.TraySpeedBits);

    /// <summary>
    /// A row as the agent reads it (AgentContext.TrayReadingsFor): its value now, its own style and size, the reading its
    /// look is drawn from, both speeds' upload, a CPU's cores.
    /// </summary>
    private TrayReading Reading(TaskbarSensorRow row)
    {
        var settings = _settings.Current;
        if (row.Sensor is not { } s)
            return new TrayReading(row.Id, row.Name, SensorKind.Factor, null, TrayPart.Other, "", Alone: row.Alone, Style: row.Style, Size: row.Size);
        var from = row.LookFrom is { } f ? SensorById(f) : null;
        float[]? cores = null;
        if (row.Look == TrayLook.Cores && from is null && TrayParts.IsCpuTotal(s.Kind, row.Part, s.Name))
        {
            var list = _live.AllSensors.Where(c => c.HardwareName == s.HardwareName && TrayParts.IsCoreLoad(c.Kind, c.Name)).ToList();
            if (list.Count > 0) cores = [.. list.Select(c => c.Value is double v ? (float)Math.Clamp(v / 100, 0, 1) : 0f)];
        }
        return new TrayReading(row.Id, row.Name, s.Kind, s.Value, row.Part, row.Hardware, row.Short, row.NumberColor, row.NameColor, row.Look, row.TileColor,
            row.OwnShort, row.Alone, row.Style, row.Size, from?.Id, from?.Kind, from?.Value,
            Second: row.Id == TrayParts.NetBoth ? _live.NetSensors[1].Value : null, Cores: cores);
    }

    /// <summary>Which sizes fit the taskbar now, why the others don't, and which readings show smaller for now.</summary>
    private void UpdateSizes()
    {
        var (height, _, scale) = _bar;
        bool Fits(TraySize size) => TrayParts.SizeNeeds(size) * scale <= height + 0.5;
        var off = new[] { TraySize.Small, TraySize.Medium, TraySize.Large }.Where(z => !Fits(z)).ToList();
        int px = (int)Math.Round(height / scale);
        string note = off.Count == 0 ? ""
            : off.Count == 1
                ? Lang.T($"Your taskbar is {px} px tall, so {TrayParts.SizeName(off[0])} is off: it needs {TrayParts.SizeNeeds(off[0])} px. To use it, set Show smaller taskbar buttons to Never.")
                : Lang.T($"Your taskbar is {px} px tall, so {string.Join(" and ", off.Select(TrayParts.SizeName))} are off: they need {TrayParts.SizeNeeds(off[^1])} px. To use them, set Show smaller taskbar buttons to Never.");
        foreach (var row in Sensors)
        {
            row.CanSmall = Fits(TraySize.Small);
            row.CanMedium = Fits(TraySize.Medium);
            row.CanLarge = Fits(TraySize.Large);
            row.SizeNote = note;
            row.SizeNow = row.Size != TraySize.Auto && !Fits(row.Size) ? Lang.T("Shown smaller for now: the taskbar is too short for this size") : "";
        }
    }

    [RelayCommand]
    private void OpenTaskbarSettings() => TaskbarBar.OpenSettings();

    /// <summary>Two rows' boxes as the agent makes them: each run of readings in two rows, two to a box in the list's order, one "on its own" alone.</summary>
    public IReadOnlyList<TaskbarBox> Boxes => _boxes;
    private List<TaskbarBox> _boxes = [];

    /// <summary>Makes the boxes again, and gives each reading in two rows its box's title ("Box 2 · on its own").</summary>
    private void RefreshBoxes()
    {
        var boxes = new List<TaskbarBox>();
        var run = new List<TaskbarSensorRow>();
        void Flush()
        {
            foreach (var box in TrayParts.Boxes(run, r => r.Alone || r.Id == TrayParts.NetBoth)) boxes.Add(new TaskbarBox(boxes.Count + 1, box));
            run.Clear();
        }
        TaskbarSensorRow? before = null;
        foreach (var row in Sensors)
        {
            if (row.Style == TrayStyle.TwoRows && (before is null || before.Style == TrayStyle.TwoRows && before.Size == row.Size)) run.Add(row);
            else
            {
                Flush();
                if (row.Style == TrayStyle.TwoRows) run.Add(row);
            }
            before = row;
        }
        Flush();
        _boxes = boxes;
        foreach (var row in Sensors) row.BoxTitle = "";
        foreach (var box in boxes)
            foreach (var row in box.Rows)
                row.BoxTitle = box.Rows.Count > 1 ? Lang.T($"Box {box.Number}") : row.Alone ? Lang.T($"Box {box.Number} · on its own") : Lang.T($"Box {box.Number} · one reading");
        OnPropertyChanged(nameof(Boxes));
    }

    /// <summary>Windows 11's taskbar, light and dark (as the page's preview draws it).</summary>
    private static readonly Brush LightBar = Frozen(new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE))), DarkBar = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)));

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
        RefreshBoxes();
        Redraw();
    }

    [RelayCommand]
    private void RemoveSensor(TaskbarSensorRow row)
    {
        _settings.Update(s =>
        {
            s.TraySensors.Remove(row.Id);
            s.TrayStyles.Remove(row.Id);
            s.TraySizes.Remove(row.Id);
            s.TrayLookFrom.Remove(row.Id);
        });
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
        foreach (var item in _live.NetSensors) byId.TryAdd(item.Id, item);
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
            row.LookPictureOf ??= LookPicture;
            row.BarBrush = TaskbarLight ? LightBar : DarkBar;
            row.LookFromSensor ??= SensorById;
            row.LookFromCandidates ??= LookFromCandidates;
            row.AlonePicked ??= SetAlone;
            row.LookPicked ??= SetLook;
            row.StylePicked ??= SetStyleOf;
            row.SizePicked ??= SetSizeOf;
            row.LookFromPicked ??= SetLookFrom;
            row.Look = settings.TrayLooks.GetValueOrDefault(row.Id);
            row.Style = settings.TrayStyles.GetValueOrDefault(row.Id, settings.TrayStyle);
            row.Size = settings.TraySizes.GetValueOrDefault(row.Id);
            row.LookFrom = settings.TrayLookFrom.GetValueOrDefault(row.Id);
            row.NumberColor = TrayColors.NumberOf(settings, row.Id);
            row.NameColor = TrayColors.NameOf(settings, row.Id);
            row.TileColor = TrayColors.TileOf(settings, row.Id);
            row.Alone = settings.TrayAlone.Contains(row.Id);
        }
        OnPropertyChanged(nameof(HasSensors));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(QuickPicks));
        OnPropertyChanged(nameof(HasQuickPicks));
        OnPropertyChanged(nameof(ShowCommon));
        OnPropertyChanged(nameof(AddList));
        OnPropertyChanged(nameof(AddListEmpty));
        OnPropertyChanged(nameof(InStrip));
        OnPropertyChanged(nameof(TwoRows));
        OnPropertyChanged(nameof(Separate));
        OnPropertyChanged(nameof(Grouped));
        OnPropertyChanged(nameof(ShowWidth));
        OnPropertyChanged(nameof(ShowNames));
        OnPropertyChanged(nameof(ShowSpeeds));
        OnPropertyChanged(nameof(StripNote));
        RefreshBoxes();
        OnPropertyChanged(nameof(WaitingText));
        OnPropertyChanged(nameof(AllSensors));
        Redraw();
    }

    /// <summary>A look's picture for its picker (strip looks drawn on the strip, icon looks on an icon).</summary>
    internal BitmapSource? LookPicture(TaskbarSensorRow row, TrayLook look) => PictureOf(row, look, row.Style == TrayStyle.TwoRows ? TrayStyle.Strip : row.Style);
}
