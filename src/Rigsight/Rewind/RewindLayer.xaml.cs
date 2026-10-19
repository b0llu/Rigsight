using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Rigsight.Core.Rewind;
using Rigsight.Services;

namespace Rigsight.Rewind;

/// <summary>
/// Draws the rewind's cards into the full screen, the viewer and Share. Cards exist only while shown: everything is
/// let go when the layer closes, so a closed rewind holds no pictures.
/// </summary>
public partial class RewindLayer : UserControl
{
    private RewindViewModel? _vm;

    public RewindLayer()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) { _vm.PropertyChanged -= OnChanged; _vm.LookChanged -= DrawShare; }
            _vm = e.NewValue as RewindViewModel;
            if (_vm is not null) { _vm.PropertyChanged += OnChanged; _vm.LookChanged += DrawShare; }
        };
        PreviewKeyDown += OnKey;
        PreviewBox.SizeChanged += (_, _) => DrawPreview();
        // The card's corners: a border's radius doesn't clip the picture inside it.
        RewindPainter.Round(CardHost, 20);
        RewindPainter.Round(PeekPrev, 16);
        RewindPainter.Round(PeekNext, 16);
        RewindPainter.Round(PreviewHost, 10);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        switch (e.PropertyName)
        {
            case nameof(RewindViewModel.IsRevealing):
                if (_vm.IsRevealing) DrawFan(); else Fan.Children.Clear();
                Focus();
                break;
            case nameof(RewindViewModel.IsWatching):
                if (!_vm.IsWatching) { CardHost.Child = null; PeekPrev.Child = null; PeekNext.Child = null; Thumbs.Children.Clear(); }
                else DrawCard();
                Dispatcher.BeginInvoke(() => Focus(), System.Windows.Threading.DispatcherPriority.Input);
                break;
            case nameof(RewindViewModel.Index):
            case nameof(RewindViewModel.Cards):
            case nameof(RewindViewModel.Watched):
                DrawCard();
                break;
            case nameof(RewindViewModel.IsSharing):
                if (_vm.IsSharing) DrawShare(); else { PreviewHost.Child = null; AccentSwatches.Children.Clear(); DrawCard(); }
                break;
            case nameof(RewindViewModel.Previewed):
                DrawPreview();
                break;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.IsSharing)
        {
            if (e.Key == Key.Escape) { _vm.CloseShareCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (_vm.IsWatching)
        {
            if (e.Key is Key.Right or Key.Space) { _vm.NextCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Left) { _vm.PreviousCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Escape) { _vm.CloseCommand.Execute(null); e.Handled = true; }
        }
        else if (_vm.IsRevealing && e.Key == Key.Escape) { _vm.LaterCommand.Execute(null); e.Handled = true; }
    }

    private RewindLook ViewLook => _vm!.Own;

    /// <summary>The full screen's three cards: most played (or the first after the cover) left, the cover in front, the next right.</summary>
    private void DrawFan()
    {
        Fan.Children.Clear();
        if (_vm?.Revealed is not { } f) return;
        var cards = RewindCards.Of(f);
        var cover = cards[0];
        var left = cards.FirstOrDefault(c => c.Id == "top") ?? cards.ElementAtOrDefault(1);
        var right = cards.FirstOrDefault(c => c.Id == "games") ?? cards.ElementAtOrDefault(2);
        void Add(RewindCard? card, double x, double angle, bool dim)
        {
            if (card is null) return;
            int i = cards.IndexOf(card);
            var face = RewindPainter.Card(card, f, i, cards.Count, "story", ViewLook, 1080, 1920);
            UIElement inner = new Viewbox { Stretch = Stretch.Fill, Child = face };
            // The two behind are dimmed, so the cover in front stands out.
            if (dim) inner = new Grid { Children = { inner, new Border { Background = new SolidColorBrush(Color.FromArgb(115, 0, 0, 0)) } } };
            var box = new Border
            {
                Width = 168, Height = 298, CornerRadius = new CornerRadius(12), ClipToBounds = true,
                Child = inner,
                Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Opacity = 0.7, Color = Colors.Black },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(angle),
            };
            Canvas.SetLeft(box, 210 - 84 + x);
            Canvas.SetTop(box, dim ? 18 : 0);
            Fan.Children.Add(box);
        }
        Add(left, -120, -9, true);
        Add(right, 120, 9, true);
        Add(cover, 0, 0, false);
    }

    /// <summary>The card being watched, as RigSight shows it (wide, the app's look), with the progress and the end's thumbnails.</summary>
    private void DrawCard()
    {
        try { DrawCardNow(); }
        catch (Exception ex) { Core.Log.Error("rewind", ex); } // a card that can't be drawn mustn't leave the screen half drawn unseen
    }

    private void DrawCardNow()
    {
        if (_vm?.Watched is not { } f || !_vm.IsWatching) return;
        var cards = _vm.Cards;
        FrameworkElement Tall(int k, double w, double h) => new Viewbox
        {
            Stretch = Stretch.Fill, Width = w, Height = h,
            Child = RewindPainter.Card(cards[k], f, k, cards.Count, "story", ViewLook, 1080, 1920),
        };
        PeekPrev.Child = _vm.Index > 0 && _vm.Index - 1 < cards.Count ? Tall(_vm.Index - 1, 200, 356) : null;
        PeekNext.Child = _vm.Index + 1 < cards.Count ? Tall(_vm.Index + 1, 200, 356) : null;
        if (_vm.Current is not null)
        {
            var face = RewindPainter.Card(cards[_vm.Index], f, _vm.Index, cards.Count, "story", ViewLook, 1080, 1920);
            // The pieces start hidden and rise in, set before the card is shown: started after it, the whole card
            // flashed in place first (a picture made for Share stays still).
            RewindPainter.Animate(face);
            CardHost.Child = new Viewbox { Stretch = Stretch.Fill, Child = face };
        }
        else
        {
            CardHost.Child = null;
            Thumbs.Children.Clear();
            for (int k = 0; k < cards.Count; k++)
                Thumbs.Children.Add(new Border { Width = 52, Height = 92, CornerRadius = new CornerRadius(6), ClipToBounds = true, Margin = new Thickness(4), Child = Tall(k, 52, 92) });
        }
    }

    private void PeekPrev_Click(object sender, MouseButtonEventArgs e) => _vm?.PreviousCommand.Execute(null);

    private void Card_Click(object sender, MouseButtonEventArgs e) => _vm?.NextCommand.Execute(null);

    private void ShareBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => _vm?.CloseShareCommand.Execute(null);

    // ── Share ────────────────────────────────────────────────────────────

    private void DrawShare()
    {
        if (_vm is null || !_vm.IsSharing) return;
        DrawPreview();
        DrawSwatches();
        // The small pictures follow the look: built again.
        var source = PickList.ItemsSource;
        PickList.ItemsSource = null;
        PickList.ItemsSource = source;
    }

    private void DrawPreview()
    {
        if (_vm is null || !_vm.IsSharing || _vm.Previewed is not { } p || _vm.Watched is not { } f) { PreviewHost.Child = null; return; }
        var (w, h) = RewindPainter.Sizes[_vm.Shape];
        double room = Math.Max(100, PreviewBox.ActualHeight), across = Math.Max(100, PreviewBox.ActualWidth);
        double scale = Math.Min(room / h, across / w);
        PreviewFrame.Width = w * scale;
        PreviewFrame.Height = h * scale;
        PreviewHost.Child = new Viewbox { Stretch = Stretch.Fill, Child = RewindPainter.Card(p.Card, f, p.Index, _vm.Cards.Count, _vm.Shape, _vm.Look, w, h) };
    }

    /// <summary>Anywhere on a row but its tick: that picture in the preview.</summary>
    private void PickRow_Click(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != sender; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is CheckBox) return;
        if (sender is FrameworkElement { Tag: SharePick p }) _vm?.PreviewCommand.Execute(p);
    }

    private void PickThumb_Loaded(object sender, RoutedEventArgs e)
    {
        if (_vm?.Watched is not { } f || sender is not Border box || box.Tag is not SharePick p) return;
        box.Child = new Viewbox { Stretch = Stretch.Fill, Child = RewindPainter.Card(p.Card, f, p.Index, _vm.Cards.Count, "story", _vm.Look, 1080, 1920) };
    }

    /// <summary>The accents, RigSight's own colours first: a ring on the one in use.</summary>
    private void DrawSwatches()
    {
        if (_vm is null) return;
        var look = _vm.Look;
        AccentSwatches.Children.Clear();
        foreach (var a in Accents.All)
        {
            Brush fill = a.Key == Accents.Mono
                ? new LinearGradientBrush(Color.FromRgb(0x5B, 0x8C, 0xFF), Color.FromRgb(0xF4, 0x72, 0xB6), 45)
                : new SolidColorBrush(a.OnDark);
            string key = a.Key;
            AccentSwatches.Children.Add(Swatch(16, fill, look.Accent == key, key == Accents.Mono ? Core.Lang.T("RigSight colours") : Core.Lang.T(a.Name), () => _vm.SetAccent(key)));
        }
    }

    private static Button Swatch(double size, Brush fill, bool on, string name, Action pick)
    {
        var dot = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = fill };
        var ring = new Border
        {
            Padding = new Thickness(2), CornerRadius = new CornerRadius(size / 2 + 4), BorderThickness = new Thickness(on ? 2 : 0), Child = dot,
        };
        ring.SetResourceReference(Border.BorderBrushProperty, "TextBrush");
        var b = new Button { Content = ring, Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand, Focusable = false, ToolTip = name };
        b.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='Transparent'><ContentPresenter /></Border></ControlTemplate>");
        System.Windows.Automation.AutomationProperties.SetName(b, name);
        b.Click += (_, _) => pick();
        return b;
    }
}
