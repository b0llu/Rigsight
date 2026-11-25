using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;

namespace Rigsight.ViewModels;

/// <summary>
/// One page on All pages: its icon and name, a NEW tag while an update's page waits to be opened, one line under it (its
/// figure right now where it has one, else what it's for) and the switch that shows it in the sidebar.
/// </summary>
public sealed partial class AllPageTile(NavEntry entry, SidebarViewModel sidebar, Action<string> open) : ObservableObject
{
    public NavEntry Entry { get; } = entry;
    public string Key => Entry.Key;
    public string Icon => Entry.Icon;
    public string Name => Entry.Title;
    public bool IsNewPage => Entry.IsNewPage;

    /// <summary>Home is always in the sidebar: its tile has no switch.</summary>
    public bool CanHide => Entry.CanHide;

    /// <summary>The line under the name: a live figure ("CPU 50°, GPU 47°") or what the page is for.</summary>
    [ObservableProperty] private string _line = entry.Description;

    /// <summary>The line is a figure right now (brighter than a description).</summary>
    [ObservableProperty] private bool _isLive;

    /// <summary>The switch: shown in the sidebar, under its heading. Off takes a new page out of the sidebar at once.</summary>
    public bool IsShown
    {
        get => !sidebar.IsHidden(Key);
        set { if (value == IsShown) return; sidebar.SetShown(Entry, value); OnPropertyChanged(); }
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsShown));
        OnPropertyChanged(nameof(IsNewPage));
        OnPropertyChanged(nameof(Name));
    }

    [RelayCommand]
    private void Open() => open(Key);
}

/// <summary>A heading of All pages ("OVERVIEW", "HARDWARE", or "NEW" first while a new page waits) and its tiles.</summary>
public sealed record PageSection(string Heading, IReadOnlyList<AllPageTile> Tiles, bool IsNew = false);

/// <summary>
/// All pages (approved mock-up 2026-11-24): every page, under the sidebar's own headings and in its order, new pages first,
/// with a find box. It's fixed above Settings in the sidebar, so the sidebar never has to hold every page.
/// </summary>
public sealed partial class AllPagesViewModel : ObservableObject
{
    private readonly SidebarViewModel _sidebar;
    private readonly LiveData _live;
    private readonly Action<string> _open;
    private readonly Dictionary<string, AllPageTile> _tiles = [];

    public AllPagesViewModel(SidebarViewModel sidebar, LiveData live, Action<string> open)
    {
        _sidebar = sidebar;
        _live = live;
        _open = open;
        _sidebar.Changed += SidebarChanged;
        Rebuild();
    }

    /// <summary>
    /// The sidebar changes on every page switch: the page is built again only while it's on screen (and once it's shown
    /// again after), so switching pages costs nothing here. Gone pages lose their tiles at once all the same.
    /// </summary>
    private void SidebarChanged()
    {
        _dirty = true;
        if (_isShown) Rebuild();
        else DropGone();
    }

    private bool _dirty, _isShown;

    /// <summary>All pages is the page on screen (set by the window).</summary>
    public bool IsShown
    {
        get => _isShown;
        set { _isShown = value; if (value && _dirty) Rebuild(); }
    }

    /// <summary>A page that's gone (a deleted dashboard) leaves no tile behind: the tile would keep the dashboard in memory.</summary>
    private void DropGone()
    {
        if (_tiles.Count == 0) return;
        var live = _sidebar.Entries.ToHashSet();
        foreach (var gone in _tiles.Where(t => !live.Contains(t.Value.Entry)).Select(t => t.Key).ToList()) _tiles.Remove(gone);
    }

    public ObservableCollection<PageSection> Sections { get; } = [];

    /// <summary>The find box: a page's name or what it's for.</summary>
    [ObservableProperty] private string _filter = "";
    partial void OnFilterChanged(string value) => Rebuild();

    /// <summary>Nothing matches what's typed.</summary>
    public bool NothingFound => Sections.Count == 0;

