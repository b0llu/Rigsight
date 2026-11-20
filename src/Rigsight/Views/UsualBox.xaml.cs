using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Rigsight.Views;

/// <summary>
/// Your usual, over the page (see UsualViewModel). Built once and kept: each time it shows it fades in (no slide, as
/// every pop-in), with Windows' animations on. Everything else is bound; Esc and a click outside close it in the window.
/// </summary>
public partial class UsualBox : UserControl
{
    public UsualBox()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            if (!SystemParameters.ClientAreaAnimation) { Panel.Opacity = 1; return; }
            Panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, (Duration)FindResource("MotionNormal")));
        };
    }
}
