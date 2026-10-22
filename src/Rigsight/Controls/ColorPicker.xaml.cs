using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Rigsight.Core;

namespace Rigsight.Controls;

/// <summary>
/// The colours of one taskbar reading (Taskbar page), floating next to its Colors button: its number's (by heat, for a
/// temperature, or a colour) and its name's, which also colours its mark and graph. The quick picks are
/// <see cref="TrayColors.Palette"/>, then the colours made with that row's "+" (picked on a square and a hue bar or typed
/// as a code), in the order made, kept for every reading. Each row shows three lines of tiles and scrolls past them. A pick is used at once (see
/// <see cref="Changed"/>) and the picker stays open for the next.
/// </summary>
public partial class ColorPicker : UserControl
{
    private string? _number, _name;
    private bool _temperature;
    private string _partHex = "#949494";
    private string? _customFor;            // "number" or "name": what the custom colour is for, while it's open
    private double _hue = 220, _sat = 0.64, _val = 1;
    private bool _typing;
    private List<string> _numberCustoms = [], _nameCustoms = [];

    public ColorPicker() => InitializeComponent();

    /// <summary>A colour picked: the number's and the name's now (null: as RigSight colours them).</summary>
    public event Action<string?, string?>? Changed;

    /// <summary>A colour made with "+": for the number ("number") or the name ("name"), and its code, to keep with the others.</summary>
    public event Action<string, string>? CustomMade;

    /// <summary>Opens under <paramref name="target"/> for a reading: its name, whether it's a temperature, its colours and its part's.</summary>
    public void Open(FrameworkElement target, string heading, bool temperature, string? number, string? name, string partHex, string defaultText,
        IReadOnlyList<string> numberCustoms, IReadOnlyList<string> nameCustoms)
    {
        _numberCustoms = [.. numberCustoms];
        _nameCustoms = [.. nameCustoms];
        _temperature = temperature;
        _number = number;
        _name = name;
        _partHex = partHex;
        _customFor = null;
        Heading.Text = heading;
        DefaultText.Text = defaultText;
        Pop.PlacementTarget = target;
        Refresh();
        NumberScroll.ScrollToTop();
        NameScroll.ScrollToTop();
        Pop.IsOpen = true;
    }

    public void Close() => Pop.IsOpen = false;

    /// <summary>The number's colour in use (by heat when nothing was picked for a temperature).</summary>
    private string? NumberShown => _number ?? (_temperature ? TrayColors.Heat : null);

    /// <summary>The name's colour in use: picked, else its part's.</summary>
    private string NameShown => _name ?? _partHex;

    private void Refresh()
    {
        HeatButton.Visibility = _temperature ? Visibility.Visible : Visibility.Collapsed;
        HeatButton.Tag = NumberShown == TrayColors.Heat ? "on" : null;
        Fill(NumberTiles, NumberShown, "number");
        Fill(NameTiles, NameShown, "name");
        Custom.Visibility = _customFor is null ? Visibility.Collapsed : Visibility.Visible;
        CustomHeading.Text = _customFor == "name" ? Lang.T("Custom color for the name and mark") : Lang.T("Custom color for the number");
    }

    /// <summary>The quick picks, the colours made with "+" (and one in use that isn't among them), then "+".</summary>
    private void Fill(WrapPanel tiles, string? selected, string kind)
    {
        tiles.Children.Clear();
        foreach (var (name, hex) in TrayColors.Palette) tiles.Children.Add(Tile(Lang.T(name), hex, selected, kind));
        var made = (kind == "number" ? _numberCustoms : _nameCustoms).Where(c => TrayColors.Palette.All(p => !Same(p.Hex, c))).ToList();
        if (selected is not null && selected != TrayColors.Heat && TrayColors.Palette.All(p => !Same(p.Hex, selected)) && made.All(c => !Same(c, selected)))
            made.Add(selected);
        foreach (var hex in made) tiles.Children.Add(Tile($"{Lang.T("Custom color")} {hex}", hex, selected, kind));
        var add = new Button { Style = (Style)Resources["AddSwatch"], Tag = _customFor == kind ? "on" : null, ToolTip = Lang.T("Custom color") };
        AutomationProperties.SetName(add, Lang.T("Custom color"));
        add.Click += (_, _) =>
        {
            if (_customFor == kind) _customFor = null;
            else
            {
                _customFor = kind;
                string from = (kind == "number" ? NumberShown : NameShown) is { } h && h != TrayColors.Heat ? h : "#5B8CFF";
                SetFrom(from);
            }
            Refresh();
        };
        tiles.Children.Add(add);
    }

