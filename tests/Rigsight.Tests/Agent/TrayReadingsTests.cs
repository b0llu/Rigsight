using System.Drawing;
using Rigsight.Agent.Ui;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>Sensors in the taskbar: the short numbers, grouping by part, the colour marks, the tooltips and the drawing.</summary>
public class TrayReadingsTests
{
    [Theory]
    [InlineData(SensorKind.Temperature, 61.6, "62")]
    [InlineData(SensorKind.Load, 7.2, "7")]
    [InlineData(SensorKind.Load, 100, "100")]
    [InlineData(SensorKind.Fan, 845, "845")]
    [InlineData(SensorKind.Fan, 1245, "1.2k")]
    [InlineData(SensorKind.Clock, 4450, "4.5")]
    [InlineData(SensorKind.Clock, 800, "800")]
    [InlineData(SensorKind.Power, 118.4, "118")]
    [InlineData(SensorKind.Voltage, 1.244, "1.24")]
    [InlineData(SensorKind.SmallData, 3300, "3.2")]
    [InlineData(SensorKind.Data, 15.4, "15")]
    [InlineData(SensorKind.Data, 9.54, "9.5")]
    public void Readings_are_a_few_characters_without_units(SensorKind kind, double value, string text) =>
        Assert.Equal(text, WidgetRenderer.TrayText(kind, value));

    [Fact]
    public void No_reading_is_a_dash()
    {
        Assert.Equal("–", WidgetRenderer.TrayText(SensorKind.Temperature, null));
        Assert.Equal("–", WidgetRenderer.TrayText(SensorKind.Load, double.NaN));
    }

    private static TrayReading R(int i) => new($"/s/{i}", $"Sensor {i}", SensorKind.Temperature, 50 + i, TrayPart.Other, "Chip");

    private static TrayReading Cpu(string name, SensorKind kind, double value, int n = 0) =>
        new($"/amdcpu/0/{kind.ToString().ToLowerInvariant()}/{n}", name, kind, value, TrayPart.Cpu, "AMD Ryzen 7 7800X3D");

    private static TrayReading Gpu(string name, SensorKind kind, double value, int card = 0, int n = 0) =>
        new($"/gpu-nvidia/{card}/{kind.ToString().ToLowerInvariant()}/{n}", name, kind, value, TrayPart.Gpu, $"NVIDIA GeForce RTX 3080 Ti #{card}");

    private static readonly TrayReading CpuTemp = Cpu("CPU Package", SensorKind.Temperature, 62);
    private static readonly TrayReading CpuLoad = Cpu("CPU Total", SensorKind.Load, 35);
    private static readonly TrayReading GpuTemp = Gpu("GPU Core", SensorKind.Temperature, 71);
    private static readonly TrayReading GpuLoad = Gpu("GPU Core", SensorKind.Load, 98);
    private static readonly TrayReading Ram = new("/ram/load/0", "Memory", SensorKind.Load, 70, TrayPart.Ram, "Total Memory");

    private static List<string[]> Ids(List<List<TrayReading>> icons) => [.. icons.Select(i => i.Select(r => r.Name + " " + r.Kind).ToArray())];

    [Fact]
    public void An_icon_each_keeps_the_users_order()
    {
        var icons = TrayReadings.Icons([GpuLoad, CpuTemp, Ram], grouped: false);
        Assert.Equal([[GpuLoad], [CpuTemp], [Ram]], icons);
    }

    [Fact]
    public void Grouped_each_part_gets_an_icon_with_its_temperature_on_top()
    {
        // Listed in any order: the CPU's and the GPU's readings come together, the temperature above the load.
        var icons = TrayReadings.Icons([CpuLoad, GpuLoad, Ram, GpuTemp, CpuTemp], grouped: true);
        Assert.Equal([[CpuTemp, CpuLoad], [GpuTemp, GpuLoad], [Ram]], icons);
    }

    [Fact]
    public void A_part_with_more_readings_than_fit_gets_another_icon()
    {
        var clock = Cpu("Core #1", SensorKind.Clock, 4450);
        var power = Cpu("Package", SensorKind.Power, 88);
        var icons = TrayReadings.Icons([clock, CpuLoad, power, CpuTemp], grouped: true);
        Assert.Equal([[CpuTemp, CpuLoad], [clock, power]], icons);
    }

