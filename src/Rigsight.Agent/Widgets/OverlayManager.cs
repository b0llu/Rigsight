using System.Diagnostics;
using Rigsight.Agent.Native;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

/// <summary>
/// Shows and hides the overlay with its keyboard shortcut, and keeps it up to date. The overlay is
/// our own always-on-top window, or the same readings drawn inside exclusive-fullscreen games by
/// RivaTuner (see <see cref="Refresh"/>). UI thread only.
/// </summary>
internal sealed class OverlayManager : IDisposable
{
    private readonly GlobalHotkey _hotkey = new();
    private readonly ToolStripMenuItem _trayItem = new("Show overlay");
    private readonly bool _isAdmin;
    private OverlayForm? _form;
    private OverlaySettings _settings = new();
    private WidgetData? _data;
    private string? _registered;
    private bool _rtssWritten, _rtssStartTried, _installing;
    private string? _installResult;
    private readonly HashSet<string> _warnedApps = new(StringComparer.OrdinalIgnoreCase);

    public OverlayManager(bool isAdmin)
    {
        _isAdmin = isAdmin;
        _hotkey.Pressed += Toggle;
        _trayItem.Click += (_, _) => Toggle();
    }

    public bool Visible { get; private set; }

    /// <summary>Another program already owns the shortcut, so pressing it won't reach us.</summary>
    public bool HotkeyTaken { get; private set; }

    /// <summary>Raised when the overlay appears or disappears, the shortcut can't be used, or RivaTuner's state changes.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raised (with the app's name) when the overlay was turned on over a game in exclusive fullscreen
    /// that neither our window nor RivaTuner can reach.
    /// </summary>
    public event Action<string>? CantReachGame;

    /// <summary>The tray menu entry ("Show overlay   Alt+Shift+O").</summary>
    public ToolStripMenuItem TrayItem => _trayItem;

    /// <summary>running · stopped · missing · installing · install-failed</summary>
    public string RtssState =>
        _installing ? "installing"
        : Rtss.IsRunning() ? "running"
        : Rtss.FindExe() is not null ? "stopped"
        : _installResult ?? "missing";

    public void Apply(OverlaySettings settings)
    {
        _settings = settings;
        string? wanted = settings.Enabled ? settings.Hotkey : null;
        if (wanted != _registered)
        {
            _registered = wanted;
            Hotkey? key = Hotkey.TryParse(wanted, out var parsed) ? parsed : null;
            bool taken = !_hotkey.Register(key);
            if (taken) Log.Write("overlay", $"Shortcut {wanted} is already used by another program");
            if (taken != HotkeyTaken)
            {
                HotkeyTaken = taken;
                StateChanged?.Invoke();
            }
        }
        _trayItem.ShortcutKeyDisplayString = settings.Enabled ? settings.Hotkey : null;

        if (Visible) Refresh();
    }

    public void Toggle() => SetVisible(!Visible);

    public void SetVisible(bool visible)
    {
        if (visible == Visible) return;
        Visible = visible;
        _trayItem.Checked = visible;
        if (visible)
        {
            Refresh();
            WarnIfUnreachable();
        }
        else
        {
            _form?.Hide();
            ClearRtss();
        }
        StateChanged?.Invoke();
    }

    public void Update(WidgetData data)
    {
        _data = data;
        if (Visible) Refresh();
    }

    /// <summary>
    /// One overlay at a time: RivaTuner inside an exclusive-fullscreen game in front (where no window can
    /// appear), our own window everywhere else (desktop, windowed and borderless games, or the fullscreen
    /// game once it's minimised). Checked on every update, so switching follows Alt+Tab within a second.
    /// </summary>
    private void Refresh()
    {
        if (IsExclusiveFullscreen())
        {
            _rtssWritten = Rtss.Show(WidgetRenderer.RtssText(_settings, _data));
            if (_form is { Visible: true }) _form.Hide();
            return;
        }

        ClearRtss();
        _form ??= new OverlayForm();
        if (!_form.Visible) _form.Show();
        _form.Redraw(_settings, _data);
    }

    private void ClearRtss()
    {
        if (!_rtssWritten) return;
        Rtss.Clear();
        _rtssWritten = false;
    }

    /// <summary>A Direct3D game is running in exclusive fullscreen in front (not borderless, not minimised).</summary>
    private static bool IsExclusiveFullscreen() =>
        Win32.SHQueryUserNotificationState(out int state) == 0 && state == Win32.QUNS_RUNNING_D3D_FULL_SCREEN;

    private static int ForegroundPid()
    {
        Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out int pid);
        return pid;
    }

    /// <summary>Exclusive fullscreen and RivaTuner isn't in the game: nothing can show. Say so (once per app and run).</summary>
    private void WarnIfUnreachable()
    {
        if (!IsExclusiveFullscreen()) return;
        if (Rtss.IsHooked(ForegroundPid())) return;
        string app = _data?.Activity.Name ?? "This game";
        if (_warnedApps.Add(app)) CantReachGame?.Invoke(app);
    }

    /// <summary>
    /// Fullscreen games only show the overlay through RivaTuner, and it only reaches games started after
    /// it, so keep it running (it sits quietly in the tray). Tried once per agent run unless asked again
    /// (<see cref="StartRtss"/>); needs admin, as RTSS itself does.
    /// </summary>
    public void EnsureRtssRunning()
    {
        if (!_isAdmin || _rtssStartTried) return;
        var exe = Rtss.FindExe();
        if (exe is null || Rtss.IsRunning()) return;
        _rtssStartTried = true;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
            Log.Write("overlay", "Started RivaTuner");
        }
        catch (Exception ex)
        {
            Log.Error("overlay", ex);
        }
        StateChanged?.Invoke();
    }

    /// <summary>The Overlay page's "Start RivaTuner" button.</summary>
    public void StartRtss()
    {
        _rtssStartTried = false;
        EnsureRtssRunning();
        StateChanged?.Invoke();
    }

    /// <summary>Installs RivaTuner with winget (the agent already has admin rights), then starts it.</summary>
    public void InstallRtss(SynchronizationContext ui)
    {
        if (_installing || Rtss.FindExe() is not null) return;
        _installing = true;
        _installResult = null;
        StateChanged?.Invoke();
        Task.Run(() =>
        {
            bool ok = false;
            try
            {
                using var p = Process.Start(new ProcessStartInfo("winget",
                    "install --id Guru3D.RTSS -e --silent --accept-package-agreements --accept-source-agreements")
                { CreateNoWindow = true, UseShellExecute = false });
                ok = p is not null && p.WaitForExit(TimeSpan.FromMinutes(5)) && Rtss.FindExe() is not null;
            }
            catch (Exception ex)
            {
                Log.Error("overlay", ex);
            }
            Log.Write("overlay", ok ? "Installed RivaTuner" : "Couldn't install RivaTuner");
            ui.Post(_ =>
            {
                _installing = false;
                _installResult = ok ? null : "install-failed";
                _rtssStartTried = false;
                EnsureRtssRunning();
                StateChanged?.Invoke();
            }, null);
        });
    }

    /// <summary>Saves a picture of the overlay (with current readings) for the app's Overlay page.</summary>
    public void RenderPreview(string dir)
    {
        using var bmp = WidgetRenderer.RenderOverlay(_settings, _data, 2f);
        bmp.Save(Path.Combine(dir, "Overlay.png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    public void Dispose()
    {
        ClearRtss();
        _hotkey.Dispose();
        _form?.Close();
        _form?.Dispose();
    }
}
