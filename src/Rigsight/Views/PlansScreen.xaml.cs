using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rigsight.Converters;
using Rigsight.Services;

namespace Rigsight.Views;

/// <summary>RigSight AI's plans over the whole window; everything it shows and does is PlansViewModel's.</summary>
public partial class PlansScreen : UserControl
{
    public PlansScreen()
    {
        InitializeComponent();
        // The words on the plan to pick (its button and badge) are dark or white by the blue and violet in use now:
        // worked out when shown and again when the theme or accent changes (a fixed dark didn't read in Light).
        Loaded += (_, _) => { ThemeManager.Changed += SetPickInk; SetPickInk(); };
        Unloaded += (_, _) => ThemeManager.Changed -= SetPickInk;
        SetPickInk();
    }

    private void SetPickInk()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(SetPickInk); return; }
        if (TryFindResource("CpuColor") is not Color blue || TryFindResource("PurpleColor") is not Color violet) return;
        var ink = new SolidColorBrush(InkOnConverter.InkFor(blue, violet));
        ink.Freeze();
        Resources["PickInk"] = ink;
    }
}
