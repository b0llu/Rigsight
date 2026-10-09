using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Reports;
using Rigsight.Services;

namespace Rigsight.ViewModels;

/// <summary>An app and how much it used today, for a dashboard's list.</summary>
public sealed record NetTopRow(string Name, string? Path, string Amount, string DotKey);

/// <summary>
/// The internet for dashboards: right now (the speeds, who's using it) and today (everything used, the apps that used
/// the most, each hour's use, drops). One for the whole app; the tiles of every dashboard read from it. Today's part is
/// read when a dashboard with an internet tile is opened or refreshed, the live part follows the agent's readings.
/// </summary>
public sealed partial class NetTodayViewModel : ObservableObject
{
    /// <summary>The most apps a dashboard's list shows.</summary>
    internal const int TopApps = 6;

    private readonly ReportService _reports;
    private int _load;

    public NetTodayViewModel(ReportService reports, LiveData live)
    {
        _reports = reports;
        Live = live;
        live.PropertyChanged += OnLiveChanged;
    }

    public LiveData Live { get; }

    // ── Right now ──

    /// <summary>Whether the agent is reading each app's network use (it does with admin rights).</summary>
    [ObservableProperty] private bool _hasLive;
    [ObservableProperty] private string _downNow = "—";
    [ObservableProperty] private string _upNow = "—";
    public ObservableCollection<NetLiveRow> UsingNow { get; } = [];
    [ObservableProperty] private bool _nobodyNow;

    private void OnLiveChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveData.Tick)) UpdateLive();
    }

    private void UpdateLive()
    {
        var net = Live.Net;
        HasLive = net is not null && !net.Stalled;
        if (net is null) return;
        DownNow = Units.Speed(net.Down);
        UpNow = Units.Speed(net.Up);
        var apps = net.Apps.Where(a => a.Down + a.Up >= 1024).Take(TopApps).ToList();
        // Rows kept where they are while the same apps stay busy (no flicker each second).
        if (!apps.Select(a => a.Exe).SequenceEqual(UsingNow.Select(r => r.Exe)))
        {
            UsingNow.Clear();
            foreach (var a in apps) UsingNow.Add(new NetLiveRow { Exe = a.Exe });
        }
        for (int i = 0; i < apps.Count; i++)
        {
            var (row, a) = (UsingNow[i], apps[i]);
            row.Name = a.Name;
            row.Path = a.Path;
            row.DownText = "↓ " + Units.Speed(a.Down);
            row.UpText = "↑ " + Units.Speed(a.Up);
        }
        NobodyNow = UsingNow.Count == 0;
    }

    // ── Today ──

    /// <summary>Anything recorded today.</summary>
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _totalText = "—";
    [ObservableProperty] private string _downText = "—";
    [ObservableProperty] private string _upText = "—";
    [ObservableProperty] private string _backgroundText = "—";
    [ObservableProperty] private IReadOnlyList<NetTopRow> _top = [];
    [ObservableProperty] private IReadOnlyList<NetBin> _bins = [];
    [ObservableProperty] private IReadOnlyList<long> _colorApps = [];
    [ObservableProperty] private IReadOnlyDictionary<long, string> _appNames = new Dictionary<long, string>();
    [ObservableProperty] private string _dropsTitle = "—";
    [ObservableProperty] private string _dropsNote = "";

    public async Task RefreshAsync()
    {
        int id = ++_load;
        var (report, _) = await _reports.NetworkAsync(ReportRange.Day, DateTime.Today);
        if (id != _load) return;
        UpdateLive();
        HasData = report is { HasData: true };
        if (report is null || !report.HasData)
        {
            (TotalText, DownText, UpText, BackgroundText) = ("—", "—", "—", "—");
            Top = [];
            Bins = [];
            (DropsTitle, DropsNote) = ("—", "");
            return;
        }
        TotalText = Units.Data(report.Down + report.Up);
        DownText = Units.Data(report.Down);
        UpText = Units.Data(report.Up);
        BackgroundText = Units.Data(report.Background);
        var colors = report.ColorApps;
        string Dot(long app) => colors.IndexOf(app) is int i and >= 0 && i < Controls.NetBarsChart.AppBrushKeys.Length
            ? Controls.NetBarsChart.AppBrushKeys[i] : Controls.NetBarsChart.OtherBrushKey;
        Top = Models.Kept.Or(Top, [.. report.Apps.Where(a => a.Use.Total >= 1 << 20).Take(TopApps)
            .Select(a => new NetTopRow(a.Name, a.Path, Units.Data(a.Use.Total), Dot(a.Id)))]);
        Bins = Models.Kept.Or(Bins, report.Bins);
        ColorApps = Models.Kept.Or(ColorApps, colors);
        var names = report.Apps.ToDictionary(a => a.Id, a => a.Name);
        if (!names.OrderBy(kv => kv.Key).SequenceEqual(AppNames.OrderBy(kv => kv.Key))) AppNames = names;
        (DropsTitle, DropsNote) = NetworkViewModel.DropsText(report, "today");
    }
}
