using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The user's dashboards as pages of the sidebar: listed in the editor under DASHBOARDS, moved among themselves and
/// into any group, renamed there and on their own page, hidden without being deleted, and gone without a trace when deleted.
/// </summary>
[Collection("UI")]
public sealed class SidebarDashboardTests
{
    private const string Gaming = "custom:gaming0001", Work = "custom:work000001";

    /// <summary>A sidebar with the user's dashboards, kept as the shell keeps them.</summary>
    private sealed record Boards(SidebarViewModel Sidebar, SettingsModel Settings, ObservableCollection<CustomPageViewModel> Pages, List<string> Opened, Func<CustomPageConfig, CustomPageViewModel> Make)
    {
        public CustomPageViewModel Page(string key) => Pages.Single(p => p.NavKey == key);
        public NavEntry Entry(string key) => Sidebar.EntryOf(key)!;
        public SidebarSettings Saved => Settings.Current.Sidebar;

        public CustomPageViewModel New(string name)
        {
            var page = Make(new CustomPageConfig { Name = name, Grid = CustomPageConfig.CurrentGrid });
            Pages.Add(page);
            page.AddTiles([]); // as a new dashboard is saved
            return page;
        }
    }

    private static RigsightSettings TwoDashboards()
    {
        var start = SeedData.QuietSettings();
        start.CustomPages.Add(new CustomPageConfig { Id = "gaming0001", Name = "Gaming", Grid = CustomPageConfig.CurrentGrid });
        start.CustomPages.Add(new CustomPageConfig { Id = "work000001", Name = "Work", Grid = CustomPageConfig.CurrentGrid });
        return start;
    }

    private static Boards Sidebar(RigsightSettings? start = null)
    {
        SharedData.EnsureSeeded();
        var settings = Kit.OfflineSettings(start ?? TwoDashboards());
        var opened = new List<string>();
        return Ui.Run(() =>
        {
            var live = new LiveData(settings);
            var reports = new ReportService(settings);
            var (home, crashes) = (new HomeViewModel(reports, live), new CrashesViewModel(reports, settings));
            var pages = new ObservableCollection<CustomPageViewModel>();
            CustomPageViewModel Make(CustomPageConfig config) => new(config, settings, live, home, crashes, open: _ => { }, delete: page =>
            {
                settings.Update(s => s.CustomPages.RemoveAll(p => p.Id == page.Id));
                pages.Remove(page);
                page.Dispose();
            });
            foreach (var config in settings.Current.CustomPages) pages.Add(Make(config));
            var sidebar = new SidebarViewModel(settings, opened.Add, pages);
            settings.Changed += sidebar.Refresh; // as the shell has it
            return new Boards(sidebar, settings, pages, opened, Make);
        });
    }

    private static string[] Keys(IEnumerable<NavEntry> entries) => [.. entries.Select(e => e.Key)];

    /// <summary>The same settings as the next start would read them, with the dashboards they hold.</summary>
    private static Boards Again(Boards boards) => Sidebar(SettingsStore.Deserialize(SettingsStore.Serialize(boards.Settings.Current)));

