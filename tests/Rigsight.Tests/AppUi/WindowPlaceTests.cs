using System.IO;
using System.Windows;
using Rigsight.Core;
using Rigsight.Services;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// The window opens where it was closed, at that size, maximized if it was; a place on no screen now opens centred at
/// the default size (GitHub issue #3, 2026-10-26). Real windows, on the test's own data folder.
/// </summary>
[Collection("UI")]
public sealed class WindowPlaceTests
{
    private static string Kept => Path.Combine(RigsightPaths.DataDir, "window.json");

    private static Window Open(Action<Window>? before = null)
    {
        var w = new Window { Width = 1400, Height = 920, MinWidth = 600, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowActivated = false };
        WindowPlace.Keep(w);
        before?.Invoke(w);
        w.Show();
        return w;
    }

    [Fact]
    public void It_opens_where_it_was_closed_at_that_size_and_maximized_if_it_was()
    {
        File.Delete(Kept);
        Ui.Run(() =>
        {
            var first = Open();
            first.Left = 120;
            first.Top = 90;
            first.Width = 1000;
            first.Height = 700;
            first.Close();
            Assert.True(File.Exists(Kept));

            var again = Open();
            Assert.Equal((120, 90, 1000, 700), ((int)again.Left, (int)again.Top, (int)again.ActualWidth, (int)again.ActualHeight));
            again.WindowState = WindowState.Maximized;
            again.Close();

            var maximized = Open();
            Assert.Equal(WindowState.Maximized, maximized.WindowState);
            // Back to normal: the place it had before it was maximized.
            maximized.WindowState = WindowState.Normal;
            Assert.Equal((120, 90), ((int)maximized.Left, (int)maximized.Top));
            maximized.Close();
        });
        File.Delete(Kept);
    }

    [Fact]
    public void A_place_on_no_screen_now_opens_centred()
    {
        File.Delete(Kept);
        Directory.CreateDirectory(RigsightPaths.DataDir);
        // A monitor far to the right that has been unplugged since.
        File.WriteAllText(Kept, """{"Left":20000,"Top":300,"Right":21200,"Bottom":1100,"Maximized":false}""");
        Ui.Run(() =>
        {
            var w = Open();
            var area = SystemParameters.WorkArea;
            Assert.InRange(w.Left + w.ActualWidth / 2, area.Left + area.Width / 2 - 2, area.Left + area.Width / 2 + 2);
            Assert.True(w.ActualHeight <= area.Height, $"{w.ActualHeight} tall on a {area.Height} screen");
            w.Close();
        });
        File.Delete(Kept);
    }
}
