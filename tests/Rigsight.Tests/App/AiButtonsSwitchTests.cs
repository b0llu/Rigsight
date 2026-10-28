using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>Settings › Ask RigSight › AI buttons on pages (2026-10-28): shown by default, off hides every one at once.</summary>
[Collection("UI")]
public sealed class AiButtonsSwitchTests
{
    [Fact]
    public void The_switch_hides_every_AI_button_and_brings_them_back()
    {
        bool wasOn = AiGate.IsOn;
        try
        {
            var settings = Kit.OfflineSettings(new RigsightSettings());
            var ai = new AiSettingsViewModel(settings, new AskViewModel(settings));
            Assert.True(ai.PageButtons);
            Assert.True(AiGate.Shows(new AiQuestion("Why?", "crashes")));
            ai.PageButtons = false;
            Assert.False(settings.Current.Ai.PageButtons);
            Assert.False(AiGate.Shows(new AiQuestion("Why?", "crashes")));
            ai.PageButtons = true;
            Assert.True(AiGate.Shows(new AiQuestion("Why?", "crashes")));
        }
        finally { AiGate.Update(wasOn, _ => true, _ => { }); }
    }

    /// <summary>Explain pressed before the first question, then Not now: a later Start asking sends nothing it wasn't asked.</summary>
    [Fact]
    public void Not_now_drops_the_Explain_that_was_waiting()
    {
        bool wasOn = AiGate.IsOn;
        try
        {
            var settings = Kit.OfflineSettings(new RigsightSettings());
            var ask = new AskViewModel(settings);
            Assert.True(ask.ShowFirstQuestion);
            ask.Explain("The PC shut off suddenly");
            Assert.True(ask.IsOpen);
            ask.CloseCommand.Execute(null);
            ask.IsOpen = true;
            ask.StartAskingCommand.Execute(null);
            Assert.DoesNotContain(ask.Conversations, c => c.Title.StartsWith("Explain", StringComparison.Ordinal));
        }
        finally { AiGate.Update(wasOn, _ => true, _ => { }); }
    }
}
