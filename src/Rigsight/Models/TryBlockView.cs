using System.Windows;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Advice;
using Rigsight.Core.Stability;
using Rigsight.Services;

namespace Rigsight.Models;

/// <summary>
/// One finding's What to try, ready to draw (approved mock-up 2026-11-26): its steps, what RigSight saw since one was
/// tried, "Ask about this" while the AI is on, and "I know about this" where the finding stands behind a PC health line.
/// </summary>
public sealed partial class TryBlockView : ObservableObject
{
    public TryBlock Block { get; }
    public List<TryStepView> Steps { get; }

    /// <param name="since">For a finding with times of its own (a crash): how many times it happened after a day, for the line at the top.</param>
    public TryBlockView(TryBlock block, Func<DateTime, int>? since = null)
    {
        Block = block;
        Steps = [.. block.Steps.Select((s, i) => new TryStepView(this, s, i == 0))];
        if (since is not null && TryActions.FirstTried(block.Key) is { } first) SeenText = WhatToTry.Since(first, since(first));
        _hidden = block.HomeKey is { } key && TryActions.IsHidden(key);
        if (block.HomeKey is not null) TryActions.Watch(this);
    }

    /// <summary>Hidden or shown again elsewhere (Home's Show again, the same finding on another page): the link follows.</summary>
    internal void Refresh() => Hidden = Block.HomeKey is { } key && TryActions.IsHidden(key);

    /// <summary>"Not seen since you tried a step on 26 Nov": only what RigSight saw, never "Fixed".</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSeen))] private string _seenText = "";
    public bool HasSeen => SeenText.Length > 0;

    /// <summary>The finding and its steps, for the AI (the link hides itself while the AI is off).</summary>
    public AiQuestion Ask => new(Lang.T($"{Block.Subject} What else could I try? So far the steps are: {string.Join(" ", Block.Steps.Select(s => s.Text))}"), Block.AskPage);

    public bool CanHide => Block.HomeKey is not null;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HideText))] private bool _hidden;
    /// <summary>"I know about this", or once pressed, the way back.</summary>
    public string HideText => Hidden ? Lang.T("Show it on Home again") : Lang.T("I know about this");

    [RelayCommand]
    private void ToggleHide()
    {
        if (Block.HomeKey is not { } key) return;
        bool hide = !Hidden;
        if (hide) TryActions.Hide(key); else TryActions.ShowAgain(key);
        Hidden = hide;
    }
}

/// <summary>One step: its words, its tag, its button and the question before a change, and "Tried it".</summary>
public sealed partial class TryStepView : ObservableObject
{
    private readonly TryBlockView _block;
    public TryStep Step { get; }
    public bool IsFirst { get; }

    public TryStepView(TryBlockView block, TryStep step, bool first)
    {
        (_block, Step, IsFirst) = (block, step, first);
        if (TryActions.TriedOn(block.Block.Key, step.Id) is { } day) _triedOn = day;
        if (step.Do == TryDo.MemoryTest)
        {
            if (!TryActions.AgentIsAdmin() && TryActions.StandardAccount()) Note = Lang.T("Needs an administrator. Windows asks for one when you press it.");
            if (_triedOn is { } since) _ = ReadMemoryResult(since);
        }
    }

    public string Text => Step.Text;
    public string? Why => Step.Why;
    public bool HasWhy => !string.IsNullOrEmpty(Step.Why);
    public string FirstLabel => IsFirst ? Lang.T("TRY FIRST") : "";

    public bool HasTag => Step.Tag != TryTag.None;
    public string TagIcon => Step.Tag switch { TryTag.Restart => "", TryTag.Bios => "", TryTag.PcOff => "", _ => "" };
    public string TagText => Step.Tag switch
    {
        TryTag.Windows => Lang.T("Opens Windows"),
        TryTag.Steam => Lang.T("Opens Steam"),
        TryTag.Restart => Lang.T("Restarts your PC"),
        TryTag.Bios => Lang.T("In the BIOS"),
        TryTag.PcOff => Lang.T("With the PC off"),
        _ => "",
    };

