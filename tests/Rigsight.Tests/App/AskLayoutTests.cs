using Rigsight.Core.Settings;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// Where the conversations go at each width: a wide window, stuck to the corner or popped out, has them as a column
/// beside the chat; a narrower one opens them over the chat from the button at the top left.
/// </summary>
public sealed class AskLayoutTests
{
    private static AskViewModel Window() =>
        new(Kit.OfflineSettings(new RigsightSettings()), Path.Combine(TestEnvironment.NewFolder("ask"), "conversations.json"));

    [Fact]
    public void A_narrow_window_opens_the_conversations_over_the_chat()
    {
        var w = Window();
        w.PanelWidth = 420;
        Assert.False(w.ShowHistoryColumn);
        w.ToggleHistoryCommand.Execute(null);
        Assert.True(w.ShowHistoryOver);
        Assert.True(w.HistoryPressed);
    }

    [Fact]
    public void A_wide_window_stuck_to_the_corner_has_the_column_too()
    {
        var w = Window();
        w.PanelWidth = 900;
        Assert.False(w.IsPoppedOut);
        Assert.True(w.ShowHistoryColumn);
        w.ToggleHistoryCommand.Execute(null);
        Assert.False(w.ShowHistoryColumn);
        Assert.False(w.ShowHistoryOver);
    }

    [Fact]
    public void Narrowed_below_the_column_it_works_over_the_chat()
    {
        var w = Window();
        w.IsPoppedOut = true;
        w.PanelWidth = 780;
        Assert.True(w.ShowHistoryColumn);

        w.PanelWidth = 640;
        Assert.False(w.ShowHistoryColumn);
        w.ToggleHistoryCommand.Execute(null);
        Assert.True(w.ShowHistoryOver);
    }
}
