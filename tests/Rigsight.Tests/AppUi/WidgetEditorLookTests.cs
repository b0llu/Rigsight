using System.Windows;
using Rigsight.Controls;
using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.AppUi;

/// <summary>The Widgets page in the real window: the editor as a picture, and a widget dragged on the map of the screens.</summary>
[Collection("UI")]
public sealed class WidgetEditorLookTests(AppHost host) : IClassFixture<AppHost>
{
    private static readonly string Shots = Path.Combine(TestEnvironment.DataDir, "screens");

    [Fact]
    public void The_editor_as_it_looks()
    {
        host.Show("widgets");
        Ui.Run(() =>
        {
            var cfg = WidgetsViewModel.NewWidget("In use", [WidgetTiles.New(WidgetTiles.Kinds.First(k => k.Id == WidgetTiles.NowPlaying))]);
            host.Shell.Settings.Update(s => s.Widgets.Add(cfg));
            host.Shell.Widgets.Edit(cfg.Id);
        });
        Ui.Pump(800);
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "widget-editor.png")));
        Ui.Run(() => host.Shell.Widgets.CloseEditorCommand.Execute(null));
    }

    // The editor as in the approved mock-up CqfxQo4cR6vCG3GxFx1VMT: nothing picked (the whole widget), the timer picked, the
    // to-do list picked; for comparing with the mock-up's boards.
    [Fact]
    public void The_editor_with_nothing_then_a_tile_picked()
    {
        host.Show("widgets");
        Ui.Run(() =>
        {
            var cfg = WidgetsViewModel.NewWidget("Desk",
            [
                new() { Kind = WidgetTiles.Clock, W = 6, H = 2 },
                new() { Kind = WidgetTiles.Timer, X = 6, W = 6, H = 2 },
                new() { Kind = WidgetTiles.Notes, Y = 2, W = 6, H = 5, Text = "BIOS update on Saturday\nReturn the spare fan\nAsk about the 1440p monitor" },
                new() { Kind = WidgetTiles.Todo, X = 6, Y = 2, W = 6, H = 5, Todos = [new() { Text = "Back up the E drive", Done = true }, new() { Text = "Clean the dust filters" }, new() { Text = "Update the graphics driver" }] },
            ]);
            host.Shell.Settings.Update(s => s.Widgets.Add(cfg));
            host.Shell.Widgets.Edit(cfg.Id);
        });
        Ui.Pump(800);
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "editor-1-nothing.png")));
        var ed = Ui.Run(() => host.Shell.Widgets.Editor!);
        Assert.False(Ui.Run(() => ed.HasTile));
        Ui.Run(() => ed.PickTileCommand.Execute(ed.TileBoxes[1]));
        Ui.Pump(400);
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "editor-2-timer.png")));
        Ui.Run(() => ed.PickTileCommand.Execute(ed.TileBoxes[3]));
        Ui.Pump(400);
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "editor-3-todo.png")));
        Ui.Run(() => { ed.WholeWidgetCommand.Execute(null); ed.PanelTab = "desktop"; });
        Ui.Pump(400);
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "editor-4-desktop-tab.png")));
        Ui.AssertNoProblems("the editor");
        Ui.Run(() => host.Shell.Widgets.CloseEditorCommand.Execute(null));
    }

    // The reading list, as its pop-up shows it (drawn on its own: a pop-up is a window of its own).
    [Fact]
    public void The_reading_picker_as_it_looks()
    {
        host.Show("widgets");
        Ui.Run(() =>
        {
            var cfg = WidgetsViewModel.NewWidget("Reading", [WidgetTiles.New(WidgetTiles.Kinds.First(k => k.Id == WidgetTiles.Reading))]);
            host.Shell.Settings.Update(s => s.Widgets.Add(cfg));
            host.Shell.Widgets.Edit(cfg.Id);
            var ed = host.Shell.Widgets.Editor!;
            ed.OpenReadingPickerCommand.Execute("tile");
            var view = Visuals.Descendants<Rigsight.Views.WidgetsView>(host.Window).First();
            var list = new System.Windows.Controls.ContentControl { Content = ed, ContentTemplate = (DataTemplate)view.FindResource("ReadingPickerList") };
            var window = new Window { Content = list, SizeToContent = SizeToContent.WidthAndHeight, Left = -3000, Top = -3000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.SetResourceReference(Window.BackgroundProperty, "BgBrush");
            window.Show();
            window.UpdateLayout();
            Ui.SavePng(list, Path.Combine(Shots, "widget-reading-picker.png"));
            window.Close();
            ed.ReadingPickerOpen = false;
            host.Shell.Widgets.CloseEditorCommand.Execute(null);
        });
    }

    // The user, 2026-11-10: "i still cant drag on preview to change where the widget is placed". Dragged .
    [Fact]
    public void A_widget_dragged_on_the_map_gets_its_new_spot()
    {
        host.Show("widgets");
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var cfg = WidgetsViewModel.NewWidget("Clock", [WidgetTiles.New(WidgetTiles.Kinds.First(k => k.Id == WidgetTiles.Clock))]);
        (cfg.X, cfg.Y) = (screen.Left + screen.Width / 2, screen.Top + screen.Height / 3);
        Ui.Run(() =>
        {
            host.Shell.Settings.Update(s => { s.WidgetsShown = true; s.WidgetSnap = false; s.Widgets.Add(cfg); });
            host.Shell.Widgets.LoadMap();
        });
        Ui.Pump(600);
        var map = Ui.Run(() => Visuals.Descendants<WidgetScreens>(host.Window).Single());
        Ui.Run(() => map.BringIntoView());
        Ui.Pump(400);
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "widget-map-before.png")));

        var trail = new List<string>();
        var from = Ui.Run(() => map.CentreOf(cfg.Id)) ?? throw new Xunit.Sdk.XunitException("The widget isn't on the map");
        trail.Add($"centre {from}");
        // What's under the widget on the map takes the press: the map itself, nothing over it.
        Assert.Same(map, Ui.Run(() => host.Window.InputHitTest(map.TranslatePoint(from, host.Window))));
        Ui.Run(() =>
        {
            Assert.True(map.PickUp(from));
            for (int i = 1; i <= 6; i++) map.DragTo(new Point(from.X + i * 10, from.Y + i * 4));
            map.Drop();
        });
        Ui.Pump(400);
        Ui.Run(() => Ui.SavePng(host.Window, Path.Combine(Shots, "widget-map-after.png")));

        var saved = Ui.Run(() => host.Shell.Settings.Current.Widgets.Single(w => w.Id == cfg.Id));
        Assert.True(saved.X > cfg.X && saved.Y > cfg.Y, $"Saved at {saved.X},{saved.Y}, it was at {cfg.X},{cfg.Y}. " + string.Join(" | ", trail));
        // And sent to the agent as a move: settings from the app never move a widget on the desktop.
        Assert.True(Ui.WaitFor(() => host.Agent.Commands(WidgetCatalog.MoveCommand).Any(m => m.Arg == WidgetCatalog.MoveArg(cfg.Id, saved.X!.Value, saved.Y!.Value)), 5000),
            "No move sent to the agent");
    }
}
