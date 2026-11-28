using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Stability;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>A driver line's parts (1.22.0, "Drivers inside Timeline"): the maker's square, the versions before and after, and the device it opens.</summary>
public sealed partial class TimelineEntry
{
    /// <summary>The device this driver line opens inside Timeline (null: a line of a driver the history doesn't follow).</summary>
    public string? DriverKey { get; init; }

    /// <summary>A driver line with its versions ("621.01 → 621.24"): drawn with the maker's square in place of the kind's icon.</summary>
    public bool IsDriverLine { get; init; }

    /// <summary>The version before, muted ("" for none).</summary>
    public string VersionFrom { get; init; } = "";
    /// <summary>The version it went to, bold ("" for none).</summary>
    public string VersionTo { get; init; } = "";
    /// <summary>The arrow between them, only when there's a version before.</summary>
    public string VersionArrow => VersionFrom.Length > 0 ? "  →  " : "";

    /// <summary>What a screen reader says for a driver line: "NVIDIA graphics driver 621.01 → 621.24".</summary>
    public string SpokenDriver => string.Join(' ', new[] { Title, VersionFrom, VersionArrow.Trim(), VersionTo }.Where(s => s.Length > 0));

    public Models.MakerSquare? Maker { get; init; }
    public bool CanOpenDriver => DriverKey is not null;
}

public sealed partial class TimelineViewModel
{
    /// <summary>Every device's driver and each version it has had; null before the agent's first look (the lines read as they always did).</summary>
    private DriverHistory? _driverHistory;

    /// <summary>The driver lines the history knows, by their change (the same object the list and Before and after hold).</summary>
    private Dictionary<SystemChange, DriverChangeLine> _driverLines = new(ReferenceEqualityComparer.Instance);

    /// <summary>The changes with the history's driver lines in them (see <see cref="DriverHistory.WithChanges"/>).</summary>
    private List<SystemChange> WithDrivers(TimelineData data)
    {
        _driverHistory = data.Drivers is { HasRecord: true } h ? h : null;
        if (_driverHistory is null)
        {
            _driverLines = new(ReferenceEqualityComparer.Instance);
            return data.Changes;
        }
        var (changes, lines) = _driverHistory.WithChanges(data.Changes);
        _driverLines = lines;
        return changes;
    }

    private DriverChangeLine? DriverLineOf(SystemChange c) => _driverLines.GetValueOrDefault(c);

    /// <summary>A change as one text: a driver line with its versions ("NVIDIA High Definition Audio 1.4.6.3 → 1.4.6.6"), the rest as shown.</summary>
    private string ShownOf(SystemChange c) => DriverLineOf(c)?.Text ?? c.Shown;

    /// <summary>A driver line: the device, the version before muted and the new one bold, the whole line opening that driver.</summary>
    private TimelineEntry? DriverEntryOf(SystemChange c, string when, string? tip)
    {
        if (DriverLineOf(c) is not { } line) return null;
        return new TimelineEntry
        {
            TimeText = when, TimeTip = tip, Icon = "", Title = line.Title, IsKey = true, IsDriverLine = true,
            VersionFrom = line.Was ?? "", VersionTo = line.Version ?? "", DriverKey = line.DeviceKey, Maker = Models.MakerSquare.Of(line.Maker),
        };
    }

    /// <summary>A month's folded driver line: its device and versions, its date, and the driver it opens.</summary>
    private TimelineEntry? MonthDriverChild(SystemChange c) =>
        DriverLineOf(c) is { } line ? new TimelineEntry { Title = line.Text, Detail = $"{c.Time:d MMM}", DriverKey = line.DeviceKey } : null;

    // ── This PC now ─────────────────────────────────────────────────────

    /// <summary>The graphics card's own driver, which This PC now's graphics driver line opens.</summary>
    private static string? GraphicsOpens(TimelineData data) =>
        data.Drivers is { HasRecord: true } h && h.GraphicsCard is { } card ? DriverPrefix + card.Key : null;

    /// <summary>"Drivers · 30, 9 changed in the last 90 days": every driver from a device maker, opening the every-driver list.</summary>
    private static NowFact? DriversFact(TimelineData data)
    {
        if (data.Drivers is not { HasRecord: true } h) return null;
        int recent = h.Devices.Count(d => !d.Inbox && h.ChangedRecently(d));
        string since = recent == 0 ? Lang.T("None changed in the last 90 days") : recent == 1 ? Lang.T("1 changed in the last 90 days") : Lang.T($"{recent} changed in the last 90 days");
        return new NowFact(Lang.T("Drivers"), h.FromMakers.Count().ToString("N0"), since) { Opens = AllDrivers, HasArrow = true };
    }

