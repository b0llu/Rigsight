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
/// A widget the person has, on the Widgets page: its picture (drawn here with the desktop's own drawing), its name and
/// what it is, its switch, the screen it's on, and Edit.
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
        }
    }

    /// <summary>The screen it's on, while it's on and has a spot ("Main screen", "Screen 2"); empty otherwise.</summary>
    public string Where => Config is { Enabled: true, X: int x, Y: int y } ? _page.ScreenName(x, y) : "";

    [ObservableProperty] private bool _isEditing;

    [RelayCommand]
    private void Edit() => _page.Edit(this);

    private ImageSource? _preview;
    private bool _drawn;

    /// <summary>The widget as it looks, drawn the first time the card is shown and again when it changes.</summary>
    public ImageSource? Preview
    {
        get
        {
            if (!_drawn)
            {
                _drawn = true;
                _preview = WidgetPictures.Draw(Config, _live, _settings.Current, 1.5f);
            }
            return _preview;
        }
    }

    /// <summary>Draws it again (its settings or the readings changed), if it has been shown.</summary>
    public void Redraw()
    {
        if (!_drawn) return;
        _preview = WidgetPictures.Draw(Config, _live, _settings.Current, 1.5f);
        OnPropertyChanged(nameof(Preview));
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Where));
        Redraw();
    }
}

/// <summary>A widget to add: a ready-made one or a tile on its own, with its picture.</summary>
public sealed partial class WidgetChoice(string title, string line, string group, Func<WidgetConfig> make, WidgetsViewModel page) : ObservableObject
{
    public string Title { get; } = title;
    public string Line { get; } = line;

    /// <summary>Its group's name in English (the chips' key), shown translated.</summary>
    public string Group { get; } = group;

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

    /// <summary>"Added" for a moment on the button after a click.</summary>
    [ObservableProperty] private bool _justAdded;

    [RelayCommand]
    private async Task Add()
    {
        page.Add(this);
        JustAdded = true;
        await Task.Delay(1400);
        JustAdded = false;
    }
}

