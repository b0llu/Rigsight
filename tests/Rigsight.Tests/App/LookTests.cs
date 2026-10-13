using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rigsight.Core.Apps;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The app's look as a whole: the themes with a palette of their own, a picked accent taking over every colour that
/// says which part something is, readings following it or not by the setting, warnings never following it, and
/// Settings as tabs.
/// </summary>
[Collection("UI")]
public sealed class LookTests
{
    private static Color Res(string key) => (Color)Application.Current.FindResource(key);
    private static SolidColorBrush BrushOf(string key) => (SolidColorBrush)Application.Current.FindResource(key);
    private static readonly Color Pink = Accents.All.Single(a => a.Key == "pink").OnDark;

    private static void Reset() => Ui.Run(() => ThemeManager.Apply(ThemeManager.Dark, Accents.Mono));

    // ── Themes ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ThemeManager.Midnight, "Midnight")]
    [InlineData(ThemeManager.Forest, "Forest")]
    [InlineData(ThemeManager.Ember, "Ember")]
    public void A_theme_of_its_own_brings_its_background_and_its_accent_and_keeps_the_parts_colours(string theme, string file)
    {
        try
        {
            Ui.Run(() =>
            {
                ThemeManager.Apply(ThemeManager.Dark);
                var (darkBg, darkCpu, darkGpu, darkHot) = (Res("BgColor"), Res("CpuColor"), Res("GpuColor"), Res("HotColor"));

                ThemeManager.Apply(theme);
                Assert.False(ThemeManager.IsLight);
                Assert.Equal(ThemeMode.Dark, Application.Current.ThemeMode);
                Assert.Single(Application.Current.Resources.MergedDictionaries, d => d.Source?.OriginalString.EndsWith($"Themes/{file}.xaml") == true);
                Assert.DoesNotContain(Application.Current.Resources.MergedDictionaries, d => d.Source?.OriginalString.EndsWith("Themes/Dark.xaml") == true);
                Assert.NotEqual(darkBg, Res("BgColor"));

                // Its own accent: a colour, and in the Fluent controls' keys too (the palette file writes those in white).
                var own = Res("AccentColor");
                Assert.NotEqual(Colors.White, own);
                Assert.Equal(own, ThemeManager.OwnAccent);
                Assert.Equal(own, Res("SystemAccentColor"));
                Assert.Equal(own, BrushOf("RamBrush").Color);
                Assert.Equal(Accents.OnAccent(own), BrushOf("OnAccentBrush").Color);

                // No accent was picked, so nothing is taken over: the parts and the warnings are the dark theme's.
                Assert.False(ThemeManager.AccentTakesOver);
                Assert.Equal((darkCpu, darkGpu, darkHot), (Res("CpuColor"), Res("GpuColor"), Res("HotColor")));
                Assert.Equal(Res("HoverColor"), BrushOf("SegmentOnBrush").Color);
                Assert.Equal(Res("FaintColor"), BrushOf("ListBarBrush").Color);
            });
        }
        finally { Reset(); }
    }

    [Fact]
    public void The_dark_theme_s_own_accent_is_white()
    {
        try
        {
            Ui.Run(() =>
            {
                ThemeManager.Apply(ThemeManager.Dark);
                Assert.Equal(Colors.White, ThemeManager.OwnAccent);
                ThemeManager.Apply(ThemeManager.Dark, "pink");
                Assert.Equal(Colors.White, ThemeManager.OwnAccent); // whatever is picked
                ThemeManager.Apply(ThemeManager.Light);
                Assert.Equal(Colors.Black, ThemeManager.OwnAccent);
            });
        }
        finally { Reset(); }
    }

    // ── A picked accent ──────────────────────────────────────────────────

