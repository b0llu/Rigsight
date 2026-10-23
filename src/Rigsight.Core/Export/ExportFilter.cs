using Rigsight.Core.Data;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Export;

/// <summary>The readings a temperatures file can hold, as the Temperatures page's legend names them.</summary>
[Flags]
public enum ExportReadings
{
    None = 0,
    CpuTemp = 1,
    GpuTemp = 2,
    GpuHotSpot = 4,
    GpuMemory = 8,
    Load = 16,
    Power = 32,
    All = CpuTemp | GpuTemp | GpuHotSpot | GpuMemory | Load | Power,
}

/// <summary>
/// What a page narrows its export to, the way the page itself is narrowed: the app or game picked, the crashes its
/// filter shows, the kinds of change, the drives, the readings. Each part left empty keeps everything.
/// </summary>
public sealed record ExportFilter
{
    public static readonly ExportFilter None = new();

    /// <summary>Only these programs (their file names, "chrome.exe"); null: every app.</summary>
    public IReadOnlySet<string>? Apps { get; init; }

    /// <summary>Only apps of this category (the user's own category for an app first, as the Apps page sorts them); null: every one.</summary>
    public Settings.AppCategory? Category { get; init; }

    /// <summary>Only apps whose name or program has each of these words (the Apps page's search); null or none: every one.</summary>
    public IReadOnlyList<string>? AppWords { get; init; }

    /// <summary>Only sessions of games.</summary>
    public bool GamesOnly { get; init; }

    /// <summary>Only the crashes the Crashes page's filter and search would show; null: every one.</summary>
    public Func<CrashEvent, bool>? Crashes { get; init; }

    public bool HasCrash(CrashEvent e) => Crashes is null || Crashes(e);

    /// <summary>Only these kinds of change; null: every kind.</summary>
    public IReadOnlySet<ChangeKind>? ChangeKinds { get; init; }

    /// <summary>Only the changes that say each of these words (the Timeline's search); null or none: every change.</summary>
    public IReadOnlyList<string>? ChangeWords { get; init; }

    /// <summary>Only the changes that made something measurably better or worse.</summary>
    public bool ResultsOnly { get; init; }

    /// <summary>Only these drives, by letter ("C:"); null: every drive.</summary>
    public IReadOnlySet<string>? Drives { get; init; }

    /// <summary>The readings a temperatures file has columns for.</summary>
    public ExportReadings Readings { get; init; } = ExportReadings.All;

    /// <summary>Whether an app is kept: its program, category and name against what's picked.</summary>
    public bool HasApp(string? exe, Settings.AppCategory? category = null, string? name = null) =>
        (Apps is null || exe is not null && Apps.Contains(exe))
        && (Category is null || category == Category)
        && (AppWords is not { Count: > 0 } words || words.All(w => (name?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false) || (exe?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)));

    /// <summary>Whether anything narrows the files (their sizes are then "up to").</summary>
    public bool Narrows => Apps is not null || Category is not null || AppWords is { Count: > 0 } || GamesOnly || Crashes is not null
        || ChangeKinds is not null || ChangeWords is { Count: > 0 } || ResultsOnly || Drives is not null;

    public bool HasChange(SystemChange c) =>
        (ChangeKinds is null || ChangeKinds.Contains(c.Kind))
        && (ChangeWords is not { Count: > 0 } words || TextMatch.Has([.. words], c.Title, c.Was, c.Now) || TextMatch.Has([.. words], c.Shown));

    public bool HasDrive(string drive) => Drives is null || Drives.Contains(drive.TrimEnd('\\'));
}
