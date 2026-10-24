using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// The box over the window where people change the full instructions for Ollama or their own key (Settings › Ask RigSight ›
/// Edit the full instructions, approved mock-up 2026-10-24). It opens with what is in use now; a warning says their
/// text replaces RigSight's, and RigSight's own text is one tab away to compare (read only), so the box they type in
/// keeps the whole width. Only Save keeps it; "Go back to RigSight's" drops theirs. The text stays on this PC.
/// </summary>
public sealed partial class InstructionsEditorViewModel : ObservableObject
{
    private AiMode _mode = AiMode.OwnKey;
    private string _opened = "";

    /// <summary>After a save or reset, so the Settings row shows the new state.</summary>
    public event Action? Saved;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Count))] private string _text = "";

    /// <summary>The RigSight's tab is showing (read only); theirs stays as they left it.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowingYours))] private bool _showingRigSights;
    public bool ShowingYours => !ShowingRigSights;

    public string RigSightText => AiInstructions.Default(_mode);

    public string ModeName => _mode == AiMode.Ollama ? Lang.T("Ollama") : Lang.T("Your API key");

    public string Count => Lang.T($"{Text.Length:N0} characters");

    /// <summary>They have their own saved: "Go back to RigSight's" shows.</summary>
    public bool HasOwn => AiInstructions.Custom(_mode) is not null;

    public void Open(AiMode mode)
    {
        _mode = mode;
        Text = _opened = AiInstructions.Current(mode);
        ShowingRigSights = false;
        OnPropertyChanged(nameof(RigSightText));
        OnPropertyChanged(nameof(ModeName));
        OnPropertyChanged(nameof(HasOwn));
        IsOpen = true;
    }

    [RelayCommand] private void ShowYours() => ShowingRigSights = false;
    [RelayCommand] private void ShowRigSights() => ShowingRigSights = true;

    /// <summary>Their own text dropped: RigSight's is used again, at once.</summary>
    [RelayCommand]
    private void GoBack()
    {
        AiInstructions.Reset(_mode);
        IsOpen = false;
        Saved?.Invoke();
    }

    [RelayCommand] private void Cancel() => IsOpen = false;

    /// <summary>Esc or a click beside the box: closes it only while nothing is changed, so typing isn't lost to a stray click.</summary>
    public void Dismiss() { if (Text == _opened) IsOpen = false; }

    [RelayCommand]
    private void Save()
    {
        AiInstructions.Save(_mode, Text);
        IsOpen = false;
        Saved?.Invoke();
    }
}