    [Fact]
    public void A_picked_accent_takes_over_every_part_s_colour_and_the_theme_s_own_gives_them_back()
    {
        try
        {
            Ui.Run(() =>
            {
                ThemeManager.Apply(ThemeManager.Dark);
                string[] parts = ["CpuColor", "GpuColor", "PurpleColor", "PinkColor", "SkyColor"];
                var before = parts.ToDictionary(k => k, Res);

                ThemeManager.Apply(ThemeManager.Dark, "pink");
                Assert.True(ThemeManager.AccentTakesOver);
                Assert.Equal(Pink, Res("CpuColor"));
                Assert.Equal(Pink, Res("SkyColor"));
                // Lines on one chart still differ: four shades, none the colour it was.
                var shades = new[] { Res("CpuColor"), Res("GpuColor"), Res("PurpleColor"), Res("PinkColor") };
                Assert.Equal(4, shades.Distinct().Count());
                Assert.DoesNotContain(before["GpuColor"], shades);
                Assert.DoesNotContain(before["PurpleColor"], shades);
                // The brushes follow their colours, the soft ones still soft.
                Assert.Equal(Res("GpuColor"), BrushOf("GpuBrush").Color);
                Assert.Equal(Pink, BrushOf("CpuSoftBrush").Color);
                Assert.True(BrushOf("CpuSoftBrush").Opacity < 1);
                // Memory's bars, the Apps list's bars and the picked segment.
                Assert.Equal(Pink, BrushOf("RamBrush").Color);
                Assert.Equal(Pink, BrushOf("ListBarBrush").Color);
                Assert.Equal(Pink, BrushOf("SegmentOnBrush").Color);
                Assert.Equal(Accents.OnAccent(Pink), BrushOf("SegmentOnTextBrush").Color);

                ThemeManager.Apply(ThemeManager.Dark, Accents.Mono);
                Assert.False(ThemeManager.AccentTakesOver);
                Assert.All(parts, k => Assert.Equal(before[k], Res(k)));
                Assert.Equal(Colors.White, BrushOf("RamBrush").Color);
                Assert.Equal(Res("HoverColor"), BrushOf("SegmentOnBrush").Color);
                Assert.Equal(Res("TextColor"), BrushOf("SegmentOnTextBrush").Color);
                Assert.Equal(Res("FaintColor"), BrushOf("ListBarBrush").Color);
            });
        }
        finally { Reset(); }
    }

    [Fact]
    public void Warnings_keep_amber_orange_and_red_whatever_accent_is_picked()
    {
        try
        {
            Ui.Run(() =>
            {
                ThemeManager.Apply(ThemeManager.Dark);
                var (warm, orange, hot) = (Res("WarmColor"), Res("OrangeColor"), Res("HotColor"));
                foreach (bool keep in new[] { true, false })
                {
                    ThemeManager.Apply(ThemeManager.Dark, "pink", keepStatus: keep);
                    Assert.Equal((warm, orange, hot), (Res("WarmColor"), Res("OrangeColor"), Res("HotColor")));
                    Assert.Equal(hot, BrushOf("HotBrush").Color);
                    Assert.Equal(warm, BrushOf("WarmSoftBrush").Color);
                    // What the charts draw a crash's mark and a gap in: the same.
                    Assert.Equal((warm, orange, hot), (Controls.ChartPaint.Warm.Color, Controls.ChartPaint.Orange.Color, Controls.ChartPaint.Hot.Color));
                }
            });
        }
        finally { Reset(); }
    }

    [Fact]
    public void Readings_and_all_is_well_follow_the_accent_only_if_the_setting_says_so()
    {
        try
        {
            Ui.Run(() =>
            {
                var temp = new Converters.TempToBrushConverter();
                Color At(double c) => ((SolidColorBrush)temp.Convert(c, typeof(Brush), null, CultureInfo.InvariantCulture)).Color;
                ThemeManager.Apply(ThemeManager.Dark);
                var (cool, good, warm, hot) = (Res("CoolColor"), Res("GoodColor"), Res("WarmColor"), Res("HotColor"));
                Assert.Equal((cool, good, warm, hot), (At(30), At(60), At(80), At(95)));
                Assert.Null(ThemeManager.ReadingAccent);

                // Kept: a picked accent leaves blue, green, amber and red on readings, and green on "all is well".
                ThemeManager.Apply(ThemeManager.Dark, "pink", keepStatus: true);
                Assert.Null(ThemeManager.ReadingAccent);
                Assert.Equal((cool, good, warm, hot), (At(30), At(60), At(80), At(95)));
                Assert.Equal(good, Res("GoodColor"));
                Assert.Equal(good, BrushOf("GoodSoftBrush").Color);

                // Following: four shades of the accent by how the reading is doing, and green is the accent.
                ThemeManager.Apply(ThemeManager.Dark, "pink", keepStatus: false);
                Assert.Equal(Pink, ThemeManager.ReadingAccent);
                var read = new[] { At(30), At(60), At(80), At(95) };
                Assert.Equal(Pink, read[1]);
                Assert.Equal(4, read.Distinct().Count());
                Assert.DoesNotContain(warm, read);
                Assert.DoesNotContain(hot, read);
                Assert.Equal(Pink, Res("GoodColor"));
                Assert.Equal(Pink, BrushOf("GoodBrush").Color);
                // A warning's red is still red, beside a hot reading that isn't.
                Assert.Equal(hot, Controls.ChartPaint.Hot.Color);
                Assert.NotEqual(hot, Controls.ChartPaint.ReadHot.Color);

                // Changing only the setting is a change: what caches colours hears of it.
                int version = ThemeManager.Version;
                ThemeManager.Apply(ThemeManager.Dark, "pink", keepStatus: true);
                Assert.Equal(version + 1, ThemeManager.Version);
                Assert.Equal(good, At(60));
            });
        }
        finally { Reset(); }
    }

