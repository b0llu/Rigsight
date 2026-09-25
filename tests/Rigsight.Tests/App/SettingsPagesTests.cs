using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Settings;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>The Settings, Widgets and Overlay pages: every control maps to its setting, and commands reach the agent.</summary>
[Collection("UI")]
public sealed class SettingsPageTests
{
    private static SettingsViewModel Page(AgentLink link) =>
        Ui.Run(() => new SettingsViewModel(link.Settings, link.Client, new ReportService(link.Settings)));

    [Fact]
    public void Every_setting_maps_to_its_field()
    {
        using var link = new AgentLink();
        var vm = Page(link);
        var cases = new (string Property, object Value, Func<RigsightSettings, object> Field, object Expected)[]
        {
            (nameof(vm.UseFahrenheit), true, s => s.UseFahrenheit, true),
            (nameof(vm.Theme), "light", s => s.Theme, "light"),
            (nameof(vm.LiveRefreshMs), 2500, s => s.LiveRefreshMs, 2500),
            (nameof(vm.StartPage), "memory", s => s.StartPage, "memory"),
            (nameof(vm.TrackingEnabled), false, s => s.Tracking.Enabled, false),
            (nameof(vm.SensorIntervalMs), 4000, s => s.Tracking.SensorIntervalMs, 4000),
            (nameof(vm.ProcessIntervalSeconds), 10, s => s.Tracking.ProcessIntervalSeconds, 10),
            (nameof(vm.IdleMinutes), 15, s => s.Tracking.IdleMinutes, 15),
            (nameof(vm.FullscreenCountsAsActive), false, s => s.Tracking.FullscreenCountsAsActive, false),
            (nameof(vm.AlertsEnabled), true, s => s.Alerts.Enabled, true),
            (nameof(vm.CpuLimit), 72.6, s => s.Alerts.CpuLimit, 73.0),
            (nameof(vm.GpuLimit), 80.4, s => s.Alerts.GpuLimit, 80.0),
            (nameof(vm.HotSpotLimit), 101.6, s => s.Alerts.GpuHotSpotLimit, 102.0),
            (nameof(vm.SustainSeconds), 30, s => s.Alerts.SustainSeconds, 30),
            (nameof(vm.CooldownMinutes), 20, s => s.Alerts.CooldownMinutes, 20),
            (nameof(vm.SessionSummaries), true, s => s.Alerts.SessionSummaries, true),
            (nameof(vm.DailyRecap), true, s => s.Alerts.DailyRecap, true),
            (nameof(vm.CrashNotifications), true, s => s.Alerts.CrashNotifications, true),
            (nameof(vm.QuietDuringFullscreen), false, s => s.Alerts.QuietDuringFullscreen, false),
            (nameof(vm.SessionSummaryMinMinutes), 30, s => s.Alerts.SessionSummaryMinMinutes, 30),
            (nameof(vm.CardSeconds), 12, s => s.Alerts.CardSeconds, 12),
            (nameof(vm.NotificationStyle), "Windows", s => s.Alerts.Style, NotificationStyle.Windows),
            (nameof(vm.AutoUpdate), false, s => s.AutoUpdate, false),
        };
        Ui.Run(() =>
        {
            foreach (var (property, value, field, expected) in cases)
            {
                var prop = typeof(SettingsViewModel).GetProperty(property)!;
                var changed = Kit.Changes(vm, () => prop.SetValue(vm, value));
                Assert.True(expected.Equals(field(link.Settings.Current)), $"{property}: {field(link.Settings.Current)} instead of {expected}");
                Assert.Contains(property, changed);
                // Read back as it's shown.
                var shown = prop.GetValue(vm);
                Assert.True(shown!.ToString() == expected.ToString(), $"{property} reads back {shown}");
            }
        });
        // All of it reaches the agent in one message.
        Assert.True(Ui.WaitFor(() => link.Agent.Settings.Alerts.CardSeconds == 12 && !link.Agent.Settings.AutoUpdate, 3000));
        Assert.Equal(73, link.Agent.Settings.Alerts.CpuLimit);
        Assert.Equal("memory", link.Agent.Settings.StartPage);
    }

