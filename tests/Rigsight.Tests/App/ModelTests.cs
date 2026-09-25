using System.Windows;
using System.Windows.Media;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.App;

/// <summary>The ring buffer behind every chart. No UI involved, so these run alongside everything else.</summary>
public sealed class HistoryBufferTests
{
    private const long T0 = 1_750_000_000_000;

    [Fact]
    public void Empty()
    {
        var b = new HistoryBuffer(10);
        Assert.Equal((0, 0L, 0L, 10), (b.Count, b.FirstTime, b.LastTime, b.Capacity));
        Assert.Equal(-1, b.NearestIndex(T0));
        Assert.Equal(0, b.IndexAtOrAfter(T0));
    }

    [Fact]
    public void Samples_are_kept_in_order_until_full()
    {
        var b = new HistoryBuffer(200);
        for (int i = 0; i < 150; i++) b.Add(T0 + i * 1000, i);
        Assert.Equal(150, b.Count);
        Assert.Equal(T0, b.FirstTime);
        Assert.Equal(T0 + 149_000, b.LastTime);
        for (int i = 0; i < 150; i++) Assert.Equal((T0 + i * 1000, (double)i), (b.TimeAt(i), b.ValueAt(i)));
    }

    [Fact]
    public void A_full_buffer_drops_the_oldest()
    {
        var b = new HistoryBuffer(100);
        for (int i = 0; i < 1234; i++) b.Add(T0 + i * 1000, i);
        Assert.Equal(100, b.Count);
        Assert.Equal(T0 + 1134_000, b.FirstTime);
        Assert.Equal(T0 + 1233_000, b.LastTime);
        for (int i = 0; i < 100; i++) Assert.Equal(1134 + i, b.ValueAt(i));
    }

    [Fact]
    public void A_capacity_of_one_keeps_the_newest()
    {
        var b = new HistoryBuffer(1);
        b.Add(T0, 1);
        b.Add(T0 + 5, 2);
        Assert.Equal((1, T0 + 5, 2.0), (b.Count, b.LastTime, b.ValueAt(0)));
    }

    [Fact]
    public void Values_are_stored_as_floats_and_nan_marks_a_gap()
    {
        var b = new HistoryBuffer(10);
        b.Add(T0, 1.0 / 3);
        b.Add(T0 + 1, double.NaN);
        b.Add(T0 + 2, double.PositiveInfinity);
        Assert.Equal((float)(1.0 / 3), b.ValueAt(0));
        Assert.True(double.IsNaN(b.ValueAt(1)));
        Assert.True(double.IsPositiveInfinity(b.ValueAt(2)));
    }

    [Theory]
    [InlineData(T0 - 5000, 0, 0)]
    [InlineData(T0, 0, 0)]
    [InlineData(T0 + 400, 1, 0)]
    [InlineData(T0 + 600, 1, 1)]
    [InlineData(T0 + 1000, 1, 1)]
    [InlineData(T0 + 9000, 9, 9)]
    [InlineData(T0 + 50_000, 10, 9)]
    public void Finding_samples_by_time(long time, int atOrAfter, int nearest)
    {
        var b = new HistoryBuffer(64);
        for (int i = 0; i < 10; i++) b.Add(T0 + i * 1000, i);
        Assert.Equal(atOrAfter, b.IndexAtOrAfter(time));
        Assert.Equal(nearest, b.NearestIndex(time));
    }

    [Fact]
    public void Finding_samples_after_wrapping()
    {
        var b = new HistoryBuffer(64);
        for (int i = 0; i < 1000; i++) b.Add(T0 + i * 1000, i);
        Assert.Equal(0, b.IndexAtOrAfter(T0));
        Assert.Equal(10, b.IndexAtOrAfter(T0 + 946_000));
        Assert.Equal(63, b.NearestIndex(T0 + 10_000_000));
    }

    [Fact]
    public void Clear_empties_it_for_reuse()
    {
        var b = new HistoryBuffer(8);
        for (int i = 0; i < 20; i++) b.Add(T0 + i, i);
        b.Clear();
        Assert.Equal(0, b.Count);
        b.Add(T0 + 99_999_999, 5);
        Assert.Equal((1, T0 + 99_999_999, 5.0), (b.Count, b.FirstTime, b.ValueAt(0)));
    }