    [Fact]
    public void Shades_are_mixed_towards_the_text_or_the_background()
    {
        Assert.Equal(Colors.Red, Accents.Mix(Colors.Red, Colors.Blue, 1));
        Assert.Equal(Colors.Blue, Accents.Mix(Colors.Red, Colors.Blue, 0));
        Assert.Equal(Color.FromRgb(128, 0, 128), Accents.Mix(Colors.Red, Colors.Blue, 0.5));
        var (cool, good, warm, hot) = Accents.ReadingShades(Pink, Colors.White, Colors.Black);
        Assert.Equal(Pink, good);
        Assert.Equal(4, new[] { cool, good, warm, hot }.Distinct().Count());
    }

    // ── App categories ───────────────────────────────────────────────────

    [Fact]
    public void Categories_keep_their_colours_until_an_accent_is_picked_then_are_its_shades()
    {
        var all = Enum.GetValues<AppCategory>();
        try
        {
            Ui.Run(() =>
            {
                ThemeManager.Apply(ThemeManager.Dark);
                Assert.All(all, c => Assert.Equal(AppCatalog.Color(c), ThemeManager.CategoryHex(c)));

                ThemeManager.Apply(ThemeManager.Dark, "pink");
                var coloured = all.Where(c => c is not (AppCategory.System or AppCategory.Other)).ToList();
                Assert.All(coloured, c => Assert.NotEqual(AppCatalog.Color(c), ThemeManager.CategoryHex(c)));
                Assert.Equal(coloured.Count, coloured.Select(ThemeManager.CategoryHex).Distinct().Count());
                Assert.All(coloured, c => Assert.Matches("^#[0-9A-F]{6}$", ThemeManager.CategoryHex(c)));
                Assert.Equal($"#{Pink.R:X2}{Pink.G:X2}{Pink.B:X2}", ThemeManager.CategoryHex(AppCategory.Browser));
                // The two greys stay grey.
                Assert.Equal(AppCatalog.Color(AppCategory.System), ThemeManager.CategoryHex(AppCategory.System));
                Assert.Equal(AppCatalog.Color(AppCategory.Other), ThemeManager.CategoryHex(AppCategory.Other));

                // Another accent: other shades (nothing kept from the last one).
                string pinkGame = ThemeManager.CategoryHex(AppCategory.Game);
                ThemeManager.Apply(ThemeManager.Dark, "blue");
                Assert.NotEqual(pinkGame, ThemeManager.CategoryHex(AppCategory.Game));
            });
        }
        finally { Reset(); }
    }

    [Fact]
    public void A_category_s_brush_follows_the_accent_and_a_plain_list_s_bars_are_all_the_accent()
    {
        try
        {
            Ui.Run(() =>
            {
                var converter = new Converters.CategoryBrushConverter();
                SolidColorBrush Of(AppCategory c, string? parameter = null) =>
                    (SolidColorBrush)converter.Convert(c, typeof(Brush), parameter, CultureInfo.InvariantCulture);
                Color Hex(AppCategory c) => (Color)ColorConverter.ConvertFromString(ThemeManager.CategoryHex(c));

                ThemeManager.Apply(ThemeManager.Dark);
                Assert.Equal(Hex(AppCategory.Game), Of(AppCategory.Game).Color);
                Assert.Equal(Hex(AppCategory.Game), Of(AppCategory.Game, "Bar").Color); // no accent picked: the category's own
                Assert.True(Of(AppCategory.Game, "Soft").Opacity < 1);

                ThemeManager.Apply(ThemeManager.Dark, "pink");
                Assert.Equal(Hex(AppCategory.Game), Of(AppCategory.Game).Color); // not the brush kept from before
                Assert.NotEqual(Of(AppCategory.Game).Color, Of(AppCategory.Development).Color);
                Assert.Equal(Pink, Of(AppCategory.Game, "Bar").Color);
                Assert.Equal(Pink, Of(AppCategory.Development, "Bar").Color);
            });
        }
        finally { Reset(); }
    }

