using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Rigsight.Views;

/// <summary>What an Export box's custom period calendar works on: the days there is history for, and the days picked.</summary>
public interface IDayRange
{
    /// <summary>The first day there is history for; null with none yet.</summary>
    DateTime? FirstDay { get; }
    /// <summary>The custom period's first and last day (both in it).</summary>
    DateTime CustomFrom { get; }
    DateTime CustomTo { get; }
    /// <summary>The custom period picked in the calendar: its two days, in either order.</summary>
    void PickCustom(DateTime a, DateTime b);
}

/// <summary>
/// The custom period's calendar in an Export box: a click on the first day, a click on the last, and the days between
/// are the period (the calendar's own way wants a drag or Shift). It floats under the period buttons, its right edge
/// on theirs, so nothing in the box moves.
/// </summary>
internal sealed class DayRangeCalendar
{
    private readonly Popup _popup;
    private readonly Calendar _cal;
    private readonly TextBlock _hint;
    private readonly Func<IDayRange?> _host;

    public DayRangeCalendar(Popup popup, Calendar cal, TextBlock hint, Func<IDayRange?> host)
    {
        (_popup, _cal, _hint, _host) = (popup, cal, hint, host);
        _popup.CustomPopupPlacementCallback = (p, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - p.Width + 8, target.Height + 6), PopupPrimaryAxis.Horizontal)];
        _popup.Closed += (_, _) => _first = null;
        // Days that can't be picked are faded, on top of the theme's own calendar style (as in the pages' date picker).
        var style = new Style(typeof(CalendarDayButton), _cal.CalendarDayButtonStyle);
        var blocked = new Trigger { Property = CalendarDayButton.IsBlackedOutProperty, Value = true };
        blocked.Setters.Add(new Setter(UIElement.OpacityProperty, 0.28));
        style.Triggers.Add(blocked);
        _cal.CalendarDayButtonStyle = style;
        _cal.SelectedDatesChanged += SelectedDatesChanged;
        _cal.PreviewMouseLeftButtonDown += (_, _) => _presses++;
        _cal.PreviewMouseLeftButtonUp += Up;
    }

    /// <summary>The first day clicked, while the last is still to come.</summary>
    private DateTime? _first;

    // Set while the calendar is being set up or marked by us, so its change events aren't taken as a click.
    private bool _marking;

    // Presses on the calendar, and the one that picked the first day.
    private int _presses, _firstPress;

    /// <summary>Opens on the days the period has now.</summary>
    public void Open()
    {
        if (_host() is not { } vm) return;
        var today = DateTime.Today;
        var first = vm.FirstDay is { } f && f.Date <= today ? f.Date : today; // the day alone: a time of day would turn the blacked-out range round
        var (from, to) = (Clamp(vm.CustomFrom, first, today), Clamp(vm.CustomTo, first, today));
        _first = null;
        _marking = true;
        // Only the days there is history for: none before the first recorded, none after today.
        _cal.SelectedDates.Clear();
        _cal.BlackoutDates.Clear();
        _cal.DisplayDateStart = _cal.DisplayDateEnd = null;
        var (monthStart, monthEnd) = (new DateTime(first.Year, first.Month, 1), new DateTime(today.Year, today.Month, 1).AddMonths(1).AddDays(-1));
        _cal.DisplayDate = to;
        _cal.DisplayDateStart = monthStart;
        _cal.DisplayDateEnd = monthEnd;
        if (first > monthStart) _cal.BlackoutDates.Add(new CalendarDateRange(monthStart, first.AddDays(-1)));
        if (today < monthEnd) _cal.BlackoutDates.Add(new CalendarDateRange(today.AddDays(1), monthEnd));
        _cal.SelectedDates.AddRange(from <= to ? from : to, from <= to ? to : from);
        _marking = false;
        _hint.Text = Core.Lang.T("Click the first day, then the last");
        _popup.IsOpen = true;
    }

    private static DateTime Clamp(DateTime day, DateTime first, DateTime last) => day < first ? first : day > last ? last : day.Date;

    /// <summary>
    /// The calendar's own picking: a click leaves one day picked. The first is the period's first day; the next closes
    /// the period and the calendar. Days dragged over, or picked with Shift, are a period at once.
    /// </summary>
    private void SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_marking || _cal.SelectedDates.Count == 0) return;
        if (_cal.SelectedDates.Count > 1) Finish(_cal.SelectedDates.Min(), _cal.SelectedDates.Max());
        else PickDay(_cal.SelectedDates[0]);
    }

    /// <summary>The first day picked again as the last (a period of one day): the calendar says nothing then, so the click is looked at.</summary>
    private void Up(object sender, MouseButtonEventArgs e)
    {
        // Not the click that picked the first day itself: a press after it.
        if (_first is { } first && _presses > _firstPress && (e.OriginalSource as FrameworkElement)?.DataContext is DateTime day && day.Date == first) Finish(first, first);
    }

    public void PickDay(DateTime day)
    {
        if (_first is { } first)
        {
            Finish(first, day);
            return;
        }
        _first = day.Date;
        _firstPress = _presses;
        _hint.Text = Core.Lang.T("Now click the last day");
        // The calendar keeps the mouse after a click: back to the box, so a click beside it still closes it.
        Mouse.Capture(_popup.Child, CaptureMode.SubTree);
    }

    private void Finish(DateTime a, DateTime b)
    {
        _first = null;
        _popup.IsOpen = false;
        Mouse.Capture(null);
        _host()?.PickCustom(a, b);
    }
}
