using System.Windows;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>The sidebar (pages moved between groups, names, hidden pages and headings, folded sections) and its editor, dashboard presets and accent colours.</summary>
[Collection("UI")]
public sealed class SidebarPresetAccentTests
{
    // ── Sidebar ──────────────────────────────────────────────────────────

    private static (SidebarViewModel Sidebar, SettingsModel Settings, List<string> Opened) Sidebar(RigsightSettings? start = null)
    {
        var settings = Kit.OfflineSettings(start);
        var opened = new List<string>();
        var sidebar = Ui.Run(() => new SidebarViewModel(settings, opened.Add));
        return (sidebar, settings, opened);
    }

    private static string[] Keys(IEnumerable<NavEntry> entries) => [.. entries.Select(e => e.Key)];

    [Fact]
    public void Sidebar_starts_in_the_usual_order()
    {
        var (sidebar, _, _) = Sidebar();
        Ui.Run(() =>
        {
            Assert.Equal(["home", "reports", "apps", "processes", "crashes", "timeline"], Keys(sidebar.Main));
            Assert.Equal(["temperatures", "fans", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware));
            Assert.Equal(["widgets", "overlay", "taskbar"], Keys(sidebar.OnScreen));
            Assert.All(sidebar.Main.Concat(sidebar.Hardware).Concat(sidebar.OnScreen), e => Assert.True(e.IsVisible));
            Assert.False(sidebar.DashboardsCollapsed);
            Assert.False(sidebar.HardwareCollapsed);
        });
    }

    [Fact]
    public void Moving_a_page_within_its_group_is_saved()
    {
        var (sidebar, settings, _) = Sidebar();
        Ui.Run(() =>
        {
            var fans = sidebar.Hardware.Single(e => e.Key == "fans");
            Assert.True(sidebar.MoveBy(fans, -1));
            Assert.Equal(["fans", "temperatures", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware));

            // Dropped on a place: the index counts the rows as they are, the moved one included.
            var apps = sidebar.Main.Single(e => e.Key == "apps");
            Assert.True(sidebar.MoveTo(apps, sidebar.MainGroup, 6));
            Assert.Equal(["home", "reports", "processes", "crashes", "timeline", "apps"], Keys(sidebar.Main));
            Assert.False(sidebar.MoveTo(apps, sidebar.MainGroup, 6)); // where it already is
            Assert.False(sidebar.MoveTo(apps, sidebar.MainGroup, 5));
            Assert.True(sidebar.MoveTo(apps, sidebar.MainGroup, 1));
            Assert.Equal(["home", "apps", "reports", "processes", "crashes", "timeline"], Keys(sidebar.Main));

            Assert.Equal(["home", "apps", "reports", "processes", "crashes", "timeline", "fans", "temperatures", "memory", "storage", "network", "sensors", "widgets", "overlay", "taskbar"],
                settings.Current.Sidebar.Order);
            Assert.Empty(settings.Current.Sidebar.Groups); // nothing left its own group
        });
    }