    // ── Settings ─────────────────────────────────────────────────────────

    [Fact]
    public void Readings_follow_a_picked_accent_unless_told_to_keep_their_colours()
    {
        Assert.False(new RigsightSettings().KeepStatusColors);
        var settings = Kit.OfflineSettings();
        Ui.Run(() =>
        {
            var vm = new SettingsViewModel(settings, new AgentClient(Ui.Dispatcher), new ReportService(settings));
            Assert.False(vm.AccentPicked); // the theme's own: nothing to say about readings
            Assert.False(vm.KeepStatusColors);
            vm.Accent = "teal";
            Assert.True(vm.AccentPicked);
            vm.KeepStatusColors = true;
            Assert.True(settings.Current.KeepStatusColors);
        });
        Reset();
    }

    [Fact]
    public void Settings_offers_every_theme_in_its_own_colours()
    {
        var settings = Kit.OfflineSettings();
        try
        {
            Ui.Run(() =>
            {
                var vm = new SettingsViewModel(settings, new AgentClient(Ui.Dispatcher), new ReportService(settings));
                Assert.Equal(["dark", "grey", "midnight", "forest", "ember", "light", "system"], vm.ThemeOptions.Select(o => o.Key));
                Assert.Equal(vm.ThemeOptions.Count, vm.ThemeOptions.Select(o => o.Name).Distinct().Count());
                Color Of(Brush b) => ((SolidColorBrush)b).Color;
                Assert.Equal(Colors.Black, Of(vm.ThemeOptions[0].Bg));
                Assert.Equal(Colors.White, Of(vm.ThemeOptions.Single(o => o.Key == "light").Bg));
                var midnight = vm.ThemeOptions.Single(o => o.Key == "midnight");
                Assert.NotEqual(Colors.White, Of(midnight.Accent));
                Assert.NotEqual(Of(midnight.Bg), Of(midnight.Card));
                Assert.Same(vm.ThemeOptions, vm.ThemeOptions); // one list: a new one would drop the picked card's ring

                // Picking one saves it, and the first accent swatch becomes that theme's own accent.
                vm.Theme = "midnight";
                Assert.Equal("midnight", settings.Current.Theme);
                ThemeManager.Apply(settings.Current.Theme, settings.Current.Accent, settings.Current.KeepStatusColors);
                Assert.Equal(Of(midnight.Accent), Of(vm.AccentOptions[0].Brush));
                Assert.Equal(Accents.Mono, vm.AccentOptions[0].Key);
            });
        }
        finally { Reset(); }
    }

    [Fact]
    public void Settings_shows_one_tab_at_a_time()
    {
        var settings = Kit.OfflineSettings();
        Ui.Run(() =>
        {
            var vm = new SettingsViewModel(settings, new AgentClient(Ui.Dispatcher), new ReportService(settings));
            Assert.Equal("general", vm.Tab);
            bool[] Flags() => [vm.IsGeneral, vm.IsAppearance, vm.IsTracking, vm.IsSensors, vm.IsNotifications, vm.IsHistory, vm.IsAbout];
            for (int i = 0; i < 7; i++)
            {
                vm.Tab = SettingsViewModel.Tabs[i];
                Assert.Equal(Enumerable.Range(0, 7).Select(n => n == i), Flags());
            }
            // Anything else is not a tab: the page stays where it is.
            vm.Tab = "privacy";
            Assert.Equal("about", vm.Tab);
            vm.Tab = null!;
            Assert.Equal("about", vm.Tab);
            // "Admin rights" is a tab on a standard account only, and leaving such an account leaves the tab.
            vm.Tab = "admin";
            Assert.Equal("about", vm.Tab);
            Assert.False(vm.IsAdminRights);
            vm.StandardAccount = true;
            vm.Tab = "admin";
            Assert.True(vm.IsAdminRights);
            Assert.DoesNotContain(true, Flags());
            vm.StandardAccount = false;
            Assert.Equal("general", vm.Tab);
        });
    }

