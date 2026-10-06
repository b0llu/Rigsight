using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rigsight.Tests.App;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;
using Rigsight.Views;

namespace Rigsight.Tests.Ask;

/// <summary>The corner bubble: the chat is built only while open, and nothing of it shows while closed.</summary>
[Collection("UI")]
public sealed class AskBubbleTests
{
    [Fact]
    public void A_closed_chat_draws_nothing_and_an_open_one_is_built()
    {
        var settings = Kit.OfflineSettings();
        var (bubble, vm) = Ui.Run(() =>
        {
            var model = new AskViewModel(settings, _ => { });
            var view = new AskBubble { DataContext = model };
            Lay(view);
            return (view, model);
        });

        // Closed: no words anywhere in it (the chat's host once showed its content's type name beside Riggy).
        Ui.Run(() => Assert.Empty(Texts(bubble)));

        Ui.Run(() => vm.IsOpen = true);
        Ui.Pump();
        Ui.Run(() =>
        {
            Lay(bubble);
            Assert.NotEmpty(Texts(bubble));
            Assert.DoesNotContain(Texts(bubble), text => text.Contains("ViewModels"));
        });

        Ui.Run(() => vm.IsOpen = false);
        Ui.Pump();
        Ui.Run(() =>
        {
            Lay(bubble);
            Assert.Empty(Texts(bubble));
        });
    }

    [Fact]
    public void An_opened_chat_starts_unseen_and_ends_fully_shown()
    {
        var settings = Kit.OfflineSettings();
        var (window, bubble, vm) = Ui.Run(() =>
        {
            var model = new AskViewModel(settings, _ => { });
            var view = new AskBubble { DataContext = model };
            var host = new Window { Content = view, Width = 900, Height = 700, Left = -4000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            host.Show();
            return (host, view, model);
        });
        try
        {
            Ui.Run(() =>
            {
                vm.IsOpen = true;
                bubble.UpdateLayout();
                // Built, and not yet seen: shown whole before its fade began, it flashed.
                var panel = Named(bubble, "Panel");
                Assert.NotNull(panel);
                Assert.True(panel.Opacity < 1, "the chat was fully shown the moment it was built");
            });
            // Its fade done, it is there in full with nothing of the animation left on it (nothing moved,
            // the fade let go: a fade left attached kept its text soft for seconds).
            bool shown = Ui.WaitFor(() => Named(bubble, "Panel") is { Opacity: 1, HasAnimatedProperties: false }, 3000);
            Assert.True(shown, "the chat never ended fully shown, or kept its fade");
        }
        finally
        {
            Ui.Run(window.Close);
        }
    }

    [Fact]
    public void The_link_in_the_missing_add_on_note_closes_the_chat_and_asks_for_Riggys_part_of_Settings()
    {
        var settings = Kit.OfflineSettings();
        global::Rigsight.Core.Ask.AskLink? opened = null;
        var vm = Ui.Run(() => new AskViewModel(settings, link => opened = link) { IsOpen = true });
        Ui.Run(() => vm.GetRuntimeCommand.Execute(null));
        Assert.False(Ui.Run(() => vm.IsOpen));
        Assert.Equal(AskViewModel.SettingsRiggy, opened?.Page);
    }

    private static FrameworkElement? Named(DependencyObject parent, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: var n } match && n == name) return match;
            if (Named(child, name) is { } deeper) return deeper;
        }
        return null;
    }

    private static void Lay(FrameworkElement view)
    {
        view.Measure(new Size(900, 700));
        view.Arrange(new Rect(0, 0, 900, 700));
        view.UpdateLayout();
    }

    private static List<string> Texts(DependencyObject parent)
    {
        var found = new List<string>();
        Walk(parent);
        return found;

        void Walk(DependencyObject node)
        {
            if (node is TextBlock { Text.Length: > 0 } block) found.Add(block.Text);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
    }
}
