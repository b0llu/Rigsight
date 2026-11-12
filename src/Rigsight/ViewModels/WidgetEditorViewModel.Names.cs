using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// A reading's name in a tile's settings, as the approved mock-up XcuVN6VYHFqAbEb33kYRYx has the Fans tile's rows: a name
/// to click and type a new one, and, on a tile that lists several, a switch for whether this tile shows it. A reading
/// that is a sensor is renamed for the whole app, as renaming in All sensors does; a cleared name is its own again.
/// </summary>
public sealed partial class NameRow : ObservableObject
{
    private readonly Action<string>? _rename;
    private readonly Action<bool>? _show;
    private string _name;
    private bool _shown;

    public NameRow(string name, string usual, Action<string>? rename, Action<bool>? show = null, bool shown = true, Action? remove = null, string group = "")
    {
        (_name, Usual, _rename, _show, _shown, Group) = (name, usual, rename, show, shown, group);
        RemoveCommand = new RelayCommand(() => remove?.Invoke(), () => remove is not null);
        CanRemove = remove is not null;
    }

    /// <summary>The heading this row starts (the Fans page's GPU, Cooler, Motherboard), or none.</summary>
    public string Group { get; }
    public bool HasGroup => Group.Length > 0;

    /// <summary>Its own name, which a cleared box goes back to.</summary>
    public string Usual { get; }

    public bool CanRename => _rename is not null;
    public bool HasSwitch => _show is not null;
    public bool CanRemove { get; }
    public IRelayCommand RemoveCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            string typed = (value ?? "").Trim();
            if (typed == _name || _rename is null) return;
            _name = typed.Length == 0 ? Usual : typed;
            _rename(typed);
            OnPropertyChanged();
            OnPropertyChanged(nameof(NameTip));
        }
    }

    public bool Shown
    {
        get => _shown;
        set
        {
            if (value == _shown || _show is null) return;
            _shown = value;
            OnPropertyChanged();
            _show(value);
        }
    }

    /// <summary>What the name says when pointed at, as on All sensors: that it can be renamed, and its own name once it has been.</summary>
    public string NameTip => Name == Usual ? Lang.T("Click to rename") : Lang.T($"{Usual}  ·  click to rename");

    // For screen readers.
    public string NameLabel => Lang.T($"Name of {Usual}");
    public string SwitchLabel => Lang.T($"Show {Name} on this tile");
    public string RemoveLabel => Lang.T($"Remove {Name}");
}

public sealed partial class WidgetEditorViewModel
{
    // ── The names of the readings a tile shows, and which it shows ──────

    /// <summary>
    /// The picked tile's readings by name, each one renamed by clicking it, with a switch on a tile that lists several:
    /// a reading (its one), a CPU or GPU gauge (its temperature), a line of readings and a graph (each, with Remove), the
    /// Fans tile (the Fans page's rows, under its headings) and the Drives tile (each drive, which isn't a sensor and keeps
    /// its letter). None for a tile without readings' names.
    /// </summary>
    public IReadOnlyList<NameRow> NameRows => Tile is not { } t ? [] : t.Kind switch
    {
        WidgetTiles.Reading => [ItemRow(t.Item ?? nameof(OverlayMetric.CpuTemp), t.Label, (x, l) => x.Label = l)],
        WidgetTiles.CpuGauge => [ItemRow(nameof(OverlayMetric.CpuTemp), null, null)],
        WidgetTiles.GpuGauge => [ItemRow(nameof(OverlayMetric.GpuTemp), null, null)],
        WidgetTiles.Line or WidgetTiles.Graph => [.. (t.Items ?? []).Select((item, i) => ItemRow(item.Id, item.Label,
            (x, l) => { if (x.Items is { } list && i < list.Count) list[i].Label = l; },
            show: on => ChangeTile(x => { if (x.Items is { } list && i < list.Count) list[i].Hidden = !on; }), shown: !item.Hidden,
            remove: () => ChangeTile(x => { if (x.Items is { } list && i < list.Count) list.RemoveAt(i); })))],
        WidgetTiles.Fans => FanRows(t),
        WidgetTiles.Drives => DriveRows(t),
        _ => [],
    };

