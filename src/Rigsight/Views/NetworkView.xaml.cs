using System.Windows.Controls;

namespace Rigsight.Views;

public partial class NetworkView : UserControl
{
    /// <summary>The usage chart's height (the apps card beside it is as tall as the chart's card).</summary>
    public const double ChartHeight = 320;

    public NetworkView() => InitializeComponent();
}