    [Fact]
    public void Temperature_limits_show_in_the_chosen_unit()
    {
        using var link = new AgentLink();
        var vm = Page(link);
        Ui.Run(() =>
        {
            var changed = Kit.Changes(vm, () => vm.CpuLimit = 90);
            Assert.Contains(nameof(vm.CpuLimitText), changed);
            Assert.Equal("90°", vm.CpuLimitText);
            vm.UseFahrenheit = true;
            Assert.Equal("194°", vm.CpuLimitText);
            Assert.Equal(Units.TempShort(vm.GpuLimit), vm.GpuLimitText);
            Assert.Equal(Units.TempShort(vm.HotSpotLimit), vm.HotSpotLimitText);
        });
    }

    [Fact]
    public void Blank_theme_or_start_page_is_ignored()
    {
        using var link = new AgentLink();
        var vm = Page(link);
        Ui.Run(() =>
        {
            vm.Theme = null!;
            vm.StartPage = null!;
            Assert.Equal("dark", link.Settings.Current.Theme);
            Assert.Equal("home", link.Settings.Current.StartPage);
        });
    }

    [Theory]
    [InlineData(730, 0)]   // two years → forever
    [InlineData(90, 365)]  // longer
    [InlineData(365, 730)]
    [InlineData(90, 0)]
    public void Keeping_history_longer_needs_no_confirmation(int from, int to)
    {
        using var link = new AgentLink();
        var vm = Page(link);
        Ui.Run(() =>
        {
            link.Settings.Update(s => s.Tracking.KeepHistoryDays = from);
            var changed = Kit.Changes(vm, () => vm.KeepHistoryDays = to);
            Assert.Equal(to, link.Settings.Current.Tracking.KeepHistoryDays);
            Assert.Contains(nameof(vm.KeepHistoryDays), changed);
            // The same choice again changes nothing.
            Assert.Empty(Kit.Changes(vm, () => vm.KeepHistoryDays = to));
        });
        // Shorter asks first (a message box, which can't be answered in a test): see SettingsViewModel.KeepHistoryDays.
    }

    [Fact]
    public void Paused_tracking_is_explained()
    {
        using var link = new AgentLink();
        var vm = Page(link);
        Ui.Run(() =>
        {
            Assert.False(vm.IsPaused);
            Assert.Null(vm.PauseText);
            link.Settings.Update(s => s.Tracking.PausedUntil = -1);
            Assert.True(vm.IsPaused);
            Assert.Equal("Tracking is paused until you resume it.", vm.PauseText);
            long until = TimeUtil.NowUnix() + 1800;
            link.Settings.Update(s => s.Tracking.PausedUntil = until);
            Assert.True(vm.IsPaused);
            Assert.Equal($"Tracking is paused until {TimeUtil.FromUnix(until):h:mm tt}.", vm.PauseText);
            link.Settings.Update(s => s.Tracking.PausedUntil = TimeUtil.NowUnix() - 60);
            Assert.False(vm.IsPaused);
            Assert.Null(vm.PauseText);
            // Tracking switched off altogether isn't "paused".
            link.Settings.Update(s => { s.Tracking.PausedUntil = -1; s.Tracking.Enabled = false; });
            Assert.False(vm.IsPaused);
        });
    }

    [Fact]
    public void Excluded_apps_are_added_once_and_removed()
    {
        using var link = new AgentLink();
        var vm = Page(link);
        Ui.Run(() =>
        {
            vm.AppToExclude = "  Notepad  ";
            vm.AddExcludedCommand.Execute(null);
            Assert.Equal(["Notepad.exe"], link.Settings.Current.Tracking.ExcludedApps);
            Assert.Null(vm.AppToExclude);
            vm.AppToExclude = "NOTEPAD.EXE";
            vm.AddExcludedCommand.Execute(null);
            vm.AppToExclude = "zoom.exe";
            vm.AddExcludedCommand.Execute(null);
            vm.AppToExclude = "   ";
            vm.AddExcludedCommand.Execute(null);
            Assert.Equal(["Notepad.exe", "zoom.exe"], link.Settings.Current.Tracking.ExcludedApps);
            Assert.Equal(["Notepad.exe", "zoom.exe"], vm.ExcludedApps);
            vm.RemoveExcludedCommand.Execute("notepad.EXE");
            vm.RemoveExcludedCommand.Execute(null);
            Assert.Equal(["zoom.exe"], link.Settings.Current.Tracking.ExcludedApps);
            Assert.Equal(["zoom.exe"], vm.ExcludedApps);
        });
    }

