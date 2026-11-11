using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Rigsight.Controls;
using Rigsight.Core.Settings;
using Rigsight.Tests.AppUi;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.Audit;

/// <summary>
/// The Widgets page and its editor in the real window, looked at the way people use them: opening, picking and closing
/// change nothing that's set; ready sizes show which one a tile is at; the map, the pop-ups and the editor stay sound.
/// </summary>
[Collection("UI")]
public sealed class WidgetsUiAuditTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(Path.GetTempPath(), "rigsight-tests", "audit4");

    // A widget of tiles keeps no list of readings of its own: the settings fill one in when they're read back (the old
    // widgets' list), which isn't a change anyone made, so it's left out of the comparison.
    private static string Json(WidgetConfig? w)
    {
        if (w is null) return "null";
        var copy = WidgetEditorViewModel.Clone(w);
        if (copy.Tiles is not null) copy.Items = null;
        return JsonSerializer.Serialize(copy, SettingsStore.JsonOptions);
    }

    private string Saved(string id) => Ui.Run(() => Json(host.Shell.Settings.Current.Widgets.FirstOrDefault(w => w.Id == id)));

    /// <summary>A widget with one of every tile, each set away from what it starts at, and the widget's look set too.</summary>
    private static WidgetConfig EveryTile(string name)
    {
        var tiles = new List<WidgetTile>();
        foreach (var kind in WidgetTiles.Kinds)
        {
            var t = WidgetTiles.New(kind);
            WidgetTiles.Place(tiles, t);
            switch (kind.Id)
            {
                case WidgetTiles.Clock: t.Style = "Analog"; t.Hours24 = true; t.Seconds = true; t.ShowDate = false; t.Align = TileAlign.Right; break;
                case WidgetTiles.Reading: t.Item = nameof(OverlayMetric.GpuLoad); t.Look = WidgetTiles.Ring; t.Label = "GPU"; break;
                case WidgetTiles.Line: t.Items = WidgetCatalog.Items(OverlayMetric.CpuLoad, OverlayMetric.Ram); break;
                case WidgetTiles.Notes: t.Text = "Saturday"; t.TextSize = "Large"; t.Look = WidgetTiles.Sticky; break;
                case WidgetTiles.Todo: t.Todos = [new() { Text = "One", Done = true }, new() { Text = "Two" }]; t.TextSize = "Small"; break;
                case WidgetTiles.Timer: t.TimerKind = "Countdown"; t.Until = DateTime.Today.AddDays(20); break;
                case WidgetTiles.Photos: t.Every = 300; break;
                case WidgetTiles.Weather: (t.Place, t.Lat, t.Lon) = ("Oslo", 59.91, 10.75); break;
                case WidgetTiles.Shortcuts: t.Links = [new() { Path = "https://rigsight.app/", Name = "rigsight.app" }]; break;
                case WidgetTiles.Date: t.Align = TileAlign.Left; break;
            }
            tiles.Add(t);
        }
        var cfg = WidgetsViewModel.NewWidget(name, tiles);
        cfg.Theme = WidgetTheme.Light;
        cfg.Accent = "#22D3EE";
        cfg.Font = "Georgia";
        cfg.Grayscale = true;
        cfg.Scale = 1.1; // between the sizes the segments offer
        cfg.BackgroundOpacity = 0.37; // between the slider's ticks
        cfg.ContentOpacity = 0.83;
        cfg.Corners = WidgetCorners.Slight;
        cfg.Gap = WidgetGap.Large;
        cfg.TileBoxes = true;
        cfg.Locked = true;
        (cfg.X, cfg.Y) = (200, 150);
        WidgetTiles.Clean(cfg);
        return cfg;
    }

    /// <summary>The lines that differ between two saved widgets.</summary>
    private static string Diff(string a, string b)
    {
        var x = a.Split('\n');
        var y = b.Split('\n');
        var lines = new List<string>();
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            string l = i < x.Length ? x[i].Trim() : "", r = i < y.Length ? y[i].Trim() : "";
            if (l != r) lines.Add($"line {i}: {l}  ->  {r}");
            if (lines.Count > 12) break;
        }
        return string.Join("\n  ", lines);
    }

    private static void Same(string before, string after, string what)
    {
        if (before == after) return;
        Directory.CreateDirectory(Shots);
        File.WriteAllText(Path.Combine(Shots, what + "-before.json"), before);
        File.WriteAllText(Path.Combine(Shots, what + "-after.json"), after);
        Assert.Fail($"{what}: the widget changed\n  " + Diff(before, after));
    }

    /// <summary>
    /// Every list, combo box and segment shown under <paramref name="root"/> whose pick isn't what its setting holds now
    /// (a list or combo with nothing picked while its setting has a value it lists, or a segment lit for another value).
    /// </summary>
    private static List<string> ShownWrong(DependencyObject root)
    {
        var wrong = new List<string>();
        static object? SourceValue(System.Windows.Data.BindingExpression be)
        {
            if (be.ResolvedSource is not { } src || be.ResolvedSourcePropertyName is not { } name) return null;
            return src.GetType().GetProperty(name)?.GetValue(src);
        }
        foreach (var sel in Visuals.Descendants<System.Windows.Controls.Primitives.Selector>(root).Where(e => e.IsVisible))
        {
            if (System.Windows.Data.BindingOperations.GetBindingExpression(sel, System.Windows.Controls.Primitives.Selector.SelectedValueProperty) is not { } be) continue;
            var want = SourceValue(be)?.ToString();
            var listed = sel.Items.Cast<object>().Select(i => i.GetType().GetProperty(sel.SelectedValuePath)?.GetValue(i)?.ToString()).ToList();
            var shown = sel.SelectedValue?.ToString();
            if (want is not null && listed.Contains(want) && shown != want)
                wrong.Add($"{AutomationProperties_Name(sel)}: shows {shown ?? "nothing"}, set to {want}");
        }
        foreach (var rb in Visuals.Descendants<RadioButton>(root).Where(e => e.IsVisible))
        {
            if (System.Windows.Data.BindingOperations.GetBindingExpression(rb, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty) is not { } be
                || be.ParentBinding.Converter is not { } conv) continue;
            var want = conv.Convert(SourceValue(be), typeof(bool?), be.ParentBinding.ConverterParameter, System.Globalization.CultureInfo.InvariantCulture) as bool?;
            if (want is bool w && rb.IsChecked != w) wrong.Add($"{rb.Content}: {(rb.IsChecked == true ? "lit" : "not lit")}, should be {(w ? "lit" : "not lit")}");
        }
        return wrong;
    }

    private string AddWidget(WidgetConfig cfg)
    {
        Ui.Run(() =>
        {
            host.Shell.Settings.Update(s => s.Widgets.Add(cfg));
            host.Shell.Widgets.Refresh();
        });
        return cfg.Id;
    }

    private void Drop(params string[] ids) => Ui.Run(() =>
    {
        if (host.Shell.Widgets.IsEditing) host.Shell.Widgets.CloseEditorCommand.Execute(null);
        host.Shell.Settings.Update(s => s.Widgets.RemoveAll(w => ids.Contains(w.Id)));
        host.Shell.Widgets.Refresh();
    });

    // Opening the editor, picking every tile in turn, every tab, and closing it again changes nothing the widget has (the
    // combo boxes, segments, sliders and ready sizes only show what's set until they're moved).
    [Fact]
    public void Picking_every_tile_and_tab_in_the_editor_changes_nothing()
    {
        host.Show("widgets");
        var cfg = EveryTile("Everything");
        string id = AddWidget(cfg);
        Ui.Pump(300);
        string before = Saved(id);
        try
        {
            Ui.Run(() => host.Shell.Widgets.Edit(id));
            Ui.Pump(500);
            var ed = Ui.Run(() => host.Shell.Widgets.Editor!);
            int n = Ui.Run(() => ed.TileBoxes.Count);
            var shownWrong = new List<string>();
            foreach (var tab in new[] { "look", "shape", "desktop", "look" })
            {
                Ui.Run(() => ed.PanelTab = tab);
                Ui.Pump(120);
                shownWrong.AddRange(Ui.Run(() => ShownWrong(host.Window)).Select(w => $"{tab} tab: {w}"));
            }
            for (int round = 0; round < 2; round++)
                for (int i = 0; i < n; i++)
                {
                    int k = round == 0 ? i : n - 1 - i;
                    Ui.Run(() => ed.PickTileCommand.Execute(ed.TileBoxes[k]));
                    Ui.Pump(120);
                    var tile = Ui.Run(() => ed.TileName);
                    shownWrong.AddRange(Ui.Run(() => ShownWrong(host.Window)).Select(w => $"{tile}: {w}"));
                }
            Ui.Run(() => ed.WholeWidgetCommand.Execute(null));
            Ui.Pump(200);
            Assert.False(Ui.Run(() => ed.CanUndo), "Picking tiles left something to undo: a control wrote a value back");
            Same(before, Saved(id), "every-tile-open");
            Ui.Run(() => host.Shell.Widgets.CloseEditorCommand.Execute(null));
            Ui.Pump(300);
            Same(before, Saved(id), "every-tile-closed");
            Assert.True(shownWrong.Count == 0, "Shown other than set:\n" + string.Join("\n", shownWrong.Distinct()));
            Ui.AssertNoProblems("the editor, every tile picked");
        }
        finally { Drop(id); }
    }

    // "Edit widget…" from a widget's menu on the desktop opens the editor on it, even with the editor open on another one:
    // neither widget changes, and the one left behind stops redrawing itself every second.
    [Fact]
    public void Editing_another_widget_from_the_desktop_while_the_editor_is_open()
    {
        host.Show("widgets");
        var a = EveryTile("First");
        var b = EveryTile("Second");
        b.Theme = WidgetTheme.Dark;
        b.Accent = null;
        b.Font = "Bahnschrift";
        b.Scale = 2;
        b.Corners = WidgetCorners.Square;
        b.Gap = WidgetGap.None;
        b.TileBoxes = false;
        b.Grayscale = false;
        AddWidget(a);
        AddWidget(b);
        Ui.Pump(300);
        string beforeA = Saved(a.Id), beforeB = Saved(b.Id);
        try
        {
            Ui.Run(() => host.Shell.Widgets.Edit(a.Id));
            Ui.Pump(400);
            var first = Ui.Run(() => host.Shell.Widgets.Editor!);
            Ui.Run(() => first.PickTileCommand.Execute(first.TileBoxes[0]));
            Ui.Pump(200);
            Ui.Run(() => host.Shell.Widgets.Edit(b.Id));
            Ui.Pump(400);
            var second = Ui.Run(() => host.Shell.Widgets.Editor!);
            Ui.Run(() => second.PickTileCommand.Execute(second.TileBoxes[0]));
            Ui.Pump(200);
            Ui.Run(() => host.Shell.Widgets.Edit(a.Id));
            Ui.Pump(400);
            Assert.Equal(beforeA, Saved(a.Id));
            Assert.Equal(beforeB, Saved(b.Id));
            // The editor no longer shown is done: nothing redraws it.
            int drawn = 0;
            Ui.Run(() => first.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WidgetEditorViewModel.Picture)) drawn++; });
            Ui.Pump(2500);
            Assert.True(drawn == 0, $"The editor left behind drew its widget {drawn} times in 2.5 s");
        }
        finally { Drop(a.Id, b.Id); }
    }

    // Every widget the Add pop-up offers: added, opened in the editor, every tile picked, closed: as it was added.
    [Fact]
    public void Every_widget_to_add_opens_and_closes_unchanged()
    {
        host.Show("widgets");
        var problems = new List<string>();
        var choices = Ui.Run(() => host.Shell.Widgets.Choices.ToList());
        foreach (var choice in choices)
        {
            string id = "";
            Ui.Run(() =>
            {
                host.Shell.Widgets.OpenAddListCommand.Execute(null);
                choice.AddCommand.Execute(null);
                id = host.Shell.Widgets.PickedId!;
            });
            Ui.Pump(200);
            string before = Saved(id);
            Ui.Run(() => host.Shell.Widgets.Edit(id));
            Ui.Pump(250);
            var ed = Ui.Run(() => host.Shell.Widgets.Editor!);
            int n = Ui.Run(() => ed.TileBoxes.Count);
            for (int i = 0; i < n; i++)
            {
                Ui.Run(() => ed.PickTileCommand.Execute(ed.TileBoxes[i]));
                Ui.Pump(60);
            }
            Ui.Run(() => host.Shell.Widgets.CloseEditorCommand.Execute(null));
            Ui.Pump(150);
            if (Saved(id) != before) problems.Add($"{choice.Title}: " + Diff(before, Saved(id)));
            Drop(id);
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // A tile's ready sizes light the one it's at, and none between them; a step of the cells moves the light with it.
    [Fact]
    public void Ready_sizes_light_the_size_a_tile_is_at()
    {
        host.Show("widgets");
        var problems = new List<string>();
        var cfg = WidgetsViewModel.NewWidget("Sizes", []);
        string id = AddWidget(cfg);
        try
        {
            Ui.Run(() => host.Shell.Widgets.Edit(id));
            Ui.Pump(300);
            var ed = Ui.Run(() => host.Shell.Widgets.Editor!);
            foreach (var kind in WidgetTiles.Kinds)
            {
                Ui.Run(() => ed.AddTileCommand.Execute(new TileChoice(kind.Id, kind.Name, kind.Line, kind.Group)));
                Ui.Pump(80);
                string Lit() => Ui.Run(() =>
                {
                    var list = Visuals.Descendants<ListBox>(host.Window).Single(l => AutomationProperties_Name(l) == "Size" && l.IsVisible);
                    return list.SelectedValue as string ?? "-";
                });
                foreach (var p in WidgetTiles.Presets(kind.Id))
                {
                    string want = $"{p.W}x{p.H}";
                    Ui.Run(() => ed.TilePreset = want);
                    Ui.Pump(60);
                    var (w, h) = Ui.Run(() => (ed.TileW, ed.TileH));
                    if ((w, h) != (p.W, p.H)) problems.Add($"{kind.Id} {p.Name}: the tile is {w}x{h}");
                    if (Lit() != want) problems.Add($"{kind.Id} {p.Name}: {Lit()} lit");
                }
                Ui.Run(() => ed.GrowCommand.Execute("w+"));
                Ui.Pump(60);
                var (gw, gh) = Ui.Run(() => (ed.TileW, ed.TileH));
                string expect = WidgetTiles.Presets(kind.Id).Any(p => p.W == gw && p.H == gh) ? $"{gw}x{gh}" : "-";
                if (Lit() != expect) problems.Add($"{kind.Id} at {gw}x{gh}: {Lit()} lit, {expect} expected");
            }
            Ui.AssertNoProblems("ready sizes");
        }
        finally { Drop(id); }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static string? AutomationProperties_Name(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetName(d);

    // A widget picked up on the map and lost mid-drag (another window took the pointer) isn't moved by a later click on an
    // empty part of the map.
    [Fact]
    public void A_drag_lost_midway_moves_nothing_later()
    {
        host.Show("widgets");
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var cfg = WidgetsViewModel.NewWidget("Clock", [WidgetTiles.New(WidgetTiles.Kinds.First(k => k.Id == WidgetTiles.Clock))]);
        (cfg.X, cfg.Y) = (screen.Left + screen.Width / 2, screen.Top + screen.Height / 3);
        Ui.Run(() =>
        {
            host.Shell.Settings.Update(s => { s.WidgetsShown = true; s.WidgetSnap = false; s.Widgets.Add(cfg); });
            host.Shell.Widgets.Refresh();
            host.Shell.Widgets.LoadMap();
        });
        Ui.Pump(500);
        try
        {
            var map = Ui.Run(() => Visuals.Descendants<WidgetScreens>(host.Window).Single());
            var from = Ui.Run(() => map.CentreOf(cfg.Id)) ?? throw new Xunit.Sdk.XunitException("The widget isn't on the map");
            int moved = 0;
            Ui.Run(() =>
            {
                map.Moved += (_, _, _) => moved++;
                Assert.True(map.PickUp(from));
                map.DragTo(new Point(from.X + 30, from.Y + 12));
                // Another window takes the pointer: the map never hears the button go up.
                map.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
                // Later, a click on an empty spot of the map.
                Assert.False(map.PickUp(new Point(1, 1)));
                map.Drop();
            });
            Assert.Equal(0, moved);
        }
        finally { Drop(cfg.Id); }
    }

    // The Add widget pop-up's search: a word that matches nothing says so and lists nothing; a group's name lists that group.
    [Fact]
    public void The_add_search_says_when_nothing_matches()
    {
        host.Show("widgets");
        var vm = Ui.Run(() => host.Shell.Widgets);
        Ui.Run(() => { vm.OpenAddListCommand.Execute(null); vm.ChoiceQuery = "zzqx"; });
        Ui.Pump(200);
        Assert.True(Ui.Run(() => vm.NoChoices));
        Assert.Contains("zzqx", Ui.Run(() => vm.NoChoicesText));
        Ui.Run(() => vm.ChoiceQuery = "Your day");
        Ui.Pump(200);
        Assert.False(Ui.Run(() => vm.NoChoices));
        Assert.All(Ui.Run(() => vm.ChoicesShown.ToList()), c => Assert.Equal(WidgetTiles.GroupYourDay, c.Group));
        // Opened again, the search is empty and everything is listed.
        Ui.Run(() => { vm.AddListOpen = false; vm.OpenAddListCommand.Execute(null); });
        Ui.Pump(200);
        Assert.Equal(Ui.Run(() => vm.Choices.Count), Ui.Run(() => vm.ChoicesShown.Count));
        Ui.Run(() => vm.AddListOpen = false);
        Ui.AssertNoProblems("the Add widget search");
    }
}
