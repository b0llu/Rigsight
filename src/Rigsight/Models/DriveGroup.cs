using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;
using Rigsight.Core.Data;
using Rigsight.Core.Protocol;
using Rigsight.Core.Reports;
using Rigsight.ViewModels;

namespace Rigsight.Models;

/// <summary>
/// One physical drive on the Storage page (wear and drive details, approved mock-up 2026-11-14): its letters nested under
/// it, its health word, life left and how fast it goes, its temperature and what it wrote this week. A network drive or a
/// letter whose drive gives no reading is a group of its own with only how full it is.
/// </summary>
public sealed partial class DriveGroup : ObservableObject
{
    public DriveGroup(string id, string name, DriveHealthInfo? health, DriveSummary? summary, IReadOnlyList<VolumeRow> letters, string section)
    {
        Id = id;
        Name = name;
        Health = health;
        Summary = summary;
        Letters = letters;
        Section = section;
    }

    /// <summary>The drive's key (or "letter:E" for one with no reading), so a rebuild keeps the drive opened.</summary>
    public string Id { get; }
    public string Name { get; }
    public DriveHealthInfo? Health { get; }
    public DriveSummary? Summary { get; }
    public IReadOnlyList<VolumeRow> Letters { get; }
    /// <summary>"inside" or "other" (USB and network).</summary>
    public string Section { get; }

    public bool IsWindows => Letters.Any(l => string.Equals(l.Root, System.IO.Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase));
    public bool IsNetwork => Letters.Count > 0 && Letters.All(l => l.Kind == "network");
    internal static bool UsesUsb(DriveHealthInfo? drive, IReadOnlyList<VolumeRow> letters) =>
        drive?.Bus is "BusTypeUsb" or "Usb" or "USB" || letters.Any(l => l.Kind == "usb");
    public bool IsUsb => UsesUsb(Health, Letters);
    private bool IsSsd => Health is { IsSsd: true } or { IsNvme: true };
    /// <summary>
    /// A drive the agent found whose SMART read gave nothing back (no attributes, status, wear, hours or temperature):
    /// many USB adapters and some controllers don't pass it on. Only its size and connection are known.
    /// </summary>
    private bool GaveNothing => Health is { Status: "Unknown", Life: null, Temperature: null, PowerOnHours: null, Attributes: null or { Count: 0 } };
    public bool HasReading => Health is not null && !GaveNothing;
    public bool HasLetters => Letters.Count > 0;
    public bool NoLetter => Letters.Count == 0;

    /// <summary>"SATA SSD · 480 GB · Windows", "Hard drive · 2 TB", "USB stick · 64 GB", "Network drive".</summary>
    public string KindText
    {
        get
        {
            if (IsNetwork) return Lang.T("Network drive");
            var bits = new List<string>();
            if (Health is { } h)
            {
                bits.Add(IsUsb ? IsSsd ? Lang.T("USB SSD") : Lang.T("USB drive")
                    : h.IsNvme ? Lang.T("NVMe SSD") : h.IsSsd ? Lang.T("SATA SSD") : Lang.T("Hard drive"));
                if (h.SizeBytes > 0) bits.Add(Units.DiskBytes((long)h.SizeBytes));
            }
            else if (IsUsb) bits.Add(Lang.T("USB drive"));
            if (IsWindows) bits.Add(Lang.T("Windows"));
            return string.Join(" · ", bits);
        }
    }

    // ── Health ──────────────────────────────────────────────────────────

