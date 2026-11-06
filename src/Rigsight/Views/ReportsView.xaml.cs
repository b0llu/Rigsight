using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class ReportsView : UserControl
{
    /// <summary>The "Apps you used" box: about eight rows, then it scrolls.</summary>
    public const double AppsMaxHeight = 430;

    public ReportsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnChanged;
            if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnChanged;
        };
        DataArea.SizeChanged += (_, _) => PlaceRing();
        // A test copy showing every loader (RIGSIGHT_SHOW_LOADERS=1) shows the cover too, to judge it at leisure.
        if (Controls.Spinner.ShowAll) Loaded += (_, _) => ShowCover(true);
    }

    /// <summary>How dark the cover goes over the figures while another period is read.</summary>
    internal const double CoverOpacity = 0.72;

    /// <summary>
    /// Another period is being read (past a moment, see <see cref="ReportsViewModel.Covered"/>): the figures go under a
    /// dark cover, fading in quickly, with the big ring over them; when it's read, both go at once and the new figures
    /// move in under where they were.
    /// </summary>
    private void ShowCover(bool on)
    {
        Cover.Visibility = RingLayer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Cover.BeginAnimation(OpacityProperty, on && SystemParameters.ClientAreaAnimation
            ? new DoubleAnimation(0, CoverOpacity, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }
            : null);
        Cover.Opacity = on ? CoverOpacity : 0;
        if (on) PlaceRing();
    }

    /// <summary>The ring in the middle of what's in view of the figures, wherever the page is scrolled to.</summary>
    private void PlaceRing()
    {
        if (RingLayer.Visibility != Visibility.Visible || DataArea.ActualHeight <= 0) return;
        double top = DataArea.TranslatePoint(new Point(0, 0), PageScroll).Y;
        double visibleFrom = Math.Max(0, -top), visibleTo = Math.Min(DataArea.ActualHeight, PageScroll.ViewportHeight - top);
        double mid = visibleTo > visibleFrom ? (visibleFrom + visibleTo) / 2 : DataArea.ActualHeight / 2;
        Canvas.SetLeft(PageRing, (DataArea.ActualWidth - Controls.Spinner.PageSize) / 2);
        Canvas.SetTop(PageRing, Math.Clamp(mid, 40, Math.Max(40, DataArea.ActualHeight - 40)) - Controls.Spinner.PageSize / 2);
    }

    private void PageScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => PlaceRing();

    // Opened from Home's "hotter than usual": once the week's card is there, it's brought into view (laid out first).
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReportsViewModel.Covered) && sender is ReportsViewModel covered)
        {
            if (!Controls.Spinner.ShowAll) ShowCover(covered.Covered);
            return;
        }
        if (e.PropertyName != nameof(ReportsViewModel.UsualRows) || sender is not ReportsViewModel { WantUsual: true } vm) return;
        // A period with no card: nothing to bring into view, and nothing to jump to later.
        if (!vm.HasUsual)
        {
            if (!vm.IsLoading) vm.WantUsual = false;
            return;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!vm.WantUsual) return;
            vm.WantUsual = false;
            UsualCard.BringIntoView(new Rect(0, 0, UsualCard.ActualWidth, Math.Min(UsualCard.ActualHeight + 40, 600)));
        });
    }
}