    [Fact]
    public void Days_of_samples_are_rebased_and_keep_exact_times()
    {
        var b = new HistoryBuffer(3600);
        long step = 5 * 60_000; // every 5 minutes for 12 days: well past the first rebase
        for (int i = 0; i < 3456; i++) b.Add(T0 + i * step, i);
        Assert.Equal(T0, b.FirstTime);
        Assert.Equal(T0 + 3455 * step, b.LastTime);
        for (int i = 0; i < 3456; i += 97) Assert.Equal(T0 + i * step, b.TimeAt(i));
        Assert.Equal(1440, b.IndexAtOrAfter(T0 + 1440 * step));
        // And on, wrapping, for another week.
        for (int i = 3456; i < 5472; i++) b.Add(T0 + i * step, i);
        Assert.Equal(3600, b.Count);
        for (int i = 0; i < 3600; i += 53) Assert.Equal(T0 + (1872 + i) * step, b.TimeAt(i));
    }

    [Fact]
    public void Weeks_of_sparse_samples_never_overflow_their_times()
    {
        // Few readings over weeks (the PC mostly asleep with the window open): too sparse to wrap.
        var b = new HistoryBuffer(3600);
        long step = 20 * 60_000;
        for (int i = 0; i < 2880; i++)
        {
            b.Add(T0 + i * step, i);
            Assert.Equal(T0 + i * step, b.LastTime);
        }
        for (int i = 1; i < b.Count; i++) Assert.True(b.TimeAt(i) > b.TimeAt(i - 1));
        Assert.True(b.LastTime - b.FirstTime <= int.MaxValue);
        Assert.Equal(b.Count - 1, b.NearestIndex(b.LastTime + 1));
    }

    [Fact]
    public void A_sample_a_month_after_the_last_one_keeps_its_time()
    {
        // The window stayed open while the PC slept for a month: the next reading is far past everything kept.
        var b = new HistoryBuffer(3600);
        for (int i = 0; i < 100; i++) b.Add(T0 + i * 1000, i);
        long later = T0 + 30L * 24 * 3600_000;
        b.Add(later, 42);
        Assert.Equal(later, b.LastTime);
        Assert.Equal(42, b.ValueAt(b.Count - 1));
        Assert.Equal(b.Count - 1, b.NearestIndex(later));
        for (int i = 1; i < b.Count; i++) Assert.True(b.TimeAt(i) > b.TimeAt(i - 1));
    }
}

/// <summary>A sensor, a hardware card, a chart line and a drive summary.</summary>
[Collection("UI")]
public sealed class SensorModelTests
{
    private static SensorItem Sensor(SensorKind kind, string hardwareType = "Cpu", string name = "Thing") =>
        new(new SensorMeta { Id = "/x/" + kind, Name = name, Kind = kind }, "Some hardware", hardwareType);

    [Theory]
    [InlineData(SensorKind.Temperature, 45.26, "45.3 °C", "45°", "TEMP")]
    [InlineData(SensorKind.Load, 37.04, "37.0 %", "37%", "LOAD")]
    [InlineData(SensorKind.Clock, 4450, "4.45 GHz", "4.45 GHz", "CLOCK")]
    [InlineData(SensorKind.Clock, 800, "800 MHz", "800 MHz", "CLOCK")]
    [InlineData(SensorKind.Power, 65.5, "65.5 W", "66 W", "POWER")]
    [InlineData(SensorKind.Voltage, 1.1234, "1.123 V", "1.123 V", "VOLT")]
    [InlineData(SensorKind.Fan, 1200.4, "1200 RPM", "1200 RPM", "FAN")]
    [InlineData(SensorKind.Data, 12.34, "12.3 GB", "12.3 GB", "DATA")]
    [InlineData(SensorKind.SmallData, 512, "512 MB", "512 MB", "DATA")]
    [InlineData(SensorKind.Throughput, 2048, "2 KB/s", "2 KB/s", "RATE")]
    [InlineData(SensorKind.Control, 40, "40.0 %", "40%", "CTRL")]
    [InlineData(SensorKind.Factor, 12345, "12345", "12345", "INFO")]
    public void Readings_are_formatted_by_kind(SensorKind kind, double value, string formatted, string shortText, string type)
    {
        Ui.Run(() =>
        {
            var s = Sensor(kind);
            s.Push(1000, (float)value);
            Assert.Equal(formatted, s.FormattedValue);
            Assert.Equal(shortText, s.ShortValue);
            Assert.Equal(type, s.TypeLabel);
            Assert.Equal(formatted, s.FormattedMin);
            Assert.Equal(formatted, s.FormattedMax);
            Assert.Equal(formatted, s.FormattedAverage);
        });
    }

