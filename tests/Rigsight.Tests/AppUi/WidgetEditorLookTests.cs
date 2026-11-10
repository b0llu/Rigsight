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
