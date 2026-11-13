using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Rewind;
using Rigsight.Services;

namespace Rigsight.Rewind;

/// <summary>A rewind in Settings → Rewind's list.</summary>
public sealed partial class RewindEntry(RewindFacts facts, RewindViewModel owner) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Line), nameof(IsNew))]
    private RewindFacts _facts = facts;

    public string Title => Facts.Sample is { } sample ? $"{RewindCards.TitleWithYear(Facts)} · {sample}" : RewindCards.TitleWithYear(Facts); // a test copy's sample says so
    public RewindKind Kind => Facts.Kind;
    public string Hue => RewindCards.KindHue(Facts.Kind);
    public bool IsNew => !Facts.Seen;

    public string Line
    {
        get
        {
            int cards = RewindCards.Of(Facts).Count;
            string made = Lang.T($"made {Facts.Made.ToString("d MMMM", System.Globalization.CultureInfo.CurrentCulture)}");
            return $"{RewindCards.KindName(Facts.Kind)} · {(cards == 1 ? Lang.T("1 card") : Lang.T($"{cards} cards"))} · {made}";
        }
    }

    // Watching and sharing are all a rewind does (the user, 2026-11-13: Recreate, Remove and every setting removed).
    [RelayCommand] private void Watch() => owner.Watch(Facts);
    [RelayCommand] private void Share() => owner.Watch(Facts, toShare: true);
}

/// <summary>A card in Share's list: ticked to go in the pictures, and the one previewed.</summary>
public sealed partial class SharePick(RewindCard card, int index) : ObservableObject
{
    public RewindCard Card { get; } = card;
    public int Index { get; } = index;
    public string Name => Card.Name;
    [ObservableProperty] private bool _included = true;
    [ObservableProperty] private bool _isPreviewed;
}

/// <summary>
/// Rewind in the app: the full screen when one is ready, watching it card by card, Share at the end, and the list in
/// Settings → Rewind. RigSight decides how a rewind looks while watched; shapes and looks are Share's.
/// </summary>
public sealed partial class RewindViewModel : ObservableObject
{
    private readonly ReportService _reports;
    private readonly SettingsModel _settings;

    public RewindViewModel(ReportService reports, SettingsModel settings)
    {
        _reports = reports;
        _settings = settings;
        Reload();
    }

    private DateTime _checked = DateTime.Now;

    /// <summary>
    /// The shell's minute tick, read every half hour: one finished while RigSight stays open (the agent makes it at the
    /// month's turn) shows as the sidebar's line, never full screen over what is being done.
    /// </summary>
    public void CheckWhileOpen()
    {
        if (IsOpen || DateTime.Now - _checked < TimeSpan.FromMinutes(30)) return;
        _checked = DateTime.Now;
        Reload();
        // A rewind waiting its turn from this start keeps its full screen for the next start.
        foreach (var e in Entries.Where(e => !e.Facts.Announced && !_waitingTurn.Contains(e.Facts.Id))) { e.Facts.Announced = true; RewindStore.Save(e.Facts); }
        RaiseWaiting();
    }

    /// <summary>New rewinds that waited because another card showed this start: the sidebar's line now, full screen next start.</summary>
    private readonly HashSet<string> _waitingTurn = [];

    private Core.Settings.RewindSettings S => _settings.Current.Rewind;

    private void OnPropertyChanged(params string[] names)
    {
        foreach (var name in names) base.OnPropertyChanged(name);
    }

    // ── The list ─────────────────────────────────────────────────────────
    public ObservableCollection<RewindEntry> Entries { get; } = [];
    public bool HasEntries => Entries.Count > 0;

    /// <summary>The newest one not watched yet, for the sidebar's line while it waits.</summary>
    public RewindEntry? Waiting => IsRevealing || IsWatching ? null : Entries.FirstOrDefault(e => e.IsNew && (e.Facts.Announced || _waitingTurn.Contains(e.Facts.Id)));
    public bool HasWaiting => Waiting is not null;
    public string WaitingTitle => Waiting is { } w ? Lang.T($"{RewindCards.Title(w.Facts)} rewind is ready") : "";
    public string WaitingLine => Waiting is { } w ? (RewindCards.Of(w.Facts).Count is var n && n == 1 ? Lang.T("1 card") : Lang.T($"{RewindCards.Of(w.Facts).Count} cards")) : "";

    public void Reload()
    {
        Entries.Clear();
        foreach (var f in RewindStore.All()) Entries.Add(new RewindEntry(f, this));
        OnPropertyChanged(nameof(HasEntries));
        RaiseWaiting();
    }

