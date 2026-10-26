using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.AppUi;

/// <summary>
/// The sidebar with icons only, and the pages it shows (approved mock-up, 2026-10-25), in the real window: the title bar's
/// pane button, the width, each page's name as its hover, one icon per group with its pages listed beside it, the fold's
/// shimmer, the Customize pop-up and a new install's card. Pictures go to screens\sidebar-*.png.
/// </summary>
[Collection("UI")]
public sealed class SidebarLookTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");

    private FrameworkElement Part(string name) => (FrameworkElement)host.Window.FindName(name);

    private void Shot(string name) => Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, $"sidebar-{name}.png")));

    private static string NameOf(DependencyObject o) => System.Windows.Automation.AutomationProperties.GetName(o);

    /// <summary>The sidebar's page buttons as drawn.</summary>
    private List<RadioButton> Items() => Ui.Run(() => Visuals.Descendants<RadioButton>(Part("SidebarHost")).Where(r => r.GroupName == "Nav").ToList());

    private FrameworkElement ListOf(ToggleButton group) => (FrameworkElement)Visuals.Descendants<Popup>((DependencyObject)group.Parent).Single().Child;

    [Fact]
    public void Icons_only_folds_to_one_icon_per_group_with_its_pages_beside_it_and_opens_again()
    {
        Ui.TakeProblems();
        host.Show("home", 600);
        try
        {
            Assert.Equal(232, Ui.Run(() => Part("SidebarHost").ActualWidth));
            Assert.Equal(("", "Collapse the sidebar"), Ui.Run(() => (host.Shell.Sidebar.HandleGlyph, NameOf(Part("SidebarHandle")))));
            Ui.Run(() => Part("SidebarHandle").Opacity = 1);
            Shot("handle-wide");
            Ui.Run(() => Part("SidebarHandle").ClearValue(UIElement.OpacityProperty));
            Ui.Run(() => ((Button)Part("SidebarHandle")).Command.Execute(null));
            // At once: no animation (the user, 2026-10-25), so the very next layout is the narrow one.
            Ui.Pump(30);
            Ui.Run(() =>
            {
                Assert.Equal(1, Part("SidebarHost").Opacity);
                Assert.Null(Part("SidebarHost").Clip);
                Assert.False(Part("SidebarHandle").Focusable);
            });
            Assert.Equal(72, Ui.Run(() => Part("SidebarHost").ActualWidth));
            Assert.Equal(("", "Expand the sidebar"), Ui.Run(() => (host.Shell.Sidebar.HandleGlyph, NameOf(Part("SidebarHandle")))));
            // The handle's centre sits on the edge line (the sidebar's 1px right border), level with Home.
            Ui.Run(() => Assert.InRange(Part("SidebarHandle").TranslatePoint(new Point(11, 0), Part("SidebarHost")).X, 70.5, 72.5));
            // A click on it reaches it: nothing on top of either half (the tests can't move the mouse, so the window is asked
            // what is under each point).
            Ui.Run(() =>
            {
                var handle = Part("SidebarHandle");
                foreach (var x in new[] { 4.0, 11, 18 })
                {
                    var at = handle.TranslatePoint(new Point(x, 11), host.Window);
                    var hit = host.Window.InputHitTest(at) as DependencyObject;
                    var chain = new List<string>();
                    for (var d = hit; d is not null; d = VisualTreeHelper.GetParent(d)) chain.Add(d is FrameworkElement f && f.Name != "" ? f.Name : d.GetType().Name);
                    Assert.True(chain.Contains("SidebarHandle"), $"at {at}: {string.Join(" < ", chain.Take(8))}");
                }
            });
            Ui.Run(() =>
            {
                // Unseen until the pointer is over the sidebar, and clear of Home's icon when it shows.
                var handle = Part("SidebarHandle");
                Assert.Equal(0, handle.Opacity);
                var home = Items().First(r => NameOf(r) == "Home");
                var handleBox = new Rect(handle.TranslatePoint(new Point(0, 0), Part("SidebarHost")), handle.RenderSize);
                var homeBox = new Rect(home.TranslatePoint(new Point(0, 0), Part("SidebarHost")), home.RenderSize);
                Assert.False(handleBox.IntersectsWith(homeBox), $"handle {handleBox} over Home {homeBox}");
            });

            Ui.Run(() =>
            {
                var home = Items().First(r => NameOf(r) == "Home");
                Assert.Equal("Home", home.ToolTip);
                Assert.Equal(Visibility.Collapsed, ((ContentPresenter)home.Template.FindName("Label", home)).Visibility);
                Assert.True(home.ActualWidth <= 52);
                // The groups' own pages aren't in the rail: one icon each instead.
                Assert.DoesNotContain(Items(), r => r.IsVisible && NameOf(r) is "Temperatures" or "Widgets");
                var groups = Visuals.Descendants<ToggleButton>(Part("SidebarHost")).Where(t => t.DataContext is SidebarGroup && t.IsVisible).Select(NameOf).ToList();
                Assert.Equal(["Dashboards", "Hardware", "On screen"], groups);
            });
            Shot("icons-only");
            // As it shows under the pointer (set by hand here: the tests don't move the mouse).
            Ui.Run(() => Part("SidebarHandle").Opacity = 1);
            Shot("handle-narrow");
            Ui.Run(() => Part("SidebarHandle").ClearValue(UIElement.OpacityProperty));

            // A group's icon lists its pages beside it, names and all; picking one opens it and closes the list.
            var hardware = Ui.Run(() => Visuals.Descendants<ToggleButton>(Part("SidebarHost")).Single(t => NameOf(t) == "Hardware"));
            Ui.Run(() => hardware.IsChecked = true);
            Ui.Pump(300);
            Shot("group-list");
            // The list is a window of its own: its own picture.
            Ui.Run(() => Ui.SavePng(ListOf(hardware), Path.Combine(Shots, "sidebar-group-list-alone.png")));
            Assert.Equal(["Temperatures", "Fans", "Memory", "Storage", "Network", "All sensors"], Ui.Run(() => Visuals.Descendants<RadioButton>(ListOf(hardware)).Select(NameOf).ToList()));
            Ui.Run(() =>
            {
                var fans = Visuals.Descendants<RadioButton>(ListOf(hardware)).Single(r => NameOf(r) == "Fans");
                fans.IsChecked = true;
                fans.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            });
            Ui.Pump(400);
            Assert.Equal("fans", Ui.Run(() => host.Shell.CurrentPage));
            Assert.False(Ui.Run(() => hardware.IsChecked == true));
            // The open page is in that group: its icon is marked.
            Assert.True(Ui.Run(() => host.Shell.Sidebar.HardwareGroup.HasSelected));
            Ui.AssertNoProblems("icons only");
            Assert.True(Ui.Run(() => host.Shell.Settings.Current.Sidebar.IconsOnly));
        }
        finally
        {
            Ui.Run(() => host.Shell.Sidebar.IconsOnly = false);
            Ui.Pump(700);
            host.Show("home", 300);
        }
        Assert.Equal(232, Ui.Run(() => Part("SidebarHost").ActualWidth));
    }

    [Fact]
    public void A_new_install_is_asked_once_and_the_basics_leave_only_their_pages_and_headings()
    {
        Ui.TakeProblems();
        host.Show("home", 600);
        try
        {
            Ui.Run(() => { host.Shell.Settings.Update(s => s.Sidebar.AskPages = true); host.Shell.Sidebar.Refresh(); });
            Ui.Pump(300);
            Assert.Equal(Visibility.Visible, Ui.Run(() => Part("PagesLayer").Visibility));
            Ui.Run(() =>
            {
                // The same 16 tiles in both pictures: the basics light 6.
                var card = Part("PagesLayer");
                var tiles = Visuals.Descendants<ItemsControl>(card).Where(i => i.ItemsSource is IReadOnlyList<SidebarTile>).ToList();
                Assert.Equal(2, tiles.Count);
                Assert.Equal(6, ((IReadOnlyList<SidebarTile>)tiles[0].ItemsSource).Count(t => t.Lit));
                Assert.Equal(16, ((IReadOnlyList<SidebarTile>)tiles[1].ItemsSource).Count(t => t.Lit));
                var texts = Visuals.Descendants<TextBlock>(card).Select(t => t.Text).ToList();
                Assert.Contains("RECOMMENDED", texts);
                Assert.Contains("6 of the 16 pages, to see whether your PC is fine and what went wrong.", texts);
            });
            Shot("new-install-card");

            Ui.Run(() => host.Shell.Sidebar.PickPagesCommand.Execute("basic"));
            Ui.Pump(300);
            Assert.Equal(Visibility.Collapsed, Ui.Run(() => Part("PagesLayer").Visibility));
            // The test PC's own dashboard stays: the basics never hide what the person made.
            var dashboards = Ui.Run(() => host.Shell.Sidebar.DashboardsGroup.Pages.Select(p => p.Title).ToHashSet());
            var shown = Ui.Run(() => Items().Where(r => r.IsVisible).Select(NameOf).ToList());
            Assert.True(shown.Where(n => n != "Ask RigSight" && !dashboards.Contains(n)).SequenceEqual(["Home", "Reports", "Games", "Crashes", "Temperatures", "Storage"]), string.Join(", ", shown));
            Ui.Run(() =>
            {
                Assert.False(host.Shell.Sidebar.OnScreenGroup.HeadingInSidebar);
                Assert.Equal(dashboards.Count > 0, host.Shell.Sidebar.DashboardsGroup.HeadingInSidebar);
                Assert.True(host.Shell.Sidebar.HardwareGroup.HeadingInSidebar);
            });
            Shot("basics");
            host.Show("fans", 400);
            Assert.Contains("Fans", Ui.Run(() => Items().Where(r => r.IsVisible).Select(NameOf).ToList()));
            Ui.AssertNoProblems("the basics");
        }
        finally
        {
            Ui.Run(() => host.Shell.Sidebar.PickPagesCommand.Execute("full"));
            Ui.Run(() => host.Shell.Settings.Update(s => s.Sidebar.Pages = null));
            Ui.Pump(200);
            host.Show("home", 300);
        }
        Assert.Equal(16, Ui.Run(() => host.Shell.Sidebar.Main.Concat(host.Shell.Sidebar.Hardware).Concat(host.Shell.Sidebar.OnScreen).Count(e => e.IsVisible)));
    }

    [Fact]
    public void Customize_opens_a_pop_up_over_the_page_and_each_switch_shows_in_the_sidebar_at_once()
    {
        Ui.TakeProblems();
        host.Show("settings", 600);
        try
        {
            Ui.Run(() => host.Shell.SettingsPage.Tab = "appearance");
            Ui.Pump(300);
            var settings = Ui.Run(() => Visuals.Descendants<Views.SettingsView>(host.Window).Single());
            Ui.Run(() => Visuals.Descendants<Button>(settings).Single(b => NameOf(b) == "Customize the sidebar").Command.Execute(null));
            Ui.Pump(400);
            var panel = Ui.Run(() => (FrameworkElement)settings.FindName("EditorPanel"));
            Assert.True(Ui.Run(() => panel.IsVisible));
            // Over the page, not the sidebar: the sidebar is still there beside it.
            Ui.Run(() => Assert.True(panel.TransformToAncestor(host.Window).Transform(new Point(0, 0)).X > Part("SidebarHost").ActualWidth));
            // Scrolled to its lower part, a switch there hides the page in the sidebar and the list stays where it was.
            var scroll = Ui.Run(() => (ScrollViewer)settings.FindName("EditorScroll"));
            Ui.Run(() => scroll.ScrollToVerticalOffset(scroll.ScrollableHeight));
            Ui.Pump(200);
            double before = Ui.Run(() => scroll.VerticalOffset);
            Ui.Run(() => Visuals.Descendants<CheckBox>(panel).Single(c => NameOf(c) == "Show Taskbar").IsChecked = false);
            Ui.Pump(200);
            Assert.Equal(before, Ui.Run(() => scroll.VerticalOffset));
            Assert.DoesNotContain("Taskbar", Ui.Run(() => Items().Where(r => r.IsVisible).Select(NameOf).ToList()));
            Assert.Contains("hidden · still opens from links", Ui.Run(() => Visuals.Descendants<TextBlock>(panel).Where(t => t.IsVisible).Select(t => t.Text).ToList()));
            Shot("customize");
            // Reset: every page back.
            Ui.Run(() => host.Shell.Sidebar.ResetSidebarCommand.Execute(null));
            Ui.Pump(100);
            Assert.Contains("Taskbar", Ui.Run(() => Items().Where(r => r.IsVisible).Select(NameOf).ToList()));
            Ui.AssertNoProblems("customize");
        }
        finally
        {
            Ui.Run(() => { host.Shell.Sidebar.IsEditing = false; host.Shell.Sidebar.ResetSidebarCommand.Execute(null); });
            Ui.Pump(300);
            host.Show("home", 300);
        }
    }
}