    private static Window Host(UIElement content)
    {
        var window = new Window { Content = content, Left = -32000, Top = -32000, Width = 1200, Height = 900, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        Ui.Pump(200);
        return window;
    }

    [Fact]
    public void The_settings_page_has_seven_tabs_and_each_shows_only_its_own_settings()
    {
        var settings = Kit.OfflineSettings();
        Ui.Run(() =>
        {
            var vm = new SettingsViewModel(settings, new AgentClient(Ui.Dispatcher), new ReportService(settings));
            var window = Host(new Views.SettingsView { DataContext = vm });
            try
            {
                bool Shown(string text) => Visuals.Descendants<TextBlock>(window).Any(t => t.Text == text && t.IsVisible);
                // "Admin rights" is the eighth, and only a standard account sees it.
                var every = Visuals.Descendants<RadioButton>(window).Where(r => r.GroupName == "SettingsTab").ToList();
                Assert.Equal(8, every.Count);
                Assert.False(every[7].IsVisible);
                var tabs = every.Where(r => r.IsVisible).ToList();
                Assert.Equal(7, tabs.Count);
                Assert.True(tabs[0].IsChecked);

                // One line that belongs to each tab, in the tabs' order. Privacy sits with Tracking, Updates with About.
                string[][] own =
                [
                    ["Temperature unit"], ["Accent colour"], ["Pause tracking", "Privacy", "Apps that are never recorded"], ["Background sensor sampling"],
                    ["Daily recap"], ["Keep history for"], ["Updates", "Update automatically", "Background agent", "Open-source notices"],
                ];
                for (int i = 0; i < tabs.Count; i++)
                {
                    tabs[i].IsChecked = true;
                    Ui.Pump(80);
                    Assert.Equal(SettingsViewModel.Tabs[i], vm.Tab);
                    for (int other = 0; other < own.Length; other++)
                        Assert.All(own[other], line => Assert.True(Shown(line) == (other == i), $"\"{line}\" on the {vm.Tab} tab"));
                }
                // The old capital-letter section titles are gone: the tab says where you are.
                Assert.DoesNotContain(Visuals.Descendants<TextBlock>(window), t => t.Text is "GENERAL" or "APPEARANCE" or "TRACKING" or "NOTIFICATIONS" or "HISTORY");

                // Asked to show one part of it (the sensors notice): its tab is opened.
                vm.Tab = "general";
                vm.GoTo("sensors");
                Ui.Pump(300);
                Assert.Equal("sensors", vm.Tab);
                Assert.True(Shown("Background sensor sampling"));

                // On a standard account the eighth tab shows, with what is off there.
                vm.StandardAccount = true;
                Ui.Pump(120);
                Assert.True(every[7].IsVisible);
                every[7].IsChecked = true;
                Ui.Pump(120);
                Assert.Equal("admin", vm.Tab);
                Assert.True(Shown("This is a standard account"));
                Assert.False(Shown("Background sensor sampling"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void The_readings_colour_choice_shows_only_with_an_accent_picked()
    {
        var settings = Kit.OfflineSettings();
        try
        {
            Ui.Run(() =>
            {
                var vm = new SettingsViewModel(settings, new AgentClient(Ui.Dispatcher), new ReportService(settings)) { Tab = "appearance" };
                var window = Host(new Views.SettingsView { DataContext = vm });
                try
                {
                    TextBlock Title() => Visuals.Descendants<TextBlock>(window).Single(t => t.Text == "Temperature and usage colours");
                    Assert.False(Title().IsVisible);
                    vm.Accent = "teal";
                    vm.Refresh(); // as the shell does on every settings change
                    Ui.Pump(80);
                    Assert.True(Title().IsVisible);
                    var choices = Visuals.Descendants<RadioButton>(window).Where(r => r.GroupName == "StatusColors").ToList();
                    Assert.Equal(["Green, amber, red", "Accent"], choices.Select(c => (string)c.Content));
                    Assert.True(choices[1].IsChecked); // the accent, until told otherwise
                    choices[0].IsChecked = true;
                    Assert.True(settings.Current.KeepStatusColors);
                }
                finally { window.Close(); }
            });
        }
        finally { Reset(); }
    }
}
