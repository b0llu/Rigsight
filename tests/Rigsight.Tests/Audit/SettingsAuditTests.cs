using System.Text.Json.Nodes;
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

    // A change made in the app waits 350 ms before it's sent. A copy from the agent that came meanwhile (a widget changed
    // from its menu on the desktop, a to-do ticked there, a timer that ran out) was dropped, and the app's copy sent after
    // it put all of that back. The app's change is now made on that copy.
    [Fact]
    public void What_changed_on_the_desktop_while_the_app_waits_to_send_its_own_change_is_kept()
    {
        using var link = new AgentLink();
        string themed = "", hidden = "", removed = "";
        bool snap = false;
        Ui.Run(() =>
        {
            var widgets = link.Settings.Current.Widgets;
            (themed, hidden, removed, snap) = (widgets[0].Id, widgets[1].Id, widgets[2].Id, !link.Settings.Current.WidgetSnap);
            link.Settings.Update(s =>
            {
                s.WidgetSnap = snap;
                s.Widgets.RemoveAll(w => w.Id == removed);
            });
            // The agent's copy, changed on the desktop before the app's change reached it.
            var agents = SettingsStore.Deserialize(SettingsStore.Serialize(link.Agent.Settings));
            agents.Widgets.Single(w => w.Id == themed).Theme = WidgetTheme.Light;
            agents.Widgets.Single(w => w.Id == hidden).Enabled = !agents.Widgets.Single(w => w.Id == hidden).Enabled;
            bool on = agents.Widgets.Single(w => w.Id == hidden).Enabled;
            link.Settings.ApplyFromAgent(agents);
            Assert.Equal((WidgetTheme.Light, on, snap), (link.Settings.Current.Widgets.Single(w => w.Id == themed).Theme,
                link.Settings.Current.Widgets.Single(w => w.Id == hidden).Enabled, link.Settings.Current.WidgetSnap));
            Assert.DoesNotContain(link.Settings.Current.Widgets, w => w.Id == removed);
            link.Settings.Flush();
        });
        Assert.True(Ui.WaitFor(() => link.Agent.Received.Any(m => m.T == "settings"), 5000), "nothing was sent");
        Ui.Pump(200);
        var sent = link.Agent.Settings;
        Assert.Equal(WidgetTheme.Light, sent.Widgets.Single(w => w.Id == themed).Theme);
        Assert.Equal(snap, sent.WidgetSnap);
        Assert.DoesNotContain(sent.Widgets, w => w.Id == removed);
    }

    // Lists of things with an identifier go by it: what one side added, took away or changed is kept with the other's.
    [Fact]
    public void Widgets_are_merged_by_their_identifier()
    {
        static JsonNode List(params (string Id, string Name)[] widgets) =>
            new JsonArray([.. widgets.Select(w => (JsonNode)new JsonObject { ["Id"] = w.Id, ["Name"] = w.Name })]);
        static string Text(JsonNode? list) => string.Join(" ", list!.AsArray().Select(w => $"{w!["Id"]}={w["Name"]}"));

        var from = List(("a", "A"), ("b", "B"), ("c", "C"), ("d", "D"));
        // Here: b taken away, c renamed, e added. There: a renamed, d taken away, f added.
        var to = List(("a", "A"), ("c", "C2"), ("d", "D"), ("e", "E"));
        var now = List(("a", "A2"), ("b", "B"), ("c", "C"), ("f", "F"));
        Assert.Equal("a=A2 c=C2 e=E f=F", Text(SettingsMerge.Merge(from, to, now)));
        // Taken away there but changed here: kept.
        Assert.Equal("a=A d=D2", Text(SettingsMerge.Merge(List(("a", "A"), ("d", "D")), List(("a", "A"), ("d", "D2")), List(("a", "A")))));
        // A list without identifiers, of the same length, item by item; made longer here, taken whole.
        var numbers = new JsonArray(1, 2, 3);
        Assert.Equal("[1,5,4]", SettingsMerge.Merge(numbers, new JsonArray(1, 5, 3), new JsonArray(1, 2, 4))!.ToJsonString());
        Assert.Equal("[1,2,3,9]", SettingsMerge.Merge(numbers, new JsonArray(1, 2, 3, 9), new JsonArray(1, 2, 4))!.ToJsonString());
    }

    // Duplicate copied the widget as the editor had it when it opened or last changed it: one moved, typed in or ticked
    // on the desktop meanwhile came out as it was before, and beside its old spot.
    [Fact]
    public void Duplicate_copies_the_widget_as_it_is_now()
    {
        using var link = new AgentLink();
        var vm = WidgetsPage(link);
        Ui.Run(() =>
        {
            vm.Add(vm.Choices.Single(c => c.Title == "Notes"));
            string id = link.Settings.Current.Widgets[^1].Id;
            vm.Edit(id);
            link.Settings.Update(s =>
            {
                var w = s.Widgets.Single(w => w.Id == id);
                (w.X, w.Y) = (600, 300);
                w.Tiles![0].Text = "Typed on the desktop";
            });
            vm.Editor!.DuplicateCommand.Execute(null);
            var copy = link.Settings.Current.Widgets[^1];
            Assert.NotEqual(id, copy.Id);
            Assert.Equal(("Typed on the desktop", 624, 324), (copy.Tiles![0].Text, copy.X, copy.Y));
        });
    }
}
