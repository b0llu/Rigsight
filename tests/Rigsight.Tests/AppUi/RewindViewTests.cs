using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rigsight.Core.Rewind;
using Rigsight.Rewind;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.AppUi;

/// <summary>Rewind in the real window: watched card by card to the end, then Share.</summary>
[Collection("UI")]
public sealed class RewindViewTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");

    private static RewindFacts October() => new()
    {
        Id = "2026-10-month", Kind = RewindKind.Month, From = new(2026, 10, 1), To = new(2026, 10, 20), Made = new(2026, 10, 19, 15, 0, 0),
        Time = true, Games = true, Internet = true,
        OnSec = 220 * 3600, ActiveSec = 190 * 3600, DaysOn = 18, Days = 19, BusiestDay = new(2026, 10, 10), BusiestDaySec = 15 * 3600,
        GamingSec = 63 * 3600, GameCount = 17, Plays = 95,
        TopGames = [new("Dota 2", "dota2.exe", null, 18 * 3600, 30), new("REMATCH", "rematch.exe", null, 10 * 3600, 34)],
        LongestGame = new("No Man's Sky", "nms.exe", null, 2.6 * 3600), LongestStart = new(2026, 10, 7, 17, 47, 0),
        Down = 500L << 30, Up = 21L << 30, TopInternet = [new("Steam", "steam.exe", null, 266L << 30)],
    };

    private RewindLayer Layer() => Visuals.Descendants<RewindLayer>(host.Window).Single();

    [Fact]
    public void Watched_to_the_end_the_progress_is_full_and_the_end_shows_every_card_then_Share_opens()
    {
        var vm = host.Shell.Rewind;
        var f = October();
        Ui.TakeProblems();
        Ui.Run(() => vm.Watch(f));
        Ui.Pump(300);
        int n = Ui.Run(() => vm.Cards.Count);
        Assert.True(n >= 6);
        // What the screen shows, not only what the view model says: the first card, and the arrow to the next one on.
        Ui.Run(() =>
        {
            var layer = Layer();
            Assert.Equal(Visibility.Visible, ((FrameworkElement)layer.FindName("CardFrame")).Visibility);
            Assert.NotNull(((Border)layer.FindName("CardHost")).Child);
            var next = Visuals.Descendants<Button>(layer).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Next");
            Assert.True(next.IsEnabled);
        });
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "rewind-card-1.png")));

        for (int i = 0; i < n; i++)
        {
            Ui.Run(() => vm.NextCommand.Execute(null));
            Ui.Pump(80);
        }
        Ui.Pump(300);
        Ui.Run(() =>
        {
            Assert.True(vm.AtEnd);
            var layer = Layer();
            var progress = (Panel)layer.FindName("Progress");
            Assert.Equal(n, progress.Children.Count);
            var faint = (SolidColorBrush)Application.Current.FindResource("FaintBrush");
            Assert.All(progress.Children.Cast<Border>(), b => Assert.Equal(faint.Color, ((SolidColorBrush)b.Background).Color));
            Assert.Equal(n, ((Panel)layer.FindName("Thumbs")).Children.Count);
            Ui.SavePng(host.Window, Path.Combine(Shots, "rewind-end.png"));
        });

        Ui.Run(() => vm.OpenShareCommand.Execute(null));
        Ui.Pump(400);
        Ui.Run(() =>
        {
            Assert.True(vm.IsSharing);
            Assert.NotNull(((Border)Layer().FindName("PreviewHost")).Child);
            Ui.SavePng(host.Window, Path.Combine(Shots, "rewind-share.png"));
            vm.CloseShareCommand.Execute(null);
            vm.CloseCommand.Execute(null);
        });
        Ui.AssertNoProblems("rewind");
    }

    private void AssertEnd(int n, string shot) => Ui.Run(() =>
    {
        var vm = host.Shell.Rewind;
        Assert.True(vm.AtEnd, shot);
        var layer = Layer();
        var faint = ((SolidColorBrush)Application.Current.FindResource("FaintBrush")).Color;
        Assert.All(((Panel)layer.FindName("Progress")).Children.Cast<Border>(), b => Assert.Equal(faint, ((SolidColorBrush)b.Background).Color));
        Assert.Equal(n, ((Panel)layer.FindName("Thumbs")).Children.Count);
        Ui.SavePng(host.Window, Path.Combine(Shots, shot + ".png"));
    });

    [Fact]
    public void Every_way_into_a_rewind_ends_on_the_full_end_screen()
    {
        var vm = host.Shell.Rewind;
        var f = October();
        int n = RewindCards.Of(f).Count;
        // From the full screen: Watch, then card by card with the keyboard.
        Ui.Run(() => vm.Reveal(f, []));
        Ui.Pump(300);
        Ui.Run(() => vm.WatchRevealedCommand.Execute(null));
        Ui.Pump(200);
        for (int i = 0; i < n; i++)
        {
            Ui.Run(() => Layer().RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(host.Window), 0, System.Windows.Input.Key.Right) { RoutedEvent = UIElement.PreviewKeyDownEvent }));
            Ui.Pump(60);
        }
        Ui.Pump(200);
        AssertEnd(n, "rewind-end-keys");
        // Closed, then Share from the list: the end screen behind the Share window, and after closing it.
        Ui.Run(() => vm.CloseCommand.Execute(null));
        Ui.Pump(100);
        Ui.Run(() => vm.Watch(f, toShare: true));
        Ui.Pump(300);
        Ui.Run(() => vm.CloseShareCommand.Execute(null));
        Ui.Pump(200);
        AssertEnd(n, "rewind-end-from-list");
        // Watched again from the list after that.
        Ui.Run(() => { vm.CloseCommand.Execute(null); vm.Watch(f); });
        Ui.Pump(200);
        for (int i = 0; i < n; i++) { Ui.Run(() => vm.NextCommand.Execute(null)); Ui.Pump(40); }
        Ui.Pump(200);
        AssertEnd(n, "rewind-end-again");
        // Share, a shape and a look changed (settings saved and sent back by the agent), then closed.
        Ui.Run(() => vm.OpenShareCommand.Execute(null));
        Ui.Pump(200);
        Ui.Run(() => { vm.Shape = "post"; vm.SetTheme("grey"); vm.SetAccent("pink"); });
        Ui.Pump(600);
        Ui.Run(() => vm.SameAsAppCommand.Execute(null));
        Ui.Pump(400);
        Ui.Run(() => vm.CloseShareCommand.Execute(null));
        Ui.Pump(200);
        AssertEnd(n, "rewind-end-after-share");
        Ui.Run(() => vm.CloseCommand.Execute(null));
    }
}