    [Fact]
    public void Dashboards_are_pages_of_their_group()
    {
        var boards = Sidebar();
        Ui.Run(() =>
        {
            var sidebar = boards.Sidebar;
            Assert.Equal([Gaming, Work], Keys(sidebar.DashboardsGroup.Pages));
            Assert.Equal(["Gaming", "Work"], sidebar.DashboardsGroup.Pages.Select(e => e.Title));
            Assert.Equal(["Gaming", "Work"], sidebar.DashboardsGroup.Pages.Select(e => e.Name)); // the name box shows the dashboard's own name
            Assert.All(sidebar.DashboardsGroup.Pages, e => Assert.True(e is { CanHide: true, IsShown: true, IsVisible: true, BuiltInTitle: "Dashboard" }));
            Assert.All(sidebar.DashboardsGroup.Pages, e => Assert.Equal("", e.Icon));
            Assert.False(sidebar.DashboardsGroup.ShowDropBox);
            // The other groups are as they were, and nothing is saved about a layout nobody changed.
            Assert.Equal(["home", "reports", "apps", "processes", "crashes", "timeline"], Keys(sidebar.Main));
            Assert.Equal(["temperatures", "fans", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware));
            Assert.Empty(boards.Saved.Order);
            Assert.Empty(boards.Saved.Groups);
            Assert.Equal("", sidebar.HiddenSummary);

            // Picking one in the sidebar opens it; the page changing ticks it without opening it again.
            boards.Entry(Work).IsSelected = true;
            Assert.Equal([Work], boards.Opened);
            sidebar.Select(Work);
            Assert.Equal([Work], boards.Opened);
            Assert.True(boards.Entry(Work).IsSelected);
            Assert.False(boards.Entry(Gaming).IsSelected);

            // A folded DASHBOARDS keeps only the one you're on.
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.Dashboards);
            Assert.Equal((false, true), (boards.Entry(Gaming).IsVisible, boards.Entry(Work).IsVisible));
        });
    }

    [Fact]
    public void Settings_from_before_dashboards_were_in_the_editor_give_todays_sidebar()
    {
        // As 0.19 wrote them: dashboards in their own list, a sidebar that knows only the built-in pages.
        var old = SettingsStore.Deserialize("""
            { "SettingsVersion": 9,
              "CustomPages": [ { "Id": "gaming0001", "Name": "Gaming", "Grid": 2 }, { "Id": "work000001", "Name": "Work", "Grid": 2 } ],
              "Sidebar": { "Order": ["home", "reports", "apps", "crashes", "timeline", "fans", "temperatures"], "Hidden": ["storage"], "Collapsed": ["dashboards"] } }
            """);
        var boards = Sidebar(old);
        Ui.Run(() =>
        {
            Assert.Equal([Gaming, Work], Keys(boards.Sidebar.DashboardsGroup.Pages)); // in the order they were made
            Assert.Equal(["home", "reports", "apps", "crashes", "timeline", "processes"], Keys(boards.Sidebar.Main));
            Assert.Equal(["fans", "temperatures", "memory", "storage", "network", "sensors"], Keys(boards.Sidebar.Hardware));
            Assert.True(boards.Sidebar.DashboardsCollapsed);
            Assert.All(boards.Sidebar.DashboardsGroup.Pages, e => Assert.True(e is { IsShown: true, IsVisible: false }));
            Assert.Equal("· 1 page hidden", boards.Sidebar.HiddenSummary);
        });
    }

    [Fact]
    public void Dashboards_are_put_in_any_order()
    {
        var boards = Sidebar();
        Ui.Run(() =>
        {
            Assert.True(boards.Sidebar.MoveBy(boards.Entry(Work), -1));
            Assert.Equal([Work, Gaming], Keys(boards.Sidebar.DashboardsGroup.Pages));
            Assert.False(boards.Sidebar.MoveTo(boards.Entry(Work), boards.Sidebar.DashboardsGroup, 0)); // where it already is
            Assert.Equal([Work, Gaming], boards.Saved.Order.Where(k => k.StartsWith("custom:")));
            Assert.Equal(boards.Saved.Order.IndexOf("timeline") + 1, boards.Saved.Order.IndexOf(Work)); // between the top group and HARDWARE
            Assert.Empty(boards.Saved.Groups);
            // The dashboards themselves are untouched.
            Assert.Equal(["Gaming", "Work"], boards.Settings.Current.CustomPages.Select(p => p.Name));

            var third = boards.New("Stream");
            Assert.Equal([Work, Gaming, third.NavKey], Keys(boards.Sidebar.DashboardsGroup.Pages)); // a new one goes last
            Assert.True(boards.Sidebar.MoveTo(boards.Entry(third.NavKey), boards.Sidebar.DashboardsGroup, 1));
            Assert.Equal([Work, third.NavKey, Gaming], Keys(boards.Sidebar.DashboardsGroup.Pages));
        });
        var again = Again(boards);
        Ui.Run(() => Assert.Equal(["Work", "Stream", "Gaming"], again.Sidebar.DashboardsGroup.Pages.Select(e => e.Title)));
    }

    [Fact]
    public void A_dashboard_moves_into_any_group_and_a_page_in_among_the_dashboards()
    {
        var boards = Sidebar();
        Ui.Run(() =>
        {
            var sidebar = boards.Sidebar;
            // Gaming to the top group, after Home; Work to the end of ON SCREEN; Memory in among the dashboards.
            Assert.True(sidebar.MoveTo(boards.Entry(Gaming), sidebar.MainGroup, 1));
            Assert.True(sidebar.MoveTo(boards.Entry(Work), sidebar.OnScreenGroup, 3));
            Assert.True(sidebar.MoveTo(boards.Entry("memory"), sidebar.DashboardsGroup, 0));
            Assert.Equal(["home", Gaming, "reports", "apps", "processes", "crashes", "timeline"], Keys(sidebar.Main));
            Assert.Equal(["memory"], Keys(sidebar.DashboardsGroup.Pages));
            Assert.Equal(["temperatures", "fans", "storage", "network", "sensors"], Keys(sidebar.Hardware));
            Assert.Equal(["widgets", "overlay", "taskbar", Work], Keys(sidebar.OnScreen));
            Assert.Equal(("main", "onscreen", "dashboards"), (boards.Saved.Groups[Gaming], boards.Saved.Groups[Work], boards.Saved.Groups["memory"]));
            // Wherever it is, it is still a dashboard: its icon, its name, and it opens its page.
            Assert.Equal("", sidebar.Main[1].Icon);
            Assert.Equal("Gaming", sidebar.Main[1].Title);
            sidebar.Main[1].IsSelected = true;
            Assert.Equal([Gaming], boards.Opened);

            // It folds with the group it is in now, not with DASHBOARDS.
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.Dashboards);
            Assert.True(boards.Entry(Work).IsVisible);
            Assert.False(boards.Entry("memory").IsVisible);
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.OnScreenSection);
            Assert.False(boards.Entry(Work).IsVisible);
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.Dashboards);
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.OnScreenSection);

            // The arrow keys cross the same edges: up out of ON SCREEN's start is HARDWARE's end, and on up through DASHBOARDS.
            Assert.True(sidebar.MoveTo(boards.Entry(Work), sidebar.OnScreenGroup, 0));
            Assert.True(sidebar.MoveBy(boards.Entry(Work), -1));
            Assert.Equal(Work, sidebar.Hardware.Last().Key);
            Assert.True(sidebar.MoveTo(boards.Entry(Work), sidebar.HardwareGroup, 0));
            Assert.True(sidebar.MoveBy(boards.Entry(Work), -1));
            Assert.Equal(["memory", Work], Keys(sidebar.DashboardsGroup.Pages));
            Assert.False(boards.Saved.Groups.ContainsKey(Work)); // home again: nothing is kept about it
        });

        var again = Again(boards);
        Ui.Run(() =>
        {
            Assert.Equal(["home", Gaming, "reports", "apps", "processes", "crashes", "timeline"], Keys(again.Sidebar.Main));
            Assert.Equal(["memory", Work], Keys(again.Sidebar.DashboardsGroup.Pages));
            Assert.Equal(["widgets", "overlay", "taskbar"], Keys(again.Sidebar.OnScreen));
        });
    }

    [Fact]
    public void A_dashboard_has_one_name_in_the_editor_and_on_its_own_page()
    {
        var boards = Sidebar();
        Ui.Run(() =>
        {
            var (entry, page) = (boards.Entry(Gaming), boards.Page(Gaming));
            // Typed in the editor: the dashboard itself is renamed, and nothing is kept beside it as for a built-in page.
            entry.Name = "Games";
            Assert.Equal("Games", page.Name);
            Assert.Equal("Games", entry.Title);
            Assert.Equal("Games", boards.Settings.Current.CustomPages.Single(p => p.Id == page.Id).Name);
            Assert.Empty(boards.Saved.Names);

            // Renamed on its own page: the sidebar and the editor's box follow.
            var changes = Kit.Changes(entry, () => page.Name = "Play");
            Assert.Contains(nameof(NavEntry.Title), changes);
            Assert.Contains(nameof(NavEntry.Name), changes);
            Assert.Equal(("Play", "Play"), (entry.Title, entry.Name));

            // A space in the middle of typing stays; emptied, it is "Dashboard" (as the dashboard is saved then).
            entry.Name = "Play ";
            Assert.Equal(("Play ", "Play"), (entry.Name, entry.Title));
            entry.Name = "";
            Assert.Equal(("", "Dashboard"), (entry.Name, entry.Title));
            Assert.Equal("Dashboard", boards.Settings.Current.CustomPages.Single(p => p.Id == page.Id).Name);
            entry.Name = "Gaming";

            // The other dashboard, and a built-in page's own way of being renamed, are as they were.
            Assert.Equal("Work", boards.Entry(Work).Title);
            boards.Entry("memory").Name = "RAM";
            Assert.Equal("RAM", boards.Saved.Names["memory"]);
        });
    }

    [Fact]
    public void A_hidden_dashboard_is_out_of_the_sidebar_and_still_there()
    {
        var boards = Sidebar();
        Ui.Run(() =>
        {
            var entry = boards.Entry(Gaming);
            entry.IsShown = false;
            Assert.False(entry.IsVisible);
            Assert.False(entry.IsShown);
            Assert.Equal([Gaming], boards.Saved.Hidden);
            Assert.Equal("· 1 page hidden", boards.Sidebar.HiddenSummary);
            // Not deleted: the dashboard, its row in the editor, and its place.
            Assert.Contains(boards.Pages, p => p.NavKey == Gaming);
            Assert.Equal(["Gaming", "Work"], boards.Settings.Current.CustomPages.Select(p => p.Name));
            Assert.Equal([Gaming, Work], Keys(boards.Sidebar.DashboardsGroup.Pages));

            // On screen (opened from a link, or hidden while open), it stays in the sidebar like any hidden page.
            boards.Sidebar.Select(Gaming);
            Assert.True(entry.IsVisible);
            boards.Sidebar.Select("home");
            Assert.False(entry.IsVisible);
        });
        var again = Again(boards);
        Ui.Run(() =>
        {
            Assert.False(again.Entry(Gaming).IsVisible);
            again.Entry(Gaming).IsShown = true;
            Assert.True(again.Entry(Gaming).IsVisible);
            Assert.Empty(again.Saved.Hidden);
            Assert.Equal("", again.Sidebar.HiddenSummary);
        });
    }

    [Fact]
    public void A_deleted_dashboard_leaves_nothing_in_the_saved_layout()
    {
        var boards = Sidebar();
        Ui.Run(() =>
        {
            var sidebar = boards.Sidebar;
            sidebar.MoveTo(boards.Entry(Work), sidebar.HardwareGroup, 2);
            boards.Entry(Work).IsShown = false;
            sidebar.MoveBy(boards.Entry("fans"), -1);
            Assert.Contains(Work, boards.Saved.Order);
            Assert.Equal("· 1 page hidden", sidebar.HiddenSummary);

            boards.Page(Work).DeletePageCommand.Execute(null);
            Assert.Null(sidebar.EntryOf(Work));
            Assert.DoesNotContain(sidebar.Groups.SelectMany(g => g.Pages), e => e.Key == Work);
            Assert.DoesNotContain(Work, boards.Saved.Order);
            Assert.Empty(boards.Saved.Hidden);
            Assert.Empty(boards.Saved.Groups);
            Assert.Equal("", sidebar.HiddenSummary);
            // What the user arranged otherwise stays.
            Assert.Equal([Gaming], Keys(sidebar.DashboardsGroup.Pages));
            Assert.Equal(["fans", "temperatures", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware));

            // The last one gone: DASHBOARDS is empty, and takes a page dropped in.
            boards.Page(Gaming).DeletePageCommand.Execute(null);
            Assert.Empty(sidebar.DashboardsGroup.Pages);
            Assert.True(sidebar.DashboardsGroup.ShowDropBox);
            Assert.DoesNotContain(boards.Saved.Order, k => k.StartsWith("custom:"));

            // A new one is a row at once, at the end of DASHBOARDS, and making it opens nothing by itself.
            var made = boards.New("Fresh");
            Assert.Equal([made.NavKey], Keys(sidebar.DashboardsGroup.Pages));
            Assert.Equal("Fresh", sidebar.DashboardsGroup.Pages[0].Title);
            Assert.False(sidebar.DashboardsGroup.ShowDropBox);
            Assert.Empty(boards.Opened);
            // The deleted dashboard is let go of: renaming it no longer reaches anything here.
            Assert.Equal(15 + 1, sidebar.Groups.Sum(g => g.Pages.Count));
        });
    }

    [Fact]
    public void A_settings_file_naming_a_dashboard_that_is_gone_is_repaired_on_load()
    {
        var s = SettingsStore.Deserialize("""
            { "SettingsVersion": 9,
              "CustomPages": [ { "Id": "gaming0001", "Name": "Gaming", "Grid": 2 } ],
              "Sidebar": { "Order": ["home", "custom:work000001", "custom:gaming0001", "reports"], "Hidden": ["custom:work000001", "fans", "custom:gaming0001"],
                           "Groups": { "custom:work000001": "main", "custom:gaming0001": "hardware", "memory": "dashboards" } } }
            """);
        Assert.Equal(["home", Gaming, "reports"], s.Sidebar.Order);
        Assert.Equal(["fans", Gaming], s.Sidebar.Hidden);
        Assert.Equal([Gaming, "memory"], s.Sidebar.Groups.Keys.Order());

        var boards = Sidebar(s);
        Ui.Run(() =>
        {
            Assert.Equal(["memory"], Keys(boards.Sidebar.DashboardsGroup.Pages));
            Assert.Equal(Gaming, boards.Sidebar.Hardware[0].Key);
            Assert.False(boards.Entry(Gaming).IsShown);
        });
    }

    // ── The editor ──

    [Fact]
    public void The_editor_has_a_row_for_every_dashboard_and_none_for_new_dashboard()
    {
        var boards = Sidebar();
        Ui.TakeProblems();
        Ui.Run(() =>
        {
            var sidebar = boards.Sidebar;
            var vm = new SettingsViewModel(boards.Settings, new AgentClient(Ui.Dispatcher), new ReportService(boards.Settings)) { Sidebar = sidebar };
            var window = new Window { Content = new Views.SettingsView { DataContext = vm }, Left = -32000, Top = -32000, Width = 1200, Height = 900, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            Ui.Pump(200);
            try
            {
                static string NameOf(DependencyObject o) => System.Windows.Automation.AutomationProperties.GetName(o);
                var panel = Visuals.Descendants<Border>(window).Single(b => b.Name == "EditorPanel");
                sidebar.IsEditing = true;
                Ui.Pump(400);

                // Grip, name box and switch, like every other page; under the DASHBOARDS heading, in their order.
                var grips = Visuals.Descendants<Thumb>(panel).Where(t => t.DataContext is NavEntry).ToList();
                Assert.Equal(15 + 2, grips.Count);
                Assert.Equal(["timeline", Gaming, Work, "temperatures"], grips.Select(g => ((NavEntry)g.DataContext).Key).Skip(5).Take(4));
                Assert.Contains(grips, g => NameOf(g) == "Move Gaming: drag it, or use the arrow keys");
                var boxes = Visuals.Descendants<TextBox>(panel).Where(b => b.IsVisible).ToList();
                Assert.Equal(15 + 2 + 3, boxes.Count);
                var box = boxes.Single(b => b.DataContext == boards.Entry(Gaming));
                Assert.Equal(("Gaming", "Dashboard"), (box.Text, box.Tag));
                var switches = Visuals.Descendants<CheckBox>(panel).Where(c => c.IsVisible).ToList();
                Assert.Contains(switches, c => NameOf(c) == "Show Gaming" && c.IsEnabled && c.IsChecked == true);
                Assert.Contains(switches, c => NameOf(c) == "Show Work");
                // "New dashboard" is the sidebar's own button, not a page; and with dashboards there is nothing to drop into.
                var texts = Visuals.Descendants<TextBlock>(panel).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                Assert.DoesNotContain("New dashboard", texts);
                Assert.DoesNotContain("Drag a page here", texts);
                Assert.DoesNotContain(boxes, b => b.Text == "New dashboard");

                // Typing over the name renames the dashboard with each letter; renamed on its page, the box follows.
                box.Text = "Games";
                Assert.Equal("Games", boards.Page(Gaming).Name);
                boards.Page(Gaming).Name = "Gaming";
                Assert.Equal("Gaming", box.Text);

                // Its switch hides it and keeps it.
                switches.Single(c => NameOf(c) == "Show Work").IsChecked = false;
                Assert.False(boards.Entry(Work).IsVisible);
                Assert.Contains(boards.Pages, p => p.NavKey == Work);
                Assert.Contains(Visuals.Descendants<TextBlock>(window), t => t.Text == "· 1 page hidden" && t.IsVisible);

                // The arrow keys on its grip carry it out of DASHBOARDS, and the row built in its new place has a grip again.
                var grip = grips.Single(g => g.DataContext == boards.Entry(Work));
                var source = PresentationSource.FromVisual(grip)!;
                grip.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Down)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Assert.Equal(Work, sidebar.Hardware[0].Key);
                Ui.Pump(50);
                grip = Visuals.Descendants<Thumb>(panel).Single(g => g.DataContext == boards.Entry(Work));

                // Dragged: letting go puts it where the line is. (The pointer is far below this off-screen window, so
                // the nearest place is the very end of the list.)
                grip.RaiseEvent(new DragStartedEventArgs(0, 0));
                grip.RaiseEvent(new DragDeltaEventArgs(0, 40));
                grip.RaiseEvent(new DragCompletedEventArgs(0, 40, false));
                Assert.Equal(["widgets", "overlay", "taskbar", Work], Keys(sidebar.OnScreen));

                // And a built-in page dragged the other way: Gaming's place is the only one in DASHBOARDS, Memory joins it by key.
                var memory = Visuals.Descendants<Thumb>(panel).Single(g => g.DataContext == boards.Entry("memory"));
                memory.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Up)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Ui.Pump(50);
                memory = Visuals.Descendants<Thumb>(panel).Single(g => g.DataContext == boards.Entry("memory"));
                memory.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Up)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Ui.Pump(50);
                memory = Visuals.Descendants<Thumb>(panel).Single(g => g.DataContext == boards.Entry("memory"));
                memory.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Up)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Assert.Equal([Gaming, "memory"], Keys(sidebar.DashboardsGroup.Pages));
                Ui.Pump(200);
                Ui.SavePng(window.Content as FrameworkElement ?? panel, Path.Combine(TestEnvironment.DataDir, "screens", "sidebar-editor-dashboards.png"));

                // Deleted elsewhere while the editor is open: its row goes.
                boards.Page(Gaming).DeletePageCommand.Execute(null);
                Ui.Pump(50);
                Assert.DoesNotContain(Visuals.Descendants<Thumb>(panel), g => g.DataContext is NavEntry { Key: Gaming });
                Assert.Equal(15 + 1, Visuals.Descendants<Thumb>(panel).Count(t => t.DataContext is NavEntry));
            }
            finally
            {
                window.Close();
            }
        });
        Ui.AssertNoProblems("the sidebar editor with dashboards");
    }
}
