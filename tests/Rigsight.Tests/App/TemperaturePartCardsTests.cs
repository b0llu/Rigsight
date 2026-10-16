using System.Windows;
using System.Windows.Controls;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Models;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The Temperatures page's "Drives" and "Motherboard" cards: the board's tiles grouped by chip, each drive against its
/// own limit (or its kind's usual), the line under a drive's name, and which cards show.
/// </summary>
[Collection("UI")]
public sealed class TemperaturePartCardsTests
{
    private static HardwareMeta Part(string name, string type, params (string Id, string Name, SensorKind Kind)[] sensors) => new()
    {
        Name = name, Type = type, Sensors = [.. sensors.Select(s => new SensorMeta { Id = s.Id, Name = s.Name, Kind = s.Kind })],
    };

    private static readonly HardwareMeta Cpu = Part("Test CPU", "Cpu", ("/cpu/temperature/0", "Core (Tctl/Tdie)", SensorKind.Temperature));

    private static readonly HardwareMeta Samsung = Part("Samsung SSD 980 1TB", "Storage",
        ("/nvme/2/temperature/0", "Composite Temperature", SensorKind.Temperature),
        ("/nvme/2/temperature/10", "Warning Temperature", SensorKind.Temperature),
        ("/nvme/2/level/20", "Available Spare", SensorKind.Level));

    private static readonly HardwareMeta Kingston = Part("KINGSTON SA400S37480G", "Storage",
        ("/hdd/1/temperature/0", "Temperature", SensorKind.Temperature));

    private static readonly HardwareMeta Seagate = Part("ST2000VX008-2E3164", "Storage",
        ("/hdd/0/temperature/0", "Temperature", SensorKind.Temperature));

    private static readonly HardwareMeta Ite = Part("ITE IT8686E  ·  Gigabyte B450 AORUS PRO-CF", "SuperIO",
        ("/lpc/it8686e/0/temperature/0", "Temperature #1", SensorKind.Temperature),
        ("/lpc/it8686e/0/temperature/1", "Temperature #2", SensorKind.Temperature),
        ("/lpc/it8686e/0/fan/0", "Fan #1", SensorKind.Fan));

    private static readonly HardwareMeta Ite2 = Part("ITE IT8792E  ·  Gigabyte B450 AORUS PRO-CF", "SuperIO",
        ("/lpc/it8792e/0/temperature/0", "Temperature #1", SensorKind.Temperature));

    private static readonly HardwareMeta Ec = Part("Embedded Controller", "EmbeddedController",
        ("/ec/0/temperature/0", "Chipset", SensorKind.Temperature));

    /// <summary>What Windows says of this test's drives: disk 0 a hard drive, 1 a SATA SSD with Windows on it, 2 an NVMe SSD.</summary>
    private static DriveFacts Facts(int disk) => disk switch
    {
        0 => new(DriveKind.HardDrive, "SATA", 2_000_398_934_016, false),
        1 => new(DriveKind.Ssd, "SATA", 480_103_981_056, true),
        2 => new(DriveKind.Ssd, "NVMe", 1_000_204_886_016, false),
        _ => DriveFacts.None,
    };

    /// <summary>Live data greeted with these parts, Windows' answers from <paramref name="probe"/> (the PC's own drives never).</summary>
    private static LiveData Greeted(Func<int, DriveFacts>? probe, params HardwareMeta[] parts)
    {
        var live = Kit.Live(Kit.OfflineSettings());
        Ui.Run(() =>
        {
            live.ProbeDrive = probe ?? Facts;
            live.WindowsLetter = 'C';
            live.LoadHello(new AgentMessage { T = "hello", IsAdmin = true, Hardware = [.. parts] });
        });
        return live;
    }

    // ── Motherboard ──

