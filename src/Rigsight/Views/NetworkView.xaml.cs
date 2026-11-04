using System.Windows.Controls;

namespace Rigsight.Views;

public partial class NetworkView : UserControl
{
    /// <summary>The graph's height.</summary>
    public const double ChartHeight = 320;

    /// <summary>The Apps and What happened cards' height: side by side at one height, each scrolling inside.</summary>
    public const double ListHeight = 560;

    public NetworkView() => InitializeComponent();
}
