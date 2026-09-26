using Rigsight.Core.Protocol;
using Rigsight.Core.Settings;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>When stepping aside is on for whom, and when (and until when) Settings is pointed at.</summary>
public class SensorNoticeTests
{
    private static SensorStatus Status(params (string Part, string[] By)[] paused) =>
        new() { Paused = [.. paused.Select(p => new SkippedSensors { Part = p.Part, Because = [.. p.By] })] };

    private static SensorStatus Safe(SensorStatus s)
    {
        s.SafeMode = true;
        return s;
    }

    [Fact]
    public void A_new_install_decides_at_its_first_start_and_an_update_never_turns_it_on()
    {
        // No file yet: off until the agent's first start has looked (and it hasn't).
        var fresh = SettingsStore.Deserialize("null");
        Assert.False(fresh.YieldToHardwareApps);
        Assert.False(fresh.HardwareAppsChecked);
        // Anyone updating from before 0.6.0: Rigsight already works there, so the check counts as done and it stays off.
        foreach (int version in new[] { 1, 5, 6 })
        {
            var old = SettingsStore.Deserialize($$"""{ "SettingsVersion": {{version}} }""");
            Assert.True(old.HardwareAppsChecked, $"v{version}");
            Assert.False(old.YieldToHardwareApps, $"v{version}");
        }
        // From 0.6.0 on the flags are kept as they are.
        var now = SettingsStore.Deserialize("""{ "SettingsVersion": 7, "HardwareAppsChecked": true, "YieldToHardwareApps": true }""");
        Assert.True(now.YieldToHardwareApps);
        var notYet = SettingsStore.Deserialize("""{ "SettingsVersion": 7 }""");
        Assert.False(notYet.HardwareAppsChecked);
    }

    [Fact]
    public void The_app_cant_undo_the_first_start_check()
    {
        var agents = new RigsightSettings { HardwareAppsChecked = true, YieldToHardwareApps = true };
        var app = new RigsightSettings { HardwareAppsChecked = false, YieldToHardwareApps = false };
        Rigsight.Agent.AgentContext.KeepAgentFields(app, agents);
        Assert.True(app.HardwareAppsChecked);
        Assert.False(app.YieldToHardwareApps); // the switch itself is the user's
    }

    [Fact]
    public void Each_program_and_each_problem_is_its_own_part()
    {
        Assert.Empty(SettingsViewModel.NoticeParts(null));
        Assert.Empty(SettingsViewModel.NoticeParts(new SensorStatus()));
        Assert.Equal(["Fan hubs:SignalRGB", "Fan hubs:iCUE", "Power supply:iCUE", "safe"],
            SettingsViewModel.NoticeParts(Safe(Status(("Fan hubs", ["SignalRGB", "iCUE"]), ("Power supply", ["iCUE"])))).Order(StringComparer.Ordinal));
        Assert.Equal(["memory"], SettingsViewModel.NoticeParts(new SensorStatus { StoppedForMemory = true }));
    }

    [Fact]
    public void Nothing_left_out_needs_nothing()
    {
        Assert.False(SettingsViewModel.NeedsAttention(null, null));
        Assert.False(SettingsViewModel.NeedsAttention(new SensorStatus(), "Fan hubs:iCUE"));
    }

    [Fact]
    public void Opening_settings_isnt_enough_only_dealing_with_it_is()
    {
        var icue = Status(("Fan hubs", ["iCUE"]), ("Power supply", ["iCUE"]));
        Assert.True(SettingsViewModel.NeedsAttention(icue, null));
        // "Got it" (or the switch): the programs' parts are seen.
        string? seen = SettingsViewModel.Acknowledge(null, icue, problems: false);
        Assert.False(SettingsViewModel.NeedsAttention(icue, seen));
        // Another program is news.
        Assert.True(SettingsViewModel.NeedsAttention(Status(("Fan hubs", ["iCUE"]), ("Power supply", ["iCUE"]), ("Motherboard", ["Gigabyte Control Center"])), seen));
        // So is a problem; "Got it" doesn't cover it, "Try again" does.
        var both = Safe(Status(("Fan hubs", ["iCUE"]), ("Power supply", ["iCUE"])));
        Assert.True(SettingsViewModel.NeedsAttention(both, seen));
        Assert.True(SettingsViewModel.NeedsAttention(both, SettingsViewModel.Acknowledge(seen, both, problems: false)));
        Assert.False(SettingsViewModel.NeedsAttention(both, SettingsViewModel.Acknowledge(seen, both, problems: true)));
        // And "Try again" alone doesn't acknowledge the programs.
        Assert.True(SettingsViewModel.NeedsAttention(both, SettingsViewModel.Acknowledge(null, both, problems: true)));
    }

    [Theory]
    // current offset, section top (in the view), section height, viewport → expected offset
    [InlineData(0, 900, 300, 800, 650)]    // centred: 900 - (800 - 300) / 2
    [InlineData(200, 100, 200, 800, 0)]    // above the middle: scrolls back (the caller clamps at 0)
    [InlineData(0, 900, 900, 800, 888)]    // taller than the view: its top a little below the top instead
    [InlineData(0, 900, 790, 800, 888)]    // almost as tall: same (no room for the margins)
    public void The_section_is_centred_unless_it_doesnt_fit(double current, double top, double height, double viewport, double expected) =>
        Assert.Equal(expected, Rigsight.Controls.Attention.CenteredOffset(current, top, height, viewport, margin: 12));

    [Fact]
    public void The_seen_list_is_stable_text()
    {
        Assert.Null(SettingsViewModel.JoinParts([]));
        Assert.Equal("Fan hubs:iCUE|safe", SettingsViewModel.JoinParts(["safe", "Fan hubs:iCUE"]));
        Assert.Equal(["Fan hubs:iCUE", "safe"], SettingsViewModel.SeenParts("Fan hubs:iCUE|safe|").Order(StringComparer.Ordinal));
        Assert.Empty(SettingsViewModel.SeenParts(null));
    }
}