    private void RaiseWaiting() => OnPropertyChanged(nameof(Waiting), nameof(HasWaiting), nameof(WaitingTitle), nameof(WaitingLine));

    /// <summary>
    /// At start: makes what has finished since, then shows the biggest new one full screen. A test copy can be asked to
    /// make the month so far (RIGSIGHT_REWIND_THIS_MONTH), to try it before a month ends, or to make and show none
    /// (<see cref="RewindStore.Quiet"/>).
    /// </summary>
    /// <param name="waitTurn">Another big card showed this start (the welcome, What's new): new ones wait as the sidebar's
    /// line and open full screen next start, once (2026-10-30: from 1 Nov, What's new and the first rewind stacked).</param>
    /// <param name="shown">Told when one opens full screen: this start's big card.</param>
    public async Task StartAsync(bool waitTurn = false, Action? shown = null)
    {
        var now = DateTime.Now;
        if (RewindStore.Quiet) return;
        await EnsureGamesAsync();
        bool thisMonth = RigsightPaths.IsTestInstance && Environment.GetEnvironmentVariable("RIGSIGHT_REWIND_THIS_MONTH") == "1";
        await _reports.QueryAsync((db, s) =>
        {
            RewindStore.MakeDue(db, s, now);
            if (thisMonth)
            {
                var from = new DateTime(now.Year, now.Month, 1);
                if (!RewindStore.Exists(RewindBuilder.IdOf(RewindKind.Month, from)))
                {
                    var real = RewindBuilder.Build(db, RewindKind.Month, from, now.Date.AddDays(1), s, now);
                    RewindStore.Save(real);
                    // Samples from the same history, to see the cases a gaming PC with problems doesn't show.
                    foreach (var sample in RewindSamples.Of(real)) RewindStore.Save(sample);
                    // And the longer layouts, from the same history (mostly empty months, but every card is there).
                    foreach (var (kind, start, name) in new[] { (RewindKind.Quarter, from.AddMonths(-1), "3 months"), (RewindKind.Year, new DateTime(now.Year, 1, 1), "Year") })
                    {
                        var longer = RewindBuilder.Build(db, kind, start, start.AddMonths(RewindStore.Months(kind)), s, now);
                        (longer.Id, longer.Sample) = ($"{longer.Id}~sample", name);
                        RewindStore.Save(longer);
                    }
                }
            }
            return true;
        });
        Reload();
        var fresh = Entries.Where(e => !e.Facts.Announced && !e.Facts.Seen).ToList();
        if (fresh.Count == 0) return;
        if (waitTurn)
        {
            foreach (var e in fresh) _waitingTurn.Add(e.Facts.Id);
            Log.Write("rewind", $"{fresh.Count} new, waiting their turn: another card showed this start");
            RaiseWaiting();
            return;
        }
        // Everything new is announced now: the biggest full screen, the rest in the list.
        foreach (var e in fresh) { e.Facts.Announced = true; RewindStore.Save(e.Facts); }
        var biggest = fresh.OrderByDescending(e => e.Facts.To).ThenByDescending(e => e.Facts.Kind).First();
        Reveal(biggest.Facts);
        shown?.Invoke();
        RaiseWaiting();
    }

    /// <summary>
    /// The launchers' games, for the cards' covers and art: read here when the Games page hasn't been opened yet (it
    /// keeps them in the same place once it has).
    /// </summary>
    private static async Task EnsureGamesAsync()
    {
        if (Core.Apps.AppCatalog.InstalledGames.Count > 0) return;
        try { Core.Apps.AppCatalog.InstalledGames = await Task.Run(() => Core.Games.GameLibrary.Scan(Core.Games.GameSources.ThisPc(withArt: true))); }
        catch (Exception ex) { Log.Error("rewind", ex); }
    }

    // ── The full screen when one is ready ────────────────────────────────
    [ObservableProperty] private bool _isRevealing;
    [ObservableProperty] private RewindFacts? _revealed;
    [ObservableProperty] private string _revealTitle = "";
    [ObservableProperty] private string _revealSub = "";
    partial void OnIsRevealingChanged(bool value) { RaiseWaiting(); OnPropertyChanged(nameof(IsOpen)); }

    /// <summary>The layer is over the window: the full screen, or a rewind being watched.</summary>
    public bool IsOpen => IsRevealing || IsWatching;

