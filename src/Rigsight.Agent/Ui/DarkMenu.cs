using System.Drawing;
using Rigsight.Agent.Widgets;

namespace Rigsight.Agent.Ui;

/// <summary>
/// Styling for WinForms context menus (tray and widget menus) in the app's own colours: black and white, following the
/// Theme setting (dark, light, or Windows' app mode for "system"). The colours are read on every paint, so an open or
/// already-built menu follows a theme change.
/// </summary>
internal sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
{
    /// <summary>The app's Theme setting ("dark", "light" or "system"), kept up to date by the agent.</summary>
    public static string Theme { get; set; } = "dark";

    internal static bool Light => Theme == "light" || Theme == "system" && WidgetRenderer.WindowsUsesLight();

    // As in the app's Dark.xaml and Light.xaml: the raised surface, hover, stroke, text and faint text.
    internal static Color Bg => Light ? Color.FromArgb(255, 255, 255) : Color.FromArgb(22, 22, 22);
    private static Color Hover => Light ? Color.FromArgb(230, 230, 230) : Color.FromArgb(36, 36, 36);
    private static Color Border => Light ? Color.FromArgb(211, 211, 211) : Color.FromArgb(46, 46, 46);
    internal static Color TextColor => Light ? Color.FromArgb(0, 0, 0) : Color.FromArgb(255, 255, 255);
    private static Color DisabledText => Light ? Color.FromArgb(140, 140, 140) : Color.FromArgb(107, 107, 107);

    public static ContextMenuStrip Create()
    {
        return new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Font("Segoe UI", 9.5f),
            Padding = new Padding(4),
        };
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? TextColor : DisabledText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = TextColor;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        using var pen = new Pen(TextColor, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
        e.Graphics.DrawLines(pen, [new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 5, cy - 4)]);
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Bg;
        public override Color CheckBackground => Bg;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