    public bool HasNameRows => Tile?.Kind is WidgetTiles.Reading or WidgetTiles.CpuGauge or WidgetTiles.GpuGauge or WidgetTiles.Line
        or WidgetTiles.Graph or WidgetTiles.Fans or WidgetTiles.Drives;

    /// <summary>The heading over them.</summary>
    public string NameRowsCaption => Tile?.Kind switch
    {
        WidgetTiles.Line or WidgetTiles.Graph => Lang.T("READINGS"),
        WidgetTiles.Fans => Lang.T("FANS"),
        WidgetTiles.Drives => Lang.T("DRIVES"),
        _ => Lang.T("NAME"),
    };

    /// <summary>The sensor a reading is (a sensor of its own, or one of the usual readings read from one sensor); null for the others.</summary>
    private SensorItem? SensorFor(string id) =>
        WidgetCatalog.Sensor(id) is { } sensor ? _live?.AllSensors.FirstOrDefault(s => s.Id == sensor)
        : WidgetCatalog.Metric(id) is { } m ? WidgetPictures.SensorOf(_live, m) : null;

    /// <summary>
    /// A reading's row. A sensor (or a usual reading that is one) is renamed for the whole app, as All sensors does, and a
    /// name the tile had of its own from before goes, so the new one shows; any other (a frame rate, a peak) keeps a name
    /// on this tile alone, as before.
    /// </summary>
    private NameRow ItemRow(string id, string? own, Action<WidgetTile, string?>? setOwn, Action<bool>? show = null, bool shown = true, Action? remove = null)
    {
        var sensor = SensorFor(id);
        var metric = WidgetCatalog.Metric(id);
        string usual = metric is { } m ? WidgetCatalog.ShortLabel(m) : sensor is not null ? SensorWords.Show(sensor.Name) : ReadingName(id);
        string name = own ?? (metric is not null ? sensor?.CustomLabel : sensor?.DisplayName) ?? usual;
        Action<string>? rename = null;
        if (sensor is not null)
            rename = typed =>
            {
                // Its own name typed back, or nothing: the rename goes (SensorItem.Label saves it, as All sensors' box does).
                sensor.Label = typed.Length == 0 || typed == usual ? "" : typed;
                if (own is not null && setOwn is not null) ChangeTile(t => setOwn(t, null));
                else Draw();
            };
        else if (setOwn is not null)
            rename = typed => ChangeTile(t => setOwn(t, typed.Length == 0 || typed == usual ? null
                : typed.Length > OverlaySettings.MaxLabelLength ? typed[..OverlaySettings.MaxLabelLength] : typed));
        return new NameRow(name, usual, rename, show, shown, remove);
    }

    /// <summary>
    /// The Fans tile's rows: the Fans page's (<see cref="Core.Reports.FanList"/>), under GPU, Cooler and Motherboard. A
    /// graphics card's several fans are one row, as there ("GPU fans"), not renamed; one fan is renamed for the whole app.
    /// </summary>
    private List<NameRow> FanRows(WidgetTile t)
    {
        if (_live is null) return [];
        var rows = new List<NameRow>();
        string? last = null;
        foreach (var fans in WidgetPictures.FanRows(_live))
        {
            var first = fans[0];
            string group = Core.Reports.FanList.IsGpu(first.HardwareType) ? Lang.T("GPU") : first.HardwareType == "Cooler" ? Lang.T("Cooler") : Lang.T("Motherboard");
            string id = first.Id;
            bool on = t.FansHidden?.Contains(id) != true;
            void Show(bool value) => ChangeTile(x =>
            {
                var off = x.FansHidden ?? [];
                off.Remove(id);
                if (!value) off.Add(id);
                x.FansHidden = off.Count > 0 ? off : null;
            });
            string usual = Core.Reports.FanList.Title(fans.Count, SensorWords.Show(first.Name));
            Action<string>? rename = fans.Count > 1 ? null : typed =>
            {
                first.Label = typed.Length == 0 || typed == usual ? "" : typed;
                Draw();
            };
            rows.Add(new NameRow(Core.Reports.FanList.Title(fans.Count, first.DisplayName), usual, rename, Show, on, group: group == last ? "" : group));
            last = group;
        }
        return rows;
    }

