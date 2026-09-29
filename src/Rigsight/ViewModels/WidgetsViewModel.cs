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
            int n = WidgetCatalog.ItemsOf(Config).Count;
            return $"{WidgetCatalog.LayoutName(LayoutValue)} · {n} reading{(n == 1 ? "" : "s")}";
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
    public bool CanAddMore => Items.Count < MaxItems;
    public string ItemsCount => $"{Items.Count} of {MaxItems}";

    /// <summary>The readings that can still be added, in the overlay's order.</summary>
    public IReadOnlyList<WidgetMetricOption> Offered =>
        [.. WidgetCatalog.Offered(LayoutValue).Where(m => Items.All(i => i.Id != m.ToString())).Select(m => new WidgetMetricOption(m, WidgetCatalog.MetricName(m)))];

    /// <summary>The PC's sensors this layout can show (gauges: temperatures and percentages), not already on it.</summary>
    public IReadOnlyList<SensorItem> OfferedSensors =>
        _live is null || !WidgetCatalog.AllowsSensor(LayoutValue) ? []
            : [.. _live.AllSensors.Where(s => WidgetCatalog.AllowsSensor(LayoutValue, s.Kind) && Items.All(i => i.Id != WidgetCatalog.SensorPrefix + s.Id))];

    public bool CanAddSensors => WidgetCatalog.AllowsSensor(LayoutValue) && _live is not null;

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
        foreach (var row in Items) row.Refresh();
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
    }

    private void Rebuild(List<WidgetItem> wanted)
    {
        var names = new Dictionary<string, SensorItem>();
        if (_live is not null) foreach (var s in _live.AllSensors) names.TryAdd(s.Id, s);
        Items.Clear();
        foreach (var item in wanted)
        {
            if (WidgetCatalog.Metric(item.Id) is { } m) Items.Add(new WidgetItemRow(this, item.Id, WidgetCatalog.MetricName(m), ""));
            else if (WidgetCatalog.Sensor(item.Id) is { } sensorId)
                Items.Add(new WidgetItemRow(this, item.Id, names.TryGetValue(sensorId, out var s) ? s.DisplayName : sensorId,
                    names.TryGetValue(sensorId, out var hw) ? hw.HardwareName : "Not found on this PC right now"));
        }
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
        string name = $"My widget {n}";
        while (Custom.Any(c => c.Title == name)) name = $"My widget {++n}";
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
            Style = WidgetStyle.Custom, Id = id, Name = $"{WidgetCatalog.Title(from)} copy",
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
