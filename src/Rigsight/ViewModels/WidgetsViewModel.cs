using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A reading on a widget, in its editor: what it is, and the short name shown for it.</summary>
public sealed partial class WidgetItemRow(WidgetCard card, string id, string name, string detail) : ObservableObject
{
    public string Id { get; } = id;

    /// <summary>Why the widget's layout doesn't show it ("Tiles shows 6"), or null when it does. Kept, not dropped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown), nameof(NotShownText))]
    private string? _notShown;

    public bool IsShown => NotShown is null;
    /// <summary>The note under the row: "Not shown: Tiles shows 6".</summary>
    public string? NotShownText => NotShown is null ? null : Lang.T($"Not shown: {NotShown}");
    /// <summary>What the reading is ("CPU temperature", or the sensor's name): shown when no short name is set.</summary>
    public string Name { get; } = name;
    /// <summary>Where it comes from: the sensor's hardware, or nothing for the usual readings.</summary>
    public string Detail { get; } = detail;

    public string Label
    {
        get => card.ItemLabel(Id);
        set { card.SetItemLabel(Id, value); OnPropertyChanged(); }
    }

    public void Refresh() => OnPropertyChanged(nameof(Label));
}

/// <summary>A reading that can be added to a widget.</summary>
public sealed record WidgetMetricOption(OverlayMetric Metric, string Name);

/// <summary>
/// A line of the reading picker: a usual reading or a sensor, where it comes from, its value now, and why it can't be
/// added where it can't (the layout can't show it, or the widget is full). <see cref="Header"/>: the group heading above
/// it, when it's the first of its group.
/// </summary>
public sealed record PickerOption(string Id, string Name, string Source, string Value, WidgetCategory Category, string? Off)
{
    /// <summary>A fan's speed (the Fans chip lists the graphics card's fans too).</summary>
    public bool IsSensorFan { get; init; }

    /// <summary>A sensor's kind in words ("Temperature", "Clock"), so "temp" finds "Core (Tctl/Tdie)".</summary>
    public string? Kind { get; init; }
    public string? Header { get; init; }
    public bool HasHeader => Header is not null;
    public bool CanAdd => Off is null;
    /// <summary>What stands at the right: its value, or why it can't be added.</summary>
    public string Side => Off ?? Value;
    public bool HasSource => Source.Length > 0;
    /// <summary>The dot's colour: the part's (CPU blue, GPU cyan, RAM purple), muted for the rest.</summary>
    public string DotBrush => Category switch
    {
        WidgetCategory.Cpu => "CpuBrush",
        WidgetCategory.Gpu => "GpuBrush",
        WidgetCategory.Ram => "PurpleBrush",
        WidgetCategory.Game => "PinkBrush",
        WidgetCategory.Storage or WidgetCategory.Network => "SkyBrush",
        _ => "FaintBrush",
    };
}

/// <summary>A chip of the picker: a group of readings, with how many there are to add (under the words typed).</summary>
public sealed partial class PickerChip(string key, string name) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;

    /// <summary>The group has something on this PC (a PC without, say, readable drive sensors shows no Storage chip).</summary>
    [ObservableProperty] private bool _isShown = true;
}

