using System.Globalization;

namespace Rigsight.Core.Stability;

/// <summary>
/// How a stored driver version's start is known: <see cref="Unknown"/> it was there at RigSight's first look (or the log
/// said nothing of it), <see cref="Logged"/> from Windows' log of driver installs (Kernel-PnP event 400), <see cref="Found"/>
/// a later daily look found it new (its time from the log where the log has it, else the look's own).
/// </summary>
public enum DriverStart { Unknown, Logged, Found }

/// <summary>One device's driver as a look found it (the agent reads it from Windows' device list, no admin rights needed).</summary>
/// <param name="Instance">Windows' device instance ID ("USB\VID_046D&amp;PID_C53F\5&amp;2A…").</param>
/// <param name="Shown">The version as its maker writes it (NVIDIA's 621.24 for Windows' 32.0.16.2124); the raw one otherwise.</param>
/// <param name="Inbox">A driver that comes with Windows (its INF isn't a third-party oemNN.inf).</param>
public sealed record DriverSeen(string Instance, string Name, string ClassGuid, string Maker, string Version, string Shown, DateTime? Date, bool Inbox)
{
    public string Key => DriverRecord.KeyOf(Instance);
}

/// <summary>One "device configured" entry of Windows' Kernel-PnP log (event 400): the driver a device was set up with, and when.</summary>
public sealed record PnpConfigured(DateTime Time, string Instance, string Version, string Provider, DateTime? Date, bool Inbox, string ClassGuid)
{
    public string Key => DriverRecord.KeyOf(Instance);
}

/// <summary>
/// A stored row of the driver history: a version a device had from <see cref="Since"/> (to the device's next row), and the
/// last time it was seen on it. The device's facts (name, maker, kind) ride on each row: a device has only a few.
/// </summary>
public sealed record DriverRow(string Key, DateTime Since, string Version, string Shown, string Name, string ClassGuid, string Maker, bool Inbox,
    DateTime? Date, DateTime Seen, DriverStart How);

/// <summary>
/// The rules of the daily driver look (1.22.0, "Drivers inside Timeline"): which device a driver belongs to, what the
/// first look seeds from Windows' log, and what each later look adds. The agent reads, Core decides, the database keeps.
/// </summary>
public static class DriverRecord
{
    /// <summary>A device keeps at most this many versions; the oldest go first.</summary>
    public const int MostVersions = 30;

    /// <summary>
    /// The device a driver belongs to, followed by its hardware ID as the bus reported it: the instance ID without its
    /// last part ("USB\VID_046D&amp;PID_C53F" for "USB\VID_046D&amp;PID_C53F\5&amp;2a…&amp;0&amp;3"), so the same mouse in another
    /// port, unplugged and plugged back, is the same device, not a new driver. Devices made up by Windows or by software
    /// ("ROOT\SYSTEM\0001", "SWD\MMDEVAPI\{…}") are told apart only by their whole instance ID, which stays.
    /// </summary>
    public static string KeyOf(string instance)
    {
        string id = (instance ?? "").Trim().ToUpperInvariant();
        var parts = id.Split('\\');
        if (parts.Length < 3) return id;
        if (parts[0] is "ROOT" or "SWD" or "STORAGE" or "HTREE" or "SW") return id;
        return $"{parts[0]}\\{parts[1]}";
    }

    /// <summary>
    /// The first look: every version Windows' log still holds, device by device, then each present device's version now.
    /// The log has no "from" version, and "DeviceUpdated" is set on most entries whatever happened, so a change is only
    /// ever one version followed by another. A present device the log says nothing of starts at this look
    /// (<see cref="DriverStart.Unknown"/>); one whose last logged version isn't today's gets today's from this look too.
    /// </summary>
    /// <param name="nameOf">The name Windows keeps for a device no longer present, by its instance ID (null: unknown).</param>
    public static List<DriverRow> Seed(IEnumerable<PnpConfigured> log, IReadOnlyList<DriverSeen> seen, DateTime now, Func<string, string?>? nameOf = null)
    {
        var present = Present(seen);
        var rows = new List<DriverRow>();
        foreach (var device in log.Where(e => e.Version.Length > 0).GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            present.TryGetValue(device.Key, out var here);
            var events = device.OrderBy(e => e.Time).ToList();
            string name = here?.Name ?? nameOf?.Invoke(events[^1].Instance) ?? FallbackName(events[^1].Instance);
            DriverRow? run = null;
            var mine = new List<DriverRow>();
            foreach (var e in events)
            {
                if (run is not null && run.Version.Equals(e.Version, StringComparison.OrdinalIgnoreCase))
                {
                    // The same version configured again (a replug, a Windows update touching every device): still on it.
                    run = run with { Seen = e.Time };
                    mine[^1] = run;
                    continue;
                }
                string maker = Maker(e.Provider);
                run = new DriverRow(device.Key, e.Time, e.Version, Shown(maker, e.ClassGuid, e.Version), name, e.ClassGuid, maker, e.Inbox, e.Date, e.Time, DriverStart.Logged);
                mine.Add(run);
            }
            if (here is not null)
            {
                var last = mine[^1];
                if (last.Version.Equals(here.Version, StringComparison.OrdinalIgnoreCase))
                    mine[^1] = last with { Seen = now, Shown = here.Shown, Name = here.Name, Maker = here.Maker, Date = here.Date ?? last.Date, Inbox = here.Inbox };
                else
                    mine.Add(Row(here, now, DriverStart.Unknown));
            }
            rows.AddRange(Trim(mine));
        }
        foreach (var here in present.Values.Where(d => !rows.Any(r => r.Key.Equals(d.Key, StringComparison.OrdinalIgnoreCase))))
            rows.Add(Row(here, now, DriverStart.Unknown));
        return rows;
    }

