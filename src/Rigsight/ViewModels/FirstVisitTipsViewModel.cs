using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// A tip the first time each page opens, on a new install only (approved mock-up, 2026-10-30): it floats under the page's
/// title, nothing moves under it, and it never shows twice. Never while a card covers the page (it waits for the next
/// visit), and one at a time. "Asking happens here" is the same kind of tip, under the top bar's Ask box, once the welcome
/// ends with "Later".
/// </summary>
public sealed partial class FirstVisitTipsViewModel(SettingsModel settings, Func<bool> covered) : ObservableObject
{
    /// <summary>The ask box's tip, kept as a page of its own among the ones seen.</summary>
    public const string AskTip = "ask";

    /// <summary>Each page's tip (Home has the welcome instead); a dashboard of one's own shares one.</summary>
    private static readonly Dictionary<string, (string Title, string Text)> Tips = new()
    {
        ["reports"] = (Lang.Key("Reports"), Lang.Key("Your PC's day, week, month and year in one report. It fills in after your first full day.")),
        ["apps"] = (Lang.Key("Apps"), Lang.Key("Every app's time, CPU, graphics card and memory, hour by hour. It fills in as you use your PC.")),
        ["games"] = (Lang.Key("Games"), Lang.Key("Each game you play: time, temperatures, frame rate and its settings. 1% lows and frame times need RivaTuner.")),
        ["processes"] = (Lang.Key("Processes"), Lang.Key("What's running right now and what it's using, like Task Manager. Right-click one to ask what it is.")),
        ["crashes"] = (Lang.Key("Crashes from the last 90 days"), Lang.Key("Windows' own logs come in here within a minute. New crashes show up as they happen, with a card at the corner of your screen.")),
        ["timeline"] = (Lang.Key("Timeline"), Lang.Key("Everything that changed on this PC (drivers, Windows updates, BIOS, settings), with what got better or worse after.")),
        ["temperatures"] = (Lang.Key("Temperatures"), Lang.Key("Every part's heat, now and over time. You get a card when the CPU passes 85° or the graphics card 83°; change that in Settings.")),
        ["fans"] = (Lang.Key("Fans"), Lang.Key("Every fan's speed and how it follows the heat.")),
        ["memory"] = (Lang.Key("Memory"), Lang.Key("What fills your memory now, and what's usual for each app. An app holding more than its usual is pointed out.")),
        ["storage"] = (Lang.Key("Storage"), Lang.Key("Every drive's space and health, and what's filling it.")),
        ["network"] = (Lang.Key("Network"), Lang.Key("Internet use by app, big downloads and when the connection dropped.")),
        ["sensors"] = (Lang.Key("All sensors"), Lang.Key("Every reading RigSight sees, grouped by part.")),
        ["widgets"] = (Lang.Key("Widgets"), Lang.Key("Readings on your desktop. The thin bar at the top is one: change it or turn it off here.")),
        ["overlay"] = (Lang.Key("Overlay"), Lang.Key("Readings over your games, drawn by RivaTuner Statistics Server.")),
        ["taskbar"] = (Lang.Key("Taskbar"), Lang.Key("Readings next to the clock in your taskbar.")),
        ["dashboard"] = (Lang.Key("Dashboards"), Lang.Key("A page of your own, from any readings you pick.")),
        [AskTip] = (Lang.Key("Asking happens here"), Lang.Key("The box at the top of every page. Ctrl+K opens it from anywhere. The first time, it says what's shared with the AI before anything is sent.")),
    };

    /// <summary>The page whose tip is up ("" when none): one at a time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(IsPageTip), nameof(IsAskTip), nameof(Title), nameof(Text))]
    private string _showing = "";

    public bool IsOpen => Showing.Length > 0;
    public bool IsPageTip => IsOpen && Showing != AskTip;
    public bool IsAskTip => Showing == AskTip;
    public string Title => Tips.TryGetValue(Showing, out var t) ? Lang.T(t.Title) : "";
    public string Text => Tips.TryGetValue(Showing, out var t) ? Lang.T(t.Text) : "";

    /// <summary>The page's key as a tip knows it: every dashboard of one's own shares "dashboard".</summary>
    public static string KeyOf(string page) => page.StartsWith("custom", StringComparison.OrdinalIgnoreCase) ? "dashboard" : page;

    /// <summary>Whether a page has a tip that hasn't been seen, on an install that has them.</summary>
    public bool Due(string page) => settings.Current.FirstVisitTips && Tips.ContainsKey(KeyOf(page)) && !settings.Current.TipsSeen.Contains(KeyOf(page));

    /// <summary>A page opened, or the card over it closed: its tip, if it has one due and nothing covers it.</summary>
    public void Visit(string page)
    {
        string key = KeyOf(page);
        if (IsAskTip) return; // the ask box's tip stays until it's closed or the box is used
        if (!Due(page) || covered()) { Showing = ""; return; }
        if (Showing == key) return;
        Showing = key;
        Log.Write("tips", $"First visit to {key}: its tip shows");
    }

    /// <summary>The welcome ended with "Later": where asking lives, once.</summary>
    public void ShowAskTip()
    {
        if (!settings.Current.FirstVisitTips || settings.Current.TipsSeen.Contains(AskTip)) return;
        Showing = AskTip;
    }

    /// <summary>The ask box was used: its tip has done its job.</summary>
    public void AskUsed() { if (IsAskTip) Seen(); }

    /// <summary>A card came over the page: the tip goes, and comes back on the next visit.</summary>
    public void Covered() { if (IsPageTip) Showing = ""; }

    /// <summary>"Got it": seen for good.</summary>
    [RelayCommand]
    private void Seen()
    {
        if (!IsOpen) return;
        string key = Showing;
        settings.Update(s => { if (!s.TipsSeen.Contains(key)) s.TipsSeen.Add(key); });
        Showing = "";
    }
}
