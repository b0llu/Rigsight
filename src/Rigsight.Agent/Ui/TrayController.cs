using System.Drawing;
using System.Drawing.Drawing2D;
using Rigsight.Agent.Native;

namespace Rigsight.Agent.Ui;

/// <summary>Notification-area icon: the logo with a temperature health dot, plus the agent's menu.</summary>
internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly Icon _logo;
    private Icon? _rendered;
    private string _lastKey = "";
    private Action? _balloonAction;

    public TrayController(Action open, ToolStripMenuItem widgetsMenu, ToolStripMenuItem overlayItem, Action<int> pause, Action resume, Func<bool> isPaused, Action quit)
    {
        _logo = new Icon(Path.Combine(AppContext.BaseDirectory, "Rigsight.ico"), SystemInformation.SmallIconSize);
        _icon.ContextMenuStrip = BuildMenu(open, widgetsMenu, overlayItem, pause, resume, isPaused, quit);
        _icon.Icon = _logo;
        _icon.Text = "Rigsight";
        _icon.Visible = true;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };
        _icon.BalloonTipClicked += (_, _) => _balloonAction?.Invoke();
    }

    /// <summary>The tray menu. Pausing shows as "Pause tracking" (a choice of how long) or "Resume tracking", never both.</summary>
    internal static ContextMenuStrip BuildMenu(Action open, ToolStripMenuItem widgetsMenu, ToolStripMenuItem overlayItem, Action<int> pause, Action resume, Func<bool> isPaused, Action quit)
    {
        var menu = DarkMenuRenderer.Create();
        menu.Items.Add(Item("Open Rigsight", open));
        menu.Items.Add(widgetsMenu);
        menu.Items.Add(overlayItem);

        var pauseItem = new ToolStripMenuItem("Pause tracking");
        foreach (var (label, minutes) in PauseChoices)
            pauseItem.DropDownItems.Add(Item(label, () => pause(minutes)));
        if (pauseItem.DropDown is ToolStripDropDownMenu dd) dd.Renderer = new DarkMenuRenderer();
        var resumeItem = Item("Resume tracking", resume);
        menu.Items.Add(pauseItem);
        menu.Items.Add(resumeItem);
        menu.Opening += (_, _) =>
        {
            bool paused = isPaused();
            pauseItem.Visible = !paused;
            resumeItem.Visible = paused;
        };

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Quit Rigsight", quit));
        return menu;
    }

    internal static readonly (string Label, int Minutes)[] PauseChoices =
        [("For 30 minutes", 30), ("For 1 hour", 60), ("For 3 hours", 180), ("Until I resume", -1)];

    private static ToolStripMenuItem Item(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    public void ShowNotification(string title, string text, bool warning, Action? onClick = null)
    {
        _balloonAction = onClick;
        _icon.ShowBalloonTip(8000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// <param name="health">0 = cool, 1 = warm, 2 = at or above an alert limit.</param>
    public void Update(string tooltip, int health)
    {
        // NotifyIcon.Text is limited to 127 characters.
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        string key = "status" + health;
        if (key == _lastKey) return;
        _lastKey = key;
        SetIcon(RenderStatus(health));
    }

    private void SetIcon(Icon? icon)
    {
        var old = _rendered;
        _rendered = icon;
        _icon.Icon = icon ?? _logo;
        if (old is not null)
        {
            Win32.DestroyIcon(old.Handle);
            old.Dispose();
        }
    }

    /// <summary>The logo with a small colored dot in the corner: a calm, glanceable health signal.</summary>
    private Icon RenderStatus(int health)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            using (var logo = new Icon(_logo, size, size).ToBitmap())
                g.DrawImage(logo, 0, 0, size, size);

            var color = health switch
            {
                >= 2 => Color.FromArgb(248, 113, 113),
                1 => Color.FromArgb(251, 191, 36),
                _ => Color.FromArgb(52, 211, 153),
            };
            float d = size * 0.46f, x = size - d, y = size - d;
            using (var ring = new SolidBrush(Color.FromArgb(10, 10, 10)))
                g.FillEllipse(ring, x - 1, y - 1, d + 1, d + 1);
            using (var dot = new SolidBrush(color))
                g.FillEllipse(dot, x + size * 0.06f, y + size * 0.06f, d - size * 0.12f - 1, d - size * 0.12f - 1);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_rendered is not null)
        {
            Win32.DestroyIcon(_rendered.Handle);
            _rendered.Dispose();
            _rendered = null;
        }
        _logo.Dispose();
    }
}