    [Fact]
    public void Two_gpus_are_two_icons_but_the_ram_is_one()
    {
        var laptopGpu = Gpu("GPU Core", SensorKind.Temperature, 55, card: 1);
        var stick = new TrayReading("/memory/dimm/0/temperature/0", "DIMM #1", SensorKind.Temperature, 41, TrayPart.Ram, "G.Skill 16 GB");
        var icons = TrayReadings.Icons([GpuTemp, laptopGpu, Ram, stick], grouped: true);
        Assert.Equal([[GpuTemp], [laptopGpu], [stick, Ram]], icons);
    }

    [Fact]
    public void Icons_keep_who_they_are_when_other_readings_come_and_go()
    {
        // Windows remembers an icon dragged out from behind the arrow by who it is: adding or removing a reading
        // mustn't change who the others are.
        var before = TrayReadings.Keys(TrayReadings.Icons([CpuTemp, GpuTemp, Ram], grouped: false));
        var after = TrayReadings.Keys(TrayReadings.Icons([GpuTemp, Ram, CpuLoad], grouped: false));
        Assert.Equal(before.Skip(1), after.Take(2));
        Assert.Equal(3, after.Distinct().Count());

        var grouped = TrayReadings.Keys(TrayReadings.Icons([CpuTemp, GpuTemp], grouped: true));
        var more = TrayReadings.Keys(TrayReadings.Icons([CpuTemp, CpuLoad, GpuTemp, GpuLoad, Ram, Cpu("Core", SensorKind.Clock, 4000)], grouped: true));
        Assert.Equal(grouped, more.Take(1).Concat(more.Skip(2).Take(1))); // the CPU's first icon and the GPU's stay themselves
        Assert.Equal(4, more.Distinct().Count());                          // the CPU's second icon is another
    }

    [Fact]
    public void Switching_between_an_icon_each_and_per_part_keeps_the_icons_the_user_placed()
    {
        // An icon each: CPU temperature, RAM and GPU temperature dragged out. Per part, the CPU's and GPU's icons are
        // those same icons (their first reading's), so they stay out; the loads' icons go, and come back as they were.
        var each = TrayReadings.Keys(TrayReadings.Icons([CpuTemp, CpuLoad, Ram, GpuTemp, GpuLoad], grouped: false));
        var perPart = TrayReadings.Keys(TrayReadings.Icons([CpuTemp, CpuLoad, Ram, GpuTemp, GpuLoad], grouped: true));
        Assert.Equal(3, perPart.Count);
        Assert.All(perPart, key => Assert.Contains(key, each));
        Assert.Equal([each[0], each[2], each[3]], perPart);
    }

    [Fact]
    public void An_icons_identity_lasts_but_is_its_own_per_install()
    {
        var a = TrayIcon.GuidFor("reading|/amdcpu/0/temperature/2", @"C:\Program Files\Rigsight\Rigsight.Agent.exe");
        Assert.Equal(a, TrayIcon.GuidFor("reading|/amdcpu/0/temperature/2", @"c:\program files\rigsight\RIGSIGHT.AGENT.EXE"));
        Assert.NotEqual(a, TrayIcon.GuidFor("reading|/amdcpu/0/temperature/2", @"C:\Users\me\AppData\Local\RigsightTest\bin\Rigsight.Agent.exe"));
        Assert.NotEqual(a, TrayIcon.GuidFor("reading|/amdcpu/0/load/0", @"C:\Program Files\Rigsight\Rigsight.Agent.exe"));
    }

    [Theory]
    [InlineData("Cpu", TrayPart.Cpu)]
    [InlineData("GpuNvidia", TrayPart.Gpu)]
    [InlineData("GpuAmd", TrayPart.Gpu)]
    [InlineData("GpuIntel", TrayPart.Gpu)]
    [InlineData("Memory", TrayPart.Ram)]
    [InlineData("Storage", TrayPart.Drive)]
    [InlineData("SuperIO", TrayPart.Board)]
    [InlineData("Motherboard", TrayPart.Board)]
    [InlineData("Network", TrayPart.Network)]
    [InlineData("Psu", TrayPart.Other)]
    [InlineData(null, TrayPart.Other)]
    public void Each_reading_is_put_down_to_its_part(string? hardwareType, TrayPart part) => Assert.Equal(part, TrayParts.PartOf(hardwareType));

