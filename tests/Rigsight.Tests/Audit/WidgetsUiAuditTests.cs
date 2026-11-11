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
            NoProblemsButTheDatePickers("the editor, every tile picked");
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
            Assert.Empty(Ui.Run(() => ShownWrong(host.Window)));
            Assert.Equal(beforeA, Saved(a.Id));
            Assert.Equal(beforeB, Saved(b.Id));
            // The editor no longer shown is done: nothing redraws it.
            int drawn = 0;
            Ui.Run(() => first.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WidgetEditorViewModel.Picture)) drawn++; });
            Ui.Pump(2500);
            Assert.True(drawn == 0, $"The editor left behind drew its widget {drawn} times in 2.5 s");
        }
        finally { Drop(a.Id, b.Id); }
        NoProblemsButTheDatePickers("Editing_another_widget_from_the_desktop_while_the_editor_is_open");
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
        NoProblemsButTheDatePickers("Every_widget_to_add_opens_and_closes_unchanged");
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
            NoProblemsButTheDatePickers("ready sizes");
        }
        finally { Drop(id); }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// Binding errors and crashes since the last look, but one: a day box that has been shown (a countdown's Day) logs that
    /// its text box lost its date picker each time the theme is put on again, which every settings change does. Windows'
    /// own date picker look; it changes nothing on screen (found by this audit, left as it is, as Temperatures' range box
    /// has the same).
    /// </summary>
    private static void NoProblemsButTheDatePickers(string where)
    {
        var (bindings, crashes) = Ui.TakeProblems();
        var lines = bindings.Where(b => !b.Contains("AncestorType='System.Windows.Controls.DatePicker'") || !b.Contains("'DatePickerTextBox'"))
            .Select(b => "binding: " + b).Concat(crashes.Select(c => "exception: " + c)).ToList();
        Assert.True(lines.Count == 0, $"{where}:\n" + string.Join("\n", lines.Distinct().Take(40)));
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
        NoProblemsButTheDatePickers("A_drag_lost_midway_moves_nothing_later");
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
        NoProblemsButTheDatePickers("the Add widget search");
    }
    /// <summary>
    /// Words cut short or cut off under <paramref name="root"/>: a one-line text narrower than it needs, and anything that
    /// sticks out of the card it's in.
    /// </summary>
    /// <summary>Texts shown cut short with "…", for the report.</summary>
    private static readonly HashSet<string> Trimmed = [];

    private static List<string> CutOff(FrameworkElement root)
    {
        var found = new List<string>();
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root).PixelsPerDip;
        foreach (var tb in Visuals.Descendants<TextBlock>(root))
        {
            if (!tb.IsVisible || string.IsNullOrWhiteSpace(tb.Text) || tb.ActualWidth <= 0 || tb.Text.Length == 1) continue;
            if (tb.TextWrapping == TextWrapping.NoWrap)
            {
                var ft = new System.Windows.Media.FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture, tb.FlowDirection,
                    new System.Windows.Media.Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, System.Windows.Media.Brushes.Black, dpi);
                double need = ft.WidthIncludingTrailingWhitespace + tb.Padding.Left + tb.Padding.Right;
                // Cut short on purpose with "…" is listed apart (Trimmed); cut off without one is a fault.
                // The room its parent gave it (a text given less is laid out at its full width and clipped, so its own
                // width says nothing).
                var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(tb);
                double has = Math.Min(tb.ActualWidth, slot.Width - tb.Margin.Left - tb.Margin.Right);
                if (need > has + 1)
                {
                    if (tb.TextTrimming == TextTrimming.None) found.Add($"\"{tb.Text}\" needs {need:0} px, has {has:0}");
                    else Trimmed.Add(tb.Text);
                }
            }
            if (Visuals.Ancestors<SmoothBorder>(tb).FirstOrDefault() is { } card)
            {
                var r = tb.TransformToAncestor(card).TransformBounds(new Rect(0, 0, tb.ActualWidth, tb.ActualHeight));
                if (r.Right > card.ActualWidth + 1 || r.Left < -1) found.Add($"\"{tb.Text}\" runs {Math.Max(r.Right - card.ActualWidth, -r.Left):0} px out of its card");
            }
        }
        return found;
    }

    /// <summary>Lets the UI thread do what's waiting (layout, lists making their rows) without leaving the caller's flow of work.</summary>
    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    public static TheoryData<string, int> Widths => new()
    {
        { "en", 1140 }, { "en", 1280 }, { "en", 1920 }, { "es", 1140 }, { "es", 1280 }, { "es", 1920 },
    };

    // The page and the editor at the window's narrowest, a laptop's and a big screen's width, in English and Spanish:
    // nothing cut short or out of its card. Drawn to %TEMP%\rigsight-tests\audit4 to be looked at.
    [Theory]
    [MemberData(nameof(Widths))]
    public void The_page_and_the_editor_fit(string language, int windowWidth)
    {
        host.Show("widgets");
        var everything = EveryTile("Everything on one widget");
        string id = AddWidget(everything);
        // How wide the app's window leaves the page at this width.
        double pageWidth = Ui.Run(() =>
        {
            host.Window.Width = windowWidth;
            host.Window.UpdateLayout();
            return host.Window.PageHost.ActualWidth;
        });
        var pack = language == "es" ? Rigsight.Core.LanguagePack.Load(Path.Combine(Rigsight.Tests.Core.Repo.Root, "languages", "es.json")) : null;
        var problems = new List<string>();
        try
        {
            Ui.Run(() =>
            {
                using var _ = Rigsight.Core.Lang.UseHere(pack);
                var vm = new WidgetsViewModel(host.Shell.Settings, host.Client, host.Shell.Live);
                var view = new Rigsight.Views.WidgetsView { DataContext = vm };
                var window = new Window
                {
                    Content = view, Width = pageWidth, Height = 1300, Left = -6000, Top = -3000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.SetResourceReference(Window.BackgroundProperty, "BgBrush");
                try
                {
                    window.Show();
                    vm.PageShown = true;
                    vm.LoadMap();
                    vm.PickedId = id;
                    DoEvents();
                    window.UpdateLayout();
                    problems.AddRange(CutOff(view).Select(p => "page: " + p));
                    Ui.SavePng(view, Path.Combine(Shots, $"page-{language}-{windowWidth}.png"));
                    // The pop-ups' insides, drawn in a window of their own (an open pop-up would show on the screen).
                    void PopupInside(System.Windows.Controls.Primitives.Popup popup, string what)
                    {
                        var inside = (FrameworkElement)popup.Child;
                        popup.Child = null;
                        var holder = new Window { Content = inside, DataContext = popup.DataContext, SizeToContent = SizeToContent.WidthAndHeight, Left = -6000, Top = -3000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                        holder.SetResourceReference(Window.BackgroundProperty, "BgBrush");
                        try
                        {
                            holder.Show();
                            DoEvents();
                            holder.UpdateLayout();
                            var trimmedBefore = Trimmed.ToHashSet();
                            problems.AddRange(CutOff(inside).Select(p => $"{what}: " + p));
                            // What each widget or tile shows is said in a sentence: it's read whole, not cut off with "…".
                            problems.AddRange(Trimmed.Except(trimmedBefore).Select(t => $"{what}: \"{t}\" is cut short"));
                            Ui.SavePng(inside, Path.Combine(Shots, $"{what}-{language}-{windowWidth}.png"));
                        }
                        finally
                        {
                            holder.Content = null;
                            holder.Close();
                            popup.Child = inside;
                        }
                    }
                    var popups = Visuals.Descendants<System.Windows.Controls.Primitives.Popup>(view).ToList();
                    PopupInside(popups.Single(p => p.PlacementTarget is Button { Name: "AddWidgetButton" }), "add-widget");
                    vm.Edit(id);
                    DoEvents();
                    window.UpdateLayout();
                    var ed = vm.Editor!;
                    PopupInside(Visuals.Descendants<System.Windows.Controls.Primitives.Popup>(view).Single(p => p.PlacementTarget is Button { Name: "AddTileButton" }), "add-tile");
                    foreach (var tab in new[] { "look", "shape", "desktop" })
                    {
                        ed.PanelTab = tab;
                        DoEvents();
                        window.UpdateLayout();
                        problems.AddRange(CutOff(view).Select(p => $"editor, {tab}: " + p));
                        Ui.SavePng(view, Path.Combine(Shots, $"editor-{language}-{windowWidth}-{tab}.png"));
                    }
                    for (int i = 0; i < ed.TileBoxes.Count; i++)
                    {
                        ed.PickTileCommand.Execute(ed.TileBoxes[i]);
                        DoEvents();
                        window.UpdateLayout();
                        problems.AddRange(CutOff(view).Select(p => $"editor, {ed.TileName}: " + p));
                        if (windowWidth == 1140) Ui.SavePng(view, Path.Combine(Shots, $"editor-{language}-{windowWidth}-tile{i:00}.png"));
                    }
                    vm.CloseEditorCommand.Execute(null);
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            Ui.Run(() => host.Window.Width = 1440);
            Drop(id);
        }
        File.WriteAllLines(Path.Combine(Shots, $"trimmed-{language}-{windowWidth}.txt"), Trimmed);
        Trimmed.Clear();
        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
        NoProblemsButTheDatePickers("The_page_and_the_editor_fit");
    }
    // The editor in the real window, each tile picked in turn, drawn to be looked at (segments lit, the day box, lists).
    [Fact]
    public void Pictures_of_every_tile_picked()
    {
        host.Show("widgets");
        string id = AddWidget(EveryTile("Pictures"));
        try
        {
            Ui.Run(() => host.Shell.Widgets.Edit(id));
            Ui.Pump(500);
            var ed = Ui.Run(() => host.Shell.Widgets.Editor!);
            int n = Ui.Run(() => ed.TileBoxes.Count);
            for (int i = 0; i < n; i++)
            {
                Ui.Run(() => ed.PickTileCommand.Execute(ed.TileBoxes[i]));
                Ui.Pump(200);
                Ui.Run(() => Visuals.Descendants<ScrollViewer>(host.Window).First(v => v.Name == "EditorScroll").ScrollToEnd());
                Ui.Pump(200);
                string name = Ui.Run(() => ed.TileName);
                Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, $"host-tile-{i:00}-{name.Replace(' ', '-')}.png")));
            }
            NoProblemsButTheDatePickers("every tile picked");
        }
        finally { Drop(id); }
    }
    // The boxes to type into on a tile (a to-do item, a town for the weather, a web address) say what goes in them while
    // they're empty: each has its hint in Tag, as the search boxes do.
    [Fact]
    public void Empty_boxes_on_a_tile_say_what_goes_in_them()
    {
        host.Show("widgets");
        string id = AddWidget(EveryTile("Hints"));
        var missing = new List<string>();
        try
        {
            Ui.Run(() => host.Shell.Widgets.Edit(id));
            Ui.Pump(400);
            var ed = Ui.Run(() => host.Shell.Widgets.Editor!);
            foreach (var (kind, box) in new[] { (WidgetTiles.Todo, "NewTodoBox"), (WidgetTiles.Weather, "PlaceBox"), (WidgetTiles.Shortcuts, "NewLinkBox") })
            {
                Ui.Run(() => ed.SelectTile(ed.TileBoxes.First(b => b.Glyph == WidgetEditorViewModel.GlyphOf(kind)).Index));
                Ui.Pump(250);
                Ui.Run(() =>
                {
                    var tb = Visuals.Descendants<TextBox>(host.Window).Single(t => t.Name == box);
                    string hint = tb.Tag as string ?? "";
                    bool shown = Visuals.Descendants<TextBlock>(tb).Any(t => t.IsVisible && t.Text == hint);
                    if (!shown) missing.Add($"{box}: \"{hint}\" isn't shown in the empty box");
                });
            }
        }
        finally { Drop(id); }
        Assert.True(missing.Count == 0, string.Join("\n", missing));
        NoProblemsButTheDatePickers("Empty_boxes_on_a_tile_say_what_goes_in_them");
    }
    // Remove on the page: the widget goes, nothing else is picked (so a second click can't take another), Undo puts it
    // back where it was and picks it; left alone, Undo goes after a few seconds and the first widget is picked. The list
    // shows the picked widget lit throughout, and the quick settings show what the picked one has.
    [Fact]
    public void Remove_then_Undo_and_Undo_running_out()
    {
        host.Show("widgets");
        var vm = Ui.Run(() => host.Shell.Widgets);
        var a = EveryTile("Remove me");
        var b = EveryTile("Keep me");
        b.Scale = 1.25;
        b.Theme = WidgetTheme.Grey;
        AddWidget(a);
        AddWidget(b);
        Ui.Pump(300);
        string beforeA = Saved(a.Id), beforeB = Saved(b.Id);
        var list = Ui.Run(() => Visuals.Descendants<ListBox>(host.Window).Single(l => l.Name == "MineList"));
        try
        {
            Ui.Run(() => vm.PickedId = a.Id);
            Ui.Pump(200);
            int index = Ui.Run(() => host.Shell.Settings.Current.Widgets.FindIndex(w => w.Id == a.Id));
            Ui.Run(() => vm.Picked!.RemoveCommand.Execute(null));
            Ui.Pump(250);
            Assert.Null(Ui.Run(() => vm.Picked));
            Assert.True(Ui.Run(() => vm.HasRemoved));
            Assert.Equal("null", Saved(a.Id));
            Assert.Null(Ui.Run(() => list.SelectedItem));
            Ui.Run(() => vm.Removed!.UndoCommand.Execute(null));
            Ui.Pump(250);
            Assert.Equal(beforeA, Saved(a.Id));
            Assert.Equal(index, Ui.Run(() => host.Shell.Settings.Current.Widgets.FindIndex(w => w.Id == a.Id)));
            Assert.Equal(a.Id, Ui.Run(() => vm.PickedId));
            Assert.Same(Ui.Run(() => vm.Picked), Ui.Run(() => list.SelectedItem));
            Assert.Empty(Ui.Run(() => ShownWrong(host.Window)));

            // Picked one after another, the list and the quick settings follow.
            foreach (var id in new[] { b.Id, a.Id, b.Id })
            {
                Ui.Run(() => vm.PickedId = id);
                Ui.Pump(150);
                Assert.Same(Ui.Run(() => vm.Picked), Ui.Run(() => list.SelectedItem));
                Assert.Empty(Ui.Run(() => ShownWrong(host.Window)));
            }
            Assert.Equal(beforeB, Saved(b.Id));

            // Removed and left: after six seconds Undo is gone, the widget stays gone, the first one is picked.
            Ui.Run(() => vm.PickedId = a.Id);
            Ui.Run(() => vm.Picked!.RemoveCommand.Execute(null));
            var removed = Ui.Run(() => vm.Removed!);
            Assert.True(Ui.WaitFor(() => vm.Removed is null, 9000), "Undo didn't go");
            Ui.Pump(200);
            Assert.Equal("null", Saved(a.Id));
            Assert.DoesNotContain(removed, Ui.Run(() => vm.Grid.ToList()));
            Assert.Same(Ui.Run(() => vm.Cards[0]), Ui.Run(() => vm.Picked));
            Assert.Same(Ui.Run(() => vm.Picked), Ui.Run(() => list.SelectedItem));
            // Its Undo, clicked late (the row was still there a moment), brings nothing back.
            Ui.Run(() => removed.UndoCommand.Execute(null));
            Ui.Pump(150);
            Assert.Equal("null", Saved(a.Id));
            NoProblemsButTheDatePickers("remove and undo");
        }
        finally { Drop(a.Id, b.Id); }
    }
    // A shortcut left waiting for its keys ("Press the keys") and then a widget opened in the editor: typing in the editor
    // types (the page took every key as the shortcut, so a name or a note couldn't be typed, and a key combination
    // pressed there became the shortcut).
    [Fact]
    public void A_shortcut_left_waiting_doesnt_take_the_editors_keys()
    {
        host.Show("widgets");
        var vm = Ui.Run(() => host.Shell.Widgets);
        string id = AddWidget(EveryTile("Keys"));
        try
        {
            Ui.Run(() => vm.RecordShortcutCommand.Execute("shown"));
            Ui.Run(() => vm.Edit(id));
            Ui.Pump(400);
            bool eaten = Ui.Run(() =>
            {
                var name = Visuals.Descendants<TextBox>(host.Window).Single(t => t.Name == "NameBox");
                var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(name)!, 0, Key.A) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                name.RaiseEvent(e);
                return e.Handled;
            });
            Assert.False(eaten, "The A key typed in the widget's name was taken as the shortcut");
            Assert.Null(Ui.Run(() => vm.Recording));
        }
        finally
        {
            Ui.Run(() => vm.Recording = null);
            Drop(id);
        }
        NoProblemsButTheDatePickers("A_shortcut_left_waiting_doesnt_take_the_editors_keys");
    }
    // Typed into a box that's saved when it's left (a note, a reading's short name, the widget's name), then a press
    // somewhere that doesn't take the keyboard (another tile in the list, Whole widget, Back): what was typed is kept.
    [Fact]
    public void Typing_then_clicking_away_keeps_what_was_typed()
    {
        host.Show("widgets");
        var cfg = EveryTile("Typed");
        string id = AddWidget(cfg);
        try
        {
            Ui.Run(() => host.Shell.Widgets.Edit(id));
            Ui.Pump(400);
            var ed = Ui.Run(() => host.Shell.Widgets.Editor!);
            int Index(string kind) => Ui.Run(() => ed.TileBoxes.First(b => b.Glyph == WidgetEditorViewModel.GlyphOf(kind)).Index);
            // Pressed on a button in the tile list (it doesn't take the keyboard), and its click.
            void PressThenClick(int tile) => Ui.Run(() =>
            {
                var button = Visuals.Descendants<Button>(host.Window).First(b => b.DataContext is TileBox t && t.Index == tile && b.Command == ed.PickTileCommand);
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = button });
                button.Command.Execute(button.CommandParameter);
            });
            TextBox Box(Func<TextBox, bool> which) => Visuals.Descendants<TextBox>(host.Window).Single(t => t.IsVisible && which(t));

            int notes = Index(WidgetTiles.Notes), reading = Index(WidgetTiles.Reading);
            Ui.Run(() => ed.SelectTile(notes));
            Ui.Pump(200);
            Ui.Run(() => Box(t => t.AcceptsReturn).Text = "Typed into the note");
            PressThenClick(reading);
            Ui.Pump(200);
            Assert.Equal("Typed into the note", Ui.Run(() => host.Shell.Settings.Current.Widgets.Single(w => w.Id == id).Tiles![notes].Text));

            Ui.Run(() => Box(t => t.MaxLength == 18).Text = "Fast");
            PressThenClick(notes);
            Ui.Pump(200);
            Assert.Equal("Fast", Ui.Run(() => host.Shell.Settings.Current.Widgets.Single(w => w.Id == id).Tiles![reading].Label));

            Ui.Run(() => Box(t => t.Name == "NameBox").Text = "Renamed");
            Ui.Run(() =>
            {
                var back = Visuals.Descendants<Button>(host.Window).First(b => b.Command == ed.BackCommand);
                back.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent, Source = back });
                back.Command.Execute(null);
            });
            Ui.Pump(200);
            Assert.Equal("Renamed", Ui.Run(() => host.Shell.Settings.Current.Widgets.Single(w => w.Id == id).Name));
        }
        finally { Drop(id); }
        NoProblemsButTheDatePickers("Typing_then_clicking_away_keeps_what_was_typed");
    }
    // A widget picked on the map while the list's search or chips hide it: the page settles (the list can't light a row
    // it doesn't show, and the page kept telling it to, over and over).
    [Fact]
    public void Picking_on_the_map_a_widget_the_list_hides()
    {
        host.Show("widgets");
        var vm = Ui.Run(() => host.Shell.Widgets);
        var shown = EveryTile("Shown one");
        var hidden = EveryTile("Hidden one");
        AddWidget(shown);
        AddWidget(hidden);
        try
        {
            Ui.Run(() => vm.MineQuery = "Shown one");
            Ui.Pump(300);
            int told = 0;
            Ui.Run(() =>
            {
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WidgetsViewModel.PickedRow)) told++; };
                vm.PickedId = hidden.Id; // as a click on it on the map does
            });
            Ui.Pump(300);
            int settled = Ui.Run(() => told);
            Ui.Pump(1000);
            int later = Ui.Run(() => told) - settled;
            Assert.True(later == 0, $"The list was told of its pick {later} more times in a second, with nothing changing");
            NoProblemsButTheDatePickers("picked on the map, hidden in the list");
        }
        finally
        {
            Ui.Run(() => vm.MineQuery = "");
            Drop(shown.Id, hidden.Id);
        }
    }
    // Place in the editor (the nine spots) sends the widget to a corner of the screen it's on, at the size it is there:
    // the agent draws it at that screen's scale, not the scale of the screen the app's window is on (a laptop at 150%
    // beside a monitor at 100%). Here the app's window is said to be on a screen at twice this one's scale.
    [Fact]
    public void Place_uses_the_scale_of_the_widgets_own_screen()
    {
        host.Show("widgets");
        var primary = System.Windows.Forms.Screen.PrimaryScreen!;
        var cfg = WidgetsViewModel.NewWidget("Corner", [WidgetTiles.New(WidgetTiles.Kinds.First(k => k.Id == WidgetTiles.Clock))]);
        (cfg.X, cfg.Y) = (primary.Bounds.Left + 100, primary.Bounds.Top + 100);
        string id = AddWidget(cfg);
        try
        {
            double own = Ui.Run(() => ScreenChoice.ReadAll(null).Single(m => m.IsMain).Scale);
            Ui.Run(() => host.Shell.Widgets.Edit(id));
            Ui.Pump(300);
            var ed = Ui.Run(() => host.Shell.Widgets.Editor!);
            Ui.Run(() => { ed.ScreenScale = own * 2; ed.PlaceCommand.Execute("8"); });
            Ui.Pump(200);
            var saved = Ui.Run(() => host.Shell.Settings.Current.Widgets.Single(w => w.Id == id));
            var (pw, ph) = Rigsight.Services.WidgetPictures.SizeOf(saved);
            var area = primary.WorkingArea;
            int w = (int)Math.Round(pw * own * saved.Scale), h = (int)Math.Round(ph * own * saved.Scale), gap = (int)Math.Round(12 * own);
            Assert.Equal((area.Right - gap - w, area.Bottom - gap - h), (saved.X!.Value, saved.Y!.Value));
            // And the spot it's at is the one lit.
            Assert.True(Ui.Run(() => ed.PlaceSpots[8].IsActive), "Bottom right isn't lit");
        }
        finally { Drop(id); }
        NoProblemsButTheDatePickers("Place_uses_the_scale_of_the_widgets_own_screen");
    }
    // Forty widgets added from the pop-up one after another: each is picked and shown in the list, the counts follow,
    // and adding one doesn't get slower as the list grows.
    [Fact]
    public void Adding_many_widgets()
    {
        host.Show("widgets");
        var vm = Ui.Run(() => host.Shell.Widgets);
        var added = new List<string>();
        var times = new List<long>();
        var list = Ui.Run(() => Visuals.Descendants<ListBox>(host.Window).Single(l => l.Name == "MineList"));
        try
        {
            var choices = Ui.Run(() => vm.Choices.ToList());
            for (int i = 0; i < 40; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Ui.Run(() =>
                {
                    vm.OpenAddListCommand.Execute(null);
                    choices[i % choices.Count].AddCommand.Execute(null);
                    added.Add(vm.PickedId!);
                });
                Ui.Pump(30);
                times.Add(sw.ElapsedMilliseconds);
                Assert.Same(Ui.Run(() => vm.Picked), Ui.Run(() => list.SelectedItem));
                Assert.False(Ui.Run(() => vm.AddListOpen));
            }
            Assert.Equal(Ui.Run(() => vm.Cards.Count), Ui.Run(() => vm.MineChips[0].Count));
            Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "page-many-widgets.png")));
            double first = times.Take(5).Average(), last = times.TakeLast(5).Average();
            File.WriteAllText(Path.Combine(Shots, "adding-times.txt"), string.Join(", ", times));
            Assert.True(last < Math.Max(first * 3, 250), $"Adding got slower: {first:0} ms at first, {last:0} ms after 35 ({string.Join(", ", times)})");
            NoProblemsButTheDatePickers("adding many widgets");
        }
        finally { Drop([.. added]); }
    }
    // The page and the editor in every theme: every remove is drawn red (its outline and words the theme's red) and the
    // picked tile's settings read; pictures of a few to be looked at.
    [Fact]
    public void Removes_are_red_in_every_theme()
    {
        host.Show("widgets");
        var vm = Ui.Run(() => host.Shell.Widgets);
        var cfg = EveryTile("Themes");
        string id = AddWidget(cfg);
        var problems = new List<string>();
        try
        {
            foreach (var theme in ThemeCatalog.All)
            {
                Ui.Run(() => { Rigsight.Services.ThemeManager.Apply(theme.Key); vm.PickedId = id; });
                Ui.Pump(150);
                Ui.Run(() =>
                {
                    var hot = ((System.Windows.Media.SolidColorBrush)Application.Current.Resources["HotBrush"]).Color;
                    foreach (var b in Visuals.Descendants<Button>(host.Window).Where(b => b.IsVisible && b.Style is { } st && (st == (Style)Application.Current.FindResource("DangerButton") || st == (Style)Application.Current.FindResource("DangerIconButton"))))
                        if (b.BorderBrush is not System.Windows.Media.SolidColorBrush { Color: var c } || c != hot) problems.Add($"{theme.Key}: {AutomationProperties_Name(b)} isn't outlined red");
                    if (theme.Key is "light" or "contrast" or "neon" or "paper") Ui.SavePng(host.Window, Path.Combine(Shots, $"theme-{theme.Key}-page.png"));
                });
                if (theme.Key is "light" or "contrast" or "neon" or "paper")
                {
                    Ui.Run(() => vm.Edit(id));
                    Ui.Pump(300);
                    Ui.Run(() =>
                    {
                        var ed = vm.Editor!;
                        ed.SelectTile(ed.TileBoxes.First(b => b.Glyph == WidgetEditorViewModel.GlyphOf(WidgetTiles.Todo)).Index);
                    });
                    Ui.Pump(200);
                    Ui.Run(() =>
                    {
                        Visuals.Descendants<ScrollViewer>(host.Window).First(v => v.Name == "EditorScroll").ScrollToEnd();
                    });
                    Ui.Pump(200);
                    Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, $"theme-{theme.Key}-editor.png")));
                    Ui.Run(() => vm.CloseEditorCommand.Execute(null));
                    Ui.Pump(150);
                }
            }
        }
        finally
        {
            Ui.Run(() => Rigsight.Services.ThemeManager.Apply(Rigsight.Services.ThemeManager.Dark));
            Drop(id);
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
        NoProblemsButTheDatePickers("Removes_are_red_in_every_theme");
    }
    // The map with a second screen to the left of the main one and higher up (negative coordinates), at another scale:
    // a widget there is drawn where it is, and dragged, it lands where it was dropped, in that screen's pixels.
    [Fact]
    public void The_map_with_a_screen_left_of_and_above_the_main_one()
    {
        _ = host;
        var (moved, centre, after) = Ui.Run(() =>
        {
            var map = new WidgetScreens
            {
                MapHeight = 120, Snap = false, Width = 400,
                Screens = [new(@"\\.\DISPLAY1", 0, 0, 2560, 1440, 1, true, false), new(@"\\.\DISPLAY2", -1920, -300, 1920, 1080, 1.5, false, false)],
                Spots = [new WidgetSpot("w", "Clock", -1500, -100, 300, 120, null)],
            };
            var window = new Window { Content = map, SizeToContent = SizeToContent.WidthAndHeight, Left = -6000, Top = -3000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.SetResourceReference(Window.BackgroundProperty, "BgBrush");
            (string Id, int X, int Y)? got = null;
            map.Moved += (id, x, y) => got = (id, x, y);
            try
            {
                window.Show();
                window.UpdateLayout();
                var c = map.CentreOf("w")!.Value;
                Ui.SavePng(map, Path.Combine(Shots, "map-negative.png"));
                // The map's scale: 4480 pixels across 392 (its width less 8).
                double scale = Math.Min(392.0 / 4480, 120.0 / 1740);
                Assert.True(map.PickUp(c));
                map.DragTo(new Point(c.X + 100 * scale, c.Y + 50 * scale));
                map.Drop();
                return (got, c, scale);
            }
            finally { window.Close(); }
        });
        Assert.NotNull(moved);
        Assert.Equal("w", moved!.Value.Id);
        Assert.InRange(moved.Value.X, -1402, -1398);
        Assert.InRange(moved.Value.Y, -52, -48);
        // Its middle on the map is where it is: 420 pixels right of the map's left (-1920) and 200 down from its top (-300).
        Assert.InRange(centre.X, (4 + (-1500 + 150 + 1920) * after) - 1, (4 + (-1500 + 150 + 1920) * after) + 1 + (400 - 8 - 4480 * after) / 2);
        NoProblemsButTheDatePickers("The_map_with_a_screen_left_of_and_above_the_main_one");
    }
}
