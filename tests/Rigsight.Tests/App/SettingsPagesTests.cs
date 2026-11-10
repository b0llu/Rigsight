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
            (nameof(vm.SensorIntervalMs), 4000, s => s.Tracking.SensorIntervalMs, 4000),
            (nameof(vm.ProcessIntervalSeconds), 10, s => s.Tracking.ProcessIntervalSeconds, 10),
            (nameof(vm.IdleMinutes), 15, s => s.Tracking.IdleMinutes, 15),
            (nameof(vm.FullscreenCountsAsActive), false, s => s.Tracking.FullscreenCountsAsActive, false),
            (nameof(vm.YieldToHardwareApps), true, s => s.YieldToHardwareApps, true),
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
        });
    }

    [Fact]
    public void The_sensors_notice_stays_until_it_is_dealt_with()
    {
        using var link = new AgentLink();
        var vm = Page(link);
        var icue = new Rigsight.Core.Protocol.SkippedSensors { Part = "Fan hubs", Because = ["iCUE"] };
        Ui.Run(() =>
        {
            Assert.False(vm.SensorsNeedAttention);
            Assert.Null(vm.AttentionSection);

            vm.SensorStatus = new() { Paused = [icue], SafeMode = true };
            Assert.True(vm.SensorsNeedAttention);
            Assert.Equal("sensors", vm.AttentionSection);
            Assert.True(vm.ShowPausedGotIt);

            // "Got it": the program is seen, the problem still isn't.
            vm.AcknowledgePausedCommand.Execute(null);
            Assert.False(vm.ShowPausedGotIt);
            Assert.True(vm.SensorsNeedAttention);

            // "Try again": the problem is dealt with, and everything is read again.
            vm.RetrySensorsCommand.Execute(null);
            Assert.False(vm.SensorsNeedAttention);
            Assert.Null(vm.AttentionSection);
        });
        link.Sent("sensors-retry");
        Ui.Run(() =>
        {
            // The problem went away; if it comes back, it's news again. The program isn't.
            vm.SensorStatus = new() { Paused = [icue] };
            Assert.False(vm.SensorsNeedAttention);
            vm.SensorStatus = new() { Paused = [icue], SafeMode = true };
            Assert.True(vm.SensorsNeedAttention);

            // Changing the switch also acknowledges a new program's notice.
            vm.SensorStatus = new() { Paused = [new() { Part = "Motherboard", Because = ["Gigabyte Control Center"] }] };
            Assert.True(vm.ShowPausedGotIt);
            vm.YieldToHardwareApps = !vm.YieldToHardwareApps;
            Assert.False(vm.ShowPausedGotIt);
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
        Ui.Run(() => vm.PauseCommand.Execute("-1"));
        Assert.Equal("-1", link.Sent("pause", 2).Arg);
        Ui.Run(() => vm.ResumeCommand.Execute(null));
        link.Sent("resume");
        Ui.Run(() => vm.RetrySensorsCommand.Execute(null));
        link.Sent("sensors-retry");
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

    private static WidgetsViewModel WidgetsPage(AgentLink link) => Ui.Run(() => new WidgetsViewModel(link.Settings, link.Client));

    [Fact]
    public void The_page_has_a_card_for_every_widget_and_a_list_to_add_from()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        Ui.Run(() =>
        {
            Assert.Equal(link.Settings.Current.Widgets.Select(w => w.Id), vm.Cards.Select(c => c.Id));
            Assert.Equal("Slim bar", vm.Cards.Single(c => c.Style == WidgetStyle.Pill).Title);
            Assert.Equal("Temperature graph", vm.Cards.Single(c => c.Style == WidgetStyle.Graph).Title);
            Assert.All(vm.Cards, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
            Assert.True(vm.CanCreate);
            Assert.False(vm.IsEditing);

            // Every ready-made widget and every tile on its own, the ready-made ones first.
            // A ready-made widget that is one tile is listed once, as that tile.
            Assert.Equal(["Compact", "Gauges", "Slim bar"], vm.Choices.Where(c => c.Group == "Ready-made").Select(c => c.Title).Order());
            Assert.Equal(3 + WidgetTiles.Kinds.Count, vm.Choices.Count);
            Assert.Equal(vm.Choices, vm.ChoicesShown);
            Assert.Equal(vm.Choices.Count, vm.Choices.Select(c => c.Title).Distinct().Count());
            Assert.Equal(["All", "Ready-made", "Everyday", "Live", "Your day"], vm.Chips.Select(c => c.Key));

            vm.PickChipCommand.Execute(vm.Chips.Single(c => c.Key == "Everyday"));
            Assert.Equal(["Clock", "Date", "Calendar"], vm.ChoicesShown.Select(c => c.Title));
            vm.PickChipCommand.Execute(vm.Chips[0]);
            vm.ChoiceQuery = "calend";
            Assert.Equal("Calendar", Assert.Single(vm.ChoicesShown).Title);
            vm.ChoiceQuery = "zzz";
            Assert.True(vm.NoChoices);
            Assert.Equal("Nothing matches “zzz”.", vm.NoChoicesText);
            vm.ChoiceQuery = "";
            Assert.False(vm.NoChoices);
        });
    }

    [Fact]
    public void A_widget_added_from_the_list_sits_on_the_desktop_in_the_apps_look()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        Ui.Run(() =>
        {
            int before = vm.Cards.Count;
            vm.Add(vm.Choices.Single(c => c.Title == "Clock"));
            vm.Add(vm.Choices.Single(c => c.Title == "Clock")); // any number of a kind
            Assert.Equal(before + 2, vm.Cards.Count);
            var added = link.Settings.Current.Widgets.TakeLast(2).ToList();
            Assert.All(added, w =>
            {
                Assert.Equal((WidgetStyle.Custom, WidgetTheme.App, WidgetSits.OnDesktop, true), (w.Style, w.Theme, w.Sits, w.Enabled));
                Assert.Equal(WidgetTiles.Clock, Assert.Single(w.Tiles!).Kind);
            });
            Assert.NotEqual(added[0].Id, added[1].Id);
            Assert.Equal("1 tile", vm.Cards[^1].Description);

            // A ready-made one comes as tiles that look as it always did.
            vm.Add(vm.Choices.Single(c => c.Title == "Compact"));
            var compact = link.Settings.Current.Widgets[^1];
            Assert.Equal(2, compact.Tiles!.Count);
            Assert.Equal("Compact", compact.Name);
        });
    }

    [Fact]
    public void A_new_widget_opens_empty_in_the_editor_and_is_not_kept_if_left_empty()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        Ui.Run(() =>
        {
            int before = link.Settings.Current.Widgets.Count;
            vm.CreateCommand.Execute(null);
            var ed = Assert.IsType<WidgetEditorViewModel>(vm.Editor);
            Assert.True(vm.IsEditing);
            Assert.True(ed.IsEmpty);
            Assert.False(ed.HasTile);
            Assert.Equal("My widget", ed.Title);
            Assert.Equal(before + 1, link.Settings.Current.Widgets.Count);
            Assert.True(vm.Cards.Single(c => c.Id == ed.Id).IsEditing);

            ed.BackCommand.Execute(null);
            Assert.Null(vm.Editor);
            Assert.Equal(before, link.Settings.Current.Widgets.Count);

            // One started from a starter is kept.
            vm.CreateCommand.Execute(null);
            ed = vm.Editor!;
            ed.StartCommand.Execute(ed.Starters.Single(s => s.Value == "desk"));
            Assert.Equal([WidgetTiles.Clock, WidgetTiles.Calendar, WidgetTiles.Date], link.Settings.Current.Widgets.Single(w => w.Id == ed.Id).Tiles!.Select(t => t.Kind));
            Assert.Equal(0, ed.Selected);
            Assert.True(ed.IsClock);
            string id = ed.Id;
            ed.BackCommand.Execute(null);
            Assert.Equal("3 tiles", vm.Cards.Single(c => c.Id == id).Description);
            Assert.False(vm.Cards.Single(c => c.Id == id).IsEditing);
        });
    }

    [Fact]
    public void Tiles_are_added_moved_resized_and_removed_with_undo_for_each()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        Ui.Run(() =>
        {
            vm.CreateCommand.Execute(null);
            var ed = vm.Editor!;
            WidgetConfig Saved() => link.Settings.Current.Widgets.Single(w => w.Id == ed.Id);

            ed.AddTileCommand.Execute(ed.TileChoices.Single(c => c.Kind == WidgetTiles.Clock));
            ed.AddTileCommand.Execute(ed.TileChoices.Single(c => c.Kind == WidgetTiles.Date));
            Assert.Equal(2, Saved().Tiles!.Count);
            Assert.Equal(1, ed.Selected); // the one added is picked
            Assert.Equal(2, ed.TileBoxes.Count);
            var (clock, date) = (Saved().Tiles![0], Saved().Tiles![1]);
            Assert.Equal((0, 0, 6, 2), (clock.X, clock.Y, clock.W, clock.H));
            Assert.Equal((0, 2), (date.X, date.Y)); // under it, as the widget is already much wider than tall

            // No limit on tiles, even past what fits on a screen.
            for (int i = 0; i < 12; i++) ed.AddTileCommand.Execute(ed.TileChoices.Single(c => c.Kind == WidgetTiles.Calendar));
            Assert.Equal(14, Saved().Tiles!.Count);
            for (int i = 0; i < 12; i++) ed.UndoCommand.Execute(null);
            Assert.Equal(2, Saved().Tiles!.Count);

            // The date dragged a cell to the right, then the clock made as small as a clock can be.
            ed.MoveTile(1, 1, 0);
            Assert.Equal((1, 2), (Saved().Tiles![1].X, Saved().Tiles![1].Y));
            ed.ResizeTile(0, -10, -10);
            Assert.Equal((2, 1), (Saved().Tiles![0].W, Saved().Tiles![0].H)); // never smaller than a clock can be
            ed.GrowCommand.Execute("w+");
            Assert.Equal(3, ed.TileW);
            ed.UndoCommand.Execute(null);
            ed.UndoCommand.Execute(null);
            Assert.Equal((6, 2), (Saved().Tiles![0].W, Saved().Tiles![0].H));
            ed.RedoCommand.Execute(null);
            Assert.Equal((2, 1), (Saved().Tiles![0].W, Saved().Tiles![0].H));
            Assert.True(ed.CanUndo && ed.CanRedo);

            ed.SelectTile(0);
            ed.DuplicateTileCommand.Execute(null);
            Assert.Equal(3, Saved().Tiles!.Count);
            Assert.Equal(WidgetTiles.Clock, Saved().Tiles![2].Kind);
            ed.RemoveTileCommand.Execute(ed.TileBoxes[2]);
            ed.SelectTile(1);
            ed.RemovePickedTileCommand.Execute(null);
            Assert.Equal(WidgetTiles.Clock, Assert.Single(Saved().Tiles!).Kind);
            ed.RemoveTileAt(0);
            Assert.True(ed.IsEmpty);
            ed.UndoCommand.Execute(null);
            Assert.False(ed.IsEmpty);
            vm.CloseEditorCommand.Execute(null);
        });
    }

    [Fact]
    public void The_picked_tiles_settings_and_the_widgets_look_are_saved_at_once()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        Ui.Run(() =>
        {
            vm.CreateCommand.Execute(null);
            var ed = vm.Editor!;
            WidgetConfig Saved() => link.Settings.Current.Widgets.Single(w => w.Id == ed.Id);

            ed.AddTileCommand.Execute(ed.TileChoices.Single(c => c.Kind == WidgetTiles.Clock));
            Assert.True(ed.IsClock && ed.CanAlign && !ed.IsReading && !ed.HasReadings);
            Assert.Equal("Clock", ed.TileName);
            ed.ClockStyle = "Analog";
            ed.Hours24 = true;
            ed.Seconds = true;
            ed.ShowDate = false;
            ed.Align = "Left";
            var clock = Saved().Tiles![0];
            Assert.Equal(("Analog", true, true, false, TileAlign.Left), (clock.Style, clock.Hours24, clock.Seconds, clock.ShowDate, clock.Align));
            ed.ClockStyle = WidgetTiles.ClockStyles[0];
            Assert.Null(Saved().Tiles![0].Style); // the first style is the usual one, kept as nothing

            ed.AddTileCommand.Execute(ed.TileChoices.Single(c => c.Kind == WidgetTiles.Reading));
            Assert.True(ed.IsReading);
            ed.Reading = nameof(OverlayMetric.GpuTemp);
            ed.Look = WidgetTiles.Looks(nameof(OverlayMetric.GpuTemp), null)[1];
            ed.ShortName = "  My GPU  ";
            var reading = Saved().Tiles![1];
            Assert.Equal((nameof(OverlayMetric.GpuTemp), "My GPU"), (reading.Item, reading.Label));
            Assert.Equal(ed.Look, reading.Look);

            ed.AddTileCommand.Execute(ed.TileChoices.Single(c => c.Kind == WidgetTiles.Line));
            Assert.True(ed.HasReadings);
            int n = ed.TileReadings.Count;
            ed.ReadingToAdd = ed.ReadingsToAdd.First(r => ed.TileReadings.All(t => t.Name != r.Name));
            Assert.Equal(n + 1, ed.TileReadings.Count);
            ed.RemoveReadingCommand.Execute(ed.TileReadings[0]);
            Assert.Equal(n, ed.TileReadings.Count);

            ed.Title = "  Desk  ";
            ed.Theme = nameof(WidgetTheme.Light);
            ed.Accent = "#22D3EE";
            ed.Font = "Bahnschrift";
            ed.Grayscale = true;
            ed.Size = "1.5";
            ed.BackgroundPercent = 40;
            ed.ContentPercent = 10; // never below 20%
            ed.Corners = nameof(WidgetCorners.Square);
            ed.TileBoxesOn = true;
            ed.Sits = nameof(WidgetSits.OverWindows);
            ed.Locked = true;
            ed.Enabled = false;
            var w = Saved();
            Assert.Equal(("Desk", WidgetTheme.Light, "#22D3EE", "Bahnschrift", true, 1.5), (w.Name, w.Theme, w.Accent, w.Font, w.Grayscale, w.Scale));
            Assert.Equal((0.4, 0.2, WidgetCorners.Square, true, true), (Math.Round(w.BackgroundOpacity, 2), Math.Round(w.ContentOpacity, 2), w.Corners, w.TileBoxes, w.Gap != WidgetGap.None));
            Assert.Equal((WidgetSits.OverWindows, true, false), (w.Sits, w.Locked, w.Enabled));
            ed.Accent = "";
            Assert.Null(Saved().Accent);
            vm.CloseEditorCommand.Execute(null);
        });
    }

    [Fact]
    public void A_ready_made_widget_becomes_tiles_at_its_first_change_and_can_be_copied_or_removed()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        Ui.Run(() =>
        {
            var compact = vm.Cards.Single(c => c.Style == WidgetStyle.Compact);
            compact.EditCommand.Execute(null);
            var ed = vm.Editor!;
            Assert.Null(link.Settings.Current.Widgets.Single(w => w.Id == compact.Id).Tiles); // opened, not changed yet
            Assert.Equal(2, ed.TileBoxes.Count); // shown as the tiles it will become
            ed.Size = "2";
            var saved = link.Settings.Current.Widgets.Single(w => w.Id == compact.Id);
            Assert.Equal(2, saved.Tiles!.Count);
            Assert.Equal(WidgetStyle.Compact, saved.Style);

            int before = link.Settings.Current.Widgets.Count;
            ed.DuplicateCommand.Execute(null);
            Assert.Null(vm.Editor);
            var copy = link.Settings.Current.Widgets[^1];
            Assert.Equal((before + 1, WidgetStyle.Custom, "Compact", 2), (link.Settings.Current.Widgets.Count, copy.Style, copy.Name, copy.Tiles!.Count));

            vm.Edit(compact.Id);
            vm.Editor!.RemoveCommand.Execute(null);
            Assert.Null(vm.Editor);
            Assert.DoesNotContain(link.Settings.Current.Widgets, w => w.Id == compact.Id);
            Assert.DoesNotContain(vm.Cards, c => c.Id == compact.Id);

            // Opened from a widget's menu on the desktop; an identifier that's gone opens nothing.
            vm.Edit(copy.Id);
            Assert.Equal(copy.Id, vm.Editor!.Id);
            vm.Edit("custom-nope");
            Assert.Equal(copy.Id, vm.Editor!.Id);
            vm.CloseEditorCommand.Execute(null);
        });
    }

    [Fact]
    public void The_settings_for_all_widgets_and_their_shortcuts()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        Ui.Run(() =>
        {
            vm.Snap = false;
            vm.Fade = true;
            vm.Shown = false;
            Assert.Equal((false, true, false), (link.Settings.Current.WidgetSnap, link.Settings.Current.WidgetFade, link.Settings.Current.WidgetsShown));

            Assert.False(vm.Record(0x57, HotkeyModifiers.Ctrl)); // nothing waiting for keys
            vm.RecordShortcutCommand.Execute("shown");
            Assert.Equal("shown", vm.Recording);
            Assert.False(vm.Record(0x57, HotkeyModifiers.None)); // a letter alone isn't a shortcut; still waiting
            Assert.Equal("shown", vm.Recording);
            Assert.True(vm.Record(0x57, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt));
            Assert.Null(vm.Recording);
            Assert.Equal("Ctrl+Alt+W", link.Settings.Current.WidgetsHotkey);

            vm.RecordShortcutCommand.Execute("front");
            Assert.True(vm.Record(0x71, HotkeyModifiers.None)); // F2 alone is fine
            Assert.Equal("F2", link.Settings.Current.WidgetsFrontHotkey);
            vm.ClearShortcutCommand.Execute("shown");
            Assert.Null(link.Settings.Current.WidgetsHotkey);
            Assert.Equal("F2", link.Settings.Current.WidgetsFrontHotkey);
        });
    }

    // The map of the screens follows every change while the page shows (the user, 2026-11-10: it still showed widgets
    // that had been removed); a burst of changes draws it once, a moment later.
    [Fact]
    public void The_map_of_the_screens_follows_widgets_switched_off_removed_and_added()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        string[] Spots() { Thread.Sleep(450); return Ui.Run(() => vm.Spots.Select(s => s.Id).ToArray()); }
        string first = "", second = "";
        Ui.Run(() =>
        {
            link.Settings.Update(s =>
            {
                foreach (var w in s.Widgets) w.Enabled = false;
                (s.Widgets[0].Enabled, s.Widgets[0].X, s.Widgets[0].Y) = (true, 100, 100);
                (s.Widgets[1].Enabled, s.Widgets[1].X, s.Widgets[1].Y) = (true, 500, 100);
                s.WidgetsShown = true;
            });
            (first, second) = (link.Settings.Current.Widgets[0].Id, link.Settings.Current.Widgets[1].Id);
            vm.PageShown = true;
            vm.LoadMap();
            Assert.Equal([first, second], vm.Spots.Select(s => s.Id));
            vm.Cards.Single(c => c.Id == first).Enabled = false; // switched off on its card
        });
        Assert.Equal([second], Spots());

        Ui.Run(() => link.Settings.Update(s => s.Widgets.RemoveAll(w => w.Id == second))); // removed from its menu on the desktop
        Assert.Empty(Spots());

        Ui.Run(() => link.Settings.Update(s => { if (s.Widgets.FirstOrDefault(w => w.Id == first) is { } w) (w.Enabled, w.X) = (true, 300); })); // on again, moved
        Assert.Equal([first], Spots());
        Assert.Equal(300, Ui.Run(() => vm.Spots[0].X));

        // Away from the page, nothing is drawn for it.
        Ui.Run(() => { vm.PageShown = false; link.Settings.Update(s => s.Widgets.First(w => w.Id == first).Enabled = false); });
        Assert.Equal([first], Spots());
    }

    [Fact]
    public void Widget_cards_refresh_when_settings_change_elsewhere()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        Ui.Run(() =>
        {
            var card = vm.Cards[0];
            bool was = card.Enabled;
            link.Settings.Update(s => s.Widgets[0].Enabled = !was);
            var changed = Kit.Changes(card, vm.Refresh);
            Assert.Contains(nameof(WidgetCard.Enabled), changed);
            Assert.Equal(!was, card.Enabled);

            // A widget removed elsewhere (its menu on the desktop) closes the editor open on it.
            vm.Edit(card.Id);
            link.Settings.Update(s => s.Widgets.RemoveAt(0));
            vm.Refresh();
            Assert.Null(vm.Editor);
            Assert.DoesNotContain(vm.Cards, c => c.Id == card.Id);
        });
        Assert.Null(PreviewImages.Load("NoSuchPreview"));
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
            // Every reading but the widgets' own (today so far, the app in front), which come after the clock.
            Assert.Equal(Enum.GetValues<OverlayMetric>().Count(m => m < OverlayMetric.ScreenTime), vm.Groups.Sum(g => g.Options.Count));
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
            vm.Spot = new Rigsight.Controls.OverlaySpot(8, -0.05, 0);
            vm.Layout = "Line";
            vm.BackgroundPercent = 35;
            vm.ContentPercent = 70;
            vm.Colors = "Grayscale";
            vm.Scale = "1.5";
            o = link.Settings.Current.Overlay;
            Assert.Equal((8, -0.05, 0.0), (o.Anchor, o.OffsetX, o.OffsetY));
            Assert.Equal(("Right", "Bottom"), (vm.PreviewHorizontal, vm.PreviewVertical));
            Assert.Equal(OverlayLayout.Line, o.Layout);
            Assert.Equal(0.35, o.BackgroundOpacity, 6);
            Assert.Equal(0.7, o.ContentOpacity, 6);
            Assert.True(o.Grayscale);
            Assert.Equal(1.5, o.Scale);
            Assert.Equal((new Rigsight.Controls.OverlaySpot(8, -0.05, 0), "Line", 35.0, 70.0, "Grayscale", "1.5"), (vm.Spot, vm.Layout, vm.BackgroundPercent, vm.ContentPercent, vm.Colors, vm.Scale));
            vm.Colors = "Color";
            Assert.False(link.Settings.Current.Overlay.Grayscale);

            // Pushed past the left edge: RivaTuner can't draw there, and the page says so.
            Assert.False(vm.HangsOffLeftOrTop);
            vm.Spot = new Rigsight.Controls.OverlaySpot(0, -0.02, 0);
            Assert.True(vm.HangsOffLeftOrTop);
            vm.Spot = new Rigsight.Controls.OverlaySpot(2, 0.02, 0); // off the right edge is fine
            Assert.False(vm.HangsOffLeftOrTop);
            Assert.Equal(("Right", "Top"), (vm.PreviewHorizontal, vm.PreviewVertical));
            vm.Spot = new Rigsight.Controls.OverlaySpot(4, 0, 0);
            Assert.Equal(("Center", "Center"), (vm.PreviewHorizontal, vm.PreviewVertical));
        });
    }

    [Fact]
    public void Switching_the_overlay_off_keeps_saying_what_it_does()
    {
        // The switch by the title is the overlay's on/off (2026-11-09): off, it's hidden and its shortcut does nothing,
        // so the page still says what it does when on (it said "You can still show the overlay from here").
        using var page = new Page();
        var (link, vm, _) = page;
        Ui.Run(() =>
        {
            vm.Enabled = true;
            Assert.StartsWith("Press Alt+Shift+O in any game", vm.Intro);
            vm.Enabled = false;
            Assert.StartsWith("Press Alt+Shift+O in any game", vm.Intro);
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
