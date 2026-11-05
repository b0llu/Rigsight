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
/// A choice in one of the page's floating lists (a look, who a spot is shared with, colours, background): its name, a
/// line under it, its picture as the taskbar draws it (looks only), whether it's the one in use, and what picking it does.
/// </summary>
public sealed record TaskbarChoice(string Name, string Detail, BitmapSource? Picture, bool Selected, Action Pick)
{
    /// <summary>A tick for the one in use.</summary>
    public string Glyph => Selected ? "" : "";
    public bool HasPicture => Picture is not null;
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// A line in a spot's "Paired with" list: a part's heading, None, or a reading of this PC (one in the taskbar says so;
/// one that can't share this spot is greyed), and what picking it does.
/// </summary>
public sealed record TaskbarPartnerItem(string? Heading, string Name, string Detail, string Value, bool Enabled, bool Selected, Action? Pick, string? Id = null)
{
    public bool IsHeading => Heading is not null;
    public bool IsChoice => Heading is null;
    public bool HasDetail => Detail.Length > 0;
    /// <summary>A tick for the one it's paired with now.</summary>
    public string Glyph => Selected ? "" : "";
}

/// <summary>
/// A sensor in the taskbar: what it is, the part it's about (its colour), its live reading, how it shows (its style,
/// size, look and spot, set for its spot on the page) and its own colours.
/// </summary>
public sealed partial class TaskbarSensorRow(string id, string name, string hardware, SensorItem? sensor) : ObservableObject
{
    /// <summary>How it draws (see <see cref="TrayLook"/>): on the strip every look, on an icon its number, Fill (Level) or Ring.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LookName), nameof(ShownLook), nameof(ShowsNameColor), nameof(ColorsHint), nameof(HasGraph))]
    private TrayLook _look;

    /// <summary>Its graph behind its name, under its number (its own choice; else beside its number).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GraphPlaceText))]
    private bool _graphBehind;

    /// <summary>Whether its look is a graph (filled, line, bar, mirrored) in the taskbar's own style: it can go beside or behind.</summary>
    public bool HasGraph => Style == TrayStyle.Strip && ShownLook is TrayLook.Area or TrayLook.Line or TrayLook.Bars or TrayLook.Mirror;

    /// <summary>"Beside" or "Behind", for its graph's switch.</summary>
    public string GraphPlaceText
    {
        get => GraphBehind ? "Behind" : "Beside";
        set { bool behind = value == "Behind"; if (behind != GraphBehind) GraphPlacePicked?.Invoke(this, behind); }
    }

    /// <summary>"Graph", or in a pair "Graph of CPU load".</summary>
    [ObservableProperty]
    private string _graphTitle = "";

    internal Action<TaskbarSensorRow, bool>? GraphPlacePicked;

    /// <summary>Its style: its own, else the taskbar's (see <see cref="RigsightSettings.TrayStyles"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LookName), nameof(ShownLook), nameof(LookHint), nameof(ShowsNameColor), nameof(ColorsHint), nameof(HasGraph))]
    private TrayStyle _style;

    /// <summary>Its size (see <see cref="TraySize"/>).</summary>
    [ObservableProperty]
    private TraySize _size;

    /// <summary>In grayscale, its own or the taskbar's; <see cref="GrayOwn"/> when its own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Colored), nameof(ColorsHint))]
    private bool _isGray;

    /// <summary>
    /// Under its colours' title: the colours it draws (its number always; its name, or on an icon its ring or fill; its
    /// tile when on one), or that they wait while it's in grayscale.
    /// </summary>
    public string ColorsHint
    {
        get
        {
            if (!Colored) return Lang.T("Grayscale hides its own colors. Pick Color above to use them.");
            var parts = new List<string> { Lang.T("number") };
            if (ShowsNameColor) parts.Add(SecondWord);
            if (ShowsTileColor) parts.Add(Lang.T("tile"));
            string list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " " + Lang.T("and") + " " + parts[^1];
            return Lang.T($"Its {list}.");
        }
    }

    /// <summary>
    /// Whether its second colour shows: its name and graph (not a big number, which is the number alone), or on an icon its
    /// ring or fill (a plain icon is its number on its tile).
    /// </summary>
    public bool ShowsNameColor => Sensor is not null && (TrayParts.IsIcon(Style)
        ? ShownLook is TrayLook.Level or TrayLook.Ring
        : !(Style == TrayStyle.Strip && ShownLook == TrayLook.Big));

    /// <summary>Whether its tile's colour shows: on a tile only.</summary>
    public bool ShowsTileColor => OnTile;

    /// <summary>What its second colour is drawn on: its name, or on an icon its ring or fill.</summary>
    public string SecondWord => !TrayParts.IsIcon(Style) ? Lang.T("name") : ShownLook == TrayLook.Ring ? Lang.T("ring") : Lang.T("fill");

    /// <summary>On a tile, its own or the taskbar's; <see cref="TilesOwn"/> when its own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsTileColor), nameof(ColorsHint))]
    private bool _onTile;

    /// <summary>Its own colour or grayscale, and its own background (null: like the rest).</summary>
    public bool? GrayOwn { get; set; }
    public bool? TilesOwn { get; set; }

    /// <summary>Its name as one letter in two rows (its spot's choice).</summary>
    public bool Letters { get; set; }

    /// <summary>Whether it shares its spot with the next (null: no pairs saved yet, as before 1.3).</summary>
    public bool? PairNext { get; set; }

    /// <summary>The reading its look is drawn from (null: its own; from before 1.3.0's pairs, kept as it was).</summary>
    public string? LookFrom { get; set; }

    /// <summary>The kind its look is drawn from (another reading's, with one; else its own).</summary>
    public SensorKind? LookFromKind => LookFrom is null ? null : LookFromSensor?.Invoke(LookFrom)?.Kind;

    /// <summary>The look as it draws: on the strip a filled graph in place of a fill-up look it can't have; on an icon its number, Fill or Ring.</summary>
    public TrayLook ShownLook => Sensor is null ? TrayLook.Number
        : TrayParts.IsIcon(Style) ? TrayParts.IconLook(LookFromKind ?? Sensor.Kind, Look)
        : TrayParts.LookFor(LookFromKind ?? Sensor.Kind, Look, Id == TrayParts.NetBoth);

    public string LookName => TrayParts.IsIcon(Style) ? TrayParts.IconLookName(ShownLook) : TrayParts.LookName(ShownLook);

