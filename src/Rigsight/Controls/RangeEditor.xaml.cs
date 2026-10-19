using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Rigsight.Core;

namespace Rigsight.Controls;

/// <summary>
/// The custom range editor: a pop-up with a date and hour to start from and another to end at, under the control it
/// is opened from. The pages' period picker and the temperature graph both use it, so a custom range is picked the
/// same way everywhere.
/// </summary>
public partial class RangeEditor : UserControl
{
    public RangeEditor()
    {
        InitializeComponent();
        // Under the target's right edge, like the pager's calendar: the pickers sit at the right of page headers.
        RangePopup.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width + 8, target.Height + 6), PopupPrimaryAxis.Horizontal)];
        foreach (var box in new[] { FromHour, ToHour })
            for (int h = 0; h < 24; h++) box.Items.Add(new ComboBoxItem { Content = PeriodPicker.HourText(h), Tag = h });
        // The end follows the start: never before it, and moved along when the start passes it.
        FromDate.SelectedDateChanged += (_, _) => KeepInOrder();
        FromHour.SelectionChanged += (_, _) => KeepInOrder();
        ToDate.SelectedDateChanged += (_, _) => KeepInOrder();
    }

    /// <summary>The range picked, once "Show" is pressed with a range that can be shown.</summary>
    public event Action<DateTime, DateTime>? Applied;

    private DateTime? _minDate;

    public bool IsOpen => RangePopup.IsOpen;

    public void Close() => RangePopup.IsOpen = false;

    /// <summary>Opens under <paramref name="target"/> on a range (none yet: today), the start's calendar from <paramref name="minDate"/>.</summary>
    public void Open(UIElement target, DateTime from, DateTime to, DateTime? minDate)
    {
        _minDate = minDate;
        if (to <= from) (from, to) = (DateTime.Today, DateTime.Today.AddDays(1));
        RangePopup.PlacementTarget = target;
        _ordering = true;
        // Wide open while the dates are set (a date outside a calendar's range isn't taken), then narrowed.
        FromDate.DisplayDateStart = ToDate.DisplayDateStart = null;
        FromDate.DisplayDateEnd = ToDate.DisplayDateEnd = null;
        FromDate.SelectedDate = from.Date;
        FromHour.SelectedIndex = from.Hour;
        ToDate.SelectedDate = to.Date;
        ToHour.SelectedIndex = to.Hour;
        _ordering = false;
        KeepInOrder();
        FromDate.DisplayDateStart = minDate is { } min && min <= from.Date ? min : from.Date;
        FromDate.DisplayDateEnd = DateTime.Today;
        RangeError.Visibility = Visibility.Collapsed;
        RangePopup.IsOpen = true;
    }

    private bool _ordering;

    /// <summary>
    /// Keeps the editor's end after its start: the end's calendar starts at the start's day, its hours on that day
    /// only after the start's hour, and an end the start has passed moves to an hour after it. The start's hours today
    /// stop at the current hour (nothing to show after it).
    /// </summary>
    private void KeepInOrder()
    {
        if (_ordering) return;
        _ordering = true;
        try
        {
            var now = DateTime.Now;
            bool startToday = FromDate.SelectedDate?.Date == now.Date;
            for (int h = 0; h < 24; h++) ((ComboBoxItem)FromHour.Items[h]).IsEnabled = !startToday || h <= now.Hour;
            if (startToday && FromHour.SelectedIndex > now.Hour) FromHour.SelectedIndex = now.Hour;

            if (FromDate.SelectedDate is not { } fromDay || FromHour.SelectedIndex < 0)
            {
                ToDate.DisplayDateStart = _minDate;
                ToDate.DisplayDateEnd = DateTime.Today.AddDays(1);
                return;
            }
            var from = fromDay.Date.AddHours(FromHour.SelectedIndex);
            var earliest = from.AddHours(1);
            if (ToDate.SelectedDate is not { } toDay || toDay.Date.AddHours(Math.Max(0, ToHour.SelectedIndex)) < earliest)
            {
                // Passed by the start (or not picked): an hour after the start.
                ToDate.SelectedDate = toDay = earliest.Date;
                ToHour.SelectedIndex = earliest.Hour;
            }
            ToDate.DisplayDateStart = earliest.Date;
            ToDate.DisplayDateEnd = DateTime.Today.AddDays(1) > toDay.Date ? DateTime.Today.AddDays(1) : toDay.Date;
            bool sameDay = toDay.Date == earliest.Date;
            for (int h = 0; h < 24; h++) ((ComboBoxItem)ToHour.Items[h]).IsEnabled = !sameDay || h >= earliest.Hour;
            if (sameDay && ToHour.SelectedIndex < earliest.Hour) ToHour.SelectedIndex = earliest.Hour;
        }
        finally
        {
            _ordering = false;
        }
    }

    /// <summary>The range typed into the editor, or why it can't be shown.</summary>
    public static (DateTime From, DateTime To, string? Error) ReadRange(DateTime? fromDate, int fromHour, DateTime? toDate, int toHour, DateTime now)
    {
        if (fromDate is not { } fd || toDate is not { } td || fromHour < 0 || toHour < 0) return (default, default, Lang.T("Pick a date and hour for both ends."));
        var from = fd.Date.AddHours(fromHour);
        var to = td.Date.AddHours(toHour);
        if (to <= from) return (from, to, Lang.T("The end has to be after the start."));
        if (from >= now) return (from, to, Lang.T("That's still to come."));
        return (from, to, null);
    }

    /// <summary>"Show": the range, or why it can't be shown under the fields.</summary>
    internal void Apply()
    {
        var (from, to, error) = ReadRange(FromDate.SelectedDate, FromHour.SelectedIndex, ToDate.SelectedDate, ToHour.SelectedIndex, DateTime.Now);
        if (error is not null)
        {
            RangeError.Text = error;
            RangeError.Visibility = Visibility.Visible;
            return;
        }
        RangePopup.IsOpen = false;
        Applied?.Invoke(from, to);
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Apply();

    private void Cancel_Click(object sender, RoutedEventArgs e) => RangePopup.IsOpen = false;
}
