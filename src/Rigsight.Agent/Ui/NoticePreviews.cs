using Rigsight.Core;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;

namespace Rigsight.Agent.Ui;

/// <summary>
/// Settings › Notifications › Preview for the notifications round's kinds: each put through its real words, with made-up
/// figures, so the preview is always what that notification would say.
/// </summary>
internal static class NoticePreviews
{
    public static (NoticeKind Kind, NoticeText Text, string Page)? Of(string? kind)
    {
        var now = DateTime.Now;
        string check = Lang.T("If you changed the fan curve in MSI Afterburner, that's why; if not, check they're clean and turning freely.");
        return kind switch
        {
            "fan-stopped" => (NoticeKind.FanStopped, NoticeWords.FanStopped("GPU Fan 1", true, 4, now.AddMinutes(-6), 74), "fans"),
            "drive-bad" => (NoticeKind.DriveFailing, NoticeWords.DriveFailing("Samsung SSD 980 1TB"), "storage"),
            "hotter" => (NoticeKind.Usual, NoticeWords.Hotter(new UsualHeat("Crimson Desert", "GPU", 78.2, 70.6, 69.1, 72.3, 95, 410, 6, 41.2, 40.8, 300, 302, EffectTone.Worse)), "games"),
            "slowed" => (NoticeKind.Slowed, NoticeWords.Slowed(new Throttling(12, 9, 83)), "temperatures"),
            "fan-slower" => (NoticeKind.Fans, NoticeWords.FanSlower("GPU Fan 1", true, 18, 1650, 2010, "GPU", 72, Lang.T("in the 7 days before"), check), "fans"),
            "drive-sectors" => (NoticeKind.Drive, NoticeWords.DriveSectors("WDC WD20EZRZ", Core.Notices.NoticeChecks.SectorWords(new DriveHealthInfo { ReallocatedSectors = 3 }), 0), "storage"),
            "problems" => (NoticeKind.Usual, NoticeWords.MoreProblems(Wording.Count(Core.Stability.CrashKind.GpuDriverReset, 3), Lang.T("in the last 7 days"), 0, 4, 1), "crashes"),
            "drive-full" => (NoticeKind.Space, NoticeWords.DriveFull('C', 8L << 30, 476L << 30, true), "storage"),
            "weekly" => NoticeWords.Weekly(31 * 3600 + 20 * 60, "Crimson Desert", 9 * 3600 + 5 * 60, 35 * 3600 + 20 * 60) is var (t, b)
                ? (NoticeKind.Weekly, new NoticeText(t, b), "reports") : null,
            "memory" => (NoticeKind.Memory, NoticeWords.MemoryGrowing("Google Chrome", (long)(2.1 * (1L << 30)), (long)(9.2 * (1L << 30)), 3 * 3600), "processes"),
            "startup" => (NoticeKind.Startup, NoticeWords.StartsWithWindows(SavedText.Show("FACEIT now starts with Windows"), now.AddMinutes(-20), now), "timeline"),
            _ => null,
        };
    }
}
