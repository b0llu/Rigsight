using Rigsight.Core.Settings;
using Rigsight.Tests.App;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.Audit;

/// <summary>
/// The widgets' settings on their way between the Widgets page, its editor and the agent (audit, 2026-11-11): nothing the
/// person has, nor anything done on the desktop, is lost on the way.
/// </summary>
[Collection("UI")]
public sealed class SettingsAuditTests
{
    private static WidgetsViewModel WidgetsPage(AgentLink link) => Ui.Run(() => new WidgetsViewModel(link.Settings, link.Client));

    // The owner lost all 13 widgets at 10:59:47 (agent log: "widgets on: 6 -> 5", then "5 -> 0", both from the app): the
    // pane's Remove picked the next widget under the same button, so each further click (a double click, a click again
    // because the pane still showed a widget, Enter held on the focused button) took another away, and the 350 ms wait
    // before sending put them all in one update. Every press now reaches only the widget the first one removed.
    [Fact]
    public void Quick_presses_on_the_panes_Remove_take_away_one_widget_and_the_agent_keeps_the_rest()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        int count = 0;
        Ui.Run(() =>
        {
            count = link.Settings.Current.Widgets.Count;
            Assert.True(count >= 3);
            vm.PickedRow = vm.Cards[0];
            for (int i = 0; i < 6; i++) vm.Picked?.RemoveCommand.Execute(null);
            Assert.Equal(count - 1, link.Settings.Current.Widgets.Count);
            Assert.Null(vm.Picked);
            Assert.True(vm.HasRemoved);
            link.Settings.Flush();
        });
        Assert.True(Ui.WaitFor(() => link.Agent.Settings.Widgets.Count == count - 1, 5000), "the agent didn't get the one removal");
        Ui.Pump(200);
        Assert.Equal(count - 1, link.Agent.Settings.Widgets.Count);
    }

    // The editor's Remove took a widget (its tiles, notes and to-dos) away with no way back, where the page's Remove has
    // Undo: it now leaves the page saying what was removed, with Undo.
    [Fact]
    public void Remove_in_the_editor_can_be_undone_like_the_pages_remove()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        Ui.Run(() =>
        {
            int count = link.Settings.Current.Widgets.Count;
            string id = vm.Cards[1].Id;
            vm.Edit(id);
            vm.Editor!.RemoveCommand.Execute(null);
            Assert.Null(vm.Editor);
            Assert.Equal(count - 1, link.Settings.Current.Widgets.Count);
            Assert.DoesNotContain(vm.Cards, c => c.Id == id);
            Assert.Null(vm.Picked);
            Assert.True(vm.HasRemoved);

            vm.Removed!.UndoCommand.Execute(null);
            Assert.Equal(count, link.Settings.Current.Widgets.Count);
            Assert.Equal(id, link.Settings.Current.Widgets[1].Id);
            Assert.Equal(id, vm.PickedId);

            // A new widget left empty is still dropped without a word.
            vm.CreateCommand.Execute(null);
            vm.Editor!.RemoveCommand.Execute(null);
            Assert.Equal(count, link.Settings.Current.Widgets.Count);
            Assert.False(vm.HasRemoved);
        });
    }

    // Undo in the editor brought back the whole widget as it was before the change it undoes: a note typed on the
    // desktop, a to-do ticked there and a timer started there went back too (a timer that had run out started again and
    // said it was up a second time). Undo and Redo now take back only what the change itself did.
    [Fact]
    public void Undo_in_the_editor_keeps_what_was_typed_ticked_or_timed_on_the_desktop()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        Ui.Run(() =>
        {
            vm.CreateCommand.Execute(null);
            var ed = vm.Editor!;
            WidgetConfig Saved() => link.Settings.Current.Widgets.Single(w => w.Id == ed.Id);
            WidgetTile Tile(string kind) => Saved().Tiles!.Single(t => t.Kind == kind);
            foreach (var kind in new[] { WidgetTiles.Notes, WidgetTiles.Todo, WidgetTiles.Timer })
                ed.AddTileCommand.Execute(ed.TileChoices.Single(c => c.Kind == kind));

            ed.Theme = nameof(WidgetTheme.Light);
            // Used on the desktop meanwhile.
            link.Settings.Update(s =>
            {
                var tiles = s.Widgets.Single(w => w.Id == ed.Id).Tiles!;
                tiles.Single(t => t.Kind == WidgetTiles.Notes).Text = "Call the shop";
                tiles.Single(t => t.Kind == WidgetTiles.Todo).Todos = [new TodoItem { Text = "Milk", Done = true }];
                tiles.Single(t => t.Kind == WidgetTiles.Timer).RunningSince = 1_800_000_000_000;
            });
            void Kept()
            {
                Assert.Equal("Call the shop", Tile(WidgetTiles.Notes).Text);
                Assert.Equal(("Milk", true), (Assert.Single(Tile(WidgetTiles.Todo).Todos!).Text, Tile(WidgetTiles.Todo).Todos![0].Done));
                Assert.Equal(1_800_000_000_000, Tile(WidgetTiles.Timer).RunningSince);
            }

            ed.UndoCommand.Execute(null);
            Assert.Equal(WidgetTheme.App, Saved().Theme);
            Kept();
            ed.RedoCommand.Execute(null);
            Assert.Equal(WidgetTheme.Light, Saved().Theme);
            Kept();

            // What the editor itself changed in a note is still undone.
            ed.SelectTile(Saved().Tiles!.FindIndex(t => t.Kind == WidgetTiles.Notes));
            ed.NoteText = "Call the shop at five";
            Assert.Equal("Call the shop at five", Tile(WidgetTiles.Notes).Text);
            ed.UndoCommand.Execute(null);
            Assert.Equal("Call the shop", Tile(WidgetTiles.Notes).Text);
            Assert.Equal(WidgetTheme.Light, Saved().Theme);
            vm.CloseEditorCommand.Execute(null);
        });
    }
}