    [Fact]
    public void Refresh_lists_excluded_apps_alphabetically_and_rereads_everything()
    {
        using var link = new AgentLink();
        var vm = Page(link);
        Ui.Run(() =>
        {
            link.Settings.Update(s => s.Tracking.ExcludedApps = ["zeta.exe", "Alpha.exe", "beta.exe"]);
            var changed = Kit.Changes(vm, vm.Refresh);
            Assert.Equal(["Alpha.exe", "beta.exe", "zeta.exe"], vm.ExcludedApps);
            Assert.Contains("", changed);
        });
    }

    [Fact]
    public void Known_apps_and_how_far_history_goes_back()
    {
        SharedData.EnsureSeeded();
        using var link = new AgentLink();
        var vm = Page(link);
        Kit.Wait(() => vm.LoadKnownAppsAsync());
        Ui.Run(() =>
        {
            Assert.Matches(@"^History goes back to \d{1,2} \w+ \d{4} \(\d+ days\)\.$", vm.TrackingSince);
            Assert.Contains("chrome.exe", vm.KnownApps);
            Assert.DoesNotContain("explorer.exe", vm.KnownApps); // Windows' own
            Assert.Equal(vm.KnownApps.Order(StringComparer.OrdinalIgnoreCase), vm.KnownApps);
        });
        // Loading again doesn't duplicate them.
        int count = Ui.Run(() => vm.KnownApps.Count);
        Kit.Wait(() => vm.LoadKnownAppsAsync());
        Assert.Equal(count, Ui.Run(() => vm.KnownApps.Count));
    }

    [Fact]
    public void Start_page_choices_are_the_pages_and_dashboards()
    {
        using var link = new AgentLink();
        var vm = Page(link);
        Ui.Run(() =>
        {
            Assert.Equal(ShellViewModel.BuiltInPages, vm.StartPageOptions);
            vm.GetCustomPages = () => [new PageOption("custom:abc", "Games")];
            Assert.Equal("custom:abc", vm.StartPageOptions[^1].Key);
            Assert.Equal(ShellViewModel.BuiltInPages.Count + 1, vm.StartPageOptions.Count);
            Assert.Matches(@"^v\d+\.\d+\.\d+$", vm.AppVersion);
        });
    }

    [Fact]
    public void Commands_reach_the_agent()
    {
        using var link = new AgentLink();
        var vm = Page(link);
        Ui.Run(() => vm.PauseCommand.Execute("30"));
        Assert.Equal("30", link.Sent("pause").Arg);
        Assert.Equal("Tracking paused for 30 minutes.", Ui.Run(() => vm.StatusMessage));
        Ui.Run(() => vm.PauseCommand.Execute("-1"));
        Assert.Equal("-1", link.Sent("pause", 2).Arg);
        Assert.Equal("Tracking paused.", Ui.Run(() => vm.StatusMessage));
        Ui.Run(() => vm.ResumeCommand.Execute(null));
        link.Sent("resume");
        Assert.Equal("Tracking resumed.", Ui.Run(() => vm.StatusMessage));
        Ui.Run(() => vm.PreviewCommand.Execute("alert"));
        Assert.Equal("alert", link.Sent("preview-notification").Arg);
    }

    [Fact]
    public void Start_with_windows_goes_through_the_agent()
    {
        using var link = new AgentLink();
        var vm = Page(link);
        Ui.Run(() =>
        {
            var changed = Kit.Changes(vm, () => vm.StartWithWindows = true);
            Assert.True(vm.StartupEnabled);
            Assert.Contains(nameof(vm.StartWithWindows), changed);
        });
        link.Sent("startup-on");
        Ui.Run(() => vm.StartWithWindows = false);
        link.Sent("startup-off");
        Assert.False(Ui.Run(() => vm.StartWithWindows));
    }

