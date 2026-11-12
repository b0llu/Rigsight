using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// A widget the person has, on the Widgets page (approved mock-up UckKnwokm5kF7JjdjHZpqd, "Two panes"): a row in the list
/// (its picture, name, the screen it's on, its switch) and, picked, the pane beside it (its picture at its size, where it
/// is, and where it sits, Click-through and its size).
/// </summary>
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
    }

    public string Id { get; }

    internal WidgetConfig Config => _settings.Current.Widgets.FirstOrDefault(w => w.Id == Id) ?? new WidgetConfig { Id = Id };

    public WidgetStyle Style => Config.Style;

    public string Title => WidgetCatalog.Title(Config);

    /// <summary>A built-in widget says what it shows; one of tiles, how many; one of the person's own from before, its layout.</summary>
    public string Description
    {
        get
        {
            var c = Config;
            if (c.Tiles is { } tiles) return tiles.Count == 1 ? Lang.T("1 tile") : Lang.T($"{tiles.Count} tiles");
            if (c.Style != WidgetStyle.Custom) return WidgetCatalog.Description(c.Style);
            int n = WidgetCatalog.ShownItems(c).Count;
            string layout = WidgetCatalog.LayoutName(WidgetCatalog.LayoutOf(c));
            return n == 1 ? Lang.T($"{layout} · 1 reading") : Lang.T($"{layout} · {n} readings");
        }
    }

    public bool Enabled
    {
        get => Config.Enabled;
        set
        {
            if (value == Enabled) return;
            _settings.Update(s => { if (s.Widgets.FirstOrDefault(w => w.Id == Id) is { } c) c.Enabled = value; });
            OnPropertyChanged(nameof(Enabled));
            OnPropertyChanged(nameof(Where));
            OnPropertyChanged(nameof(Line));
            _page.CardsChanged();
        }
    }

    /// <summary>Click-through: it can't be dragged and clicks go to what's behind it (the editor's "Lock in place").</summary>
    public bool Locked
    {
        get => Config.Locked;
        set
        {
            if (value == Locked) return;
            _settings.Update(s => { if (s.Widgets.FirstOrDefault(w => w.Id == Id) is { } c) c.Locked = value; });
            OnPropertyChanged(nameof(Locked));
        }
    }

    [RelayCommand]
    private void ToggleLocked() => Locked = !Locked;

    [RelayCommand]
    private void Remove() => _page.Remove(this);

    /// <summary>The screen it's on, while it's on and has a spot ("Main screen", "Screen 2"); empty otherwise.</summary>
    public string Where => Config is { Enabled: true, X: int x, Y: int y } ? _page.ScreenName(x, y) : "";

    /// <summary>Under its name in the list: the screen it's on, or Off.</summary>
    public string Line => !Enabled ? Lang.T("Off") : Where is { Length: > 0 } where ? where : Lang.T("On");

    /// <summary>Its theme (the editor's Theme).</summary>
    public string Theme
    {
        get => Config.Theme.ToString();
        set
        {
            if (value is null || value == Theme || !Enum.TryParse<WidgetTheme>(value, out var theme)) return;
            _settings.Update(s => { if (s.Widgets.FirstOrDefault(w => w.Id == Id) is { } c) c.Theme = theme; });
            OnPropertyChanged(nameof(Theme));
            Redraw();
        }
    }

    /// <summary>How solid its backdrop is, 0 to 100 (the editor's Backdrop).</summary>
    public double BackgroundPercent
    {
        get => Math.Round(Config.BackgroundOpacity * 100);
        set
        {
            if (Math.Abs(value - BackgroundPercent) < 1) return;
            _settings.Update(s => { if (s.Widgets.FirstOrDefault(w => w.Id == Id) is { } c) c.BackgroundOpacity = Math.Clamp(value / 100, 0, 1); });
            OnPropertyChanged(nameof(BackgroundPercent));
            Redraw();
        }
    }

    /// <summary>Its size, S to 3XL (the editor's Size).</summary>
    public string Size
    {
        get => Config.Scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (value is null || value == Size
                || !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale)) return;
            _settings.Update(s => { if (s.Widgets.FirstOrDefault(w => w.Id == Id) is { } c) c.Scale = scale; });
            OnPropertyChanged(nameof(Size));
            Redraw();
        }
    }

    [ObservableProperty] private bool _isEditing;

    [RelayCommand]
    private void Edit() => _page.Edit(this);

    private ImageSource? _preview, _big;
    private bool _drawn, _bigDrawn;

    /// <summary>The widget at its size on the desktop, for the pane beside the list, drawn sharp.</summary>
    public ImageSource? BigPreview
    {
        get
        {
            if (!_bigDrawn)
            {
                _bigDrawn = true;
                _shown ??= Shows();
                _big = WidgetPictures.Draw(Config, _live, _settings.Current, 2f);
            }
            return _big;
        }
    }

    /// <summary>The widget as it looks, drawn the first time the card is shown and again when it changes.</summary>
    public ImageSource? Preview
    {
        get
        {
            if (!_drawn)
            {
                _drawn = true;
                _shown ??= Shows();
                _preview = WidgetPictures.Draw(Config, _live, _settings.Current, 1.5f);
            }
            return _preview;
        }
    }

    /// <summary>Draws it again (its settings or the readings changed), if it has been shown.</summary>
    public void Redraw()
    {
        _shown = Shows();
        if (_bigDrawn)
        {
            _big = WidgetPictures.Draw(Config, _live, _settings.Current, 2f);
            OnPropertyChanged(nameof(BigPreview));
        }
        if (!_drawn) return;
        _preview = WidgetPictures.Draw(Config, _live, _settings.Current, 1.5f);
        OnPropertyChanged(nameof(Preview));
    }

    /// <summary>
    /// Any change to the settings: the card's words follow, and its pictures are drawn again only when what they show
    /// changed (its widget, the app's look, the units, the language). The gate, 2026-11-11: every change drew every
    /// card again, so adding the 40th widget took three times as long as the first.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Locked));
        OnPropertyChanged(nameof(Where));
        OnPropertyChanged(nameof(Line));
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(BackgroundPercent));
        OnPropertyChanged(nameof(Size));
        if (Shows() != _shown) Redraw();
    }

    /// <summary>What the pictures show, as they were last drawn (null before then).</summary>
    private string? _shown;

    private string Shows()
    {
        var s = _settings.Current;
        return $"{s.Theme}|{s.DarkTheme}|{s.LightTheme}|{s.Accent}|{s.UseFahrenheit}|{s.Language}|"
            + System.Text.Json.JsonSerializer.Serialize(Config, SettingsStore.JsonOptions);
    }
}

