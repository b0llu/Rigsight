using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A page in the sidebar.</summary>
/// <param name="icon">An icon-font character, or "fan" for the drawn fan (see <see cref="Converters.NavIconConverter"/>).</param>
public sealed partial class NavEntry(string key, string title, string icon, SidebarViewModel owner) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Icon { get; } = icon;

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

    /// <summary>The "Show" switch in Settings.</summary>
    public bool IsShown
    {
        get => !owner.IsHidden(Key);
        set => owner.SetHidden(this, !value);
    }

    internal void ShownChanged() => OnPropertyChanged(nameof(IsShown));
}

/// <summary>
/// The sidebar's pages in three groups: your history (no heading), HARDWARE and ON SCREEN (the readouts Rigsight puts
/// outside its window: widgets, the game overlay, taskbar readings). Settings reorders pages within their group and
/// hides them; DASHBOARDS, HARDWARE and ON SCREEN fold away with a click on their heading.
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    public const string Dashboards = "dashboards", HardwareSection = "hardware", OnScreenSection = "onscreen";

    private readonly SettingsModel _settings;
    private readonly Action<string> _go;
    private bool _selecting;

    public SidebarViewModel(SettingsModel settings, Action<string> go)
    {
        _settings = settings;
        _go = go;
        NavEntry E(string key, string title, string icon) => new(key, title, icon, this);
        Main = [E("home", "Home", "\uE80F"), E("reports", "Reports", "\uE9F9"), E("apps", "Apps", "\uECA5"), E("crashes", "Crashes", "\uE7BA")];
        Hardware = [E("temperatures", "Temperatures", "\uE9CA"), E("fans", "Fans", "fan"), E("memory", "Memory", "\uE964"),
            E("storage", "Storage", "\uEDA2"), E("network", "Network", "network"), E("sensors", "All sensors", "\uE9D9")];
        OnScreen = [E("widgets", "Widgets", "\uF246"), E("overlay", "Overlay", "\uE7FC"), E("taskbar", "Taskbar", "\uE75B")];
        foreach (var group in Groups) ApplyOrder(group);
        Update();
    }

    public ObservableCollection<NavEntry> Main { get; }
    public ObservableCollection<NavEntry> Hardware { get; }
    public ObservableCollection<NavEntry> OnScreen { get; }

    private IEnumerable<ObservableCollection<NavEntry>> Groups => [Main, Hardware, OnScreen];
    private IEnumerable<NavEntry> All => Main.Concat(Hardware).Concat(OnScreen);

    private SidebarSettings S => _settings.Current.Sidebar;

    public bool DashboardsCollapsed => S.Collapsed.Contains(Dashboards);
    public bool HardwareCollapsed => S.Collapsed.Contains(HardwareSection);
    public bool OnScreenCollapsed => S.Collapsed.Contains(OnScreenSection);

    /// <summary>Raised when what the dashboards section shows may have changed (folded or not).</summary>
    public event Action? Changed;

    internal void Open(NavEntry entry)
    {
        if (!_selecting) _go(entry.Key);
    }

    /// <summary>The page shown changed: tick its entry.</summary>
    public void Select(string page)
    {
        _selecting = true;
        foreach (var e in All) e.IsSelected = e.Key == page;
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

    [RelayCommand]
    private void ToggleSection(string section)
    {
        _settings.Update(s =>
        {
            if (!s.Sidebar.Collapsed.Remove(section)) s.Sidebar.Collapsed.Add(section);
        });
        CollapsedChanged();
        Update();
    }

    private void CollapsedChanged()
    {
        OnPropertyChanged(nameof(DashboardsCollapsed));
        OnPropertyChanged(nameof(HardwareCollapsed));
        OnPropertyChanged(nameof(OnScreenCollapsed));
    }

    [RelayCommand]
    private void MoveUp(NavEntry entry) => Move(entry, -1);

    [RelayCommand]
    private void MoveDown(NavEntry entry) => Move(entry, +1);

    /// <summary>One place up or down within its own group.</summary>
    private void Move(NavEntry entry, int by)
    {
        var list = Groups.FirstOrDefault(g => g.Contains(entry));
        if (list is null) return;
        int from = list.IndexOf(entry), to = from + by;
        if (to < 0 || to >= list.Count) return;
        list.Move(from, to);
        var order = All.Select(e => e.Key).ToList();
        _settings.Update(s => s.Sidebar.Order = order);
    }

    /// <summary>Settings changed elsewhere (another window, the agent): read it again.</summary>
    public void Refresh()
    {
        foreach (var group in Groups) ApplyOrder(group);
        foreach (var e in All) e.ShownChanged();
        CollapsedChanged();
        Update();
    }

    /// <summary>Puts a group in the saved order; pages the saved order doesn't know (new ones) keep their place at the end.</summary>
    private void ApplyOrder(ObservableCollection<NavEntry> list)
    {
        var order = S.Order;
        var sorted = list.OrderBy(e => order.IndexOf(e.Key) is var i and >= 0 ? i : int.MaxValue).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int at = list.IndexOf(sorted[i]);
            if (at != i) list.Move(at, i);
        }
    }

    private void Update()
    {
        foreach (var e in Main) e.IsVisible = !IsHidden(e.Key) || e.IsSelected;
        foreach (var e in Hardware) e.IsVisible = (!IsHidden(e.Key) && !HardwareCollapsed) || e.IsSelected;
        foreach (var e in OnScreen) e.IsVisible = (!IsHidden(e.Key) && !OnScreenCollapsed) || e.IsSelected;
        Changed?.Invoke();
    }
}
