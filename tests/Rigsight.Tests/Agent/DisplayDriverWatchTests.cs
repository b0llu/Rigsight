using Rigsight.Agent.Sensors;

namespace Rigsight.Tests.Agent;

/// <summary>
/// A graphics driver replaced under a running agent (7 Oct 2026: an NVIDIA update ended it with an access violation
/// in the card's library): the cards are left alone from the first sign, the agent starts again once it's quiet, and
/// Windows starts it again should it crash all the same.
/// </summary>
public class DisplayDriverWatchTests
{
    [Theory]
    [InlineData(@"\\?\PCI#VEN_10DE&DEV_2208&SUBSYS_40881458&REV_A1#4&1fc990d7&0&0019#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}", true)]
    [InlineData(@"\\?\pci#ven_8086&dev_a780&subsys_00000000&rev_04#3&11583659&0&10#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}", true)]
    // Virtual screens (remote desktop, streaming) come and go all day: not a reason to start again.
    [InlineData(@"\\?\ROOT#DISPLAY#0000#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}", false)]
    [InlineData(@"\\?\SWD#RemoteDisplayEnum#RdpIdd_IndirectDisplay&SessionId_0002#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}", false)]
    [InlineData(@"\\?\USB#VID_17E9&PID_6006#1234#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_real_graphics_card_counts(string? path, bool card) =>
        Assert.Equal(card, DisplayDriverWatch.IsGraphicsCard(path));

    [Fact]
    public void Nothing_changed_until_Windows_says_so()
    {
        var watch = new DisplayDriverWatch();
        Assert.False(watch.Changed);
        Assert.False(watch.Settled(10_000_000));
    }

    [Fact]
    public void Changed_at_once_and_settled_only_after_a_quiet_while()
    {
        var watch = new DisplayDriverWatch();
        watch.Note(100_000); // the old driver goes
        Assert.True(watch.Changed);
        Assert.False(watch.Settled(100_000 + DisplayDriverWatch.SettleMs - 1));

        watch.Note(120_000); // the new one arrives: the wait starts over
        Assert.False(watch.Settled(100_000 + DisplayDriverWatch.SettleMs));
        Assert.False(watch.Settled(120_000 + DisplayDriverWatch.SettleMs - 1));
        Assert.True(watch.Settled(120_000 + DisplayDriverWatch.SettleMs));
        Assert.True(watch.Changed); // for the rest of this agent's life
    }

    [Fact]
    public void A_change_at_the_very_start_of_the_clock_still_counts()
    {
        var watch = new DisplayDriverWatch();
        watch.Note(0);
        Assert.True(watch.Changed);
    }

    [Fact]
    public void A_copy_that_carries_on_starts_as_this_one_did_without_what_was_for_its_own_start()
    {
        Assert.Empty(Rigsight.Agent.Program.RestartArgs([], null));
        Assert.Equal(["--no-elevate"], Rigsight.Agent.Program.RestartArgs(["--no-elevate", "--open", "--replace", "--after-crash"], null));
        // A test copy told its folder by the environment says it outright; one already told so isn't told twice.
        Assert.Equal(["--data-dir", @"C:\t\data"], Rigsight.Agent.Program.RestartArgs(["--open"], @"C:\t\data"));
        Assert.Equal(["--data-dir", @"C:\t\data"], Rigsight.Agent.Program.RestartArgs(["--data-dir", @"C:\t\data"], @"C:\t\data"));
    }

    [Fact]
    public void What_Windows_starts_after_a_crash()
    {
        Assert.Equal("--after-crash", Rigsight.Agent.Program.CrashRestartLine([], null));
        Assert.Equal("--no-elevate --after-crash", Rigsight.Agent.Program.CrashRestartLine(["--no-elevate", "--open"], null));
        // A folder with a space is quoted, without a last backslash (it would swallow the quote).
        Assert.Equal("--data-dir \"C:\\my data\\t\" --after-crash", Rigsight.Agent.Program.CrashRestartLine([], @"C:\my data\t\"));
    }
}
