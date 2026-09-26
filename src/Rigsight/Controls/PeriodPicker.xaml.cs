using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Reports;

namespace Rigsight.Controls;

/// <summary>
/// The period a page looks at: a day, a Monday-to-Sunday week, a month, a year, all time, or a custom range (from a
/// date and hour to another). The arrows step to the period before or after (a custom range by its own length), and the
/// label opens the matching picker (a calendar for a day or week, a month grid, a year list, the range editor).
/// Reports, Apps and Crashes all use it, so a period means the same thing everywhere.
/// </summary>
public partial class PeriodPicker : UserControl
{
    public PeriodPicker()
    {
        InitializeComponent();
        Pager.PreviousCommand = new RelayCommand(() => Step(-1));
        Pager.NextCommand = new RelayCommand(() => Step(1));
        // Under the pager's right edge, like its calendar: the picker sits at the right of page headers.
        RangePopup.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width + 8, target.Height + 6), PopupPrimaryAxis.Horizontal)];
        foreach (var box in new[] { FromHour, ToHour })
            for (int h = 0; h < 24; h++) box.Items.Add(new ComboBoxItem { Content = HourText(h), Tag = h });
        // The end follows the start: never before it, and moved along when the start passes it.
        FromDate.SelectedDateChanged += (_, _) => KeepInOrder();
        FromHour.SelectionChanged += (_, _) => KeepInOrder();
        ToDate.SelectedDateChanged += (_, _) => KeepInOrder();
        Loaded += (_, _) => Refresh();
    }

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(ReportRange), typeof(PeriodPicker),
            new FrameworkPropertyMetadata(ReportRange.Day, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((PeriodPicker)d).Refresh()));

    /// <summary>Any day in the period shown.</summary>
    public static readonly DependencyProperty AnchorProperty =
        DependencyProperty.Register(nameof(Anchor), typeof(DateTime), typeof(PeriodPicker),
            new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((PeriodPicker)d).Refresh()));

    /// <summary>The first day with history: nothing before it to step back to or pick.</summary>
    public static readonly DependencyProperty MinDateProperty =
        DependencyProperty.Register(nameof(MinDate), typeof(DateTime?), typeof(PeriodPicker), new PropertyMetadata(null, (d, _) => ((PeriodPicker)d).Refresh()));

    /// <summary>Whether "All time" is offered (Reports leaves it out).</summary>
    public static readonly DependencyProperty AllowAllProperty =
        DependencyProperty.Register(nameof(AllowAll), typeof(bool), typeof(PeriodPicker), new PropertyMetadata(true, (d, _) => ((PeriodPicker)d).Refresh()));

    /// <summary>Start of a custom range (whole hours).</summary>
    public static readonly DependencyProperty CustomFromProperty =
        DependencyProperty.Register(nameof(CustomFrom), typeof(DateTime), typeof(PeriodPicker),
            new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((PeriodPicker)d).Refresh()));

    /// <summary>End of a custom range (whole hours; not included).</summary>
    public static readonly DependencyProperty CustomToProperty =
        DependencyProperty.Register(nameof(CustomTo), typeof(DateTime), typeof(PeriodPicker),
            new FrameworkPropertyMetadata(default(DateTime), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((PeriodPicker)d).Refresh()));

    public DateTime CustomFrom { get => (DateTime)GetValue(CustomFromProperty); set => SetValue(CustomFromProperty, value); }
    public DateTime CustomTo { get => (DateTime)GetValue(CustomToProperty); set => SetValue(CustomToProperty, value); }

    public ReportRange Unit { get => (ReportRange)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public DateTime Anchor { get => (DateTime)GetValue(AnchorProperty); set => SetValue(AnchorProperty, value); }
    public DateTime? MinDate { get => (DateTime?)GetValue(MinDateProperty); set => SetValue(MinDateProperty, value); }
    public bool AllowAll { get => (bool)GetValue(AllowAllProperty); set => SetValue(AllowAllProperty, value); }

    /// <summary>A period's start and end: a custom range's own, or the unit's around <paramref name="anchor"/>.</summary>
    public static (DateTime From, DateTime To) Bounds(ReportRange unit, DateTime anchor, DateTime customFrom, DateTime customTo) =>
        unit == ReportRange.Custom && customTo > customFrom ? (customFrom, customTo) : ReportBuilder.Bounds(unit, anchor);

    /// <summary>The period in words, custom ranges included ("Thu 25 Sep, 8 AM – Fri 26 Sep, 1 AM").</summary>
    public static string Text(ReportRange unit, DateTime anchor, DateTime customFrom, DateTime customTo) =>
        unit == ReportRange.Custom ? (customTo > customFrom ? Report.CustomTitle(customFrom, customTo) : "Custom") : Text(unit, anchor);

    /// <summary>The dates a period covers; for a custom range (its dates already on the picker) how long it is.</summary>
    public static string Span(ReportRange unit, DateTime anchor, DateTime? firstDay, DateTime customFrom, DateTime customTo) =>
        unit == ReportRange.Custom ? (customTo > customFrom ? Duration(customTo - customFrom) : "") : Span(unit, anchor, firstDay);

    /// <summary>The period in words: "Today", "Last week", "August 2026", "2025", "All time".</summary>
    public static string Text(ReportRange unit, DateTime anchor)
    {
        if (unit == ReportRange.All) return "All time";
        var (from, to) = ReportBuilder.Bounds(unit, anchor);
        var (thisFrom, _) = ReportBuilder.Bounds(unit, DateTime.Today);
        var (lastFrom, _) = ReportBuilder.Bounds(unit, ReportBuilder.Previous(unit, DateTime.Today));
        return unit switch
        {
            ReportRange.Day when from == thisFrom => "Today",
            ReportRange.Day when from == lastFrom => "Yesterday",
            ReportRange.Day => from.Year == DateTime.Today.Year ? from.ToString("ddd, d MMM") : from.ToString("d MMM yyyy"),
            ReportRange.Week when from == thisFrom => "This week",
            ReportRange.Week when from == lastFrom => "Last week",
            ReportRange.Week => (from.Month == to.AddDays(-1).Month
                ? $"{from:%d}–{to.AddDays(-1):d MMM}"
                : $"{from:d MMM} – {to.AddDays(-1):d MMM}") + (from.Year == DateTime.Today.Year ? "" : $" {to.AddDays(-1):yyyy}"),
            ReportRange.Year when from == thisFrom => "This year",
            ReportRange.Year when from == lastFrom => "Last year",
            ReportRange.Year => from.Year.ToString(),
            _ when from == thisFrom => "This month",
            _ when from == lastFrom => "Last month",
            _ => from.ToString("MMMM yyyy"),
        };
    }

    /// <summary>The dates a period covers, for a subtitle: "1 Jan – 31 Dec 2025"; a period in progress ends today.</summary>
    public static string Span(ReportRange unit, DateTime anchor, DateTime? firstDay = null)
    {
        var today = DateTime.Today;
        var (from, to) = ReportBuilder.Bounds(unit, anchor);
        if (unit == ReportRange.All) from = firstDay ?? today;
        if (unit == ReportRange.Day) return from.ToString("dddd, d MMMM yyyy");
        var last = to.AddDays(-1) > today ? today : to.AddDays(-1);
        string text = from.Year == last.Year ? $"{from:d MMM} – {last:d MMM yyyy}" : $"{from:d MMM yyyy} – {last:d MMM yyyy}";
        return unit == ReportRange.All && firstDay is not null ? $"{text} ({(int)(today - from).TotalDays + 1} days)" : text;
    }

    /// <summary>A length of time in words: "17 hours", "3 days and 4 hours".</summary>
    public static string Duration(TimeSpan span)
    {
        int days = (int)span.TotalDays, hours = span.Hours;
        string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
        return days == 0 ? Plural(Math.Max(1, (int)Math.Round(span.TotalHours)), "hour")
            : hours == 0 ? Plural(days, "day") : $"{Plural(days, "day")} and {Plural(hours, "hour")}";
    }

    private bool _refreshing;

    private void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            AllButton.Visibility = AllowAll ? Visibility.Visible : Visibility.Collapsed;
            foreach (RadioButton button in UnitButtons.Children)
                button.IsChecked = (string)button.Tag == Unit.ToString();

            Pager.Visibility = Unit == ReportRange.All ? Visibility.Collapsed : Visibility.Visible;
            if (Unit == ReportRange.Custom)
            {
                Pager.Label = CustomTo > CustomFrom ? Core.Reports.Report.CustomTitle(CustomFrom, CustomTo) : "Pick a range";
                Pager.LabelCommand = new RelayCommand(OpenRangeEditor);
                Pager.CanGoPrevious = CustomTo > CustomFrom && (MinDate is not { } min || CustomFrom > min);
                Pager.CanGoNext = CustomTo > CustomFrom && CustomTo < DateTime.Now;
                return;
            }
            Pager.LabelCommand = null;
            Pager.Label = Text(Unit, Anchor);
            Pager.PickMonth = Unit == ReportRange.Month;
            Pager.PickYear = Unit == ReportRange.Year;
            var (from, to) = ReportBuilder.Bounds(Unit, Anchor);
            Pager.CanGoPrevious = MinDate is not { } first || from > first;
            Pager.CanGoNext = to <= DateTime.Today;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Unit_Checked(object sender, RoutedEventArgs e)
    {
        if (_refreshing || sender is not RadioButton { Tag: string tag }) return;
        var unit = Enum.Parse<ReportRange>(tag);
        if (unit == Unit) return;
        if (unit == ReportRange.Custom)
        {
            // Starts as the period that was shown (all time: since the first day), then the editor opens to change it.
            var (from, to) = Unit == ReportRange.All
                ? (MinDate ?? DateTime.Today, DateTime.Today.AddDays(1))
                : ReportBuilder.Bounds(Unit, Anchor);
            if (to > ReportBuilder.HourEnd(DateTime.Now)) to = ReportBuilder.HourEnd(DateTime.Now);
            SetCurrentValue(CustomFromProperty, from);
            SetCurrentValue(CustomToProperty, to);
            SetCurrentValue(UnitProperty, unit);
            OpenRangeEditor();
            return;
        }
        SetCurrentValue(UnitProperty, unit);
    }

    public static string HourText(int hour) => hour switch { 0 => "12 AM", 12 => "12 PM", < 12 => $"{hour} AM", _ => $"{hour - 12} PM" };

    private void OpenRangeEditor()
    {
        var (from, to) = CustomTo > CustomFrom ? (CustomFrom, CustomTo) : (DateTime.Today, DateTime.Today.AddDays(1));
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
        FromDate.DisplayDateStart = MinDate is { } min && min <= from.Date ? min : from.Date;
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
                ToDate.DisplayDateStart = MinDate;
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
        if (fromDate is not { } fd || toDate is not { } td || fromHour < 0 || toHour < 0) return (default, default, "Pick a date and hour for both ends.");
        var from = fd.Date.AddHours(fromHour);
        var to = td.Date.AddHours(toHour);
        if (to <= from) return (from, to, "The end has to be after the start.");
        if (from >= now) return (from, to, "That's still to come.");
        return (from, to, null);
    }

    private void RangeApply_Click(object sender, RoutedEventArgs e)
    {
        var (from, to, error) = ReadRange(FromDate.SelectedDate, FromHour.SelectedIndex, ToDate.SelectedDate, ToHour.SelectedIndex, DateTime.Now);
        if (error is not null)
        {
            RangeError.Text = error;
            RangeError.Visibility = Visibility.Visible;
            return;
        }
        RangePopup.IsOpen = false;
        SetCurrentValue(CustomFromProperty, from);
        SetCurrentValue(CustomToProperty, to);
    }

    private void RangeCancel_Click(object sender, RoutedEventArgs e) => RangePopup.IsOpen = false;

    private void Step(int direction)
    {
        if (Unit == ReportRange.Custom)
        {
            // A custom range steps by its own length (three hours back, the two days before…).
            var length = CustomTo - CustomFrom;
            if (length <= TimeSpan.Zero) return;
            var (start, end) = (CustomFrom + direction * length, CustomTo + direction * length);
            if (direction > 0 && start >= DateTime.Now) return;
            if (direction < 0 && MinDate is { } min && end <= min) return;
            SetCurrentValue(CustomFromProperty, start);
            SetCurrentValue(CustomToProperty, end);
            return;
        }
        var next = direction < 0 ? ReportBuilder.Previous(Unit, Anchor) : ReportBuilder.Next(Unit, Anchor);
        var (from, _) = ReportBuilder.Bounds(Unit, next);
        if (direction > 0 && from > DateTime.Today) return;
        if (direction < 0 && MinDate is { } first && ReportBuilder.Bounds(Unit, next).To <= first) return;
        // Never land on a day after today (stepping months from the 31st, say).
        SetCurrentValue(AnchorProperty, next > DateTime.Today ? DateTime.Today : next);
    }
}