    private double? LifeLeft => Health?.Life ?? Summary?.Life?.Value;
    private bool HasHealth => LifeLeft.HasValue || Health?.Status is "Good" or "Caution" or "Bad";
    /// <summary>0 for no warning, 1 for early warning, 2 for failing.</summary>
    public int Level => DriveSummary.HealthLevel(Health?.Status, LifeLeft);
    public bool NeedsLook => HasReading && Level > 0;
    public bool IsFailing => Level == 2;
    public string HealthWord => Level switch { 2 => Lang.T("Failing"), 1 => Lang.T("Early warning"), _ => HasHealth ? Lang.T("Healthy") : Lang.T("No reading") };
    public Brush HealthBrush => Brush(Level switch { 2 => "HotBrush", 1 => "WarmBrush", _ => HasHealth ? "GoodBrush" : "FaintBrush" });
    /// <summary>The word itself is only coloured when it needs a look (a healthy drive's word is plain, its dot green).</summary>
    public Brush HealthTextBrush => NeedsLook ? HealthBrush : !HasHealth ? Brush("MutedBrush") : Brush("TextBrush");
    public FontWeight HealthWeight => NeedsLook ? FontWeights.SemiBold : FontWeights.Normal;

    private long? Sectors => Health is { } h && (h.ReallocatedSectors, h.PendingSectors, h.UncorrectableSectors) is not (null, null, null)
        ? (h.ReallocatedSectors ?? 0) + (h.PendingSectors ?? 0) + (h.UncorrectableSectors ?? 0) : null;

    /// <summary>The "Life left" column: an SSD's life, a hard drive's bad sectors.</summary>
    public string LifeText
    {
        get
        {
            if (LifeLeft is double life) return $"{life:0}%";
            return Sectors is long s ? s == 0 ? Lang.T("No bad sectors") : s == 1 ? Lang.T("1 bad sector") : Lang.T($"{s:N0} bad sectors") : "";
        }
    }

    public Brush LifeBrush => Health?.Life is double life && life <= DriveSummary.WornOut || Sectors > 0 ? HealthBrush : Brush("TextBrush");

    /// <summary>Under it: the pace of wear ("8% a year"), a hard drive's hours, or the bad sectors' rise.</summary>
    public string LifeSub => _wear is { } w && Health?.Life is not null
        ? w.LifePerYear is double perYear ? perYear < 0.5 ? Lang.T("Under 1% a year") : Lang.T($"{perYear:0}% a year") : Lang.T("Too soon to say")
        : _wear is { SectorsThen: long then, SectorsNow: long now } && now > then && w2().ThenDate is DateTime t ? Lang.T($"Up from {then:N0} on {t:d MMM}")
        : Health?.PowerOnHours is long hours && Health.Life is null ? Lang.T($"{hours:N0} hours on") : "";

    private WearRow w2() => _wear!;

    private WearRow? _wear;
    /// <summary>The drive's wear against its earlier weeks (read from its daily rows): its pace and its sectors' rise.</summary>
    public WearRow? Wear
    {
        get => _wear;
        set { _wear = value; OnPropertyChanged(); OnPropertyChanged(nameof(LifeSub)); OnPropertyChanged(nameof(WhatLine)); }
    }

    public string TempText => Health?.Temperature is double t ? $"{t:0}°" : "";
    public bool IsHot => Health is { Temperature: double t, TemperatureWarning: double warn } && warn > 0 && t >= warn;
    public Brush TempBrush => IsHot ? Brush("WarmBrush") : Brush("TextBrush");
    public string TempSub => IsHot && Health?.TemperatureWarning is double warn ? Lang.T($"Warns at {warn:0}°") : "";

    private double? _writtenWeek;
    /// <summary>What it wrote in the last 7 days (GB), from its lifetime count then and now; null until it has a week's rows.</summary>
    public double? WrittenWeek
    {
        get => _writtenWeek;
        set { _writtenWeek = value; OnPropertyChanged(); OnPropertyChanged(nameof(WrittenText)); }
    }
    /// <summary>What it wrote in the last 7 days; under a week of its daily record, too soon; a drive that doesn't count its writes, no reading.</summary>
    public string WrittenText => WrittenWeek is double gb ? Units.DiskBytes((long)(gb * 1_000_000_000)) : !HasReading ? ""
        : Health?.WrittenGb is null ? Wording.NoReading : Lang.T("Too soon to say");