    private AllPageTile TileOf(NavEntry entry)
    {
        if (!_tiles.TryGetValue(entry.Key, out var tile) || tile.Entry != entry) _tiles[entry.Key] = tile = new AllPageTile(entry, _sidebar, _open);
        return tile;
    }

    /// <summary>The sections again: after a switch, a move, a new page opened, or the find box.</summary>
    public void Rebuild()
    {
        string find = Filter.Trim();
        bool Matches(NavEntry e) => find.Length == 0
            || e.Title.Contains(find, StringComparison.CurrentCultureIgnoreCase)
            || e.Description.Contains(find, StringComparison.CurrentCultureIgnoreCase);

        _dirty = false;
        DropGone();

        var sections = new List<PageSection>();
        var fresh = _sidebar.Groups.SelectMany(g => g.Pages).Where(e => e.IsNewPage && Matches(e)).Select(TileOf).ToList();
        if (fresh.Count > 0) sections.Add(new PageSection(Lang.T("NEW"), fresh, IsNew: true));
        foreach (var group in _sidebar.Groups)
        {
            var tiles = group.Pages.Where(Matches).Select(TileOf).ToList();
            if (tiles.Count == 0) continue;
            sections.Add(new PageSection(group.HasHeading ? group.Heading : Lang.T("OVERVIEW"), tiles));
        }
        foreach (var tile in _tiles.Values) tile.Refresh();

        // Only what changed is replaced, so the page doesn't flicker or lose its scroll on a switch.
        bool same = sections.Count == Sections.Count && sections.Zip(Sections).All(p => p.First.Heading == p.Second.Heading && p.First.Tiles.SequenceEqual(p.Second.Tiles));
        if (!same)
        {
            Sections.Clear();
            foreach (var s in sections) Sections.Add(s);
            OnPropertyChanged(nameof(NothingFound));
        }
        RefreshFigures();
    }

    /// <summary>The live lines, each tick while the page is open: the pages whose figure says more than their description.</summary>
    public void RefreshFigures()
    {
        foreach (var tile in _tiles.Values)
        {
            string? figure = FigureOf(tile.Key);
            tile.IsLive = figure is not null;
            tile.Line = figure ?? tile.Entry.Description;
        }
    }

    private string? FigureOf(string key)
    {
        switch (key)
        {
            case "temperatures": return Pair(_live.CpuTemp, _live.GpuTemp);
            case "power": return Pair(_live.CpuPower, _live.GpuPower);
            case "fans":
            {
                var spinning = _live.Fans.Where(f => f.Value is > 0).ToList();
                if (spinning.Count == 0) return null;
                string fastest = spinning.Max(f => f.Value!.Value).ToString("N0", CultureInfo.CurrentCulture);
                return spinning.Count == 1 ? Lang.T($"1 spinning, at {fastest} rpm") : Lang.T($"{spinning.Count} spinning, up to {fastest} rpm");
            }
            case "memory": return string.IsNullOrEmpty(_live.RamText) ? null : Lang.T($"{_live.RamText} in use");
            case "storage": return _live.Drives.Count switch { 0 => null, 1 => Lang.T("1 drive"), var n => Lang.T($"{n} drives") };
            default: return null;
        }
    }

    /// <summary>
    /// "CPU 50°, GPU 47°": both parts when both read, else the one that does. A zero isn't a reading here (a CPU read
    /// without admin rights gives 0 W): that part is left out rather than shown as nothing drawn.
    /// </summary>
    private static string? Pair(Models.SensorItem? cpu, Models.SensorItem? gpu)
    {
        bool hasCpu = cpu?.Value is > 0, hasGpu = gpu?.Value is > 0;
        if (hasCpu && hasGpu) return Lang.T($"CPU {cpu!.ShortValue}, GPU {gpu!.ShortValue}");
        if (hasCpu) return Lang.T($"CPU {cpu!.ShortValue}");
        if (hasGpu) return Lang.T($"GPU {gpu!.ShortValue}");
        return null;
    }
}
