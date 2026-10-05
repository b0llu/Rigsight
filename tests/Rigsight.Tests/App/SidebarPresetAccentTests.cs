using System.Windows;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>The sidebar (order, hidden pages, folded sections), dashboard presets and accent colours.</summary>
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
            Assert.Equal(["home", "reports", "apps", "crashes", "timeline"], Keys(sidebar.Main));
            Assert.Equal(["temperatures", "fans", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware));
            Assert.Equal(["widgets", "overlay", "taskbar"], Keys(sidebar.OnScreen));
            Assert.All(sidebar.Main.Concat(sidebar.Hardware).Concat(sidebar.OnScreen), e => Assert.True(e.IsVisible));
            Assert.False(sidebar.DashboardsCollapsed);
            Assert.False(sidebar.HardwareCollapsed);
        });
    }

    [Fact]
    public void Moving_a_page_stays_in_its_group_and_is_saved()
    {
        var (sidebar, settings, _) = Sidebar();
        Ui.Run(() =>
        {
            var fans = sidebar.Hardware.Single(e => e.Key == "fans");
            sidebar.MoveUpCommand.Execute(fans);
            sidebar.MoveUpCommand.Execute(fans); // already first: stays
            Assert.Equal(["fans", "temperatures", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware));

            var apps = sidebar.Main.Single(e => e.Key == "apps");
            sidebar.MoveDownCommand.Execute(apps);
            sidebar.MoveDownCommand.Execute(apps);
            sidebar.MoveDownCommand.Execute(apps); // already last of its group: doesn't jump into HARDWARE
            Assert.Equal(["home", "reports", "crashes", "timeline", "apps"], Keys(sidebar.Main));
            Assert.Equal(["fans", "temperatures", "memory", "storage", "network", "sensors"], Keys(sidebar.Hardware));

            Assert.Equal(["home", "reports", "crashes", "timeline", "apps", "fans", "temperatures", "memory", "storage", "network", "sensors", "widgets", "overlay", "taskbar"],
                settings.Current.Sidebar.Order);
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
            Assert.Equal(["crashes", "home", "reports", "apps", "timeline"], Keys(sidebar.Main));
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
            sidebar.MoveUpCommand.Execute(taskbar);
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