    [Fact]
    public void Nothing_read_yet_shows_dashes()
    {
        var s = Sensor(SensorKind.Temperature);
        Assert.Equal(("—", "—", "—", "—", ""), (s.FormattedValue, s.ShortValue, s.FormattedMin, s.FormattedMax, s.MinMaxText));
        Assert.Equal(0, s.Version);
    }

    [Fact]
    public void Each_reading_announces_every_text_and_moves_the_version()
    {
        var s = Sensor(SensorKind.Load);
        var changed = Kit.Changes(s, () => s.Push(1000, 5));
        foreach (var p in new[] { nameof(s.Value), nameof(s.FormattedValue), nameof(s.ShortValue), nameof(s.FormattedMin), nameof(s.FormattedMax),
                     nameof(s.FormattedAverage), nameof(s.MinMaxText), nameof(s.Version), nameof(s.Min), nameof(s.Max), nameof(s.Average) })
            Assert.Contains(p, changed);
        s.Push(2000, 5);
        Assert.Equal(2, s.Version);
    }

    [Fact]
    public void Todays_range_replaces_the_one_since_opened()
    {
        var s = Sensor(SensorKind.Temperature);
        s.Push(1000, 50);
        var changed = Kit.Changes(s, () => s.SetTodayRange(31, 88));
        Assert.Equal((31.0, 88.0), (s.Min, s.Max));
        Assert.Contains(nameof(s.MinMaxText), changed);
        Assert.Equal(50, s.Average); // the average is still of what was read
    }

    [Fact]
    public void Renaming_and_hiding_raise_a_preference_change_once_each()
    {
        var s = Sensor(SensorKind.Load, name: "CPU Total");
        int raised = 0;
        s.UserPreferenceChanged += _ => raised++;
        s.Label = "Busy";
        s.Label = " Busy ";
        Assert.Equal(1, raised);
        Assert.Equal(("Busy", "Busy", "Busy"), (s.CustomLabel, s.DisplayName, s.Label));
        s.Label = "CPU Total";
        Assert.Null(s.CustomLabel);
        s.ToggleHidden();
        s.ToggleHidden();
        Assert.Equal(4, raised);
        Assert.False(s.IsHidden);
        s.CustomLabel = "   "; // set directly (from settings): blank shows the sensor's own name
        Assert.Equal("CPU Total", s.DisplayName);
    }

    [Fact]
    public void Refresh_formatting_tells_every_binding()
    {
        var s = Sensor(SensorKind.Temperature);
        Assert.Equal([""], Kit.Changes(s, s.RefreshFormatting));
    }

    [Fact]
    public void Seeding_history_keeps_gaps_and_leaves_readings_alone()
    {
        var s = Sensor(SensorKind.Temperature);
        s.Seed([1000, 2000, 60_000, 61_000], [40, null, 42, 43, 99]);
        Assert.Equal(5, s.History.Count); // a gap was added before 60 s
        Assert.True(double.IsNaN(s.History.ValueAt(1)));
        Assert.True(double.IsNaN(s.History.ValueAt(2)));
        Assert.Null(s.Value);
        Assert.Equal(0, s.Version);
        s.Seed([], []);
        Assert.Equal(5, s.History.Count);
    }

    [Fact]
    public void History_holds_an_hour_of_readings()
    {
        var s = Sensor(SensorKind.Load);
        for (int i = 0; i < 4000; i++) s.Push(i * 1000L, i % 100);
        Assert.Equal(3600, s.History.Count);
        Assert.Equal(3999_000, s.History.LastTime);
    }