    /// <summary>The looks it can have in its style (an icon's, or the strip's), each a choice with its picture.</summary>
    public IReadOnlyList<TaskbarChoice> LookChoices()
    {
        if (Sensor is null) return [];
        bool icon = TrayParts.IsIcon(Style);
        var looks = icon ? TrayParts.IconLooksFor(Sensor.Kind, LookFromKind)
            : TrayParts.LooksFor(Sensor.Kind, TrayParts.IsCpuTotal(Sensor.Kind, Part, Sensor.Name), Id == TrayParts.NetBoth, LookFromKind);
        return [.. looks.Select(l => new TaskbarChoice(icon ? TrayParts.IconLookName(l) : TrayParts.LookName(l), "", LookPictureOf?.Invoke(this, l), l == ShownLook,
            () => PickLook(l)))];
    }

    /// <summary>"Look", or in a pair "Look of CPU load" (by its name when both are the same kind: "Look of CCD1 (Tdie)").</summary>
    [ObservableProperty]
    private string _lookTitle = "";

    /// <summary>"Its colors", or in a pair "Colors of CPU load".</summary>
    [ObservableProperty]
    private string _colorsTitle = "";

    /// <summary>What it's called in its spot: what it is ("CPU load"), or its own name when its pair is the same kind.</summary>
    public string SpotName { get; set; } = "";

    /// <summary>Under its look's title: how the look is drawn.</summary>
    public string LookHint => TrayParts.IsIcon(Style)
        ? Lang.T("The icon can fill up, or ring round, as the reading rises.")
        : Lang.T("Drawn beside its number, never over it.");

    /// <summary>Its picture as the taskbar draws it now, alone, on its look's button.</summary>
    [ObservableProperty]
    private BitmapSource? _picture;

    /// <summary>The taskbar's colour behind its pictures (Windows' light or dark taskbar, as the pictures are drawn for it).</summary>
    public Brush BarBrush { get; internal set; } = Brushes.Black;

    /// <summary>In two rows before 1.3's pairs, a box of its own (see <see cref="RigsightSettings.TrayAlone"/>).</summary>
    public bool Alone { get; set; }

    /// <summary>Told when a look is picked, to save it.</summary>
    internal Action<TaskbarSensorRow, TrayLook>? LookPicked;

    /// <summary>The page's: a look's picture for this reading (in its spot), a sensor by id.</summary>
    internal Func<TaskbarSensorRow, TrayLook, BitmapSource?>? LookPictureOf;
    internal Func<string, SensorItem?>? LookFromSensor;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Hardware { get; } = sensor is null ? Lang.T("Not found on this PC right now") : hardware;
    public SensorItem? Sensor { get; } = sensor;
    public bool Found => Sensor is not null;
    public TrayPart Part { get; } = TrayParts.PartOf(sensor?.HardwareType);
    public string PartLabel => TrayParts.Label(Part);

    /// <summary>What it is, in words: "CPU temperature", "GPU load"; a speed by its own name ("Download speed").</summary>
    public string KindPhrase => Sensor is null || TrayParts.IsNet(Id) ? Name : TaskbarViewModel.KindPhrase(PartLabel, Sensor.Kind);

    /// <summary>The word under it on the strip (after its part's first, see <see cref="TrayParts.ShortLabel"/>).</summary>
    public string Short => Sensor is null ? ""
        : TrayParts.IsNet(Id) ? (OwnShort ? Sensor.CustomLabel!.Trim() : Id == TrayParts.NetUp ? Lang.T("Up") : Lang.T("Down"))
        : TrayParts.ShortLabel(Sensor.Kind, Sensor.Name, Part, Sensor.CustomLabel);

    /// <summary>Whether <see cref="Short"/> is a name given on All sensors (it wins in two rows too).</summary>
    public bool OwnShort => Sensor?.CustomLabel?.Trim() is { Length: > 0 and <= TrayParts.MaxShortLabel };

    /// <summary>What it's grouped with (see <see cref="TrayParts.GroupOf"/>).</summary>
    public string Group => TrayParts.GroupOf(Part, Id);

    /// <summary>What its spot depends on (see <see cref="TrayParts.Spots"/>).</summary>
    public TrayParts.SpotOf Spot => new(Id, Group, Sensor?.Kind ?? SensorKind.Factor, Alone, Id == TrayParts.NetBoth, PairNext);

    /// <summary>The number's colour picked (null: as RigSight colours it; <see cref="TrayColors.Heat"/>; or a code).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberDot))]
    private string? _numberColor;

    /// <summary>The name's colour picked, which its graph and its icon's tile take too (null: its part's).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark))]
    private string? _nameColor;

    /// <summary>The tile's colour picked (null: the usual tile).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileDot))]
    private string? _tileColor;

    /// <summary>The tile's colour on its colours' button: the one picked, else none (an outline only).</summary>
    public Brush TileDot => TrayColors.Parse(TileColor) is { } c ? TaskbarViewModel.SolidOf(c) : Brushes.Transparent;

    public bool IsTemperature => Sensor?.Kind == SensorKind.Temperature;

    /// <summary>Its name's colour (its part's unless one was picked), as the taskbar marks it.</summary>
    public Brush Mark => TaskbarViewModel.NameBrush(this, light: false, gray: false);

    /// <summary>The number's colour on its Pick button: by heat (blue, green, amber), the one picked, or the text's.</summary>
    public Brush NumberDot => TaskbarViewModel.NumberDot(this);

    /// <summary>In colour, so its colours can be picked (in grayscale they wait).</summary>
    public bool Colored => !IsGray && Found;

    public string ColorsName => Lang.T($"Colors for {Name}");

    private void PickLook(TrayLook look)
    {
        Look = look;
        LookPicked?.Invoke(this, look);
    }
}

/// <summary>
/// A spot in the taskbar, a row on the page: one reading, or two that share it (side by side on one tile, one over the
/// other in two rows, stacked in one icon). Its settings are set for the whole spot; its looks for each reading.
/// </summary>
public sealed partial class TaskbarSpot : ObservableObject
{
    private readonly TaskbarViewModel _page;

    internal TaskbarSpot(IReadOnlyList<TaskbarSensorRow> rows, TaskbarViewModel page)
    {
        Rows = rows;
        _page = page;
    }

    public IReadOnlyList<TaskbarSensorRow> Rows { get; }
    public TaskbarSensorRow First => Rows[0];
    public TaskbarSensorRow? Second => Rows.Count > 1 ? Rows[1] : null;
    public bool IsPair => Rows.Count > 1;

    /// <summary>Its key: its first reading's id (it stays open by it as the list is rebuilt).</summary>
    public string Key => First.Id;

    /// <summary>
    /// A pair by what they are ("CPU temperature and load", "CPU temperature and GPU load"); by their names when they're
    /// the same kind ("Core (Tctl/Tdie) and CCD1 (Tdie)"); one reading by its name.
    /// </summary>
    public string Title
    {
        get
        {
            if (!IsPair) return First.Name;
            var a = First;
            if (Rows.Skip(1).Any(b => b.KindPhrase == a.KindPhrase)) return Lang.T($"{a.Name} and {string.Join(", ", Rows.Skip(1).Select(b => b.Name))}");
            var rest = Rows.Skip(1).Select(b => b.Found && a.Found && b.Part == a.Part && !TrayParts.IsNet(b.Id) && !TrayParts.IsNet(a.Id)
                ? TaskbarViewModel.KindWord(b.Sensor!.Kind) : b.KindPhrase);
            return Lang.T($"{a.KindPhrase} and {string.Join(", ", rest)}");
        }
    }

