using System.Collections.ObjectModel;
using System.Diagnostics;
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

    public string Title => RewindCards.TitleWithYear(Facts);
    public RewindKind Kind => Facts.Kind;
    public string Hue => RewindCards.KindHue(Facts.Kind);
    public bool IsNew => !Facts.Seen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Line))]
    private bool _recreating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Line))]
    private bool _recreated;

    public bool CanRecreate => RewindStore.CanRecreate(Facts, owner.KeepDays, DateTime.Now);

    public string Line
    {
        get
        {
            int cards = RewindCards.Of(Facts).Count;
            string made = Lang.T($"made {Facts.Made.ToString("d MMMM", System.Globalization.CultureInfo.CurrentCulture)}");
            string line = $"{RewindCards.KindName(Facts.Kind)} · {(cards == 1 ? Lang.T("1 card") : Lang.T($"{cards} cards"))} · {made}";
            if (Recreated) return Lang.T("Recreated just now");
            return CanRecreate ? line : $"{line} · {Lang.T("too old to recreate")}";
        }
    }

    /// <summary>History's length changed: whether it can be recreated, and its line, may have too.</summary>
    public void Refresh() { OnPropertyChanged(nameof(CanRecreate)); OnPropertyChanged(nameof(Line)); }

    [RelayCommand] private void Watch() => owner.Watch(Facts);
    [RelayCommand] private void Share() => owner.Watch(Facts, toShare: true);
    [RelayCommand] private Task Recreate() => owner.RecreateAsync(this);
    [RelayCommand] private void Remove() => owner.Remove(this);
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
        foreach (var e in Entries.Where(e => !e.Facts.Announced)) { e.Facts.Announced = true; RewindStore.Save(e.Facts); }
        RaiseWaiting();
    }

    public Action? OpenSettings { get; set; }
    public int KeepDays => _settings.Current.Tracking.KeepHistoryDays;
    private Core.Settings.RewindSettings S => _settings.Current.Rewind;

    private void OnPropertyChanged(params string[] names)
    {
        foreach (var name in names) base.OnPropertyChanged(name);
    }

    // ── The list ─────────────────────────────────────────────────────────
    public ObservableCollection<RewindEntry> Entries { get; } = [];
    public bool HasEntries => Entries.Count > 0;

    /// <summary>The newest one not watched yet, for the sidebar's line while it waits.</summary>
    public RewindEntry? Waiting => IsRevealing || IsWatching ? null : Entries.FirstOrDefault(e => e.IsNew && e.Facts.Announced);
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
    /// At start: makes what has finished since, then shows the biggest new one full screen (when that's wanted). A test
    /// copy can be asked to make the month so far (RIGSIGHT_REWIND_THIS_MONTH), to try it before a month ends.
    /// </summary>
    public async Task StartAsync()
    {
        var now = DateTime.Now;
        bool thisMonth = RigsightPaths.IsTestInstance && Environment.GetEnvironmentVariable("RIGSIGHT_REWIND_THIS_MONTH") == "1";
        await _reports.QueryAsync((db, s) =>
        {
            RewindStore.MakeDue(db, s, now);
            if (thisMonth)
            {
                var from = new DateTime(now.Year, now.Month, 1);
                if (!RewindStore.Exists(RewindBuilder.IdOf(RewindKind.Month, from)))
                    RewindStore.Save(RewindBuilder.Build(db, RewindKind.Month, from, now.Date.AddDays(1), s, now));
            }
            return true;
        });
        Reload();
        var fresh = Entries.Where(e => !e.Facts.Announced && !e.Facts.Seen).ToList();
        if (fresh.Count == 0) return;
        // Everything new is announced now: the biggest full screen, the rest in the list.
        foreach (var e in fresh) { e.Facts.Announced = true; RewindStore.Save(e.Facts); }
        var biggest = fresh.OrderByDescending(e => e.Facts.To).ThenByDescending(e => e.Facts.Kind).First();
        if (S.FullScreen) Reveal(biggest.Facts, fresh.Where(e => e != biggest).Select(e => e.Facts).ToList());
        RaiseWaiting();
    }

    // ── The full screen when one is ready ────────────────────────────────
    [ObservableProperty] private bool _isRevealing;
    [ObservableProperty] private RewindFacts? _revealed;
    [ObservableProperty] private string _revealTitle = "";
    [ObservableProperty] private string _revealSub = "";
    [ObservableProperty] private string _alsoReady = "";
    [ObservableProperty] private bool _askedNoFullScreen;
    public bool HasAlsoReady => AlsoReady.Length > 0;
    partial void OnAlsoReadyChanged(string value) => OnPropertyChanged(nameof(HasAlsoReady));
    partial void OnIsRevealingChanged(bool value) { RaiseWaiting(); OnPropertyChanged(nameof(IsOpen)); }

    /// <summary>The layer is over the window: the full screen, or a rewind being watched.</summary>
    public bool IsOpen => IsRevealing || IsWatching;

    public void Reveal(RewindFacts f, IReadOnlyList<RewindFacts> also)
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
        AlsoReady = also.Count == 0 ? "" : Lang.T($"Also ready: {string.Join(", ", also.Select(RewindCards.Title))}. You’ll find them in Settings → Rewind.");
        AskedNoFullScreen = false;
        IsRevealing = true;
    }

    [RelayCommand] private void WatchRevealed() { if (Revealed is { } f) { IsRevealing = false; Watch(f); } }
    [RelayCommand] private void Later() => IsRevealing = false;

    [RelayCommand]
    private void NoFullScreen()
    {
        _settings.Update(s => s.Rewind.FullScreen = false);
        AskedNoFullScreen = true;
        OnPropertyChanged(nameof(FullScreen));
    }

    [RelayCommand]
    private void UndoNoFullScreen()
    {
        _settings.Update(s => s.Rewind.FullScreen = true);
        AskedNoFullScreen = false;
        OnPropertyChanged(nameof(FullScreen));
    }

    [RelayCommand] private void RevealSettings() { IsRevealing = false; OpenSettings?.Invoke(); }

    // ── Watching ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isWatching;
    [ObservableProperty] private RewindFacts? _watched;
    [ObservableProperty] private IReadOnlyList<RewindCard> _cards = [];
    [ObservableProperty] private int _index;
    public bool AtEnd => Index >= Cards.Count;
    public bool CanGoBack => Index > 0;
    public bool CanGoNext => Index < Cards.Count;
    public string WatchedTitle => Watched is { } f ? RewindCards.TitleWithYear(f) : "";
    public string WatchedKind => Watched is { } f ? RewindCards.KindName(f.Kind) : "";
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
    public RewindLook AppLook => RewindLook.App(_settings.Current);
    public RewindLook Look => new(S.ShareTheme.Length > 0 ? S.ShareTheme : AppLook.Theme, S.ShareAccent.Length > 0 ? S.ShareAccent : AppLook.Accent);
    public bool LookIsApps => Look == AppLook;
    public string ShareTitle => Watched is { } f ? Lang.T($"Share your {RewindCards.Title(f)} rewind") : "";
    public int IncludedCount => Picks.Count(p => p.Included);
    public string PickCount => Lang.T($"{IncludedCount} of {Picks.Count}");
    public string ExportText => IncludedCount == 1 ? Lang.T("Export 1 picture") : Lang.T($"Export {IncludedCount} pictures");

    /// <summary>The view redraws the preview and the small pictures when the shape or look changes.</summary>
    public event Action? LookChanged;

    public void SetTheme(string key) { _settings.Update(s => s.Rewind.ShareTheme = key == AppLook.Theme && S.ShareAccent.Length == 0 ? "" : key); RaiseLook(); }
    public void SetAccent(string key) { _settings.Update(s => s.Rewind.ShareAccent = key == AppLook.Accent && S.ShareTheme.Length == 0 ? "" : key); RaiseLook(); }
    [RelayCommand] private void SameAsApp() { _settings.Update(s => { s.Rewind.ShareTheme = ""; s.Rewind.ShareAccent = ""; }); RaiseLook(); }
    private void RaiseLook() { OnPropertyChanged(nameof(Look), nameof(LookIsApps)); LookChanged?.Invoke(); }

    [RelayCommand]
    private void OpenShare()
    {
        Picks.Clear();
        for (int i = 0; i < Cards.Count; i++)
        {
            var p = new SharePick(Cards[i], i);
            p.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SharePick.Included)) OnPropertyChanged(nameof(IncludedCount), nameof(PickCount), nameof(ExportText)); };
            Picks.Add(p);
        }
        Preview(Picks.FirstOrDefault());
        OnPropertyChanged(nameof(ShareTitle), nameof(IncludedCount), nameof(PickCount), nameof(ExportText), nameof(Shape), nameof(ShapeSize), nameof(Look), nameof(LookIsApps));
        IsSharing = true;
    }

    [RelayCommand] private void CloseShare() { IsSharing = false; Toast = ""; NextBatchText = ""; }

    [RelayCommand]
    private void Preview(SharePick? pick)
    {
        foreach (var p in Picks) p.IsPreviewed = p == pick;
        Previewed = pick;
    }

    private System.Windows.Media.Imaging.BitmapSource PictureOf(SharePick p) =>
        RewindPainter.Picture(p.Card, Watched!, p.Index, Cards.Count, Shape, Look);

    [RelayCommand]
    private void Copy()
    {
        if (Previewed is not { } p || Watched is null) return;
        if (CopyPictures([p])) Say(Lang.T("Picture copied. Paste it into a post anywhere."));
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
        catch (Exception ex) { Log.Error("rewind", ex); Say(Lang.T("The picture couldn't be copied. Try again in a moment.")); return false; }
    }

    private List<SharePick> Included => [.. Picks.Where(p => p.Included)];

    /// <summary>X takes up to 4 pictures a post: the ticked ones go in fours, the first four in the post, the rest in replies.</summary>
    public const int XBatch = 4;
    private int _xNext;
    [ObservableProperty] private string _nextBatchText = "";
    public bool HasNextBatch => NextBatchText.Length > 0;
    partial void OnNextBatchTextChanged(string value) => base.OnPropertyChanged(nameof(HasNextBatch));

    private void SetNextBatch()
    {
        int left = Included.Count - _xNext;
        NextBatchText = left <= 0 ? "" : left == 1 ? Lang.T($"Share picture {_xNext + 1}") : Lang.T($"Share pictures {_xNext + 1} to {_xNext + Math.Min(XBatch, left)}");
    }

    private string PostText => Watched is { } f ? Lang.T($"My {RewindCards.TitleWithYear(f)} on my PC, from #RigSight Rewind") : "";

    /// <summary>
    /// Windows' Share window with the next pictures (up to four, in order) and the post's text in it: picking the X app
    /// there opens a post with both. More than four: the rest go the same way for a reply, from the button that stays.
    /// </summary>
    private bool ShareBatch(int from)
    {
        var picks = Included.Skip(from).Take(XBatch).ToList();
        if (picks.Count == 0 || Application.Current?.MainWindow is not { } window) return false;
        try
        {
            var (files, _) = SavePictures(picks);
            if (!WindowsShare.Show(window, ShareTitle, PostText, files)) { Say(Lang.T("The Windows Share window couldn't open. Try Export instead.")); return false; }
            _xNext = from + picks.Count;
            return true;
        }
        catch (Exception ex) { Log.Error("rewind", ex); Say(Lang.T("The pictures couldn't be shared. Try Export instead.")); return false; }
    }

    [RelayCommand]
    private void PostX()
    {
        if (Watched is null || !ShareBatch(0)) return;
        SetNextBatch();
        Say(HasNextBatch
            ? Lang.T("Pick X in the Windows Share window: the first 4 pictures and the text go in the post. Then share the next ones as a reply.")
            : Lang.T("Pick X in the Windows Share window: the pictures and the text go in the post."), keep: HasNextBatch);
    }

    /// <summary>Every ticked picture, in order, to whichever app is picked in Windows' Share window.</summary>
    [RelayCommand]
    private void ShareWindows()
    {
        var picks = Included;
        if (Watched is null || picks.Count == 0 || Application.Current?.MainWindow is not { } window) return;
        try
        {
            var (files, _) = SavePictures(picks);
            if (!WindowsShare.Show(window, ShareTitle, PostText, files)) Say(Lang.T("The Windows Share window couldn't open. Try Export instead."));
        }
        catch (Exception ex) { Log.Error("rewind", ex); Say(Lang.T("The pictures couldn't be shared. Try Export instead.")); }
    }

    /// <summary>The next four, for a reply under the post, in order.</summary>
    [RelayCommand]
    private void CopyNextBatch()
    {
        if (!ShareBatch(_xNext)) return;
        SetNextBatch();
        Say(Lang.T("Pick X again, and post these as a reply under your post."), keep: HasNextBatch);
    }

    [RelayCommand]
    private void PostReddit()
    {
        // A Reddit post takes up to 20 pictures: all the ticked ones, in order.
        var picks = Included;
        if (!CopyPictures(picks.Take(20).ToList())) return;
        Open("https://www.reddit.com/submit?type=IMAGE");
        Say(Lang.T("Reddit opened. The pictures are copied: paste them into the post."));
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("rewind", ex); }
    }

    /// <summary>The folder the pictures go in: Pictures › RigSight Rewind › October 2026.</summary>
    public string ExportFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RigSight Rewind",
        Watched is { } f ? string.Concat(RewindCards.TitleWithYear(f).Split(Path.GetInvalidFileNameChars())) : "Rewind");

    [ObservableProperty] private bool _isExporting;

    [RelayCommand]
    private async Task Export()
    {
        if (Watched is null || IncludedCount == 0) return;
        IsExporting = true;
        try
        {
            string folder = ExportFolder;
            Directory.CreateDirectory(folder);
            int n = 0;
            foreach (var p in Picks.Where(p => p.Included))
            {
                string name = $"{++n:00} {string.Concat(p.Name.Split(Path.GetInvalidFileNameChars()))}.png";
                RewindPainter.SavePng(PictureOf(p), Path.Combine(folder, name));
                await Task.Yield(); // the window keeps answering between pictures
            }
            _lastExport = folder;
            Say(n == 1 ? Lang.T($"1 picture saved to Pictures › RigSight Rewind › {Path.GetFileName(folder)}")
                : Lang.T($"{n} pictures saved to Pictures › RigSight Rewind › {Path.GetFileName(folder)}"), folder: true);
            Log.Write("rewind", $"Exported {n} pictures ({Shape}) to {folder}");
        }
        catch (Exception ex)
        {
            Log.Error("rewind", ex);
            Say(Lang.T("The pictures couldn't be saved."));
        }
        finally { IsExporting = false; }
    }

    private string? _lastExport;

    [RelayCommand]
    private void OpenFolder()
    {
        if (_lastExport is { } folder && Directory.Exists(folder)) Open(folder);
    }

    // ── A short message at the foot of the screen ────────────────────────
    [ObservableProperty] private string _toast = "";
    [ObservableProperty] private bool _toastHasFolder;
    public bool HasToast => Toast.Length > 0;
    partial void OnToastChanged(string value) => OnPropertyChanged(nameof(HasToast));
    private int _toastAsk;

    private async void Say(string text, bool folder = false, bool keep = false)
    {
        int ask = ++_toastAsk;
        Toast = text;
        ToastHasFolder = folder;
        if (!keep) NextBatchText = "";
        // With more pictures to copy it stays until they are, or Share closes.
        if (keep) return;
        await Task.Delay(folder ? 6000 : 3500);
        if (ask == _toastAsk) Toast = "";
    }

    // ── Settings → Rewind ────────────────────────────────────────────────
    public bool Month { get => S.Month; set { _settings.Update(s => s.Rewind.Month = value); OnPropertyChanged(); } }
    public bool Quarter { get => S.Quarter; set { _settings.Update(s => s.Rewind.Quarter = value); OnPropertyChanged(); } }
    public bool Half { get => S.Half && CanHalf; set { _settings.Update(s => s.Rewind.Half = value); OnPropertyChanged(); } }
    public bool Year { get => S.Year && CanYear; set { _settings.Update(s => s.Rewind.Year = value); OnPropertyChanged(); } }
    public bool CanQuarter => RewindStore.HistoryAllows(RewindKind.Quarter, KeepDays);
    public bool CanHalf => RewindStore.HistoryAllows(RewindKind.Half, KeepDays);
    public bool CanYear => RewindStore.HistoryAllows(RewindKind.Year, KeepDays);
    public bool HistoryTooShort => !CanYear;
    public string HistoryNote => !CanQuarter ? Lang.T("History is kept for less than 3 months, so only monthly rewinds can be made.")
        : !CanHalf ? Lang.T("History is kept for 3 months, so 6-month and yearly rewinds can’t be made.")
        : Lang.T("History is kept for less than a year, so yearly rewinds can’t be made.");

    public bool Time { get => S.Time; set { _settings.Update(s => s.Rewind.Time = value); OnPropertyChanged(); } }
    public bool Games { get => S.Games; set { _settings.Update(s => s.Rewind.Games = value); OnPropertyChanged(); } }
    public bool Internet { get => S.Internet; set { _settings.Update(s => s.Rewind.Internet = value); OnPropertyChanged(); } }
    public bool Heat { get => S.Heat; set { _settings.Update(s => s.Rewind.Heat = value); OnPropertyChanged(); } }
    public bool Changes { get => S.Changes; set { _settings.Update(s => s.Rewind.Changes = value); OnPropertyChanged(); } }
    public bool Problems { get => S.Problems; set { _settings.Update(s => s.Rewind.Problems = value); OnPropertyChanged(); } }
    public bool FullScreen { get => S.FullScreen; set { _settings.Update(s => s.Rewind.FullScreen = value); OnPropertyChanged(); } }
    public bool Notify { get => S.Notify; set { _settings.Update(s => s.Rewind.Notify = value); OnPropertyChanged(); } }

    /// <summary>Settings → History changed: what can be made, and what can be recreated, may have too.</summary>
    public void HistoryChanged()
    {
        OnPropertyChanged(nameof(Half), nameof(Year), nameof(CanQuarter), nameof(CanHalf), nameof(CanYear), nameof(HistoryTooShort), nameof(HistoryNote));
        foreach (var e in Entries) e.Refresh();
    }

    public async Task RecreateAsync(RewindEntry entry)
    {
        if (entry.Recreating || !entry.CanRecreate) return;
        entry.Recreating = true;
        var old = entry.Facts;
        var fresh = await _reports.QueryAsync((db, s) => RewindStore.Recreate(db, s, old, DateTime.Now));
        entry.Recreating = false;
        if (fresh is null) return;
        entry.Facts = fresh;
        entry.Recreated = true;
        if (Watched?.Id == fresh.Id) { Watched = fresh; Cards = RewindCards.Of(fresh); }
    }

    public void Remove(RewindEntry entry)
    {
        RewindStore.Remove(entry.Facts.Id);
        Entries.Remove(entry);
        OnPropertyChanged(nameof(HasEntries));
        RaiseWaiting();
    }

    // ── Never show in a rewind ───────────────────────────────────────────
    public sealed record LeaveOption(string Exe, string Name, string? Path, string Time, bool IsLeftOut);

    [ObservableProperty] private IReadOnlyList<LeaveOption> _leaveOptions = [];
    [ObservableProperty] private string _leaveSearch = "";
    [ObservableProperty] private bool _isPicking;
    private List<(string Exe, string Name, string? Path, double Sec)> _used = [];

    public IReadOnlyList<LeaveOption> LeftOut => [.. S.LeftOut.Select(exe =>
        _used.FirstOrDefault(u => string.Equals(u.Exe, exe, StringComparison.OrdinalIgnoreCase)) is { Exe: not null } u
            ? new LeaveOption(u.Exe, u.Name, u.Path, "", true)
            : new LeaveOption(exe, Path.GetFileNameWithoutExtension(exe), null, "", true))];
    public bool HasLeftOut => S.LeftOut.Count > 0;

    partial void OnLeaveSearchChanged(string value) => FilterLeave();

    /// <summary>The most used games and apps of the last month, for the picker.</summary>
    public async Task LoadUsedAsync()
    {
        var to = DateTime.Now;
        var report = await _reports.QueryAsync((db, s) => Core.Reports.ReportBuilder.BuildAppTotals(db, to.AddDays(-30), to,
            db.LoadApps().ToDictionary(a => a.Id), s));
        _used = report is null ? [] : [.. report.Apps.Where(a => a.ActiveSec >= 60).OrderByDescending(a => a.ActiveSec).Select(a => (a.Exe, a.Name, a.Path, a.ActiveSec))];
        FilterLeave();
        OnPropertyChanged(nameof(LeftOut));
    }

    private void FilterLeave()
    {
        var words = TextMatch.Words(LeaveSearch);
        LeaveOptions = [.. _used.Where(u => words.Length == 0 || TextMatch.Has(words, u.Name, u.Exe)).Take(30)
            .Select(u => new LeaveOption(u.Exe, u.Name, u.Path, Units.Duration(u.Sec), S.IsLeftOut(u.Exe)))];
    }

    [RelayCommand]
    private async Task TogglePicker()
    {
        IsPicking = !IsPicking;
        if (IsPicking && _used.Count == 0) await LoadUsedAsync();
    }

    [RelayCommand]
    private void LeaveOut(LeaveOption? o)
    {
        if (o is null || S.IsLeftOut(o.Exe)) return;
        _settings.Update(s => s.Rewind.LeftOut.Add(o.Exe));
        FilterLeave();
        OnPropertyChanged(nameof(LeftOut), nameof(HasLeftOut));
    }

    [RelayCommand]
    private void ShowAgain(LeaveOption? o)
    {
        if (o is null) return;
        _settings.Update(s => s.Rewind.LeftOut.RemoveAll(x => string.Equals(x, o.Exe, StringComparison.OrdinalIgnoreCase)));
        FilterLeave();
        OnPropertyChanged(nameof(LeftOut), nameof(HasLeftOut));
    }
}