    [Fact]
    public void Parts_have_rigsights_colours_and_the_others_share_grey()
    {
        Assert.Equal(((byte)91, (byte)140, (byte)255), TrayParts.Color(TrayPart.Cpu));
        Assert.Equal(((byte)61, (byte)220, (byte)151), TrayParts.Color(TrayPart.Gpu));
        Assert.NotEqual(TrayParts.Color(TrayPart.Cpu), TrayParts.Color(TrayPart.Cpu, light: true)); // deeper on a light taskbar
        Assert.Equal(TrayParts.Color(TrayPart.Drive), TrayParts.Color(TrayPart.Other));
        Assert.Equal(3, new[] { TrayPart.Cpu, TrayPart.Gpu, TrayPart.Ram }.Select(p => TrayParts.Color(p)).Distinct().Count());
    }

    [Theory]
    [InlineData("/gpu-nvidia/0/temperature/0", "/gpu-nvidia/0")]
    [InlineData("/lpc/it8686e/fan/0", "/lpc/it8686e")]
    [InlineData("/nvme/1/temperature/0", "/nvme/1")]
    [InlineData("odd", "odd")]
    public void A_sensor_belongs_to_the_device_in_its_id(string id, string device) => Assert.Equal(device, TrayParts.DeviceOf(id));

    [Fact]
    public void Tooltips_name_the_hardware_then_each_reading_within_windows_limit()
    {
        Assert.Equal($"AMD Ryzen 7 7800X3D\nCPU Package: {Units.Format(SensorKind.Temperature, 62)}\nCPU Total: {Units.Format(SensorKind.Load, 35)}",
            TrayReadings.Tooltip([CpuTemp, CpuLoad]));
        var missing = new TrayReading("/x", "Not found on this PC", SensorKind.Factor, null, TrayPart.Other, "");
        Assert.Equal($"Not found on this PC: {Units.Format(SensorKind.Factor, null)}", TrayReadings.Tooltip([missing]));

        // Too long with the hardware: that goes first, then the names are cut.
        var longHardware = CpuTemp with { Hardware = new string('h', 120) };
        Assert.Equal($"CPU Package: {Units.Format(SensorKind.Temperature, 62)}", TrayReadings.Tooltip([longHardware]));
        string longName = TrayReadings.Tooltip([R(1) with { Name = new string('x', 200) }]);
        Assert.Equal(127, longName.Length);
    }

    [Fact]
    public void Right_click_names_what_it_removes()
    {
        Assert.Equal($"Remove Sensor 1 · {Units.Format(SensorKind.Temperature, 51)}", TrayReadings.RemoveText([R(1)]));
        Assert.Equal("Remove these CPU readings", TrayReadings.RemoveText([CpuTemp, CpuLoad]));
        Assert.Equal("Remove from the taskbar", TrayReadings.RemoveText([R(1) with { Name = new string('x', 60) }]));
        Assert.Equal("Remove from the taskbar", TrayReadings.RemoveText([]));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void Icons_are_drawn_at_the_trays_size_with_the_digits_inside(int size)
    {
        foreach (var lines in new List<(string, Color)>[] { [("62", Color.White)], [("1.2k", Color.White)], [("62", Color.White), ("71", Color.White)] })
        {
            using var bmp = WidgetRenderer.RenderTrayIcon(lines, size);
            Assert.Equal((size, size), (bmp.Width, bmp.Height));
            int inked = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (bmp.GetPixel(x, y).A > 0) inked++;
            Assert.InRange(inked, size * size / 10, size * size * 3 / 4); // something drawn, and transparent around it
            Assert.Equal(0, bmp.GetPixel(0, 0).A);
        }
    }

    [Theory]
    [InlineData(16, 2)]
    [InlineData(24, 3)]
    [InlineData(32, 4)]
    public void The_colour_mark_is_a_crisp_bar_on_the_left_with_the_digits_beside_it(int size, int bar)
    {
        var mark = WidgetRenderer.TrayMark(TrayPart.Gpu, light: false);
        using var bmp = WidgetRenderer.RenderTrayIcon([("88", Color.White), ("88", Color.White)], size, mark);
        for (int x = 0; x < bar; x++)
            Assert.Equal(mark.ToArgb(), bmp.GetPixel(x, size / 2).ToArgb());
        for (int y = 0; y < size; y++)
            Assert.Equal(0, bmp.GetPixel(bar, y).A); // a clear gap: the digits never touch it
        Assert.Equal(0, bmp.GetPixel(0, 0).A);          // a pixel short of the top and bottom
    }

    [Fact]
    public void Temperatures_take_their_colour_other_readings_the_taskbars()
    {
        Assert.NotEqual(WidgetRenderer.TrayColor(SensorKind.Temperature, 40, light: false), WidgetRenderer.TrayColor(SensorKind.Temperature, 90, light: false));
        Assert.Equal(Color.White.ToArgb(), WidgetRenderer.TrayColor(SensorKind.Load, 40, light: false).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), WidgetRenderer.TrayColor(SensorKind.Load, 40, light: true).ToArgb());
    }

