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
    public string OutsideTitle => Outside.Any(p => p.Plan < 2) ? Lang.T($"{Outside.Count} more pages with a plan") : Lang.T($"{Outside.Count} more pages with Complete");
    public string OutsideList => string.Join(", ", Outside.Select(p => p.Label));

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