    [Theory]
    [InlineData("Cpu", "CPU", false)]
    [InlineData("GpuNvidia", "GPU", true)]
    [InlineData("GpuAmd", "GPU", true)]
    [InlineData("GpuIntel", "GPU", true)]
    [InlineData("Motherboard", "BOARD", false)]
    [InlineData("SuperIO", "BOARD", false)]
    [InlineData("EmbeddedController", "BOARD", false)]
    [InlineData("Memory", "RAM", false)]
    [InlineData("Storage", "DRIVE", false)]
    [InlineData("Cooler", "COOLER", false)]
    [InlineData("Psu", "PSU", false)]
    [InlineData("Battery", "BATTERY", false)]
    [InlineData("Network", "NET", false)]
    [InlineData("Whatever", "DEVICE", false)]
    public void Hardware_badges(string type, string badge, bool gpu)
    {
        var node = new HardwareNode("x", type);
        Assert.Equal((badge, gpu), (node.Badge, node.IsGpu));
        Assert.Same(node.End, node.End);
        Assert.Same(node, node.End.Owner);
        Assert.True(node.IsExpanded);
        Assert.True(node.HasVisibleSensors);
    }

    // ── Chart lines ──────────────────────────────────────────────────────

    [Fact]
    public void Chart_lines_use_the_theme_colour_and_remake_it_when_the_theme_changes()
    {
        var series = new ChartSeries("CPU", Sensor(SensorKind.Temperature), "CpuColor");
        try
        {
            Ui.Run(() =>
            {
                ThemeManager.Apply(ThemeManager.Dark);
                var dark = series.Brush;
                Assert.Equal((Color)Application.Current.FindResource("CpuColor"), series.Color);
                Assert.Same(dark, series.Brush);
                Assert.Same(dark, series.LinePen.Brush);
                Assert.True(series.LinePen.IsFrozen && series.Fill.IsFrozen && dark.IsFrozen);
                ThemeManager.Apply(ThemeManager.Light);
                Assert.NotSame(dark, series.Brush);
                Assert.Equal((Color)Application.Current.FindResource("CpuColor"), series.Color);
            });
        }
        finally
        {
            Ui.Run(() => ThemeManager.Apply(ThemeManager.Dark));
        }
        Assert.Equal("CPU", series.Label);
    }

    [Fact]
    public void Minute_history_breaks_where_the_pc_was_off()
    {
        var series = new ChartSeries("GPU", Sensor(SensorKind.Temperature), "GpuColor", m => m.GpuTemp);
        long t = 1_750_000_000;
        var minutes = new List<SystemMinute>
        {
            new() { Ts = t, GpuTemp = 40 },
            new() { Ts = t + 60, GpuTemp = null },
            new() { Ts = t + 120, GpuTemp = 42 },
            new() { Ts = t + 600, GpuTemp = 50 }, // eight minutes later
        };
        series.LoadMinutes(minutes);
        Assert.Equal(5, series.Minutes.Count);
        Assert.Equal((t * 1000 + 30_000, 40.0), (series.Minutes.TimeAt(0), series.Minutes.ValueAt(0)));
        Assert.True(double.IsNaN(series.Minutes.ValueAt(1)));
        Assert.True(double.IsNaN(series.Minutes.ValueAt(3)));
        Assert.Equal(50, series.Minutes.ValueAt(4));
        // Loading again replaces it.
        series.LoadMinutes(minutes.Take(1));
        Assert.Equal(1, series.Minutes.Count);
        series.LoadMinutes([]);
        Assert.Equal(0, series.Minutes.Count);
    }

    [Fact]
    public void A_line_without_minute_history_ignores_it()
    {
        var series = new ChartSeries("X", Sensor(SensorKind.Temperature), "CpuColor");
        series.LoadMinutes([new SystemMinute { Ts = 1, CpuTemp = 5 }]);
        Assert.Equal(0, series.Minutes.Count);
        Assert.Null(series.FromMinute);
    }

    [Fact]
    public void Minute_history_holds_a_day_and_an_hour()
    {
        var series = new ChartSeries("X", Sensor(SensorKind.Temperature), "CpuColor", m => m.CpuTemp);
        series.LoadMinutes(Enumerable.Range(0, 3000).Select(i => new SystemMinute { Ts = 1_750_000_000 + i * 60L, CpuTemp = i }));
        Assert.Equal(1500, series.Minutes.Count);
        Assert.Equal(2999, series.Minutes.ValueAt(1499));
    }

    // ── Drives ───────────────────────────────────────────────────────────

