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
    public bool IsUsb => Health?.Bus is "Usb" or "USB" || Letters.Any(l => l.Kind == "usb");
    public bool HasReading => Health is not null;
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
                bits.Add(IsUsb ? h.IsSsd ? Lang.T("USB SSD") : Lang.T("USB drive")
                    : h.IsNvme ? Lang.T("NVMe SSD") : h.IsSsd ? Lang.T("SATA SSD") : Lang.T("Hard drive"));
                if (h.SizeBytes > 0) bits.Add(Units.DiskBytes((long)h.SizeBytes));
            }
            else if (IsUsb) bits.Add(Lang.T("USB drive"));
            if (IsWindows) bits.Add(Lang.T("Windows"));
            return string.Join(" · ", bits);
        }
    }

    // ── Health ──────────────────────────────────────────────────────────

    /// <summary>0 healthy, 1 early warning, 2 failing (as the drive health card has always judged it).</summary>
    public int Level => Summary?.StateWord switch { var w when w == Lang.T("Failing") => 2, var w when w == Lang.T("Early warning") => 1, _ => 0 };
    public bool NeedsLook => HasReading && Level > 0;
    public bool IsFailing => Level == 2;
    public string HealthWord => !HasReading ? Lang.T("No reading") : Summary?.StateWord is { Length: > 0 } w ? w : Lang.T("Healthy");
    public Brush HealthBrush => !HasReading ? Brush("FaintBrush") : Summary?.HealthBrush ?? Brush("GoodBrush");
    /// <summary>The word itself is only coloured when it needs a look (a healthy drive's word is plain, its dot green).</summary>
    public Brush HealthTextBrush => NeedsLook ? HealthBrush : !HasReading ? Brush("MutedBrush") : Brush("TextBrush");
    public FontWeight HealthWeight => NeedsLook ? FontWeights.SemiBold : FontWeights.Normal;

    private long? Sectors => Health is { } h && (h.ReallocatedSectors, h.PendingSectors, h.UncorrectableSectors) is not (null, null, null)
        ? (h.ReallocatedSectors ?? 0) + (h.PendingSectors ?? 0) + (h.UncorrectableSectors ?? 0) : null;

    /// <summary>The "Life left" column: an SSD's life, a hard drive's bad sectors.</summary>
    public string LifeText => Health?.Life is double life ? $"{life:0}%"
        : Sectors is long s ? s == 0 ? Lang.T("No bad sectors") : s == 1 ? Lang.T("1 bad sector") : Lang.T($"{s:N0} bad sectors") : "";

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

    public string TileLifeLabel => Health?.Life is not null ? Lang.T("LIFE LEFT") : Lang.T("BAD SECTORS");
    public string TileLifeValue => Health?.Life is double l ? $"{l:0}%" : Sectors is long s ? s == 0 ? Lang.T("None") : $"{s:N0}" : Lang.T("No reading");
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

/// <summary>A group of Every reading in plain words ("WHAT TO WATCH", "WEAR AND USE"): a column of its own.</summary>
public sealed record ReadingGroup(string Title, IReadOnlyList<ReadingRow> Rows);

/// <summary>One line of "Every reading": in plain words with what it was a month ago, or as the drive reports it.</summary>
public sealed record ReadingRow(string Group, string Name, string Now, string Then, Brush? Dot, string Id = "", string Current = "", string Worst = "", string Threshold = "", string Maker = "")
{
    public bool HasDot => Dot is not null;
    /// <summary>A group's heading line ("WHAT TO WATCH"), put in the list where the group starts.</summary>
    public bool IsHeader { get; init; }
    public bool IsRow => !IsHeader;
}

/// <summary>A drive's attributes in plain words (CrystalDiskInfo's numbers; an NVMe drive's health log comes as attributes 1 to 15).</summary>
public static class DriveReadings
{
    /// <summary>
    /// The plain list as its groups, each a column of its own under its name (the user picked this, 2026-11-14: one group
    /// split over two columns read like two drives side by side).
    /// </summary>
    public static List<ReadingGroup> Groups(IEnumerable<ReadingRow> rows) =>
        [.. rows.Where(r => r.IsRow).GroupBy(r => r.Group).Select(g => new ReadingGroup(g.Key, [.. g]))];

