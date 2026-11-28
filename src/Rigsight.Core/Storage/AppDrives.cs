using Rigsight.Core.Data;

namespace Rigsight.Core.Storage;

/// <summary>What one app read from and wrote to one physical drive over a period, by the drive's key (<see cref="DriveKey"/>).</summary>
public sealed record DriveShare(string Disk, long Read, long Write)
{
    public long Total => Read + Write;
}

/// <summary>
/// What one app read and wrote over a period, on every drive together, and the same split by drive, busiest first
/// (<see cref="AppDrives.ByApp"/>). Storage's "What reads and writes the most" shows it: the drives under the app's name
/// ("E:, C: and F:") and, opened, a line per drive.
/// </summary>
public sealed record AppDriveUse(long App, long Read, long Write, IReadOnlyList<DriveShare> Drives)
{
    public long Total => Read + Write;
    /// <summary>The drive it read and wrote most on; null with none.</summary>
    public string? MainDisk => Drives.Count > 0 ? Drives[0].Disk : null;
}

/// <summary>
/// Each app's reads and writes split by drive (1.23.0, Storage): built once here from the rows the agent keeps per app
/// and drive (disk_app_hour, disk_app_day), so the list and its split come from one read, never a query per app.
/// </summary>
public static class AppDrives
{
    /// <summary>
    /// The span a period reads: Day is the last 24 hours, hour by hour; a week or a month is whole days, today's
    /// included (<paramref name="days"/> of them). From is in, To is not (Unix seconds).
    /// </summary>
    public static (long From, long To, bool ByHour) Span(int days, DateTime now)
    {
        if (days <= 1) return (TimeUtil.ToUnix(now.AddHours(-24)), TimeUtil.ToUnix(now) + 3600, true);
        return (TimeUtil.ToUnix(now.Date.AddDays(-(days - 1))), TimeUtil.ToUnix(now.Date.AddDays(1)), false);
    }

    /// <summary>Days picked in the calendar (both in it, either order): whole days, from local midnight to the midnight after the last.</summary>
    public static (long From, long To, bool ByHour) Span(DateTime first, DateTime last)
    {
        var (a, b) = first <= last ? (first.Date, last.Date) : (last.Date, first.Date);
        return (TimeUtil.ToUnix(a), TimeUtil.ToUnix(b.AddDays(1)), false);
    }

    /// <summary>Every app's use over a span from the database, split by drive (see <see cref="ByApp"/>).</summary>
    public static List<AppDriveUse> Read(RigsightDb db, (long From, long To, bool ByHour) span, string? disk = null) =>
        ByApp(span.ByHour ? db.GetDiskAppHours(span.From, span.To) : db.GetDiskAppDays(span.From, span.To), disk);

    /// <summary>
    /// The rows (any number per app, drive and hour or day) added up per app, each app's drives busiest first (reads
    /// and writes together; a tie by key, so the order never shifts between reads), the apps most first. With
    /// <paramref name="disk"/> only that drive's rows count. An app or a drive with nothing read or written is left out.
    /// </summary>
    public static List<AppDriveUse> ByApp(IEnumerable<DiskAppUse> rows, string? disk = null)
    {
        var apps = new Dictionary<long, Dictionary<string, (long Read, long Write)>>();
        foreach (var u in rows)
        {
            if (u.Disk is null || (disk is not null && u.Disk != disk)) continue;
            if (!apps.TryGetValue(u.App, out var drives)) apps[u.App] = drives = new(StringComparer.Ordinal);
            var (r, w) = drives.GetValueOrDefault(u.Disk);
            drives[u.Disk] = (r + u.Read, w + u.Write);
        }
        var list = new List<AppDriveUse>(apps.Count);
        foreach (var (app, drives) in apps)
        {
            var shares = drives.Where(d => d.Value.Read + d.Value.Write > 0)
                .Select(d => new DriveShare(d.Key, d.Value.Read, d.Value.Write))
                .OrderByDescending(d => d.Total).ThenBy(d => d.Disk, StringComparer.Ordinal).ToList();
            if (shares.Count == 0) continue;
            list.Add(new AppDriveUse(app, shares.Sum(d => d.Read), shares.Sum(d => d.Write), shares));
        }
        return [.. list.OrderByDescending(a => a.Total).ThenBy(a => a.App)];
    }

    /// <summary>
    /// The drives under an app's name, busiest first: "C:", "C: and E:", "E:, C: and F:". Each drive by its letter, or
    /// its short name where it has none (see <see cref="ShortName"/>); a drive with neither (null) is "another drive",
    /// said once however many there are; a drive said twice is said once.
    /// </summary>
    public static string Caption(IEnumerable<string?> drives)
    {
        var said = new List<string>();
        foreach (var d in drives)
        {
            string word = string.IsNullOrWhiteSpace(d) ? Lang.T("another drive") : d;
            if (!said.Contains(word, StringComparer.OrdinalIgnoreCase)) said.Add(word);
        }
        return Wording.Capital(Wording.JoinAnd(said));
    }

    /// <summary>
    /// A drive with no letter in a few words: its model without the size ("Samsung SSD 980 PRO 1TB" is "Samsung SSD
    /// 980"), at most three words. With no model known, the one in its key ("model|size"); a key that is only a serial
    /// number says nothing to a person: null.
    /// </summary>
    public static string? ShortName(string? model, string? key = null)
    {
        string name = model?.Trim() ?? "";
        if (name.Length == 0 && key is not null && key.IndexOf('|') is int bar and > 0) name = key[..bar].Trim();
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !IsSize(w)).Take(3).ToList();
        return words.Count == 0 ? null : string.Join(' ', words);
    }

    // "1TB", "500GB", "2T", "480G": a size, which the drive's line says elsewhere.
    private static bool IsSize(string word)
    {
        int i = 0;
        while (i < word.Length && (char.IsDigit(word[i]) || word[i] == '.')) i++;
        if (i == 0 || i == word.Length) return false;
        return word[i..].ToUpperInvariant() is "TB" or "GB" or "T" or "G";
    }
}
