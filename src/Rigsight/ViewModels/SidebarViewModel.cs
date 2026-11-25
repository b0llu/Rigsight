using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A page in the sidebar: a built-in one, or one of the user's dashboards.</summary>
/// <param name="icon">An icon-font character, or "fan" for the drawn fan (see <see cref="Converters.NavIconConverter"/>).</param>
/// <param name="home">The group it's in until the user moves it.</param>
/// <param name="dashboard">The dashboard this is the sidebar's entry for; null for a built-in page.</param>
public sealed partial class NavEntry(string key, string title, string icon, string home, SidebarViewModel owner, CustomPageViewModel? dashboard = null) : ObservableObject
{
    public string Key { get; } = key;
    public string Icon { get; } = icon;
    internal string Home { get; } = home;
    internal CustomPageViewModel? Dashboard { get; } = dashboard;

    /// <summary>Its own name: what it's called until renamed, and again once the typed name is cleared.</summary>
    public string BuiltInTitle { get; } = title;

    /// <summary>What the sidebar calls it: the user's name for it, or its own.</summary>
    public string Title => Name.Trim() is { Length: > 0 } name ? name : BuiltInTitle;

    /// <summary>
    /// The name box in the sidebar editor: the user's name as typed, empty for its own. A dashboard has one name only,
    /// its own: the box shows it, and typing here renames the dashboard itself.
    /// </summary>
    public string Name
    {
        get => Dashboard?.Name ?? owner.NameOf(Key);
        set => owner.Rename(this, value);
    }

    /// <summary>Home always stays in the sidebar.</summary>
    public bool CanHide => Key != "home";

    /// <summary>An update's new feature is here and it hasn't been opened since: a dot, and what's new as its hover.</summary>
    public bool IsNew => owner.NewPage == Key && !IsNewPage;
    public string? NewText => IsNew ? owner.NewText : null;

    /// <summary>A page an update added that this person hasn't opened yet: a NEW tag, here and in All pages (mock-up 2026-11-24).</summary>
    public bool IsNewPage => owner.IsNewPage(Key);

    internal void NewChanged() { OnPropertyChanged(nameof(IsNew)); OnPropertyChanged(nameof(NewText)); OnPropertyChanged(nameof(IsNewPage)); }

    /// <summary>
    /// A page left out of the sidebar, listed in its group's "N more" (while that list is open, or while it's the page on
    /// screen). It never slots in among the shown pages, so nothing jumps when it's opened (the user, 2026-11-24).
    /// </summary>
    [ObservableProperty] private bool _inMore;

    /// <summary>What the page is for, in one line (All pages, a new page's Home card).</summary>
    public string Description => SidebarViewModel.DescriptionOf(Key);

    /// <summary>Its name is being typed in the Customize pop-up (the pencil); otherwise the name shows as text.</summary>
    [ObservableProperty] private bool _isRenaming;
    [RelayCommand] private void ToggleRename() => IsRenaming = !IsRenaming;

    /// <summary>Bound two-way to the sidebar button: picking it opens the page.</summary>
    [ObservableProperty] private bool _isSelected;
    partial void OnIsSelectedChanged(bool value)
    {
        if (value) owner.Open(this);
    }

    /// <summary>In the sidebar: not hidden, and its section open (a folded section still shows the page you're on).</summary>
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>The "Show" switch in the sidebar editor.</summary>
    public bool IsShown
    {
        get => !owner.IsHidden(Key);
        set => owner.SetShown(this, value);
    }

    internal void ShownChanged() => OnPropertyChanged(nameof(IsShown));

    internal void NameChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Title));
    }
}

/// <summary>A page as the new-install card pictures it: its sidebar icon (see <see cref="Converters.NavIconConverter"/>), lit or not.</summary>
public sealed record SidebarTile(string Icon, bool Lit);

/// <summary>
/// One group of the sidebar: the top one (no heading), DASHBOARDS, HARDWARE or ON SCREEN. A heading can be renamed and
/// left out; without its heading a group can't fold, and its pages follow the ones above.
/// </summary>
/// <param name="name">The heading's own name ("Hardware"); null for the top group, which has none.</param>
public sealed class SidebarGroup(string key, string? name, SidebarViewModel owner) : ObservableObject
{
    public string Key { get; } = key;
    public bool HasHeading { get; } = name is not null;

    public ObservableCollection<NavEntry> Pages { get; } = [];

    /// <summary>The heading's own name: what it says until renamed, and again once the typed name is cleared.</summary>
    public string BuiltInName { get; } = name ?? "";

    /// <summary>The same in capitals, as the sidebar writes headings.</summary>
    public string BuiltInHeading => BuiltInName.ToUpperInvariant();

    /// <summary>The name box in the sidebar editor: the user's name as typed, empty for its own.</summary>
    public string Name
    {
        get => owner.HeadingNameOf(Key);
        set => owner.RenameHeading(this, value);
    }

    /// <summary>What it's called: the user's name for it, or its own.</summary>
    public string Title => Name.Trim() is { Length: > 0 } typed ? typed : BuiltInName;

    /// <summary>The heading as the sidebar writes it.</summary>
    public string Heading => Title.ToUpper();

    /// <summary>The "Heading" switch in the sidebar editor.</summary>
    public bool ShowHeading
    {
        get => HasHeading && !owner.IsHeadingHidden(Key);
        set => owner.SetHeadingHidden(this, !value);
    }

    /// <summary>Folded away. Only a group whose heading shows can be: the heading is what unfolds it.</summary>
    public bool Collapsed => ShowHeading && owner.IsFolded(Key);

