using System.Windows.Controls;

namespace Rigsight.Views;

public partial class ReportsView : UserControl
{
    /// <summary>The "Apps you used" box: about eight rows, then it scrolls.</summary>
    public const double AppsMaxHeight = 430;

    public ReportsView() => InitializeComponent();
}