/// <summary>A chip over the list of widgets to add.</summary>
public sealed partial class WidgetGroupChip(string key, int count) : ObservableObject
{
    public string Key { get; } = key;
    public string Name => Lang.T(Key);
    public int Count { get; } = count;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>The last tile in the grid of widgets: make one of your own.</summary>
public sealed class MakeYourOwnTile;

/// <summary>A widget on the page's map of the screens: where it is and how big, in screen pixels, and its picture.</summary>
public sealed record WidgetSpot(string Id, string Title, int X, int Y, int Width, int Height, ImageSource? Picture);

/// <summary>
/// The Widgets page (approved mock-up 8Bfo1Ce7KFJs4g1wxHQP1U): Desktop widgets on or off; the screens with the widgets
/// on them; the widgets the person has; the list to add from (ready-made widgets and every tile); the settings for all
/// widgets; and the editor, a page of its own, for one widget.
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
        Chips = [.. new[] { AllGroup, ReadyMade }.Concat(WidgetTiles.Kinds.Select(k => k.Group).Distinct())
            .Select(g => new WidgetGroupChip(g, g == AllGroup ? Choices.Count : Choices.Count(c => c.Group == g)))];
        Chips[0].IsSelected = true;
        FilterChoices();
        Sync();
        // The tray menu, the agent and the editor change these while the page is open.
        settings.Changed += () =>
        {
            OnPropertyChanged(nameof(Shown));
            OnPropertyChanged(nameof(Snap));
            OnPropertyChanged(nameof(Fade));
            OnPropertyChanged(nameof(ShownHotkey));
            OnPropertyChanged(nameof(FrontHotkey));
            MapChanged();
        };
    }

    private readonly System.Windows.Threading.DispatcherTimer _mapTimer;

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

    public bool Fade
    {
        get => _settings.Current.WidgetFade;
        set { if (value != Fade) _settings.Update(s => s.WidgetFade = value); }
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

    /// <summary>The cards as the page lays them out: every widget, then the "Make your own" tile at the end of the grid.</summary>
    public ObservableCollection<object> Grid { get; } = [];

    public MakeYourOwnTile MakeYourOwn { get; } = new();

    public bool CanCreate => _settings.Current.Widgets.Count(w => w.Style == WidgetStyle.Custom) < WidgetCatalog.MaxCustom;

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
        foreach (var c in Cards) c.IsEditing = c.Id == id;
        Editor = new WidgetEditorViewModel(id, _settings, _live, CloseEditor);
    }

    [RelayCommand]
    private void CloseEditor()
    {
        if (Editor is { } editor)
        {
            editor.Close();
            // A new widget left with nothing in it isn't kept.
            if (_settings.Current.Widgets.FirstOrDefault(w => w.Id == editor.Id) is { Tiles.Count: 0 })
                _settings.Update(s => s.Widgets.RemoveAll(w => w.Id == editor.Id && w.Tiles is { Count: 0 }));
        }
        Editor = null;
        foreach (var c in Cards) c.IsEditing = false;
        Sync();
        foreach (var c in Cards) c.Refresh();
        LoadMap();
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
        Theme = WidgetTheme.App, Sits = WidgetSits.OnDesktop,
    };

    // ── The list to add from ────────────────────────────────────────────

    public static string AllGroup => Lang.Key("All");
    public static string ReadyMade => Lang.Key("Ready-made");

    /// <summary>The ready-made widgets (today's built-in ones, as tiles) and every tile on its own.</summary>
    public IReadOnlyList<WidgetChoice> Choices { get; }

    public List<WidgetGroupChip> Chips { get; }

    public ObservableCollection<WidgetChoice> ChoicesShown { get; } = [];

    [ObservableProperty] private string _choiceQuery = "";

    partial void OnChoiceQueryChanged(string value) => FilterChoices();

    /// <summary>The chip picked over the list (the page's chips are a list with one picked).</summary>
    public WidgetGroupChip? PickedChip
    {
        get => Chips.FirstOrDefault(c => c.IsSelected);
        set { if (value is not null && value != PickedChip) PickChip(value); }
    }

    [RelayCommand]
    private void PickChip(WidgetGroupChip chip)
    {
        foreach (var c in Chips) c.IsSelected = c == chip;
        OnPropertyChanged(nameof(PickedChip));
        FilterChoices();
    }

    public bool NoChoices => ChoicesShown.Count == 0;
    public string NoChoicesText => Lang.T($"Nothing matches “{ChoiceQuery.Trim()}”.");

    private void FilterChoices()
    {
        string group = Chips.FirstOrDefault(c => c.IsSelected)?.Key ?? AllGroup, q = ChoiceQuery.Trim();
        var shown = Choices.Where(c => (group == AllGroup || c.Group == group)
            && (q.Length == 0 || c.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || c.Line.Contains(q, StringComparison.CurrentCultureIgnoreCase)));
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

    internal ImageSource? Draw(WidgetConfig cfg) => WidgetPictures.Draw(cfg, _live, _settings.Current, 1.5f);

    /// <summary>Adds a widget from the list: on, on the desktop, placed by the agent at the top right of the main screen.</summary>
    internal void Add(WidgetChoice choice)
    {
        if (!CanCreate) return;
        var cfg = choice.Make();
        _settings.Update(s => s.Widgets.Add(cfg));
        Sync();
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

    /// <summary>A widget dragged on the map to a new spot (its panel's top left, in screen pixels).</summary>
    public void MoveWidget(string id, int x, int y)
    {
        _settings.Update(s => { if (s.Widgets.FirstOrDefault(w => w.Id == id) is { } c) (c.X, c.Y) = (x, y); });
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
            Grid.Clear();
            foreach (var c in Cards) Grid.Add(c);
            Grid.Add(MakeYourOwn);
        }
        else if (Grid.Count == 0) Grid.Add(MakeYourOwn);
        OnPropertyChanged(nameof(CanCreate));
        CreateCommand.NotifyCanExecuteChanged();
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