    [Fact]
    public void An_ssd_shows_its_wear_and_follows_it()
    {
        Ui.Run(() =>
        {
            var life = Sensor(SensorKind.Level, "Storage", "Life");
            var drive = new DriveSummary("SSD", null, life, null, null);
            Assert.True(drive.HasHealth);
            Assert.Equal("—", drive.HealthText);
            var changed = Kit.Changes(drive, () => life.Push(1000, 97));
            Assert.Contains(nameof(DriveSummary.HealthText), changed);
            Assert.Equal("97%", drive.HealthText);
            Assert.Equal("How much of its rated wear the SSD has left.", drive.HealthToolTip);
            drive.Health = new DriveHealthInfo { Name = "SSD", Status = "Good" };
            Assert.Equal("97%", drive.HealthText);
            Assert.EndsWith("The percentage is how much of its rated wear the SSD has left.", drive.HealthToolTip);
            Assert.Null(drive.SectorsText);
        });
    }

    [Theory]
    [InlineData("Good", "GpuBrush", "Good")]
    [InlineData("Caution", "WarmBrush", "Caution")]
    [InlineData("Bad", "HotBrush", "Bad")]
    [InlineData("Unknown", "GpuBrush", "Unknown")]
    public void A_hard_drive_shows_its_smart_status(string status, string brush, string text)
    {
        Ui.Run(() =>
        {
            var drive = new DriveSummary("HDD", null, null, null, null);
            Assert.False(drive.HasHealth); // nothing known yet
            var changed = Kit.Changes(drive, () => drive.Health = new DriveHealthInfo { Name = "HDD", Status = status });
            Assert.Equal(status != "Unknown", drive.HasHealth);
            Assert.Equal(text, drive.HealthText);
            Assert.Same(Application.Current.FindResource(brush), drive.HealthBrush);
            Assert.False(string.IsNullOrEmpty(drive.HealthToolTip));
            foreach (var p in new[] { nameof(drive.HasHealth), nameof(drive.HealthText), nameof(drive.HealthBrush), nameof(drive.SectorsText), nameof(drive.HealthToolTip) })
                Assert.Contains(p, changed);
        });
    }

    [Fact]
    public void Bad_sectors_are_counted_in_words()
    {
        Ui.Run(() =>
        {
            var drive = new DriveSummary("HDD", null, null, null, null);
            drive.Health = new DriveHealthInfo { Status = "Good", ReallocatedSectors = 0, PendingSectors = 0, UncorrectableSectors = 0 };
            Assert.Equal("No bad sectors", drive.SectorsText);
            drive.Health = new DriveHealthInfo { Status = "Caution", ReallocatedSectors = 1200, PendingSectors = 3, UncorrectableSectors = null };
            Assert.Equal("1,200 reallocated, 3 pending sectors", drive.SectorsText);
            drive.Health = new DriveHealthInfo { Status = "Bad", UncorrectableSectors = 8 };
            Assert.Equal("8 unreadable sectors", drive.SectorsText);
            drive.Health = new DriveHealthInfo { Status = "Good" };
            Assert.Null(drive.SectorsText);
            drive.Health = null;
            Assert.Null(drive.SectorsText);
            Assert.Equal("Unknown", drive.HealthText);
        });
    }
}

