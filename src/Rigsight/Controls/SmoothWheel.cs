using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// The mouse wheel glides: each notch moves the same distance WPF's own wheel does (its lines, 16 px each), over
/// <see cref="Duration"/>, easing out, and a fast spin adds on to where it's going (the approved mock-up, the user,
/// 2026-11-08: "add this type of scrolling on all pages"). <see cref="Everywhere"/> turns it on for every scrolling box in
/// the app; <see cref="EnabledProperty"/> for one (the tests).
/// <para>
/// Taken as the wheel reaches a scrolling box on its way out from what's under the pointer, so anything inside that uses
/// the wheel itself (a sideways row, Ctrl + wheel's zoom, <see cref="WheelChain"/> at a box's end) has it first, and the
/// box nearest the pointer is the one that moves. WPF's own wheel is left as it was for a touchpad (not whole notches),
/// with Windows' animation effects off, at a box's end, and for a list that scrolls by the item rather than the pixel.
/// </para>
/// </summary>
public static class SmoothWheel
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothWheel), new PropertyMetadata(false));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    /// <summary>How long one notch's glide takes (the approved mock-up's tenth of a second).</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(100);

    private static bool _everywhere;

    static SmoothWheel() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnWheel));

    /// <summary>Every scrolling box in the app glides. Once, at startup.</summary>
    public static void Everywhere() => _everywhere = true;

    /// <summary>How far one notch (a delta of 120) moves: WPF's own wheel, its lines of 16 px, or a page.</summary>
    internal static double Step(int delta, int wheelLines, double viewport) =>
        -(delta / 120.0) * (wheelLines < 0 ? viewport : wheelLines * 16.0);

    /// <summary>Where a glide is at a moment of it (0 to 1): fast at first, settling into place.</summary>
    internal static double Ease(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv) return;
        if (TryGlide(sv, e.Delta)) e.Handled = true;
    }

    /// <summary>
    /// One turn of the wheel for <paramref name="sv"/>: glided when that box glides, the turn is whole notches and the box
    /// can move that way. False leaves it to WPF's wheel (or the caller's own).
    /// </summary>
    internal static bool TryGlide(ScrollViewer sv, int delta) =>
        (_everywhere || GetEnabled(sv)) && delta != 0 && delta % 120 == 0 && Services.Motion.Enabled && !ByItem(sv) && Glide(sv, delta);

    /// <summary>Whether <paramref name="sv"/> is gliding now (the tests).</summary>
    internal static bool IsGliding(ScrollViewer sv) => Glides.ContainsKey(sv);

    /// <summary>
    /// A list whose box scrolls by the item, not the pixel: its offsets count rows, so it keeps WPF's wheel. That's a box
    /// leaving its scrolling to the list's panel (CanContentScroll) when the panel scrolls itself by the item: a stack
    /// panel, or a virtualizing one not set to pixels (VirtualizingPanel.ScrollUnit).
    /// </summary>
    internal static bool ByItem(ScrollViewer sv) =>
        sv.CanContentScroll && ItemsHost(sv) is IScrollInfo host
        && !(host is VirtualizingStackPanel panel
             && VirtualizingPanel.GetScrollUnit(ItemsControl.GetItemsOwner(panel) ?? (DependencyObject)panel) == ScrollUnit.Pixel);

    /// <summary>One notch more for <paramref name="sv"/>, from where it's gliding to; false when it can't move that way.</summary>
    private static bool Glide(ScrollViewer sv, int delta)
    {
        bool going = Glides.TryGetValue(sv, out var g);
        double from = going ? g!.Target : sv.VerticalOffset;
        // Up to the end there is now; a list that adds rows as it nears its end (Timeline) has them a screen ahead.
        double to = Math.Clamp(from + Step(delta, SystemParameters.WheelScrollLines, sv.ViewportHeight), 0, sv.ScrollableHeight);
        if (!going && Math.Abs(to - sv.VerticalOffset) < 0.5) return false;
        // Its clock starts at the next frame (see OnFrame).
        Glides[sv] = new Going(sv.VerticalOffset, to, null, sv.VerticalOffset);
        if (!going && Glides.Count == 1) CompositionTarget.Rendering += OnFrame;
        return true;
    }

    /// <summary>A glide: from where, to where, the frame time it began on (null until its first frame), where it was last put.</summary>
    private sealed record Going(double From, double Target, TimeSpan? Start, double Set);
    private static readonly Dictionary<ScrollViewer, Going> Glides = [];
    private static TimeSpan _lastFrame = TimeSpan.MinValue;

    /// <summary>
    /// How far a glide is at its first frame: one frame in already, so the box moves on the very next frame. A 143 Hz
    /// screen's frame (7 ms); a slower one only starts a little further along.
    /// </summary>
    private static readonly TimeSpan FirstStep = TimeSpan.FromMilliseconds(7);

    /// <summary>
    /// Each frame, every glide put where its curve is at that frame's own time (WPF's RenderingTime: one step per frame,
    /// evenly spaced). Not the system's tick count: it moves every 15.6 ms, so on a 143 Hz screen two or three frames in a
    /// row got the same place and then a jump (2026-11-09, the user: "stuttery, laggy" on every page).
    /// </summary>
    private static void OnFrame(object? sender, EventArgs e) =>
        Frame(e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.FromTicks(System.Diagnostics.Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency));

    /// <summary>One frame at <paramref name="now"/> (its RenderingTime): every glide to where its curve is then (the tests step it).</summary>
    internal static void Frame(TimeSpan now)
    {
        // Told more than once for one frame: once is enough.
        if (now == _lastFrame) return;
        _lastFrame = now;
        foreach (var (sv, g) in Glides.ToList())
        {
            // Moved by something else meanwhile (its scroll bar dragged, a key, a jump to a day, another page): that wins.
            if (Math.Abs(sv.VerticalOffset - g.Set) > 1 || !sv.IsLoaded)
            {
                Glides.Remove(sv);
                continue;
            }
            var start = g.Start ?? now - FirstStep;
            double t = (now - start).TotalMilliseconds / Duration.TotalMilliseconds;
            double at = g.From + (g.Target - g.From) * Ease(t);
            sv.ScrollToVerticalOffset(at);
            if (t >= 1) Glides.Remove(sv);
            else Glides[sv] = g with { Start = start, Set = at };
        }
        if (Glides.Count == 0) CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>The panel holding a list's rows inside <paramref name="sv"/>, if it holds a list (a few levels down).</summary>
    private static Panel? ItemsHost(DependencyObject d, int depth = 0)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is Panel { IsItemsHost: true } host) return host;
            if (depth < 4 && ItemsHost(child, depth + 1) is { } deeper) return deeper;
        }
        return null;
    }
}
