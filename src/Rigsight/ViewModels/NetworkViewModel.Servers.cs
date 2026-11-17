using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core;
using Rigsight.Core.Net;
using Rigsight.Core.Protocol;

namespace Rigsight.ViewModels;

// An app's ping in Using it now: only games, calls, remote play and apps picked with "Check its ping" have one.
public sealed partial class NetLiveRow
{
    [ObservableProperty] private string _pingText = "";
    [ObservableProperty] private string? _under;
    [ObservableProperty] private bool _pingWarm;
    [ObservableProperty] private string? _pingTip;
    public bool HasUnder => Under is not null;
    partial void OnUnderChanged(string? value) => OnPropertyChanged(nameof(HasUnder));

    /// <summary>Picked with "Check its ping" (its server is checked like a game's); offered only where it isn't checked anyway.</summary>
    [ObservableProperty] private bool _picked;
    [ObservableProperty] private bool _checkedAnyway;
    public bool CanPick => Picked || !CheckedAnyway;
    public string PickText => Picked ? Lang.T("Stop checking its ping") : Lang.T("Check its ping");
    partial void OnPickedChanged(bool value) { OnPropertyChanged(nameof(PickText)); OnPropertyChanged(nameof(CanPick)); }
    partial void OnCheckedAnywayChanged(bool value) => OnPropertyChanged(nameof(CanPick));

    internal Action<NetLiveRow>? OnPick { get; set; }

    [RelayCommand]
    private void Pick() => OnPick?.Invoke(this);
}

/// <summary>
/// The Right now card's top strip, one line (the user, 2026-11-16: version 2): the line's state, the way out read left to
/// right (your router, your provider, the server of the game or call that matters most, with its usual and lost checks),
/// and the connection's facts in grey at the end.
/// </summary>
public sealed partial class NetworkViewModel
{
    [ObservableProperty] private string _routerMs = "—";
    [ObservableProperty] private string _providerMs = "—";
    [ObservableProperty] private string _routerTip = "";
    [ObservableProperty] private string _providerTip = "";
    [ObservableProperty] private bool _hasFar;
    [ObservableProperty] private string _farName = "";
    [ObservableProperty] private string _farMs = "";
    [ObservableProperty] private bool _farWarm;
    [ObservableProperty] private string _farTip = "";

    /// <summary>After the server's figure: " · usual 90 to 100 ms" (empty while too soon) and ", 3 of the last 120 checks lost".</summary>
    [ObservableProperty] private string _farUsual = "";
    [ObservableProperty] private string _farLost = "";
    /// <summary>The figure is well above its usual (amber); apart from <see cref="FarLosing"/>, so a loss alone doesn't colour a usual figure.</summary>
    [ObservableProperty] private bool _farMsWarm;
    [ObservableProperty] private bool _farLosing;

    /// <summary>The line's end when there's no figure to show: "No games or calls online.", the no-admin note, a server that doesn't answer.</summary>
    [ObservableProperty] private string _restText = "";
    public bool HasRest => RestText.Length > 0;
    partial void OnRestTextChanged(string value) => OnPropertyChanged(nameof(HasRest));

    [ObservableProperty] private string _vpnTag = "—";
    [ObservableProperty] private string _jitterTag = "";
    [ObservableProperty] private string _lossTag = "";
    [ObservableProperty] private bool _hasLossTag;