    [Fact]
    public void Board_tiles_are_grouped_by_their_chip_named_as_all_sensors_names_it()
    {
        var live = Greeted(null, Cpu, Ite, Ite2, Ec);
        Ui.Run(() =>
        {
            Assert.Equal(["ITE IT8686E", "ITE IT8792E", "Embedded Controller"], live.BoardChips.Select(c => c.Chip));
            Assert.Equal(["Gigabyte B450 AORUS PRO-CF", "Gigabyte B450 AORUS PRO-CF", null], live.BoardChips.Select(c => c.Board));
            Assert.Equal("ITE IT8686E", live.BoardChips[0].ChipCaps);
            Assert.Equal("EMBEDDED CONTROLLER", live.BoardChips[2].ChipCaps);
            Assert.False(live.BoardChips[2].HasBoard);
            // Each chip's own "Temperature #1", under its own chip; fans aren't tiles.
            Assert.Equal(["Temperature #1", "Temperature #2"], live.BoardChips[0].Tiles.Select(t => t.Name));
            Assert.Equal(["Temperature #1"], live.BoardChips[1].Tiles.Select(t => t.Name));
            Assert.All(live.BoardChips.SelectMany(c => c.Tiles), t => Assert.Equal("40,60,80", t.Steps));

            // Hidden on All sensors: its tile goes, and a chip with none left has no group; renamed: the tile follows.
            var only = live.BoardChips[1].Tiles[0].Sensor;
            only.ToggleHidden();
            Assert.Equal(["ITE IT8686E", "Embedded Controller"], live.BoardChips.Select(c => c.Chip));
            only.ToggleHidden();
            Assert.Equal(3, live.BoardChips.Count);
            var first = live.BoardChips[0].Tiles[0].Sensor;
            first.Label = "CPU socket";
            Assert.Equal("CPU socket", live.BoardChips[0].Tiles[0].Name);
        });
    }

    [Fact]
    public void A_name_without_a_board_is_all_chip()
    {
        Assert.Equal(("ITE IT8686E", "Gigabyte B450 AORUS PRO-CF"), BoardChip.Split("ITE IT8686E  ·  Gigabyte B450 AORUS PRO-CF"));
        Assert.Equal(("Nuvoton NCT6798D", (string?)null), BoardChip.Split("Nuvoton NCT6798D"));
    }

    [Fact]
    public void The_same_board_tiles_keep_the_list_shown()
    {
        var live = Greeted(null, Cpu, Ite);
        Ui.Run(() =>
        {
            var shown = live.BoardChips;
            var drives = live.DriveTemps;
            // Another temperature renamed (the CPU's): the cards are put together again, say the same, and stay as they are (no rebuilt tiles).
            live.AllSensors.First(s => s.Id == "/cpu/temperature/0").Label = "CPU";
            Assert.Same(shown, live.BoardChips);
            Assert.Same(drives, live.DriveTemps);
        });
    }

    // ── Drives ──

    [Fact]
    public void Each_drive_is_held_to_its_own_limit_else_its_kinds_usual()
    {
        var live = Greeted(null, Cpu, Samsung, Kingston, Seagate);
        Ui.Run(() =>
        {
            Assert.Equal(["Samsung SSD 980 1TB", "KINGSTON SA400S37480G", "ST2000VX008-2E3164"], live.DriveTemps.Select(d => d.Name));
            var (samsung, kingston, seagate) = (live.DriveTemps[0], live.DriveTemps[1], live.DriveTemps[2]);
            // Before the Samsung's own limit arrives, its kind's: an SSD's 70°.
            Assert.Equal(70, samsung.Limit);
            var changes = Kit.Changes(samsung, () => samsung.ReportedLimit!.Push(TimeUtil.NowUnixMs(), 81));
            Assert.Equal(81, samsung.Limit);
            Assert.Contains(nameof(DriveTemp.Limit), changes);
            Assert.Contains(nameof(DriveTemp.Steps), changes);
            Assert.Equal("limit 81°", samsung.LimitText);
            Assert.Equal("The limit this drive reports for itself", samsung.LimitTip);
            // No limit reported: 70° for an SSD, 50° for a hard drive (the old footnote's figures).
            Assert.Null(kingston.ReportedLimit);
            Assert.Equal(70, kingston.Limit);
            Assert.Equal("limit 70°", kingston.LimitText);
            Assert.Equal(50, seagate.Limit);
            Assert.Equal("limit 50°", seagate.LimitText);
            Assert.Contains("hard drive", seagate.LimitTip);
            // Colour steps and the bar from the limit: warm from 10° under it, the mark inside the bar's end.
            Assert.Equal("35,60,70", kingston.Steps);
            Assert.Equal("35,40,50", seagate.Steps);
            Assert.Equal("35,71,81", samsung.Steps);
            Assert.Equal(90, kingston.Top);
            Assert.Equal(91, samsung.Top);
            Assert.Equal(new GridLength(70, GridUnitType.Star), kingston.LimitWidth);
            Assert.Equal(new GridLength(20, GridUnitType.Star), kingston.AboveLimitWidth);
        });
    }

