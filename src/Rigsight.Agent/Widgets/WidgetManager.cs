using System.Drawing;
using Rigsight.Agent.Ui;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

/// <summary>Creates, updates and positions widget windows according to settings. UI thread only.</summary>
internal sealed class WidgetManager(Func<RigsightSettings> getSettings, Action<Action<RigsightSettings>> mutateSettings, Action<string?> openWidgetSettings)
{
    private readonly Dictionary<string, WidgetForm> _forms = [];
    private WidgetData? _data;
    private IntPtr _gameMonitor;

    public static string DisplayName(WidgetStyle style) => WidgetCatalog.Title(style);

    public void Apply(RigsightSettings settings)
    {
        // Gone from settings (a widget of the user's own deleted) or turned off: its window goes.
        var shown = settings.Widgets.Where(w => w.Enabled).Select(w => w.Id).ToHashSet();
        foreach (var id in _forms.Keys.Where(id => !shown.Contains(id)).ToList())
        {
            _forms[id].Close();
            _forms[id].Dispose();
            _forms.Remove(id);
        }
        foreach (var cfg in settings.Widgets.Where(w => w.Enabled))
        {
            _forms.TryGetValue(cfg.Id, out var form);
            if (form is null)
            {
                form = new WidgetForm(cfg, this);
                _forms[cfg.Id] = form;
                if (_data is not null) form.UpdateData(_data);
                PlaceInitially(form, cfg);
            }
            form.Apply(cfg);
        }
        UpdateVisibility();
    }

    private void PlaceInitially(WidgetForm form, WidgetConfig cfg)
    {
        var screen = Screen.PrimaryScreen!.WorkingArea;
        if (cfg.X is int cx && cfg.Y is int cy && Screen.AllScreens.Any(s => s.WorkingArea.Contains(cx + 20, cy + 20)))
        {
            form.Location = new Point(cx, cy);
            form.Show();
            form.Redraw();
            return;
        }

        // No saved spot: render off-screen first to learn the real size, then tuck it into the
        // top-right corner, below any other widgets already placed there.
        form.Location = new Point(screen.Right + 4000, screen.Top);
        form.Show();
        form.Redraw();
        int margin = (int)(12 * form.DeviceDpi / 96.0);
        // Start just below where maximized windows draw their title bar and close button.
        int y = screen.Top + (int)(44 * form.DeviceDpi / 96.0);
        foreach (var other in _forms.Values.Where(f => f != form && f.Visible && f.Right >= screen.Right - margin * 3 && f.Top < screen.Top + screen.Height / 2))
            y = Math.Max(y, other.Bottom + margin);
        form.Location = new Point(screen.Right - form.Width - margin, y);
        form.Redraw();
    }

    /// <param name="gameMonitor">The screen of the game in front, or zero.</param>
    public void Update(WidgetData data, IntPtr gameMonitor)
    {
        if (_forms.Values.Any(f => ShowsFrameRate(f.Config)))
        {
            // Its own copy: the overlay sets the frame rate on the shared data only when it shows it.
            data = data.Copy();
            data.Frame = Rtss.ReadFrameStats(ForegroundPid());
            data.RtssRunning = data.Frame is not null || Rtss.IsLive();
        }
        _data = data;
        if (gameMonitor != _gameMonitor && _forms.Count > 0)
            Rigsight.Core.Log.Write("widgets", gameMonitor == IntPtr.Zero ? "Showing again" : $"Stepping aside for {data.Activity.Name ?? "a game"}");
        _gameMonitor = gameMonitor;
        UpdateVisibility();
        foreach (var form in _forms.Values)
            form.UpdateData(data);
    }

    /// <summary>Whether a widget shows anything from RivaTuner (frame rate, frame time, 1% low).</summary>
    public static bool ShowsFrameRate(WidgetConfig cfg) =>
        WidgetCatalog.ItemsOf(cfg).Any(i => WidgetCatalog.Metric(i.Id) is OverlayMetric.Fps or OverlayMetric.FrameTime or OverlayMetric.OnePercentLow);

    private static int ForegroundPid()
    {
        Native.Win32.GetWindowThreadProcessId(Native.Win32.GetForegroundWindow(), out int pid);
        return pid;
    }

    /// <summary>
    /// Widgets are for the desktop and apps (fullscreen videos too): on the screen of a game in front, windowed or
    /// not, they step aside and the overlay takes over (a window over a game makes Windows compose its frames: later
    /// on screen, G-Sync/FreeSync off). Widgets on other screens stay.
    /// </summary>
    private void UpdateVisibility()
    {
        foreach (var form in _forms.Values)
        {
            bool show = _gameMonitor == IntPtr.Zero ||
                Native.Win32.MonitorFromWindow(form.Handle, 2 /* MONITOR_DEFAULTTONEAREST */) != _gameMonitor;
            if (show && !form.Visible)
            {
                form.Show();
                form.Redraw();
            }
            else if (!show && form.Visible)
            {
                form.Hide();
            }
        }
    }

    public void Mutate(string id, Action<WidgetConfig> change) =>
        mutateSettings(s =>
        {
            if (s.Widgets.FirstOrDefault(w => w.Id == id) is { } cfg) change(cfg);
        });