/// <summary>A widget to add: a ready-made one or a tile on its own, with its picture.</summary>
public sealed partial class WidgetChoice(string title, string line, string group, Func<WidgetConfig> make, WidgetsViewModel page) : ObservableObject
{
    public string Title { get; } = title;
    public string Line { get; } = line;

    /// <summary>Its group's name in English, shown translated as the heading it's listed under.</summary>
    public string Group { get; } = group;

    public string GroupName => Lang.T(Group);

    /// <summary>The heading it's listed under, in capitals as every caption is.</summary>
    public string GroupCaps => GroupName.ToUpper(System.Globalization.CultureInfo.CurrentCulture);

    internal WidgetConfig Make() => make();

    private ImageSource? _preview;
    private bool _drawn;

    public ImageSource? Preview
    {
        get
        {
            if (!_drawn)
            {
                _drawn = true;
                _preview = page.Draw(Make());
            }
            return _preview;
        }
    }

    /// <summary>Adds it, closes the pop-up, and picks it in the list.</summary>
    [RelayCommand]
    private void Add() => page.Add(this);
}

/// <summary>A chip over the list of the person's widgets: All, On, Off.</summary>
public sealed partial class WidgetGroupChip(string key, int count) : ObservableObject
{
    public string Key { get; } = key;
    public string Name => Lang.T(Key);
    [ObservableProperty] private int _count = count;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// A widget just removed with its card's quick Remove: in the card's place for a few seconds, with Undo, which puts it
/// back as it was, where it was.
/// </summary>
public sealed partial class RemovedWidget(WidgetConfig config, int index, WidgetsViewModel page)
{
    internal WidgetConfig Config { get; } = config;
    internal int Index { get; } = index;
    public string Line { get; } = Lang.T($"{WidgetCatalog.Title(config)} removed.");

