using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>
/// The "What's new" card: once after an update, the first time the app opens (never on a new install, never on its
/// own over a game, never when switched off in Settings → About), and any time from Settings → About. Shows what came since the version last seen, or every version.
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
    [NotifyPropertyChangedFor(nameof(Heading), nameof(SubHeading), nameof(Newest), nameof(Older), nameof(HasOlder), nameof(OlderHeading), nameof(Counts))]
    private IReadOnlyList<ReleaseNotes> _shown = [];

    partial void OnShownChanged(IReadOnlyList<ReleaseNotes> value)
    {
        _older = [.. value.Skip(1).Select(r => new ReleaseRow(r))];
        _ = LoadFactsAsync([.. value.Where(r => r.Feature is not null).Select(r => r.Feature!)]);
    }

    /// <summary>The newest update shown, in full: its feature, then every line (approved mock-up, 2026-10-24).</summary>
    public ReleaseNotes? Newest => Shown.Count > 0 ? Shown[0] : null;

    /// <summary>The ones before it, one row each that opens in place.</summary>
    public IReadOnlyList<ReleaseRow> Older => _older;
    private IReadOnlyList<ReleaseRow> _older = [];
    public bool HasOlder => _older.Count > 0;
    public string OlderHeading => ShowingAll ? Core.Lang.T("EARLIER UPDATES") : Core.Lang.T("EARLIER UPDATES YOU SKIPPED");

    /// <summary>
    /// Under the heading: the version and date, or how many updates came since the one last seen; every update when
    /// all are shown.
    /// </summary>
    public string SubHeading
    {
        get
        {
            if (Newest is not { } newest) return "";
            if (ShowingAll) return Core.Lang.T($"{Shown.Count} updates, newest first");
            string version = $"{newest.Version.Major}.{newest.Version.Minor}.{Math.Max(newest.Version.Build, 0)}";
            return Shown.Count > 1 && _seen is { } seen
                ? Core.Lang.T($"RigSight {version} · {Shown.Count} updates since {seen}, the last you saw")
                : Core.Lang.T($"RigSight {version} · {newest.DateText}");
        }
    }

    private string? _seen;

    /// <summary>
    /// The whole update in a glance, before any detail: "1 new feature", "2 new", "3 better", "1 fixed", across what's
    /// shown since the version last seen (not for every update at once, where the counts would say nothing).
    /// </summary>
    public IReadOnlyList<string> Counts
    {
        get
        {
            if (ShowingAll || Shown.Count == 0) return [];
            int features = Shown.Count(r => r.Feature is { IsRemoval: false });
            int added = Shown.Sum(r => r.New.Length), better = Shown.Sum(r => r.Better.Length), fixedCount = Shown.Sum(r => r.Fixed.Length);
            var list = new List<string>();
            if (features > 0) list.Add(features == 1 ? Core.Lang.T("1 new feature") : Core.Lang.T($"{features} new features"));
            if (added > 0) list.Add(Core.Lang.T($"{added} new"));
            if (better > 0) list.Add(Core.Lang.T($"{better} better"));
            if (fixedCount > 0) list.Add(Core.Lang.T($"{fixedCount} fixed"));
            return list;
        }
    }

    /// <summary>A line naming a page: the card closes and that page opens.</summary>
    [RelayCommand]
    private void OpenLine(ReleaseLine line)
    {
        if (line.Page is null) return;
        Close();
        go?.Invoke(line.Page);
    }

    /// <summary>Asks each shown feature's page what it already has for this PC (most have nothing to add).</summary>
    private async Task LoadFactsAsync(List<ReleaseFeature> features)
    {
        foreach (var feature in features)
        {
            feature.Fact = null;
            // Only on a page's own arrival, and only while that's the news (the newest card shown): on an older card,
            // or one about something else that merely opens the page, the line had nothing to do with what the card says.
            if (factFor is null || feature != features[0] || !feature.IsPage) continue;
            try { feature.Fact = await factFor(feature.Page); }
            catch (Exception ex) { Core.Log.Error("whatsnew", ex); }
        }
    }

    /// <summary>A feature's "Open" button: the card closes and its page opens.</summary>
    [RelayCommand]
    private void OpenFeature(ReleaseFeature feature)
    {
        Close();
        go?.Invoke(feature.Page);
    }

    public string Heading => ShowingAll ? Core.Lang.T("Every update") : Core.Lang.T("What's new");

    /// <summary>"Every update" at the card's foot, for what came before (not while every update is already shown).</summary>
    public bool CanSeeAll => !ShowingAll;
    partial void OnShowingAllChanged(bool value) { OnPropertyChanged(nameof(CanSeeAll)); OnPropertyChanged(nameof(OlderHeading)); OnPropertyChanged(nameof(Counts)); }

    private bool _fromUpdate;

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
        if (since.Count == 0 || !settings.Current.ShowWhatsNew)
        {
            // An update with nothing a person would notice, or the card switched off: only remember where we are.
            if (seen < current) MarkSeen();
            return;
        }
        _seen = $"{seen.Major}.{seen.Minor}.{Math.Max(seen.Build, 0)}";
        ShowingAll = false;
        Shown = since;
        _fromUpdate = true;
        IsOpen = true;
    }

    /// <summary>Settings → About: "See all updates".</summary>
    [RelayCommand]
    private void SeeAll()
    {
        ShowingAll = true;
        Shown = Changelog.Releases;
        _fromUpdate = false;
        IsOpen = true;
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        MarkSeen();
        // After an update's own card: its feature's page keeps a dot in the sidebar until it's opened (a feature with a
        // page of the sidebar's, not one taken away). Someone who switched What's new off never sees the card, so never the dot.
        if (_fromUpdate && Newest?.Feature is { IsRemoval: false, ShowsButton: true } feature && ViewModels.SidebarViewModel.IconOf(feature.Page) is not null)
        {
            string version = $"{Newest.Version.Major}.{Newest.Version.Minor}.{Math.Max(Newest.Version.Build, 0)}", name = feature.Name;
            string text = Core.Lang.T($"New in {version}: {name}");
            settings.Update(s => { s.NewFeaturePage = feature.Page; s.NewFeatureText = text; });
        }
        _fromUpdate = false;
    }

    private void MarkSeen()
    {
        string now = $"{current.Major}.{current.Minor}.{Math.Max(current.Build, 0)}";
        if (settings.Current.WhatsNewSeen != now) settings.Update(s => s.WhatsNewSeen = now);
    }
}

/// <summary>An earlier update as one row under the newest: its version and biggest change, opening in place to all its lines.</summary>
public sealed partial class ReleaseRow(ReleaseNotes notes) : ObservableObject
{
    public ReleaseNotes Notes { get; } = notes;
    public string Version => $"{Notes.Version.Major}.{Notes.Version.Minor}.{Math.Max(Notes.Version.Build, 0)}";

    /// <summary>Its headline feature as a line of its own when opened (its name and what it's for), then its lines.</summary>
    public IReadOnlyList<ReleaseLine> Lines =>
        Notes.Feature is { } f ? [new ReleaseLine($"{f.Name}: {f.Pitch}", f.IsRemoval ? "fixed" : "new", f.Icon, f.ShowsButton ? f.Page : null, null), .. Notes.Lines] : Notes.Lines;

    [ObservableProperty] private bool _isExpanded;

    [RelayCommand] private void Toggle() => IsExpanded = !IsExpanded;
}
