using System.Diagnostics;
using Rigsight.Agent.Widgets;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Reports;
using Rigsight.Core.Settings;
using static Rigsight.Tests.Agent.WidgetSamples;

namespace Rigsight.Tests.Agent;

/// <summary>
/// What the PC tiles cost the agent, measured on a copy of this PC's own history (never the history itself): reading
/// the records once a minute, drawing a widget with every PC tile, and asking Windows how full the drives are. Runs only
/// when asked (RIGSIGHT_MEASURE_PC=1), as it reads this PC's data; it prints the figures.
/// </summary>
[Collection("Drawing")]
public class WidgetPcCostTests(ITestOutputHelper output)
{
    private static bool Asked => Environment.GetEnvironmentVariable("RIGSIGHT_MEASURE_PC") == "1";

    private static double CpuMs(Action work)
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        var before = p.TotalProcessorTime;
        work();
        p.Refresh();
        return (p.TotalProcessorTime - before).TotalMilliseconds;
    }

    [Fact]
    public void What_the_pc_tiles_cost()
    {
        if (!Asked) return;
        string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rigsight", "rigsight.db");
        if (!File.Exists(real)) return;
        string dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "pc-cost-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, "rigsight.db");
        // A copy of the history as it is now (its write-ahead part too), read only from the copy.
        using (var src = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var dst = File.Create(copy)) src.CopyTo(dst);
        if (File.Exists(real + "-wal"))
            using (var src = new FileStream(real + "-wal", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var dst = File.Create(copy + "-wal")) src.CopyTo(dst);
        var s = SettingsStore.Load();
        var now = DateTime.Now;
        var all = (PcNeeds)255;
        PcFacts? first = null;
        using (var db = RigsightDb.OpenReader(copy)!)
        {
            double cold = CpuMs(() => first = PcFacts.Read(db, s, now, all, [], HomeCards.Fullest()));
            var minute = new List<double>();
            var f = first;
            for (int i = 1; i <= 5; i++) minute.Add(CpuMs(() => f = PcFacts.Read(db, s, now.AddMinutes(i), all, [], HomeCards.Fullest(), f)));
            output.WriteLine($"Reading the records, every PC tile: first {cold:0} ms of CPU, then each minute {minute.Average():0} ms (the slow parts kept)");
            foreach (var need in Enum.GetValues<PcNeeds>().Where(n => n != PcNeeds.None))
            {
                var one = PcFacts.Read(db, s, now, need);
                double ms = Enumerable.Range(0, 3).Select(i => CpuMs(() => one = PcFacts.Read(db, s, now.AddMinutes(i + 1), need, before: one))).Average();
                output.WriteLine($"  {need}: {ms:0} ms a minute");
            }
            output.WriteLine($"  {first!.MostUsed.Count} most used, {first.Health.Count} health lines, {first.Changes.Count} changes, {first.Crashes} crashes, last game {first.LastGame?.Name ?? "none"}, internet {first.NetHasData}");
        }
        double drives = Enumerable.Range(0, 5).Select(_ => CpuMs(() => DriveSpace.Fixed())).Average();
        output.WriteLine($"Asking Windows how full the drives are: {drives:0.0} ms (every 30 seconds)");

        // A widget with every PC tile, drawn as the agent draws it (scale 1.5), on the facts just read.
        var tiles = new List<WidgetTile>();
        int y = 0;
        foreach (var k in WidgetTiles.Kinds.Where(k => k.Group != WidgetTiles.GroupEveryday && k.Id is not (WidgetTiles.Reading or WidgetTiles.Line or WidgetTiles.Graph or WidgetTiles.Fps or WidgetTiles.Today or WidgetTiles.NowPlaying)))
        {
            tiles.Add(new WidgetTile { Kind = k.Id, Y = y, W = k.W, H = k.H });
            y += k.H;
        }
        var cfg = new WidgetConfig { Style = WidgetStyle.Custom, Id = "cost", Tiles = tiles, BackgroundOpacity = 0.94 };
        var data = Full();
        data.Facts = first;
        using (WidgetRenderer.Render(cfg, data, 1.5f, false, out _)) { }
        double draw = Enumerable.Range(0, 5).Select(i => CpuMs(() => { using var b = WidgetRenderer.Render(cfg, data, 1.5f, false, out _); })).Average();
        output.WriteLine($"Drawing a widget with all {tiles.Count} PC tiles at 150%: {draw:0} ms");
        foreach (var t in tiles)
        {
            var one = new WidgetConfig { Style = WidgetStyle.Custom, Id = "cost1", Tiles = [new WidgetTile { Kind = t.Kind, W = t.W, H = t.H }], BackgroundOpacity = 0.94 };
            using (WidgetRenderer.Render(one, data, 1.5f, false, out _)) { }
            double ms = Enumerable.Range(0, 5).Select(i => CpuMs(() => { using var b = WidgetRenderer.Render(one, data, 1.5f, false, out _); })).Average();
            output.WriteLine($"  {t.Kind}: {ms:0.0} ms");
        }
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