    /// <summary>Under one reading's name: what it is (a speed: where it's counted).</summary>
    public string What => TrayParts.IsNet(First.Id) || !First.Found ? First.Hardware : First.KindPhrase;

    /// <summary>Its stripe: its first reading's colour on top, its second's under it.</summary>
    public Brush StripeTop => First.Mark;
    public Brush StripeBottom => Rows[^1].Mark;

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Its picture as the taskbar draws it now, on its row.</summary>
    [ObservableProperty]
    private BitmapSource? _picture;

    /// <summary>Where a reading dragged over it would go: above or under it (a line shows there).</summary>
    [ObservableProperty]
    private bool _dropAbove, _dropBelow;

    /// <summary>The last in the list: no line under it (the card's edge is there).</summary>
    [ObservableProperty]
    private bool _isLast;

    /// <summary>Its style, an icon per part counted as an icon.</summary>
    public TrayStyle Style => First.Style == TrayStyle.Grouped ? TrayStyle.Icons : First.Style;

    /// <summary>"Strip", "TwoRows" or "Icons", for its Shows as switch.</summary>
    public string ShowsText
    {
        get => Style.ToString();
        set { if (Enum.TryParse<TrayStyle>(value, out var style) && style != Style) _page.SetShowsAs(this, style); }
    }

    /// <summary>The first of its Shows as: one reading "Standard" (its number over its name), a pair "Side by side".</summary>
    public string StripWord => IsPair ? Lang.T("Side by side") : TrayParts.StyleName(TrayStyle.Strip);

    public string ShowsHint => IsPair
        ? Lang.T("Two readings in one spot, side by side, one over the other or stacked in an icon.")
        : Lang.T("Standard puts its number over its name, with a look. Two rows puts it beside its name, in a box. Icon shows only an icon.");

    /// <summary>
    /// Whether it can be paired: both speeds are two lines already, so in two rows or an icon they fill it (they pair
    /// only side by side).
    /// </summary>
    public bool CanPair => First.Found && (Style == TrayStyle.Strip || !Rows.Any(r => r.Id == TrayParts.NetBoth));

    /// <summary>Who it's paired with ("CPU load"), or None.</summary>
    public string PartnerText => IsPair ? string.Join(", ", Rows.Skip(1).Select(r => r.SpotName)) : Lang.T("None");

    public string PartnerHint => IsPair
        ? Lang.T("Shown together in one spot. Pick None to split them.")
        : Lang.T("Show another reading in the same spot, picked from every reading on this PC.");

    /// <summary>The readings with a look to pick: none in two rows, an icon's first (it draws the icon's look), else each.</summary>
    public IReadOnlyList<TaskbarSensorRow> LookRows => !First.Found || Style == TrayStyle.TwoRows ? [] : Style == TrayStyle.Icons ? [First] : [.. Rows.Where(r => r.Found)];

    public bool ShowNames => Style == TrayStyle.TwoRows;

    /// <summary>"Full" or "Letter": two rows' names in full ("CPU", "Load") or as one letter ("C", "L").</summary>
    public string NamesText
    {
        get => First.Letters ? "Letter" : "Full";
        set { bool letters = value == "Letter"; if (letters != First.Letters) _page.SetLetters(this, letters); }
    }

    /// <summary>"Auto", "Small", "Medium" or "Large", for its size switch.</summary>
    public string SizeText
    {
        get => First.Size.ToString();
        set { if (Enum.TryParse<TraySize>(value, out var size) && size != First.Size) _page.SetSize(this, size); }
    }

    /// <summary>Whether a size fits the taskbar as it is (set by the page as it measures the taskbar).</summary>
    [ObservableProperty]
    private bool _canSmall = true, _canMedium = true, _canLarge = true;

    /// <summary>Why a size it can't have now is off, and what to do (empty: every size fits).</summary>
    [ObservableProperty]
    private string _sizeNote = "";

    /// <summary>"Shown smaller for now": its size doesn't fit this taskbar (empty when it does).</summary>
    [ObservableProperty]
    private string _sizeNow = "";

    // Colors and background: a switch showing what it has now (its own, or the taskbar's), a pick giving it its own,
    // and "Like the rest" by the title to follow the taskbar again (Windows Terminal's settings do the same).

    /// <summary>"Color" or "Grayscale" as it shows now, for its switch; picking one makes it its own.</summary>
    public string ColorsText
    {
        get => First.IsGray ? "Grayscale" : "Color";
        set { bool gray = value == "Grayscale"; if (gray != First.IsGray) _page.SetGray(this, gray); }
    }

    public bool ColorsOwn => First.GrayOwn is not null;

    public string ColorsHint => !ColorsOwn ? Lang.T("Like the rest of the taskbar. Pick one to give it its own.")
        : _page.Gray ? Lang.T("Its own. The rest of the taskbar is in grayscale.") : Lang.T("Its own. The rest of the taskbar is in color.");

    /// <summary>"Tiles" or "None" as it shows now, for its switch; picking one makes it its own.</summary>
    public string BackgroundText
    {
        get => First.OnTile ? "Tiles" : "None";
        set { bool tiles = value == "Tiles"; if (tiles != First.OnTile) _page.SetTiles(this, tiles); }
    }

    public bool BackgroundOwn => First.TilesOwn is not null;

    public string BackgroundHint => !BackgroundOwn ? Lang.T("Like the rest of the taskbar. Pick one to give it its own.")
        : _page.Tiles ? Lang.T("Its own. The rest of the taskbar is on tiles.") : Lang.T("Its own. The rest of the taskbar has no tiles.");

    /// <summary>The readings whose own colours can be picked here (a row each, its colours' swatches on its button).</summary>
    public IReadOnlyList<TaskbarSensorRow> PickRows => [.. Rows.Where(r => r.Found)];

