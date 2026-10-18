using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>The Games page's view model as the agent's messages reach it.</summary>
public class GamesViewModelTests
{
    [Fact]
    public void RivaTuner_starting_or_stopping_updates_the_frame_rate_box_once()
    {
        // 0.29.0 test copy: a single name went back to the page's own several-names helper, round and round, and the
        // app died of a stack overflow seconds after start, when the agent first said how RivaTuner was.
        var (settings, live) = Kit.Greeted();
        var (games, overlay) = Ui.Run(() =>
        {
            var overlay = new OverlayViewModel(settings, new AgentClient(Ui.Dispatcher), live);
            return (new GamesViewModel(new ReportService(settings), settings, live, overlay), overlay);
        });

        var changed = Ui.Run(() => Kit.Changes(games, () => overlay.RtssState = "running"));

        Assert.Equal([nameof(GamesViewModel.FpsEmpty)], changed);
    }
}

/// <summary>Settings changes as the pages make them.</summary>
public class SettingsNoChangeTests
{
    [Fact]
    public void A_change_that_changes_nothing_doesnt_make_every_page_refresh()
    {
        // Pages refresh on Changed; a control writing back the value it just read would loop until the stack ran out.
        var settings = Kit.OfflineSettings();
        int changed = 0;
        Ui.Run(() =>
        {
            settings.Changed += () => changed++;
            settings.Update(s => s.ExportAsCsv = s.ExportAsCsv);
            settings.Update(s => s.ExportAsCsv = !s.ExportAsCsv); // not Fahrenheit: that sets the unit for every test after this one
        });
        Assert.Equal(1, changed);
    }
}
