using System.Windows.Controls;

namespace Rigsight.Views;

public partial class NetworkView : UserControl
{
    /// <summary>The Apps box: about nine rows, then it scrolls.</summary>
    public const double AppsMaxHeight = 460;

    public NetworkView() => InitializeComponent();
}