    [Fact]
    public void A_limit_out_of_reason_is_not_believed()
    {
        var live = Greeted(null, Cpu, Samsung);
        Ui.Run(() =>
        {
            var samsung = live.DriveTemps[0];
            samsung.ReportedLimit!.Push(TimeUtil.NowUnixMs(), 255);
            Assert.Equal(70, samsung.Limit);
        });
    }

    [Fact]
    public void A_drive_of_unknown_kind_is_held_to_the_lower_limit()
    {
        Assert.Equal(DriveTemp.SsdLimit, DriveTemp.UsualLimit(DriveKind.Ssd));
        Assert.Equal(DriveTemp.HardDriveLimit, DriveTemp.UsualLimit(DriveKind.HardDrive));
        Assert.Equal(50, DriveTemp.UsualLimit(DriveKind.Unknown));
    }

    [Fact]
    public void The_line_under_a_drive_says_its_kind_size_and_the_C_drive()
    {
        var probed = new List<int>();
        var live = Greeted(d => { probed.Add(d); return Facts(d); }, Cpu, Samsung, Kingston, Seagate);
        Ui.Run(() =>
        {
            // The drive's number is the one in its sensors' ids.
            Assert.Equal([2, 1, 0], probed);
            Assert.Equal(["NVMe SSD · 1 TB", "SATA SSD · 480 GB · the C drive", "Hard drive · 2 TB"], live.DriveTemps.Select(d => d.Detail));
            Assert.All(live.DriveTemps, d => Assert.True(d.HasDetail));
            // A new hello with the same drives asks Windows nothing again.
            live.LoadHello(new AgentMessage { T = "hello", IsAdmin = true, Hardware = [Cpu, Samsung] });
            Assert.Equal([2, 1, 0], probed);
        });
    }

    [Fact]
    public void Where_Windows_says_nothing_an_NVMe_drive_or_one_that_wears_is_an_SSD()
    {
        var worn = Part("Some SSD", "Storage", ("/hdd/4/temperature/0", "Temperature", SensorKind.Temperature), ("/hdd/4/level/0", "Remaining Life", SensorKind.Level));
        var bare = Part("Some drive", "Storage", ("/hdd/5/temperature/0", "Temperature", SensorKind.Temperature));
        var live = Greeted(_ => DriveFacts.None, Cpu, Samsung, worn, bare);
        Ui.Run(() =>
        {
            Assert.Equal(["NVMe SSD", "SSD", ""], live.DriveTemps.Select(d => d.Detail));
            Assert.Equal([DriveKind.Ssd, DriveKind.Ssd, DriveKind.Unknown], live.DriveTemps.Select(d => d.Kind));
            Assert.False(live.DriveTemps[2].HasDetail);
            Assert.Equal(50, live.DriveTemps[2].Limit);
        });
    }

    [Fact]
    public void Drive_words_kind_size_and_letter()
    {
        Assert.Equal("1 TB", DriveFacts.SizeText(1_000_204_886_016));
        Assert.Equal("2 TB", DriveFacts.SizeText(2_000_398_934_016));
        Assert.Equal("480 GB", DriveFacts.SizeText(480_103_981_056));
        Assert.Equal("256 GB", DriveFacts.SizeText(256_060_514_304));
        Assert.Equal("1.9 TB", DriveFacts.SizeText(1_920_383_410_176));
        Assert.Null(DriveFacts.SizeText(null));
        Assert.Null(DriveFacts.SizeText(0));
        Assert.Equal("NVMe SSD", DriveFacts.KindText(DriveKind.Ssd, "NVMe"));
        Assert.Equal("SSD", DriveFacts.KindText(DriveKind.Ssd, null));
        Assert.Equal("Hard drive", DriveFacts.KindText(DriveKind.HardDrive, "SATA"));
        Assert.Equal("USB hard drive", DriveFacts.KindText(DriveKind.HardDrive, "USB"));
        Assert.Null(DriveFacts.KindText(DriveKind.Unknown, "SATA"));
        Assert.Equal("NVMe SSD · 1 TB · the C drive", new DriveFacts(DriveKind.Ssd, "NVMe", 1_000_204_886_016, true).Line('C'));
        Assert.Equal("Hard drive · 2 TB", new DriveFacts(DriveKind.HardDrive, "SATA", 2_000_398_934_016, false).Line('C'));
        // Nothing known but that Windows is on it: a sentence of its own.
        Assert.Equal("The D drive", new DriveFacts(DriveKind.Unknown, null, null, true).Line('D'));
        Assert.Equal("", DriveFacts.None.Line('C'));
    }

