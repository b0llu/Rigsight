using Rigsight.Core.Data;

namespace Rigsight.Core.Stability;

/// <summary>
/// Days a drive's used space jumped or dropped: a game installed, a big download, a clean-up. Worked out from the
/// space recorded each day, so they reach back as far as that does.
/// </summary>
public static class StorageChanges
{
    /// <summary>The least that counts: smaller swings happen every day (updates, caches, the page file).</summary>
    public const double MinGb = 10;

    public static List<SystemChange> From(IEnumerable<DriveDay> days)
    {
        var changes = new List<SystemChange>();
        foreach (var drive in days.GroupBy(d => d.Drive, StringComparer.OrdinalIgnoreCase))
        {
            DriveDay? before = null;
            foreach (var day in drive.OrderBy(d => d.Day))
            {
                // A drive of another size under the same letter is another drive: nothing to compare.
                if (before is not null && Math.Abs(day.TotalGb - before.TotalGb) <= before.TotalGb * 0.01)
                {
                    double delta = day.UsedGb - before.UsedGb;
                    if (Math.Abs(delta) >= MinGb)
                    {
                        string letter = drive.Key.TrimEnd('\\');
                        changes.Add(new SystemChange(TimeUtil.FromUnix(day.Day), ChangeKind.Storage,
                            delta > 0 ? $"{delta:N0} GB more in use on {letter}" : $"{-delta:N0} GB freed on {letter}")
                        {
                            Subject = letter, Was = $"{before.UsedGb:N0} GB", Now = $"{day.UsedGb:N0} GB",
                        });
                    }
                }
                before = day;
            }
        }
        return [.. changes.OrderBy(c => c.Time)];
    }
}
