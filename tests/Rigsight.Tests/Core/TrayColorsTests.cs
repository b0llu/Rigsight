using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Core;

/// <summary>A taskbar reading's own colours: what's kept from settings, and that a picked colour stays readable on the taskbar.</summary>
public class TrayColorsTests
{
    [Fact]
    public void Only_colours_RigSight_can_read_are_kept_and_an_empty_pick_leaves_no_entry()
    {
        var s = new RigsightSettings
        {
            TrayColors = new()
            {
                ["/cpu/0/temperature/0"] = new TrayColor { Number = "heat", Name = "ff6a00" },
                ["/gpu/0/load/0"] = new TrayColor { Number = "#12345", Name = "not a colour" },
                ["/ram/load"] = new TrayColor { Number = "#E879F9" },
            },
        };
        var clean = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal("heat", clean.TrayColors["/cpu/0/temperature/0"].Number);
        Assert.Equal("#FF6A00", clean.TrayColors["/cpu/0/temperature/0"].Name);
        Assert.False(clean.TrayColors.ContainsKey("/gpu/0/load/0"));
        Assert.Equal("#E879F9", clean.TrayColors["/ram/load"].Number);
        Assert.Null(clean.TrayColors["/ram/load"].Name);
    }

    [Fact]
    public void White_is_deepened_on_a_light_taskbar_and_black_lifted_on_a_dark_one()
    {
        var onLight = TrayColors.ForTaskbar((255, 255, 255), light: true);
        Assert.True(onLight.R < 150, $"white on a light taskbar became {onLight}");
        var onDark = TrayColors.ForTaskbar((0, 0, 0), light: false);
        Assert.True(onDark.R > 100, $"black on a dark taskbar became {onDark}");
        // A colour that already reads well is kept as it is.
        Assert.Equal(((byte)0x5B, (byte)0x8C, (byte)0xFF), TrayColors.ForTaskbar((0x5B, 0x8C, 0xFF), light: false));
    }

    [Fact]
    public void The_quick_picks_hold_no_warning_colour()
    {
        foreach (var (_, hex) in TrayColors.Palette)
        {
            var (r, g, b) = TrayColors.Parse(hex)!.Value;
            bool greenish = g > r + 40 && g > b + 40, reddish = r > g + 60 && r > b + 60 && b < 150, amber = r > 200 && g > 120 && b < 80;
            Assert.False(greenish || reddish || amber, hex);
        }
    }
}
