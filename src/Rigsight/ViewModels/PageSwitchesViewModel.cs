using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>One page's tile under "What answers can read": its icon and name, pressed to stop or start sending it.</summary>
public sealed partial class PageTile(AiPage page, string glyph, bool on) : ObservableObject
{
    public AiPage Page { get; } = page;
    public string Name => Page.Label;
    public string Glyph { get; } = glyph;
    [ObservableProperty] private bool _on = on;
    public string Tip => Page.Description;
}

/// <summary>
/// Settings › AI's "What answers can read" (the mock-up of 2026-10-23): a tile for each page the plan opens, pressed to stop
/// RigSight sending it, and one line for the pages outside the plan, never a lock on each. Also shown in the Ask window's
/// first-question window. With the person's own key or Ollama no plan applies: every page has its tile.
/// </summary>
public sealed partial class PageSwitchesViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly Func<IReadOnlyCollection<string>?> _planPages;

    public PageSwitchesViewModel(SettingsModel settings, Func<IReadOnlyCollection<string>?> planPages)
    {
        _settings = settings;
        _planPages = planPages;
        Refresh();
    }

    public IReadOnlyList<PageTile> Tiles { get; private set; } = [];
    public IReadOnlyList<AiPage> Outside { get; private set; } = [];

    /// <summary>Opened from the line for pages outside the plan.</summary>
    public Action? OpenPlans { get; set; }

    public void Refresh()
    {
        var plan = _planPages();
        var a = _settings.Current.Ai;
        Tiles = [.. AiPages.All.Where(p => plan is null || plan.Contains(p.Id)).Select(p => new PageTile(p, Glyph(p.Id), a.Sends(p.Id)))];
        Outside = [.. AiPages.All.Where(p => plan is not null && !plan.Contains(p.Id))];
        OnPropertyChanged(string.Empty);
    }

    public int OnCount => Tiles.Count(t => t.On);
    public string OnText => Lang.T($"{OnCount} of {Tiles.Count} on");
    public bool SomeOff => OnCount < Tiles.Count;
    public bool HasOutside => Outside.Count > 0;
    public string OutsideTitle => Outside.Any(p => p.Plan < 2) ? Lang.T($"{Outside.Count} more with a plan") : Lang.T($"{Outside.Count} more with Complete");
    public string OutsideList => string.Join(", ", Outside.Select(p => p.Label));
    /// <summary>Over the switches when some pages are outside the plan: "IN YOUR PLAN", or "FREE" on Free.</summary>
    public string OpenHeading => Outside.Any(p => p.Plan == 1) ? Lang.T("FREE") : Lang.T("IN YOUR PLAN");

    /// <summary>
    /// The pages outside the plan, one line for each plan that opens them ("Crashes, Memory, Storage, Processes, Reports" ·
    /// In Essentials; "Games, Timeline, Apps and 6 more" · In Complete): the first-question window's draft of 2026-10-23.
    /// </summary>
    public IReadOnlyList<OutsideGroup> OutsideGroups => [.. Outside.GroupBy(p => p.Plan).OrderBy(g => g.Key).Select(g =>
    {
        var names = g.Select(p => p.Label).ToList();
        string shown = names.Count <= 5 ? string.Join(", ", names) : Lang.T($"{string.Join(", ", names.Take(3))} and {names.Count - 3} more");
        return new OutsideGroup(shown, string.Join(", ", names), g.Key >= 2 ? Lang.T("In Complete") : Lang.T("In Essentials"));
    })];

    [RelayCommand]
    private void Toggle(PageTile? tile)
    {
        if (tile is null) return;
        bool on = !tile.On;
        _settings.Update(s => s.Ai.SetSends(tile.Page.Id, on));
        tile.On = on;
        Log.Write("ai-settings", $"Answers {(on ? "may use" : "won't use")} the {tile.Page.Name} page");
        Changed();
    }

    [RelayCommand]
    private void AllOn()
    {
        _settings.Update(s => { foreach (var t in Tiles) s.Ai.SetSends(t.Page.Id, true); });
        foreach (var t in Tiles) t.On = true;
        Changed();
    }

    [RelayCommand] private void SeePlans() => OpenPlans?.Invoke();

    private void Changed()
    {
        OnPropertyChanged(nameof(OnCount));
        OnPropertyChanged(nameof(OnText));
        OnPropertyChanged(nameof(SomeOff));
    }

    /// <summary>The page's icon, the same as in the sidebar (drawn ones by their word, see NavIconConverter).</summary>
    public static string Glyph(string page) => SidebarViewModel.IconOf(page) ?? page switch
    {
        AiPages.Parts => "",
        AiPages.Rewind => "",
        _ => SidebarViewModel.DashboardIcon,
    };
}

/// <summary>A line of pages one plan opens, with its tag ("In Essentials"); <see cref="All"/> names every one (on hover).</summary>
public sealed record OutsideGroup(string Names, string All, string Tag);