    /// <summary>A row with no reading says why, across the health columns.</summary>
    public string Note => IsNetwork ? Lang.T("Only how full it is, since it's on another computer")
        : IsUsb ? Lang.T("Only how full it is, through this USB adapter")
        : GaveNothing ? Lang.T("Only how full it is, since this drive gives no health readings")
        : Lang.T("Drive health needs admin rights on this PC.");

    // ── Needs a look card ───────────────────────────────────────────────

    /// <summary>What happened, in the card's colour: "12 bad sectors, up from 4 on 2 Nov", "8% life left, about 9% used a year".</summary>
    public string WhatLine
    {
        get
        {
            if (Sectors is long s && s > 0)
            {
                string count = s == 1 ? Lang.T("1 bad sector") : Lang.T($"{s:N0} bad sectors");
                return _wear is { SectorsThen: long then, ThenDate: DateTime t } && then < s ? Lang.T($"{count}, up from {then:N0} on {t:d MMM}") : count;
            }
            if (Health?.Life is double life)
                return _wear?.LifePerYear is double perYear && perYear >= 0.5 ? Lang.T($"{life:0}% life left, about {perYear:0}% used a year") : Lang.T($"{life:0}% life left");
            return Summary?.StateWord ?? "";
        }
    }

    /// <summary>Why, in grey under it: the drive health card's own sentence.</summary>
    public string Reason => Summary?.HealthReason ?? "";

    // ── The opened drive's tiles ────────────────────────────────────────

    /// <summary>"the C and D drives · Windows" style line under an opened drive's name.</summary>
    public string OpenedSub
    {
        get
        {
            var letters = Letters.OrderBy(l => l.Root, StringComparer.OrdinalIgnoreCase).Select(l => l.Letter.TrimEnd(':')).ToList();
            string where = letters.Count switch
            {
                0 => Lang.T("No drive letter"),
                1 => Lang.T($"the {letters[0]} drive"),
                _ => Lang.T($"the {string.Join(", ", letters.Take(letters.Count - 1))} and {letters[^1]} drives"),
            };
            return KindText.Length > 0 ? $"{KindText} · {where}" : where;
        }
    }

    public string TileLifeLabel => LifeLeft.HasValue || (IsSsd && Sectors is null) ? Lang.T("LIFE LEFT") : Lang.T("BAD SECTORS");
    public string TileLifeValue => LifeLeft is double l ? $"{l:0}%" : Sectors is long s ? s == 0 ? Lang.T("None") : $"{s:N0}" : Lang.T("No reading");
    public string TileLifeSub => LifeSub;
    public string TileWritten => Health?.WrittenGb is double w ? Units.DiskBytes(w * 1e9) : Lang.T("No reading");
    public string TileWrittenSub => WrittenWeek is double gb ? Lang.T($"{Units.DiskBytes(gb * 1e9)} in the last 7 days") : "";
    public string TileHours => Health?.PowerOnHours is long h ? $"{h:N0}" : Lang.T("No reading");
    public string TileHoursSub => Health?.PowerOnCount is long n ? n == 1 ? Lang.T("Switched on once") : Lang.T($"Switched on {n:N0} times") : "";
    public string TileTemp => TempText.Length > 0 ? TempText : Lang.T("No reading");
    public string TileTempSub => Health?.TemperatureWarning is double warn && warn > 0 ? Lang.T($"Warns at {warn:0}°") : "";

    /// <summary>The page's own question for the AI about this drive.</summary>
    public Services.AiQuestion Ask => new(Lang.T($"Is my drive {Name} okay?"), Core.Ai.AiPages.Storage);

    /// <summary>The letter the card's bar shows: the drive's fullest.</summary>
    public VolumeRow? MainLetter => Letters.OrderByDescending(l => l.UsedPercent).FirstOrDefault();

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}

/// <summary>A letter's space to free on the Storage overview: "Temporary files and the Recycle Bin", or not scanned yet.</summary>
public sealed record CleanupLine(string Root, string Letter, string What, string Size, bool NeedsScan);

