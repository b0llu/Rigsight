using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
    }

    // Opened from Home's "hotter than usual": once the week's card is there, it's brought into view (laid out first).
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
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
