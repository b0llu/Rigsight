using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rigsight.ViewModels;

/// <summary>What was answered to "End … ?".</summary>
public enum EndAnswer { Cancel, End, Restart }

/// <summary>One part of Windows among several things about to be ended, and what ending it does.</summary>
public sealed record EndQuestionLine(string Name, string Effect);

/// <summary>
/// The question before a part of Windows is ended from the Processes page: what will happen, in a sentence (several
/// picked: a line for each part of Windows among them).
/// </summary>
public sealed partial class EndQuestion : ObservableObject
{
    public string Title { get; init; } = "";

    /// <summary>What ending it does (one thing asked about).</summary>
    public string? Body { get; init; }

    /// <summary>Several picked: what is said above the parts of Windows among them, and each of those.</summary>
    public string? Intro { get; init; }
    public IReadOnlyList<EndQuestionLine> Lines { get; init; } = [];

    /// <summary>Windows can't run without it: "End anyway" stays off until the box is ticked.</summary>
    public bool NeedsTick { get; init; }

    /// <summary>Windows Explorer alone: it can be restarted instead, and that is the button in front.</summary>
    public bool CanRestart { get; init; }
    public bool CancelIsDefault => !CanRestart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnd))]
    private bool _ticked;

    public bool CanEnd => !NeedsTick || Ticked;
}

/// <summary>The question as a box over the window (see MainWindow.xaml); the answer comes back to whoever asked.</summary>
public sealed partial class EndQuestionViewModel : ObservableObject
{
    private TaskCompletionSource<EndAnswer>? _answer;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private EndQuestion? _question;

    public Task<EndAnswer> AskAsync(EndQuestion question)
    {
        _answer?.TrySetResult(EndAnswer.Cancel);
        _answer = new TaskCompletionSource<EndAnswer>();
        Question = question;
        IsOpen = true;
        return _answer.Task;
    }

    [RelayCommand]
    private void Answer(string answer)
    {
        var given = Enum.TryParse<EndAnswer>(answer, out var parsed) ? parsed : EndAnswer.Cancel;
        if (given == EndAnswer.End && Question is { CanEnd: false }) return;
        IsOpen = false;
        Question = null;
        _answer?.TrySetResult(given);
        _answer = null;
    }

    /// <summary>Esc, or a click beside the box.</summary>
    public void Cancel() => Answer(nameof(EndAnswer.Cancel));
}