/// <summary>One app's reads and writes on the drives over a period (see ReportService.DiskUseAsync); <see cref="MainDisk"/> is the drive it used most.</summary>
public sealed record DiskUseLine(string Name, bool IsWindows, string? MainDisk, long Read, long Write)
{
    public long Total => Read + Write;
}

/// <summary>A line of "What reads and writes": its name, its bars' widths against the top line, and the figure beside it ("190 GB · E").</summary>
public sealed record DiskUseRow(string Name, bool IsWindows, double ReadShare, double WriteShare, string Figure, string ToolTip)
{
}

/// <summary>
/// One line of Every reading in plain words (approved mock-up 2026-11-14): the reading worked out in Core
/// (<see cref="Core.Storage.DriveReading"/>) with its brushes, and the change column's width (none on a drive's first day).
/// </summary>
public sealed class ReadingLine(Core.Storage.DriveReading r, GridLength sinceWidth, bool isFirst)
{
    /// <summary>The group's first line, which has no line above it (its heading has one under it).</summary>
    public bool IsFirst { get; } = isFirst;
    public string Name => r.Name;
    public string What => r.What;
    public string Now => r.Now;
    public string Usual => r.Usual;
    public bool HasUsual => r.Usual.Length > 0;
    public string Change => r.Change;
    public string Glyph => r.Glyph;
    public string Tip => r.Tip;
    public string MakerLine => r.MakerLine;
    public bool HasMakerLine => r.MakerLine.Length > 0;
    public bool HasDot => r.Dot != Core.Storage.ReadingDot.None;
    public Brush? Dot => DriveReadingBrushes.Dot(r.Dot);
    public Brush IconBrush => DriveReadingBrushes.Get(r.Colour);
    public Brush ValueBrush => DriveReadingBrushes.Get(r.ValueWarm ? "WarmBrush" : "TextBrush");
    /// <summary>Amber only when a watched reading went the wrong way.</summary>
    public Brush ChangeBrush => DriveReadingBrushes.Get(r.ChangeWarm ? "WarmBrush" : "MutedBrush");
    public GridLength SinceWidth { get; } = sinceWidth;
    /// <summary>
    /// The change shows only while its column does: with no earlier day the column is 0 wide, and its text wrapped a letter
    /// a line there, making every line some 300 px tall (the user's test copy, 2026-11-14).
    /// </summary>
    public bool HasChange => SinceWidth.Value > 0 && r.Change.Length > 0;
}

/// <summary>One line of Every reading as the drive reports it (see <see cref="Core.Storage.DriveAttributeLine"/>), its columns' widths set by the drive's kind.</summary>
public sealed class OwnReadingLine(Core.Storage.DriveAttributeLine a, bool nvme)
{
    public string IdText => a.IdText;
    public string Name => a.Name;
    public string Plain => a.Plain;
    public bool HasPlain => a.Plain.Length > 0;
    public string Score => a.Score;
    public string Lowest => a.Lowest;
    public string Limit => a.Limit;
    public string Count => a.Count;
    public string Tip => a.Tip;
    public bool Watched => a.Watched;
    public Brush? Dot => DriveReadingBrushes.Dot(a.Dot);
    /// <summary>Score, Lowest and Limit: none on an NVMe drive, which gives counts only.</summary>
    public GridLength ScoreWidth => DriveReadingBrushes.ScoreWidth(nvme);
    public GridLength CountWidth => DriveReadingBrushes.CountWidth(nvme);
}

/// <summary>The palette brushes and column widths Every reading's lines use.</summary>
public static class DriveReadingBrushes
{
    public static Brush Get(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static Brush? Dot(Core.Storage.ReadingDot dot) => dot switch
    {
        Core.Storage.ReadingDot.Good => Get("GoodBrush"),
        Core.Storage.ReadingDot.Warm => Get("WarmBrush"),
        _ => null,
    };

    public static GridLength ScoreWidth(bool nvme) => new(nvme ? 0 : 72);
    public static GridLength CountWidth(bool nvme) => new(nvme ? 170 : 130);
}
