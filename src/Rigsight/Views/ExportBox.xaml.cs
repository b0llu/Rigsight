using System.Windows.Controls;
using System.Windows.Input;
using Rigsight.ViewModels;

namespace Rigsight.Views;

/// <summary>
/// The Export box over the window. Its own part here is the custom period's calendar (see <see cref="DayRangeCalendar"/>).
/// </summary>
public partial class ExportBox : UserControl
{
    private readonly DayRangeCalendar _calendar;

    public ExportBox()
    {
        InitializeComponent();
        _calendar = new DayRangeCalendar(RangePopup, Cal, RangeHint, () => DataContext as ExportViewModel);
    }

    /// <summary>"Custom" opens the calendar on the days it has now; picked again, it opens again.</summary>
    private void Custom_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenCalendar();
    }

    internal void OpenCalendar() => _calendar.Open();

    internal void PickDay(DateTime day) => _calendar.PickDay(day);
}
