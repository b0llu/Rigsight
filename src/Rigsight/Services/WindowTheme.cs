using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Rigsight.Services;

/// <summary>Colors the native Windows 11 title bar to blend with the app background.</summary>
public static partial class WindowTheme
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    public static void ApplyTitleBar(Window window, bool darkMode, Color caption, Color text)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        int dark = darkMode ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int captionRef = ToColorRef(caption);
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionRef, sizeof(int));
        int textRef = ToColorRef(text);
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textRef, sizeof(int));
    }

    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
