using Rigsight.Core.Reports;

namespace Rigsight.Tests.Reports;

/// <summary>Fan-control programs a fan warning names, for the fans each one sets.</summary>
public class FanSoftwareTests
{
    [Fact]
    public void A_running_fan_program_is_named_for_the_fans_it_sets()
    {
        Assert.Equal("MSI Afterburner", FanSoftware.Among(["chrome.exe", "MSIAfterburner.exe"], gpu: true));
        Assert.Null(FanSoftware.Among(["MSIAfterburner.exe"], gpu: false)); // it doesn't set the board's fans
        Assert.Equal("iCUE", FanSoftware.Among(["iCUE.exe"], gpu: false));
        Assert.Equal("Fan Control", FanSoftware.Among(["fancontrol"], gpu: true)); // either case, with or without ".exe"
        Assert.Equal("Fan Control", FanSoftware.Among(["FanControl.exe"], gpu: false));
        Assert.Null(FanSoftware.Among(["steam.exe", "discord.exe"], gpu: true));
    }
}