    private const string DriverPrefix = "driver:", AllDrivers = "drivers";

    // ── The views inside Timeline ───────────────────────────────────────

    /// <summary>One driver opened (from a Timeline line, This PC now or the every-driver list): only that driver, in place of the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListShown), nameof(IsDriverShown), nameof(IsEveryDriverShown))]
    private Models.DriverPage? _oneDriver;

    /// <summary>Every driver on this PC, opened from This PC now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListShown), nameof(IsDriverShown), nameof(IsEveryDriverShown))]
    private Models.EveryDriverPage? _everyDriver;

    public bool IsDriverShown => Opened is null && OneDriver is not null;
    public bool IsEveryDriverShown => Opened is null && OneDriver is null && EveryDriver is not null;
    private bool IsDriverViewOpen => OneDriver is not null || EveryDriver is not null;

    /// <summary>Raised when a driver view opens or goes back, to scroll it to the top.</summary>
    public event Action? DriverViewChanged;

    [RelayCommand]
    private void OpenDriverEntry(TimelineEntry entry)
    {
        if (entry.DriverKey is { } key) ShowDriver(key, fromList: false);
    }

    [RelayCommand]
    private void OpenNowFact(NowFact fact)
    {
        if (fact.Opens == AllDrivers) ShowEveryDriver();
        else if (fact.Opens is { } opens && opens.StartsWith(DriverPrefix, StringComparison.Ordinal)) ShowDriver(opens[DriverPrefix.Length..], fromList: false);
    }

    [RelayCommand]
    private void OpenDriverRow(Models.DriverListRow row) => ShowDriver(row.Key, fromList: true);

    /// <summary>Back: from a driver opened from the list to the list, else to the Timeline.</summary>
    [RelayCommand]
    private void DriverBack()
    {
        if (OneDriver is { FromList: true } && EveryDriver is not null) OneDriver = null;
        else CloseDrivers();
        DriverViewChanged?.Invoke();
    }

    /// <summary>Back to the Timeline's list from any driver view (also the sidebar's Timeline).</summary>
    [RelayCommand]
    private void CloseDrivers()
    {
        OneDriver = null;
        EveryDriver = null;
    }

    [RelayCommand]
    private void OpenDriverResult()
    {
        if (OneDriver?.Result is { } r) OpenChange(r.Day);
    }

    /// <summary>Windows' Device Manager, through the same list of Windows screens the AI's buttons and What to try use.</summary>
    [RelayCommand]
    private void OpenDeviceManager()
    {
        string target = Core.Ai.AiActions.Screen("device-manager")?.Uri ?? "devmgmt.msc";
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Write("app", $"Expected Device Manager to open, found {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Opens one driver from another page (the GPU page's "Every version", 1.23.0); false when the history doesn't have it.</summary>
    public bool OpenDriver(string key)
    {
        if (_driverHistory?.Find(key) is null) return false;
        Opened = null;
        ShowDriver(key, fromList: false);
        return true;
    }

    private void ShowDriver(string key, bool fromList)
    {
        if (_driverHistory?.Find(key) is not { } device) return;
        OneDriver = new Models.DriverPage(device, _driverHistory.Now, ResultOf(device), fromList);
        DriverViewChanged?.Invoke();
    }

    private void ShowEveryDriver()
    {
        if (_driverHistory is null) return;
        OneDriver = null;
        EveryDriver = new Models.EveryDriverPage(_driverHistory);
        DriverViewChanged?.Invoke();
    }

    /// <summary>
    /// What this driver's newest change made better or worse, where Before and after has one (only a big change has: a
    /// graphics driver). Only the newest change's own: an older version's result shown on the version now read as "after
    /// this driver" when it was after the one before.
    /// </summary>
    private Core.Reports.ChangeResult? ResultOf(DriverDevice device) =>
        _driverLines.Where(p => p.Value.DeviceKey is { } k && k.Equals(device.Key, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key).OrderByDescending(c => c.Time).FirstOrDefault() is { } newest
            && _results.TryGetValue(newest.Time.Date, out var r) && r.Changes.Contains(newest) ? r : null;

    /// <summary>New readings: an open driver view shows them (or closes when its device is no longer on record).</summary>
    private void RefreshDriverViews()
    {
        if (_driverHistory is null)
        {
            CloseDrivers();
            return;
        }
        if (EveryDriver is not null) EveryDriver = new Models.EveryDriverPage(_driverHistory) { RecentOnly = EveryDriver.RecentOnly, Search = EveryDriver.Search };
        if (OneDriver is { } open)
            OneDriver = _driverHistory.Find(open.Device.Key) is { } device ? new Models.DriverPage(device, _driverHistory.Now, ResultOf(device), open.FromList) : null;
    }
}
