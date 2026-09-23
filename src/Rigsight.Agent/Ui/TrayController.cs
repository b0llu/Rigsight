using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Rigsight.Agent.Native;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Agent.Ui;

/// <summary>Notification-area icon: shows a live reading as a colored number, plus the agent's menu.</summary>
internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly ToolStripMenuItem _pauseItem;
    private readonly Icon _logo;
    private Icon? _rendered;
    private string _lastKey = "";
    private Action? _balloonAction;

    public TrayController(Action open, ToolStripMenuItem widgetsMenu, Action<int> pause, Action resume, Func<bool> isPaused, Action quit)
    {
        _logo = new Icon(Path.Combine(AppContext.BaseDirectory, "Rigsight.ico"), SystemInformation.SmallIconSize);

        var menu = DarkMenuRenderer.Create();
        menu.Items.Add(Item("Open Rigsight", open));
        menu.Items.Add(widgetsMenu);

        _pauseItem = new ToolStripMenuItem("Pause tracking");
        var pauseFor = new[] { ("For 30 minutes", 30), ("For 1 hour", 60), ("For 3 hours", 180), ("Until I resume", -1) };
        foreach (var (label, minutes) in pauseFor)
            _pauseItem.DropDownItems.Add(Item(label, () => pause(minutes)));
        if (_pauseItem.DropDown is ToolStripDropDownMenu dd) dd.Renderer = new DarkMenuRenderer();
        var resumeItem = Item("Resume tracking", resume);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(resumeItem);
        menu.Opening += (_, _) =>
        {
            bool paused = isPaused();
            _pauseItem.Visible = !paused;
            resumeItem.Visible = paused;
        };

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Quit Rigsight", quit));

        _icon.ContextMenuStrip = menu;
        _icon.Icon = _logo;
        _icon.Text = "Rigsight";
        _icon.Visible = true;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };
        _icon.BalloonTipClicked += (_, _) => _balloonAction?.Invoke();
    }

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

    /// <param name="health">0 = cool, 1 = warm, 2 = at or above an alert limit (used by the Status style).</param>
    public void Update(TrayMetric metric, double? value, bool isTemperature, string tooltip, int health)
    {
        // NotifyIcon.Text is limited to 127 characters.
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        if (metric == TrayMetric.Status)
        {
            string statusKey = "status" + health;
            if (statusKey == _lastKey) return;
            _lastKey = statusKey;
            SetIcon(RenderStatus(health));
            return;
        }

        if (metric == TrayMetric.Logo || value is null)
        {
            if (_lastKey != "logo")
            {
                _lastKey = "logo";
                SetIcon(null);
            }
            return;
        }

        double shown = isTemperature ? Units.Temp(value.Value) : value.Value;
        string text = Math.Round(shown).ToString("0");
        var color = isTemperature ? TempColor(value.Value) : Color.FromArgb(91, 140, 255);
        string key = text + color.ToArgb();
        if (key == _lastKey) return;
        _lastKey = key;
        SetIcon(Render(text, color));
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

    private static Color TempColor(double c) => c switch
    {
        < 45 => Color.FromArgb(56, 189, 248),
        < 70 => Color.FromArgb(52, 211, 153),
        < 85 => Color.FromArgb(251, 191, 36),
        _ => Color.FromArgb(248, 113, 113),
    };

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
            using (var ring = new SolidBrush(Color.FromArgb(11, 14, 20)))
                g.FillEllipse(ring, x - 1, y - 1, d + 1, d + 1);
            using (var dot = new SolidBrush(color))
                g.FillEllipse(dot, x + size * 0.06f, y + size * 0.06f, d - size * 0.12f - 1, d - size * 0.12f - 1);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static Icon Render(string text, Color background)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float r = size * 0.28f;
            using var path = new GraphicsPath();
            path.AddArc(0, 0, 2 * r, 2 * r, 180, 90);
            path.AddArc(size - 2 * r - 1, 0, 2 * r, 2 * r, 270, 90);
            path.AddArc(size - 2 * r - 1, size - 2 * r - 1, 2 * r, 2 * r, 0, 90);
            path.AddArc(0, size - 2 * r - 1, 2 * r, 2 * r, 90, 90);
            path.CloseFigure();
            using (var bg = new SolidBrush(background))
                g.FillPath(bg, path);

            float fontPx = size * (text.Length >= 3 ? 0.50f : 0.70f);
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.FromArgb(11, 14, 20));
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, fg, new RectangleF(0, size * 0.04f, size, size), fmt);
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
