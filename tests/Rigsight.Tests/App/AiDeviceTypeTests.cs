using Rigsight.Core.Ai;

namespace Rigsight.Tests.App;

/// <summary>
/// get_pc_info says laptop or desktop from the PC's own firmware (audit Q029, Q110, Q129, 2026-11-17: a laptop's heat at
/// low load judged as a desktop's, with nothing that said which it was), and "unknown" when the PC doesn't say clearly.
/// </summary>
public sealed class AiDeviceTypeTests
{
    private const int Desktop = 1, Mobile = 2, Unspecified = 0;

    [Fact]
    public void The_firmwares_chassis_decides()
    {
        Assert.Equal("Laptop (the PC's firmware calls its case \"Notebook\"; it has a battery).", DeviceType.Decide([10], true, Mobile));
        Assert.Equal("Desktop (the PC's firmware calls its case \"Tower\"; it has no battery).", DeviceType.Decide([7], false, Desktop));
        Assert.Equal("Desktop (the PC's firmware calls its case \"Desktop\").", DeviceType.Decide([3], null, null));
        // A UPS on USB shows as a battery: the desktop case still decides.
        Assert.StartsWith("Desktop", DeviceType.Decide([3], true, Desktop));
    }

    [Fact]
    public void A_chassis_that_both_other_signs_contradict_is_unknown()
    {
        Assert.StartsWith("unknown", DeviceType.Decide([3], true, Mobile));
        Assert.StartsWith("unknown", DeviceType.Decide([9], false, Desktop));
    }

    [Fact]
    public void Without_a_chassis_the_battery_and_power_role_must_agree()
    {
        Assert.StartsWith("Laptop (it has a battery and Windows treats it as a laptop", DeviceType.Decide([2], true, Mobile));
        Assert.StartsWith("Desktop (it has no battery and Windows treats it as a desktop", DeviceType.Decide([], false, Desktop));
        Assert.Equal("unknown (the PC doesn't say clearly). Don't guess it from the CPU's name.", DeviceType.Decide([1], true, Unspecified));
        Assert.StartsWith("unknown", DeviceType.Decide([], null, null));
    }

    [Fact]
    public void The_chassis_is_read_from_the_SMBIOS_structures()
    {
        // Header (8 bytes, length of the table), a type 1 with one string, a type 3 (System Enclosure) of chassis 0x8A
        // (the lock bit set over Notebook), then the end marker.
        byte[] tables =
        [
            1, 8, 0, 0, 0, 0, 0, 0, (byte)'X', 0, 0,
            3, 9, 1, 0, 1, 0x8A, 0, 0, 0, 0, 0,
            127, 4, 2, 0, 0, 0,
        ];
        var raw = new byte[8 + tables.Length];
        BitConverter.GetBytes(tables.Length).CopyTo(raw, 4);
        tables.CopyTo(raw, 8);

        Assert.Equal([10], DeviceType.ChassisIn(raw));
    }

    [Fact]
    public void This_PC_gets_a_line_whatever_it_is()
    {
        Assert.Matches("^Device type: (Laptop|Desktop|unknown) \\(", DeviceType.Line());
    }
}
