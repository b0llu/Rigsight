using System.Drawing;
using Rigsight.Agent.Ui;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Agent;

/// <summary>Sensors in the taskbar: the short numbers, the combined icon's turns, the tooltips and the drawing.</summary>
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

    private static TrayReading R(int i) => new($"/s/{i}", $"Sensor {i}", SensorKind.Temperature, 50 + i);

    [Fact]
    public void The_combined_icon_shows_two_at_a_time_in_turn()
    {
        var one = new[] { R(1) };
        var two = new[] { R(1), R(2) };
        var five = Enumerable.Range(1, 5).Select(R).ToArray();
        Assert.Equal(one, TrayReadings.Turn(one, 123_456));
        Assert.Equal(two, TrayReadings.Turn(two, 123_456));
        Assert.Equal([R(1), R(2)], TrayReadings.Turn(five, 0));
        Assert.Equal([R(3), R(4)], TrayReadings.Turn(five, TrayReadings.TurnMs));
        Assert.Equal([R(5)], TrayReadings.Turn(five, 2 * TrayReadings.TurnMs));
        Assert.Equal([R(1), R(2)], TrayReadings.Turn(five, 3 * TrayReadings.TurnMs)); // and round again
    }

    [Fact]
    public void Tooltips_name_each_reading_within_windows_limit()
    {
        Assert.Equal($"Sensor 1: {Units.Format(SensorKind.Temperature, 51)}", TrayReadings.Tooltip(R(1)));
        var few = Enumerable.Range(1, 3).Select(R).ToArray();
        Assert.Equal(3, TrayReadings.CombinedTooltip(few).Split('\n').Length);

        var many = Enumerable.Range(1, 20).Select(i => R(i) with { Name = $"A rather long sensor name {i}" }).ToArray();
        string tip = TrayReadings.CombinedTooltip(many);
        Assert.True(tip.Length <= 127, $"{tip.Length} characters");
        Assert.Matches(@"…and \d+ more$", tip);
        Assert.StartsWith("A rather long sensor name 1:", tip);

        string longName = TrayReadings.Tooltip(R(1) with { Name = new string('x', 200) });
        Assert.Equal(127, longName.Length);
    }

    [Fact]
    public void Right_click_names_what_it_removes()
    {
        Assert.Equal($"Remove Sensor 1 · {Units.Format(SensorKind.Temperature, 51)}", TrayReadings.RemoveText(false, R(1)));
        Assert.Equal("Remove all from the taskbar", TrayReadings.RemoveText(true, null));
        Assert.Equal("Remove from the taskbar", TrayReadings.RemoveText(false, R(1) with { Name = new string('x', 60) }));
        Assert.Equal("Remove from the taskbar", TrayReadings.RemoveText(false, null));
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

    [Fact]
    public void Temperatures_take_their_colour_other_readings_the_taskbars()
    {
        Assert.NotEqual(WidgetRenderer.TrayColor(SensorKind.Temperature, 40, light: false), WidgetRenderer.TrayColor(SensorKind.Temperature, 90, light: false));
        Assert.Equal(Color.White.ToArgb(), WidgetRenderer.TrayColor(SensorKind.Load, 40, light: false).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), WidgetRenderer.TrayColor(SensorKind.Load, 40, light: true).ToArgb());
    }

    [Fact]
    public void Taskbar_sensors_are_saved_tidy_with_no_limit()
    {
        var s = new RigsightSettings { TraySensors = ["/a", "", "/b", "/a", " ", .. Enumerable.Range(0, 30).Select(i => $"/s{i}")] };
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal(32, back.TraySensors.Count);
        Assert.Equal(["/a", "/b"], back.TraySensors.Take(2));
        Assert.False(back.TrayCombined);
    }
}
