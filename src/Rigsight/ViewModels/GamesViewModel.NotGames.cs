using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A game that just left the library ("Not a game"), with its Undo (approved mock-up board "Not a game").</summary>
public sealed record LeftRow(string Name, string Sub, ImageSource? Icon, string Letter, IReadOnlyDictionary<string, AppCategory?> Before)
{
    public bool HasIcon => Icon is not null;
}

/// <summary>A row of "N apps you marked as not games": its name and icon, and Put back.</summary>
public sealed record NotGameItem(NotGameRow Row, ImageSource? Icon)
{
    public string Name => Row.Name;
    public string Letter => Row.Name.Length > 0 ? Row.Name[..1].ToUpperInvariant() : "?";
    public bool HasIcon => Icon is not null;
}

public sealed partial class GamesViewModel
{
    // ── Not a game (1.23.0) ─────────────────────────────────────────────

    /// <summary>The game that just left, with its Undo, at the top of the library until the next one leaves or it's undone.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasLeft))] private LeftRow? _left;
    public bool HasLeft => Left is not null;

    /// <summary>Everything marked not a game that RigSight had as one, for the quiet line at the foot of the library.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNotGames), nameof(NotGamesLine))] private IReadOnlyList<NotGameItem> _notGames = [];
    public bool HasNotGames => NotGames.Count > 0;
    /// <summary>"2 apps you marked as not games".</summary>
    public string NotGamesLine => NotGames.Count == 1 ? Lang.T("1 app you marked as not a game") : Lang.T($"{NotGames.Count} apps you marked as not games");

    /// <summary>
    /// The opened game's ⋯ menu, Not a game: the person's category for its programs (and its launcher's listing), at once,
    /// with no pop-up to confirm; the page goes back to the library, where it has left, with Undo. Its history stays, as an app.
    /// </summary>
    [RelayCommand]
    private void NotAGame()
    {
        if (Opened?.Entry is not { } entry) return;
        IReadOnlyDictionary<string, AppCategory?> before = new Dictionary<string, AppCategory?>();
        var category = Core.Games.NotGames.CategoryFor(entry);
        _settings.Update(s => before = Core.Games.NotGames.Mark(s, entry));
        Log.Write("games", $"Not a game: {entry.Name} ({string.Join(", ", before.Keys)}) is now {category}, was {string.Join(", ", before.Select(b => b.Value?.ToString() ?? "worked out"))}");
        string sub = !entry.HasHistory ? Lang.T("Left Games.")
            : category == AppCategory.Launcher ? Lang.T("Left Games. Its hours stay on Apps, as a launcher.")
            : Lang.T("Left Games. Its hours stay on Apps.");
        var icon = GamePictures.Icon(entry.ExePath ?? GamePictures.ProgramIn(entry.Installed?.InstallDir));
        Left = new LeftRow(entry.Name, sub, icon, entry.Name.Length > 0 ? entry.Name[..1].ToUpperInvariant() : "?", before);
        // Back to the library, where it has left at once (its cover gone before the library is read again): the Undo is there.
        Opened = null;
        _returnTo = null;
        BackText = Lang.T("Back to library");
        _all = [.. _all.Where(g => g.Key != entry.Key)];
        BuildCards();
        BuildShelf();
        _ = LoadAsync();
    }

    /// <summary>The game that just left comes back, every key as it was.</summary>
    [RelayCommand]
    private void UndoNotAGame()
    {
        if (Left is not { } left) return;
        _settings.Update(s => Core.Games.NotGames.Undo(s, left.Before));
        Log.Write("games", $"Not a game undone: {left.Name}");
        Left = null;
        _ = LoadAsync();
    }

    /// <summary>A game from the foot's list comes back to Games: RigSight's own reading of it counts again.</summary>
    [RelayCommand]
    private void PutBack(NotGameItem? item)
    {
        if (item is null) return;
        _settings.Update(s => Core.Games.NotGames.PutBack(s, item.Row));
        Log.Write("games", $"Put back in Games: {item.Name} ({string.Join(", ", item.Row.Keys)})");
        if (Left is { } left && left.Before.Keys.Intersect(item.Row.Keys, StringComparer.OrdinalIgnoreCase).Any()) Left = null;
        _ = LoadAsync();
    }

    /// <summary>
    /// The foot's list, read with the library: nothing is read when the person has put no app in another category (most
    /// people), so a visit costs nothing more. Their icons off the window's thread, as the covers'.
    /// </summary>
    private async Task LoadNotGamesAsync(IReadOnlyList<InstalledGame> installed)
    {
        if (!_settings.Current.AppCategories.Values.Any(c => c != AppCategory.Game))
        {
            NotGames = [];
            return;
        }
        var rows = await _reports.QueryAsync((db, s) => Core.Games.NotGames.Marked(db.LoadApps(), installed, s)) ?? [];
        var items = await Task.Run(() => rows.Select(r => new NotGameItem(r, GamePictures.Icon(r.ExePath ?? GamePictures.ProgramIn(installed.FirstOrDefault(g => g.Key == r.Key)?.InstallDir)))).ToList());
        NotGames = items;
    }
}
