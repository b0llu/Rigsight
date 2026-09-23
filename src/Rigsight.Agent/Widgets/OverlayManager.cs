using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

/// <summary>Shows and hides the overlay with its keyboard shortcut, and keeps it up to date. UI thread only.</summary>
internal sealed class OverlayManager : IDisposable
{
    private readonly GlobalHotkey _hotkey = new();
    private readonly ToolStripMenuItem _trayItem = new("Show overlay");
    private OverlayForm? _form;
    private OverlaySettings _settings = new();
    private WidgetData? _data;
    private string? _registered;

    public OverlayManager()
    {
        _hotkey.Pressed += Toggle;
        _trayItem.Click += (_, _) => Toggle();
    }

    public bool Visible { get; private set; }

    /// <summary>Another program already owns the shortcut, so pressing it won't reach us.</summary>
    public bool HotkeyTaken { get; private set; }

    /// <summary>Raised when the overlay appears or disappears, or the shortcut can't be used.</summary>
    public event Action? StateChanged;

    /// <summary>The tray menu entry ("Show overlay   Alt+Shift+O").</summary>
    public ToolStripMenuItem TrayItem => _trayItem;

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
        if (Visible) _form?.Redraw(_settings, _data);
    }

    public void Toggle() => SetVisible(!Visible);

    public void SetVisible(bool visible)
    {
        if (visible == Visible) return;
        Visible = visible;
        _trayItem.Checked = visible;
        if (visible)
        {
            _form ??= new OverlayForm();
            _form.Show();
            _form.Redraw(_settings, _data);
        }
        else
        {
            _form?.Hide();
        }
        StateChanged?.Invoke();
    }

    public void Update(WidgetData data)
    {
        _data = data;
        if (Visible) _form?.Redraw(_settings, data);
    }

    /// <summary>Saves a picture of the overlay (with current readings) for the app's Overlay page.</summary>
    public void RenderPreview(string dir)
    {
        using var bmp = WidgetRenderer.RenderOverlay(_settings, _data, 2f);
        bmp.Save(Path.Combine(dir, "Overlay.png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    public void Dispose()
    {
        _hotkey.Dispose();
        _form?.Close();
        _form?.Dispose();
    }
}
