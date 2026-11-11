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
}
