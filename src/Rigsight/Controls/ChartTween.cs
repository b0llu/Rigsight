using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// A drawn chart's way from one set of values to the next (issue #11, 2026-11-06: switching Reports' periods "feels
/// abrupt"): its bars or lines move from where they were in a quarter of a second, slowing as they land; when what
/// they are changes (a day's hours to a week's days), the new ones fade in instead. With Windows' animations turned
/// off, nothing moves: the new values are drawn at once.
/// </summary>
internal sealed class ChartTween(FrameworkElement owner)
{
    public enum Kind { None, Move, Fade }

    private static readonly TimeSpan MoveTime = TimeSpan.FromMilliseconds(250), FadeTime = TimeSpan.FromMilliseconds(220);

    private readonly Stopwatch _clock = new();
    private Kind _kind;
    private bool _hooked;

    /// <summary>Starts moving (or fading) from the values drawn until now.</summary>
    public void Start(Kind kind)
    {
        _kind = SystemParameters.ClientAreaAnimation && owner.IsVisible ? kind : Kind.None;
        if (_kind == Kind.None) { Unhook(); return; }
        _clock.Restart();
        if (!_hooked)
        {
            CompositionTarget.Rendering += OnFrame;
            _hooked = true;
        }
    }

    private double Progress => _kind switch
    {
        Kind.Move => Math.Min(1, _clock.Elapsed / MoveTime),
        Kind.Fade => Math.Min(1, _clock.Elapsed / FadeTime),
        _ => 1,
    };

    private static double Ease(double t) => 1 - Math.Pow(1 - t, 4);

    /// <summary>While moving: how far along (0 to 1, eased); null when the values stand.</summary>
    public double? Moving => _kind == Kind.Move ? Ease(Progress) : null;

    /// <summary>The opacity to draw the values at: rising to 1 while they fade in, else 1.</summary>
    public double Opacity => _kind == Kind.Fade ? 0.1 + 0.9 * Ease(Progress) : 1;

    private void OnFrame(object? sender, EventArgs e)
    {
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
