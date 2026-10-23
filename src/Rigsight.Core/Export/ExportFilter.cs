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

    public bool HasApp(string? exe) => Apps is null || exe is not null && Apps.Contains(exe);

    public bool HasChange(SystemChange c) =>
        (ChangeKinds is null || ChangeKinds.Contains(c.Kind))
        && (ChangeWords is not { Count: > 0 } words || TextMatch.Has([.. words], c.Title, c.Was, c.Now) || TextMatch.Has([.. words], c.Shown));

    public bool HasDrive(string drive) => Drives is null || Drives.Contains(drive.TrimEnd('\\'));
}