    /// <summary>
    /// A later look against what's stored: a present device on the same version is seen again; a new version or a new
    /// device is a row from when Windows' log says it was configured with it (after the version before), else from now.
    /// Devices not seen keep their last seen time (gone for 30 days, they're no longer on this PC).
    /// </summary>
    /// <param name="recent">Windows' log of driver installs since the look before (or a little earlier).</param>
    /// <returns>The rows to write (new ones and ones seen again) and a line for the log for each change found.</returns>
    public static (List<DriverRow> Write, List<string> Said) Update(IReadOnlyList<DriverRow> stored, IReadOnlyList<DriverSeen> seen, DateTime now, IReadOnlyList<PnpConfigured> recent)
    {
        var latest = stored.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.MaxBy(r => r.Since)!, StringComparer.OrdinalIgnoreCase);
        var write = new List<DriverRow>();
        var said = new List<string>();
        foreach (var here in Present(seen).Values)
        {
            if (latest.TryGetValue(here.Key, out var last) && last.Version.Equals(here.Version, StringComparison.OrdinalIgnoreCase))
            {
                write.Add(last with { Seen = now, Shown = here.Shown, Name = here.Name, Maker = here.Maker, Date = here.Date ?? last.Date, Inbox = here.Inbox, ClassGuid = here.ClassGuid });
                continue;
            }
            DateTime after = last?.Since ?? DateTime.MinValue;
            var logged = recent.Where(e => e.Key.Equals(here.Key, StringComparison.OrdinalIgnoreCase) && e.Version.Equals(here.Version, StringComparison.OrdinalIgnoreCase)
                                           && e.Time > after && e.Time <= now).MinBy(e => e.Time);
            write.Add(Row(here, logged?.Time ?? now, DriverStart.Found) with { Seen = now });
            string when = logged is null ? $"found at {now:d MMM HH:mm}" : $"at {logged.Time:d MMM HH:mm} (Windows' log)";
            said.Add(last is null ? $"new: {here.Name} ({here.Maker}) {here.Shown}, {when}" : $"{here.Name} ({here.Maker}) {last.Shown} to {here.Shown}, {when}");
        }
        return (write, said);
    }

    /// <summary>The present devices by key; two devices of one key (two of the same mouse) count as one, the first by instance.</summary>
    private static Dictionary<string, DriverSeen> Present(IEnumerable<DriverSeen> seen)
    {
        var map = new Dictionary<string, DriverSeen>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in seen.Where(d => d.Version.Length > 0).OrderBy(d => d.Instance, StringComparer.OrdinalIgnoreCase)) map.TryAdd(d.Key, d);
        return map;
    }

    private static DriverRow Row(DriverSeen d, DateTime since, DriverStart how) =>
        new(d.Key, since, d.Version, d.Shown, d.Name, d.ClassGuid, d.Maker, d.Inbox, d.Date, since, how);

    /// <summary>A device's newest <see cref="MostVersions"/> rows.</summary>
    private static IEnumerable<DriverRow> Trim(List<DriverRow> rows) => rows.Count <= MostVersions ? rows : rows.Skip(rows.Count - MostVersions);

    /// <summary>The maker as people know it ("Realtek Semiconductor Corp." is Realtek Semiconductor; "Advanced Micro Devices, Inc." AMD).</summary>
    public static string Maker(string provider) => ChangeLogReader.Shorten((provider ?? "").Trim());

    /// <summary>A graphics card's version as its maker writes it (NVIDIA's 621.24); any other driver's as Windows has it.</summary>
    public static string Shown(string maker, string classGuid, string version, string? radeon = null) =>
        DriverHistory.KindOf(classGuid) == DriverKind.Graphics ? Inventory.DriverVersion(maker, version, radeon) : version.Trim();

    /// <summary>A device Windows no longer has a name for: its hardware ID ("VID_046D&amp;PID_C53F").</summary>
    internal static string FallbackName(string instance)
    {
        var parts = (instance ?? "").Split('\\');
        return parts.Length >= 2 ? parts[1] : instance ?? "";
    }

    /// <summary>The name Windows keeps in a device's DeviceDesc ("@oem72.inf,%xvdd.devicedesc%;Xbox storage controller").</summary>
    public static string? NameFromDesc(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return null;
        int semi = desc.LastIndexOf(';');
        string name = (semi >= 0 ? desc[(semi + 1)..] : desc).Trim();
        return name.Length == 0 || name.StartsWith('@') ? null : name;
    }

    /// <summary>A driver's date as event 400 writes it ("10/11/2026", month first), or null.</summary>
    public static DateTime? ParseLoggedDate(string? text) =>
        DateTime.TryParseExact(text?.Trim(), ["M/d/yyyy", "MM/dd/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
