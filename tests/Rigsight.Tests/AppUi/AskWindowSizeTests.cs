using System.Windows;
using System.Windows.Controls.Primitives;
using Rigsight.Tests.Support;
using Rigsight.Views;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// The Ask window's edges pulled far past the other side, stuck and popped out: it stays at its smallest size instead of
/// crashing ("Width and Height must be non-negative", a tester's crash dialog, 2026-10-22).
/// </summary>
[Collection("UI")]
public sealed class AskWindowSizeTests(AppHost host) : IClassFixture<AppHost>
{
    private static void Pull(AskWindow window, string edge, double x, double y)
    {
        var thumb = (Thumb)window.FindName(edge);
        thumb.RaiseEvent(new DragDeltaEventArgs(x, y) { RoutedEvent = Thumb.DragDeltaEvent, Source = thumb });
    }

    [Fact]
    public void An_edge_pulled_past_the_other_side_keeps_the_smallest_size()
    {
        var vm = host.Shell.Ask;
        Ui.TakeProblems();
        Ui.Run(() => { vm.IsPoppedOut = false; vm.IsOpen = true; });
        Ui.Pump(300);
        Ui.Run(() =>
        {
            var window = Visuals.Descendants<AskWindow>(host.Window).Single();
            var panel = (FrameworkElement)window.FindName("Panel");
            // Stuck: the left and top edges dragged right and down by far more than the window is wide.
            Pull(window, "TopLeft", 5000, 5000);
            Assert.True(panel.Width >= 360 && panel.Height >= 380, $"{panel.Width} x {panel.Height}");
            // Popped out, then moved off to the right: the right edge dragged far left.
            vm.IsPoppedOut = true;
            vm.Floating = new Rect(4000, 3000, 500, 500);
            Pull(window, "BottomRight", -5000, -5000);
            Pull(window, "RightEdge", -5000, 0);
            Assert.True(panel.Width >= 360 && panel.Height >= 380, $"{panel.Width} x {panel.Height}");
            vm.IsPoppedOut = false;
            vm.IsOpen = false;
        });
        Ui.Pump(100);
        Assert.Empty(Ui.TakeProblems().Crashes);
    }

    // A pull past 720 px (2026-10-27: "the chat box is stuck for a sec" as the conversations' column opened): the one list
    // of conversations is built once and moved between the column and over the chat, never built again or doubled.
    [Fact]
    public void The_conversations_are_built_once_and_moved_not_rebuilt()
    {
        var vm = host.Shell.Ask;
        var place = typeof(AskWindow).GetMethod("Place", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Ui.Run(() => { vm.IsPoppedOut = false; vm.IsBig = false; vm.StuckSmall = new Size(600, 640); vm.IsOpen = true; });
        Ui.Pump(600);
        Ui.Run(() =>
        {
            var window = Visuals.Descendants<AskWindow>(host.Window).Single();
            System.Windows.Controls.TextBox Search() => Visuals.Descendants<System.Windows.Controls.TextBox>(window).Single(t => t.Name == "HistorySearchBox");
            var built = Search();
            Assert.False(built.IsVisible, "built ahead, hidden while narrow");
            vm.StuckSmall = new Size(760, 640); place.Invoke(window, null); window.UpdateLayout();
            Assert.True(vm.ShowHistoryColumn);
            Assert.Same(built, Search());
            Assert.True(built.IsVisible);
            vm.StuckSmall = new Size(600, 640); place.Invoke(window, null);
            vm.IsHistoryOpen = true; window.UpdateLayout();
            Assert.Same(built, Search());
            Assert.True(built.IsVisible, "over the chat");
            vm.IsHistoryOpen = false;
            vm.IsOpen = false;
        });
    }
}