    [Fact]
    public void In_grayscale_temperatures_take_the_taskbars_colour_and_every_part_is_marked_grey()
    {
        foreach (bool light in new[] { false, true })
        {
            var text = WidgetRenderer.TrayColor(SensorKind.Load, 40, light).ToArgb();
            Assert.Equal(text, WidgetRenderer.TrayColor(SensorKind.Temperature, 90, light, gray: true).ToArgb());
            Assert.Equal(WidgetRenderer.TrayColor(SensorKind.Temperature, null, light).ToArgb(), WidgetRenderer.TrayColor(SensorKind.Temperature, null, light, gray: true).ToArgb());
            var grey = WidgetRenderer.TrayMark(TrayPart.Other, light).ToArgb();
            Assert.All(Enum.GetValues<TrayPart>(), part => Assert.Equal(grey, WidgetRenderer.TrayMark(part, light, gray: true).ToArgb()));
            Assert.NotEqual(grey, WidgetRenderer.TrayMark(TrayPart.Cpu, light).ToArgb());
        }
    }

    [Fact]
    public void Taskbar_sensors_are_saved_tidy_with_no_limit()
    {
        var s = new RigsightSettings { TraySensors = ["/a", "", "/b", "/a", " ", .. Enumerable.Range(0, 30).Select(i => $"/s{i}")] };
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal(32, back.TraySensors.Count);
        Assert.Equal(["/a", "/b"], back.TraySensors.Take(2));
        Assert.Equal(TrayStyle.Strip, back.TrayStyle);
    }

    [Fact]
    public void Everyone_moves_to_the_strip_whatever_their_icons_were()
    {
        // Settings from before 0.8.1 had "all in one" as TrayCombined: gone, and the strip is how readings show now.
        var old = SettingsStore.Deserialize("""{ "SettingsVersion": 8, "TraySensors": ["/a"], "TrayCombined": true }""");
        Assert.Equal((TrayStyle.Strip, "/a"), (old.TrayStyle, old.TraySensors.Single()));
        var chosen = SettingsStore.Deserialize(SettingsStore.Serialize(new RigsightSettings { TrayStyle = TrayStyle.Grouped }));
        Assert.Equal(TrayStyle.Grouped, chosen.TrayStyle);
    }

    [Theory]
    [InlineData(SensorKind.Temperature, 61.6, "62°")]
    [InlineData(SensorKind.Load, 7.2, "7%")]
    [InlineData(SensorKind.Power, 118.4, "118 W")]
    [InlineData(SensorKind.Clock, 4450, "4.5 GHz")]
    [InlineData(SensorKind.Clock, 800, "800 MHz")]
    [InlineData(SensorKind.Fan, 1245, "1.2k rpm")]
    [InlineData(SensorKind.Voltage, 1.244, "1.24 V")]
    [InlineData(SensorKind.SmallData, 3300, "3.2 GB")]
    [InlineData(SensorKind.Throughput, 5 * 1024 * 1024, "5.0 MB/s")]
    [InlineData(SensorKind.Factor, 1.5, "1.5")]
    public void The_strip_has_room_for_units(SensorKind kind, double value, string text) => Assert.Equal(text, Units.StripText(kind, value));

    [Fact]
    public void A_missing_reading_on_the_strip_is_a_dash_without_a_unit() => Assert.Equal("–", Units.StripText(SensorKind.Temperature, null));

