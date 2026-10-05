using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// The "What's new" card: once after an update, the first time the app opens (never on a new install, never on its
/// own over a game), and any time from Settings → About. Shows what came since the version last seen, or every version.
/// </summary>
/// <param name="go">Opens a page (a new feature's "Open" button).</param>
/// <param name="factFor">Something true about this PC for a new feature's page ("31 changes already found…"), if there is.</param>
public sealed partial class WhatsNewViewModel(SettingsModel settings, Version current, Action<string>? go = null,
    Func<string, Task<string?>>? factFor = null) : ObservableObject
{
    [ObservableProperty] private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    private bool _showingAll;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    private IReadOnlyList<ReleaseNotes> _shown = [];

    /// <summary>What the newest shown feature already has to show on this PC (null: nothing to say).</summary>
    [ObservableProperty] private string? _featureFact;

    partial void OnShownChanged(IReadOnlyList<ReleaseNotes> value) => _ = LoadFactsAsync([.. value.Where(r => r.Feature is not null).Select(r => r.Feature!)]);

    /// <summary>Asks each shown feature's page what it already has for this PC (most have nothing to add).</summary>
    private async Task LoadFactsAsync(List<ReleaseFeature> features)
    {
        FeatureFact = null;
        foreach (var feature in features)
        {
            feature.Fact = null;
            if (factFor is null) continue;
            try { feature.Fact = await factFor(feature.Page); }
            catch (Exception ex) { Core.Log.Error("whatsnew", ex); }
            if (feature == features[0]) FeatureFact = feature.Fact;
        }
    }

    /// <summary>A feature's "Open" button: the card closes and its page opens.</summary>
    [RelayCommand]
    private void OpenFeature(ReleaseFeature feature)
    {
        Close();
        go?.Invoke(feature.Page);
    }

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
