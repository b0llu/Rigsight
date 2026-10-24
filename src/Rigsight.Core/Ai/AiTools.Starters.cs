using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Ai;

/// <summary>A question to start a new chat with, and the page whose records answer it (for its coloured dot).</summary>
public sealed record AiStarter(string Text, string Page);

public sealed partial class AiTools
{
    /// <summary>
    /// Questions to start a new chat with, from this PC's own records (approved mock-up 2026-10-24): the latest sudden
    /// shut-off or blue screen this week, graphics driver resets, the game played most this week, a drive over 70% full.
    /// Only pages switched on for answers and open on the plan (<paramref name="open"/>); the general ones fill the rest, four in all.
    /// </summary>
    public IReadOnlyList<AiStarter> Starters(DateTime now, Func<string, bool>? open = null)
    {
        var list = new List<AiStarter>();
        bool On(string page) => settings.Ai.Sends(page) && (open?.Invoke(page) ?? true);
        try
        {
            using var db = (dbPath is null ? RigsightDb.OpenReader() : RigsightDb.OpenReader(dbPath));
            if (db is not null)
            {
                var week = db.GetCrashes(TimeUtil.ToUnix(now.Date.AddDays(-6)), TimeUtil.ToUnix(now) + 1);
                if (On(AiPages.Crashes) && week.Where(c => c.Kind is CrashKind.UnexpectedShutdown or CrashKind.SystemCrash).MaxBy(c => c.Ts) is { } down)
                {
                    var at = TimeUtil.FromUnix(down.Ts);
                    string when = at.Date == now.Date ? Lang.T($"at {TimeText.Time(at)} today")
                        : at.Date == now.Date.AddDays(-1) ? Lang.T($"at {TimeText.Time(at)} yesterday")
                        : Lang.T($"on {TimeText.At(at, "dddd")}");
                    list.Add(new(down.Kind == CrashKind.SystemCrash ? Lang.T($"Why did my PC blue-screen {when}?") : Lang.T($"Why did my PC shut off {when}?"), AiPages.Crashes));
                }
                int resets = week.Count(c => c.Kind == CrashKind.GpuDriverReset);
                if (On(AiPages.Temperatures) && On(AiPages.Crashes) && resets > 0)
                    list.Add(new(resets == 1 ? Lang.T("Did this week's graphics driver reset come from heat?") : Lang.T($"Did this week's {resets} graphics driver resets come from heat?"), AiPages.Temperatures));
                if (On(AiPages.Games) && PlayedIn(db, now.Date.AddDays(-6), now.Date).FirstOrDefault() is { Game: { } game })
                    list.Add(new(Lang.T($"How hard does {game.Name} push my graphics card?"), AiPages.Games));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write("ask", $"Starters from the records couldn't be read ({ex.Message}); the general ones show"); }
        if (On(AiPages.Storage) && Fullest() is { } drive)
            list.Add(new(Lang.T($"What's filling my {drive.Letter} drive ({drive.Percent}% full)?"), AiPages.Storage));
        // The general ones, for what the records had nothing new on.
        foreach (var (text, page) in new[]
        {
            (Lang.T("Why did my PC turn off by itself?"), AiPages.Crashes),
            (Lang.T("Are my temperatures normal for my parts?"), AiPages.Temperatures),
            (Lang.T("Which game is hardest on my PC?"), AiPages.Games),
            (Lang.T("Did anything change before my crashes started?"), AiPages.Timeline),
        })
        {
            if (list.Count >= 4) break;
            if (list.Any(s => s.Page == page) || !On(page)) continue;
            list.Add(new(text, page));
        }
        return [.. list.Take(4)];
    }

    /// <summary>
    /// The PC in a line, for the new chat's heading: "Ryzen 7 5700X3D · RTX 3080 Ti · 16 GB", as people say the parts
    /// (the maker's words and "8-Core Processor" dropped). Empty before the parts were first read.
    /// </summary>
    public string PcLine()
    {
        try
        {
            using var db = dbPath is null ? RigsightDb.OpenReader() : RigsightDb.OpenReader(dbPath);
            if (db is null) return "";
            var items = db.GetInventory();
            var bits = new List<string>();
            if (items.FirstOrDefault(i => i.Kind == Inventory.Cpu)?.Value is { Length: > 0 } cpu) bits.Add(ShortCpu(cpu));
            if (OllamaPicks.MainCard(items.Where(i => i.Kind == Inventory.Gpu).Select(i => i.Name)) is { } gpu) bits.Add(OllamaPicks.ShortCard(gpu));
            if (items.FirstOrDefault(i => i.Kind == Inventory.Ram)?.Value is { Length: > 0 } ram) bits.Add(ram);
            return string.Join(" · ", bits);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write("ask", $"The parts line couldn't be read: {ex.Message}"); return ""; }
    }

    /// <summary>"Ryzen 7 5700X3D" from "AMD Ryzen 7 5700X3D 8-Core Processor", "Core i7-12700K" from "12th Gen Intel(R) Core(TM) i7-12700K".</summary>
    internal static string ShortCpu(string name)
    {
        var n = System.Text.RegularExpressions.Regex.Replace(name, @"\((R|TM)\)|\b\d+(th|st|nd|rd) Gen\b|\bAMD\b|\bIntel\b|\b\d+-Core\b|\bProcessor\b|\bCPU\b|@.*$", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        n = System.Text.RegularExpressions.Regex.Replace(n, @"\s+", " ").Trim();
        return n.Length > 0 ? n : name.Trim();
    }

    /// <summary>The fixed drive most full, when it's over 70%: its letter and how full.</summary>
    private static (string Letter, int Percent)? Fullest()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.TotalSize > 0)
                .Select(d => (Letter: d.Name.TrimEnd('\\'), Percent: (int)Math.Round(100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize)))
                .Where(d => d.Percent >= 70).OrderByDescending(d => d.Percent).Cast<(string, int)?>().FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
