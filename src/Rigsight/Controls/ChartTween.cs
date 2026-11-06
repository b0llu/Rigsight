using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// A drawn chart's way from one set of values to the next (issue #11), as mock-up ReK33EVbdKbsoarY7jwYXQ moves: the
/// same bars as before (another week, another day) go from where they were to their new values in a quarter of a
/// second, slowing as they land; when what they are changes (a day's hours to a week's days), the old ones fade out
/// and the new ones fade in, in the same quarter second. With Windows' animations turned off, nothing moves.
/// </summary>
internal sealed class ChartTween(FrameworkElement owner)
{
    public enum Kind { None, Move, Swap }

    /// <summary>The mock-up's duration for both.</summary>
    internal static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(250);

    private readonly Stopwatch _clock = new();
    private Kind _kind;
    private bool _hooked;

    /// <summary>
    /// Frames still to pass before the clock starts. New figures come with a frame that lays the page out again, which
    /// can take a fifth of a second; started with the change, the whole quarter second was over before the first frame
    /// showed it, and nothing was seen to move (2026-11-06, frames of the real app: the old week at 955 ms, the month
    /// finished at 1,174 ms). So it starts at the frame after the one that lays the new figures out.
    /// </summary>
    private int _waitFrames;

    /// <summary>Starts moving (or swapping) from the values drawn until now.</summary>
    public void Start(Kind kind)
    {
        _kind = SystemParameters.ClientAreaAnimation ? kind : Kind.None;
        if (_kind == Kind.None) { Unhook(); return; }
        _clock.Reset();
        _waitFrames = Frames.Settle;
        if (!_hooked)
        {
            CompositionTarget.Rendering += OnFrame;
            _hooked = true;
        }
    }

    private double Progress => _kind == Kind.None ? 1 : _waitFrames > 0 ? 0 : Math.Min(1, _clock.Elapsed / Duration);

    /// <summary>
    /// The same bars as before, with new figures: the same period read again (coming back to the page, the minute's
    /// refresh), not another one. Then nothing moves; the new figures just show (2026-11-06, the user: coming back to
    /// Reports "should not animate").
    /// </summary>
    internal static bool SameDays(IReadOnlyList<Core.Reports.DayBucket>? old, IReadOnlyList<Core.Reports.DayBucket>? now)
    {
        if (old is not { Count: > 0 } || now is not { Count: > 0 } || old.Count != now.Count) return false;
        for (int i = 0; i < old.Count; i++)
            if (old[i].Day != now[i].Day) return false;
        return true;
    }

    /// <summary>The mock-up's easing: a quartic ease out (1 − (1 − t)⁴).</summary>
    internal static double Ease(double t) => 1 - Math.Pow(1 - t, 4);

    /// <summary>While moving: how far along (0 to 1, eased); null when the values stand.</summary>
    public double? Moving => _kind == Kind.Move ? Ease(Progress) : null;

    /// <summary>While swapping: the old values are still the ones drawn (the first half).</summary>
    public bool ShowsOld => _kind == Kind.Swap && Progress < 0.5;

    /// <summary>The opacity to draw at: while swapping, the old fading out, then the new fading in (the mock-up's).</summary>
    public double Opacity
    {
        get
        {
            if (_kind != Kind.Swap) return 1;
            double p = Progress, e = Ease(p);
            return Math.Clamp(p < 0.5 ? 1 - e * 1.6 : (e - 0.4) * 1.7, 0, 1);
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_waitFrames > 0 && --_waitFrames == 0) _clock.Restart();
        if (Progress >= 1)
        {
            _kind = Kind.None;
            Unhook();
        }
        owner.InvalidateVisual();
    }

    private void Unhook()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnFrame;
        _hooked = false;
    }
}

/// <summary>Running something a few frames on, once a change has been laid out and shown (see <see cref="Settle"/>).</summary>
internal static class Frames
{
    /// <summary>
    /// How many frames go by before motion starts after a change: the frame that lays the change out (which may take a
    /// while), and the one after it, the first that shows it.
    /// </summary>
    public const int Settle = 2;

    /// <summary>Runs <paramref name="then"/> after <see cref="Settle"/> frames.</summary>
    public static void After(Action then)
    {
        int left = Settle;
        void Tick(object? sender, EventArgs e)
        {
            if (--left > 0) return;
            CompositionTarget.Rendering -= Tick;
            then();
        }
        CompositionTarget.Rendering += Tick;
    }
}