    [Fact]
    public void Auto_update_tells_the_update_section()
    {
        using var link = new AgentLink();
        var vm = Page(link);
        Ui.Run(() =>
        {
            vm.AutoUpdate = false; // no update section yet: fine
            var update = new UpdateViewModel(link.Client, () => false, () => link.Settings.Current.AutoUpdate);
            vm.Update = update;
            var changed = Kit.Changes(update, () => vm.AutoUpdate = true);
            Assert.Contains(nameof(update.PromptText), changed);
        });
    }

    // Clearing history and quitting the agent ask first with a message box, which a test can't answer; the commands
    // they send are the agent's to test.

    // ── Widgets ──────────────────────────────────────────────────────────

    [Fact]
    public void There_is_a_card_for_every_widget_style()
    {
        using var link = new AgentLink();
        var vm = Ui.Run(() => new WidgetsViewModel(link.Settings, link.Client));
        Ui.Run(() =>
        {
            Assert.Equal(Enum.GetValues<WidgetStyle>(), vm.Cards.Select(c => c.Style));
            Assert.Equal(vm.Cards.Count, vm.Cards.Select(c => c.Title).Distinct().Count());
            Assert.Equal(vm.Cards.Count, vm.Cards.Select(c => c.Description).Distinct().Count());
            Assert.All(vm.Cards, c => Assert.False(string.IsNullOrWhiteSpace(c.Title)));
            Assert.Equal("Slim bar", vm.Cards.Single(c => c.Style == WidgetStyle.Pill).Title);
            Assert.Equal("Temperature graph", vm.Cards.Single(c => c.Style == WidgetStyle.Graph).Title);
        });
    }

    [Theory]
    [InlineData(WidgetStyle.Compact)]
    [InlineData(WidgetStyle.Pill)]
    [InlineData(WidgetStyle.Graph)]
    public void A_widget_cards_controls_change_only_that_widget(WidgetStyle style)
    {
        using var link = new AgentLink();
        var vm = Ui.Run(() => new WidgetsViewModel(link.Settings, link.Client));
        Ui.Run(() =>
        {
            var card = vm.Cards.Single(c => c.Style == style);
            var others = SettingsStore.Serialize(link.Settings.Current.Clone());
            card.Enabled = true;
            card.Theme = "Light";
            card.Visibility = "HideInFullscreen";
            card.Locked = true;
            card.BackgroundPercent = 55;
            card.ContentPercent = 80;
            card.Scale = "1.25";
            var config = link.Settings.Current.Widgets.Single(w => w.Style == style);
            Assert.True(config.Enabled);
            Assert.Equal(WidgetTheme.Light, config.Theme);
            Assert.Equal(WidgetVisibility.HideInFullscreen, config.Visibility);
            Assert.True(config.Locked);
            Assert.Equal(0.55, config.BackgroundOpacity, 6);
            Assert.Equal(0.8, config.ContentOpacity, 6);
            Assert.Equal(1.25, config.Scale);
            Assert.Equal((55.0, 80.0, "1.25", "Light", "HideInFullscreen"), (card.BackgroundPercent, card.ContentPercent, card.Scale, card.Theme, card.Visibility));
            Assert.All(link.Settings.Current.Widgets.Where(w => w.Style != style), w => Assert.False(w.Enabled));
            Assert.NotEqual(others, SettingsStore.Serialize(link.Settings.Current));
        });
    }

    [Fact]
    public void Widget_scale_reads_without_trailing_zeros_in_any_culture()
    {
        using var link = new AgentLink();
        var vm = Ui.Run(() => new WidgetsViewModel(link.Settings, link.Client));
        Ui.Run(() =>
        {
            var old = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            try
            {
                var card = vm.Cards[0];
                card.Scale = "1.5";
                Assert.Equal("1.5", card.Scale);
                Assert.Equal(1.5, link.Settings.Current.Widgets[0].Scale);
                card.Scale = "1";
                Assert.Equal("1", card.Scale);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = old;
            }
        });
    }