    /// <summary>The Drives tile's rows: each fixed drive by its letter (a drive here is Windows' volume, not a sensor: no rename), with its switch.</summary>
    private List<NameRow> DriveRows(WidgetTile t)
    {
        _drives ??= DriveSpace.Fixed().Select(v => v.Letter).ToList();
        return [.. _drives.Concat(t.DrivesHidden ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Select(letter =>
        {
            bool on = t.DrivesHidden?.Contains(letter, StringComparer.OrdinalIgnoreCase) != true;
            void Show(bool value) => ChangeTile(x =>
            {
                var off = x.DrivesHidden ?? [];
                off.RemoveAll(l => string.Equals(l, letter, StringComparison.OrdinalIgnoreCase));
                if (!value) off.Add(letter);
                x.DrivesHidden = off.Count > 0 ? off : null;
            });
            string name = Lang.T($"{letter} drive");
            return new NameRow(name, name, null, Show, on);
        })];
    }

    /// <summary>The fixed drives, asked of Windows once while the editor is open.</summary>
    private List<string>? _drives;

    // ── Background colour (approved mock-up XcuVN6VYHFqAbEb33kYRYx: one colour for the whole widget) ──

    /// <summary>The row's name: "Background" as a widget's colour (the word also names apps in the background).</summary>
    public string BackgroundLabel => Lang.C("widget", "Background");

    /// <summary>The quick colours for a widget's background, the mock-up's: dark ones, then light ones.</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> BackgroundColors =
    [
        (Lang.Key("Black"), "#0B0C0E"), (Lang.Key("Navy"), "#1B2A3A"), (Lang.Key("Dark green"), "#16302B"), (Lang.Key("Plum"), "#2B1F3A"),
        (Lang.Key("Wine"), "#3A1F24"), (Lang.Key("Mist"), "#E9EDF2"), (Lang.Key("Cream"), "#F6E7C8"), (Lang.Key("Light blue"), "#CFE3F7"),
    ];

    /// <summary>"theme" (the theme's, as every widget had) or "own" (a colour picked here).</summary>
    public string BackgroundKind
    {
        get => _w.BackgroundColor is null ? "theme" : "own";
        set
        {
            if (value == BackgroundKind) return;
            // My colour starts on the one last picked here, else the first quick colour.
            if (value == "own") Change(w => w.BackgroundColor = _lastBackground ?? BackgroundColors[1].Hex);
            else
            {
                _lastBackground = _w.BackgroundColor;
                Change(w => w.BackgroundColor = null);
            }
        }
    }

    private string? _lastBackground;

    public bool HasOwnBackground => _w.BackgroundColor is not null;

    /// <summary>The colour picked, on the row's button.</summary>
    public Brush BackgroundSwatch => new SolidColorBrush(Controls.ColorPicker.ColorOf(_w.BackgroundColor ?? BackgroundColors[0].Hex));

    public string? BackgroundColor => _w.BackgroundColor;

    /// <summary>A colour picked in the picker (null: back to the theme's).</summary>
    public void SetBackground(string? hex)
    {
        string? code = TrayColors.Parse(hex) is { } c ? TrayColors.Hex(c.R, c.G, c.B) : null;
        if (code != _w.BackgroundColor) Change(w => w.BackgroundColor = code);
    }

    /// <summary>The colours made with "+" for widget backgrounds, offered for every widget.</summary>
    public IReadOnlyList<string> BackgroundCustoms => _settings.Current.WidgetBackgroundColors;

    public void AddBackgroundCustom(string hex) => _settings.Update(s =>
    {
        if (!s.WidgetBackgroundColors.Contains(hex, StringComparer.OrdinalIgnoreCase)) s.WidgetBackgroundColors.Add(hex);
        if (s.WidgetBackgroundColors.Count > RigsightSettings.MaxTrayCustomColors) s.WidgetBackgroundColors.RemoveAt(0);
    });
}
