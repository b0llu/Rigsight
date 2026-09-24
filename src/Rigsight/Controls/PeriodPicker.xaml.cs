using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using Rigsight.Core.Reports;

namespace Rigsight.Controls;

/// <summary>
/// The period a page looks at: a day, a Monday-to-Sunday week, a month, a year, or all time. The arrows step to the
/// period before or after, and the label opens the matching picker (a calendar for a day or week, a month grid, a
/// year list). Reports, Apps and Crashes all use it, so a period means the same thing everywhere.
/// </summary>
public partial class PeriodPicker : UserControl
{
    public PeriodPicker()
    {
        InitializeComponent();
        Pager.PreviousCommand = new RelayCommand(() => Step(-1));
        Pager.NextCommand = new RelayCommand(() => Step(1));
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

    public ReportRange Unit { get => (ReportRange)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public DateTime Anchor { get => (DateTime)GetValue(AnchorProperty); set => SetValue(AnchorProperty, value); }
    public DateTime? MinDate { get => (DateTime?)GetValue(MinDateProperty); set => SetValue(MinDateProperty, value); }
    public bool AllowAll { get => (bool)GetValue(AllowAllProperty); set => SetValue(AllowAllProperty, value); }

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
        if (unit != Unit) SetCurrentValue(UnitProperty, unit);
    }

    private void Step(int direction)
    {
        var next = direction < 0 ? ReportBuilder.Previous(Unit, Anchor) : ReportBuilder.Next(Unit, Anchor);
        var (from, _) = ReportBuilder.Bounds(Unit, next);
        if (direction > 0 && from > DateTime.Today) return;
        if (direction < 0 && MinDate is { } first && ReportBuilder.Bounds(Unit, next).To <= first) return;
        // Never land on a day after today (stepping months from the 31st, say).
        SetCurrentValue(AnchorProperty, next > DateTime.Today ? DateTime.Today : next);
    }
}