    // ── Which cards show ──

    [Fact]
    public void Both_cards_with_drives_and_board_readings()
    {
        var live = Greeted(null, Cpu, Samsung, Ite);
        Ui.Run(() =>
        {
            Assert.True(live.HasDriveTemps);
            Assert.True(live.HasBoardTemps);
            Assert.True(live.HasPartTemps);
        });
    }

    [Fact]
    public void Only_drives_when_the_board_gives_no_temperatures()
    {
        var live = Greeted(null, Cpu, Samsung);
        Ui.Run(() =>
        {
            Assert.True(live.HasDriveTemps);
            Assert.False(live.HasBoardTemps);
            Assert.True(live.HasPartTemps);
        });
    }

    [Fact]
    public void Only_the_board_when_no_drive_gives_a_temperature()
    {
        var noTemp = Part("Bare HDD", "Storage", ("/hdd/1/load/0", "Used Space", SensorKind.Load));
        var live = Greeted(null, Cpu, noTemp, Ite);
        Ui.Run(() =>
        {
            Assert.False(live.HasDriveTemps);
            Assert.True(live.HasBoardTemps);
            Assert.True(live.HasPartTemps);
        });
    }

    [Fact]
    public void Neither_without_admin_rights()
    {
        var live = Greeted(null, Cpu);
        Ui.Run(() =>
        {
            Assert.False(live.HasDriveTemps);
            Assert.False(live.HasBoardTemps);
            Assert.False(live.HasPartTemps);
            Assert.Empty(live.DriveTemps);
            Assert.Empty(live.BoardChips);
        });
    }

    [Fact]
    public void Board_readings_dropped_as_nonsense_take_their_card_with_them()
    {
        var live = Greeted(null, Cpu, Samsung, Ec);
        Ui.Run(() =>
        {
            Assert.True(live.HasBoardTemps);
            var changes = Kit.Changes(live, () =>
            {
                long t = TimeUtil.NowUnixMs();
                // Three readings in, a board sensor reading 0 is dropped (see LiveData.UpdateDerived).
                for (int i = 0; i < 3; i++) live.ApplyTick(new AgentMessage { T = "tick", Time = t + i * 1000, Values = [50, 40, 70, 30, 0] });
            });
            Assert.False(live.HasBoardTemps);
            Assert.True(live.HasPartTemps);
            Assert.Contains(nameof(LiveData.HasBoardTemps), changes);
        });
    }

    // ── The two cards' panel ──

    [Fact]
    public void Two_cards_share_the_row_stack_when_narrow_and_one_alone_takes_it_all()
    {
        Ui.Run(() =>
        {
            var drives = new Border { MinHeight = 200 };
            var board = new Border { MinHeight = 300 };
            var pair = new CardPair { Children = { drives, board } };
            // Wide: equal halves, of one height.
            pair.Measure(new Size(1200, double.PositiveInfinity));
            Assert.Equal(300, pair.DesiredSize.Height);
            pair.Arrange(new Rect(0, 0, 1200, 300));
            Assert.Equal(new Rect(0, 0, 591, 300), new Rect(drives.TranslatePoint(default, pair), drives.RenderSize));
            Assert.Equal(new Rect(609, 0, 591, 300), new Rect(board.TranslatePoint(default, pair), board.RenderSize));
            // Narrow: drives above the board, each the whole width.
            pair.Measure(new Size(700, double.PositiveInfinity));
            Assert.Equal(200 + 18 + 300, pair.DesiredSize.Height);
            pair.Arrange(new Rect(0, 0, 700, pair.DesiredSize.Height));
            Assert.Equal(new Rect(0, 218, 700, 300), new Rect(board.TranslatePoint(default, pair), board.RenderSize));
            // Only the board has readings: it takes the whole row, wide or not.
            drives.Visibility = Visibility.Collapsed;
            pair.Measure(new Size(1200, double.PositiveInfinity));
            Assert.Equal(300, pair.DesiredSize.Height);
            pair.Arrange(new Rect(0, 0, 1200, 300));
            Assert.Equal(new Rect(0, 0, 1200, 300), new Rect(board.TranslatePoint(default, pair), board.RenderSize));
        });
    }
}
