using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Rigsight;

/// <summary>
/// The window's own title bar. Windows' title bar was used before, coloured to match, but Windows makes it shorter when
/// the window is maximized and squeezes its buttons; other apps draw their own and keep one height. Dragging, a double
/// click and the window menu are WindowChrome's caption; maximize answers Windows as a maximize button, so hovering it
/// shows Windows 11's snap layouts.
/// </summary>
public partial class MainWindow
{
    private const int WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCMOUSELEAVE = 0x02A2;
    private const int HTMAXBUTTON = 9;
    /// <summary>The bar's height at 100% (40 since the ask box, 2026-10-27); zoomed with the pages.</summary>
    private const double CaptionHeight = 40;
    private bool _maxPressed;

    private void SetUpTitleBar()
    {
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TitleBarHook);
        StateChanged += (_, _) => FitFrame();
        Activated += (_, _) => TitleText.Opacity = 1;
        Deactivated += (_, _) => TitleText.Opacity = 0.55;
        TitleBar.SizeChanged += (_, _) => FitWhatsNew();
        AskBox.IsVisibleChanged += (_, _) => FitWhatsNew();
        FitFrame();
    }

    /// <summary>
    /// The What's new button's words, while the bar has room for them between the ask box (and its chat button) and the
    /// window's buttons; otherwise its dot and icon alone, so it never runs into the box.
    /// </summary>
    private void FitWhatsNew()
    {
        // The chat button's right edge is 278 right of the middle; the window's buttons take 138 at the right; 16 to spare.
        double room = AskBox.IsVisible ? TitleBar.ActualWidth / 2 - 278 - 138 - 16 : double.MaxValue;
        string? tag = room < 112 ? "compact" : null;
        if (!Equals(WhatsNewButton.Tag, tag)) WhatsNewButton.Tag = tag;
    }

    /// <summary>
    /// Maximized, the window reaches a frame's width past each screen edge: the padding keeps the title bar and pages
    /// on screen, and the drag area grows by the same so it still ends under the bar.
    /// </summary>
    private void FitFrame()
    {
        bool max = WindowState == WindowState.Maximized;
        var edge = max ? FrameEdge() : new Thickness(0);
        Frame.Padding = edge;
        if (WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = CaptionHeight * _barZoom + edge.Top;
        MaxButton.Content = max ? "" : "";
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, max ? "Restore" : "Maximize");
    }

    /// <summary>The frame's width as Windows sizes it (resize border plus padding), in the window's own units.</summary>
    private Thickness FrameEdge()
    {
        var r = SystemParameters.WindowResizeBorderThickness;
        var n = SystemParameters.WindowNonClientFrameThickness;
        // Resize border alone is short by the padded border; the non-client frame's sides are the whole of it.
        double side = Math.Max(r.Left, n.Left);
        return new Thickness(side, Math.Max(r.Top, n.Left), side, Math.Max(r.Bottom, n.Bottom));
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private IntPtr TitleBarHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST when OverMaxButton(lParam):
                handled = true;
                return HTMAXBUTTON;
            case WM_NCMOUSEMOVE:
                MaxButton.Tag = wParam == HTMAXBUTTON ? (_maxPressed ? "pressed" : "hover") : null;
                break;
            case WM_NCMOUSELEAVE:
                MaxButton.Tag = null;
                _maxPressed = false;
                break;
            case WM_NCLBUTTONDOWN when wParam == HTMAXBUTTON:
                _maxPressed = true;
                MaxButton.Tag = "pressed";
                handled = true; // left to Windows, it would draw an old-style button of its own over ours
                break;
            case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                if (_maxPressed) Maximize_Click(MaxButton, new RoutedEventArgs());
                _maxPressed = false;
                MaxButton.Tag = "hover";
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private bool OverMaxButton(IntPtr lParam)
    {
        if (!MaxButton.IsVisible) return false;
        int x = unchecked((short)(long)lParam), y = unchecked((short)((long)lParam >> 16));
        try
        {
            var p = MaxButton.PointFromScreen(new Point(x, y));
            return p.X >= 0 && p.Y >= 0 && p.X < MaxButton.ActualWidth && p.Y < MaxButton.ActualHeight;
        }
        catch (InvalidOperationException) { return false; } // not connected to a window yet
    }
}
