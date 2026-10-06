using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Rigsight.Controls;
using Rigsight.ViewModels;

namespace Rigsight.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // Each time Settings is opened (the page is kept between visits), and when something new needs a look while it's
        // open: go straight to that section.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) ScrollToAttention(); };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnViewModelChanged;
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.AttentionSection) && IsVisible) ScrollToAttention();
    }

    private void ScrollToAttention()
    {
        if (DataContext is not SettingsViewModel { AttentionSection: { } section } vm) return;
        // After layout, so the section's position is known.
        Dispatcher.BeginInvoke(() =>
        {
            Attention.ScrollTo(Scroller, section);
            vm.Arrived();
        }, DispatcherPriority.Loaded);
    }
}