    public void Reveal(RewindFacts f)
    {
        Revealed = f;
        RevealTitle = RewindCards.Ready(f);
        int n = RewindCards.Of(f).Count;
        string length = f.Kind switch
        {
            RewindKind.Month => Lang.T($"{f.From.Day} to {f.To.AddDays(-1).ToString("d MMMM", System.Globalization.CultureInfo.CurrentCulture)}"),
            RewindKind.Year => Lang.T("The whole year"),
            _ => RewindCards.KindName(f.Kind),
        };
        RevealSub = $"{length} · {(n == 1 ? Lang.T("1 card") : Lang.T($"{n} cards"))}";
        IsRevealing = true;
    }

    [RelayCommand] private void WatchRevealed() { if (Revealed is { } f) { IsRevealing = false; Watch(f); } }
    [RelayCommand] private void Later() => IsRevealing = false;

    // ── Watching ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isWatching;
    [ObservableProperty] private RewindFacts? _watched;
    [ObservableProperty] private IReadOnlyList<RewindCard> _cards = [];
    [ObservableProperty] private int _index;
    public bool AtEnd => Index >= Cards.Count;
    public bool CanGoBack => Index > 0;
    public bool CanGoNext => Index < Cards.Count;
    public string WatchedTitle => Watched is { } f ? RewindCards.TitleWithYear(f) : "";
    public string WatchedKind => Watched is { } f ? (f.Sample is { } sample ? $"{RewindCards.KindName(f.Kind)} · {sample}" : RewindCards.KindName(f.Kind)) : "";
    public string EndTitle => Watched is { } f ? Lang.T($"That was your {RewindCards.Title(f)}") : "";
    public string EndSub => Cards.Count == 1 ? Lang.T("1 card") : Lang.T($"{Cards.Count} cards");
    public RewindCard? Current => Index < Cards.Count ? Cards[Index] : null;
    partial void OnIndexChanged(int value) => OnPropertyChanged(nameof(AtEnd), nameof(CanGoBack), nameof(CanGoNext), nameof(Current));
    // The cards arriving change where the end is: without this the screen kept "at the end" from before (no cards),
    // so a rewind opened straight onto its end screen with the arrow to the next card greyed out.
    partial void OnCardsChanged(IReadOnlyList<RewindCard> value) => OnPropertyChanged(nameof(AtEnd), nameof(CanGoBack), nameof(CanGoNext), nameof(Current), nameof(EndSub));
    partial void OnIsWatchingChanged(bool value) { RaiseWaiting(); OnPropertyChanged(nameof(IsOpen)); }

    public void Watch(RewindFacts f, bool toShare = false)
    {
        IsRevealing = false;
        Watched = f;
        Cards = RewindCards.Of(f);
        Index = toShare ? Cards.Count : 0;
        OnPropertyChanged(nameof(WatchedTitle), nameof(WatchedKind), nameof(EndTitle), nameof(EndSub));
        IsSharing = false;
        IsWatching = true;
        if (!f.Seen) { f.Seen = true; RewindStore.Save(f); }
        foreach (var e in Entries.Where(e => e.Facts.Id == f.Id)) e.Facts = f;
        if (toShare) OpenShare();
        RaiseWaiting();
    }

    /// <summary>The notification's click: that rewind (by its id).</summary>
    public void Watch(string? id)
    {
        if (id is not null && RewindStore.Get(id) is { } f) Watch(f);
    }

    [RelayCommand] private void Next() { if (Index < Cards.Count) Index++; }
    [RelayCommand] private void Previous() { if (Index > 0) Index--; }
    [RelayCommand] private void Again() => Index = 0;
    [RelayCommand] private void Close() { IsSharing = false; IsWatching = false; }

    // ── Share ────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isSharing;
    public ObservableCollection<SharePick> Picks { get; } = [];
    [ObservableProperty] private SharePick? _previewed;
    public string Shape { get => S.ShareShape is "story" or "post" or "wide" ? S.ShareShape : "story"; set { _settings.Update(s => s.Rewind.ShareShape = value); OnPropertyChanged(); OnPropertyChanged(nameof(ShapeSize)); LookChanged?.Invoke(); } }
    public string ShapeSize => RewindPainter.Sizes[Shape] is var (w, h) ? $"{w} × {h}" : "";
    /// <summary>RigSight's own colours for the cards, watched and shared alike (Share's colours removed 2026-11-13).</summary>
    public RewindLook Own => new(Accents.Mono);
    public RewindLook Look => Own;
    public string ShareTitle => Watched is { } f ? Lang.T($"Share your {RewindCards.Title(f)} rewind") : "";
    public int IncludedCount => Picks.Count(p => p.Included);
    public string PickCount => Lang.T($"{IncludedCount} of {Picks.Count}");
    public string CopyText => IncludedCount == 1 ? Lang.T("Copy 1 picture") : Lang.T($"Copy {IncludedCount} pictures");