    [Fact]
    public void Widget_previews_are_asked_for_and_loaded_when_ready()
    {
        using var link = new AgentLink();
        var vm = Ui.Run(() => new WidgetsViewModel(link.Settings, link.Client));
        Ui.Run(vm.RequestPreviews);
        link.Sent("render-previews");

        var folder = Path.Combine(RigsightPaths.DataDir, "previews");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Pill.png");
        try
        {
            WritePng(file, 40, 12);
            Ui.Run(() =>
            {
                vm.OnPreviewsReady();
                var pill = vm.Cards.Single(c => c.Style == WidgetStyle.Pill);
                var preview = Assert.IsAssignableFrom<BitmapSource>(pill.Preview);
                Assert.Equal(40, preview.PixelWidth);
                Assert.True(preview.IsFrozen);
                // Mid-rewrite (unreadable): the last picture stays.
                File.WriteAllText(file, "not a png");
                pill.LoadPreview();
                Assert.Same(preview, pill.Preview);
                // Styles without a picture yet have none.
                Assert.Null(vm.Cards.Single(c => c.Style == WidgetStyle.Graph).Preview);
            });
        }
        finally
        {
            File.Delete(file);
        }
        Assert.Null(PreviewImages.Load("NoSuchPreview"));
    }

    [Fact]
    public void Widget_cards_refresh_when_settings_change_elsewhere()
    {
        using var link = new AgentLink();
        var vm = Ui.Run(() => new WidgetsViewModel(link.Settings, link.Client));
        Ui.Run(() =>
        {
            var card = vm.Cards[0];
            link.Settings.Update(s => s.Widgets[0].Locked = true);
            var changed = Kit.Changes(card, vm.Refresh);
            Assert.Contains("", changed);
            Assert.True(card.Locked);
        });
    }

    internal static void WritePng(string path, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, new byte[w * h * 4], w * 4);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        png.Save(f);
    }
}

[Collection("UI")]
public sealed class OverlayPageTests
{
    /// <summary>The Overlay page on the made-up PC, connected to a fake agent.</summary>
    private sealed class Page : IDisposable
    {
        public AgentLink Link { get; } = new();
        public OverlayViewModel Vm { get; }
        public LiveData Live { get; }

        public Page()
        {
            (Vm, Live) = Ui.Run(() =>
            {
                var live = new LiveData(Link.Settings);
                live.LoadHello(Pc.Hello());
                return (new OverlayViewModel(Link.Settings, Link.Client, live), live);
            });
        }

        public void Deconstruct(out AgentLink link, out OverlayViewModel vm, out LiveData live) => (link, vm, live) = (Link, Vm, Live);

        public void Dispose() => Link.Dispose();
    }

    [Fact]
    public void Metric_chips_turn_readings_on_and_off_in_screen_order()
    {
        using var page = new Page();
        var (link, vm, _) = page;
        Ui.Run(() =>
        {
            Assert.Equal(["GAME", "CPU", "GPU", "MEMORY", "OTHER"], vm.Groups.Select(g => g.Title));
            Assert.Equal(Enum.GetValues<OverlayMetric>().Length, vm.Groups.Sum(g => g.Options.Count));
            var clock = vm.Groups.Single(g => g.Title == "OTHER").Options.Single(o => o.Label == "Time of day");
            var fps = vm.Groups[0].Options[0];
            Assert.False(clock.IsOn);
            Assert.True(fps.IsOn);
            clock.IsOn = true;
            fps.IsOn = false;
            fps.IsOn = false;
            var metrics = link.Settings.Current.Overlay.Metrics;
            Assert.Contains(OverlayMetric.Clock, metrics);
            Assert.DoesNotContain(OverlayMetric.Fps, metrics);
            Assert.Equal(metrics.Order(), metrics);
            Assert.Equal(metrics.Distinct(), metrics);
            var changed = Kit.Changes(clock, vm.Refresh);
            Assert.Contains(nameof(OverlayMetricOption.IsOn), changed);
        });
    }

