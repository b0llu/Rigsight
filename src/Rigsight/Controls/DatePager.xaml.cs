using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Rigsight.Controls;

/// <summary>
/// "‹ Yesterday ›": steps through periods, says which one is shown, and opens a calendar to jump to any day
/// (or, with <see cref="PickMonth"/>, any month) since tracking started.
/// </summary>
public partial class DatePager : UserControl
{
    public DatePager()
    {
        InitializeComponent();
        // Right-aligned under the button: the pager sits at the right of page headers, so the calendar opens inwards.
        Picker.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width + 8, target.Height + 6), PopupPrimaryAxis.Horizontal)];
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(DatePager), new PropertyMetadata(""));

    public static readonly DependencyProperty DateProperty =
        DependencyProperty.Register(nameof(Date), typeof(DateTime), typeof(DatePager),
            new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>First day with history (the calendar doesn't go earlier).</summary>
    public static readonly DependencyProperty MinDateProperty =
        DependencyProperty.Register(nameof(MinDate), typeof(DateTime?), typeof(DatePager), new PropertyMetadata(null));

    public static readonly DependencyProperty PickMonthProperty =
        DependencyProperty.Register(nameof(PickMonth), typeof(bool), typeof(DatePager), new PropertyMetadata(false));

    public static readonly DependencyProperty CanGoPreviousProperty =
        DependencyProperty.Register(nameof(CanGoPrevious), typeof(bool), typeof(DatePager), new PropertyMetadata(true));

    public static readonly DependencyProperty CanGoNextProperty =
        DependencyProperty.Register(nameof(CanGoNext), typeof(bool), typeof(DatePager), new PropertyMetadata(true));

    public static readonly DependencyProperty PreviousCommandProperty =
        DependencyProperty.Register(nameof(PreviousCommand), typeof(ICommand), typeof(DatePager));

    public static readonly DependencyProperty NextCommandProperty =
        DependencyProperty.Register(nameof(NextCommand), typeof(ICommand), typeof(DatePager));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public DateTime Date { get => (DateTime)GetValue(DateProperty); set => SetValue(DateProperty, value); }
    public DateTime? MinDate { get => (DateTime?)GetValue(MinDateProperty); set => SetValue(MinDateProperty, value); }
    public bool PickMonth { get => (bool)GetValue(PickMonthProperty); set => SetValue(PickMonthProperty, value); }
    public bool CanGoPrevious { get => (bool)GetValue(CanGoPreviousProperty); set => SetValue(CanGoPreviousProperty, value); }
    public bool CanGoNext { get => (bool)GetValue(CanGoNextProperty); set => SetValue(CanGoNextProperty, value); }
    public ICommand? PreviousCommand { get => (ICommand?)GetValue(PreviousCommandProperty); set => SetValue(PreviousCommandProperty, value); }
    public ICommand? NextCommand { get => (ICommand?)GetValue(NextCommandProperty); set => SetValue(NextCommandProperty, value); }

    // Set while the calendar is being prepared, so its own change events aren't taken as the user's pick.
    private bool _preparing;

    private void Previous_Click(object sender, RoutedEventArgs e) => PreviousCommand?.Execute(null);
    private void Next_Click(object sender, RoutedEventArgs e) => NextCommand?.Execute(null);

    private bool _styled;

    /// <summary>Fades days that can't be picked, on top of the theme's own calendar style.</summary>
    private void StyleCalendar()
    {
        if (_styled) return;
        _styled = true;
        var style = new Style(typeof(CalendarDayButton), Cal.CalendarDayButtonStyle);
        var trigger = new Trigger { Property = CalendarDayButton.IsBlackedOutProperty, Value = true };
        trigger.Setters.Add(new Setter(OpacityProperty, 0.28));
        style.Triggers.Add(trigger);
        Cal.CalendarDayButtonStyle = style;
    }

    private DateTime _min;
    private int _year;

    private void Label_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        _min = MinDate is { } m && m.Date <= today ? m.Date : today;
        var shown = Date.Date < _min ? _min : Date.Date > today ? today : Date.Date;
        Cal.Visibility = PickMonth ? Visibility.Collapsed : Visibility.Visible;
        MonthPanel.Visibility = PickMonth ? Visibility.Visible : Visibility.Collapsed;
        if (PickMonth)
        {
            _year = shown.Year;
            BuildMonths();
        }
        else
        {
            PrepareCalendar(shown);
        }
        SinceText.Text = MinDate is { } since ? $"History from {since:d MMM yyyy}" : "";
        Picker.IsOpen = true;
    }

    private void PrepareCalendar(DateTime shown)
    {
        StyleCalendar();
        _preparing = true;
        var today = DateTime.Today;
        Cal.DisplayDateStart = null;
        Cal.DisplayDateEnd = null;
        Cal.BlackoutDates.Clear();
        Cal.SelectedDate = null;
        Cal.DisplayDate = shown;
        // Whole months are shown (no blank gaps); days without history are greyed out and can't be picked.
        var firstMonth = new DateTime(_min.Year, _min.Month, 1);
        var lastMonthEnd = new DateTime(today.Year, today.Month, 1).AddMonths(1).AddDays(-1);
        Cal.DisplayDateStart = firstMonth;
        Cal.DisplayDateEnd = lastMonthEnd;
        if (_min > firstMonth) Cal.BlackoutDates.Add(new CalendarDateRange(firstMonth, _min.AddDays(-1)));
        if (today < lastMonthEnd) Cal.BlackoutDates.Add(new CalendarDateRange(today.AddDays(1), lastMonthEnd));
        Cal.SelectedDate = shown;
        _preparing = false;
    }

    private void BuildMonths()
    {
        YearText.Text = _year.ToString();
        PreviousYear.IsEnabled = _year > _min.Year;
        NextYear.IsEnabled = _year < DateTime.Today.Year;
        MonthGrid.Children.Clear();
        var firstMonth = new DateTime(_min.Year, _min.Month, 1);
        var selected = new DateTime(Date.Year, Date.Month, 1);
        for (int month = 1; month <= 12; month++)
        {
            var start = new DateTime(_year, month, 1);
            bool available = start >= firstMonth && start <= DateTime.Today;
            var button = new Button
            {
                Content = start.ToString("MMM"),
                Style = (Style)FindResource("GhostButton"),
                Background = start == selected ? (Brush)FindResource("AccentSoftBrush") : Brushes.Transparent,
                Margin = new Thickness(2),
                Padding = new Thickness(0, 4, 0, 4),
                IsEnabled = available,
                Opacity = available ? 1 : 0.28,
            };
            button.Click += (_, _) => Pick(start);
            MonthGrid.Children.Add(button);
        }
    }

    private void PreviousYear_Click(object sender, RoutedEventArgs e)
    {
        _year--;
        BuildMonths();
    }

    private void NextYear_Click(object sender, RoutedEventArgs e)
    {
        _year++;
        BuildMonths();
    }

    private void Cal_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_preparing || PickMonth || Cal.SelectedDate is not { } day) return;
        Pick(day);
    }

    private void Today_Click(object sender, RoutedEventArgs e) => Pick(DateTime.Today);

    private void Pick(DateTime day)
    {
        Picker.IsOpen = false;
        Date = day.Date;
        // The calendar keeps mouse capture after a click; release it so the page responds straight away.
        Mouse.Capture(null);
    }
}
