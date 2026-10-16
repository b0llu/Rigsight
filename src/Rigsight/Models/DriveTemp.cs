using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Rigsight.Core;

namespace Rigsight.Models;

/// <summary>What a drive is, as far as its temperature's limit goes: an SSD and a hard drive are happy at different heats.</summary>
public enum DriveKind { Unknown, Ssd, HardDrive }

/// <summary>
/// What Windows says of a physical drive (see <see cref="Services.DriveProbe"/>): SSD or hard drive, how it is
/// connected ("NVMe", "SATA", "USB"; null when it says nothing plainer), its size in bytes, and whether Windows itself
/// is on it. Anything Windows didn't answer is left unknown, and the line under the drive's name just leaves it out.
/// </summary>
public sealed record DriveFacts(DriveKind Kind, string? Bus, long? Bytes, bool HoldsWindows)
{
    public static readonly DriveFacts None = new(DriveKind.Unknown, null, null, false);

    /// <summary>"1 TB", "480 GB", "1.9 TB": the way drives are sold (1000s, not 1024s), so it reads as on the box.</summary>
    public static string? SizeText(long? bytes) => bytes switch
    {
        null or <= 0 => null,
        >= 999_500_000_000 => $"{Math.Round(bytes.Value / 1e12, 1).ToString("0.#", CultureInfo.CurrentCulture)} TB",
        _ => $"{Math.Round(bytes.Value / 1e9):0} GB",
    };

    /// <summary>"NVMe SSD", "SATA SSD", "Hard drive", "USB hard drive"; null when not known.</summary>
    public static string? KindText(DriveKind kind, string? bus) => kind switch
    {
        DriveKind.Ssd => bus is null ? "SSD" : $"{bus} SSD",
        DriveKind.HardDrive => bus == "USB" ? "USB hard drive" : "Hard drive",
        _ => null,
    };

    /// <summary>
    /// The line under a drive's name: "NVMe SSD · 1 TB · the C drive" ("the C drive" only on the drive Windows is
    /// on, <paramref name="windowsLetter"/> being its letter). Empty when nothing is known.
    /// </summary>
    public string Line(char windowsLetter)
    {
        var parts = new List<string>();
        if (KindText(Kind, Bus) is { } kind) parts.Add(kind);
        if (SizeText(Bytes) is { } size) parts.Add(size);
        if (HoldsWindows) parts.Add(parts.Count == 0 ? $"The {windowsLetter} drive" : $"the {windowsLetter} drive");
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// A row of the Temperatures page's "Drives": the drive's temperature now and today's range, set against the limit
/// the drive itself reports (its "Warning Temperature", the figure All sensors calls "its limit"), else the usual one
/// for its kind: 70° for an SSD, 50° for a hard drive.
/// </summary>
public sealed partial class DriveTemp : ObservableObject
{
    public const double SsdLimit = 70, HardDriveLimit = 50;

    public DriveTemp(string name, string detail, SensorItem sensor, SensorItem? reportedLimit, DriveKind kind)
    {
        Name = name;
        Detail = detail;
        Sensor = sensor;
        ReportedLimit = reportedLimit;
        Kind = kind;
        // The reported limit comes with the readings, after the row is made (and never moves after): follow it.
        if (reportedLimit is not null)
            reportedLimit.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not (nameof(SensorItem.Value) or "" or null)) return;
                OnPropertyChanged(nameof(Limit));
                OnPropertyChanged(nameof(LimitText));
                OnPropertyChanged(nameof(LimitTip));
                OnPropertyChanged(nameof(Steps));
                OnPropertyChanged(nameof(Top));
                OnPropertyChanged(nameof(LimitWidth));
                OnPropertyChanged(nameof(AboveLimitWidth));
            };
    }

    public string Name { get; }
    /// <summary>"NVMe SSD · 1 TB · the C drive"; empty when Windows said nothing of the drive.</summary>
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
    public SensorItem Sensor { get; }
    /// <summary>The drive's own "Warning Temperature" sensor, when it has one.</summary>
    public SensorItem? ReportedLimit { get; }
    public DriveKind Kind { get; }

    /// <summary>
    /// The limit the drive reports where it reports a believable one, else its kind's usual. A drive of unknown kind
    /// is held to the hard drive's, the lower: a warning too early beats one too late.
    /// </summary>
    public double Limit => ReportedLimit?.Value is double reported and >= 40 and <= 120 ? reported : UsualLimit(Kind);

    public static double UsualLimit(DriveKind kind) => kind == DriveKind.Ssd ? SsdLimit : HardDriveLimit;

    /// <summary>The first temperature sensor of a drive named as its warning limit (the one All sensors' "its limit is" reads).</summary>
    public static SensorItem? ReportedLimitOf(IEnumerable<SensorItem> sensors) =>
        sensors.FirstOrDefault(x => x.Kind == SensorKind.Temperature && x.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase));

    public string LimitText => $"limit {Units.TempShort(Limit)}";

    /// <summary>Where the limit comes from, said under the pointer: the drive's own figure, or the usual one for its kind.</summary>
    public string LimitTip => ReportedLimit?.Value is double reported and >= 40 and <= 120 ? "The limit this drive reports for itself"
        : Kind == DriveKind.Ssd ? "This drive reports no limit: the usual one for an SSD"
        : Kind == DriveKind.HardDrive ? "This drive reports no limit: the usual one for a hard drive"
        : "This drive reports no limit, and Windows didn't say what kind it is: the lower usual limit, a hard drive's";

    /// <summary>
    /// Its colour's steps, from its own limit: cool up to 35° (or 15° under a low limit), warm from 10° under the
    /// limit, hot at it. One drive at 45° is fine, another at 45° is near its limit, and the colour says which.
    /// </summary>
    public string Steps => string.Create(CultureInfo.InvariantCulture, $"{Math.Min(35, Limit - 15):0},{Limit - 10:0},{Limit:0}");

    /// <summary>The bar's right end: past the limit, so the mark at the limit stands inside the bar.</summary>
    public double Top => Math.Max(90, Limit + 10);

    /// <summary>The two sides of the mark, as star widths of the bar.</summary>
    public System.Windows.GridLength LimitWidth => new(Limit, System.Windows.GridUnitType.Star);
    public System.Windows.GridLength AboveLimitWidth => new(Top - Limit, System.Windows.GridUnitType.Star);
}

/// <summary>
/// A group of the Temperatures page's "Motherboard": one sensor chip's readings under its name ("ITE IT8686E") and, when
/// known, the board it sits on, both as All sensors names that hardware. Two chips each have a "Temperature #1"; under
/// their own chip they no longer look like one reading shown twice.
/// </summary>
public sealed record BoardChip(string Chip, string? Board, IReadOnlyList<PartTemp> Tiles)
{
    /// <summary>The chip's name in the small capitals of a group's header.</summary>
    public string ChipCaps => Chip.ToUpperInvariant();
    public bool HasBoard => !string.IsNullOrEmpty(Board);

    /// <summary>All sensors' name for a chip ("ITE IT8686E  ·  Gigabyte B450 AORUS PRO-CF"), split into chip and board.</summary>
    public static (string Chip, string? Board) Split(string hardwareName) =>
        hardwareName.Split("  ·  ", 2) is [var chip, var board] ? (chip.Trim(), board.Trim()) : (hardwareName.Trim(), null);

    /// <summary>The same chip and the same tiles (so the list shown can stay as it is).</summary>
    public static bool Same(BoardChip a, BoardChip b) => a.Chip == b.Chip && a.Board == b.Board && a.Tiles.SequenceEqual(b.Tiles);
}