    [Fact]
    public void Sensors_are_added_once_up_to_the_limit()
    {
        using var page = new Page();
        var (link, vm, live) = page;
        Ui.Run(() =>
        {
            Assert.False(vm.HasSensors);
            Assert.Equal("0 of 10", vm.SensorsCount);
            Assert.False(vm.AddSensorCommand.CanExecute(null));
            vm.SensorToAdd = live.CpuTemp;
            Assert.True(vm.AddSensorCommand.CanExecute(null));
            vm.AddSensorCommand.Execute(null);
            Assert.Null(vm.SensorToAdd);
            var row = Assert.Single(vm.Sensors);
            Assert.Equal(live.CpuTemp!.Id, row.Id);
            Assert.Equal("Core (Tctl/Tdie)", row.Name);
            Assert.Equal("Test CPU", row.Hardware);
            Assert.True(row.Found);
            Assert.True(vm.HasSensors);
            Assert.Equal("1 of 10", vm.SensorsCount);

            vm.SensorToAdd = live.CpuTemp; // already there
            Assert.False(vm.AddSensorCommand.CanExecute(null));

            foreach (var s in live.AllSensors.Skip(10).Take(12))
            {
                vm.SensorToAdd = s;
                if (vm.AddSensorCommand.CanExecute(null)) vm.AddSensorCommand.Execute(null);
            }
            Assert.Equal(OverlaySettings.MaxSensors, vm.Sensors.Count);
            Assert.Equal(OverlaySettings.MaxSensors, link.Settings.Current.Overlay.Sensors.Count);
            Assert.False(vm.CanAddMoreSensors);
            Assert.Equal("10 of 10", vm.SensorsCount);
            vm.SensorToAdd = live.GpuTemp;
            Assert.False(vm.AddSensorCommand.CanExecute(null));
            Assert.Equal(10, vm.MaxSensors);
        });
    }

    [Fact]
    public void Sensors_are_removed_and_reordered()
    {
        using var page = new Page();
        var (link, vm, live) = page;
        Ui.Run(() =>
        {
            foreach (var s in new[] { live.CpuTemp, live.GpuTemp, live.RamLoad })
            {
                vm.SensorToAdd = s;
                vm.AddSensorCommand.Execute(null);
            }
            string Ids() => string.Join(",", link.Settings.Current.Overlay.Sensors.Select(x => x.Id));
            vm.MoveSensorDownCommand.Execute(vm.Sensors[0]);
            Assert.Equal("/gpu/temperature/0,/cpu/temperature/0,/ram/load/0", Ids());
            Assert.Equal(["/gpu/temperature/0", "/cpu/temperature/0", "/ram/load/0"], vm.Sensors.Select(r => r.Id));
            vm.MoveSensorUpCommand.Execute(vm.Sensors[0]); // already first
            vm.MoveSensorDownCommand.Execute(vm.Sensors[2]); // already last
            Assert.Equal("/gpu/temperature/0,/cpu/temperature/0,/ram/load/0", Ids());
            vm.MoveSensorUpCommand.Execute(vm.Sensors[2]);
            Assert.Equal("/gpu/temperature/0,/ram/load/0,/cpu/temperature/0", Ids());
            vm.RemoveSensorCommand.Execute(vm.Sensors[1]);
            Assert.Equal("/gpu/temperature/0,/cpu/temperature/0", Ids());
            Assert.Equal(2, vm.Sensors.Count);
        });
    }

    [Fact]
    public void Short_names_are_trimmed_and_blank_means_the_sensors_own()
    {
        using var page = new Page();
        var (link, vm, live) = page;
        Ui.Run(() =>
        {
            vm.SensorToAdd = live.CpuTemp;
            vm.AddSensorCommand.Execute(null);
            var row = vm.Sensors[0];
            Assert.Equal("", row.Label);
            var changed = Kit.Changes(row, () => row.Label = "  CPU  ");
            Assert.Equal("CPU", link.Settings.Current.Overlay.Sensors[0].Label);
            Assert.Equal("CPU", row.Label);
            Assert.Contains(nameof(OverlaySensorRow.Label), changed);
            row.Label = "   ";
            Assert.Null(link.Settings.Current.Overlay.Sensors[0].Label);
            // The box stops at 18 characters; anything longer that gets in is cut to fit when saved.
            row.Label = new string('x', 30);
            var saved = link.Settings.Current.Clone();
            Assert.Equal(OverlaySettings.MaxLabelLength, saved.Overlay.Sensors[0].Label!.Length);
        });
    }

