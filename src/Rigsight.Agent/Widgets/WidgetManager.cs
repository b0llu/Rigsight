using System.Drawing;
using Rigsight.Agent.Ui;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Widgets;

/// <summary>Creates, updates and positions widget windows according to settings. UI thread only.</summary>
internal sealed class WidgetManager(Func<RigsightSettings> getSettings, Action<Action<RigsightSettings>> mutateSettings, Action openWidgetSettings)
{
    private readonly Dictionary<WidgetStyle, WidgetForm> _forms = [];
    private WidgetData? _data;
    private bool _fullscreen;

    public static string DisplayName(WidgetStyle style) => style switch
    {
        WidgetStyle.Compact => "Compact",
        WidgetStyle.Pill => "Slim bar",
        WidgetStyle.Gauges => "Gauges",
        WidgetStyle.NowPlaying => "Now playing",
        WidgetStyle.Today => "Today",
        _ => "Temperature graph",
    };

    public void Apply(RigsightSettings settings)
    {
        foreach (var cfg in settings.Widgets)
        {
            _forms.TryGetValue(cfg.Style, out var form);
            if (!cfg.Enabled)
            {
                if (form is not null)
                {
                    form.Close();
                    form.Dispose();
                    _forms.Remove(cfg.Style);
                }
                continue;
            }

            if (form is null)
            {
                form = new WidgetForm(cfg, this);
                _forms[cfg.Style] = form;
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

    public void Update(WidgetData data, bool fullscreen)
    {
        _data = data;
        if (_fullscreen != fullscreen)
        {
            _fullscreen = fullscreen;
            UpdateVisibility();
        }
        foreach (var form in _forms.Values)
            form.UpdateData(data);
    }

    private void UpdateVisibility()
    {
        foreach (var form in _forms.Values)
        {
            bool show = form.Config.Visibility switch
            {
                WidgetVisibility.HideInFullscreen => !_fullscreen,
                _ => true,
            };
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

    public void Mutate(WidgetStyle style, Action<WidgetConfig> change) =>
        mutateSettings(s =>
        {
            var cfg = s.Widgets.First(w => w.Style == style);
            change(cfg);
        });

    public void ShowMenu(WidgetForm form, Point at)
    {
        var cfg = form.Config;
        var menu = DarkMenu();

        menu.Items.Add(Header(DisplayName(cfg.Style) + " widget"));
        menu.Items.Add(new ToolStripSeparator());

        var theme = new ToolStripMenuItem("Theme");
        foreach (var t in new[] { WidgetTheme.Dark, WidgetTheme.Light, WidgetTheme.System })
            theme.DropDownItems.Add(Check(t.ToString(), cfg.Theme == t, () => Mutate(cfg.Style, c => c.Theme = t)));
        menu.Items.Add(theme);

        var opacity = new ToolStripMenuItem("Opacity");
        foreach (var o in new[] { 1.0, 0.9, 0.75, 0.6, 0.45 })
            opacity.DropDownItems.Add(Check($"{o:P0}", Math.Abs(cfg.Opacity - o) < 0.01, () => Mutate(cfg.Style, c => c.Opacity = o)));
        menu.Items.Add(opacity);

        var size = new ToolStripMenuItem("Size");
        foreach (var (label, s) in new[] { ("Small", 0.8), ("Normal", 1.0), ("Large", 1.25), ("Extra large", 1.5) })
            size.DropDownItems.Add(Check(label, Math.Abs(cfg.Scale - s) < 0.01, () => Mutate(cfg.Style, c => c.Scale = s)));
        menu.Items.Add(size);

        var show = new ToolStripMenuItem("Show");
        show.DropDownItems.Add(Check("Always", cfg.Visibility == WidgetVisibility.Always, () => Mutate(cfg.Style, c => c.Visibility = WidgetVisibility.Always)));
        show.DropDownItems.Add(Check("Hide during fullscreen apps", cfg.Visibility == WidgetVisibility.HideInFullscreen, () => Mutate(cfg.Style, c => c.Visibility = WidgetVisibility.HideInFullscreen)));
        menu.Items.Add(show);

        menu.Items.Add(Check("Lock in place (click-through)", cfg.Locked, () => Mutate(cfg.Style, c => c.Locked = !c.Locked)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("More widget options…", openWidgetSettings));
        menu.Items.Add(Item("Hide this widget", () => Mutate(cfg.Style, c => c.Enabled = false)));

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
            foreach (var cfg in settings.Widgets)
            {
                var style = cfg.Style;
                root.DropDownItems.Add(Check(DisplayName(style), cfg.Enabled, () => Mutate(style, c => c.Enabled = !c.Enabled)));
            }
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(Item("Unlock all widgets", () => mutateSettings(s => s.Widgets.ForEach(w => w.Locked = false))));
            root.DropDownItems.Add(Item("Widget settings…", openWidgetSettings));
        };
        root.DropDownItems.Add("…"); // placeholder so the arrow shows
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
                var preview = new WidgetConfig { Style = cfg.Style, Theme = cfg.Theme, Opacity = 1, Scale = 1 };
                using var bmp = WidgetRenderer.Render(preview, _data, 2f, hover: false, out _);
                bmp.Save(Path.Combine(dir, $"{cfg.Style}.png"), System.Drawing.Imaging.ImageFormat.Png);
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
