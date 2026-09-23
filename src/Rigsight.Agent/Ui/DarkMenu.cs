using System.Drawing;

namespace Rigsight.Agent.Ui;

/// <summary>Dark styling for WinForms context menus (tray and widget menus).</summary>
internal sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new DarkColors())
{
    private static readonly Color TextColor = Color.FromArgb(232, 236, 244);
    private static readonly Color DisabledText = Color.FromArgb(110, 118, 135);

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
        using var pen = new Pen(Color.FromArgb(91, 140, 255), 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
        e.Graphics.DrawLines(pen, [new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 5, cy - 4)]);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(27, 33, 48);
        private static readonly Color Hover = Color.FromArgb(40, 48, 68);
        private static readonly Color Border = Color.FromArgb(45, 54, 76);

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
