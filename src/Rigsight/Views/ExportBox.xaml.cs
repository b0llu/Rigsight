using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.ViewModels;

namespace Rigsight.Views;

/// <summary>
/// The Export box over the window. Its own part here is the custom period's calendar: a click on the first day, a
/// click on the last, and the days between are the period (the calendar's own way wants a drag or Shift).
/// </summary>
public partial class ExportBox : UserControl
{
    public ExportBox()
    {
        InitializeComponent();
        // Under the period buttons, its right edge on theirs.
        RangePopup.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width + 8, target.Height + 6), PopupPrimaryAxis.Horizontal)];
        RangePopup.Closed += (_, _) => _first = null;
        // Days that can't be picked are faded, on top of the theme's own calendar style (as in the pages' date picker).
        var style = new Style(typeof(CalendarDayButton), Cal.CalendarDayButtonStyle);
        var blocked = new Trigger { Property = CalendarDayButton.IsBlackedOutProperty, Value = true };
        blocked.Setters.Add(new Setter(OpacityProperty, 0.28));
        style.Triggers.Add(blocked);
        Cal.CalendarDayButtonStyle = style;
    }

    private ExportViewModel? Vm => DataContext as ExportViewModel;

    /// <summary>The first day clicked, while the last is still to come.</summary>
    private DateTime? _first;

    // Set while the calendar is being set up or marked by us, so its change events aren't taken as a click.
    private bool _marking;

    /// <summary>"Custom" opens the calendar on the days it has now; picked again, it opens again.</summary>
    private void Custom_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenCalendar();
    }

    internal void OpenCalendar()
    {
        if (Vm is not { } vm) return;
        var today = DateTime.Today;
        var first = vm.FirstDay is { } f && f.Date <= today ? f.Date : today; // the day alone: a time of day would turn the blacked-out range round
        var (from, to) = (Clamp(vm.CustomFrom, first, today), Clamp(vm.CustomTo, first, today));
        _first = null;
        _marking = true;
        // Only the days there is history for: none before the first recorded, none after today.
        Cal.SelectedDates.Clear();
        Cal.BlackoutDates.Clear();
        Cal.DisplayDateStart = Cal.DisplayDateEnd = null;
        var (monthStart, monthEnd) = (new DateTime(first.Year, first.Month, 1), new DateTime(today.Year, today.Month, 1).AddMonths(1).AddDays(-1));
        Cal.DisplayDate = to;
        Cal.DisplayDateStart = monthStart;
        Cal.DisplayDateEnd = monthEnd;
        if (first > monthStart) Cal.BlackoutDates.Add(new CalendarDateRange(monthStart, first.AddDays(-1)));
        if (today < monthEnd) Cal.BlackoutDates.Add(new CalendarDateRange(today.AddDays(1), monthEnd));
        Cal.SelectedDates.AddRange(from <= to ? from : to, from <= to ? to : from);
        _marking = false;
        RangeHint.Text = "Click the first day, then the last";
        RangePopup.IsOpen = true;
    }

    private static DateTime Clamp(DateTime day, DateTime first, DateTime last) => day < first ? first : day > last ? last : day.Date;

    /// <summary>
    /// The calendar's own picking: a click leaves one day picked. The first is the period's first day; the next closes
    /// the period and the calendar. Days dragged over, or picked with Shift, are a period at once.
    /// </summary>
    private void Cal_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_marking || Cal.SelectedDates.Count == 0) return;
        if (Cal.SelectedDates.Count > 1) Finish(Cal.SelectedDates.Min(), Cal.SelectedDates.Max());
        else PickDay(Cal.SelectedDates[0]);
    }

    /// <summary>The first day picked again as the last (a period of one day): the calendar says nothing then, so the click is looked at.</summary>
    private void Cal_Up(object sender, MouseButtonEventArgs e)
    {
        // Not the click that picked the first day itself: a press after it.
        if (_first is { } first && _presses > _firstPress && (e.OriginalSource as FrameworkElement)?.DataContext is DateTime day && day.Date == first) Finish(first, first);
    }

    // Presses on the calendar, and the one that picked the first day.
    private int _presses, _firstPress;

    private void Cal_Down(object sender, MouseButtonEventArgs e) => _presses++;

    internal void PickDay(DateTime day)
    {
        if (_first is { } first)
        {
            Finish(first, day);
            return;
        }
        _first = day.Date;
        _firstPress = _presses;
        RangeHint.Text = "Now click the last day";
        // The calendar keeps the mouse after a click: back to the box, so a click beside it still closes it.
        Mouse.Capture(RangePopup.Child, CaptureMode.SubTree);
    }

    private void Finish(DateTime a, DateTime b)
    {
        _first = null;
        RangePopup.IsOpen = false;
        Mouse.Capture(null);
        Vm?.PickCustom(a, b);
    }
}
