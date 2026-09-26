using System.Diagnostics;
using System.Drawing;
using Rigsight.Agent.Native;
using Rigsight.Agent.Ui;
using Rigsight.Agent.Widgets;
using Rigsight.Core.Settings;
using static Rigsight.Tests.Agent.WidgetSamples;

namespace Rigsight.Tests.Agent;

/// <summary>
/// Widgets, the overlay and cards are redrawn every second on the user's PC by an agent that never exits: each draw must
/// stay cheap, and nothing may leak. Budgets are about 3× what was measured on the development PC (Ryzen 7 5700X3D).
/// </summary>
[Collection("Drawing")]
[Trait("Category", "Perf")]
public class DrawingCostTests(ITestOutputHelper output)
{
    private static void Warm()
    {
        foreach (var style in Enum.GetValues<WidgetStyle>())
            foreach (var theme in new[] { WidgetTheme.Dark, WidgetTheme.Light })
                WidgetRenderer.Render(new WidgetConfig { Style = style, Theme = theme, BackgroundOpacity = 0.2 }, Full(), 1.5f, true, out _).Dispose();
        WidgetRenderer.RenderOverlay(AllMetrics(), Full(), 1.5f).Dispose();
        WidgetRenderer.RenderOverlay(AllMetrics(OverlayLayout.Line), Full(), 1.5f).Dispose();
        ToastRenderer.Render(new Notice(NoticeKind.Alert, "Running hot", "GPU 90°"), 1.5f, true, out _).Dispose();
    }

    // Measured (150%, every reading): Compact 2.1 ms / 32 KB, Pill 0.7 ms / 2.7 KB, Gauges 2.0 ms / 2.7 KB, Now playing
    // 1.4 ms / 2.7 KB, Today 1.9 ms / 3.3 KB, Graph 3.8 ms / 5.1 KB, overlay 4.5 ms / 21 KB, RivaTuner text 0.03 ms / 27 KB,
    // card 1.3 ms / 1 KB, putting a bitmap on screen 0.6 ms.

