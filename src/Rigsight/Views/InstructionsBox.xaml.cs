using System.Windows.Controls;

namespace Rigsight.Views;

/// <summary>The instructions box over the window (see <see cref="ViewModels.InstructionsEditorViewModel"/>).</summary>
public partial class InstructionsBox : UserControl
{
    public InstructionsBox()
    {
        InitializeComponent();
        // Ready to type in as it opens, at the start of the text.
        Loaded += (_, _) => { Box.Focus(); Box.CaretIndex = 0; Box.ScrollToHome(); };
    }
}