    /// <summary>"Side by side · filled graph · small · no tile": how it shows, in words, on its row.</summary>
    public string Summary
    {
        get
        {
            var bits = new List<string>
            {
                Style switch { TrayStyle.TwoRows => Lang.T("Two rows"), TrayStyle.Icons => Lang.T("Icon"), _ => StripWord },
            };
            // A pair's looks once each ("blocks", not "blocks · blocks").
            bits.AddRange(LookRows.Where(r => r.ShownLook != TrayLook.Number).Select(r => Lower(r.LookName)).Distinct());
            if (First.Size != TraySize.Auto) bits.Add(Lower(TrayParts.SizeName(First.Size)));
            if (First.GrayOwn is bool gray) bits.Add(Lower(gray ? Lang.T("Grayscale") : Lang.T("Color")));
            if (First.TilesOwn is bool tiles) bits.Add(tiles ? Lang.T("on a tile") : Lang.T("no tile"));
            if (ShowNames && First.Letters) bits.Add(Lang.T("one-letter names"));
            return string.Join(" · ", bits);
        }
    }

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    public string ToggleName => $"{Title}: {Summary}";
    public string MoveName => Lang.T($"Move {Title}: drag, or press the up and down arrows");
    public string RemoveName => Lang.T($"Remove {Title} from the taskbar");

    /// <summary>Everything it shows changed (the page's settings came again).</summary>
    internal void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void Toggle() => _page.Toggle(this);

    [RelayCommand]
    private void Remove() => _page.RemoveSpot(this);

    [RelayCommand]
    private void ResetColors() => _page.SetGray(this, null);

    [RelayCommand]
    private void ResetBackground() => _page.SetTiles(this, null);
}

/// <summary>
/// The Taskbar page: readings shown in the taskbar, each spot (one reading, or two sharing it) in its own style
/// (in the taskbar, two rows, an icon) and size, colours and background, at either end. Its preview is the agent's own
/// drawing (its StripComposer, linked into the app) at the taskbar's real height, so what is picked is what the taskbar
/// shows. As many readings as the user likes, in their order: the ones that don't fit wait in "+N".
/// </summary>
public sealed partial class TaskbarViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly LiveData _live;

    /// <summary>The preview's drawing, and the pictures' (each spot and reading alone, so each look has its minutes).</summary>
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

    /// <summary>Every reading in the taskbar, in its order.</summary>
    public ObservableCollection<TaskbarSensorRow> Sensors { get; } = [];

    /// <summary>The taskbar's spots, in its order: a row each on the page.</summary>
    public ObservableCollection<TaskbarSpot> Spots { get; } = [];

    public bool HasSensors => Sensors.Count > 0;