    /// <summary>
    /// The heading in the sidebar itself: shown, over any page of the group, shown or in its "N more" (approved mock-up
    /// 2026-11-24: a basics sidebar keeps ON SCREEN with "3 more" under it). DASHBOARDS, with nothing in it yet, keeps its
    /// heading for "New dashboard", except with only the basics shown.
    /// </summary>
    public bool HeadingInSidebar => ShowHeading && (Pages.Count > 0 || (Key == SidebarViewModel.Dashboards && !owner.IsBasic));

    /// <summary>Its heading's name is being typed in the Customize pop-up (the pencil), with the switch that hides the heading.</summary>
    public bool IsRenaming
    {
        get => _renaming;
        set { if (_renaming == value) return; _renaming = value; OnPropertyChanged(); }
    }
    private bool _renaming;
    public CommunityToolkit.Mvvm.Input.IRelayCommand ToggleRenameCommand => _toggle ??= new CommunityToolkit.Mvvm.Input.RelayCommand(() => IsRenaming = !IsRenaming);
    private CommunityToolkit.Mvvm.Input.RelayCommand? _toggle;

    /// <summary>It has no page (every one was moved out, or there is no dashboard yet): the editor shows a box to drop one in.</summary>
    public bool ShowDropBox => Pages.Count == 0;

    // ── "N more": the group's pages that aren't shown, at its end (approved mock-up 2026-11-24) ──

    /// <summary>The group's pages left out of the sidebar, in its order; each shows only while <see cref="NavEntry.InMore"/>.</summary>
    public ObservableCollection<NavEntry> MorePages { get; } = [];

    /// <summary>The "N more" list is open (for as long as the app runs).</summary>
    public bool MoreOpen
    {
        get => _moreOpen;
        set { if (_moreOpen == value) return; _moreOpen = value; OnPropertyChanged(nameof(MoreText)); owner.MoreToggled(); }
    }
    private bool _moreOpen;

    public CommunityToolkit.Mvvm.Input.IRelayCommand ToggleMoreCommand => _toggleMore ??= new CommunityToolkit.Mvvm.Input.RelayCommand(() => MoreOpen = !MoreOpen);
    private CommunityToolkit.Mvvm.Input.RelayCommand? _toggleMore;

    /// <summary>The "N more" row: there are pages left out, and the group isn't folded.</summary>
    public bool ShowMoreRow => MorePages.Count > 0 && !Collapsed;

    /// <summary>"3 more", or "Fewer" while the list is open.</summary>
    public string MoreText => MoreOpen ? Lang.T("Fewer") : MorePages.Count == 1 ? Lang.T("1 more") : Lang.T($"{MorePages.Count} more");

    /// <summary>
    /// The pages left out, in order. This runs on every page switch, so the row, its words and the heading are told only
    /// when the list really changed: telling them every time cost about 2 ms a switch (the 1.20.0 gate's made-up year,
    /// against 1.19.0: 2 and 4 switches over 100 ms instead of none). Folding and opening tell them themselves.
    /// </summary>
    internal void SyncMore(IEnumerable<NavEntry> hidden)
    {
        var wanted = hidden.ToList();
        if (wanted.Count == MorePages.Count && wanted.SequenceEqual(MorePages)) return;
        foreach (var gone in MorePages.Except(wanted).ToList()) MorePages.Remove(gone);
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = MorePages.IndexOf(wanted[i]);
            if (at < 0) MorePages.Insert(i, wanted[i]);
            else if (at != i) MorePages.Move(at, i);
        }
        OnPropertyChanged(nameof(ShowMoreRow));
        OnPropertyChanged(nameof(MoreText));
        OnPropertyChanged(nameof(HeadingInSidebar));
    }

    // ── Icons only: the group as one icon, its pages in a small list beside it (approved mock-up, 2026-10-25) ──

    /// <summary>The group's icon in the icons-only sidebar.</summary>
    public string Glyph => Key switch { SidebarViewModel.Dashboards => SidebarViewModel.DashboardIcon, SidebarViewModel.HardwareSection => "\uE950", _ => "\uE7F4" };

    /// <summary>Its pages that aren't hidden, for the list beside its icon (a folded group still lists them).</summary>
    public IReadOnlyList<NavEntry> ShownPages => [.. Pages.Where(p => !owner.IsHidden(p.Key))];

    /// <summary>Its icon is in the icons-only sidebar: it has a heading and a page to list (DASHBOARDS also for "New dashboard").</summary>
    public bool InRail => HasHeading && (ShownPages.Count > 0 || (Key == SidebarViewModel.Dashboards && !owner.IsBasic));

    /// <summary>The page on screen is one of its own: its icon is marked like a picked page.</summary>
    public bool HasSelected => Pages.Any(p => p.IsSelected);

    /// <summary>"New dashboard" in DASHBOARDS' list.</summary>
    public bool OffersNewDashboard => Key == SidebarViewModel.Dashboards;

    internal void RailChanged()
    {
        OnPropertyChanged(nameof(ShownPages));
        OnPropertyChanged(nameof(InRail));
        OnPropertyChanged(nameof(HasSelected));
    }

    /// <summary>
    /// Another page opened: only the mark on its icon changes. Its list isn't built again then: a page picked in it opens
    /// as the press goes down, and a list rebuilt at that moment would lose the click that closes it.
    /// </summary>
    internal void SelectionChanged() => OnPropertyChanged(nameof(HasSelected));

    internal void Changed()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(ShowHeading));
        OnPropertyChanged(nameof(Collapsed));
        OnPropertyChanged(nameof(ShowDropBox));
        OnPropertyChanged(nameof(HeadingInSidebar));
        OnPropertyChanged(nameof(ShowMoreRow));
        OnPropertyChanged(nameof(MoreText));
        RailChanged();
    }
}

