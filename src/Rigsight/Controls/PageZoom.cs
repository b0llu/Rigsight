namespace Rigsight.Controls;

/// <summary>
/// The zoom the pages are drawn at (Ctrl + wheel or a pinch, as far as the window has room: see MainWindow.ApplyZoom).
/// Anything kept as a picture (a BitmapCache) is drawn at this scale, or the zoom stretches the picture and it blurs
/// (the Temperatures graph at 110%, 2026-11-16).
/// </summary>
public static class PageZoom
{
    public static double Current { get; private set; } = 1;

    public static event Action? Changed;

    internal static void Set(double used)
    {
        if (used <= 0 || used == Current) return;
        Current = used;
        Changed?.Invoke();
    }
}