    [Fact]
    public void A_page_moves_to_any_group_and_stays_there()
    {
        var (sidebar, settings, _) = Sidebar();
        Ui.Run(() =>
        {
            // Memory out of HARDWARE into the top group, after Reports; Widgets into HARDWARE, first.
            var memory = sidebar.Hardware.Single(e => e.Key == "memory");
            Assert.True(sidebar.MoveTo(memory, sidebar.MainGroup, 2));
            var widgets = sidebar.OnScreen.Single(e => e.Key == "widgets");
            Assert.True(sidebar.MoveTo(widgets, sidebar.HardwareGroup, 0));
            Assert.Equal(["home", "reports", "memory", "apps", "processes", "crashes", "timeline"], Keys(sidebar.Main));
            Assert.Equal(["widgets", "temperatures", "fans", "storage", "network", "sensors"], Keys(sidebar.Hardware));
            Assert.Equal(["overlay", "taskbar"], Keys(sidebar.OnScreen));
            Assert.Equal("main", settings.Current.Sidebar.Groups["memory"]);
            Assert.Equal("hardware", settings.Current.Sidebar.Groups["widgets"]);

            // It folds with the group it's in now.
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.HardwareSection);
            Assert.False(widgets.IsVisible);
            Assert.True(memory.IsVisible);
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.HardwareSection);

            // DASHBOARDS takes a built-in page like any group.
            Assert.True(sidebar.MoveTo(memory, sidebar.DashboardsGroup, 0));
            Assert.Equal(["memory"], Keys(sidebar.DashboardsGroup.Pages));
            Assert.Equal("dashboards", settings.Current.Sidebar.Groups["memory"]);
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.Dashboards);
            Assert.False(memory.IsVisible);
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.Dashboards);

            // Back home: nothing is kept about it.
            Assert.True(sidebar.MoveTo(memory, sidebar.HardwareGroup, 3));
            Assert.Equal(["widgets", "temperatures", "fans", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware));
            Assert.False(settings.Current.Sidebar.Groups.ContainsKey("memory"));
        });

        // The next start (and the agent's copy coming back) reads the same layout.
        var again = Ui.Run(() => new SidebarViewModel(Kit.OfflineSettings(SettingsStore.Deserialize(SettingsStore.Serialize(settings.Current))), _ => { }));
        Ui.Run(() =>
        {
            Assert.Equal(["home", "reports", "apps", "processes", "crashes", "timeline"], Keys(again.Main));
            Assert.Equal(["widgets", "temperatures", "fans", "memory", "storage", "network", "sensors"], Keys(again.Hardware));
            Assert.Equal(["overlay", "taskbar"], Keys(again.OnScreen));
        });
    }

    [Fact]
    public void The_arrow_keys_carry_a_page_across_groups()
    {
        var (sidebar, _, _) = Sidebar();
        Ui.Run(() =>
        {
            var home = sidebar.Main.Single(e => e.Key == "home");
            Assert.False(sidebar.MoveBy(home, -1)); // nothing above the top

            // Down off the end of the top group: into DASHBOARDS, then the start of HARDWARE, then on down.
            var timeline = sidebar.Main.Single(e => e.Key == "timeline");
            Assert.True(sidebar.MoveBy(timeline, +1));
            Assert.Equal(["timeline"], Keys(sidebar.DashboardsGroup.Pages));
            Assert.True(sidebar.MoveBy(timeline, +1));
            Assert.Equal(["timeline", "temperatures", "fans", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware));
            Assert.True(sidebar.MoveBy(timeline, +1));
            Assert.Equal(["temperatures", "timeline", "fans", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware));

            // Up off the start of ON SCREEN: the end of HARDWARE.
            var widgets = sidebar.OnScreen.Single(e => e.Key == "widgets");
            Assert.True(sidebar.MoveBy(widgets, -1));
            Assert.Equal("widgets", sidebar.Hardware.Last().Key);

            var taskbar = sidebar.OnScreen.Single(e => e.Key == "taskbar");
            Assert.False(sidebar.MoveBy(taskbar, +1)); // nothing below the bottom

            // A group emptied of its pages offers a place to drop one, and takes one back.
            Assert.False(sidebar.OnScreenGroup.ShowDropBox);
            foreach (var e in sidebar.OnScreen.ToList()) sidebar.MoveTo(e, sidebar.MainGroup, 0);
            Assert.True(sidebar.OnScreenGroup.ShowDropBox);
            Assert.True(sidebar.DashboardsGroup.ShowDropBox); // no dashboard yet, and nothing moved in
            Assert.True(sidebar.MoveBy(widgets, +1));
            Assert.Equal(["widgets"], Keys(sidebar.OnScreen));
            Assert.False(sidebar.OnScreenGroup.ShowDropBox);
        });
    }

    [Fact]
    public void Renaming_a_page_shows_at_once_and_clearing_the_name_brings_its_own_back()
    {
        var (sidebar, settings, _) = Sidebar();
        Ui.Run(() =>
        {
            var sensors = sidebar.Hardware.Single(e => e.Key == "sensors");
            Assert.Equal("", sensors.Name);
            Assert.Equal("All sensors", sensors.Title);

            var raised = new List<string?>();
            sensors.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            sensors.Name = "Every reading ";
            Assert.Contains(nameof(NavEntry.Title), raised);
            Assert.Equal("Every reading ", sensors.Name); // as typed: the box mustn't take a space away mid-word
            Assert.Equal("Every reading", sensors.Title);
            Assert.Equal("Every reading ", settings.Current.Sidebar.Names["sensors"]);
            Assert.Equal("All sensors", sensors.BuiltInTitle);

            sensors.Name = "   ";
            Assert.Equal("", sensors.Name);
            Assert.Equal("All sensors", sensors.Title);
            Assert.Empty(settings.Current.Sidebar.Names);

            // Headings the same, written in capitals as the sidebar does.
            var hardware = sidebar.HardwareGroup;
            Assert.Equal("HARDWARE", hardware.Heading);
            hardware.Name = "My PC";
            Assert.Equal("MY PC", hardware.Heading);
            Assert.Equal("My PC", settings.Current.Sidebar.HeadingNames["hardware"]);
            sidebar.DashboardsGroup.Name = "Boards";
            Assert.Equal("BOARDS", sidebar.DashboardsGroup.Heading);
            hardware.Name = "";
            Assert.Equal("HARDWARE", hardware.Heading);
            Assert.Equal(["dashboards"], settings.Current.Sidebar.HeadingNames.Keys);

            sidebar.MainGroup.Name = "Top"; // the top group has no heading to name
            Assert.False(sidebar.MainGroup.HasHeading);
            Assert.Equal(["dashboards"], settings.Current.Sidebar.HeadingNames.Keys);
        });
    }

    [Fact]
    public void A_hidden_heading_cannot_fold_and_its_pages_show()
    {
        var (sidebar, settings, _) = Sidebar();
        Ui.Run(() =>
        {
            Assert.All(sidebar.Groups.Where(g => g.HasHeading), g => Assert.True(g.ShowHeading));
            Assert.False(sidebar.MainGroup.ShowHeading);
            Assert.Equal("", sidebar.HiddenSummary);

            // Folded, then its heading taken away: the pages are back, with nothing left to unfold them by.
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.HardwareSection);
            Assert.All(sidebar.Hardware, e => Assert.False(e.IsVisible));
            sidebar.HardwareGroup.ShowHeading = false;
            Assert.Equal(["hardware"], settings.Current.Sidebar.HiddenHeadings);
            Assert.False(sidebar.HardwareCollapsed);
            Assert.False(sidebar.HardwareGroup.Collapsed);
            Assert.All(sidebar.Hardware, e => Assert.True(e.IsVisible));
            Assert.Equal("· 1 heading hidden", sidebar.HiddenSummary);

            // The same for DASHBOARDS, which the shell asks about for the user's own pages.
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.Dashboards);
            Assert.True(sidebar.DashboardsCollapsed);
            int told = 0;
            sidebar.Changed += () => told++;
            sidebar.DashboardsGroup.ShowHeading = false;
            Assert.False(sidebar.DashboardsCollapsed);
            Assert.True(told > 0);

            sidebar.Hardware.Single(e => e.Key == "fans").IsShown = false;
            sidebar.Hardware.Single(e => e.Key == "storage").IsShown = false;
            Assert.Equal("· 2 pages hidden, 2 headings hidden", sidebar.HiddenSummary);

            // The heading back: it is folded as it was left.
            sidebar.HardwareGroup.ShowHeading = true;
            Assert.True(sidebar.HardwareCollapsed);
            Assert.All(sidebar.Hardware, e => Assert.False(e.IsVisible));
            Assert.Equal("· 2 pages hidden, 1 heading hidden", sidebar.HiddenSummary);
        });
    }

    [Fact]
    public void Settings_from_before_the_editor_give_the_usual_sidebar()
    {
        // As 0.19 wrote them: an order, hidden pages and folded sections, and nothing about groups, names or headings.
        var old = SettingsStore.Deserialize("""
            { "SettingsVersion": 9, "Sidebar": { "Order": ["home", "reports", "apps", "crashes", "timeline", "fans", "temperatures"], "Hidden": ["storage"], "Collapsed": ["onscreen"] } }
            """);
        Assert.Empty(old.Sidebar.Groups);
        Assert.Empty(old.Sidebar.Names);
        Assert.Empty(old.Sidebar.HeadingNames);
        Assert.Empty(old.Sidebar.HiddenHeadings);

        var (sidebar, _, _) = Sidebar(old);
        Ui.Run(() =>
        {
            Assert.Equal(["home", "reports", "apps", "crashes", "timeline", "processes"], Keys(sidebar.Main));
            Assert.Equal(["fans", "temperatures", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware));
            Assert.Equal(["widgets", "overlay", "taskbar"], Keys(sidebar.OnScreen));
            Assert.Equal(["Home", "Reports", "Apps", "Crashes", "Timeline", "Processes"], sidebar.Main.Select(e => e.Title));
            Assert.Equal(["DASHBOARDS", "HARDWARE", "ON SCREEN"], sidebar.Groups.Where(g => g.HasHeading).Select(g => g.Heading));
            Assert.All(sidebar.Groups.Where(g => g.HasHeading), g => Assert.True(g.ShowHeading));
            Assert.True(sidebar.OnScreenCollapsed);
            Assert.False(sidebar.Hardware.Single(e => e.Key == "storage").IsVisible);
            Assert.Equal("· 1 page hidden", sidebar.HiddenSummary);
        });

        // A hand edit: nulls, empty names, a group that isn't one.
        var odd = SettingsStore.Deserialize("""
            { "SettingsVersion": 9, "Sidebar": { "Groups": { "memory": "nowhere", "fans": "dashboards", "taskbar": "main", "apps": null }, "Names": { "fans": "  ", "apps": null, "home": "Start" }, "HeadingNames": null, "HiddenHeadings": null } }
            """);
        Assert.Equal(["home"], odd.Sidebar.Names.Keys);
        Assert.Empty(odd.Sidebar.HeadingNames);
        Assert.Empty(odd.Sidebar.HiddenHeadings);
        (sidebar, _, _) = Sidebar(odd);
        Ui.Run(() =>
        {
            Assert.Equal(["home", "reports", "apps", "processes", "crashes", "timeline", "taskbar"], Keys(sidebar.Main));
            Assert.Equal(["temperatures", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware)); // a group that isn't one: its own
            Assert.Equal(["fans"], Keys(sidebar.DashboardsGroup.Pages));
            Assert.Equal("Start", sidebar.Main[0].Title);
        });
    }

    [Fact]
    public void A_page_the_saved_layout_does_not_know_goes_last_in_its_own_group()
    {
        // A layout saved by a version without Network or Timeline, with Memory moved to the top group.
        var start = SeedData.QuietSettings();
        start.Sidebar.Order = ["home", "memory", "reports", "apps", "crashes", "sensors", "temperatures", "fans", "storage", "widgets", "overlay", "taskbar"];
        start.Sidebar.Groups["memory"] = "main";
        var (sidebar, _, _) = Sidebar(start);
        Ui.Run(() =>
        {
            Assert.Equal(["home", "memory", "reports", "apps", "crashes", "processes", "timeline"], Keys(sidebar.Main));
            Assert.Equal(["sensors", "temperatures", "fans", "storage", "network"], Keys(sidebar.Hardware));
        });
    }

    [Fact]
    public void Reading_the_same_settings_again_leaves_the_rows_alone()
    {
        // Every change comes back from the agent a moment later: a row rebuilt then would take the focus from its name box.
        var (sidebar, settings, _) = Sidebar();
        Ui.Run(() =>
        {
            sidebar.MoveTo(sidebar.Hardware.Single(e => e.Key == "memory"), sidebar.MainGroup, 1);
            int moves = 0;
            foreach (var group in sidebar.Groups) group.Pages.CollectionChanged += (_, _) => moves++;
            sidebar.Refresh();
            Assert.Equal(0, moves);

            // Changed elsewhere: followed.
            settings.Update(s =>
            {
                s.Sidebar.Groups.Clear();
                s.Sidebar.Order = [];
                s.Sidebar.Names["memory"] = "RAM";
            });
            sidebar.Refresh();
            Assert.Equal(["home", "reports", "apps", "processes", "crashes", "timeline"], Keys(sidebar.Main));
            Assert.Equal(["temperatures", "fans", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware));
            Assert.Equal("RAM", sidebar.Hardware[2].Title);
        });
    }

    [Fact]
    public void The_sidebar_editor_opens_from_settings_with_a_row_for_every_page()
    {
        var settings = Kit.OfflineSettings();
        Ui.Run(() =>
        {
            var sidebar = new SidebarViewModel(settings, _ => { });
            var vm = new SettingsViewModel(settings, new AgentClient(Ui.Dispatcher), new ReportService(settings)) { Sidebar = sidebar };
            var window = Host(new Views.SettingsView { DataContext = vm });
            try
            {
                static string NameOf(DependencyObject o) => System.Windows.Automation.AutomationProperties.GetName(o);
                var panel = Visuals.Descendants<System.Windows.Controls.Border>(window).Single(b => b.Name == "EditorPanel");
                Assert.False(panel.IsVisible);
                Assert.DoesNotContain(Visuals.Descendants<System.Windows.Controls.TextBlock>(window), t => t.Text == "SIDEBAR"); // the old card is gone

                var edit = Visuals.Descendants<System.Windows.Controls.Button>(window).Single(b => NameOf(b) == "Edit the sidebar");
                edit.Command.Execute(null);
                Ui.Pump(400);
                Assert.True(sidebar.IsEditing);
                Assert.True(panel.IsVisible);

                // A grip, a name box and a switch for each page; a name box and a switch for each heading.
                var grips = Visuals.Descendants<System.Windows.Controls.Primitives.Thumb>(panel).Where(t => t.DataContext is NavEntry).ToList();
                Assert.Equal(15, grips.Count);
                Assert.All(grips, g => Assert.True(g.Focusable && g.ActualHeight > 0));
                Assert.Contains(grips, g => NameOf(g) == "Move Memory: drag it, or use the arrow keys");
                var boxes = Visuals.Descendants<System.Windows.Controls.TextBox>(panel).Where(b => b.IsVisible).ToList();
                Assert.Equal(15 + 3, boxes.Count);
                var switches = Visuals.Descendants<System.Windows.Controls.CheckBox>(panel).Where(c => c.IsVisible).Select(NameOf).ToList();
                Assert.Contains("Show Memory", switches);
                Assert.Contains("Show the Hardware heading", switches);
                Assert.Equal(15 + 3, switches.Count);
                Assert.False(Visuals.Descendants<System.Windows.Controls.CheckBox>(panel).Single(c => NameOf(c) == "Show Home").IsEnabled);

                // Typing over a name renames the page with each letter.
                var memory = sidebar.Hardware.Single(e => e.Key == "memory");
                var box = boxes.Single(b => NameOf(b) == "Name of Memory");
                Assert.Equal("Memory", box.Tag);
                box.Text = "RAM";
                Assert.Equal("RAM", memory.Title);
                box.Text = "";
                Assert.Equal("Memory", memory.Title);
                boxes.Single(b => NameOf(b) == "Name of the Hardware heading").Text = "PARTS";
                Assert.Equal("PARTS", sidebar.HardwareGroup.Heading);

                // The arrow keys on a grip move its page, and the row built in its new place has a grip again.
                var grip = grips.Single(g => g.DataContext == memory);
                var source = PresentationSource.FromVisual(grip)!;
                grip.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Up)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Assert.Equal(["temperatures", "memory", "fans", "storage", "network", "sensors"], Keys(sidebar.Hardware));
                Ui.Pump(50);
                grip = Visuals.Descendants<System.Windows.Controls.Primitives.Thumb>(panel).Single(g => g.DataContext == memory);

                // Dragged: a line marks where it will land, and letting go puts it there. (The pointer is far below this
                // off-screen window, so the nearest place is the very end of the list.)
                var line = Visuals.Descendants<System.Windows.Shapes.Rectangle>(panel).Single(r => r.Name == "DropLine");
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, 40));
                Assert.True(line.IsVisible);
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 40, false));
                Assert.False(line.IsVisible);
                Assert.Equal(["widgets", "overlay", "taskbar", "memory"], Keys(sidebar.OnScreen));

                // A drag given up changes nothing.
                Ui.Pump(50);
                grip = Visuals.Descendants<System.Windows.Controls.Primitives.Thumb>(panel).Single(g => g.DataContext == memory);
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0));
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, -400));
                grip.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, -400, true));
                Assert.Equal(["widgets", "overlay", "taskbar", "memory"], Keys(sidebar.OnScreen));

                // An emptied group shows its box.
                foreach (var e in sidebar.OnScreen.ToList()) sidebar.MoveTo(e, sidebar.MainGroup, 0);
                Ui.Pump(50);
                Assert.Equal(2, Visuals.Descendants<System.Windows.Controls.TextBlock>(panel).Count(t => t.Text == "Drag a page here" && t.IsVisible)); // and DASHBOARDS, with no dashboard yet

                // Settings says what is hidden, and Esc closes the editor.
                sidebar.OnScreenGroup.ShowHeading = false;
                Assert.Contains(Visuals.Descendants<System.Windows.Controls.TextBlock>(window), t => t.Text == "· 1 heading hidden" && t.IsVisible);
                panel.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Assert.False(sidebar.IsEditing);
                Ui.Pump(500);
                Assert.False(panel.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_saved_order_is_read_back_and_new_pages_go_last()
    {
        var start = SeedData.QuietSettings();
        start.Sidebar.Order = ["storage", "memory", "crashes", "home"]; // an older list without the rest
        var (sidebar, _, _) = Sidebar(start);
        Ui.Run(() =>
        {
            Assert.Equal(["crashes", "home", "reports", "apps", "processes", "timeline"], Keys(sidebar.Main));
            Assert.Equal(["storage", "memory", "temperatures", "fans", "network", "sensors"], Keys(sidebar.Hardware));
        });
    }

    [Fact]
    public void Hidden_pages_leave_the_sidebar_but_show_while_open()
    {
        var (sidebar, settings, _) = Sidebar();
        Ui.Run(() =>
        {
            var storage = sidebar.Hardware.Single(e => e.Key == "storage");
            storage.IsShown = false;
            Assert.False(storage.IsVisible);
            Assert.Equal(["storage"], settings.Current.Sidebar.Hidden);

            // Reached another way (a link): shown while it's the page you're on, gone again after.
            sidebar.Select("storage");
            Assert.True(storage.IsVisible);
            Assert.True(storage.IsSelected);
            sidebar.Select("home");
            Assert.False(storage.IsVisible);

            storage.IsShown = true;
            Assert.True(storage.IsVisible);
            Assert.Empty(settings.Current.Sidebar.Hidden);
        });
    }

    [Fact]
    public void Home_cannot_be_hidden()
    {
        var (sidebar, settings, _) = Sidebar();
        Ui.Run(() =>
        {
            var home = sidebar.Main.Single(e => e.Key == "home");
            Assert.False(home.CanHide);
            Assert.All(sidebar.Main.Concat(sidebar.Hardware).Concat(sidebar.OnScreen).Where(e => e != home), e => Assert.True(e.CanHide));
            home.IsShown = false;
            Assert.True(home.IsShown && home.IsVisible);
            Assert.Empty(settings.Current.Sidebar.Hidden);
        });
    }

    [Fact]
    public void A_folded_section_keeps_the_page_you_are_on()
    {
        var (sidebar, settings, _) = Sidebar();
        Ui.Run(() =>
        {
            sidebar.Select("memory");
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.HardwareSection);
            Assert.True(sidebar.HardwareCollapsed);
            Assert.Equal(["hardware"], settings.Current.Sidebar.Collapsed);
            Assert.Equal(["memory"], Keys(sidebar.Hardware.Where(e => e.IsVisible)));

            sidebar.Select("home");
            Assert.Empty(sidebar.Hardware.Where(e => e.IsVisible));
            Assert.All(sidebar.Main, e => Assert.True(e.IsVisible)); // the top group never folds

            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.HardwareSection);
            Assert.False(sidebar.HardwareCollapsed);
            Assert.All(sidebar.Hardware, e => Assert.True(e.IsVisible));
        });
    }

    [Fact]
    public void On_screen_pages_have_their_own_group_that_folds()
    {
        var (sidebar, settings, opened) = Sidebar();
        Ui.Run(() =>
        {
            Assert.Equal(["widgets", "overlay", "taskbar"], Keys(sidebar.OnScreen));
            sidebar.OnScreen.Single(e => e.Key == "taskbar").IsSelected = true;
            Assert.Equal(["taskbar"], opened);

            sidebar.Select("overlay");
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.OnScreenSection);
            Assert.True(sidebar.OnScreenCollapsed);
            Assert.False(sidebar.HardwareCollapsed);
            Assert.Equal(["overlay"], Keys(sidebar.OnScreen.Where(e => e.IsVisible)));
            Assert.All(sidebar.Hardware, e => Assert.True(e.IsVisible));

            var taskbar = sidebar.OnScreen.Single(e => e.Key == "taskbar");
            sidebar.MoveBy(taskbar, -1);
            Assert.Equal(["widgets", "taskbar", "overlay"], Keys(sidebar.OnScreen));
            Assert.Equal(["widgets", "taskbar", "overlay"], settings.Current.Sidebar.Order.TakeLast(3));
            sidebar.ToggleSectionCommand.Execute(SidebarViewModel.OnScreenSection);
        });
    }

    [Fact]
    public void Picking_a_page_in_the_sidebar_opens_it()
    {
        var (sidebar, _, opened) = Sidebar();
        Ui.Run(() =>
        {
            sidebar.Hardware.Single(e => e.Key == "fans").IsSelected = true;
            Assert.Equal(["fans"], opened);
            sidebar.Select("fans"); // the page changing ticks it without opening it again
            Assert.Equal(["fans"], opened);
        });
    }

    [Fact]
    public void Sidebar_settings_are_repaired_on_load()
    {
        var s = SettingsStore.Deserialize("""{ "SettingsVersion": 8, "Sidebar": null }""");
        Assert.NotNull(s.Sidebar);
        Assert.Empty(s.Sidebar.Order);

        s = SettingsStore.Deserialize("""{ "SettingsVersion": 8, "Sidebar": { "Order": null, "Hidden": ["home", "fans", "taskbar"], "Collapsed": null } }""");
        Assert.Equal(["fans", "taskbar"], s.Sidebar.Hidden);
        Assert.Empty(s.Sidebar.Order);
        Assert.Empty(s.Sidebar.Collapsed);
        Assert.Equal("mono", s.Accent);
    }

    // ── Dashboard presets ────────────────────────────────────────────────

    private static PresetPickerViewModel Picker(params string[] skip)
    {
        var (_, live) = Kit.Greeted(hello: Pc.Hello(skip));
        return Ui.Run(() => new PresetPickerViewModel(live, _ => { }));
    }

    private static List<string> Offered(PresetPickerViewModel picker) => Ui.Run(() =>
    {
        picker.OpenCommand.Execute(null);
        return picker.Cards.Select(c => c.Name).ToList();
    });

    [Fact]
    public void Every_preset_is_offered_on_a_PC_with_every_reading()
    {
        var picker = Picker();
        Assert.Equal([.. DashboardPresets.All.Select(p => p.Name), "Blank"], Offered(picker));
        Ui.Run(() =>
        {
            foreach (var preset in DashboardPresets.All)
                Assert.Equal(preset.Tiles.Count, DashboardPresets.TilesFor(preset, picker.Available).Count);
            Assert.True(picker.IsOpen);
            Assert.Equal("", picker.Cards[^1].Shows);
            Assert.Contains("GPU temperature", picker.Cards.Single(c => c.Name == "Gaming").Shows);
        });
    }

    [Fact]
    public void Presets_about_hardware_this_PC_cannot_read_are_not_offered()
    {
        // No graphics card sensors and no board chip (so no fans).
        var picker = Picker("Test GPU", "Test Board");
        var names = Offered(picker);
        Assert.DoesNotContain("Gaming", names);
        Assert.DoesNotContain("Cooling", names);
        Assert.Contains("Temperatures", names);
        Assert.Contains("Workload", names);
        Assert.Contains("My day", names);
        Assert.Equal("Blank", names[^1]);
    }

    [Fact]
    public void A_preset_leaves_out_the_tiles_this_PC_cannot_fill()
    {
        var picker = Picker("Test GPU", "Test Board");
        Ui.Run(() =>
        {
            var temps = DashboardPresets.TilesFor(DashboardPresets.All.Single(p => p.Name == "Temperatures"), picker.Available);
            Assert.Contains(temps, t => t.Kind == "cpu-gauge");
            Assert.Contains(temps, t => t.Kind == "temp-chart"); // the CPU's line is enough
            Assert.Contains(temps, t => t.Kind == "drives");
            Assert.DoesNotContain(temps, t => t.Kind == "gpu-gauge");
            Assert.DoesNotContain(temps, t => t.Sensor is { } s && s.Contains("gpu", StringComparison.OrdinalIgnoreCase));

            var workload = DashboardPresets.TilesFor(DashboardPresets.All.Single(p => p.Name == "Workload"), picker.Available);
            Assert.Contains(workload, t => t.Sensor == "key:" + KeySensors.CpuLoad);
            Assert.Contains(workload, t => t.Sensor == "key:" + KeySensors.RamLoad);
            Assert.DoesNotContain(workload, t => t.Sensor == "key:" + KeySensors.GpuLoad);

            // The card says what the page will hold here.
            picker.OpenCommand.Execute(null);
            Assert.DoesNotContain("GPU", picker.Cards.Single(c => c.Name == "Workload").Shows);
        });
    }

    [Fact]
    public void Before_the_readings_arrive_every_preset_is_offered_whole()
    {
        var live = Kit.Live(Kit.OfflineSettings());
        var picker = Ui.Run(() => new PresetPickerViewModel(live, _ => { }));
        Assert.Equal([.. DashboardPresets.All.Select(p => p.Name), "Blank"], Offered(picker));
    }

    [Fact]
    public void Picking_a_card_closes_the_picker_and_makes_that_dashboard()
    {
        var (_, live) = Kit.Greeted();
        var made = new List<DashboardPreset>();
        var picker = Ui.Run(() => new PresetPickerViewModel(live, made.Add));
        Ui.Run(() =>
        {
            picker.OpenCommand.Execute(null);
            picker.PickCommand.Execute(picker.Cards.Single(c => c.Name == "Cooling"));
            Assert.False(picker.IsOpen);
            Assert.Equal(["Cooling"], made.Select(p => p.Name));

            picker.OpenCommand.Execute(null);
            picker.CloseCommand.Execute(null);
            Assert.False(picker.IsOpen);
            Assert.Single(made);
        });
    }

    [Fact]
    public void Every_preset_tile_is_in_the_catalog_and_every_preset_fits_the_grid()
    {
        var kinds = TileCatalog.Groups.SelectMany(g => g.Tiles).ToList();
        foreach (var preset in DashboardPresets.All)
        {
            Assert.All(preset.Tiles, t => Assert.Contains(kinds, k => k.Kind == t.Kind && (t.Kind != "sensor" || k.Sensor == t.Sensor)));
            Assert.All(preset.Tiles, t => Assert.InRange(t.W ?? 1, 1, TileConfig.Columns));
            Assert.All(preset.Needs, n => Assert.Contains(preset.Tiles, t => t.Kind == n.Kind && t.Sensor == n.Sensor));
            Assert.Equal(preset.Tiles.Count, preset.Tiles.Distinct().Count());
        }
        Assert.Equal(DashboardPresets.All.Count, DashboardPresets.All.Select(p => p.Name).Distinct().Count());
    }

    // ── Accent colours ───────────────────────────────────────────────────

    [Fact]
    public void Accents_have_a_shade_for_each_theme()
    {
        Assert.Null(Accents.ColorFor(Accents.Mono, light: false));
        Assert.Null(Accents.ColorFor("no-such-colour", light: true));
        Assert.Equal(Accents.Mono, Accents.Normalize(null));
        Assert.Equal(Accents.Windows, Accents.Normalize(Accents.Windows));
        foreach (var a in Accents.All.Skip(1))
        {
            Assert.Equal(a.OnDark, Accents.ColorFor(a.Key, light: false));
            Assert.Equal(a.OnLight, Accents.ColorFor(a.Key, light: true));
            Assert.NotEqual(a.OnDark, a.OnLight);
        }
        Assert.Equal(Accents.All.Count, Accents.All.Select(a => a.Key).Distinct().Count());
    }

    [Fact]
    public void Text_on_an_accent_is_black_on_bright_and_white_on_deep()
    {
        Assert.Equal(Colors.Black, Accents.OnAccent(Colors.White));
        Assert.Equal(Colors.Black, Accents.OnAccent(Accents.All.Single(a => a.Key == "yellow").OnDark));
        Assert.Equal(Colors.White, Accents.OnAccent(Colors.Black));
        Assert.Equal(Colors.White, Accents.OnAccent(Accents.All.Single(a => a.Key == "blue").OnLight));
    }

    [Fact]
    public void Picking_an_accent_recolours_the_app_and_mono_brings_back_the_theme()
    {
        var blue = Accents.All.Single(a => a.Key == "blue");
        try
        {
            Ui.Run(() =>
            {
                ThemeManager.Apply(ThemeManager.Dark, "blue");
                var res = Application.Current.Resources;
                Assert.Equal(blue.OnDark, (Color)res["AccentColor"]);
                Assert.Equal(blue.OnDark, ((SolidColorBrush)res["AccentBrush"]).Color);
                Assert.Equal(blue.OnDark, ((SolidColorBrush)res["AccentSoftBrush"]).Color);
                Assert.True(((SolidColorBrush)res["AccentSoftBrush"]).Opacity < 1); // the soft shade stays soft
                Assert.Equal(Accents.OnAccent(blue.OnDark), ((SolidColorBrush)res["OnAccentBrush"]).Color);
                Assert.Equal(blue.OnDark, (Color)res["SystemAccentColor"]);

                ThemeManager.Apply(ThemeManager.Light, "blue");
                Assert.Equal(blue.OnLight, (Color)res["AccentColor"]);

                ThemeManager.Apply(ThemeManager.Dark, Accents.Mono);
                Assert.Equal(Colors.White, (Color)res["AccentColor"]);
                Assert.Equal(Colors.White, ((SolidColorBrush)res["AccentBrush"]).Color);
            });
        }
        finally
        {
            Ui.Run(() => ThemeManager.Apply(ThemeManager.Dark, Accents.Mono));
        }
    }

    /// <summary>A left click as the mouse sends it (automation's Select skips the path a click takes).</summary>
    private static void Click(UIElement element) =>
        element.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
            System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });

    private static Window Host(UIElement content)
    {
        var window = new Window { Content = content, Left = -32000, Top = -32000, Width = 1200, Height = 900, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        Ui.Pump(200);
        return window;
    }

    [Fact]
    public void Clicking_an_accent_swatch_picks_it()
    {
        // The swatches once couldn't be clicked at all: a list item is only picked by a click once it takes focus.
        var settings = Kit.OfflineSettings();
        Ui.Run(() =>
        {
            var vm = new SettingsViewModel(settings, new AgentClient(Ui.Dispatcher), new ReportService(settings));
            var window = Host(new Views.SettingsView { DataContext = vm });
            try
            {
                var list = Visuals.Descendants<System.Windows.Controls.ListBox>(window)
                    .Single(l => System.Windows.Automation.AutomationProperties.GetName(l) == "Accent colour");
                var blue = (System.Windows.Controls.ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(vm.AccentOptions.Single(o => o.Key == "blue"));
                Click(blue);
                Assert.Equal("blue", settings.Current.Accent);
                Assert.True(blue.IsSelected);
            }
            finally
            {
                window.Close();
                ThemeManager.Apply(ThemeManager.Dark, Accents.Mono);
            }
        });
    }

    [Fact]
    public void Clicking_a_filter_chip_picks_it()
    {
        Ui.Run(() =>
        {
            var list = new System.Windows.Controls.ListBox
            {
                Style = (Style)Application.Current.FindResource("ChipList"),
                ItemsSource = new[] { "All", "Games", "Browsing" },
                SelectedIndex = 0,
            };
            var window = Host(list);
            try
            {
                Click((UIElement)list.ItemContainerGenerator.ContainerFromIndex(1));
                Assert.Equal("Games", list.SelectedItem);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Settings_offers_every_accent_and_saves_the_pick()
    {
        var settings = Kit.OfflineSettings();
        var vm = Ui.Run(() => new SettingsViewModel(settings, new AgentClient(Ui.Dispatcher), new ReportService(settings)));
        Ui.Run(() =>
        {
            var keys = vm.AccentOptions.Select(o => o.Key).ToList();
            Assert.Equal(Accents.All.Select(a => a.Key), keys.Take(Accents.All.Count));
            Assert.Equal(Accents.Mono, vm.Accent);
            vm.Accent = "green";
            Assert.Equal("green", settings.Current.Accent);
            Assert.Equal("green", vm.Accent);
        });
    }
}
