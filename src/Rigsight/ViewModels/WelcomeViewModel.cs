using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// The welcome a new install gets, once (approved mock-up "RigSight first-run journey", 2026-10-30): what RigSight is,
/// the pages for the sidebar (The basics picked to start), and Ask RigSight with its trial. Open while the new install's
/// question about the pages is unanswered (<see cref="SidebarViewModel.AskPages"/>); never after an update.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly SidebarViewModel _sidebar;
    private readonly SettingsModel _settings;
    private readonly Action _askFirst, _later;

    /// <param name="askFirst">"Ask your first question": the top bar's Ask box opens.</param>
    /// <param name="later">"Later": where asking lives is pointed out once.</param>
    public WelcomeViewModel(SidebarViewModel sidebar, SettingsModel settings, Action askFirst, Action later)
    {
        (_sidebar, _settings, _askFirst, _later) = (sidebar, settings, askFirst, later);
        sidebar.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SidebarViewModel.AskPages)) OnPropertyChanged(nameof(IsOpen)); };
    }

    public bool IsOpen => _sidebar.AskPages;

    /// <summary>1 what RigSight is, 2 the pages, 3 Ask RigSight (left out when the AI is off).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAbout), nameof(IsPages), nameof(IsAsk))]
    private int _step = 1;

    public bool IsAbout => Step == 1;
    public bool IsPages => Step == 2;
    public bool IsAsk => Step == 3;

    /// <summary>How many steps there are: the AI's only while it's on.</summary>
    public int Steps => HasAsk ? 3 : 2;
    public bool HasAsk => _settings.Current.Ai.Mode != AiMode.Off;

    /// <summary>The pages picked: The basics to start (the recommended one), Everything with a click on it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BasicPicked), nameof(FullPicked))]
    private string _picked = "basic";

    public bool BasicPicked => Picked == "basic";
    public bool FullPicked => Picked == "full";

    public IReadOnlyList<SidebarTile> BasicTiles => _sidebar.BasicTiles;
    public IReadOnlyList<SidebarTile> FullTiles => _sidebar.FullTiles;

    /// <summary>The trial, in the words the What's new card uses (2026-10-30, wording A).</summary>
    public string TrialTitle => Lang.T("7 days of Complete, free");
    public string TrialText => Lang.T("Ask about every page, with web search, up to 300 questions a day. Your 7 days start with your first question and end on their own. No card needed.");

    [RelayCommand] private void Pick(string? mode) { if (mode is "basic" or "full") Picked = mode; }

    /// <summary>Next: the pages after what RigSight is; after the pages, Ask, or done when the AI is off.</summary>
    [RelayCommand]
    private void Next()
    {
        if (Step == 1) Step = 2;
        else if (Step == 2 && HasAsk) Step = 3;
        else Finish(ask: false);
    }

    [RelayCommand] private void Back() { if (Step > 1) Step--; }

    /// <summary>Skip on the first step: straight to the pages, which still have to be picked (as before the welcome).</summary>
    [RelayCommand] private void Skip() => Step = 2;

    [RelayCommand] private void AskFirst() => Finish(ask: true);
    [RelayCommand] private void Later() => Finish(ask: false);

    private void Finish(bool ask)
    {
        Log.Write("welcome", $"Done: {Picked} pages, {(ask ? "asking a first question" : "asking later")}");
        _sidebar.PickPagesCommand.Execute(Picked);
        if (!HasAsk) return;
        if (ask) _askFirst(); else _later();
    }
}