/// <summary>
/// The sidebar's pages in their groups: your history (no heading), HARDWARE and ON SCREEN (the readouts Rigsight puts
/// outside its window: widgets, the game overlay, taskbar readings), with DASHBOARDS (the pages the user built) between.
/// The sidebar editor (opened from Settings) moves a page, a dashboard too, to any place in any group, renames and hides
/// pages and headings; DASHBOARDS, HARDWARE and ON SCREEN fold away with a click on their heading.
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    public const string MainSection = "main", Dashboards = "dashboards", HardwareSection = "hardware", OnScreenSection = "onscreen";

    /// <summary>
    /// Pages added after people could save their own sidebar order: one goes right after the page before it here (Games
    /// under Apps) rather than at the end of its group.
    /// </summary>
    private static readonly HashSet<string> AddedLater = ["games", "power", "latency"];

    /// <summary>
    /// "The basics" (2026-10-25): the pages someone who mostly wants to know their PC is fine looks at. Every other page is
    /// hidden, and still opens from links (Home's lines, the AI's sources).
    /// </summary>
    public static readonly IReadOnlySet<string> BasicPages = new HashSet<string> { "home", "reports", "games", "crashes", "temperatures", "storage" };

    /// <summary>Every built-in page, in its usual group and order (its title in English, shown translated). A new page is one more line here.</summary>
    private static readonly (string Key, string Title, string Icon, string Group)[] BuiltIn =
    [
        ("home", Lang.Key("Home"), "", MainSection),
        ("reports", Lang.Key("Reports"), "", MainSection),
        ("apps", Lang.Key("Apps"), "", MainSection),
        ("games", Lang.Key("Games"), "", MainSection),
        ("processes", Lang.Key("Processes"), "", MainSection),
        ("crashes", Lang.Key("Crashes"), "", MainSection),
        ("timeline", Lang.Key("Timeline"), "", MainSection),
        // Driver delays and the app wake-up check (issue #15): a stopwatch.
        ("latency", Lang.Key("Latency"), "", MainSection),
        ("temperatures", Lang.Key("Temperatures"), "", HardwareSection),
        ("power", Lang.Key("Power"), "", HardwareSection),
        ("fans", Lang.Key("Fans"), "fan", HardwareSection),
        ("memory", Lang.Key("Memory"), "", HardwareSection),
        ("storage", Lang.Key("Storage"), "", HardwareSection),
        ("network", Lang.Key("Network"), "network", HardwareSection),
        ("sensors", Lang.Key("All sensors"), "", HardwareSection),
        ("widgets", Lang.Key("Widgets"), "", OnScreenSection),
        ("overlay", Lang.Key("Overlay"), "", OnScreenSection),
        ("taskbar", Lang.Key("Taskbar"), "", OnScreenSection),
    ];

    /// <summary>What a dashboard is called while its name is empty (as it is saved then: see CustomPageViewModel.Save).</summary>
    private static string DashboardTitle => Lang.T("Dashboard");
    internal const string DashboardIcon = "\uF0E2";

    /// <summary>A built-in page's sidebar icon (a character, or "fan"/"network" for the drawn ones), or null for one not in the sidebar.</summary>
    public static string? IconOf(string key) => BuiltIn.FirstOrDefault(p => p.Key == key).Icon;

    /// <summary>Every built-in page with its name in the person's language ("games", "Juegos").</summary>
    public static IEnumerable<(string Key, string Name)> BuiltInNames() => BuiltIn.Select(p => (p.Key, Lang.T(p.Title)));

    /// <summary>
    /// The version each page came in, for the pages added after the first versions (the rest were always there). Someone
    /// who updates from before a page came meets it as new; a page added later is one more line here.
    /// </summary>
    private static readonly Dictionary<string, Version> AddedIn = new()
    {
        ["games"] = new(0, 29, 0),
        ["power"] = new(1, 18, 0),
        ["latency"] = new(1, 19, 0),
    };

    /// <summary>
    /// What a page is for, in one line short enough for an All pages tile at any window width (approved mock-up
    /// 2026-11-24: every tile one line, so the tiles in a row are the same height).
    /// </summary>
    public static string DescriptionOf(string key) => key switch
    {
        "home" => Lang.T("How your PC is doing today"),
        "reports" => Lang.T("Your days and weeks"),
        "apps" => Lang.T("Time spent in each app"),
        "games" => Lang.T("Every game you played"),
        "processes" => Lang.T("What's running right now"),
        "crashes" => Lang.T("Crashes and blue screens"),
        "timeline" => Lang.T("What changed on this PC"),
        "latency" => Lang.T("How long drivers held the CPU"),
        "temperatures" => Lang.T("Heat of each part"),
        "power" => Lang.T("What the parts draw"),
        "fans" => Lang.T("How fast each fan spins"),
        "memory" => Lang.T("What fills your memory"),
        "storage" => Lang.T("Drives, space and wear"),
        "network" => Lang.T("Internet used by each app"),
        "sensors" => Lang.T("Every reading this PC gives"),
        "widgets" => Lang.T("Readings on your desktop"),
        "overlay" => Lang.T("Readings over your games"),
        "taskbar" => Lang.T("Readings in the taskbar"),
        _ => Lang.T("Your own page"),
    };

    /// <summary>A new page's Home card, under its name: what the page shows, as a sentence (its own subtitle where it has one).</summary>
    public static string PitchOf(string key) => key switch
    {
        "latency" => Lang.T("How long drivers kept a CPU core busy, and how long apps waited to run."),
        _ => DescriptionOf(key) + ".",
    };

    private readonly SettingsModel _settings;
    private readonly Action<string> _go;
    private readonly List<NavEntry> _entries;
    private string? _selected;
    private bool _selecting;

    /// <param name="dashboards">The user's dashboards, each a page of the DASHBOARDS group until moved; followed as they come and go.</param>
    public SidebarViewModel(SettingsModel settings, Action<string> go, ObservableCollection<CustomPageViewModel>? dashboards = null)
    {
        _settings = settings;
        _go = go;
        MainGroup = new(MainSection, null, this);
        DashboardsGroup = new(Dashboards, Lang.T("Dashboards"), this);
        HardwareGroup = new(HardwareSection, Lang.T("Hardware"), this);
        OnScreenGroup = new(OnScreenSection, Lang.T("On screen"), this);
        Groups = [MainGroup, DashboardsGroup, HardwareGroup, OnScreenGroup];
        _entries = [.. BuiltIn.Select(p => new NavEntry(p.Key, Lang.T(p.Title), p.Icon, p.Group, this))];
        foreach (var page in dashboards ?? []) Add(page);
        if (dashboards is not null) dashboards.CollectionChanged += OnDashboardsChanged;
        _settings.Changed += NewDotChanged;
        MeetPages();
        Arrange();
        Update();
    }

    // ── New pages, All pages and its card (approved mock-up 2026-11-24) ──

    /// <summary>
    /// The first time this version reads the sidebar: every page the person's last version had is one they've met. A new
    /// install has met every page (each is new to them alike); someone who updated also gets the one card about All pages.
    /// </summary>
    private void MeetPages()
    {
        if (S.Known is not null) return;
        string? last = _settings.Current.WhatsNewSeen;
        bool updated = !S.AskPages && Version.TryParse(last, out var from);
        var known = BuiltIn.Where(p => !updated || !AddedIn.TryGetValue(p.Key, out var came) || came <= Version.Parse(last!)).Select(p => p.Key).ToList();
        _settings.Update(s => { s.Sidebar.Known = known; s.Sidebar.AllPagesCard = updated; });
        Core.Log.Write("sidebar", updated
            ? $"Pages met, updated from {last}: {known.Count} of {BuiltIn.Length}; new: {string.Join(", ", BuiltIn.Select(p => p.Key).Except(known))}"
            : $"Pages met on a new install: all {known.Count}");
    }

    /// <summary>A built-in page this person hasn't opened since an update brought it.</summary>
    internal bool IsNewPage(string key) => S.Known is { } known && !known.Contains(key) && BuiltIn.Any(p => p.Key == key);

    /// <summary>A page opened on a basics sidebar for the first time goes back to All pages once another page opens (no jump while it's open).</summary>
    private string? _backToAllPages;

    /// <summary>New pages shown in the sidebar, for "1 new" on the All pages row.</summary>
    public int NewCount => _entries.Count(e => e.IsNewPage && !IsHidden(e.Key));

    /// <summary>The All pages row: "1 new" while a new page waits to be opened, else how many pages there are.</summary>
    public string AllPagesNote => NewCount switch { 0 => _entries.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), 1 => Lang.T("1 new"), var n => Lang.T($"{n} new") };
    public bool AllPagesHasNew => NewCount > 0;

    /// <summary>All pages is the page on screen: its row is marked like a page.</summary>
    public bool AllPagesSelected
    {
        get => _selected == AllPagesKey;
        set { if (value && _selected != AllPagesKey) _go(AllPagesKey); }
    }

    public const string AllPagesKey = "allpages";

    /// <summary>Every page, in sidebar order: the All pages page lists these.</summary>
    public IEnumerable<NavEntry> Entries => _entries;

    /// <summary>
    /// The switch on an All pages tile, and "Show in sidebar": the person's own choice, which a basics sidebar keeps after
    /// a first visit and which takes a new page out of the sidebar at once (the user, 2026-11-24).
    /// </summary>
    public void SetShown(NavEntry entry, bool shown)
    {
        if (!entry.CanHide) return;
        if (_backToAllPages == entry.Key) _backToAllPages = null;
        if (!S.Chosen.Contains(entry.Key)) _settings.Update(s => s.Sidebar.Chosen.Add(entry.Key));
        SetHidden(entry, !shown);
    }

    // The Home card: a new page first, else the one about All pages.

    private NavEntry? CardPage => _entries.FirstOrDefault(e => e.IsNewPage && !IsHidden(e.Key) && !S.CardsLater.Contains(e.Key));

    public bool HasCard => CardPage is not null || S.AllPagesCard;
    public bool CardIsPage => CardPage is not null;
    public string CardIcon => CardPage?.Icon is { } icon ? icon : "";
    public string CardTitle => CardPage is { } p ? Lang.T($"{p.Title} is a new page") : Lang.T("All pages is above Settings");
    public string CardText => CardPage is { } p ? PitchOf(p.Key) : Lang.T("It lists every page, each with a switch for the sidebar. Your sidebar stays as you set it.");
    public string CardOpenText => CardPage is { } p ? Lang.T($"Open {p.Title}") : Lang.T("Open All pages");

    [RelayCommand]
    private void CardOpen() => _go(CardPage?.Key ?? AllPagesKey);

    /// <summary>Later on a new page's card: the card goes, the NEW tag stays until the page is opened. The All pages card just closes.</summary>
    [RelayCommand]
    private void CardLater()
    {
        // One card per update: putting off a new page's card retires the All pages one too.
        if (CardPage is { } p) _settings.Update(s => { if (!s.Sidebar.CardsLater.Contains(p.Key)) s.Sidebar.CardsLater.Add(p.Key); s.Sidebar.AllPagesCard = false; });
        else if (S.AllPagesCard) _settings.Update(s => s.Sidebar.AllPagesCard = false);
        CardChanged();
    }

    /// <summary>
    /// The card and the All pages row are told only when what they show changed: this runs on every page switch, and
    /// Home's card would otherwise read its words again each time.
    /// </summary>
    private void CardChanged()
    {
        string now = $"{CardPage?.Key}|{CardPage?.Title}|{S.AllPagesCard}|{NewCount}|{_entries.Count}";
        if (now == _cardShown) return;
        _cardShown = now;
        Notify(nameof(HasCard), nameof(CardIsPage), nameof(CardIcon), nameof(CardTitle), nameof(CardText), nameof(CardOpenText),
            nameof(NewCount), nameof(AllPagesNote), nameof(AllPagesHasNew));
    }
    private string? _cardShown;

    /// <summary>A group's "N more" opened or closed.</summary>
    internal void MoreToggled() => Update();

    // ── Moving pages in the sidebar itself, with Undo (a plain drag, nothing on screen for it: the user, 2026-11-24) ──

    private sealed record Layout(List<string> Order, Dictionary<string, string> Groups, List<string> Hidden, List<string> Chosen);
    private Layout? _undo;
    private System.Windows.Threading.DispatcherTimer? _undoTimer;

    /// <summary>"Moved Temperatures": shown under the pages for a few seconds after a drag, with Undo beside it.</summary>
    [ObservableProperty] private string _undoText = "";

    /// <summary>A page dropped in the sidebar: before <paramref name="before"/>, or at the end of <paramref name="group"/>. A page that wasn't shown is shown.</summary>
    public void Drop(NavEntry entry, SidebarGroup group, NavEntry? before)
    {
        var undo = new Layout([.. S.Order], new(S.Groups), [.. S.Hidden], [.. S.Chosen]);
        bool shown = false;
        if (IsHidden(entry.Key)) { SetShown(entry, true); shown = true; }
        int index = before is not null && group.Pages.Contains(before) ? group.Pages.IndexOf(before) : group.Pages.Count;
        if (!MoveTo(entry, group, index) && !shown) return;
        _undo = undo;
        UndoText = Lang.T($"Moved {entry.Title}");
        _undoTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _undoTimer.Tick -= UndoExpired;
        _undoTimer.Tick += UndoExpired;
        _undoTimer.Stop();
        _undoTimer.Start();
        Core.Log.Write("sidebar", $"Dragged {entry.Key} to {group.Key}{(before is null ? " (end)" : $" before {before.Key}")}");
    }

    private void UndoExpired(object? sender, EventArgs e) { _undoTimer?.Stop(); UndoText = ""; _undo = null; }

    [RelayCommand]
    private void Undo()
    {
        if (_undo is not { } was) return;
        _settings.Update(s => { s.Sidebar.Order = was.Order; s.Sidebar.Groups = was.Groups; s.Sidebar.Hidden = was.Hidden; s.Sidebar.Chosen = was.Chosen; });
        UndoExpired(null, EventArgs.Empty);
        foreach (var e in _entries) e.ShownChanged();
        Arrange();
        GroupsChanged();
        Update();
        Core.Log.Write("sidebar", "Undid the last drag");
    }

    // ── Dashboards ──

    private void Add(CustomPageViewModel page)
    {
        _entries.Add(new NavEntry(page.NavKey, DashboardTitle, DashboardIcon, Dashboards, this, page) { IsSelected = page.NavKey == _selected });
        page.PropertyChanged += OnDashboardChanged;
    }

    /// <summary>A dashboard was made or deleted: its entry comes or goes, and a deleted one leaves nothing behind in the saved layout.</summary>
    private void OnDashboardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var pages = ((IEnumerable<CustomPageViewModel>)sender!).ToList();
        foreach (var gone in _entries.Where(x => x.Dashboard is { } d && !pages.Contains(d)).ToList())
        {
            gone.Dashboard!.PropertyChanged -= OnDashboardChanged;
            _entries.Remove(gone);
            Forget(gone.Key);
        }
        _selecting = true; // a new entry is ticked if its page is the one on screen: that opens nothing
        foreach (var page in pages.Where(p => _entries.All(x => x.Dashboard != p))) Add(page);
        _selecting = false;
        Arrange();
        Update();
    }

    /// <summary>Renamed on its own page: the sidebar and the editor's name box follow.</summary>
    private void OnDashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CustomPageViewModel.Name)) _entries.FirstOrDefault(x => x.Dashboard == sender)?.NameChanged();
    }

    private void Forget(string key)
    {
        if (!S.Order.Contains(key) && !S.Hidden.Contains(key) && !S.Groups.ContainsKey(key)) return;
        _settings.Update(s =>
        {
            s.Sidebar.Order.Remove(key);
            s.Sidebar.Hidden.Remove(key);
            s.Sidebar.Groups.Remove(key);
        });
    }

    /// <summary>A page's entry, wherever it is (null for a page that has none, like Settings).</summary>
    public NavEntry? EntryOf(string key) => _entries.FirstOrDefault(e => e.Key == key);

    /// <summary>The groups from the top of the sidebar down.</summary>
    public IReadOnlyList<SidebarGroup> Groups { get; }
    public SidebarGroup MainGroup { get; }
    public SidebarGroup DashboardsGroup { get; }
    public SidebarGroup HardwareGroup { get; }
    public SidebarGroup OnScreenGroup { get; }

    public ObservableCollection<NavEntry> Main => MainGroup.Pages;
    public ObservableCollection<NavEntry> Hardware => HardwareGroup.Pages;
    public ObservableCollection<NavEntry> OnScreen => OnScreenGroup.Pages;

    private SidebarSettings S => _settings.Current.Sidebar;

    public bool DashboardsCollapsed => DashboardsGroup.Collapsed;
    public bool HardwareCollapsed => HardwareGroup.Collapsed;
    public bool OnScreenCollapsed => OnScreenGroup.Collapsed;

    /// <summary>Raised when what the dashboards section shows may have changed (folded or not).</summary>
    public event Action? Changed;

    // ── Pages shown, and icons only ──

    /// <summary>Only the basics were picked (the editor may have changed which since).</summary>
    public bool IsBasic => S.Pages == "basic";

    /// <summary>"basic", "full", or "" while never picked: the "Pages in the sidebar" choice in Settings.</summary>
    public string PagesMode
    {
        get => S.Pages ?? "";
        set => PickPages(value);
    }

    /// <summary>A new install asks which pages to show, once (the card over the window).</summary>
    public bool AskPages => S.AskPages;

    /// <summary>The card's two pictures: the same 16 pages as tiles, the basics lighting 6 of them, everything all (approved mock-up).</summary>
    public IReadOnlyList<SidebarTile> BasicTiles => [.. BuiltIn.Select(p => new SidebarTile(p.Icon, BasicPages.Contains(p.Key)))];
    public IReadOnlyList<SidebarTile> FullTiles => [.. BuiltIn.Select(p => new SidebarTile(p.Icon, true))];

    /// <summary>
    /// Shows only the basics, or every page: the pages left out are hidden as the editor would hide them (dashboards
    /// stay), and the question a new install gets is answered.
    /// </summary>
    [RelayCommand]
    private void PickPages(string? mode)
    {
        if (mode is not ("basic" or "full")) return;
        var hide = mode == "basic" ? _entries.Where(e => e.CanHide && e.Dashboard is null && !BasicPages.Contains(e.Key)).Select(e => e.Key).ToList() : [];
        _settings.Update(s =>
        {
            s.Sidebar.Pages = mode;
            s.Sidebar.AskPages = false;
            s.Sidebar.Hidden.RemoveAll(k => _entries.Any(e => e.Key == k && e.Dashboard is null));
            s.Sidebar.Hidden.AddRange(hide);
        });
        foreach (var e in _entries) e.ShownChanged();
        Notify(nameof(PagesMode), nameof(IsBasic), nameof(AskPages), nameof(ShowNewDashboard));
        GroupsChanged();
        Update();
    }

    /// <summary>"New dashboard" under DASHBOARDS: not with only the basics shown and no dashboard yet, or the group folded.</summary>
    public bool ShowNewDashboard => !DashboardsCollapsed && (!IsBasic || DashboardsGroup.Pages.Any(p => !IsHidden(p.Key)));

    /// <summary>Icons only, narrow; each page's name shows on hover.</summary>
    public bool IconsOnly
    {
        get => S.IconsOnly;
        set
        {
            if (S.IconsOnly == value) return;
            _settings.Update(s => s.Sidebar.IconsOnly = value);
            Notify(nameof(IconsOnly), nameof(IconsTip), nameof(HandleGlyph));
            Update();
        }
    }

    [RelayCommand] private void ToggleIcons() => IconsOnly = !IconsOnly;

    private void Notify(params string[] names) { foreach (var n in names) OnPropertyChanged(n); }

    /// <summary>The edge handle's hover: what pressing it does (Ctrl+B does the same).</summary>
    public string IconsTip => IconsOnly ? Lang.T("Expand the sidebar") : Lang.T("Collapse the sidebar");

    /// <summary>The edge handle's arrow: towards the page to open, back to fold.</summary>
    public string HandleGlyph => IconsOnly ? "\uE76C" : "\uE76B";


    /// <summary>The sidebar editor is open (the panel that slides in over Settings).</summary>
    [ObservableProperty] private bool _isEditing;

    [RelayCommand]
    private void Edit() => IsEditing = true;

    /// <summary>"14 of 17 pages shown", beside Sidebar in Settings and at the foot of the Customize pop-up.</summary>
    public string ShownSummary => Lang.T($"{_entries.Count(e => !IsHidden(e.Key))} of {_entries.Count} pages shown");

    /// <summary>The arrows on a row in the Customize pop-up: one place up or down, as the arrow keys on its grip.</summary>
    [RelayCommand] private void MoveUp(NavEntry? entry) { if (entry is not null) MoveBy(entry, -1); }
    [RelayCommand] private void MoveDown(NavEntry? entry) { if (entry is not null) MoveBy(entry, 1); }

    /// <summary>
    /// "Reset to the usual sidebar": every page shown, in its usual group and order, with its own name; every heading
    /// shown with its own name. Dashboards keep their names (those are theirs) and come back too.
    /// </summary>
    [RelayCommand]
    private void ResetSidebar()
    {
        _settings.Update(s =>
        {
            s.Sidebar.Order.Clear(); s.Sidebar.Hidden.Clear(); s.Sidebar.Groups.Clear(); s.Sidebar.Names.Clear();
            s.Sidebar.HeadingNames.Clear(); s.Sidebar.HiddenHeadings.Clear();
        });
        foreach (var e in _entries) { e.IsRenaming = false; e.ShownChanged(); e.NameChanged(); }
        foreach (var g in Groups) g.IsRenaming = false;
        Arrange();
        GroupsChanged();
        Update();
    }

    [RelayCommand]
    private void CloseEditor() => IsEditing = false;

    /// <summary>What Settings says beside "Sidebar" while something is left out ("· 1 page hidden, 1 heading hidden"); empty with nothing hidden.</summary>
    public string HiddenSummary
    {
        get
        {
            int pages = _entries.Count(e => IsHidden(e.Key)), headings = Groups.Count(g => g.HasHeading && !g.ShowHeading);
            var parts = new List<string>();
            if (pages > 0) parts.Add(pages == 1 ? Lang.T("1 page hidden") : Lang.T($"{pages} pages hidden"));
            if (headings > 0) parts.Add(headings == 1 ? Lang.T("1 heading hidden") : Lang.T($"{headings} headings hidden"));
            return parts.Count == 0 ? "" : "· " + string.Join(", ", parts);
        }
    }

    internal void Open(NavEntry entry)
    {
        if (!_selecting) _go(entry.Key);
    }

    /// <summary>The page shown changed: tick its entry.</summary>
    internal string? NewPage => _settings.Current.NewFeaturePage;
    internal string? NewText => _settings.Current.NewFeatureText;

    /// <summary>The settings changed: the new-feature dot follows (set when What's new closes, gone once its page opens).</summary>
    private void NewDotChanged()
    {
        foreach (var e in _entries) e.NewChanged();
        CardChanged();
    }

    public void Select(string page)
    {
        // The page with the dot opened: what's new there has been seen.
        if (page == NewPage) _settings.Update(s => { s.NewFeaturePage = null; s.NewFeatureText = null; });
        // Leaving a page a basics sidebar met for the first time: it goes back to All pages (and its group's "N more").
        if (_backToAllPages is { } back && back != page)
        {
            _backToAllPages = null;
            if (EntryOf(back) is { } leaving && IsBasic && !S.Chosen.Contains(back)) SetHidden(leaving, true);
        }
        // A new page opened: met. Shown pages keep it (everything sidebars); a basics one lets it go once it's left.
        if (IsNewPage(page))
        {
            _settings.Update(s => { if (s.Sidebar.Known is { } k && !k.Contains(page)) k.Add(page); s.Sidebar.CardsLater.Remove(page); });
            if (IsBasic && !S.Chosen.Contains(page) && !IsHidden(page)) _backToAllPages = page;
            if (EntryOf(page) is { } met) met.NewChanged();
            Core.Log.Write("sidebar", $"New page {page} opened for the first time{(_backToAllPages == page ? "; back to All pages once left (basics)" : "")}");
        }
        if (page == AllPagesKey && S.AllPagesCard) _settings.Update(s => s.Sidebar.AllPagesCard = false);
        _selected = page;
        _selecting = true;
        foreach (var e in _entries) e.IsSelected = e.Key == page;
        _selecting = false;
        OnPropertyChanged(nameof(AllPagesSelected));
        Update();
    }

    internal bool IsHidden(string key) => S.Hidden.Contains(key);

    internal void SetHidden(NavEntry entry, bool hidden)
    {
        if (!entry.CanHide || IsHidden(entry.Key) == hidden) return;
        _settings.Update(s =>
        {
            s.Sidebar.Hidden.Remove(entry.Key);
            if (hidden) s.Sidebar.Hidden.Add(entry.Key);
        });
        entry.ShownChanged();
        GroupsChanged();
        Update();
    }

    // ── Names ──

    internal string NameOf(string key) => S.Names.GetValueOrDefault(key, "");

    /// <summary>A page's name as typed; cleared, the page goes back to its own. A dashboard is renamed itself.</summary>
    internal void Rename(NavEntry entry, string? name)
    {
        name ??= "";
        if (entry.Dashboard is { } dashboard)
        {
            dashboard.Name = name; // its entry hears of it like a rename on the dashboard's own page
            return;
        }
        if (NameOf(entry.Key) == name) return;
        _settings.Update(s => SetName(s.Sidebar.Names, entry.Key, name));
        entry.NameChanged();
    }

    internal string HeadingNameOf(string section) => S.HeadingNames.GetValueOrDefault(section, "");

    internal void RenameHeading(SidebarGroup group, string? name)
    {
        name ??= "";
        if (!group.HasHeading || HeadingNameOf(group.Key) == name) return;
        _settings.Update(s => SetName(s.Sidebar.HeadingNames, group.Key, name));
        group.Changed();
    }

    /// <summary>Kept as typed, so a space in the middle of typing isn't taken away; trimmed where it's shown.</summary>
    private static void SetName(Dictionary<string, string> names, string key, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) names.Remove(key);
        else names[key] = name;
    }

    // ── Headings ──

    internal bool IsHeadingHidden(string section) => S.HiddenHeadings.Contains(section);

    internal bool IsFolded(string section) => S.Collapsed.Contains(section);

    internal void SetHeadingHidden(SidebarGroup group, bool hidden)
    {
        if (!group.HasHeading || IsHeadingHidden(group.Key) == hidden) return;
        _settings.Update(s =>
        {
            s.Sidebar.HiddenHeadings.Remove(group.Key);
            if (hidden) s.Sidebar.HiddenHeadings.Add(group.Key);
        });
        GroupsChanged();
        Update();
    }

    [RelayCommand]
    private void ToggleSection(string section)
    {
        _settings.Update(s =>
        {
            if (!s.Sidebar.Collapsed.Remove(section)) s.Sidebar.Collapsed.Add(section);
        });
        GroupsChanged();
        Update();
    }

    private void GroupsChanged()
    {
        foreach (var group in Groups) group.Changed();
        OnPropertyChanged(nameof(ShowNewDashboard));
        OnPropertyChanged(nameof(DashboardsCollapsed));
        OnPropertyChanged(nameof(HardwareCollapsed));
        OnPropertyChanged(nameof(OnScreenCollapsed));
    }

    // ── Moving pages ──

    private SidebarGroup? GroupOf(NavEntry entry) => Groups.FirstOrDefault(g => g.Pages.Contains(entry));

    /// <summary>
    /// The pages a few typed words name, for Ctrl+K: names first, then what a page is for; at most four, none for a long
    /// question (that's for the AI).
    /// </summary>
    public IReadOnlyList<NavEntry> Find(string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length < 2 || text.Length > 30 || text.Contains('\n')) return [];
        var byName = _entries.Where(e => e.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(e => e.Title.StartsWith(text, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1);
        var byUse = _entries.Where(e => e.Description.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        return [.. byName.Concat(byUse).Distinct().Take(4)];
    }

    /// <summary>The group a page is in now (for a drop in the sidebar).</summary>
    public SidebarGroup? GroupOfEntry(NavEntry entry) => GroupOf(entry);

    /// <summary>
    /// Puts a page at a place in any group: <paramref name="index"/> counts that group's pages as they are now, the
    /// moved one included. False when it's already there.
    /// </summary>
    public bool MoveTo(NavEntry entry, SidebarGroup to, int index)
    {
        if (GroupOf(entry) is not { } from) return false;
        int at = from.Pages.IndexOf(entry);
        if (from == to && index > at) index--; // its own place closes up first
        index = Math.Clamp(index, 0, to.Pages.Count - (from == to ? 1 : 0));
        if (from == to && index == at) return false;

        var pages = Groups.ToDictionary(g => g, g => g.Pages.ToList());
        pages[from].Remove(entry);
        pages[to].Insert(index, entry);
        var order = Groups.SelectMany(g => pages[g]).Select(e => e.Key).ToList();
        _settings.Update(s =>
        {
            s.Sidebar.Order = order;
            s.Sidebar.Groups.Remove(entry.Key);
            if (to.Key != entry.Home) s.Sidebar.Groups[entry.Key] = to.Key;
        });
        Arrange();
        Update();
        return true;
    }

    /// <summary>
    /// One place up or down (the arrow keys on a page's grip). At the edge of its group it crosses into the next one:
    /// its end going up, its start going down.
    /// </summary>
    public bool MoveBy(NavEntry entry, int by)
    {
        if (by == 0 || GroupOf(entry) is not { } from) return false;
        int at = from.Pages.IndexOf(entry);
        if (by < 0 ? at > 0 : at < from.Pages.Count - 1) return MoveTo(entry, from, by < 0 ? at - 1 : at + 2);
        int next = Groups.ToList().IndexOf(from) + Math.Sign(by);
        if (next < 0 || next >= Groups.Count) return false;
        return MoveTo(entry, Groups[next], by < 0 ? Groups[next].Pages.Count : 0);
    }

    /// <summary>Settings changed elsewhere (another window, the agent): read it again.</summary>
    public void Refresh()
    {
        Notify(nameof(PagesMode), nameof(IsBasic), nameof(AskPages), nameof(IconsOnly), nameof(IconsTip), nameof(HandleGlyph));
        Arrange();
        foreach (var e in _entries)
        {
            e.ShownChanged();
            e.NameChanged();
        }
        GroupsChanged();
        Update();
    }

    /// <summary>The group a page is in: where the user put it, or its own.</summary>
    private string GroupKeyOf(NavEntry entry) =>
        S.Groups.TryGetValue(entry.Key, out var moved) && Groups.Any(g => g.Key == moved) ? moved : entry.Home;

    /// <summary>
    /// Puts every page in its group, in the saved order; a page the saved order doesn't know (a new one, like Games in
    /// 0.29.0) goes right after the built-in page before it (Games under Apps), else at the end of its own group. Only
    /// what is out of place is touched, so reading the same settings again changes nothing.
    /// </summary>
    private void Arrange()
    {
        var order = S.Order;
        double Rank(NavEntry e)
        {
            if (order.IndexOf(e.Key) is var i and >= 0) return i;
            int b = Array.FindIndex(BuiltIn, x => x.Key == e.Key);
            if (AddedLater.Contains(e.Key) && b > 0 && order.IndexOf(BuiltIn[b - 1].Key) is var before and >= 0) return before + 0.5;
            return int.MaxValue;
        }
        var wanted = Groups.ToDictionary(g => g, g => _entries.Where(e => GroupKeyOf(e) == g.Key).OrderBy(Rank).ToList());
        foreach (var (group, pages) in wanted)
            foreach (var gone in group.Pages.Except(pages).ToList()) group.Pages.Remove(gone);
        foreach (var (group, pages) in wanted)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                int at = group.Pages.IndexOf(pages[i]);
                if (at < 0) group.Pages.Insert(i, pages[i]);
                else if (at != i) group.Pages.Move(at, i);
            }
        }
        foreach (var group in Groups) group.Changed();
    }

    private void Update()
    {
        foreach (var group in Groups)
        {
            // A shown page: in its place (a folded group keeps the page on screen and a new page in sight). A page that
            // isn't shown: only in the group's "N more", never among the shown ones (approved mock-up 2026-11-24).
            foreach (var e in group.Pages)
            {
                bool hidden = IsHidden(e.Key);
                e.IsVisible = !hidden && (!group.Collapsed || e.IsSelected || e.IsNewPage);
                e.InMore = hidden && (e.IsSelected || (group.MoreOpen && !group.Collapsed && !IconsOnly));
            }
            group.SyncMore(group.Pages.Where(e => IsHidden(e.Key)));
            group.SelectionChanged();
        }
        OnPropertyChanged(nameof(HiddenSummary));
        OnPropertyChanged(nameof(ShownSummary));
        CardChanged();
        Changed?.Invoke();
    }
}