    [Fact]
    public void Rows_are_kept_while_nothing_changed_so_a_name_being_typed_survives()
    {
        using var page = new Page();
        var (link, vm, live) = page;
        Ui.Run(() =>
        {
            vm.SensorToAdd = live.CpuTemp;
            vm.AddSensorCommand.Execute(null);
            var row = vm.Sensors[0];
            vm.LoadSensors();
            vm.Refresh();
            Assert.Same(row, vm.Sensors[0]);
        });
    }

    [Fact]
    public void A_sensor_this_pc_no_longer_has_says_so()
    {
        using var page = new Page();
        var (link, vm, live) = page;
        Ui.Run(() =>
        {
            link.Settings.Update(s => s.Overlay.Sensors = [new OverlaySensor { Id = "/old/gpu/temp", Label = "Old GPU" }, new OverlaySensor { Id = "/gone" }]);
            vm.LoadSensors();
            Assert.Equal(["Old GPU", "/gone"], vm.Sensors.Select(r => r.Name));
            Assert.All(vm.Sensors, r => { Assert.False(r.Found); Assert.Equal("Not found on this PC right now", r.Hardware); });
            // The sensor appears (another hello): the row is rebuilt as found.
            var hello = Pc.Hello();
            hello.Hardware![0].Sensors.Add(new Rigsight.Core.Protocol.SensorMeta { Id = "/gone", Name = "Back again", Kind = SensorKind.Load });
            live.LoadHello(hello);
            vm.LoadSensors();
            Assert.True(vm.Sensors[1].Found);
            Assert.Equal("Back again", vm.Sensors[1].Name);
        });
    }

    [Fact]
    public void Look_and_place_settings()
    {
        using var page = new Page();
        var (link, vm, _) = page;
        Ui.Run(() =>
        {
            var o = link.Settings.Current.Overlay;
            vm.Corner = "BottomRight";
            vm.Layout = "Line";
            vm.BackgroundPercent = 35;
            vm.ContentPercent = 70;
            vm.Colors = "Grayscale";
            vm.Scale = "1.5";
            o = link.Settings.Current.Overlay;
            Assert.Equal(OverlayCorner.BottomRight, o.Corner);
            Assert.Equal(OverlayLayout.Line, o.Layout);
            Assert.Equal(0.35, o.BackgroundOpacity, 6);
            Assert.Equal(0.7, o.ContentOpacity, 6);
            Assert.True(o.Grayscale);
            Assert.Equal(1.5, o.Scale);
            Assert.Equal(("BottomRight", "Line", 35.0, 70.0, "Grayscale", "1.5"), (vm.Corner, vm.Layout, vm.BackgroundPercent, vm.ContentPercent, vm.Colors, vm.Scale));
            vm.Colors = "Color";
            Assert.False(link.Settings.Current.Overlay.Grayscale);
        });
    }

    [Fact]
    public void Turning_the_shortcut_off_changes_the_intro()
    {
        using var page = new Page();
        var (link, vm, _) = page;
        Ui.Run(() =>
        {
            vm.Enabled = true;
            Assert.StartsWith("Press Alt+Shift+O in any game", vm.Intro);
            var changed = Kit.Changes(vm, () => vm.Enabled = false);
            Assert.Contains(nameof(vm.Intro), changed);
            Assert.StartsWith("The shortcut is off.", vm.Intro);
            Assert.False(link.Settings.Current.Overlay.Enabled);
        });
    }

