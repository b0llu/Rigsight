using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Notices;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>One notification in the recent list: its words, when, its kind's icon and colour, where it opens, and whether it's read.</summary>
public sealed partial class NoticeRow(NoticeRecord n, bool unseen) : ObservableObject
{
    public long Id => n.Id;
    public string Title => n.Title;
    public string Body => n.Body;
    public string Time => TimeText.At(n.Time, "h:mm tt");
    /// <summary>Not read yet: a dot beside it until it's opened or everything is marked as read.</summary>
    [ObservableProperty] private bool _unseen = unseen;
    /// <summary>Held back (past the day's limit, or while Windows said not to disturb): only ever here.</summary>
    public bool HeldBack => !n.Shown;
    public string? Page => n.Page;
    public string? Arg => n.Arg;
    public string Glyph => NoticeLook.Glyph(n.Kind);
    public string BrushKey => NoticeLook.BrushKey(n.Kind, n.Level);
}

/// <summary>A day of the recent list: "Today", "Yesterday", "Wednesday", "3 November".</summary>
public sealed record NoticeDay(string Title, IReadOnlyList<NoticeRow> Rows);

/// <summary>A kind's icon and colour in the list, the colour of its card's edge.</summary>
public static class NoticeLook
{
    public static string Glyph(string kind) => kind switch
    {
        "Alert" or "Slowed" => "\uE9CA",
        "Crash" => "\uE7BA",
        "Internet" or "InternetBack" => "\uE774",
        "FanStopped" or "Fans" => "\uEDA8",
        "DriveFailing" or "Drive" or "Space" => "\uEDA2",
        "Usual" => "\uE8AB",
        "Change" => "\uE823",
        "Recap" or "Weekly" => "\uE787",
        "Session" => "\uE7FC",
        "Rewind" => "\uE768",
        "Memory" => "\uE964",
        "Startup" => "\uE7E8",
        _ => "\uE896",
    };

    public static string BrushKey(string kind, NoticeLevel level) => kind switch
    {
        "Alert" or "FanStopped" or "DriveFailing" => "HotBrush",
        "Session" or "InternetBack" => "GoodBrush",
        "Crash" or "Internet" => "WarmBrush",
        _ => level switch { NoticeLevel.Worth => "WarmBrush", NoticeLevel.Recap => "CoolBrush", NoticeLevel.Problem => "HotBrush", _ => "PurpleBrush" },
    };
}

/// <summary>
/// The bell in the title bar and its list (the notifications round, 2026-11-07): every notification of the last 30 days,
/// newest first by day, the ones held back too; the bell counts those newer than the list was last opened on. Read from
/// the history the agent keeps, again whenever it says a new one came ("notice").
/// </summary>
public sealed partial class NoticesViewModel(ReportService reports, SettingsModel settings) : ObservableObject
{
    public ObservableCollection<NoticeDay> Days { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUnseen), nameof(UnseenText))] private int _unseen;
    public bool HasUnseen => Unseen > 0;
    public string UnseenText => Unseen > 9 ? "9+" : Unseen.ToString();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty))] private bool _loaded;
    public bool IsEmpty => Loaded && Days.Count == 0;

    [ObservableProperty] private bool _isOpen;

    /// <summary>A row clicked: its page (the shell's Navigate), as its card would have.</summary>
    public Action<string?, string?>? Open { get; set; }
    /// <summary>"Settings ›" in the list's foot: Settings, on Notifications.</summary>
    public Action? OpenSettings { get; set; }

    private long _newest;

    public async Task LoadAsync()
    {
        var list = await reports.QueryAsync((db, _) => db.GetNotices(TimeUtil.ToUnix(DateTime.Now - RigsightDb.NoticesKept))) ?? [];
        _newest = list.Count > 0 ? list.Max(n => n.Id) : 0;
        var today = DateTime.Today;
        Days.Clear();
        foreach (var g in list.GroupBy(n => n.Time.Date))
            Days.Add(new NoticeDay(DayTitle(g.Key, today), [.. g.Select(n => new NoticeRow(n, IsUnread(n.Id)))]));
        Count();
        Loaded = true;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool IsUnread(long id) => id > settings.Current.NoticesSeen && !settings.Current.NoticesRead.Contains(id);

    private void Count() => Unseen = Days.SelectMany(d => d.Rows).Count(r => r.Unseen);

    internal static string DayTitle(DateTime day, DateTime today)
    {
        var names = Lang.Translating ? System.Globalization.CultureInfo.GetCultureInfo(Lang.Code) : System.Globalization.CultureInfo.InvariantCulture;
        return (today - day).Days switch
        {
            0 => Lang.T("Today"),
            1 => Lang.T("Yesterday"),
            < 7 => Wording.Capital(day.ToString("dddd", names)),
            _ => day.ToString("d MMMM", names),
        };
    }

    /// <summary>The bell: opens or closes the list. Opening it reads nothing by itself: a notification is read once it's opened, or with Mark all as read.</summary>
    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    /// <summary>"Mark all as read": every notification in the list, and the bell's number gone.</summary>
    [RelayCommand]
    private void MarkAllRead()
    {
        foreach (var row in Days.SelectMany(d => d.Rows)) row.Unseen = false;
        Unseen = 0;
        long newest = _newest;
        settings.Update(s => { if (newest > s.NoticesSeen) s.NoticesSeen = newest; s.NoticesRead.Clear(); });
    }

    [RelayCommand]
    private void OpenRow(NoticeRow row)
    {
        IsOpen = false;
        if (row.Unseen)
        {
            row.Unseen = false;
            Count();
            // Kept as its id, and forgotten once Mark all as read reaches past it.
            settings.Update(s => { s.NoticesRead.RemoveAll(id => id <= s.NoticesSeen); if (!s.NoticesRead.Contains(row.Id)) s.NoticesRead.Add(row.Id); });
        }
        Open?.Invoke(row.Page, row.Arg);
    }

    [RelayCommand]
    private void GoToSettings()
    {
        IsOpen = false;
        OpenSettings?.Invoke();
    }
}