    /// <summary>The strip and each row's ping, from the last tick's line (on the window's thread).</summary>
    internal void UpdateServers(LineLive? line)
    {
        bool on = PingChecksOn;
        var servers = on ? line?.Servers : null;
        RouterMs = on && line?.LanMs is int lan ? $"{lan} ms" : "—";
        ProviderMs = on && line?.IspMs is int isp ? $"{isp} ms" : on && line is { ProviderFound: false } ? Lang.T("No answer") : "—";
        RouterTip = Lang.T("Your router, the first stop out of this PC");
        ProviderTip = line is { ProviderFound: false } ? Lang.T("Your internet provider's first router didn't answer, so only the home side is checked")
            : Lang.T("Your internet provider's first router, where the internet starts");
        JitterTag = Lang.T($"Jitter {JitterNow}");
        LossTag = Lang.T($"Packet loss {LostNow}");
        HasLossTag = on && line is { LostPercent: > 0 };
        VpnTag = Vpn == Lang.T("Off") ? Lang.T("VPN off") : Lang.T($"Through {Vpn}");

        var picked = _settings?.Current.Tracking.PingApps ?? [];
        foreach (var row in UsingNow)
        {
            var s = servers?.FirstOrDefault(x => x.Exe.Equals(row.Exe, StringComparison.OrdinalIgnoreCase));
            row.OnPick ??= TogglePing;
            row.Picked = picked.Contains(row.Exe, StringComparer.OrdinalIgnoreCase);
            row.CheckedAnyway = s is not null && s.Kind != "picked";
            row.PingText = s is null ? "" : ServerWords.Figure(s);
            row.Under = s is null ? null : ServerWords.Under(s);
            row.PingWarm = s is not null && ServerWords.NeedsLook(s);
            row.PingTip = s is null ? null : ServerWords.Tip(row.Name, s);
        }

        var lead = ServerWords.Lead(servers);
        HasFar = lead is not null;
        if (lead is not null)
        {
            string app = NameOf(lead.Exe);
            FarName = lead.Place is null ? app : Lang.T($"{app}, {lead.Place}");
            FarMs = ServerWords.Figure(lead);
            FarWarm = ServerWords.NeedsLook(lead);
            FarTip = ServerWords.Tip(app, lead) + (lead.Ms is int ms && line?.IspMs is int p
                ? "\n" + Lang.T($"Your router and your provider take {p} ms of it, the rest is the way from your provider to the server.") : "");
            FarMsWarm = lead.Ms is int now && ServerWords.AboveUsual(now, lead.UsualHigh);
            FarLosing = ServerWords.Losing(lead);
            if (lead.NoAnswer)
            {
                (FarUsual, FarLost) = ("", "");
                RestText = Lang.T($"{ServerOf(app, lead)} doesn't answer checks, so its ping can't be shown.");
            }
            else
            {
                FarUsual = lead.UsualLow is int lo && lead.UsualHigh is int hi ? "  ·  " + Lang.T($"usual {lo} to {hi} ms") : "";
                FarLost = (FarUsual.Length > 0 ? ", " : "  ·  ") + (lead.Lost == 0 ? Lang.T($"none of the last {lead.Sent} checks lost") : Lang.T($"{lead.Lost} of the last {lead.Sent} checks lost"));
                RestText = "";
            }
        }
        else
        {
            (FarUsual, FarLost, FarMsWarm, FarLosing) = ("", "", false, false);
            RestText = !on ? ""
                : !HasLive ? Lang.T("Only your router and your provider, since RigSight is running without admin rights.")
                : _settings?.Current.Tracking.ServerPings != false ? Lang.T("No games or calls online.")
                : "";
        }
    }

    /// <summary>"Check its ping" on a row: the app's server is checked like a game's from now on, or no longer.</summary>
    private void TogglePing(NetLiveRow row)
    {
        bool add = !row.Picked;
        _settings?.Update(s =>
        {
            s.Tracking.PingApps.RemoveAll(x => x.Equals(row.Exe, StringComparison.OrdinalIgnoreCase));
            if (add) s.Tracking.PingApps.Add(row.Exe);
        });
        row.Picked = add;
    }

    private static string ServerOf(string app, ServerLive s) => ServerWords.ServerOf(app, s.Place);

    /// <summary>The name the list shows for an exe (its row's), else the exe without ".exe".</summary>
    private string NameOf(string exe) =>
        UsingNow.FirstOrDefault(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)) is { Name.Length: > 0 } row ? row.Name
            : exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
}
