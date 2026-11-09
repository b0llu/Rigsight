using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// The Taskbar page's "Add a reading" box (1.2.0, the user's report: the list flashed as it opened, sometimes stayed
/// open after a click outside, and the caret sat over the placeholder): the app's search box, and a list the page keeps
/// open and closes itself.
/// </summary>
[Collection("UI")]
public sealed class TaskbarAddBoxTests(AppHost host) : IClassFixture<AppHost>
{
    [Fact]
    public void Typing_what_nothing_matches_says_so_and_FPS_is_there_to_add()
    {
        // 2026-11-09: "asd" showed a bare empty box (the list's "nothing matches" line was never told the list emptied);
        // and FPS couldn't be added at all.
        host.Show("taskbar", 600);
        var vm = Ui.Run(() => (Rigsight.ViewModels.TaskbarViewModel)Visuals.Descendants<Rigsight.Views.TaskbarView>(host.Window).Single().DataContext);
        Ui.Run(() =>
        {
            var told = new List<string?>();
            vm.PropertyChanged += (_, e) => told.Add(e.PropertyName);
            vm.AddSearch = "asd qqq";
            Assert.Contains(nameof(vm.AddListEmpty), told);
            Assert.True(vm.AddListEmpty);

            vm.AddSearch = "fps";
            Assert.False(vm.AddListEmpty);
            Assert.Contains(vm.AddList, i => i.Sensor?.Id == Rigsight.Core.TrayParts.Fps);
            vm.AddSearch = "";
            if (!vm.Sensors.Any(r => r.Id == Rigsight.Core.TrayParts.Fps))
                Assert.Contains(vm.QuickPicks, p => p.Label == "FPS");
        });
    }

    [Fact]
    public void The_add_list_opens_from_the_box_and_closes_on_esc_a_click_elsewhere_or_the_page_scrolling()
    {
        host.Show("taskbar", 600);
        var (view, box, list, page) = Ui.Run(() =>
        {
            var v = Visuals.Descendants<Rigsight.Views.TaskbarView>(host.Window).Single();
            return (v, (TextBox)v.FindName("AddSearchText"), (Popup)v.FindName("AddPop"), (ScrollViewer)v.FindName("PageScroll"));
        });
        void Key(Key key) => Ui.Run(() => box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent }));
        try
        {
            Ui.Run(() =>
            {
                // The app's search box: no box of its own drawn inside it, the placeholder where the caret is.
                Assert.Same(Application.Current.FindResource("SearchBox"), box.Style);
                Assert.Equal("Add a reading…", box.Tag);
                // Kept open by the page: Windows' own closing (StaysOpen false) is what made it flash and miss clicks outside.
                Assert.True(list.StaysOpen);
                Assert.Same(box, list.PlacementTarget);
            });

            Key(System.Windows.Input.Key.R);
            Assert.True(Ui.Run(() => list.IsOpen), "typing opens the list");
            Key(System.Windows.Input.Key.Escape);
            Assert.False(Ui.Run(() => list.IsOpen), "Esc closes it");

            Key(System.Windows.Input.Key.R);
            // A click anywhere else in the window (the pointer isn't over the box).
            Ui.Run(() => host.Window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent }));
            Assert.False(Ui.Run(() => list.IsOpen), "a click elsewhere closes it");

            Key(System.Windows.Input.Key.R);
            Key(System.Windows.Input.Key.Tab);
            Assert.False(Ui.Run(() => list.IsOpen), "Tab moves on and closes it");

            // The page scrolling away from the box.
            Key(System.Windows.Input.Key.R);
            if (Ui.Run(() => page.ScrollableHeight) > 0)
            {
                Ui.Run(() => page.ScrollToVerticalOffset(page.ScrollableHeight));
                Ui.Pump(200);
                Assert.False(Ui.Run(() => list.IsOpen), "the page scrolling closes it");
                Ui.Run(page.ScrollToTop);
            }

            // Another page: closed, not left floating over it.
            Key(System.Windows.Input.Key.R);
            host.Show("home", 300);
            Assert.False(Ui.Run(() => list.IsOpen), "leaving the page closes it");
        }
        finally
        {
            Ui.Run(() => list.IsOpen = false);
        }
    }
}
