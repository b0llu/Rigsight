using System.Globalization;
using Rigsight.Core.Data;
using Rigsight.Core.Games;
using Rigsight.Core.Settings;

namespace Rigsight.Core.Export;

/// <summary>
/// A file made from what a page shows rather than from one table of history: the games library, the plays, this PC
/// now, memory now, space that could be freed, the fans' summary. Written by <see cref="DataExport.WriteSheet"/> as CSV
/// or laid-out text, like the tables.
/// </summary>
/// <param name="File">Its file name without the extension, in the language in use.</param>
/// <param name="When">The period it covers, or when it was made, for the line under its title in a text file.</param>
public sealed record ExportSheet(string File, string Title, string About, string When, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>A line of the Memory page's "Using memory right now" list.</summary>
public sealed record MemoryNowRow(string App, string Program, int Processes, double NowMb, double? UsualMb);

/// <summary>A line of the Storage page's "Space you could free".</summary>
public sealed record SpaceRow(string What, string About, long Bytes, string? Folder);

/// <summary>A fact on a page as it reads there: what, its value, and the line under it.</summary>
public sealed record FactRow(string What, string Value, string? Detail);

/// <summary>The files only a page's own Export box makes (see <see cref="ExportSheet"/>).</summary>
public static class ExportSheets
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string Time(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", Inv);
    private static double? R(double? v, int digits = 1) => v is double d ? Math.Round(d, digits) : null;

    /// <summary>When a file of what's on the page now was made: "Made 2026-10-23 11:20".</summary>
    public static string MadeAt(DateTime now)
    {
        string at = Time(now);
        return Lang.T($"Made {at}");
    }

    /// <summary>The period a file covers, as its text heading says it.</summary>
    public static string Period(DateTime from, DateTime to)
    {
        bool days = from.TimeOfDay == TimeSpan.Zero && to.TimeOfDay == TimeSpan.Zero;
        string first = days ? from.ToString("yyyy-MM-dd", Inv) : Time(from), last = days ? to.AddDays(-1).ToString("yyyy-MM-dd", Inv) : Time(to);
        // All time: from whenever the history starts.
        return from.Year <= 2000 ? Lang.T($"Everything up to {last}") : Lang.T($"{first} to {last}");
    }

    /// <summary>A launcher's name as people know it; none for a game no launcher lists.</summary>
    public static string? LauncherName(Launcher? launcher) => launcher switch
    {
        Launcher.Steam => "Steam",
        Launcher.Epic => "Epic Games",
        Launcher.Riot => "Riot Client",
        Launcher.Gog => "GOG Galaxy",
        Launcher.Ubisoft => "Ubisoft Connect",
        Launcher.Ea => "EA app",
        Launcher.BattleNet => "Battle.net",
        _ => null,
    };

    private static string YesNo(bool yes) => yes ? Lang.T("Yes") : Lang.T("No");

    /// <summary>The Games library: each game, where it's from, how long it was played and how big it is.</summary>
    public static ExportSheet Library(IEnumerable<GameEntry> games, DateTime now) => new(
        Lang.T("Games library"), Lang.T("Games library"), Lang.T("Each game, its launcher, play time, last played and size on disk."), MadeAt(now),
        [Lang.T("Game"), Lang.T("Launcher"), Lang.T("Installed"), Lang.T("Played (h)"), Lang.T("Sessions"), Lang.T("First played"), Lang.T("Last played"), Lang.T("Size on disk (GB)"), Lang.T("Folder")],
        [.. games.OrderByDescending(g => g.LastPlayed ?? g.Installed?.LastPlayed ?? DateTime.MinValue).Select(g => new object?[]
        {
            g.Name, LauncherName(g.Launcher), YesNo(g.IsInstalled), g.HasHistory ? Math.Round(g.ActiveSec / 3600, 1) : null, g.HasHistory ? g.Sessions : null,
            g.FirstPlayed is { } first ? Time(first) : null, (g.LastPlayed ?? g.Installed?.LastPlayed) is { } last ? Time(last) : null,
            g.Installed?.SizeBytes is long size ? Math.Round(size / 1073741824.0, 1) : null, g.Installed?.InstallDir,
        })]);

    /// <summary>How a play ended, as a cell.</summary>
    public static string Ending(PlayEnd end) => Lang.T(end switch
    {
        PlayEnd.PowerCut => Lang.Key("Power cut"),
        PlayEnd.ShutOff => Lang.Key("PC shut off"),
        PlayEnd.BlueScreen => Lang.Key("Blue screen"),
        PlayEnd.DriverReset => Lang.Key("Graphics driver reset"),
        PlayEnd.Crashed => Lang.Key("Game crashed"),
        PlayEnd.Hung => Lang.Key("Game froze"),
        _ => Lang.Key("Closed"),
    });

    /// <summary>
    /// Each time a game was played that started in [from, to): how long, how it ended, the frame rate and what the
    /// graphics card and CPU did. Read the way the Games page reads a game (its plays, joined across short breaks).
    /// </summary>
    public static ExportSheet Plays(RigsightDb db, IEnumerable<GameEntry> games, RigsightSettings s, DateTime from, DateTime to, DateTime now)
    {
        var rows = new List<(GamePlay Play, string Game)>();
        foreach (var game in games.Where(g => g.HasHistory))
            rows.AddRange(GameHistory.Story(db, game, s, now).Plays.Where(p => p.StartTime >= from && p.StartTime < to).Select(p => (p, game.Name)));
        return new(Lang.T("Play sessions"), Lang.T("Play sessions"), Lang.T("Each time a game was played: how long, how it ended, frame rate, graphics card heat and power."), Period(from, to),
            [Lang.T("Start"), Lang.T("End"), Lang.T("Game"), Lang.T("Played (min)"), Lang.T("Ended"), Lang.T("FPS"), Lang.T("1% low FPS"), Lang.T("GPU (°C)"), Lang.T("GPU highest (°C)"),
             Lang.T("GPU hot spot highest (°C)"), Lang.T("CPU highest (°C)"), Lang.T("GPU load (%)"), Lang.T("GPU power (W)"), Lang.T("CPU and GPU power (W)"), Lang.T("Video memory highest (GB)")],
            [.. rows.OrderBy(r => r.Play.Start).Select(r => new object?[]
            {
                Time(r.Play.StartTime), Time(r.Play.EndTime), r.Game, Math.Round(r.Play.ActiveSec / 60, 1), Ending(r.Play.Ending),
                R(r.Play.Stats.Fps, 0), R(r.Play.Stats.FpsLow, 0), R(r.Play.Stats.Gpu), R(r.Play.Stats.GpuMax), R(r.Play.Stats.HotMax), R(r.Play.Stats.CpuMax),
                R(r.Play.Stats.GpuLoad, 0), R(r.Play.Stats.GpuPower, 0), R(r.Play.Stats.Power, 0), (r.Play.Stats.VramOwnMax ?? r.Play.Stats.VramMax) is double v ? Math.Round(v / 1024, 1) : null,
            })]);
    }

    /// <summary>This PC as the Timeline's "This PC now" shows it.</summary>
    public static ExportSheet PcNow(IEnumerable<FactRow> facts, DateTime now) => new(
        Lang.T("This PC now"), Lang.T("This PC now"), Lang.T("Windows, graphics driver, BIOS, memory, power plan and apps, as they are now."), MadeAt(now),
        [Lang.T("What"), Lang.T("Now"), Lang.T("Detail")],
        [.. facts.Select(f => new object?[] { f.What, f.Value, f.Detail })]);

    /// <summary>The Memory page's list of apps using memory now, with each one's usual.</summary>
    public static ExportSheet MemoryNow(IEnumerable<MemoryNowRow> rows, DateTime now) => new(
        Lang.T("Memory right now"), Lang.T("Memory right now"), Lang.T("Each app using memory now, its processes, and what it usually holds."), MadeAt(now),
        [Lang.T("App"), Lang.T("Program"), Lang.T("Processes"), Lang.T("Now (MB)"), Lang.T("Usual (MB)")],
        [.. rows.Select(r => new object?[] { r.App, r.Program, r.Processes, Math.Round(r.NowMb), R(r.UsualMb, 0) })]);

    /// <summary>The Storage page's "Space you could free".</summary>
    public static ExportSheet SpaceToFree(IEnumerable<SpaceRow> rows, DateTime now) => new(
        Lang.T("Space you could free"), Lang.T("Space you could free"), Lang.T("Where space could be freed, how much, and where it is. Nothing was deleted."), MadeAt(now),
        [Lang.T("What"), Lang.T("About it"), Lang.T("Size (MB)"), Lang.T("Folder")],
        [.. rows.Select(r => new object?[] { r.What, r.About, Math.Round(r.Bytes / 1048576.0, 1), r.Folder })]);

    /// <summary>A page's summary as it reads there (the Fans page's quiet time, fastest, hardest…).</summary>
    public static ExportSheet Summary(string file, string about, IEnumerable<FactRow> facts, DateTime from, DateTime to) => new(
        file, file, about, Period(from, to),
        [Lang.T("What"), Lang.T("Value"), Lang.T("Detail")],
        [.. facts.Select(f => new object?[] { f.What, f.Value, f.Detail })]);
}