    [RelayCommand]
    private void Undo() => page.UndoRemove(this);
}

/// <summary>A widget on the page's map of the screens: where it is and how big, in screen pixels, and its picture.</summary>
public sealed record WidgetSpot(string Id, string Title, int X, int Y, int Width, int Height, ImageSource? Picture);

/// <summary>
/// The Widgets page, in two panes (approved mock-up UckKnwokm5kF7JjdjHZpqd, option C, 2026-11-11): Desktop widgets on or
/// off at the top. On the left the list (search, All, On, Off; Add widget and Create widget at its foot) and the settings
/// for every widget; on the right the picked widget, with where it is on the screens and its quick settings. Add widget
/// opens a pop-up of the ready-made widgets and every tile (the user, 2026-11-11). The editor is a page of its own.
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
        _mapTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _mapTimer.Tick += (_, _) => RedrawMap();
        Choices = MakeChoices();
        ChoiceGroups = System.Windows.Data.CollectionViewSource.GetDefaultView(ChoicesShown);
        ChoiceGroups.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(WidgetChoice.GroupCaps)));
        MineChips = [new(AllGroup, 0), new(OnGroup, 0), new(OffGroup, 0)];
        MineChips[0].IsSelected = true;
        _undoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _undoTimer.Tick += (_, _) => { _undoTimer.Stop(); Removed = null; FillGrid(); };
        FilterChoices();
        Sync();
        // The tray menu, the agent and the editor change these while the page is open.
        settings.Changed += () =>
        {
            OnPropertyChanged(nameof(Shown));
            OnPropertyChanged(nameof(Snap));
            OnPropertyChanged(nameof(ShownHotkey));
            OnPropertyChanged(nameof(FrontHotkey));
            OnPropertyChanged(nameof(MapLine));
            MapChanged();
        };
    }

    private readonly System.Windows.Threading.DispatcherTimer _mapTimer;
    private readonly System.Windows.Threading.DispatcherTimer _undoTimer;

    /// <summary>The widget shown in the pane beside the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickedId), nameof(PickedRow), nameof(HasPicked), nameof(MapLine), nameof(NothingPicked), nameof(HasRemoved))]
    private WidgetCard? _picked;

    public bool HasPicked => Picked is not null;

    /// <summary>In the pane when no widget is picked: there are none yet, or the search shows none.</summary>
    public string NothingPicked => Cards.Count == 0 ? Lang.T("Add a widget, or create your own, to see it here.") : Lang.T("Pick a widget on the left to see it here.");

    /// <summary>The list's picked row (a widget just removed can't be picked: the list goes back to the one picked).</summary>
    public object? PickedRow
    {
        get => Picked;
        set
        {
            if (value is WidgetCard c) Picked = c;
            else System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => OnPropertyChanged(nameof(PickedRow)));
        }
    }

    /// <summary>Under the map: how to move the picked widget, or why it isn't there.</summary>
    public string MapLine =>
        !_settings.Current.WidgetsShown ? Lang.T("Turn Desktop widgets on to see it here.")
        : Picked is not { Enabled: true } ? Lang.T("Turn it on to see it here.")
        : Lang.T("Drag it on the map to move it.");

    /// <summary>The picked widget by its identifier: a click on another widget on the map picks that one.</summary>
    public string? PickedId
    {
        get => Picked?.Id;
        set { if (value is not null && Cards.FirstOrDefault(c => c.Id == value) is { } c) Picked = c; }
    }

    /// <summary>A widget's themes and sizes, for the picked one's quick settings.</summary>
    public IReadOnlyList<Choice> ThemeChoices { get; } = WidgetEditorViewModel.ThemeList();
    public IReadOnlyList<Choice> SizeChoices => WidgetEditorViewModel.SizeList;

    // ── On or off, and the settings for all widgets ─────────────────────

    /// <summary>Whether the widgets show on the desktop (the page's first card; the tray menu has it too). Off keeps each one's own.</summary>
    public bool Shown
    {
        get => _settings.Current.WidgetsShown;
        set { if (value != Shown) _settings.Update(s => s.WidgetsShown = value); }
    }

    public bool Snap
    {
        get => _settings.Current.WidgetSnap;
        set { if (value != Snap) _settings.Update(s => s.WidgetSnap = value); }
    }

    /// <summary>The shortcut for Desktop widgets as the person pressed it ("Ctrl+Alt+W"), or null.</summary>
    public string? ShownHotkey => _settings.Current.WidgetsHotkey;
    public string? FrontHotkey => _settings.Current.WidgetsFrontHotkey;

    /// <summary>Which shortcut is waiting for its keys: "shown", "front", or null.</summary>
    [ObservableProperty] private string? _recording;

    [RelayCommand]
    private void RecordShortcut(string which) => Recording = Recording == which ? null : which;

    [RelayCommand]
    private void ClearShortcut(string which)
    {
        Recording = null;
        _settings.Update(s => { if (which == "front") s.WidgetsFrontHotkey = null; else s.WidgetsHotkey = null; });
    }

    /// <summary>The keys pressed while a shortcut was waiting for them; false when they can't be a shortcut (the page says nothing, it keeps waiting).</summary>
    public bool Record(int virtualKey, HotkeyModifiers modifiers)
    {
        if (Recording is not { } which) return false;
        var hotkey = new Hotkey(modifiers, virtualKey);
        if (!Hotkey.IsSupportedKey(virtualKey) || !hotkey.IsValid) return false;
        string text = hotkey.ToString();
        _settings.Update(s => { if (which == "front") s.WidgetsFrontHotkey = text; else s.WidgetsHotkey = text; });
        Recording = null;
        return true;
    }

    // ── The widgets the person has ─────────────────────────────────────

    /// <summary>Every widget, in the settings' order (the built-in ones first, as they always were).</summary>
    public ObservableCollection<WidgetCard> Cards { get; } = [];

    /// <summary>
    /// The cards as the page lays them out: the widgets the chips and search let through, a widget just removed in its
    /// card's place.
    /// </summary>
    public ObservableCollection<object> Grid { get; } = [];

    public static string OnGroup => Lang.Key("On");
    public static string OffGroup => Lang.Key("Off");

    /// <summary>All, On and Off over the widgets, with how many each holds.</summary>
    public List<WidgetGroupChip> MineChips { get; }

    public WidgetGroupChip? PickedMineChip
    {
        get => MineChips.FirstOrDefault(c => c.IsSelected);
        set
        {
            if (value is null || value == PickedMineChip) return;
            foreach (var c in MineChips) c.IsSelected = c == value;
            OnPropertyChanged(nameof(PickedMineChip));
            FillGrid();
        }
    }

    [ObservableProperty] private string _mineQuery = "";

    partial void OnMineQueryChanged(string value) => FillGrid();

    /// <summary>Why the grid is empty: nothing matches the search, or no widget is on (or off).</summary>
    [ObservableProperty] private string _noMineText = "";

    /// <summary>The widget just removed, while its Undo shows.</summary>
    public RemovedWidget? Removed
    {
        get => _removed;
        private set
        {
            _removed = value;
            OnPropertyChanged(nameof(Removed));
            OnPropertyChanged(nameof(HasRemoved));
        }
    }

    private RemovedWidget? _removed;

    public bool HasRemoved => Removed is not null && Picked is null;

    /// <summary>A card's switch changed: the counts, and the grid under On or Off, follow once the click is done.</summary>
    internal void CardsChanged() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(FillGrid, System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>Lays the grid out again from the cards, the chips and the search.</summary>
    internal void FillGrid()
    {
        string chip = PickedMineChip?.Key ?? AllGroup, q = MineQuery.Trim();
        bool Passes(WidgetCard c, string g) => g == AllGroup || (g == OnGroup) == c.Enabled;
        foreach (var g in MineChips) g.Count = Cards.Count(c => Passes(c, g.Key));
        var shown = new List<object>(Cards.Where(c => Passes(c, chip)
            && (q.Length == 0 || c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || c.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase))));
        NoMineText = shown.Count > 0 ? ""
            : q.Length > 0 ? Lang.T($"Nothing matches “{q}”.")
            : chip == OnGroup ? Lang.T("No widgets are on.")
            : chip == OffGroup ? Lang.T("No widgets are off.")
            : Lang.T("No widgets yet.");
        if (Removed is { } r)
        {
            // In its card's place: before the first card that came after it.
            int at = shown.FindIndex(o => o is WidgetCard c && Cards.IndexOf(c) >= r.Index);
            shown.Insert(at < 0 ? shown.Count : at, r);
        }
        if (!Grid.SequenceEqual(shown))
        {
            Grid.Clear();
            foreach (var o in shown) Grid.Add(o);
        }
        // The picked widget stays picked while the list shows it; otherwise the first one shown is (not while the pane
        // says a widget was just removed).
        if (Removed is not null && Picked is null) OnPropertyChanged(nameof(PickedRow));
        else if (Picked is null || !shown.Contains(Picked)) Picked = shown.OfType<WidgetCard>().FirstOrDefault();
        else OnPropertyChanged(nameof(PickedRow));
        OnPropertyChanged(nameof(MapLine));
    }

    /// <summary>
    /// Removes a widget at once from its card (a built-in one too: the list to add from has it again), with Undo in the
    /// card's place for a few seconds.
    /// </summary>
    internal void Remove(WidgetCard card, WidgetConfig? asItWas = null)
    {
        int index = _settings.Current.Widgets.FindIndex(w => w.Id == card.Id);
        if (index < 0) return;
        var saved = WidgetEditorViewModel.Clone(asItWas ?? _settings.Current.Widgets[index]);
        _undoTimer.Stop();
        // Nothing is picked next: the pane says what was removed, with Undo where Remove was, so a second click can't
        // take another widget away (the user lost every widget to quick clicks on Remove, 2026-11-11).
        Removed = new RemovedWidget(saved, index, this);
        Picked = null;
        _settings.Update(s => s.Widgets.RemoveAll(w => w.Id == card.Id));
        Sync();
        _undoTimer.Start();
    }

    /// <summary>Puts a removed widget back as it was, where it was in the list.</summary>
    internal void UndoRemove(RemovedWidget removed)
    {
        if (Removed != removed) return;
        _undoTimer.Stop();
        Removed = null;
        _settings.Update(s =>
        {
            if (s.Widgets.Any(w => w.Id == removed.Config.Id)) return;
            s.Widgets.Insert(Math.Min(removed.Index, s.Widgets.Count), WidgetEditorViewModel.Clone(removed.Config));
        });
        Sync();
        PickedId = removed.Config.Id;
        FillGrid();
    }

    public bool CanCreate => _settings.Current.Widgets.Count(w => w.Style == WidgetStyle.Custom) < WidgetCatalog.MaxCustom;

    /// <summary>
    /// At the most RigSight keeps, Add widget and Create widget are off and the line under them says why (the audit,
    /// 2026-11-11: Add widget opened its list and a click on any of it did nothing, with no word).
    /// </summary>
    public bool AtLimit => !CanCreate;

    public static string LimitLine => Lang.T($"You have {WidgetCatalog.MaxCustom} widgets, the most RigSight keeps. Remove one to add another.");

    /// <summary>The widget open in the editor, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private WidgetEditorViewModel? _editor;

    public bool IsEditing => Editor is not null;

    public void Edit(WidgetCard card) => Edit(card.Id);

    /// <summary>Opens the editor on a widget by its identifier (from its right-click menu on the desktop too).</summary>
    public void Edit(string? id)
    {
        if (id is null || _settings.Current.Widgets.All(w => w.Id != id)) return;
        // Opened from a widget's menu on the desktop while the editor is open on another: that one is done (its redraw
        // each second stops, and a new widget left empty in it goes), as Back would leave it.
        if (Editor is { } open)
        {
            if (open.Id == id) return;
            LetGo(open);
            Sync();
        }
        // A shortcut left waiting for its keys stops waiting: the editor's keys are typing, not a shortcut.
        Recording = null;
        PickedId = id;
        foreach (var c in Cards) c.IsEditing = c.Id == id;
        Editor = new WidgetEditorViewModel(id, _settings, _live, CloseEditor,
            (wid, x, y) => _client.SendCommand(WidgetCatalog.MoveCommand, WidgetCatalog.MoveArg(wid, x, y)), () => RemoveFromEditor(id));
    }

    /// <summary>
    /// The editor's Remove: back on the page, which says what was removed, with Undo, as a card's Remove does (audit
    /// 2026-11-11: from the editor a widget went for good with one click). A new widget left empty just goes, as on Back.
    /// </summary>
    private void RemoveFromEditor(string id)
    {
        CloseEditor();
        if (Cards.FirstOrDefault(c => c.Id == id) is { } card) Remove(card);
    }

    [RelayCommand]
    private void CloseEditor()
    {
        if (Editor is { } editor) LetGo(editor);
        Editor = null;
        foreach (var c in Cards) c.IsEditing = false;
        Sync();
        foreach (var c in Cards) c.Refresh();
        LoadMap();
    }

    /// <summary>
    /// An editor closed: it stops drawing. A new widget left with nothing in it isn't kept; one that had tiles and was
    /// emptied goes the way Remove takes it, with Undo (the audit, 2026-11-11: it went for good, with no word).
    /// </summary>
    private void LetGo(WidgetEditorViewModel editor)
    {
        editor.Close();
        if (_settings.Current.Widgets.FirstOrDefault(w => w.Id == editor.Id) is not { Tiles.Count: 0 }) return;
        if (!editor.StartedEmpty && Cards.FirstOrDefault(c => c.Id == editor.Id) is { } card)
        {
            // Undo brings it back as it was when the editor opened, with its tiles.
            Remove(card, editor.Opened);
            return;
        }
        _settings.Update(s => s.Widgets.RemoveAll(w => w.Id == editor.Id && w.Tiles is { Count: 0 }));
    }

    /// <summary>A new widget with nothing in it yet, opened in the editor (an empty one is gone again when the editor closes).</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        var cfg = NewWidget(Lang.T("My widget"), []);
        _settings.Update(s => s.Widgets.Add(cfg));
        Sync();
        Edit(cfg.Id);
    }

    /// <summary>A widget of the person's own as a new one starts: on the desktop, in the app's own look, on.</summary>
    internal static WidgetConfig NewWidget(string name, List<WidgetTile> tiles) => new()
    {
        Style = WidgetStyle.Custom, Id = WidgetCatalog.NewId(), Name = name, Tiles = tiles, Enabled = true,
        Theme = WidgetTheme.App,
    };

    // ── The list to add from ────────────────────────────────────────────

    public static string AllGroup => Lang.Key("All");
    public static string ReadyMade => Lang.Key("Ready-made");

    /// <summary>The ready-made widgets (today's built-in ones, as tiles) and every tile on its own.</summary>
    public IReadOnlyList<WidgetChoice> Choices { get; }

    /// <summary>The ones the search lets through, in their groups (Ready-made, Everyday, Live, Your day).</summary>
    public ObservableCollection<WidgetChoice> ChoicesShown { get; } = [];

    public System.ComponentModel.ICollectionView ChoiceGroups { get; }

    /// <summary>Whether the Add widget pop-up is open.</summary>
    [ObservableProperty] private bool _addListOpen;

    /// <summary>Opens the Add widget pop-up, its search cleared.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void OpenAddList()
    {
        ChoiceQuery = "";
        AddListOpen = true;
    }

    [ObservableProperty] private string _choiceQuery = "";

    partial void OnChoiceQueryChanged(string value) => FilterChoices();

    public bool NoChoices => ChoicesShown.Count == 0;
    public string NoChoicesText => Lang.T($"Nothing matches “{ChoiceQuery.Trim()}”.");

    private void FilterChoices()
    {
        string q = ChoiceQuery.Trim();
        var shown = Choices.Where(c => q.Length == 0 || c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || c.Line.Contains(q, StringComparison.CurrentCultureIgnoreCase) || c.GroupName.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        ChoicesShown.Clear();
        foreach (var c in shown) ChoicesShown.Add(c);
        OnPropertyChanged(nameof(NoChoices));
        OnPropertyChanged(nameof(NoChoicesText));
    }

    private List<WidgetChoice> MakeChoices()
    {
        var list = new List<WidgetChoice>();
        // A ready-made widget that is one tile (the FPS widget, the graph, Today, Now playing) is listed once, as that tile.
        var tileNames = WidgetTiles.Kinds.Select(WidgetTiles.NameOf).ToHashSet();
        foreach (var style in WidgetCatalog.Listed.Where(s => !tileNames.Contains(WidgetCatalog.Title(s))))
            list.Add(new WidgetChoice(WidgetCatalog.Title(style), WidgetCatalog.Description(style), ReadyMade,
                () => NewWidget(WidgetCatalog.Title(style), WidgetTiles.FromLayout(new WidgetConfig { Style = style, Id = style.ToString() })), this));
        foreach (var kind in WidgetTiles.Kinds)
            list.Add(new WidgetChoice(WidgetTiles.NameOf(kind), WidgetTiles.LineOf(kind), kind.Group,
                () => NewWidget(WidgetTiles.NameOf(kind), [WidgetTiles.New(kind)]), this));
        return list;
    }

    internal ImageSource? Draw(WidgetConfig cfg, float scale = 1.5f) => WidgetPictures.Draw(cfg, _live, _settings.Current, scale);

    /// <summary>
    /// Adds a widget from the pop-up: on, on the desktop, placed by the agent at the top right of the main screen; the
    /// pop-up closes and it's picked in the list, so it's clear where it went.
    /// </summary>
    internal void Add(WidgetChoice choice)
    {
        if (!CanCreate) return;
        var cfg = choice.Make();
        _settings.Update(s => s.Widgets.Add(cfg));
        MineQuery = "";
        PickedMineChip = MineChips[0];
        Sync();
        PickedId = cfg.Id;
        AddListOpen = false;
    }

    // ── The map of the screens ──────────────────────────────────────────

    public List<ScreenChoice.Monitor> Screens { get; private set; } = [];

    /// <summary>The widgets that are on and have a spot, where they are on the screens.</summary>
    [ObservableProperty] private List<WidgetSpot> _spots = [];

    /// <summary>Whether the page is on the screen (the view says): only then does the map follow every change.</summary>
    public bool PageShown { get; set; }

    /// <summary>
    /// Any change to the settings (a widget switched on or off, added, removed, moved on the desktop, the tray's switch)
    /// draws the map again, a moment later so a burst of changes draws it once (the user, 2026-11-10: it still showed
    /// widgets that had been removed).
    /// </summary>
    private void MapChanged()
    {
        if (!_mapTimer.Dispatcher.CheckAccess())
        {
            _mapTimer.Dispatcher.BeginInvoke(MapChanged);
            return;
        }
        if (!PageShown || IsEditing) return;
        _mapTimer.Stop();
        _mapTimer.Start();
    }

    /// <summary>Reads the screens and draws where each widget is (the page's visits, and after the editor).</summary>
    public void LoadMap()
    {
        // Weather tiles' pictures show the weather once it has come.
        WeatherFeed.RefreshInBackground(_settings.Current.Widgets.SelectMany(w => w.Tiles ?? []),
            () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { foreach (var c in Cards) c.Redraw(); }));
        try { Screens = ScreenChoice.ReadAll(null); }
        catch (Exception) { Screens = []; }
        OnPropertyChanged(nameof(Screens));
        RedrawMap();
    }

    /// <summary>Where each widget that is on is now, on the screens already read.</summary>
    internal void RedrawMap()
    {
        _mapTimer.Stop();
        var spots = new List<WidgetSpot>();
        if (_settings.Current.WidgetsShown)
            foreach (var w in _settings.Current.Widgets.Where(w => w is { Enabled: true, X: not null, Y: not null }))
            {
                var (x, y) = (w.X!.Value, w.Y!.Value);
                double scale = (Screens.FirstOrDefault(s => x >= s.Left && x < s.Left + s.Width && y >= s.Top && y < s.Top + s.Height)?.Scale ?? 1) * w.Scale;
                var (pw, ph) = WidgetPictures.SizeOf(w);
                spots.Add(new WidgetSpot(w.Id, WidgetCatalog.Title(w), x, y, (int)Math.Round(pw * scale), (int)Math.Round(ph * scale), Draw(w)));
            }
        Spots = spots;
    }

    /// <summary>
    /// A widget dragged on the map, or placed from its editor, to a new spot (its panel's top left, in screen pixels):
    /// saved here, and sent to the agent as its own command, the one way the app moves a widget on the desktop.
    /// </summary>
    public void MoveWidget(string id, int x, int y)
    {
        _settings.Update(s => { if (s.Widgets.FirstOrDefault(w => w.Id == id) is { } c) (c.X, c.Y) = (x, y); });
        _client.SendCommand(WidgetCatalog.MoveCommand, WidgetCatalog.MoveArg(id, x, y));
        LoadMap();
        foreach (var c in Cards.Where(c => c.Id == id)) c.Refresh();
    }

    /// <summary>The name of the screen a spot is on: "Main screen", else "Screen 2" as Windows numbers it.</summary>
    internal string ScreenName(int x, int y)
    {
        if (Screens.Count == 0) LoadScreensQuietly();
        var s = Screens.FirstOrDefault(s => x >= s.Left && x < s.Left + s.Width && y >= s.Top && y < s.Top + s.Height);
        if (s is null) return "";
        return s.IsMain ? Lang.T("Main screen") : Lang.T($"Screen {ScreenChoice.NumberOf(s.Device)}");
    }

    private void LoadScreensQuietly()
    {
        try { Screens = ScreenChoice.ReadAll(null); }
        catch (Exception) { Screens = []; }
    }

    // ── Keeping up with the settings ────────────────────────────────────

    /// <summary>Makes the cards match the settings: one for each widget, in its order.</summary>
    private void Sync()
    {
        var ids = _settings.Current.Widgets.Select(w => w.Id).ToList();
        if (!Cards.Select(c => c.Id).SequenceEqual(ids))
        {
            var keep = Cards.ToDictionary(c => c.Id);
            Cards.Clear();
            foreach (var id in ids) Cards.Add(keep.GetValueOrDefault(id) ?? new WidgetCard(id, _settings, _live, this));
        }
        FillGrid();
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(AtLimit));
        CreateCommand.NotifyCanExecuteChanged();
        OpenAddListCommand.NotifyCanExecuteChanged();
        if (AtLimit) AddListOpen = false;
    }

    /// <summary>The agent used to draw the cards' pictures; the page draws them itself now, so this only refreshes them.</summary>
    public void RequestPreviews()
    {
        foreach (var c in Cards) c.Redraw();
        LoadMap();
    }

    public void OnPreviewsReady() { }

    public void Refresh()
    {
        Sync();
        foreach (var c in Cards) c.Refresh();
        if (Editor is { } editor && _settings.Current.Widgets.All(w => w.Id != editor.Id)) CloseEditor();
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