    /// <summary>The view redraws the preview and the small pictures when the shape changes.</summary>
    public event Action? LookChanged;

    [RelayCommand]
    private void OpenShare()
    {
        Picks.Clear();
        for (int i = 0; i < Cards.Count; i++)
        {
            var p = new SharePick(Cards[i], i);
            p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SharePick.Included)) OnPropertyChanged(nameof(IncludedCount), nameof(PickCount), nameof(CopyText)); };
            Picks.Add(p);
        }
        Preview(Picks.FirstOrDefault());
        OnPropertyChanged(nameof(ShareTitle), nameof(IncludedCount), nameof(PickCount), nameof(CopyText), nameof(Shape), nameof(ShapeSize));
        IsSharing = true;
    }

    [RelayCommand] private void CloseShare() { IsSharing = false; Toast = ""; }

    [RelayCommand]
    private void Preview(SharePick? pick)
    {
        foreach (var p in Picks) p.IsPreviewed = p == pick;
        Previewed = pick;
    }

    private System.Windows.Media.Imaging.BitmapSource PictureOf(SharePick p) =>
        RewindPainter.Picture(p.Card, Watched!, p.Index, Cards.Count, Shape, Look);

    /// <summary>Every ticked picture, in order, onto the clipboard: pasted into a post anywhere, they go in as they are.</summary>
    [RelayCommand]
    private void Copy()
    {
        var picks = Included;
        if (Watched is null || picks.Count == 0 || !CopyPictures(picks)) return;
        Say(picks.Count == 1 ? Lang.T("1 picture copied.") : Lang.T($"{picks.Count} pictures copied."));
    }

    /// <summary>
    /// Pictures on the clipboard the way browsers take them: each as a PNG file (so a post can take several at once,
    /// in order), and the first also as PNG and bitmap data (for apps that take one picture). WPF's own SetImage gave
    /// only a bitmap with transparency, which browsers pasted as nothing.
    /// </summary>
    /// <summary>The pictures as PNG files in a folder of their own under Temp, named in order; the first picture too.</summary>
    private (List<string> Files, System.Windows.Media.Imaging.BitmapSource First) SavePictures(IReadOnlyList<SharePick> picks)
    {
        string folder = Path.Combine(Path.GetTempPath(), "RigSight Rewind");
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder, "*.png")) { try { File.Delete(old); } catch (IOException) { } }
        var files = new List<string>();
        System.Windows.Media.Imaging.BitmapSource? first = null;
        foreach (var p in picks)
        {
            var picture = PictureOf(p);
            first ??= picture;
            string path = Path.Combine(folder, $"{p.Index + 1:00} {string.Concat(p.Name.Split(Path.GetInvalidFileNameChars()))}.png");
            RewindPainter.SavePng(picture, path);
            files.Add(path);
        }
        return (files, first!);
    }

    private bool CopyPictures(IReadOnlyList<SharePick> picks)
    {
        if (picks.Count == 0) return false;
        try
        {
            var (saved, first) = SavePictures(picks);
            var files = new System.Collections.Specialized.StringCollection();
            files.AddRange([.. saved]);
            var data = new DataObject();
            data.SetFileDropList(files);
            var png = new MemoryStream();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(first!));
            encoder.Save(png);
            data.SetData("PNG", png, false);
            // Opaque for the bitmap form: the cards are opaque anyway, and a bitmap with transparency pastes black or empty.
            data.SetImage(new System.Windows.Media.Imaging.FormatConvertedBitmap(first, System.Windows.Media.PixelFormats.Bgr24, null, 0));
            Clipboard.SetDataObject(data, copy: true);
            Log.Write("rewind", $"Copied {picks.Count} picture(s) as files, the first also as PNG and bitmap");
            return true;
        }
        catch (Exception ex) { Log.Error("rewind", ex); Say(Lang.T("The pictures couldn't be copied. Try again in a moment.")); return false; }
    }

    private List<SharePick> Included => [.. Picks.Where(p => p.Included)];

    // ── A short message at the foot of the screen ────────────────────────
    [ObservableProperty] private string _toast = "";
    public bool HasToast => Toast.Length > 0;
    partial void OnToastChanged(string value) => OnPropertyChanged(nameof(HasToast));
    private int _toastAsk;

    private async void Say(string text)
    {
        int ask = ++_toastAsk;
        Toast = text;
        await Task.Delay(3500);
        if (ask == _toastAsk) Toast = "";
    }
}