    public void ShowMenu(WidgetForm form, Point at)
    {
        var cfg = form.Config;
        var menu = DarkMenu();

        menu.Items.Add(Header(WidgetCatalog.Title(cfg) + (cfg.Style == WidgetStyle.Custom ? "" : " widget")));
        menu.Items.Add(new ToolStripSeparator());

        var theme = new ToolStripMenuItem("Theme");
        foreach (var t in new[] { WidgetTheme.Dark, WidgetTheme.Grey, WidgetTheme.Light, WidgetTheme.System })
            theme.DropDownItems.Add(Check(t.ToString(), cfg.Theme == t, () => Mutate(cfg.Id, c => c.Theme = t)));
        menu.Items.Add(theme);

        var background = new ToolStripMenuItem("Background opacity");
        foreach (var o in new[] { 1.0, 0.9, 0.75, 0.5, 0.25, 0.0 })
            background.DropDownItems.Add(Check(o == 0 ? "None" : $"{o:P0}", Math.Abs(cfg.BackgroundOpacity - o) < 0.01, () => Mutate(cfg.Id, c => c.BackgroundOpacity = o)));
        menu.Items.Add(background);

        var contentOpacity = new ToolStripMenuItem("Content opacity");
        foreach (var o in new[] { 1.0, 0.9, 0.75, 0.6, 0.45 })
            contentOpacity.DropDownItems.Add(Check($"{o:P0}", Math.Abs(cfg.ContentOpacity - o) < 0.01, () => Mutate(cfg.Id, c => c.ContentOpacity = o)));
        menu.Items.Add(contentOpacity);

        var size = new ToolStripMenuItem("Size");
        foreach (var (label, s) in new[] { ("Small", 0.8), ("Normal", 1.0), ("Large", 1.25), ("Extra large", 1.5) })
            size.DropDownItems.Add(Check(label, Math.Abs(cfg.Scale - s) < 0.01, () => Mutate(cfg.Id, c => c.Scale = s)));
        menu.Items.Add(size);

        menu.Items.Add(Check("Grayscale", cfg.Grayscale, () => Mutate(cfg.Id, c => c.Grayscale = !c.Grayscale)));
        menu.Items.Add(Check("Lock in place (click-through)", cfg.Locked, () => Mutate(cfg.Id, c => c.Locked = !c.Locked)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Edit widget…", () => openWidgetSettings(cfg.Id)));
        menu.Items.Add(Item("Hide this widget", () => Mutate(cfg.Id, c => c.Enabled = false)));

        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        menu.Show(at);
    }

    /// <summary>Tray submenu listing every widget style.</summary>
    public ToolStripMenuItem BuildTrayMenu()
    {
        var root = new ToolStripMenuItem("Widgets");
        root.DropDownOpening += (_, _) =>
        {
            root.DropDownItems.Clear();
            var settings = getSettings();
            // The built-in ones (FPS first, as on the Widgets page), then the user's own.
            var builtIn = settings.Widgets.Where(w => w.Style != WidgetStyle.Custom).OrderBy(w => w.Style != WidgetStyle.Fps).ToList();
            var custom = settings.Widgets.Where(w => w.Style == WidgetStyle.Custom).ToList();
            foreach (var cfg in builtIn) root.DropDownItems.Add(Toggle(cfg));
            if (custom.Count > 0) root.DropDownItems.Add(new ToolStripSeparator());
            foreach (var cfg in custom) root.DropDownItems.Add(Toggle(cfg));
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(Item("Unlock all widgets", () => mutateSettings(s => s.Widgets.ForEach(w => w.Locked = false))));
            root.DropDownItems.Add(Item("Widget settings…", () => openWidgetSettings(null)));
        };
        root.DropDownItems.Add("…"); // placeholder so the arrow shows

        ToolStripMenuItem Toggle(WidgetConfig cfg)
        {
            string id = cfg.Id;
            return Check(WidgetCatalog.Title(cfg), cfg.Enabled, () => Mutate(id, c => c.Enabled = !c.Enabled));
        }
        if (root.DropDown is ToolStripDropDownMenu dd) dd.Renderer = new DarkMenuRenderer();
        return root;
    }

    /// <summary>Saves a PNG of every widget style (with current data) for the app's Widgets page.</summary>
    public void RenderPreviews(RigsightSettings settings)
    {
        try
        {
            var dir = Path.Combine(Rigsight.Core.RigsightPaths.DataDir, "previews");
            Directory.CreateDirectory(dir);
            foreach (var cfg in settings.Widgets)
            {
                // Shown over a backdrop in the app, so both opacities are visible there as on the desktop.
                var preview = new WidgetConfig
                {
                    Style = cfg.Style, Id = cfg.Id, Name = cfg.Name, Layout = cfg.Layout, Items = cfg.Items, Theme = cfg.Theme,
                    Grayscale = cfg.Grayscale, BackgroundOpacity = cfg.BackgroundOpacity, ContentOpacity = cfg.ContentOpacity, Scale = 1,
                };
                var data = _data;
                // A frame rate only exists with a game in front (never, while the app is): show one as it would look.
                if (ShowsFrameRate(cfg) && data?.Frame is null)
                {
                    data = data?.Copy() ?? new WidgetData();
                    data.Frame = new FrameStats(144, 6.9, 118);
                }
                using var bmp = WidgetRenderer.Render(preview, data, 2f, hover: false, out _);
                PreviewFile.Save(bmp, Path.Combine(dir, $"{cfg.Id}.png"));
            }
        }
        catch (Exception ex)
        {
            Rigsight.Core.Log.Error("widgets", ex);
        }
    }

    public void CloseAll()
    {
        foreach (var f in _forms.Values)
        {
            f.Close();
            f.Dispose();
        }
        _forms.Clear();
    }

    private static ContextMenuStrip DarkMenu() => Ui.DarkMenuRenderer.Create();

    private static ToolStripMenuItem Header(string text) => new(text) { Enabled = false };

    private static ToolStripMenuItem Item(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    private static ToolStripMenuItem Check(string text, bool isChecked, Action action)
    {
        var item = Item(text, action);
        item.Checked = isChecked;
        return item;
    }
}
