using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rigsight.ViewModels;

namespace Rigsight.Controls;

/// <summary>
/// Lays out custom-page tiles on a grid of equal columns and fixed-height rows. Tiles that move
/// slide to their new spot; the tile being dragged is drawn wherever the mouse has it.
/// </summary>
public sealed class TileGrid : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(TileGrid),
        new FrameworkPropertyMetadata(Core.Settings.TileConfig.Columns, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(nameof(RowHeight), typeof(double), typeof(TileGrid),
        new FrameworkPropertyMetadata(56.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(TileGrid),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private Dictionary<UIElement, Rect> _last = [];
    private double _lastWidth = -1;

    public double CellWidth { get; private set; }

    private double ColumnWidth(double width) => Math.Max(10, (width - Gap * (Columns - 1)) / Columns);

    /// <summary>
    /// How many cells a tile at (<paramref name="x"/>, <paramref name="y"/>) spans if its right/bottom
    /// edge is dragged to <paramref name="edge"/>: rounds to the nearest cell boundary.
    /// </summary>
    public (int W, int H) SpanTo(int x, int y, Point edge) =>
        ((int)Math.Round((edge.X - x * (CellWidth + Gap) + Gap) / (CellWidth + Gap)),
         (int)Math.Round((edge.Y - y * (RowHeight + Gap) + Gap) / (RowHeight + Gap)));

    private Rect Slot(TileViewModel t) => new(
        t.X * (CellWidth + Gap), t.Y * (RowHeight + Gap),
        t.W * CellWidth + (t.W - 1) * Gap, t.H * RowHeight + (t.H - 1) * Gap);

    /// <summary>The grid cell under a tile whose top-left corner is at <paramref name="topLeft"/>.</summary>
    public (int Col, int Row) CellAt(Point topLeft) =>
        ((int)Math.Round(topLeft.X / (CellWidth + Gap)), (int)Math.Round(topLeft.Y / (RowHeight + Gap)));

    private static TileViewModel? TileOf(UIElement child) => (child as FrameworkElement)?.DataContext as TileViewModel;

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 900 : available.Width;
        CellWidth = ColumnWidth(width);
        int rows = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (TileOf(child) is not { } t)
            {
                child.Measure(default);
                continue;
            }
            child.Measure(Slot(t).Size);
            if (!t.IsDragging) rows = Math.Max(rows, t.Y + t.H);
        }
        // The dragged tile doesn't count (it floats); its placeholder does, so the page grows as you drag down.
        return new Size(width, rows == 0 ? 0 : rows * (RowHeight + Gap) - Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        CellWidth = ColumnWidth(finalSize.Width);
        // Only animate moves, not a window resize (which shifts everything at once).
        bool animate = Math.Abs(finalSize.Width - _lastWidth) < 0.5;
        _lastWidth = finalSize.Width;

        var next = new Dictionary<UIElement, Rect>();
        foreach (UIElement child in InternalChildren)
        {
            if (TileOf(child) is not { } t)
            {
                child.Arrange(default);
                continue;
            }

            var slot = Slot(t);
            var rect = t.IsDragging ? new Rect(t.DragPosition, slot.Size) : slot;
            child.Arrange(rect);
            Panel.SetZIndex(child, t.IsDragging ? 10 : t.IsPlaceholder ? -1 : 0);

            if (child.RenderTransform is not TranslateTransform move)
                child.RenderTransform = move = new TranslateTransform();

            if (t.IsDragging)
            {
                Stop(move);
            }
            else if (animate && _last.TryGetValue(child, out var old) && (old.X != rect.X || old.Y != rect.Y))
            {
                // Start from wherever it is on screen now (it may already be mid-slide).
                double fromX = old.X + move.X - rect.X, fromY = old.Y + move.Y - rect.Y;
                Slide(move, TranslateTransform.XProperty, fromX);
                Slide(move, TranslateTransform.YProperty, fromY);
            }
            next[child] = rect;
        }
        _last = next;
        return finalSize;
    }

    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private static void Slide(TranslateTransform move, DependencyProperty property, double from)
    {
        if (Math.Abs(from) < 0.5)
        {
            move.BeginAnimation(property, null);
            return;
        }
        move.BeginAnimation(property, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = Ease });
    }

    private static void Stop(TranslateTransform move)
    {
        move.BeginAnimation(TranslateTransform.XProperty, null);
        move.BeginAnimation(TranslateTransform.YProperty, null);
        move.X = move.Y = 0;
    }
}