    public static List<ReadingRow> Plain(DriveHealthInfo h, DriveHealthDay? monthAgo)
    {
        var rows = new List<ReadingRow>();
        string watch = Lang.T("WHAT TO WATCH"), use = Lang.T("WEAR AND USE");
        var good = (Brush)Application.Current.FindResource("GoodBrush");
        var warm = (Brush)Application.Current.FindResource("WarmBrush");
        var attrs = h.Attributes ?? [];
        ulong? Raw(int id) => attrs.FirstOrDefault(a => a.Id == id) is { } a ? a.Raw : null;
        Brush Dot(ulong v) => v == 0 ? good : warm;
        void Count(string name, ulong? now, long? then)
        {
            if (now is not ulong v) return;
            rows.Add(new ReadingRow(watch, name, $"{v:N0}", then is long t ? $"{t:N0}" : "", Dot(v)));
        }
        if (h.IsNvme)
        {
            if (Raw(1) is ulong warn) rows.Add(new ReadingRow(watch, Lang.T("Warnings from the drive"), warn == 0 ? Lang.T("None") : Lang.T("Yes"), "", Dot(warn)));
            if (Raw(3) is ulong spare) rows.Add(new ReadingRow(watch, Lang.T("Spare blocks left"), $"{spare}%", "", spare >= (Raw(4) ?? 10) ? good : warm));
            Count(Lang.T("Data it couldn't read back"), Raw(14), null);
        }
        else
        {
            Count(Lang.T("Bad sectors swapped out"), Raw(5), monthAgo?.Reallocated);
            Count(Lang.T("Sectors waiting to be swapped"), Raw(197), monthAgo?.Pending);
            Count(Lang.T("Sectors it couldn't read"), Raw(198), monthAgo?.Uncorrectable);
            Count(Lang.T("Cable or connection errors"), Raw(199), null);
        }
        if (h.Life is double life) rows.Add(new ReadingRow(use, Lang.T("Life left"), $"{life:0}%", monthAgo?.Life is double l ? $"{l:0}%" : "", null));
        if (h.WrittenGb is double w) rows.Add(new ReadingRow(use, Lang.T("Data written"), Units.DiskBytes((long)(w * 1e9)), monthAgo?.WrittenGb is double wt ? Units.DiskBytes((long)(wt * 1e9)) : "", null));
        if (h.ReadGb is double r) rows.Add(new ReadingRow(use, Lang.T("Data read"), Units.DiskBytes((long)(r * 1e9)), monthAgo?.ReadGb is double rt ? Units.DiskBytes((long)(rt * 1e9)) : "", null));
        if (h.PowerOnHours is long hrs) rows.Add(new ReadingRow(use, Lang.T("Hours on"), $"{hrs:N0}", monthAgo?.Hours is long ht ? $"{ht:N0}" : "", null));
        if (h.PowerOnCount is long ons) rows.Add(new ReadingRow(use, Lang.T("Times switched on"), $"{ons:N0}", monthAgo?.PowerOns is long ot ? $"{ot:N0}" : "", null));
        if ((h.IsNvme ? Raw(13) : Raw(192) ?? Raw(174)) is ulong cuts) rows.Add(new ReadingRow(use, Lang.T("Sudden shut-offs"), $"{cuts:N0}", "", null));
        if (h.Temperature is double temp) rows.Add(new ReadingRow(use, Lang.T("Temperature"), $"{temp:0}°", "", null));
        return rows;
    }

    /// <summary>Every attribute as the drive names it, with its score, worst, limit and raw count.</summary>
    public static List<ReadingRow> Own(DriveHealthInfo h) =>
        [.. (h.Attributes ?? []).Select(a => new ReadingRow("", a.Name, $"{a.Raw:N0}", "", null, $"{a.Id}", h.IsNvme ? "" : $"{a.Current}", h.IsNvme ? "" : $"{a.Worst}", h.IsNvme ? "" : $"{a.Threshold}", a.Name))];
}
