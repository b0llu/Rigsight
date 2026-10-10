using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
        set => owner.SetHidden(this, !value);
    }

    internal void ShownChanged() => OnPropertyChanged(nameof(IsShown));

    internal void NameChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Title));
    }
}

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

    /// <summary>It has no page (every one was moved out, or there is no dashboard yet): the editor shows a box to drop one in.</summary>
    public bool ShowDropBox => Pages.Count == 0;

    internal void Changed()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(ShowHeading));
        OnPropertyChanged(nameof(Collapsed));
        OnPropertyChanged(nameof(ShowDropBox));
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

    /// <summary>Every built-in page, in its usual group and order. A new page is one more line here.</summary>
    private static readonly (string Key, string Title, string Icon, string Group)[] BuiltIn =
    [
        ("home", "Home", "", MainSection),
        ("reports", "Reports", "", MainSection),
        ("apps", "Apps", "", MainSection),
        ("processes", "Processes", "", MainSection),
        ("crashes", "Crashes", "", MainSection),
        ("timeline", "Timeline", "", MainSection),
        ("temperatures", "Temperatures", "", HardwareSection),
        ("fans", "Fans", "fan", HardwareSection),
        ("memory", "Memory", "", HardwareSection),
        ("storage", "Storage", "", HardwareSection),
        ("network", "Network", "network", HardwareSection),
        ("sensors", "All sensors", "", HardwareSection),
        ("widgets", "Widgets", "", OnScreenSection),
        ("overlay", "Overlay", "", OnScreenSection),
        ("taskbar", "Taskbar", "", OnScreenSection),
    ];

    /// <summary>What a dashboard is called while its name is empty (as it is saved then: see CustomPageViewModel.Save).</summary>
    private const string DashboardTitle = "Dashboard";
    private const string DashboardIcon = "\uF0E2";

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
        DashboardsGroup = new(Dashboards, "Dashboards", this);
        HardwareGroup = new(HardwareSection, "Hardware", this);
        OnScreenGroup = new(OnScreenSection, "On screen", this);
        Groups = [MainGroup, DashboardsGroup, HardwareGroup, OnScreenGroup];
        _entries = [.. BuiltIn.Select(p => new NavEntry(p.Key, p.Title, p.Icon, p.Group, this))];
        foreach (var page in dashboards ?? []) Add(page);
        if (dashboards is not null) dashboards.CollectionChanged += OnDashboardsChanged;
        Arrange();
        Update();
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

    /// <summary>The sidebar editor is open (the panel that slides in over Settings).</summary>
    [ObservableProperty] private bool _isEditing;

    [RelayCommand]
    private void Edit() => IsEditing = true;

    [RelayCommand]
    private void CloseEditor() => IsEditing = false;

    /// <summary>What Settings says beside "Sidebar" while something is left out ("· 1 page hidden, 1 heading hidden"); empty with nothing hidden.</summary>
    public string HiddenSummary
    {
        get
        {
            int pages = _entries.Count(e => IsHidden(e.Key)), headings = Groups.Count(g => g.HasHeading && !g.ShowHeading);
            var parts = new List<string>();
            if (pages > 0) parts.Add($"{pages} page{(pages == 1 ? "" : "s")} hidden");
            if (headings > 0) parts.Add($"{headings} heading{(headings == 1 ? "" : "s")} hidden");
            return parts.Count == 0 ? "" : "· " + string.Join(", ", parts);
        }
    }

    internal void Open(NavEntry entry)
    {
        if (!_selecting) _go(entry.Key);
    }

    /// <summary>The page shown changed: tick its entry.</summary>
    public void Select(string page)
    {
        _selected = page;
        _selecting = true;
        foreach (var e in _entries) e.IsSelected = e.Key == page;
        _selecting = false;
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
        OnPropertyChanged(nameof(DashboardsCollapsed));
        OnPropertyChanged(nameof(HardwareCollapsed));
        OnPropertyChanged(nameof(OnScreenCollapsed));
    }

    // ── Moving pages ──

    private SidebarGroup? GroupOf(NavEntry entry) => Groups.FirstOrDefault(g => g.Pages.Contains(entry));

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
    /// Puts every page in its group, in the saved order; pages the saved order doesn't know (new ones) keep their place
    /// at the end of their own group. Only what is out of place is touched, so reading the same settings again changes nothing.
    /// </summary>
    private void Arrange()
    {
        var order = S.Order;
        var wanted = Groups.ToDictionary(g => g, g => _entries.Where(e => GroupKeyOf(e) == g.Key)
            .OrderBy(e => order.IndexOf(e.Key) is var i and >= 0 ? i : int.MaxValue).ToList());
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
            foreach (var e in group.Pages) e.IsVisible = (!IsHidden(e.Key) && !group.Collapsed) || e.IsSelected;
        OnPropertyChanged(nameof(HiddenSummary));
        Changed?.Invoke();
    }
}
