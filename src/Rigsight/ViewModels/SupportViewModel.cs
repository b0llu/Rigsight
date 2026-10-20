using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// "Enjoying RigSight?": the card in the middle of the window that asks for a coffee, at a good moment and only when
/// <see cref="SupportAsk"/> allows. Asked at most once each time the app opens. Never in What's new. The coffee button
/// opens the Buy Me a Coffee page and thanks; "Maybe later" (or Esc, or a click beside it) waits; "Don't show again"
/// ends it. Everything it counts stays on this PC.
/// </summary>
public sealed partial class SupportViewModel : ObservableObject
{
    private readonly SettingsModel _settings;
    private readonly ReportService _reports;

    public SupportViewModel(SettingsModel settings, ReportService reports)
    {
        _settings = settings;
        _reports = reports;
        settings.Changed += () => OnPropertyChanged(nameof(HighlightMenu));
    }

    /// <summary>The Settings menu's "Support RigSight" in Buy Me a Coffee's yellow (see <see cref="SupportAskState.Highlight"/>).</summary>
    public bool HighlightMenu => _settings.Current.Support.Highlight;

    [ObservableProperty] private bool _isOpen;

    /// <summary>After the coffee button: the card says thank you, then closes by itself.</summary>
    [ObservableProperty] private bool _thanked;

    private bool _askedThisRun;

    /// <summary>
    /// The test copy can be asked not to wait (RIGSIGHT_SUPPORT_NOW=1): no three weeks, days of use, bad day or 30 days
    /// after "Maybe later". What ends it still does (a coffee, "Don't show again", a Support button, three times), so
    /// that can be tried in it too.
    /// </summary>
    private static bool ShowNow => RigsightPaths.IsTestInstance && Environment.GetEnvironmentVariable("RIGSIGHT_SUPPORT_NOW") == "1";

    /// <summary>
    /// A good moment came (<paramref name="moment"/>, for the log: "opening" or "rewind"): asks, if the rule allows and
    /// it hasn't asked since the app opened.
    /// </summary>
    public async Task TryAskAsync(string moment, Func<bool>? stillGood = null)
    {
        if (_askedThisRun || IsOpen) return;
        var state = _settings.Current.Support;
        var (firstDay, daysUsed, crashes) = await _reports.SupportFactsAsync();
        var muted = _settings.Current.MutedCrashApps;
        bool badDay = (crashes ?? []).Any(exe => !muted.Contains(exe, StringComparer.OrdinalIgnoreCase));
        var (may, why) = ShowNow
            ? SupportAsk.May(new SupportAskState { Shown = state.Shown, Done = state.Done, Clicked = state.Clicked }, DateTime.Now, DateTime.Now.AddYears(-1), 99, false)
            : SupportAsk.May(state, DateTime.Now, firstDay, daysUsed, badDay);
        if (ShowNow && may) why += ", the test copy not waiting";
        if (!may)
        {
            Log.Write("support", $"Not asking at the {moment}: {why}");
            return;
        }
        // The moment may have passed while the history was read (another page, a card opened).
        if (_askedThisRun || IsOpen || stillGood?.Invoke() == false) return;
        _askedThisRun = true;
        Log.Write("support", $"Asking at the {moment} ({why}; history from {firstDay:d}, {daysUsed} days of use)");
        _settings.Update(s =>
        {
            s.Support.Shown++;
            s.Support.LastShown = DateTime.Now;
        });
        // Saved now, not after the usual short wait: counted even if the app closes or crashes while it's up.
        _settings.Flush();
        Thanked = false;
        IsOpen = true;
    }

    [RelayCommand]
    private async Task Coffee()
    {
        try { Process.Start(new ProcessStartInfo(SupportAsk.CoffeeUrl) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Write("support", $"Opening the coffee page failed: {ex.Message}"); }
        Log.Write("support", "Buy me a coffee pressed: won't ask again");
        _settings.Update(s => s.Support.Done = true);
        _settings.Flush();
        Thanked = true;
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (Thanked) IsOpen = false;
    }

    [RelayCommand]
    private void Later()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (Thanked) return;
        var s = _settings.Current.Support;
        Log.Write("support", s.Shown >= SupportAsk.MaxShown
            ? "Maybe later: that was the last time"
            : $"Maybe later: asks again in {SupportAsk.LaterDays[Math.Min(s.Shown, SupportAsk.LaterDays.Length) - 1]} days at the soonest");
    }

    [RelayCommand]
    private void Never()
    {
        Log.Write("support", "Don't show again pressed");
        _settings.Update(s => s.Support.Done = true);
        _settings.Flush();
        IsOpen = false;
    }
}
