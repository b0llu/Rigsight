using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// A row of equal cells (a day in quarter hours, a week a square a day) whose edges fall on whole screen pixels at any zoom,
/// with a gap of whole pixels between them. A UniformGrid rounds each cell before the app's zoom scales the page, so at
/// 110% every edge lands between pixels again and the cells and gaps came out uneven: some gaps gone, some doubled
/// (the person's picture of Network's Connection strip, 2026-10-27). Each child takes its whole slot, so the mouse is
/// always over one; the gap is the inset <see cref="InsetProperty"/> gives the cell inside it.
/// </summary>
public sealed class PixelRow : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(PixelRow),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>How many cells the row is divided into; 0: as many as it has.</summary>
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(PixelRow),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>The space between two cells at 100%, rounded to whole screen pixels (at least one) at the zoom in use.</summary>
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    /// <summary>The space a cell leaves on its right inside its slot (the gap), inherited down to the shape that draws it.</summary>
    public static readonly DependencyProperty InsetProperty = DependencyProperty.RegisterAttached("Inset", typeof(Thickness), typeof(PixelRow),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.Inherits));

    public static Thickness GetInset(DependencyObject o) => (Thickness)o.GetValue(InsetProperty);
    public static void SetInset(DependencyObject o, Thickness value) => o.SetValue(InsetProperty, value);

    public PixelRow()
    {
        // The row places its cells on whole pixels itself; rounding them again would undo it.
        UseLayoutRounding = false;
    }

    private int Count => Columns > 0 ? Columns : InternalChildren.Count;

    protected override Size MeasureOverride(Size available)
    {
        int n = Math.Max(1, Count);
        double w = double.IsInfinity(available.Width) ? double.PositiveInfinity : available.Width / n;
        double h = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(w, available.Height));
            h = Math.Max(h, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, double.IsInfinity(available.Height) ? h : available.Height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        int n = Math.Max(1, Count);
        var (origin, scale) = Device();
        // In screen pixels from the screen's own origin: the row's left edge, its width and the gap.
        double left = origin, width = size.Width * scale;
        double gap = Math.Max(1, Math.Round(Gap * scale));
        double inset = gap / scale;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            // Slot i runs from one pixel edge to the next; the row and its last gap together divide into n equal steps.
            double a = Math.Round(left + i * (width + gap) / n), b = Math.Round(left + (i + 1) * (width + gap) / n);
            double x = (a - origin) / scale, w = Math.Max(0, (b - a) / scale);
            var child = InternalChildren[i];
            // The last slot runs a gap past the row's end, so its cell ends with the row like every other ends before a gap.
            SetInset(child, new Thickness(0, 0, inset, 0));
            child.Arrange(new Rect(x, 0, w, size.Height));
        }
        return size;
    }

    /// <summary>Where the row's left edge is in screen pixels, and how many screen pixels one of its units is (zoom and Windows' scaling together).</summary>
    private (double Origin, double Scale) Device()
    {
        if (PresentationSource.FromVisual(this) is not { CompositionTarget: { } target, RootVisual: Visual root }) return (0, 1);
        try
        {
            var toRoot = TransformToAncestor(root);
            var dpi = target.TransformToDevice;
            Point zero = dpi.Transform(toRoot.Transform(new Point(0, 0))), one = dpi.Transform(toRoot.Transform(new Point(1, 0)));
            double scale = one.X - zero.X;
            return scale > 0 ? (zero.X, scale) : (0, 1);
        }
        catch (InvalidOperationException) { return (0, 1); }
    }
}