    [Fact]
    public void Recording_a_new_shortcut()
    {
        using var page = new Page();
        var (link, vm, _) = page;
        Ui.Run(() =>
        {
            vm.Enabled = true; // (the test settings have the shortcut off)
            Assert.Equal("Alt+Shift+O", vm.RecordText);
            vm.StartRecordingCommand.Execute(null);
            Assert.True(vm.IsRecording);
            Assert.Equal("Press the new shortcut…", vm.RecordText);
            Assert.StartsWith("Hold Ctrl, Alt or Shift", vm.RecordHint);

            vm.Record(0x1B, HotkeyModifiers.Ctrl); // Esc
            Assert.Equal("That key can't be used. Try a letter, a number or an F-key.", vm.RecordHint);
            Assert.True(vm.IsRecording);
            vm.Record('K', HotkeyModifiers.None);
            Assert.Equal("Add Ctrl, Alt or Shift, so the shortcut doesn't clash with your games.", vm.RecordHint);
            Assert.True(vm.IsRecording);
            Assert.Equal("Alt+Shift+O", link.Settings.Current.Overlay.Hotkey);

            var changed = Kit.Changes(vm, () => vm.Record('K', HotkeyModifiers.Ctrl | HotkeyModifiers.Shift));
            Assert.Equal("Ctrl+Shift+K", link.Settings.Current.Overlay.Hotkey);
            Assert.False(vm.IsRecording);
            Assert.Equal("", vm.RecordHint);
            Assert.Equal("Ctrl+Shift+K", vm.RecordText);
            Assert.StartsWith("Press Ctrl+Shift+K", vm.Intro);
            foreach (var p in new[] { nameof(vm.Hotkey), nameof(vm.Intro), nameof(vm.RecordText) }) Assert.Contains(p, changed);

            // F-keys work on their own.
            vm.StartRecordingCommand.Execute(null);
            vm.Record(0x74, HotkeyModifiers.None);
            Assert.Equal("F5", vm.Hotkey);

            vm.StartRecordingCommand.Execute(null);
            vm.CancelRecording();
            Assert.False(vm.IsRecording);
            Assert.Equal("", vm.RecordHint);
            Assert.Equal("F5", vm.Hotkey);

            vm.ResetHotkeyCommand.Execute(null);
            Assert.Equal(OverlaySettings.DefaultHotkey, link.Settings.Current.Overlay.Hotkey);
            Assert.Equal(OverlaySettings.DefaultHotkey, vm.RecordText);
        });
    }

    [Theory]
    [InlineData("", false, false, false, "The overlay won't show in fullscreen games")]
    [InlineData("running", false, false, false, "The overlay won't show in fullscreen games")]
    [InlineData("stopped", true, false, true, "The overlay won't show in fullscreen games")]
    [InlineData("missing", true, true, false, "The overlay won't show in fullscreen games")]
    [InlineData("install-failed", true, true, false, "The overlay won't show in fullscreen games")]
    [InlineData("installing", true, false, false, "Installing RivaTuner…")]
    public void RivaTuner_state(string state, bool warn, bool canInstall, bool canStart, string title)
    {
        using var page = new Page();
        var (link, vm, _) = page;
        Ui.Run(() =>
        {
            var changed = Kit.Changes(vm, () => vm.RtssState = state);
            Assert.Equal((warn, canInstall, canStart, title), (vm.ShowRtssWarning, vm.CanInstallRtss, vm.CanStartRtss, vm.RtssTitle));
            Assert.False(string.IsNullOrEmpty(vm.RtssStatus));
            if (state != "") foreach (var p in new[] { nameof(vm.ShowRtssWarning), nameof(vm.RtssStatus), nameof(vm.CanInstallRtss) }) Assert.Contains(p, changed);
        });
    }

    [Fact]
    public void Overlay_commands_reach_the_agent()
    {
        using var page = new Page();
        var (link, vm, _) = page;
        Ui.Run(() =>
        {
            Assert.Equal("Show overlay", vm.ToggleText);
            vm.IsVisible = true;
            Assert.Equal("Hide overlay", vm.ToggleText);
            vm.ToggleCommand.Execute(null);
            vm.StartRtssCommand.Execute(null);
            vm.InstallRtssCommand.Execute(null);
            vm.RequestStatus();
            vm.RequestPreview();
        });
        foreach (var cmd in new[] { "overlay-toggle", "start-rtss", "install-rtss", "overlay-status", "render-previews" }) link.Sent(cmd);
    }

    [Fact]
    public void The_overlay_preview_loads_when_ready()
    {
        using var page = new Page();
        var (link, vm, _) = page;
        var folder = Path.Combine(RigsightPaths.DataDir, "previews");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Overlay.png");
        try
        {
            Ui.Run(() => { vm.OnPreviewsReady(); Assert.Null(vm.Preview); });
            SettingsPageTests.WritePng(file, 20, 20);
            Ui.Run(() => { vm.OnPreviewsReady(); Assert.NotNull(vm.Preview); });
        }
        finally
        {
            File.Delete(file);
        }
    }
}