    /// <summary>The spot open on the page (its key), kept as the list is rebuilt.</summary>
    private string? _openKey;

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
        // A pair goes in one after the other, the temperature first, sharing a spot.
        _settings.Update(s =>
        {
            SavePairs(s);
            if (!s.TraySensors.Contains(pick.Sensor.Id)) AddId(s, pick.Sensor.Id);
            if (pick.Second is { } second && !s.TraySensors.Contains(second.Id))
            {
                AddId(s, second.Id);
                Join(s, pick.Sensor.Id, second.Id);
            }
        });
        LoadSensors();
    }

    // ── Adding a reading: the search box at the end of the readings, its list floating under it ──

    /// <summary>What's typed in the "Add a reading" box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddList), nameof(ShowCommon))]
    private string _addSearch = "";

    /// <summary>The common readings first, as pickers pin the most used (only while nothing is typed).</summary>
    public bool ShowCommon => AddSearch.Trim().Length == 0 && HasQuickPicks;

    /// <summary>Whether a sensor matches what's typed: its name, hardware, part or kind.</summary>
    private static bool Matches(SensorItem s, string q) => q.Length == 0 || s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
        || s.HardwareShown.Contains(q, StringComparison.OrdinalIgnoreCase)
        || TrayParts.Label(TrayParts.PartOf(s.HardwareType)).Contains(q, StringComparison.OrdinalIgnoreCase)
        || Units.TypeLabel(s.Kind).Contains(q, StringComparison.OrdinalIgnoreCase);

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
            var inTaskbar = Sensors.Select(r => r.Id).ToHashSet();
            var list = new List<TaskbarAddItem>();
            foreach (var part in PartOrder)
            {
                var sensors = AllSensors.Where(s => TrayParts.PartOf(s.HardwareType) == part && Matches(s, q)).ToList();
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
            SavePairs(s);
            AddId(s, sensor.Id);
        });
        LoadSensors();
    }

    /// <summary>A reading not in the taskbar yet, added to share a spot: next to that spot's first (its second, if it had one, gets its own).</summary>
    private void AddPartner(TaskbarSpot spot, SensorItem sensor)
    {
        _settings.Update(s =>
        {
            SavePairs(s);
            if (!s.TraySensors.Contains(sensor.Id)) AddId(s, sensor.Id);
            if (s.TraySensors.Contains(spot.Key)) Join(s, spot.Key, sensor.Id);
        });
        LoadSensors();
    }

    /// <summary>
    /// A spot's "Paired with" list: None, then every reading of this PC by part (what's typed narrows it). One already
    /// in the taskbar on its own moves next to this spot; one not in it yet is added there. Greyed: one in another pair,
    /// and both speeds while the spot is in two rows or an icon (their two lines fill it).
    /// </summary>
    public IReadOnlyList<TaskbarPartnerItem> PartnerItems(TaskbarSpot spot, string query)
    {
        string q = query.Trim();
        var list = new List<TaskbarPartnerItem>();
        if (q.Length == 0) list.Add(new(null, Lang.T("None"), Lang.T("A spot of its own"), "", true, !spot.IsPair, () => SetPartner(spot, null)));
        bool fullBoxes = spot.Style != TrayStyle.Strip;
        foreach (var part in PartOrder)
        {
            var sensors = AllSensors.Where(s => s.Id != spot.First.Id && TrayParts.PartOf(s.HardwareType) == part && Matches(s, q)).ToList();
            if (sensors.Count == 0) continue;
            list.Add(new(TrayParts.Label(part), "", "", "", false, false, null));
            foreach (var s in sensors)
            {
                bool partner = spot.Rows.Skip(1).Any(r => r.Id == s.Id);
                var inSpot = Spots.FirstOrDefault(o => o.Rows.Any(r => r.Id == s.Id));
                bool canJoin = !(fullBoxes && s.Id == TrayParts.NetBoth) && (inSpot is null || !inSpot.IsPair);
                string detail = partner ? Lang.T("Paired with it now")
                    : inSpot is null ? s.HardwareShown
                    : inSpot.IsPair ? Lang.T("In the taskbar, in a pair") : Lang.T("In the taskbar");
                Action? pick = partner || !canJoin ? null : inSpot is not null ? () => SetPartner(spot, inSpot) : () => AddPartner(spot, s);
                list.Add(new(null, s.DisplayName, detail, s.FormattedValue ?? "", pick is not null, partner, pick, s.Id));
            }
        }
        return list;
    }

    /// <summary>A reading added at the end, in the style of the last one in the list (the readings next to it look alike).</summary>
    private static void AddId(RigsightSettings s, string id)
    {
        if (s.TraySensors.Count > 0 && s.TrayStyles.GetValueOrDefault(s.TraySensors[^1], s.TrayStyle) is var last && last != s.TrayStyle)
            s.TrayStyles[id] = last;
        s.TraySensors.Add(id);
    }

    // ── A spot's settings ──

    internal void Toggle(TaskbarSpot spot)
    {
        bool open = !spot.IsOpen;
        foreach (var other in Spots) other.IsOpen = false;
        spot.IsOpen = open;
        _openKey = open ? spot.Key : null;
    }

    /// <summary>A spot shown in the taskbar, two rows or as an icon: each of its readings.</summary>
    internal void SetShowsAs(TaskbarSpot spot, TrayStyle style)
    {
        _settings.Update(s =>
        {
            SavePairs(s);
            foreach (var row in spot.Rows)
            {
                s.TrayStyles.Remove(row.Id);
                if (style != s.TrayStyle) s.TrayStyles[row.Id] = style;
            }
            // Both speeds fill a box or an icon with their two lines: there they part from what they were paired with.
            if (style != TrayStyle.Strip && spot.IsPair && spot.Rows.Any(r => r.Id == TrayParts.NetBoth))
                foreach (var row in spot.Rows) s.TrayPairs!.Remove(row.Id);
        });
        LoadSensors();
    }

    internal void SetSize(TaskbarSpot spot, TraySize size)
    {
        _settings.Update(s =>
        {
            SavePairs(s);
            foreach (var row in spot.Rows)
            {
                s.TraySizes.Remove(row.Id);
                if (size != TraySize.Auto) s.TraySizes[row.Id] = size;
            }
        });
        LoadSensors();
    }

    internal void SetLetters(TaskbarSpot spot, bool letters)
    {
        _settings.Update(s =>
        {
            SavePairs(s);
            foreach (var row in spot.Rows)
            {
                s.TrayLetters.Remove(row.Id);
                if (letters) s.TrayLetters.Add(row.Id);
            }
        });
        LoadSensors();
    }

    /// <summary>A spot's colours: like the rest (null), in colour, or grayscale.</summary>
    internal void SetGray(TaskbarSpot spot, bool? gray)
    {
        _settings.Update(s =>
        {
            SavePairs(s);
            foreach (var row in spot.Rows)
            {
                s.TrayGrayOf.Remove(row.Id);
                if (gray is bool g) s.TrayGrayOf[row.Id] = g;
            }
        });
        LoadSensors();
    }

    /// <summary>A spot's background: like the rest (null), on a tile, or none.</summary>
    internal void SetTiles(TaskbarSpot spot, bool? tiles)
    {
        _settings.Update(s =>
        {
            SavePairs(s);
            foreach (var row in spot.Rows)
            {
                s.TrayTilesOf.Remove(row.Id);
                if (tiles is bool t) s.TrayTilesOf[row.Id] = t;
            }
        });
        LoadSensors();
    }

    /// <summary>A spot's own (null: none, each reading a spot of its own), or shared with another spot's reading.</summary>
    internal void SetPartner(TaskbarSpot spot, TaskbarSpot? other)
    {
        _settings.Update(s =>
        {
            SavePairs(s);
            if (other is null)
            {
                foreach (var row in spot.Rows) s.TrayPairs!.Remove(row.Id);
                return;
            }
            Join(s, spot.Key, other.Key);
        });
        LoadSensors();
    }

    /// <summary>
    /// <paramref name="id"/> moved next to <paramref name="firstId"/>, sharing its spot: the spot's second (if it had one)
    /// gets one of its own where it is, and the one joining takes the spot's style, size, colours, background and names.
    /// </summary>
    private static void Join(RigsightSettings s, string firstId, string id)
    {
        var pairs = s.TrayPairs!;
        // The spot is the first and whoever it shares with: they part (its second stays where it is, alone).
        pairs.Remove(firstId);
        // The one joining leaves its own spot (it shared with its next, and the one before it with it).
        pairs.Remove(id);
        int at = s.TraySensors.IndexOf(id);
        if (at > 0) pairs.Remove(s.TraySensors[at - 1]);
        s.TraySensors.Remove(id);
        int first = s.TraySensors.IndexOf(firstId);
        if (first < 0) { s.TraySensors.Add(id); return; }
        s.TraySensors.Insert(first + 1, id);
        pairs.Add(firstId);
        void Copy<T>(Dictionary<string, T> of)
        {
            of.Remove(id);
            if (of.TryGetValue(firstId, out var v)) of[id] = v;
        }
        Copy(s.TrayStyles);
        Copy(s.TraySizes);
        Copy(s.TrayGrayOf);
        Copy(s.TrayTilesOf);
        s.TrayLetters.Remove(id);
        if (s.TrayLetters.Contains(firstId)) s.TrayLetters.Add(id);
    }

    /// <summary>
    /// The pairs saved, the first time the page changes anything (before 1.3 the spots came from the parts and the list's
    /// order): the spots as they show now, each pair's readings next to each other in the list, so nothing moves.
    /// </summary>
    private void SavePairs(RigsightSettings s)
    {
        if (s.TrayPairs is not null) return;
        var order = Spots.SelectMany(spot => spot.Rows.Select(r => r.Id)).Where(s.TraySensors.Contains).ToList();
        foreach (var id in s.TraySensors) if (!order.Contains(id)) order.Add(id);
        s.TraySensors = order;
        s.TrayPairs = [.. Spots.SelectMany(spot => spot.Rows.Take(spot.Rows.Count - 1).Select(r => r.Id)).Where(s.TraySensors.Contains)];
        s.TrayLetters = s.TrayLetterNames ? [.. order] : [];
        s.TrayLetterNames = false;
    }

    internal void RemoveSpot(TaskbarSpot spot)
    {
        _settings.Update(s =>
        {
            SavePairs(s);
            foreach (var row in spot.Rows)
            {
                s.TraySensors.Remove(row.Id);
                s.TrayStyles.Remove(row.Id);
                s.TraySizes.Remove(row.Id);
                s.TrayLookFrom.Remove(row.Id);
                s.TrayGrayOf.Remove(row.Id);
                s.TrayTilesOf.Remove(row.Id);
                s.TrayLetters.Remove(row.Id);
                s.TrayPairs!.Remove(row.Id);
            }
            // The reading before it no longer shares with what came next.
            foreach (var id in s.TrayPairs!.ToList())
            {
                int i = s.TraySensors.IndexOf(id);
                if (i < 0 || i == s.TraySensors.Count - 1) s.TrayPairs.Remove(id);
            }
        });
        LoadSensors();
    }

    /// <summary>A spot moved to <paramref name="to"/> in the list of spots (a drag, or the arrow keys on its handle).</summary>
    public void MoveSpot(TaskbarSpot spot, int to)
    {
        var order = Spots.ToList();
        int from = order.IndexOf(spot);
        to = Math.Clamp(to, 0, order.Count - 1);
        if (from < 0 || from == to) return;
        order.RemoveAt(from);
        order.Insert(to, spot);
        _settings.Update(s =>
        {
            SavePairs(s);
            var ids = order.SelectMany(o => o.Rows.Select(r => r.Id)).Where(s.TraySensors.Contains).ToList();
            foreach (var id in s.TraySensors) if (!ids.Contains(id)) ids.Add(id);
            s.TraySensors = ids;
        });
        LoadSensors();
    }

    /// <summary>A reading's graph behind its name, or beside its number.</summary>
    private void SetGraphBehind(TaskbarSensorRow row, bool behind)
    {
        _settings.Update(s =>
        {
            s.TrayGraphsBehind.Remove(row.Id);
            if (behind) s.TrayGraphsBehind.Add(row.Id);
        });
        row.GraphBehind = behind;
        Redraw();
        foreach (var spot in Spots) spot.Refresh();
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
        foreach (var spot in Spots) spot.Refresh();
    }

    // ── The whole taskbar ──

    /// <summary>The style every reading without one of its own has (see <see cref="TrayStyle"/>).</summary>
    public TrayStyle Style => _settings.Current.TrayStyle;

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
        ? Lang.T("Your taskbar's icons are aligned left, so the readings stay next to the clock. Center them in Windows' taskbar settings to use the left side.")
        : "";

    /// <summary>Each reading on a tile of its own, unless it has its own background (see <see cref="RigsightSettings.TrayTiles"/>).</summary>
    public bool Tiles => _settings.Current.TrayTiles;

    // In the taskbar's own words: "Tiles", "Background" and "Full" mean other things on Reports and Widgets in other languages.
    public string TilesWord => Lang.C("taskbar", "Tiles");
    public string BackgroundWord => Lang.C("taskbar", "Background");
    public string FullWord => Lang.C("taskbar", "Full");

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

    /// <summary>"None", "Small", "Normal" or "Large": the room between the readings (see <see cref="TrayGap"/>).</summary>
    public string GapText
    {
        get => _settings.Current.TrayGap.ToString();
        set
        {
            if (!Enum.TryParse<TrayGap>(value, out var gap) || gap == _settings.Current.TrayGap) return;
            _settings.Update(s => s.TrayGap = gap);
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

    private SensorItem? SensorById(string id) => AllSensors.FirstOrDefault(s => s.Id == id);

    private void OnStyleChanged()
    {
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(nameof(Side));
        OnPropertyChanged(nameof(SideText));
        OnPropertyChanged(nameof(OnLeft));
        OnPropertyChanged(nameof(PreviewOnLeft));
        OnPropertyChanged(nameof(SideNote));
        OnPropertyChanged(nameof(ShowWidth));
        OnPropertyChanged(nameof(ShowSpeeds));
        OnPropertyChanged(nameof(SpeedsText));
        OnPropertyChanged(nameof(StripNote));
        OnPropertyChanged(nameof(Colors));
        OnPropertyChanged(nameof(Gray));
        OnPropertyChanged(nameof(Tiles));
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(WidthMode));
        OnPropertyChanged(nameof(WidthText));
        OnPropertyChanged(nameof(GapText));
        OnPropertyChanged(nameof(WaitingText));
        Redraw();
    }

    /// <summary>The parts' colours, or grayscale (the taskbar's text colour and grey), for every reading without its own.</summary>
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

    /// <summary>RigSight draws the readings in Windows 11's taskbar; on Windows 10 they show as its own tray icons.</summary>
    public static bool StripSupported => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>The width switch: only the strip has graphs to make roomier or narrower.</summary>
    public bool ShowWidth => Sensors.Any(r => r.Style == TrayStyle.Strip) && StripSupported;

    /// <summary>The speeds' unit switch: with a speed in the taskbar.</summary>
    public bool ShowSpeeds => Sensors.Any(r => r.Sensor?.Kind == SensorKind.Throughput);

    /// <summary>The place switch: RigSight draws every style in Windows 11's taskbar, at either end.</summary>
    public bool ShowPlace => StripSupported;

    /// <summary>Under the preview: only Windows' own tray icons (Windows 10) can be tucked behind its arrow.</summary>
    public bool ShowTrayNote => !StripSupported;

    /// <summary>Under the readings, when the strip or two rows were picked on Windows 10.</summary>
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
            string which = Core.Wording.JoinAnd(names);
            return names.Count == 1
                ? Lang.T($"{which} doesn't fit in the taskbar on this screen, so it waits in the +1 tile. The readings at the top of this list stay.")
                : Lang.T($"{which} don't fit in the taskbar on this screen, so they wait in the +{names.Count} tile. The readings at the top of this list stay.");
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
    /// The page's pixels to each of its units on screen: the window's scale times the app's own zoom (Ctrl + wheel), set
    /// by the page. Its pictures are drawn at it, so they land pixel for pixel: drawn at the taskbar's and then zoomed
    /// 120%, they were smeared (the user, round 10).
    /// </summary>
    public double PageScale { get; set; } = 1;

    /// <summary>The floating lists' pixels to each unit: the window's scale alone (a list is a window of its own, not zoomed).</summary>
    public double PopupScale { get; set; } = 1;

    /// <summary>The taskbar as the page draws it: as tall and wide as it is, in the page's own pixels (see <see cref="PageScale"/>).</summary>
    private (int Height, int Width, float Scale) Drawn => DrawnAt(PageScale);

    private (int Height, int Width, float Scale) DrawnAt(double at)
    {
        var (height, width, scale) = _bar;
        float page = (float)Math.Clamp(at, 0.5, 4);
        return ((int)Math.Round(height / scale * page), (int)Math.Round(width / scale * page), page);
    }

    /// <summary>
    /// Draws the preview again, and each spot's and reading's picture: the readings as now (their newest values, their
    /// graphs' last minutes), at the taskbar's real height and scale. The page calls this every second while it shows.
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
            _preview.Remember(readings, now);
            float graphWide = WidgetRenderer.StripGraphWidthFor(width, scale, WidthMode);
            if (readings.Count == 0) PreviewImage = null;
            else
            {
                var (dh, _, ds) = Drawn;
                var layout = _preview.Build(readings, Options(), graphWide, 0, dh, ds, now);
                using var bmp = layout.Render(out _, out _);
                PreviewImage = TaskbarBar.ToImage(bmp, ds);
            }
            // The pictures' composer keeps every reading's minutes (each drawn on the strip, so a look switched to a graph has them).
            _pictures.Remember([.. readings.Select(r => r with { Style = TrayStyle.Strip })], now);
            foreach (var spot in Spots) spot.Picture = SpotPicture(spot);
            foreach (var row in Sensors) row.Picture = RowPicture(row, row.Look);
            UpdateSizes();
        }
        catch (Exception ex) { Log.Write("taskbar", $"The preview couldn't be drawn: {ex.Message}"); }
    }

    /// <summary>Below this height (at 100%) the strip goes to one line (as the agent's StripTwoLineHeight).</summary>
    private const double WidgetRendererTwoLineHeight = 36;

    /// <summary>
    /// A spot as the taskbar will draw it, alone (its readings sharing it), on its row: drawn for the taskbar's colour
    /// and shown on it, pixel for pixel (drawn for the page's own darker colour, straight on it, its words looked thin
    /// and soft: the user, round 9).
    /// </summary>
    private BitmapSource? SpotPicture(TaskbarSpot spot)
    {
        if (!spot.First.Found) return null;
        var readings = spot.Rows.Select((row, i) => Reading(row) with { Style = spot.Style, PairNext = i < spot.Rows.Count - 1, Alone = false }).ToList();
        return Picture(readings, spot.First.Size);
    }

    /// <summary>
    /// A reading alone with <paramref name="look"/>, as the taskbar will draw it in its spot's style (two rows' readings
    /// on the strip: two rows have no looks), on its look's button and in its list: only the reading the look is for.
    /// </summary>
    private BitmapSource? RowPicture(TaskbarSensorRow row, TrayLook look, double? drawAt = null)
    {
        if (row.Sensor is null) return null;
        // Side by side: the pair drawn with this look and only this reading's part of it kept, so it says what it says in
        // the taskbar ("CCD1" under the second, not "CPU" as it would alone).
        if (Spots.FirstOrDefault(s => s.Rows.Contains(row)) is { IsPair: true, Style: TrayStyle.Strip } spot)
        {
            int at = spot.Rows.ToList().IndexOf(row);
            var readings = spot.Rows.Select((r, i) => Reading(r) with { Look = r == row ? look : r.Look, Style = TrayStyle.Strip, PairNext = i < spot.Rows.Count - 1, Alone = false }).ToList();
            var (height, width, scale) = DrawnAt(drawAt ?? PageScale);
            var layout = _pictures.Build(readings, Options(), WidgetRenderer.StripGraphWidthFor(width, scale, WidthMode), 0,
                TrayParts.SizeHeight(row.Size, height, scale), scale, Environment.TickCount64);
            using var bmp = layout.Render(out _, out var valueEdges);
            if (valueEdges.Length == 1 && valueEdges[0] is { } edges && edges.Length == spot.Rows.Count)
            {
                int left = at == 0 ? 0 : (int)Math.Round(edges[at - 1]);
                int right = at == edges.Length - 1 ? bmp.Width : (int)Math.Round(edges[at]);
                if (right - left > 2)
                {
                    using var part = bmp.Clone(new System.Drawing.Rectangle(left, 0, right - left, bmp.Height), bmp.PixelFormat);
                    return TaskbarBar.ToImage(part, scale);
                }
            }
        }
        var style = row.Style == TrayStyle.Grouped ? TrayStyle.Icons : row.Style == TrayStyle.TwoRows ? TrayStyle.Strip : row.Style;
        return Picture([Reading(row) with { Look = look, Style = style, PairNext = false, Alone = true }], row.Size, drawAt);
    }

    private BitmapSource Picture(IReadOnlyList<TrayReading> readings, TraySize size, double? at = null)
    {
        var (height, width, scale) = DrawnAt(at ?? PageScale);
        var layout = _pictures.Build(readings, Options(), WidgetRenderer.StripGraphWidthFor(width, scale, WidthMode), 0,
            TrayParts.SizeHeight(size, height, scale), scale, Environment.TickCount64);
        using var bmp = layout.Render(out _, out _);
        return TaskbarBar.ToImage(bmp, scale);
    }

    /// <summary>A look's picture for its list: the reading alone with that look.</summary>
    internal BitmapSource? LookPicture(TaskbarSensorRow row, TrayLook look) => RowPicture(row, look, PopupScale);

    private StripComposer.Options Options(bool? light = null) =>
        new(light ?? TaskbarLight, Gray, Tiles, WidthMode, _settings.Current.TrayLetterNames, _settings.Current.TraySpeedBits, _settings.Current.TrayGap);

    /// <summary>
    /// A row as the agent reads it (AgentContext.TrayReadingsFor): its value now, its own style, size, colours,
    /// background, names and spot, the reading its look is drawn from, both speeds' upload, a CPU's cores.
    /// </summary>
    private TrayReading Reading(TaskbarSensorRow row)
    {
        if (row.Sensor is not { } s)
            return new TrayReading(row.Id, row.Name, SensorKind.Factor, null, TrayPart.Other, "", Alone: row.Alone, Style: row.Style, Size: row.Size,
                Gray: row.IsGray, Tiles: row.OnTile, Letters: row.Letters, PairNext: row.PairNext);
        var from = row.LookFrom is { } f ? SensorById(f) : null;
        float[]? cores = null;
        if (row.Look == TrayLook.Cores && from is null && TrayParts.IsCpuTotal(s.Kind, row.Part, s.Name))
        {
            var list = _live.AllSensors.Where(c => c.HardwareName == s.HardwareName && TrayParts.IsCoreLoad(c.Kind, c.Name)).ToList();
            if (list.Count > 0) cores = [.. list.Select(c => c.Value is double v ? (float)Math.Clamp(v / 100, 0, 1) : 0f)];
        }
        return new TrayReading(row.Id, row.Name, s.Kind, s.Value, row.Part, row.Hardware, row.Short, row.NumberColor, row.NameColor, row.Look, row.TileColor,
            row.OwnShort, row.Alone, row.Style, row.Size, from?.Id, from?.Kind, from?.Value,
            Second: row.Id == TrayParts.NetBoth ? _live.NetSensors[1].Value : null, Cores: cores,
            Gray: row.IsGray, Tiles: row.OnTile, Letters: row.Letters, PairNext: row.PairNext, GraphBehind: row.GraphBehind);
    }

    /// <summary>Which sizes fit the taskbar now, why the others don't, and which spots show smaller for now.</summary>
    private void UpdateSizes()
    {
        var (height, _, scale) = _bar;
        bool Fits(TraySize size) => TrayParts.SizeNeeds(size) * scale <= height + 0.5;
        var off = new[] { TraySize.Small, TraySize.Medium, TraySize.Large }.Where(z => !Fits(z)).ToList();
        int px = (int)Math.Round(height / scale);
        string note = off.Count == 0 ? ""
            : off.Count == 1
                ? Lang.T($"Your taskbar is {px} px tall and {TrayParts.SizeName(off[0])} needs {TrayParts.SizeNeeds(off[0])} px, so it's off. To use it, set Show smaller taskbar buttons to Never.")
                : Lang.T($"{string.Join(" and ", off.Select(TrayParts.SizeName))} need more than the {px} px your taskbar has, so they're off. To use them, set Show smaller taskbar buttons to Never.");
        foreach (var spot in Spots)
        {
            spot.CanSmall = Fits(TraySize.Small);
            spot.CanMedium = Fits(TraySize.Medium);
            spot.CanLarge = Fits(TraySize.Large);
            spot.SizeNote = note;
            spot.SizeNow = spot.First.Size != TraySize.Auto && !Fits(spot.First.Size) ? Lang.T("Shown smaller for now because the taskbar is too short for this size") : "";
        }
    }

    [RelayCommand]
    private void OpenTaskbarSettings() => TaskbarBar.OpenSettings();

    /// <summary>What a reading is, in words: its part and its kind ("CPU temperature", "GPU load").</summary>
    public static string KindPhrase(string part, SensorKind kind) => kind switch
    {
        SensorKind.Temperature => Lang.T($"{part} temperature"),
        SensorKind.Load => Lang.T($"{part} load"),
        SensorKind.Power => Lang.T($"{part} power"),
        SensorKind.Clock => Lang.T($"{part} clock"),
        SensorKind.Fan => Lang.T($"{part} fan"),
        SensorKind.Voltage => Lang.T($"{part} voltage"),
        SensorKind.Data or SensorKind.SmallData => Lang.T($"{part} data"),
        SensorKind.Throughput => Lang.T($"{part} speed"),
        _ => $"{part} {KindWord(kind)}",
    };

    /// <summary>A reading's kind in one word, after its part's name in a pair ("CPU temperature and load").</summary>
    public static string KindWord(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => Lang.T("temperature"),
        SensorKind.Load => Lang.T("load"),
        SensorKind.Power => Lang.T("power"),
        SensorKind.Clock => Lang.T("clock"),
        SensorKind.Fan => Lang.T("fan"),
        SensorKind.Voltage => Lang.T("voltage"),
        SensorKind.Data or SensorKind.SmallData => Lang.T("data"),
        SensorKind.Throughput => Lang.T("speed"),
        _ => Units.TypeLabel(kind).ToLowerInvariant(),
    };

    internal static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

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

    /// <summary>A reading's number colour for its Pick button: by heat, the one picked, or the text's.</summary>
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
        foreach (var spot in Spots) spot.Refresh();
        Redraw();
    }

    /// <summary>Rebuilds the list from settings when it changed (or the sensors behind it did), and its spots.</summary>
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
            row.LookPicked ??= SetLook;
            row.GraphPlacePicked ??= SetGraphBehind;
            row.GraphBehind = settings.TrayGraphsBehind.Contains(row.Id);
            row.Look = settings.TrayLooks.GetValueOrDefault(row.Id);
            row.Style = settings.TrayStyles.GetValueOrDefault(row.Id, settings.TrayStyle);
            row.Size = settings.TraySizes.GetValueOrDefault(row.Id);
            row.LookFrom = settings.TrayLookFrom.GetValueOrDefault(row.Id);
            row.NumberColor = TrayColors.NumberOf(settings, row.Id);
            row.NameColor = TrayColors.NameOf(settings, row.Id);
            row.TileColor = TrayColors.TileOf(settings, row.Id);
            row.Alone = settings.TrayAlone.Contains(row.Id);
            row.GrayOwn = settings.TrayGrayOf.TryGetValue(row.Id, out bool g) ? g : null;
            row.TilesOwn = settings.TrayTilesOf.TryGetValue(row.Id, out bool t) ? t : null;
            row.IsGray = TrayParts.GrayOf(settings, row.Id);
            row.OnTile = TrayParts.TilesOf(settings, row.Id);
            row.Letters = TrayParts.LettersOf(settings, row.Id);
            row.PairNext = TrayParts.PairNextOf(settings, row.Id);
        }
        LoadSpots();
        OnPropertyChanged(nameof(HasSensors));
        OnPropertyChanged(nameof(QuickPicks));
        OnPropertyChanged(nameof(HasQuickPicks));
        OnPropertyChanged(nameof(ShowCommon));
        OnPropertyChanged(nameof(AddList));
        OnPropertyChanged(nameof(AddListEmpty));
        OnPropertyChanged(nameof(ShowWidth));
        OnPropertyChanged(nameof(ShowSpeeds));
        OnPropertyChanged(nameof(StripNote));
        OnPropertyChanged(nameof(WaitingText));
        OnPropertyChanged(nameof(AllSensors));
        Redraw();
    }

    /// <summary>
    /// The spots as the agent makes them (runs of the same style, size, colours, background and names, each run's spots
    /// as <see cref="TrayParts.Spots"/> has them); the list is made again only when they changed, else each says it anew.
    /// </summary>
    private void LoadSpots()
    {
        bool paired = Sensors.Count > 0 && Sensors.All(r => r.PairNext is not null);
        TrayStyle StyleOf(TaskbarSensorRow r) => paired && r.Style == TrayStyle.Grouped ? TrayStyle.Icons : r.Style;
        var spots = new List<List<TaskbarSensorRow>>();
        var run = new List<TaskbarSensorRow>();
        void Flush()
        {
            if (run.Count > 0) spots.AddRange(TrayParts.Spots(run, StyleOf(run[0]), r => r.Spot));
            run = [];
        }
        foreach (var row in Sensors)
        {
            if (run.Count > 0 && (StyleOf(run[0]) != StyleOf(row) || run[0].Size != row.Size || run[0].IsGray != row.IsGray
                || run[0].OnTile != row.OnTile || run[0].Letters != row.Letters)) Flush();
            run.Add(row);
        }
        Flush();

        bool same = spots.Count == Spots.Count && spots.Zip(Spots).All(p => p.First.SequenceEqual(p.Second.Rows));
        if (!same)
        {
            Spots.Clear();
            foreach (var rows in spots) Spots.Add(new TaskbarSpot(rows, this) { IsOpen = rows[0].Id == _openKey });
        }
        foreach (var spot in Spots)
        {
            // In a pair each says what it is ("CPU load"); two of the same kind say their own names ("CCD1 (Tdie)").
            bool sameKind = spot.Rows.Select(r => r.KindPhrase).Distinct().Count() < spot.Rows.Count;
            foreach (var row in spot.Rows)
            {
                row.SpotName = sameKind ? row.Name : row.KindPhrase;
                row.LookTitle = spot.IsPair && spot.Style != TrayStyle.Icons ? Lang.T($"Look of {row.SpotName}") : Lang.T("Look");
                row.GraphTitle = spot.IsPair ? Lang.T($"Graph of {row.SpotName}") : Lang.T("Graph");
                row.ColorsTitle = spot.IsPair ? Lang.T($"Colors of {row.SpotName}") : Lang.T("Its colors");
            }
            spot.IsLast = spot == Spots[^1];
            spot.Refresh();
        }
    }
}