/// <summary>App icons, the PC summary for crash reports, and switching themes.</summary>
[Collection("UI")]
public sealed class ServiceTests
{
    private static readonly string Explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    [Fact]
    public void Icons_come_from_the_program_and_are_made_once()
    {
        Ui.Run(() =>
        {
            var icon = IconCache.Get(Explorer);
            Assert.NotNull(icon);
            Assert.True(icon!.IsFrozen);
            Assert.Same(icon, IconCache.Get(Explorer));
            Assert.Same(icon, IconCache.Get(Explorer.ToUpperInvariant())); // paths aren't case sensitive
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\definitely\missing\app.exe")]
    [InlineData(@"C:\bad|path<>.exe")]
    [InlineData("relative.exe")]
    public void Paths_without_an_icon_give_none(string? path)
    {
        Ui.Run(() =>
        {
            Assert.Null(IconCache.Get(path));
            Assert.Null(IconCache.Get(path)); // and asking again is just as quiet
        });
    }

    [Fact]
    public void A_file_that_is_not_a_program_gives_its_type_icon_or_none()
    {
        var file = Path.Combine(TestEnvironment.NewFolder("icons"), "not-a-program.exe");
        File.WriteAllText(file, "hello");
        Ui.Run(() => IconCache.Get(file)); // no exception, whatever Windows makes of it
    }

    [Fact]
    public void The_plain_program_icon_is_always_there()
    {
        Ui.Run(() =>
        {
            Assert.NotNull(IconCache.Program);
            Assert.Same(IconCache.Program, IconCache.Program);
        });
    }

    [Fact]
    public void Pc_summary_for_crash_reports()
    {
        var text = PcInfo.Text;
        Assert.Contains("RAM: ", text);
        Assert.Matches(@"OS: Windows 1[01] .*\(build \d+\)", text);
        Assert.Contains("CPU: ", text);
        Assert.Same(text, PcInfo.Text);
    }

    [Fact]
    public void Switching_between_dark_and_light()
    {
        try
        {
            Ui.Run(() =>
            {
                ThemeManager.Apply(ThemeManager.Dark);
                int changed = 0;
                void OnChanged() => changed++;
                ThemeManager.Changed += OnChanged;
                try
                {
                    int version = ThemeManager.Version;
                    var darkBg = (Color)Application.Current.FindResource("BgColor");
                    var darkHot = Controls.ChartPaint.Hot;

                    ThemeManager.Apply(ThemeManager.Light);
                    Assert.True(ThemeManager.IsLight);
                    Assert.Equal(version + 1, ThemeManager.Version);
                    Assert.Equal(1, changed);
                    Assert.NotEqual(darkBg, (Color)Application.Current.FindResource("BgColor"));
                    Assert.NotSame(darkHot, Controls.ChartPaint.Hot);
                    Assert.Equal(ThemeMode.Light, Application.Current.ThemeMode);
                    Assert.Single(Application.Current.Resources.MergedDictionaries, d => d.Source?.OriginalString.EndsWith("Themes/Light.xaml") == true);
                    Assert.DoesNotContain(Application.Current.Resources.MergedDictionaries, d => d.Source?.OriginalString.EndsWith("Themes/Dark.xaml") == true);

                    // The same theme again changes nothing.
                    ThemeManager.Apply(ThemeManager.Light);
                    Assert.Equal((version + 1, 1), (ThemeManager.Version, changed));

                    ThemeManager.Apply(ThemeManager.Dark);
                    Assert.False(ThemeManager.IsLight);
                    Assert.Equal((version + 2, 2), (ThemeManager.Version, changed));
                    Assert.Equal(darkBg, (Color)Application.Current.FindResource("BgColor"));
                    Assert.Equal(ThemeMode.Dark, Application.Current.ThemeMode);

                    // Anything unknown is dark.
                    ThemeManager.Apply("purple");
                    ThemeManager.Apply(null);
                    Assert.False(ThemeManager.IsLight);
                    Assert.Equal(2, changed);
                }
                finally
                {
                    ThemeManager.Changed -= OnChanged;
                }
            });
        }
        finally
        {
            Ui.Run(() => ThemeManager.Apply(ThemeManager.Dark));
        }
    }

    [Fact]
    public void System_follows_windows_app_mode()
    {
        bool windowsLight;
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            windowsLight = key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        try
        {
            Ui.Run(() =>
            {
                ThemeManager.Apply(ThemeManager.System);
                Assert.Equal(windowsLight, ThemeManager.IsLight);
            });
        }
        finally
        {
            Ui.Run(() => ThemeManager.Apply(ThemeManager.Dark));
        }
    }

    [Fact]
    public void Both_themes_define_every_colour_the_app_asks_for()
    {
        string[] keys =
        [
            "BgColor", "TextColor", "MutedColor", "FaintColor", "StrokeColor", "Surface2Color", "TooltipColor", "CpuColor", "GpuColor",
            "CoolColor", "GoodColor", "WarmColor", "OrangeColor", "HotColor", "PurpleColor", "PinkColor",
            "HotBrush", "WarmBrush", "GpuBrush", "CpuBrush", "PurpleBrush", "AccentBrush", "MutedBrush", "StrokeBrush", "FaintBrush", "HoverBrush",
        ];
        try
        {
            foreach (var theme in new[] { ThemeManager.Dark, ThemeManager.Light })
            {
                Ui.Run(() => ThemeManager.Apply(theme));
                var missing = Ui.Run(() => keys.Where(k => Application.Current.TryFindResource(k) is null).ToList());
                Assert.True(missing.Count == 0, $"{theme} lacks: {string.Join(", ", missing)}");
            }
        }
        finally
        {
            Ui.Run(() => ThemeManager.Apply(ThemeManager.Dark));
        }
    }
}