/// <summary>One widget: its card on the Widgets page, and everything its editor changes.</summary>
public sealed partial class WidgetCard : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly LiveData? _live;
    private readonly WidgetsViewModel _page;

    public WidgetCard(string id, SettingsModel settings, LiveData? live, WidgetsViewModel page)
    {
        Id = id;
        _settings = settings;
        _live = live;
        _page = page;
        // Weakly: the live data lives as long as the app, and held on to every card made (they're made on each visit).
        if (live is not null) System.ComponentModel.PropertyChangedEventManager.AddHandler(live, OnLiveChanged, nameof(LiveData.PickableSensors));
        LoadItems();
    }

    public string Id { get; }

    /// <summary>The widget's settings (a copy of the app's: changes go through <see cref="Change"/>).</summary>
    private WidgetConfig Config => _settings.Current.Widgets.FirstOrDefault(w => w.Id == Id) ?? new WidgetConfig { Id = Id };

    public WidgetStyle Style => Config.Style;

    /// <summary>The user's own (a name, a layout, can be duplicated or deleted), not a built-in one.</summary>
    public bool IsCustom => Style == WidgetStyle.Custom;
    public bool IsBuiltIn => !IsCustom;

    private void Change(Action<WidgetConfig> change, params string[] properties)
    {
        _settings.Update(s =>
        {
            if (s.Widgets.FirstOrDefault(w => w.Id == Id) is { } w) change(w);
        });
        foreach (var p in properties) OnPropertyChanged(p);
    }

    public string Title
    {
        get => WidgetCatalog.Title(Config);
        set
        {
            if (!IsCustom) return;
            string name = (value ?? "").Trim();
            if (name.Length == 0) name = WidgetCatalog.Title(WidgetStyle.Custom);
            if (name.Length > WidgetCatalog.MaxNameLength) name = name[..WidgetCatalog.MaxNameLength];
            Change(w => w.Name = name, nameof(Title));
        }
    }

    /// <summary>A built-in widget's description; the user's own say how they're made ("Bar · 3 readings").</summary>
    public string Description
    {
        get
        {
            if (IsBuiltIn) return WidgetCatalog.Description(Style);
            int n = WidgetCatalog.ShownItems(Config).Count;
            string layout = WidgetCatalog.LayoutName(LayoutValue);
            return n == 1 ? Lang.T($"{layout} · 1 reading") : Lang.T($"{layout} · {n} readings");
        }
    }

    public bool Enabled { get => Config.Enabled; set => Change(c => c.Enabled = value, nameof(Enabled)); }
    public string Theme { get => Config.Theme.ToString(); set => Change(c => c.Theme = Enum.Parse<WidgetTheme>(value), nameof(Theme)); }
    public string Colors { get => Config.Grayscale ? "Grayscale" : "Color"; set => Change(c => c.Grayscale = value == "Grayscale", nameof(Colors)); }
    public bool Locked { get => Config.Locked; set => Change(c => c.Locked = value, nameof(Locked)); }
    public double BackgroundPercent { get => Math.Round(Config.BackgroundOpacity * 100); set => Change(c => c.BackgroundOpacity = value / 100, nameof(BackgroundPercent)); }
    public double ContentPercent { get => Math.Round(Config.ContentOpacity * 100); set => Change(c => c.ContentOpacity = value / 100, nameof(ContentPercent)); }
    public string Scale { get => Config.Scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture); set => Change(c => c.Scale = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture), nameof(Scale)); }

    // ── Layout and readings ──

    private WidgetLayout LayoutValue => WidgetCatalog.LayoutOf(Config);

    /// <summary>The layout (Bar, Tiles, Gauges, Graph); only the user's own widgets can change it.</summary>
    public string Layout
    {
        get => LayoutValue.ToString();
        set
        {
            if (!IsCustom || !Enum.TryParse<WidgetLayout>(value, out var layout) || !WidgetCatalog.CustomLayouts.Contains(layout)) return;
            // Keep the readings the new layout can show (a graph can't show a sensor, gauges can't show a clock speed).
            Change(w =>
            {
                w.Layout = layout;
                WidgetCatalog.Clean(w);
            }, nameof(Layout), nameof(Description));
            LoadItems();
        }
    }

    public string LayoutName => WidgetCatalog.LayoutName(LayoutValue);

    /// <summary>Whether this widget shows readings to pick (Now playing and Today don't).</summary>
    public bool HasReadings => WidgetCatalog.MaxItems(LayoutValue) > 0;

    public ObservableCollection<WidgetItemRow> Items { get; } = [];

    public int MaxItems => WidgetCatalog.MaxItems(LayoutValue);
    public bool CanAddMore => Items.Count(i => i.IsShown) < MaxItems && Items.Count < WidgetCatalog.MaxKept;
    public string ItemsCount => Lang.T($"{Items.Count(i => i.IsShown)} of {MaxItems}");

    /// <summary>The readings that can still be added, in the overlay's order.</summary>
    public IReadOnlyList<WidgetMetricOption> Offered =>
        [.. WidgetCatalog.Offered(LayoutValue).Where(m => Items.All(i => i.Id != m.ToString())).Select(m => new WidgetMetricOption(m, WidgetCatalog.MetricName(m)))];

    /// <summary>The PC's sensors this layout can show (gauges: temperatures and percentages), not already on it.</summary>
    public IReadOnlyList<SensorItem> OfferedSensors =>
        _live is null || !WidgetCatalog.AllowsSensor(LayoutValue) ? []
            : [.. _live.PickableSensors.Where(s => WidgetCatalog.AllowsSensor(LayoutValue, s.Kind) && Items.All(i => i.Id != WidgetCatalog.SensorPrefix + s.Id))];

    public bool CanAddSensors => WidgetCatalog.AllowsSensor(LayoutValue) && _live is not null;

    // ── The reading picker: one searchable list of the usual readings and every sensor, by part ──

    /// <summary>What's typed in the picker: every word must be in a reading's name, its part's name or its group.</summary>
    [ObservableProperty] private string _pickerText = "";

    [ObservableProperty] private bool _pickerOpen;

    /// <summary>The chip picked ("all", or a <see cref="WidgetCategory"/>'s name).</summary>
    [ObservableProperty] private string _pickerCategory = "all";

    public List<PickerChip> PickerChips { get; } =
        [new("all", Lang.T("All")), .. Enum.GetValues<WidgetCategory>().Select(c => new PickerChip(c.ToString(), WidgetCatalog.CategoryName(c)))];

    [ObservableProperty] private List<PickerOption> _pickerOptions = [];
    [ObservableProperty] private bool _pickerEmpty;
    [ObservableProperty] private string _pickerEmptyText = "";
    [ObservableProperty] private string _pickerEmptyHint = "";
    [ObservableProperty] private bool _canSearchAll;

    partial void OnPickerTextChanged(string value)
    {
        if (value.Length > 0) PickerOpen = true;
        BuildPicker();
    }

    partial void OnPickerOpenChanged(bool value)
    {
        if (value) BuildPicker();
    }

    partial void OnPickerCategoryChanged(string value) => BuildPicker();

    [RelayCommand]
    private void PickChip(PickerChip chip) => PickerCategory = chip.Key;

    [RelayCommand]
    private void SearchAll() => PickerCategory = "all";

    /// <summary>A click on a line adds it at once; the picker stays open for the next one.</summary>
    [RelayCommand]
    private void PickOption(PickerOption option)
    {
        if (!option.CanAdd || Items.Any(i => i.Id == option.Id)) return;
        EditItems(list => list.Add(new WidgetItem { Id = option.Id }));
        BuildPicker();
    }

    /// <summary>Everything that can go on a widget, grouped by part: the usual readings first, then the sensors.</summary>
    private List<PickerOption> AllOptions()
    {
        var layout = LayoutValue;
        bool full = !CanAddMore;
        string? Off(string? why) => why ?? (full ? Lang.T("Full: remove one to add another") : null);
        var list = new List<PickerOption>();
        foreach (var m in Enum.GetValues<OverlayMetric>())
        {
            string id = m.ToString();
            if (WidgetCatalog.Metric(id) is null || Items.Any(i => i.Id == id)) continue;
            list.Add(new PickerOption(id, WidgetCatalog.MetricName(m), WidgetCatalog.MetricDetail(m), ValueOf(m), WidgetCatalog.CategoryOf(m),
                Off(WidgetCatalog.WhyNot(layout, m))));
        }
        if (_live is not null)
            foreach (var sensor in _live.PickableSensors)
            {
                string id = WidgetCatalog.SensorPrefix + sensor.Id;
                if (Items.Any(i => i.Id == id)) continue;
                list.Add(new PickerOption(id, sensor.DisplayName, sensor.HardwareName, sensor.ShortValue, WidgetCatalog.CategoryOf(sensor.HardwareType, sensor.Kind),
                    Off(WidgetCatalog.WhyNot(layout, null, sensor.Kind))) { IsSensorFan = sensor.Kind == Core.SensorKind.Fan, Kind = sensor.TypeLabel });
            }
        return list;
    }

    /// <summary>A usual reading's value now, where the app has it (the frame rate and what's in front are the agent's alone).</summary>
    private string ValueOf(OverlayMetric m)
    {
        if (_live is not { } live) return "";
        var today = live.Today;
        return m switch
        {
            OverlayMetric.CpuTemp => live.CpuTemp?.ShortValue ?? "",
            OverlayMetric.CpuLoad => live.CpuLoad?.ShortValue ?? "",
            OverlayMetric.CpuClock => live.CpuClock?.ShortValue ?? "",
            OverlayMetric.CpuPower => live.CpuPower?.ShortValue ?? "",
            OverlayMetric.GpuTemp => live.GpuTemp?.ShortValue ?? "",
            OverlayMetric.GpuHotSpot => live.GpuHotSpot?.ShortValue ?? "",
            OverlayMetric.GpuLoad => live.GpuLoad?.ShortValue ?? "",
            OverlayMetric.GpuClock => live.GpuClock?.ShortValue ?? "",
            OverlayMetric.GpuPower => live.GpuPower?.ShortValue ?? "",
            OverlayMetric.GpuMemory => live.GpuVramUsed?.ShortValue ?? "",
            OverlayMetric.Ram => live.RamLoad?.ShortValue ?? "",
            OverlayMetric.ScreenTime => today.ActiveSec > 0 ? Core.Units.Duration(today.ActiveSec) : "",
            OverlayMetric.PcOn => today.OnSec > 0 ? Core.Units.Duration(today.OnSec) : "",
            OverlayMetric.TopApp => today.TopApp ?? "",
            OverlayMetric.CpuPeakToday => Core.Units.TempShort(today.CpuPeak) is var t && today.CpuPeak is not null ? t : "",
            OverlayMetric.GpuPeakToday => Core.Units.TempShort(today.GpuPeak) is var g && today.GpuPeak is not null ? g : "",
            OverlayMetric.Clock => DateTime.Now.ToString("t", System.Globalization.CultureInfo.CurrentCulture),
            OverlayMetric.Date => DateTime.Now.ToString("ddd d MMM", System.Globalization.CultureInfo.CurrentCulture),
            _ => "",
        };
    }

    /// <summary>The picker's list for what's typed and the chip picked, with each chip's count, or why nothing shows.</summary>
    private void BuildPicker()
    {
        if (!PickerOpen) return;
        var words = Core.TextMatch.Words(PickerText);
        bool Matches(PickerOption o) => words.Length == 0
            || Core.TextMatch.Has(words, o.Name, o.Source, WidgetCatalog.CategoryName(o.Category), o.Kind, o.IsSensorFan ? "fan" : null, o.IsSensorFan ? Lang.T("fan") : null,
                o.Id.StartsWith(WidgetCatalog.SensorPrefix, StringComparison.Ordinal) ? "sensor" : null);
        // The Fans chip has every fan, the graphics card's too.
        bool In(PickerOption o, string key) => key == "all" || o.Category.ToString() == key || (key == nameof(WidgetCategory.Fans) && o.IsSensorFan);
        var options = AllOptions();
        var matching = options.Where(Matches).ToList();
        foreach (var chip in PickerChips)
        {
            chip.Count = matching.Count(o => In(o, chip.Key));
            chip.IsSelected = chip.Key == PickerCategory;
            chip.IsShown = chip.IsSelected || chip.Key == "all" || options.Any(o => In(o, chip.Key));
        }
        bool all = PickerCategory == "all";
        var shown = matching.Where(o => In(o, PickerCategory))
            .OrderBy(o => all ? (int)o.Category : 0)
            .ThenBy(o => o.Id.StartsWith(WidgetCatalog.SensorPrefix, StringComparison.Ordinal))
            .ThenBy(o => o.Id.StartsWith(WidgetCatalog.SensorPrefix, StringComparison.Ordinal) ? o.Source : "", StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        // A heading over each group: the part under All, else the usual readings apart from the sensors.
        string Group(PickerOption o) => all ? WidgetCatalog.CategoryName(o.Category).ToUpperInvariant()
            : o.Id.StartsWith(WidgetCatalog.SensorPrefix, StringComparison.Ordinal) ? Lang.T("SENSORS") : Lang.T("USUAL READINGS");
        string? last = null;
        PickerOptions = [.. shown.Select(o =>
        {
            string g = Group(o);
            var line = g == last ? o : o with { Header = g };
            last = g;
            return line;
        })];
        PickerEmpty = shown.Count == 0;
        string name = PickerChips.First(c => c.Key == PickerCategory).Name;
        int elsewhere = all ? 0 : matching.Count;
        string typed = PickerText.Trim();
        PickerEmptyText = words.Length == 0 ? (all ? Lang.T("Nothing left to add") : Lang.T($"Nothing left to add in {name}"))
            : all ? Lang.T($"Nothing matches \"{typed}\"") : Lang.T($"Nothing matches \"{typed}\" in {name}");
        CanSearchAll = PickerEmpty && elsewhere > 0;
        // "1 matches", "2 match": the readings that match, so the verb agrees with the number.
        PickerEmptyHint = CanSearchAll ? (elsewhere == 1 ? Lang.T("1 matches in other groups.") : Lang.T($"{elsewhere} match in other groups."))
            : words.Length > 0 ? Lang.T("Try another word, such as a part's name or \"fan\".") : "";
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddReadingCommand))]
    private WidgetMetricOption? _readingToAdd;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSensorCommand))]
    private SensorItem? _sensorToAdd;

    private bool CanAddReading() => ReadingToAdd is not null && CanAddMore;
    private bool CanAddSensor() => SensorToAdd is not null && CanAddMore;

    [RelayCommand(CanExecute = nameof(CanAddReading))]
    private void AddReading()
    {
        if (ReadingToAdd is not { } option) return;
        EditItems(list => list.Add(new WidgetItem { Id = option.Metric.ToString() }));
        ReadingToAdd = null;
    }

    [RelayCommand(CanExecute = nameof(CanAddSensor))]
    private void AddSensor()
    {
        if (SensorToAdd is not { } sensor) return;
        EditItems(list => list.Add(new WidgetItem { Id = WidgetCatalog.SensorPrefix + sensor.Id }));
        SensorToAdd = null;
    }

    [RelayCommand]
    private void RemoveItem(WidgetItemRow row)
    {
        // A widget always shows something: the last reading stays.
        if (Items.Count <= 1) return;
        EditItems(list => list.RemoveAll(i => i.Id == row.Id));
    }

    [RelayCommand]
    private void MoveItemUp(WidgetItemRow row) => MoveItem(row, -1);

    [RelayCommand]
    private void MoveItemDown(WidgetItemRow row) => MoveItem(row, 1);

    private void MoveItem(WidgetItemRow row, int by) => EditItems(list =>
    {
        int i = list.FindIndex(x => x.Id == row.Id), j = i + by;
        if (i >= 0 && j >= 0 && j < list.Count) (list[i], list[j]) = (list[j], list[i]);
    });

    internal string ItemLabel(string id) => WidgetCatalog.ItemsOf(Config).FirstOrDefault(i => i.Id == id)?.Label ?? "";

    internal void SetItemLabel(string id, string? label)
    {
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (label?.Length > OverlaySettings.MaxLabelLength) label = label[..OverlaySettings.MaxLabelLength];
        if (label == (ItemLabel(id) is { Length: > 0 } now ? now : null)) return;
        EditItems(list => { if (list.FirstOrDefault(i => i.Id == id) is { } item) item.Label = label; }, reload: false);
    }

    /// <summary>Changes the widget's own list of readings (a built-in one gets its own copy of its defaults first).</summary>
    private void EditItems(Action<List<WidgetItem>> change, bool reload = true)
    {
        Change(w =>
        {
            var list = WidgetCatalog.ItemsOf(w).Select(i => new WidgetItem { Id = i.Id, Label = i.Label }).ToList();
            change(list);
            w.Items = list;
            WidgetCatalog.Clean(w);
        }, nameof(Description), nameof(CanReset));
        if (reload) LoadItems();
    }

    /// <summary>A built-in widget whose readings were changed can go back to its own.</summary>
    public bool CanReset => IsBuiltIn && Config.Items is not null;

    [RelayCommand]
    private void Reset()
    {
        Change(w => w.Items = null, nameof(CanReset), nameof(Description));
        LoadItems();
    }

    /// <summary>Rebuilds the readings from settings, only when they changed (so a short name being typed isn't disturbed).</summary>
    private void LoadItems()
    {
        var wanted = WidgetCatalog.ItemsOf(Config);
        if (!Items.Select(i => i.Id).SequenceEqual(wanted.Select(i => i.Id)))
            Rebuild(wanted);
        // What this layout doesn't show stays in the list, greyed, with why: switching back shows it again.
        var shown = WidgetCatalog.ShownItems(Config).Select(i => i.Id).ToHashSet();
        foreach (var row in Items)
        {
            row.NotShown = shown.Contains(row.Id) ? null
                : WidgetCatalog.Shows(LayoutValue, new WidgetItem { Id = row.Id }) ? Lang.T($"{LayoutName} shows {MaxItems}")
                : WidgetCatalog.WhyNot(LayoutValue, WidgetCatalog.Metric(row.Id), SensorKindOf(row.Id)) ?? Lang.T($"{LayoutName} can't show this");
            row.Refresh();
        }
        OnPropertyChanged(nameof(CanAddMore));
        OnPropertyChanged(nameof(ItemsCount));
        OnPropertyChanged(nameof(MaxItems));
        OnPropertyChanged(nameof(Offered));
        OnPropertyChanged(nameof(OfferedSensors));
        OnPropertyChanged(nameof(CanAddSensors));
        OnPropertyChanged(nameof(HasReadings));
        OnPropertyChanged(nameof(LayoutName));
        AddReadingCommand.NotifyCanExecuteChanged();
        AddSensorCommand.NotifyCanExecuteChanged();
        BuildPicker();
    }

    private Core.SensorKind? SensorKindOf(string id) =>
        WidgetCatalog.Sensor(id) is { } sensorId ? _live?.AllSensors.FirstOrDefault(s => s.Id == sensorId)?.Kind : null;

    private void Rebuild(List<WidgetItem> wanted)
    {
        var names = new Dictionary<string, SensorItem>();
        if (_live is not null) foreach (var s in _live.AllSensors) names.TryAdd(s.Id, s);
        Items.Clear();
        // Before the agent's first readings there are no sensors to name them by: said as such, not as missing.
        bool read = names.Count > 0;
        _namedSensors = read;
        foreach (var item in wanted)
        {
            if (WidgetCatalog.Metric(item.Id) is { } m) Items.Add(new WidgetItemRow(this, item.Id, WidgetCatalog.MetricName(m), ""));
            else if (WidgetCatalog.Sensor(item.Id) is { } sensorId)
                Items.Add(names.TryGetValue(sensorId, out var s)
                    ? new WidgetItemRow(this, item.Id, s.DisplayName, s.HardwareName)
                    : new WidgetItemRow(this, item.Id, SensorName(sensorId), read ? Lang.T("Not found on this PC right now") : Lang.T("Waiting for the readings")));
        }
    }

    /// <summary>Whether the rows were named from the agent's sensors (else they're named again once those arrive).</summary>
    private bool _namedSensors;

    /// <summary>A sensor's name from its identifier while it can't be looked up ("/gpu-nvidia/0/fan/1" is "GPU fan 1").</summary>
    internal static string SensorName(string id)
    {
        var parts = id.Trim('/').Split('/');
        if (parts.Length < 2) return id;
        string part = parts[0] switch
        {
            var p when p.StartsWith("gpu", StringComparison.OrdinalIgnoreCase) => "GPU ",
            var p when p.Contains("cpu", StringComparison.OrdinalIgnoreCase) => "CPU ",
            var p when p.Contains("lpc", StringComparison.OrdinalIgnoreCase) || p.Contains("mainboard", StringComparison.OrdinalIgnoreCase) => "Motherboard ",
            _ => "",
        };
        string kind = parts[^2].Replace('-', ' ');
        string number = parts[^1];
        return $"{part}{kind} {number}".Trim() is var name && name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : id;
    }

    /// <summary>The agent's sensors arrived (or changed): sensor rows named from them, and the picker's list with them.</summary>
    private void OnLiveChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LiveData.PickableSensors)) return;
        if (!_namedSensors && Items.Any(i => WidgetCatalog.Sensor(i.Id) is not null))
        {
            Rebuild(WidgetCatalog.ItemsOf(Config));
            LoadItems(); // the rows' "not shown" notes, and the picker
        }
        else BuildPicker();
    }

    // ── Page actions ──

    [ObservableProperty] private bool _isEditing;

    [RelayCommand]
    private void Edit() => _page.Edit(this);

    [RelayCommand]
    private void Duplicate() => _page.Duplicate(this);

    [RelayCommand]
    private void Delete() => _page.Delete(this);

    [ObservableProperty] private ImageSource? _preview;

    public void LoadPreview() => Preview = PreviewImages.Load(Id) ?? Preview;

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        LoadItems();
    }
}

