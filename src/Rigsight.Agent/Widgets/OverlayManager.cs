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
    private readonly InGameNotice _notice = new();

    public OverlayManager(bool isAdmin)
    {
        _isAdmin = isAdmin;
        // Text left in RivaTuner by a previous run that didn't exit cleanly would stay frozen in every game.
        Rtss.Clear();
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
    /// One overlay at a time: RivaTuner inside a game in front (see <see cref="InGameRtssReaches"/>),
    /// our own window everywhere else (desktop, apps, or the game once it's minimised).
    /// Checked on every update, so switching follows Alt+Tab within a second.
    /// </summary>
    private void Refresh()
    {
        if (_data is not null)
            _data.Frame = _settings.Metrics.Any(m => m is OverlayMetric.Fps or OverlayMetric.FrameTime or OverlayMetric.OnePercentLow)
                ? Rtss.ReadFrameStats(ForegroundPid()) : null;

        if (InGameRtssReaches())
        {
            int pid = ForegroundPid();
            _rtssWritten = Rtss.Show(WidgetRenderer.RtssText(_settings, _data, Rtss.Resolution(pid) ?? ForegroundScreen().Size));
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

    /// <summary>
    /// The overlay belongs inside the app in front, drawn by RivaTuner: always in exclusive fullscreen (no window
    /// can appear there), and in any game RivaTuner is in, windowed, borderless or "fullscreen" (anything that fills
    /// the screen, or an app in the Games category). A window on top of a game makes Windows compose every frame:
    /// later on screen, and G-Sync/FreeSync stop. Everything else (desktop, apps) gets our own window.
    /// </summary>
    private bool InGameRtssReaches()
    {
        if (IsExclusiveFullscreen()) return true;
        if (!Rtss.IsHooked(ForegroundPid())) return false;
        return _data?.Activity.Category == AppCategory.Game || Tracking.ActivityMonitor.IsFullscreen(Win32.GetForegroundWindow());
    }

    /// <summary>A Direct3D game is running in exclusive fullscreen in front (not borderless, not minimised).</summary>
    private static bool IsExclusiveFullscreen() =>
        Win32.SHQueryUserNotificationState(out int state) == 0 && state == Win32.QUNS_RUNNING_D3D_FULL_SCREEN;

    /// <summary>The screen (in pixels) of whatever is in front.</summary>
    internal static System.Drawing.Rectangle ForegroundScreen() => ScreenOf(Win32.GetForegroundWindow());

    internal static System.Drawing.Rectangle ScreenOf(IntPtr window)
    {
        IntPtr monitor = Win32.MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        return Win32.GetMonitorInfo(monitor, ref info)
            ? System.Drawing.Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom)
            : Screen.PrimaryScreen!.Bounds;
    }

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
            if (RtssSetup.InTrustedFolder(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
                Log.Write("overlay", "Started RivaTuner");
            }
            else
            {
                // Outside Program Files: through Explorer, with the user's rights (Windows asks if RivaTuner wants admin).
                Process.Start("explorer.exe", $"\"{exe}\"")?.Dispose();
                Log.Write("overlay", $"Started RivaTuner from {exe} with the user's rights (not in Program Files)");
            }
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
        if (_installing || RtssSetup.IsInstalled()) return;
        _installing = true;
        _installResult = null;
        StateChanged?.Invoke();
        Task.Run(() =>
        {
            bool ok = false;
            try
            {
                // As long as it makes progress (see RtssSetup.Install); a question from its installer comes to the front.
                ok = RtssSetup.Install() is RtssInstallResult.Installed or RtssInstallResult.AlreadyInstalled;
            }
            catch (Exception ex)
            {
                Log.Error("overlay", ex);
            }
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

    /// <summary>
    /// A notice while a game may be in front: inside a game RivaTuner is in, it's drawn by RivaTuner (no
    /// window on top of the game); in exclusive fullscreen without RivaTuner it can't be seen; anywhere else the usual card shows.
    /// </summary>
    public InGame ShowInGame(Ui.Notice notice, int seconds)
    {
        if (!InGameRtssReaches()) return InGame.NotNeeded;
        if (Rtss.IsHooked(ForegroundPid()) && _notice.Show(notice, _settings, Visible, seconds)) return InGame.Shown;
        // A borderless game can still show the usual card; exclusive fullscreen can't show anything.
        return IsExclusiveFullscreen() ? InGame.Unreachable : InGame.NotNeeded;
    }

    /// <summary>
    /// Saves a picture of the overlay for the app's Overlay page: the current readings, the given sensors, and a
    /// sample frame rate (a real one only exists while a game is running).
    /// </summary>
    public void RenderPreview(string dir, List<OverlaySensorReading> sensors)
    {
        var data = _data?.Copy() ?? new WidgetData();
        data.Sensors = sensors;
        data.Frame ??= new FrameStats(144, 6.9, 118);
        using var bmp = WidgetRenderer.RenderOverlay(_settings, data, 2f);
        PreviewFile.Save(bmp, Path.Combine(dir, "Overlay.png"));
    }

    public void Dispose()
    {
        ClearRtss();
        _notice.Dispose();
        Rtss.Release();
        _hotkey.Dispose();
        _form?.Close();
        _form?.Dispose();
    }
}