    private Button Tile(string name, string hex, string? selected, string kind)
    {
        var c = ColorOf(hex);
        bool on = selected is not null && Same(hex, selected);
        var tile = new Button
        {
            Style = (Style)Resources["Swatch"],
            Background = new SolidColorBrush(c),
            Tag = on ? "on" : null,
            ToolTip = name,
            Content = on ? new TextBlock
            {
                Text = "", Style = (Style)FindResource("Icon"), FontSize = 13,
                Foreground = Dark(c) ? Brushes.White : Brushes.Black,
            } : null,
        };
        AutomationProperties.SetName(tile, name);
        tile.Click += (_, _) => Pick(kind, hex);
        return tile;
    }

    private void Pick(string kind, string? value)
    {
        if (kind == "number") _number = value;
        else _name = value;
        Refresh();
        Changed?.Invoke(_number, _name);
    }

    private void Heat_Click(object sender, RoutedEventArgs e) => Pick("number", TrayColors.Heat);

    private void Default_Click(object sender, RoutedEventArgs e)
    {
        _number = null;
        _name = null;
        _customFor = null;
        Refresh();
        Changed?.Invoke(null, null);
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (_customFor is not { } kind) return;
        string hex = HexOf(_hue, _sat, _val);
        // Kept with the others made for this row, the newest last (one made before stays where it is).
        var customs = kind == "number" ? _numberCustoms : _nameCustoms;
        if (customs.All(c => !Same(c, hex)) && TrayColors.Palette.All(p => !Same(p.Hex, hex)))
        {
            customs.Add(hex);
            CustomMade?.Invoke(kind, hex);
        }
        Pick(kind, hex);
    }

    // ── The custom colour: a square for saturation (across) and brightness (down), a bar for the hue ──

    private void Square_Down(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).CaptureMouse();
        SquareAt(e.GetPosition(Square));
    }

    private void Square_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && ((UIElement)sender).IsMouseCaptured) SquareAt(e.GetPosition(Square));
    }

    private void Hue_Down(object sender, MouseButtonEventArgs e)
    {
        ((UIElement)sender).CaptureMouse();
        HueAt(e.GetPosition(HueBar));
    }

    private void Hue_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && ((UIElement)sender).IsMouseCaptured) HueAt(e.GetPosition(HueBar));
    }

    private void Drag_Up(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    private void SquareAt(Point p)
    {
        _sat = Math.Clamp(p.X / Math.Max(1, Square.ActualWidth), 0, 1);
        _val = 1 - Math.Clamp(p.Y / Math.Max(1, Square.ActualHeight), 0, 1);
        ShowCustom();
    }

    private void HueAt(Point p)
    {
        _hue = Math.Clamp(p.X / Math.Max(1, HueBar.ActualWidth), 0, 0.9999) * 360;
        ShowCustom();
    }

    private void SetFrom(string hex)
    {
        var c = ColorOf(hex);
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        _hue = (h + 360) % 360;
        _sat = max == 0 ? 0 : d / max;
        _val = max;
        ShowCustom();
    }

    /// <summary>The square, knobs, preview and code for the colour being made (the code left alone while it's typed in).</summary>
    private void ShowCustom()
    {
        SquareHue.Fill = new SolidColorBrush(ColorOf(HexOf(_hue, 1, 1)));
        // Laid out only once the panel shows: placed again then.
        if (Square.ActualWidth <= 0)
        {
            Dispatcher.BeginInvoke(new Action(ShowCustom), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        Canvas.SetLeft(SquareKnob, _sat * Square.ActualWidth - 7);
        Canvas.SetTop(SquareKnob, (1 - _val) * Square.ActualHeight - 7);
        Canvas.SetLeft(HueKnob, _hue / 360 * HueBar.ActualWidth - 7);
        string hex = HexOf(_hue, _sat, _val);
        Preview.Background = new SolidColorBrush(ColorOf(hex));
        if (!_typing)
        {
            Code.Text = hex;
            CodeBox.BorderBrush = Brushes.Transparent;
        }
    }

    private void Code_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _customFor is null) return;
        bool ok = TrayColors.Parse(Code.Text) is not null;
        CodeBox.BorderBrush = ok || Code.Text.Trim().Length <= 1 ? Brushes.Transparent : (Brush)FindResource("HotBrush");
        if (!ok) return;
        _typing = true;
        SetFrom(Code.Text.Trim().StartsWith('#') ? Code.Text.Trim() : "#" + Code.Text.Trim());
        _typing = false;
    }

    private void Code_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TrayColors.Parse(Code.Text) is not null) Use_Click(sender, e);
    }

    // ── Colours ──

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static Color ColorOf(string hex) => TrayColors.Parse(hex) is { } c ? Color.FromRgb(c.R, c.G, c.B) : Colors.Gray;

    private static bool Dark(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 140;

    private static string HexOf(double h, double s, double v)
    {
        double F(double n)
        {
            double k = (n + h / 60) % 6;
            return v - v * s * Math.Max(0, Math.Min(k, Math.Min(4 - k, 1)));
        }
        return TrayColors.Hex((byte)Math.Round(F(5) * 255), (byte)Math.Round(F(3) * 255), (byte)Math.Round(F(1) * 255));
    }
}
