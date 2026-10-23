using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rigsight.ViewModels;

namespace Rigsight.Views;

/// <summary>
/// A page's own Export box. Its own part here is picking a period: a button picks it at once, Custom opens the calendar
/// (see <see cref="DayRangeCalendar"/>) and is picked when its days are.
/// </summary>
public partial class PageExportBox : UserControl
{
    private readonly DayRangeCalendar _calendar;

    public PageExportBox()
    {
        InitializeComponent();
        _calendar = new DayRangeCalendar(RangePopup, Cal, RangeHint, () => Vm);
    }

    private PageExportViewModel? Vm => DataContext as PageExportViewModel;

    /// <summary>The button doesn't tick itself: the period is picked here (Custom only once its days are).</summary>
    private void Period_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Pick(sender);
    }

    private void Period_Key(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled = true;
        Pick(sender);
    }

    private void Pick(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is not PeriodChoice choice || Vm is not { } vm) return;
        if (choice.IsCustom) OpenCalendar();
        else vm.PickPeriod(choice);
    }

    internal void OpenCalendar() => _calendar.Open();

    internal void PickDay(DateTime day) => _calendar.PickDay(day);
}