    [Fact]
    public void The_strip_names_parts_numbering_those_that_come_twice()
    {
        Assert.Equal(["CPU", "GPU 1", "RAM", "GPU 2"], TrayParts.Names([TrayPart.Cpu, TrayPart.Gpu, TrayPart.Ram, TrayPart.Gpu]));
        // A part's readings all go together on the strip, however many.
        var clock = Cpu("Core #1", SensorKind.Clock, 4450);
        var icons = TrayParts.Group([clock, CpuLoad, CpuTemp, GpuTemp], r => (r.Group, r.Kind), int.MaxValue);
        Assert.Equal([[CpuTemp, CpuLoad, clock], [GpuTemp]], icons);
    }

    private static StripCell Cell(string name, params string[] values) =>
        new(name, Color.CornflowerBlue, [.. values.Select((v, i) => new StripValue(v, Color.White, i == 0 ? name : "Load"))]);

    [Theory]
    [InlineData("9%", "88%")]
    [InlineData("11%", "88%")]
    [InlineData("100%", "888%")]
    [InlineData("56°", "88°")]
    [InlineData("0 MHz", "88 MHz")]
    [InlineData("4.5 GHz", "8.8 GHz")]
    [InlineData("111 W", "888 W")]
    [InlineData("1.2k rpm", "8.8k rpm")]
    [InlineData("–", "–")]
    public void A_readings_place_is_kept_for_its_widest_digits(string text, string shape) => Assert.Equal(shape, TaskbarStrip.Shape(text));

    [Fact]
    public void Numbers_changing_dont_move_the_strip()
    {
        // The strip sits against the tray: were it narrower for "9%" than "11%", everything on it would jump.
        StripCell Showing(string load, string power) => new("GPU", Color.CornflowerBlue,
            [new("53°", Color.White, "GPU", "88°"), new(load, Color.White, "Load", "88%"), new(power, Color.White, "Power", "888 W")]);
        using var a = WidgetRenderer.RenderStrip([Showing("9%", "40 W")], 48, 1, out var edgesA);
        using var b = WidgetRenderer.RenderStrip([Showing("11%", "111 W")], 48, 1, out var edgesB);
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(edgesA, edgesB);
        // Right-aligned in its place: the last digit ends at the same spot either way.
        Assert.Equal(RightmostInk(a), RightmostInk(b));
    }

    [Fact]
    public void Every_word_under_the_numbers_is_as_bold_as_the_parts_name()
    {
        // A user saw "Load" and "Power" look fainter than "CPU" and "GPU": they were drawn regular and see-through.
        // The same word under the first reading (the part's name's place) and the second must come out the same.
        var cell = new StripCell("GPU", Color.FromArgb(91, 140, 255), [new("5%", Color.White, "Load"), new("5%", Color.White, "Load")]);
        using var bmp = WidgetRenderer.RenderStrip([cell], 48, 1, out _);
        (int Ink, int Strongest) Words(int from, int to)
        {
            int ink = 0, strongest = 0;
            for (int x = from; x < to; x++)
                for (int y = bmp.Height / 2; y < bmp.Height; y++)
                    if (bmp.GetPixel(x, y) is { A: > 100 } c && c.B > 200 && c.R < 150) { ink++; strongest = Math.Max(strongest, c.A); }
            return (ink, strongest);
        }
        var (first, second) = (Words(0, bmp.Width / 2), Words(bmp.Width / 2, bmp.Width));
        Assert.Equal(first.Strongest, second.Strongest);
        Assert.InRange(second.Ink, first.Ink * 0.9, first.Ink * 1.1);
    }

    private static int RightmostInk(Bitmap bmp)
    {
        for (int x = bmp.Width - 1; x >= 0; x--)
            for (int y = 0; y < bmp.Height / 2; y++)
                if (bmp.GetPixel(x, y).A > 128) return x;
        return -1;
    }

