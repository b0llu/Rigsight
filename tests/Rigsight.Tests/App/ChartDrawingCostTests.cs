using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rigsight.Controls;
using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using Rigsight.Core.Stability;
using Rigsight.Models;
using Rigsight.Services;
using Rigsight.Tests.Data;
using Rigsight.Tests.Support;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// The big charts drawn against their budgets. On its own, after the other tests: beside the gate's four other test
/// processes its measures came in up to four times what they take alone (2026-11-11: the crash strip 49.9 and 42.4 ms
/// of 35, where it takes 11), failing with the charts untouched.
/// </summary>
[Collection("UI")]
[Trait("Alone", "timing")]
public sealed class ChartDrawingCostTests
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Drawing_big_series_stays_fast()
    {
        // A second try before failing: with the gate's five test processes side by side a measure came in just over,
        // the code untouched (2026-11-11: 115.7 ms of 115, then 36.2 of 35, where they take 37 and 11 alone). Something
        // really slower is over on both tries.
        var tries = new List<string>();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string? over = MeasureDrawing();
            if (over is null) return;
            tries.Add(over);
        }
        Assert.Fail(string.Join(" || again: ", tries));
    }

    /// <summary>Draws each big chart and says which took longer than its budget, with every measure; null when none did.</summary>
    private static string? MeasureDrawing()
    {
        var results = new List<string>();
        var over = new List<string>();
        Ui.Run(() =>
        {
            // Temperatures move smoothly (a saw-tooth of 40° a minute would mostly measure WPF's software rasterizer).
            double Temp(int i, int j) => 45 + 15 * Math.Sin(j / 90.0 + i) + j % 5 * 0.3;
            var chart = new LineChart { WindowSeconds = 86400, Series = [.. Enumerable.Range(0, 4).Select(i => ControlTests.Series($"S{i}", 3600, 1500, j => Temp(i, j)))] };
            double line = ControlTests.Median(() => { chart.InvalidateVisual(); Draw.Render(chart, 1400, 400); });
            results.Add($"line chart 4×(3600+1500), drawn and rasterized: {line:0.0} ms");
            double lineDraw = ControlTests.Median(() => { chart.InvalidateVisual(); chart.UpdateLayout(); });
            results.Add($"line chart, drawing only: {lineDraw:0.0} ms");
            Draw.Set(chart, "_hoverX", 700.0);
            double hover = ControlTests.Median(() => { chart.InvalidateVisual(); chart.UpdateLayout(); });
            results.Add($"line chart hover: {hover:0.0} ms");

            var spark = new Sparkline { Source = Draw.Buffer(100_000, ControlTests.Now, 100, i => i % 97), WindowSeconds = 10_000 };
            double sparkMs = ControlTests.Median(() => { spark.Version++; Draw.Render(spark, 300, 60); });
            results.Add($"sparkline 100k: {sparkMs:0.0} ms");

            var buffer = Draw.Buffer(100_000, ControlTests.Now, 100, i => i % 97);
            double geometry = ControlTests.Median(() => ChartGeometry.Build(buffer, ControlTests.Now, ControlTests.Now + 10_000_000, ControlTests.Plot, 0, 100, v => v));
            results.Add($"geometry 100k: {geometry:0.0} ms");

            var bars = new DailyBars { Days = ControlTests.Days(3650, d => d.AddDays(1), DateTime.Today.AddDays(-3649), i => i % 50 * 600) };
            double barsMs = ControlTests.Median(() => { bars.InvalidateVisual(); Draw.Render(bars, 1400, 300); });
            results.Add($"daily bars 3650: {barsMs:0.0} ms");

            var strip = new CrashStrip
            {
                Days = CrashStrip.BuildDays(DateTime.Today.AddDays(-3649), DateTime.Today.AddDays(1),
                    CrashGroup.Build(Enumerable.Range(0, 3000).Select(i => Crashes.App(DateTime.Today.AddDays(-i).AddHours(9), $"a{i % 40}.exe"))), []),
            };
            double stripMs = ControlTests.Median(() => { strip.InvalidateVisual(); Draw.Render(strip, 1400, 90); });
            results.Add($"crash strip 3650: {stripMs:0.0} ms");

            var map = new Treemap { Items = ControlTests.Nodes([.. Enumerable.Range(1, 10_000).Select(i => (long)(1_000_000 / i + 1))]) };
            double mapMs = ControlTests.Median(() => { map.InvalidateVisual(); Draw.Render(map, 1400, 700); });
            results.Add($"treemap 10k: {mapMs:0.0} ms");

            var day = DateTime.Today.AddDays(-1);
            var timeline = new DayTimeline
            {
                Day = day,
                Segments = [.. Enumerable.Range(0, 1440).Select(i => new TimelineSegment { Start = day.AddMinutes(i), End = day.AddMinutes(i + 1), AppId = i, Category = (AppCategory)(i % 9) })],
                Temps = [.. Enumerable.Range(0, 1440).Select(i => new TempPoint(day.AddMinutes(i), 40 + i % 30, 50 + i % 20))],
            };
            double timelineMs = ControlTests.Median(() => { timeline.InvalidateVisual(); Draw.Render(timeline, 1400, 220); });
            results.Add($"day timeline 1440: {timelineMs:0.0} ms");

            // Budgets: about three times what they took on the development PC (Ryzen 7 5700X3D; in brackets), at least 25 ms.
            void Budget(string what, double ms, double budget) { if (ms > budget) over.Add($"{what} {ms:0.0} ms > {budget} ms"); }
            Budget("line chart", line, 115);            // (37)
            Budget("line chart drawing", lineDraw, 25); // (4)
            Budget("line chart hover", hover, 25);      // (5)
            Budget("sparkline", sparkMs, 35);           // (11)
            Budget("geometry", geometry, 25);           // (6)
            Budget("daily bars", barsMs, 450);          // (145)
            Budget("crash strip", stripMs, 35);         // (11)
            Budget("treemap", mapMs, 400);              // (135)
            Budget("day timeline", timelineMs, 135);    // (45)
            File.WriteAllLines(Path.Combine(TestEnvironment.DataDir, "perf-controls.txt"), results);
        });
        return over.Count == 0 ? null : string.Join("; ", over) + " | measured: " + string.Join("; ", results);
    }
}
