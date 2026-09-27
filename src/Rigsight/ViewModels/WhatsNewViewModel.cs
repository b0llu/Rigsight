using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// The "What's new" card: once after an update, the first time the app opens (never on a new install, never on its
/// own over a game), and any time from Settings → About. Shows what came since the version last seen, or every version.
/// </summary>
public sealed partial class WhatsNewViewModel(SettingsModel settings, Version current) : ObservableObject
{
    [ObservableProperty] private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    private bool _showingAll;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    private IReadOnlyList<ReleaseNotes> _shown = [];

    public string Heading => ShowingAll ? "Every update" : Shown.Count > 1 ? "What's new since you last looked" : "What's new";

    /// <summary>The app window opened: show what's new once, if an update brought anything.</summary>
    public void CheckOnStart()
    {
        var seenText = settings.Current.WhatsNewSeen;
        if (seenText is null || !Version.TryParse(seenText, out var seen))
        {
            // A new install (or something unreadable): nothing to tell, just remember where we are.
            MarkSeen();
            return;
        }
        var since = Changelog.Since(seen, current);
        if (since.Count == 0)
        {
            if (seen < current) MarkSeen(); // an update with nothing a person would notice
            return;
        }
        Shown = since;
        ShowingAll = false;
        IsOpen = true;
    }

    /// <summary>Settings → About: "See all updates".</summary>
    [RelayCommand]
    private void SeeAll()
    {
        Shown = Changelog.Releases;
        ShowingAll = true;
        IsOpen = true;
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        MarkSeen();
    }

    private void MarkSeen()
    {
        string now = $"{current.Major}.{current.Minor}.{Math.Max(current.Build, 0)}";
        if (settings.Current.WhatsNewSeen != now) settings.Update(s => s.WhatsNewSeen = now);
    }
}
