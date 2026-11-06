using System.Windows;

namespace Rigsight.Controls;

/// <summary>
/// Whether the figures under an element move to their new values (<see cref="CountUp"/>, <see cref="SmoothBar"/>), or
/// just show them. Reports sets it per load: true when another period was picked, false when the same one is read again
/// (coming back to the page, the minute's refresh). The motion is for a change of period only; coming back and seeing
/// the figures count up looked like a fault (2026-11-06, the user). Inherited, so it is set once on the page's figures.
/// </summary>
public static class Motion
{
    public static readonly DependencyProperty MovesProperty = DependencyProperty.RegisterAttached(
        "Moves", typeof(bool), typeof(Motion), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetMoves(DependencyObject d) => (bool)d.GetValue(MovesProperty);
    public static void SetMoves(DependencyObject d, bool value) => d.SetValue(MovesProperty, value);
}