    [Theory]
    [InlineData(SensorKind.Load, "CPU Total", TrayPart.Cpu, "Load")]
    [InlineData(SensorKind.Clock, "Core #1 (Effective)", TrayPart.Cpu, "Clock")]
    [InlineData(SensorKind.Temperature, "CCD1 (Tdie)", TrayPart.Cpu, "CCD1")]
    [InlineData(SensorKind.Temperature, "Core (Tctl/Tdie)", TrayPart.Cpu, "Temp")]
    [InlineData(SensorKind.Temperature, "GPU Hot Spot", TrayPart.Gpu, "Hot")]
    [InlineData(SensorKind.Temperature, "GPU Memory Junction", TrayPart.Gpu, "VRAM")]
    [InlineData(SensorKind.Load, "GPU Memory", TrayPart.Gpu, "VRAM")]
    [InlineData(SensorKind.Load, "GPU Video Engine", TrayPart.Gpu, "Video")]
    [InlineData(SensorKind.Power, "GPU Package", TrayPart.Gpu, "Power")]
    [InlineData(SensorKind.Clock, "GPU Memory", TrayPart.Gpu, "Mem")]
    [InlineData(SensorKind.Load, "Memory", TrayPart.Ram, "Used")]
    [InlineData(SensorKind.Load, "Virtual Memory", TrayPart.Ram, "Virtual")]
    [InlineData(SensorKind.Temperature, "DIMM #1", TrayPart.Ram, "DIMM")]
    [InlineData(SensorKind.Fan, "Fan #2", TrayPart.Board, "Fan")]
    [InlineData(SensorKind.Throughput, "Download Speed", TrayPart.Network, "Down")]
    [InlineData(SensorKind.Factor, "Throttling", TrayPart.Other, "Thrott")]
    public void Each_reading_on_the_strip_gets_a_short_word(SensorKind kind, string name, TrayPart part, string word) =>
        Assert.Equal(word, TrayParts.ShortLabel(kind, name, part));

    [Fact]
    public void The_parts_name_heads_its_first_reading_and_a_short_name_of_the_users_wins()
    {
        Assert.Equal(["GPU", "VRAM", "Load", "Power"], TrayParts.StripLabels(["Temp", "VRAM", "Load", "Power"], "GPU"));
        Assert.Equal("Tjunction", TrayParts.ShortLabel(SensorKind.Temperature, "GPU Memory Junction", TrayPart.Gpu, " Tjunction "));
        Assert.Equal("VRAM", TrayParts.ShortLabel(SensorKind.Temperature, "GPU Memory Junction", TrayPart.Gpu, "My graphics memory temperature")); // too long
    }

    [Theory]
    [InlineData(48, 1.0f)]
    [InlineData(72, 1.5f)]
    public void The_strip_puts_each_parts_name_under_its_numbers_on_a_normal_taskbar(int height, float scale)
    {
        using var bmp = WidgetRenderer.RenderStrip([Cell("CPU", "62°", "35%"), Cell("GPU", "71°", "98%")], height, scale, out var edges);
        Assert.Equal(height, bmp.Height);
        Assert.Equal(2, edges.Length);
        Assert.True(edges[0] < edges[1] && edges[1] <= bmp.Width + 1);
        // Numbers in the upper half, names in the lower (the name is the only blue).
        var (numbers, names) = (Rows(bmp, c => c.R > 200 && c.G > 200 && c.B > 200), Rows(bmp, c => c.B > 200 && c.R < 150));
        Assert.True(numbers.Max() < names.Min() + 2, $"numbers {numbers.Min()}–{numbers.Max()}, names {names.Min()}–{names.Max()}");
        Assert.All(new[] { bmp.GetPixel(0, 0), bmp.GetPixel(bmp.Width - 1, height - 1) }, c => Assert.Equal(1, c.A)); // there to click, not to see
    }

    [Fact]
    public void On_a_short_taskbar_the_name_goes_before_the_numbers()
    {
        int height = (int)WidgetRenderer.StripTwoLineHeight - 4;
        using var two = WidgetRenderer.RenderStrip([Cell("CPU", "62°")], 48, 1, out _);
        using var one = WidgetRenderer.RenderStrip([Cell("CPU", "62°")], height, 1, out _);
        Assert.True(one.Width > two.Width, "side by side is wider");
        var (numbers, names) = (Rows(one, c => c.R > 200 && c.G > 200 && c.B > 200), Rows(one, c => c.B > 200 && c.R < 150));
        Assert.True(names.Min() >= numbers.Min() - 3 && names.Max() <= numbers.Max() + 3, "on the same line");
    }

    private static List<int> Rows(Bitmap bmp, Func<Color, bool> match)
    {
        var rows = new List<int>();
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) is { A: > 200 } c && match(c)) { rows.Add(y); break; }
        Assert.NotEmpty(rows);
        return rows;
    }
}