/// <summary>Pictures of widgets and the overlay, drawn by the agent into the data folder.</summary>
public static class PreviewImages
{
    public static ImageSource? Load(string name)
    {
        var file = Path.Combine(RigsightPaths.DataDir, "previews", $"{name}.png");
        if (!File.Exists(file)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // Being rewritten by the agent right now; the next refresh will pick it up.
            return null;
        }
    }
}

/// <summary>
/// The Widgets page: the built-in widgets in a row, the user's own below, a button to make one, and a side panel
/// that edits the chosen widget (its readings and look) with a live preview.
/// </summary>
public sealed partial class WidgetsViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly AgentClient _client;
    private readonly LiveData? _live;

    public WidgetsViewModel(SettingsModel settings, AgentClient client, LiveData? live = null)
    {
        _settings = settings;
        _client = client;
        _live = live;
        Sync();
    }

    /// <summary>The built-in widgets, FPS first.</summary>
    public ObservableCollection<WidgetCard> BuiltIn { get; } = [];

    /// <summary>The user's own widgets, in the order they were made.</summary>
    public ObservableCollection<WidgetCard> Custom { get; } = [];

    /// <summary>Every widget, built-in ones first.</summary>
    public IEnumerable<WidgetCard> Cards => BuiltIn.Concat(Custom);

    public bool HasCustom => Custom.Count > 0;
    public bool CanCreate => Custom.Count < WidgetCatalog.MaxCustom;

    /// <summary>The widget open in the side panel, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private WidgetCard? _editing;

    public bool IsEditing => Editing is not null;

    partial void OnEditingChanged(WidgetCard? oldValue, WidgetCard? newValue)
    {
        if (oldValue is not null) oldValue.IsEditing = false;
        if (newValue is not null) newValue.IsEditing = true;
    }

    public void Edit(WidgetCard card) => Editing = card;

    /// <summary>Opens the editor for a widget by its identifier (from its right-click menu on the desktop).</summary>
    public void Edit(string? id)
    {
        if (Cards.FirstOrDefault(c => c.Id == id) is { } card) Editing = card;
    }

    [RelayCommand]
    private void CloseEditor() => Editing = null;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        string id = WidgetCatalog.NewId();
        int n = Custom.Count + 1;
        string name = Lang.T($"My widget {n}");
        while (Custom.Any(c => c.Title == name)) name = Lang.T($"My widget {++n}");
        _settings.Update(s => s.Widgets.Add(new WidgetConfig
        {
            Style = WidgetStyle.Custom, Id = id, Name = name, Layout = WidgetLayout.Bar,
            Items = WidgetCatalog.DefaultItems(WidgetStyle.Custom), Enabled = true, Grayscale = true,
        }));
        Sync();
        Edit(id);
        RequestPreviews();
    }

    public void Duplicate(WidgetCard card)
    {
        if (!CanCreate || _settings.Current.Widgets.FirstOrDefault(w => w.Id == card.Id) is not { } from) return;
        string id = WidgetCatalog.NewId();
        var layout = WidgetCatalog.LayoutOf(from);
        _settings.Update(s => s.Widgets.Add(new WidgetConfig
        {
            Style = WidgetStyle.Custom, Id = id, Name = Lang.T($"{WidgetCatalog.Title(from)} copy"),
            Layout = WidgetCatalog.CustomLayouts.Contains(layout) ? layout : WidgetLayout.Bar,
            Items = [.. WidgetCatalog.ItemsOf(from).Select(i => new WidgetItem { Id = i.Id, Label = i.Label })], Enabled = true,
            Theme = from.Theme, Grayscale = from.Grayscale, Scale = from.Scale, BackgroundOpacity = from.BackgroundOpacity, ContentOpacity = from.ContentOpacity,
        }));
        Sync();
        Edit(id);
        RequestPreviews();
    }

    public void Delete(WidgetCard card)
    {
        if (!card.IsCustom) return;
        if (Editing == card) Editing = null;
        _settings.Update(s => s.Widgets.RemoveAll(w => w.Id == card.Id));
        Sync();
    }

    /// <summary>Makes the lists match settings: cards for new widgets, none for deleted ones.</summary>
    private void Sync()
    {
        var widgets = _settings.Current.Widgets;
        Match(BuiltIn, [.. WidgetCatalog.Listed.Select(s => widgets.FirstOrDefault(w => w.Style == s)?.Id ?? s.ToString())]);
        Match(Custom, [.. widgets.Where(w => w.Style == WidgetStyle.Custom).Select(w => w.Id)]);
        if (Editing is { } editing && !Cards.Contains(editing)) Editing = null;
        OnPropertyChanged(nameof(HasCustom));
        OnPropertyChanged(nameof(CanCreate));
        CreateCommand.NotifyCanExecuteChanged();

        void Match(ObservableCollection<WidgetCard> list, List<string> ids)
        {
            if (list.Select(c => c.Id).SequenceEqual(ids)) return;
            var keep = list.ToDictionary(c => c.Id);
            list.Clear();
            foreach (var id in ids)
            {
                var card = keep.GetValueOrDefault(id) ?? new WidgetCard(id, _settings, _live, this);
                card.LoadPreview();
                list.Add(card);
            }
        }
    }

    /// <summary>Asks the agent to render fresh preview images of every widget.</summary>
    public void RequestPreviews() => _client.SendCommand("render-previews");

    public void OnPreviewsReady()
    {
        foreach (var c in Cards) c.LoadPreview();
    }

    public void Refresh()
    {
        Sync();
        foreach (var c in Cards) c.Refresh();
    }
}
