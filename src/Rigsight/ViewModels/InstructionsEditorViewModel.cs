using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// The box over the window where people change the instructions for Ollama or their own key (Settings › Ask RigSight › Edit
/// instructions). It opens with what is in use now; Reset puts RigSight's text back in the box, and only Save keeps it.
/// The text stays on this PC.
/// </summary>
public sealed partial class InstructionsEditorViewModel : ObservableObject
{
    private AiMode _mode = AiMode.OwnKey;
    private string _opened = "";

    /// <summary>After a save or reset, so the Settings row shows the new state.</summary>
    public event Action? Saved;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Count))] private string _text = "";

    public string Title => _mode == AiMode.Ollama ? Lang.T("Instructions for Ollama") : Lang.T("Instructions for your own key");

    public string Intro => _mode == AiMode.Ollama
        ? Lang.T("This is what the AI is told about how to work out and write an answer. Change anything you like. Your instructions are used only with Ollama; RigSight AI and your own key keep their own.")
        : Lang.T("This is what the AI is told about how to work out and write an answer. Change anything you like. Your instructions are used only with your own key; RigSight AI and Ollama keep their own.");

    public string Count => Lang.T($"{Text.Length:N0} characters");

    public void Open(AiMode mode)
    {
        _mode = mode;
        Text = _opened = AiInstructions.Current(mode);
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Intro));
        IsOpen = true;
    }

    [RelayCommand] private void Reset() => Text = AiInstructions.Default(_mode);

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