    public string ButtonText => Step.Do == TryDo.CloseApp && Step.Name is { } app ? Lang.T($"Close {app}") : Lang.T(Step.Button ?? "");
    public bool HasButton => Step.Do != TryDo.None && Step.Button is not null && DoneText.Length == 0;

    /// <summary>What pressing it did ("Opened in Windows", "Set for your next start"), level with "Tried it", in place of the button.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasButton), nameof(HasDone))] private string _doneText = "";
    public bool HasDone => DoneText.Length > 0;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasButton))] private bool _busy;

    public string? Note { get; }
    public bool HasNote => Note is not null;

    // ── Tried it ──
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsTried), nameof(TriedText), nameof(ShowCantSee))] private DateTime? _triedOn;
    public bool IsTried => TriedOn is not null;
    public string TriedText => TriedOn is { } d ? WhatToTry.TriedText(d) : "";
    public bool ShowCantSee => IsTried && Step.CantSee;

    [RelayCommand]
    private void Tried()
    {
        if (IsTried) return;
        TryActions.MarkTried(_block.Block.Key, Step.Id);
        TriedOn = DateTime.Now.Date;
    }

    // ── The question before a change, over the page beside the button ──
    [ObservableProperty] private bool _asking;
    [ObservableProperty] private string _askTitle = "";
    [ObservableProperty] private string _askText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAskExtra))] private string _askExtra = "";
    public bool HasAskExtra => AskExtra.Length > 0;
    [ObservableProperty] private string _goText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAlt))] private string _altText = "";
    public bool HasAlt => AltText.Length > 0;

    [RelayCommand]
    private async Task Act()
    {
        if (Busy) return;
        var s = Step;
        Log.Write("try", $"Pressed {s.Do} on {_block.Block.Key}|{s.Id} ({s.Target})");
        switch (s.Do)
        {
            case TryDo.Page:
                TryActions.OpenPage?.Invoke(s.Target, null);
                break;
            case TryDo.Windows when Core.Ai.AiActions.Screen(s.Target) is { } screen:
                DoneText = Open(screen.Uri) ? Lang.T("Opened in Windows") : Lang.T("Windows couldn't open it on this PC.");
                break;
            case TryDo.VerifyGame:
                DoneText = Open("steam://validate/" + s.Target) ? Lang.T("Opened in Steam") : Lang.T("Steam couldn't be opened on this PC.");
                break;
            case TryDo.CloseApp:
                string app = s.Name ?? s.Target;
                Ask(Lang.T($"Close {app}?"), Lang.T($"RigSight asks its windows to close, the way the X does. If anything in it isn't saved, {app} asks first."), "", Lang.T($"Close {app}"));
                break;
            case TryDo.EmptyBin:
                Busy = true;
                var (size, items) = await Task.Run(() => StorageScanner.RecycleBin());
                Busy = false;
                if (items == 0) { DoneText = Lang.T("The Recycle Bin is already empty"); break; }
                _binSize = size;
                Ask(items == 1 ? Lang.T("Delete 1 item permanently?") : Lang.T($"Delete {items:N0} items permanently?"),
                    Lang.T($"{Units.Bytes(size)} in the Recycle Bin.") + " " + Lang.T("Once the Recycle Bin is emptied, they can't be restored."), "", Lang.T("Empty Recycle Bin"));
                break;
            case TryDo.MemoryTest when !TryActions.AgentIsAdmin():
                // Without RigSight's agent as admin, Windows' own window asks, with its own two choices.
                DoneText = Open("mdsched.exe") ? Lang.T("Opened in Windows") : Lang.T("Windows couldn't open it on this PC.");
                break;
            case TryDo.MemoryTest:
                Busy = true;
                var (apps, battery) = await Task.Run(() => (TryActions.OpenApps(), TryActions.OnBattery()));
                Busy = false;
                string extra = string.Join(" ", new[]
                {
                    Unsaved(apps),
                    battery ? Lang.T("The PC is on battery, so plug it in first.") : null,
                }.Where(x => x is not null));
                Ask(Lang.T("Restart now and test the memory?"), Lang.T("Windows restarts, tests the memory for about 15 minutes, then starts as usual."),
                    extra.Length > 0 ? extra : Lang.T("Save your work first."), Lang.T("Restart and test"), Lang.T("At next start"));
                break;
        }
    }

    private long _binSize;

    /// <summary>"Save your work first, since Google Chrome is open." / "…, since Google Chrome and Discord are open." / "…, since Chrome, Discord and 3 more apps are open."</summary>
    private static string? Unsaved(List<string> apps) => apps.Count switch
    {
        0 => null,
        1 => Lang.T($"Save your work first, since {apps[0]} is open."),
        2 or 3 => Lang.T($"Save your work first, since {Wording.JoinAnd(apps)} are open."),
        _ => Lang.T($"Save your work first, since {apps[0]}, {apps[1]} and {apps.Count - 2} more apps are open."),
    };

    private void Ask(string title, string text, string extra, string go, string alt = "")
    {
        (AskTitle, AskText, AskExtra, GoText, AltText) = (title, text, extra, go, alt);
        Asking = true;
    }

    [RelayCommand] private void Cancel() => Asking = false;

    /// <summary>The question's main button: the change itself.</summary>
    [RelayCommand]
    private async Task Go()
    {
        Asking = false;
        var s = Step;
        switch (s.Do)
        {
            case TryDo.CloseApp:
                Busy = true;
                string app = s.Name ?? s.Target;
                var outcome = await Task.Run(() => TryActions.Closer.Close(s.Target));
                Busy = false;
                Log.Write("try", $"Close {s.Target} from What to try: {outcome}");
                DoneText = outcome switch
                {
                    CloseOutcome.Closed or CloseOutcome.Ended => Lang.T("Closed just now"),
                    CloseOutcome.Gone => Lang.T($"{app} had already closed."),
                    CloseOutcome.StillOpen => Lang.T($"{app} is still open. It may be asking you to save."),
                    CloseOutcome.NeedsAdmin => Lang.T($"{app} runs with admin rights, so it wasn't closed from here."),
                    _ => Lang.T($"Windows wouldn't close {app}."),
                };
                if (outcome is CloseOutcome.Closed or CloseOutcome.Ended) TryActions.SendAgent?.Invoke(TaskEnded.Command, TaskEnded.Format(app, s.Target, 0));
                break;
            case TryDo.EmptyBin:
                Busy = true;
                var owner = Application.Current?.MainWindow is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
                bool emptied;
                try { emptied = await TryActions.OnOwnThread(() => StorageScanner.EmptyRecycleBinQuiet(owner)); }
                catch (Exception ex) { Log.Error("try: emptying the Recycle Bin", ex); emptied = false; }
                var left = await Task.Run(() => StorageScanner.RecycleBin());
                Busy = false;
                Log.Write("try", $"Emptied the Recycle Bin from What to try: expected {Units.Bytes(_binSize)} gone; Windows {(emptied ? "said done" : "said it failed")}, {left.Items} items, {Units.Bytes(left.Size)} left");
                DoneText = !emptied && left.Size > 0 ? Lang.T("Windows didn't empty it.") : Lang.T($"Recycle Bin emptied, {Units.Bytes(Math.Max(0, _binSize - left.Size))} freed");
                break;
            case TryDo.MemoryTest:
                MemoryTest(now: true);
                break;
        }
    }

    /// <summary>The question's other choice: the memory test at the next start.</summary>
    [RelayCommand]
    private void Alt()
    {
        Asking = false;
        if (Step.Do == TryDo.MemoryTest) MemoryTest(now: false);
    }

    private void MemoryTest(bool now)
    {
        // Pressed is tried: the result Windows writes after the restart is read against this day.
        if (!IsTried) Tried();
        TryActions.SendAgent?.Invoke("memory-test", now ? "now" : "next");
        DoneText = now ? Lang.T("Restarting now") : Lang.T("Set for your next start");
    }

    private async Task ReadMemoryResult(DateTime since)
    {
        if (await Task.Run(() => TryActions.MemoryResult(since)) is not { } result) return;
        DoneText = result.Errors ? Lang.T($"Windows found memory errors on {result.At:d MMM}") : Lang.T($"Windows found no memory errors on {result.At:d MMM}");
    }

    private static bool Open(string target)
    {
        try { TryActions.Launch(target); return true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            Log.Write("try", $"Opening {target} failed: {ex.Message}");
            return false;
        }
    }
}