    /// <summary>Average time and managed allocation of one call, after warming up.</summary>
    private (double Ms, long Bytes) Measure(string what, Action draw, int n = 200)
    {
        for (int i = 0; i < 10; i++) draw();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) draw();
        double ms = sw.Elapsed.TotalMilliseconds / n;
        long perCall = (GC.GetAllocatedBytesForCurrentThread() - bytes) / n;
        output.WriteLine($"{what}: {ms:0.000} ms, {perCall:N0} bytes");
        return (ms, perCall);
    }

    public static TheoryData<WidgetStyle, double, long> WidgetBudgets() => new()
    {
        // style, milliseconds, bytes (managed) per draw at 150% with every reading
        { WidgetStyle.Compact, 6.5, 96_000 },
        { WidgetStyle.Pill, 2, 8_000 },
        { WidgetStyle.Gauges, 6, 8_000 },
        { WidgetStyle.NowPlaying, 4.5, 8_000 },
        { WidgetStyle.Today, 6, 10_000 },
        { WidgetStyle.Graph, 12, 16_000 },
    };

    [Theory]
    [MemberData(nameof(WidgetBudgets))]
    public void A_widget_draws_within_budget(WidgetStyle style, double msBudget, long bytesBudget)
    {
        Warm();
        var data = Full();
        var cfg = new WidgetConfig { Style = style, BackgroundOpacity = 0.3 }; // with the text shadow: the costlier look
        var (ms, bytes) = Measure(style.ToString(), () => WidgetRenderer.Render(cfg, data, 1.5f, false, out _).Dispose());
        Assert.True(ms < msBudget, $"{style}: {ms:0.00} ms per draw (budget {msBudget})");
        Assert.True(bytes < bytesBudget, $"{style}: {bytes:N0} bytes per draw (budget {bytesBudget:N0})");
    }

    [Theory]
    [InlineData(OverlayLayout.Rows, 14, 64_000)]
    [InlineData(OverlayLayout.Line, 14, 64_000)]
    public void The_overlay_draws_within_budget(OverlayLayout layout, double msBudget, long bytesBudget)
    {
        Warm();
        var data = Full();
        var o = AllMetrics(layout);
        o.BackgroundOpacity = 0.3;
        var (ms, bytes) = Measure($"overlay {layout}", () => WidgetRenderer.RenderOverlay(o, data, 1.5f).Dispose());
        Assert.True(ms < msBudget, $"{ms:0.00} ms per draw (budget {msBudget})");
        Assert.True(bytes < bytesBudget, $"{bytes:N0} bytes per draw (budget {bytesBudget:N0})");
    }

    [Fact]
    public void RivaTuner_text_is_cheap()
    {
        var data = Full();
        var o = AllMetrics();
        var (ms, bytes) = Measure("rtss text", () => WidgetRenderer.RtssText(o, data), 2000);
        Assert.True(ms < 0.1, $"{ms:0.000} ms");
        Assert.True(bytes < 80_000, $"{bytes:N0} bytes");
    }

    [Fact]
    public void A_card_draws_within_budget()
    {
        Warm();
        var n = new Notice(NoticeKind.Crash, "Dota 2 closed unexpectedly", "Likely cause: NVIDIA driver. You played for 48m. Details are on the Crashes page.");
        // A card redraws every 16 ms while it fades in and out.
        var (ms, bytes) = Measure("card", () => ToastRenderer.Render(n, 1.5f, false, out _).Dispose());
        Assert.True(ms < 4, $"{ms:0.00} ms");
        Assert.True(bytes < 3_000, $"{bytes:N0} bytes");
    }

    [Fact]
    public void Putting_a_bitmap_on_screen_is_cheap()
    {
        using var bmp = WidgetRenderer.Render(new WidgetConfig { Style = WidgetStyle.Compact }, Full(), 1.5f, false, out _);
        // No window: the update itself fails harmlessly, but every GDI step around it runs.
        var (ms, _) = Measure("layered bitmap", () => Win32.SetLayeredBitmap(IntPtr.Zero, bmp, Point.Empty, 255));
        Assert.True(ms < 2, $"{ms:0.00} ms");
    }

    /// <summary>
    /// Private memory outside the managed heap (where GDI+ keeps bitmaps, brushes and fonts), after a full collection. The
    /// managed heap's own size follows whatever the whole test run did before, so leaving it out keeps other tests out.
    /// </summary>
    private static long NativeMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return Process.GetCurrentProcess().PrivateMemorySize64 - GC.GetGCMemoryInfo().TotalCommittedBytes;
    }

    [Fact]
    public void Drawing_every_second_for_a_long_time_leaks_no_handles()
    {
        Warm();
        var data = Full();
        var extreme = Extreme();
        var overlay = AllMetrics();
        var notice = new Notice(NoticeKind.Session, "Elden Ring session", "Played for 2h 14m.", Path.Combine(Environment.SystemDirectory, "notepad.exe"));
        void Round(int i)
        {
            foreach (var style in Enum.GetValues<WidgetStyle>())
            {
                using var bmp = WidgetRenderer.Render(new WidgetConfig { Style = style, Theme = (WidgetTheme)(i % 3) }, i % 2 == 0 ? data : extreme, 1.5f, i % 5 == 0, out _);
                Win32.SetLayeredBitmap(IntPtr.Zero, bmp, Point.Empty, 255);
            }
            using (var o = WidgetRenderer.RenderOverlay(overlay, data, 1.5f)) Win32.SetLayeredBitmap(IntPtr.Zero, o, Point.Empty, 255);
            using (var t = ToastRenderer.Render(notice, 1.5f, i % 2 == 0, out _)) Win32.SetLayeredBitmap(IntPtr.Zero, t, Point.Empty, 255);
            WidgetRenderer.RtssText(overlay, data);
        }
        // GDI+ settles at its working size in the first few hundred rounds (measured: +35 MB, then flat): start after that.
        for (int i = 0; i < 400; i++) Round(i);
        uint gdi = GuiResources.Gdi, user = GuiResources.User;
        long privateBytes = NativeMemory();

        for (int i = 0; i < 600; i++) Round(i);
        uint gdiAfter = GuiResources.Gdi, userAfter = GuiResources.User;
        long privateAfter = NativeMemory();
        output.WriteLine($"GDI {gdi} → {gdiAfter}, USER {user} → {userAfter}, native private {privateBytes / 1048576} → {privateAfter / 1048576} MB");

        // 600 rounds is ten minutes of every widget, the overlay and a card: nothing may pile up.
        Assert.True(gdiAfter <= gdi + 2, $"GDI objects {gdi} → {gdiAfter} after 600 rounds");
        Assert.True(userAfter <= user + 2, $"USER objects {user} → {userAfter} after 600 rounds");
        Assert.True(privateAfter - privateBytes < 16L << 20, $"private memory grew {(privateAfter - privateBytes) / 1048576} MB");
    }
}
