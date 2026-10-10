using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace Rigsight.Core.Stability;

public enum ChangeKind
{
    Driver,
    WindowsUpdate,
    /// <summary>A new version of Windows (24H2 to 25H2).</summary>
    Windows,
    AppInstalled,
    AppRemoved,
    AppUpdated,
    /// <summary>A program that starts with Windows was added, removed, or switched on or off.</summary>
    Startup,
    /// <summary>A part was added, removed or swapped: graphics card, drive, memory, processor, motherboard.</summary>
    Hardware,
    /// <summary>The motherboard's BIOS.</summary>
    Firmware,
    /// <summary>A Windows setting that affects how the PC runs (Fast Startup, Game Mode…).</summary>
    Setting,
    /// <summary>A drive's used space jumped or dropped within a day.</summary>
    Storage,
    /// <summary>An app or one of its processes was ended from the Processes page (see <see cref="TaskEnded"/>).</summary>
    TaskEnded,
}

/// <summary>
/// Something that changed on the PC, as a line on the Changes page and a possible cause of later crashes.
/// <see cref="Title"/> reads by itself ("NVIDIA graphics driver 616.92", "7-Zip updated to 26.01").
/// </summary>
public sealed record SystemChange(DateTime Time, ChangeKind Kind, string Title)
{
    public long Id { get; init; }

    /// <summary>What changed, the same from one change of it to the next (a driver's package, an app's name).</summary>
    public string Subject { get; init; } = "";

    /// <summary>How it was before (a version, a size, "on"), where that's known.</summary>
    public string? Was { get; init; }

    /// <summary>How it is now.</summary>
    public string? Now { get; init; }

    /// <summary>
    /// The time is when the change was noticed, not when it happened: it was found by comparing the PC's inventory
    /// with the last one (see <see cref="Inventory"/>), so it happened in the minutes before. Drivers and updates read
    /// from Windows' logs carry the exact time (a graphics driver too, when its log entry was found).
    /// </summary>
    public bool IsApproximate => Subject.IndexOf(':') is > 0 and var colon && Inventory.IsKind(Subject[..colon]);

    /// <summary>
    /// When the check before the one that found it ran: it happened between then and <see cref="Time"/>. Null for an
    /// exact time, and for changes recorded before this was kept (then the usual ten minutes are assumed).
    /// </summary>
    public DateTime? NoticedFrom { get; init; }

    /// <summary>
    /// When the PC was on again, if it was off or asleep for part of the time since the check before (a check at
    /// midnight, the next at 7:20 after a start at 7:18): the change is from those two minutes, short of one made in
    /// the last minutes before the PC went down. Null when it was on throughout. For saying when; what is set against
    /// other things is <see cref="Earliest"/>.
    /// </summary>
    public DateTime? OnFrom { get; init; }

    /// <summary>The earliest it is likely to have happened: not while the PC was off.</summary>
    public DateTime Likely => OnFrom is { } on && on > Earliest && on < Time ? on : Earliest;

    /// <summary>The earliest it can have happened.</summary>
    public DateTime Earliest => !IsApproximate ? Time : NoticedFrom is { } from && from < Time ? from : Time.AddMinutes(-Inventory.ScanMinutes);

    public string Short => $"{Title} ({Time:d MMM})";

    /// <summary>A second line under the title: what it was before.</summary>
    public string? Detail => Kind == ChangeKind.Storage ? $"{Was} to {Now} in use"
        : Kind == ChangeKind.TaskEnded ? (string.IsNullOrEmpty(Now) ? null : $"It held {Now}.")
        : string.IsNullOrEmpty(Was) ? null : $"Was {Was}";

    /// <summary>Drivers and updates are things that were installed; the rest say what happened themselves.</summary>
    public string Line => Kind is ChangeKind.Driver or ChangeKind.WindowsUpdate ? $"Installed: {Title}" : Title;

    /// <summary>A graphics card's driver (the inventory follows those by version, see <see cref="Inventory"/>).</summary>
    public bool IsGraphicsDriver => Kind == ChangeKind.Driver && Title.Contains("graphics driver", StringComparison.OrdinalIgnoreCase);

    /// <summary>Changes that can explain a PC-level problem: what Windows runs on, not which apps came and went.</summary>
    public bool IsSystemLevel => Kind is ChangeKind.Driver or ChangeKind.WindowsUpdate or ChangeKind.Windows or ChangeKind.Hardware or ChangeKind.Firmware;
}

/// <summary>
/// Reads driver installs and Windows updates from the event logs, leaving out the routine noise
/// (daily Defender definitions, Store app updates, virtual adapters re-created with the same version).
/// </summary>
public static partial class ChangeLogReader
{
    // Kernel-PnP "device configured": properties [2] class GUID, [4] version, [5] provider, [11] updated, [14] driver package.
    private const string PnpLog = "Microsoft-Windows-Kernel-PnP/Configuration";
    private const string PnpQuery = "*[System[EventID=400]]";
    private const string UpdateQuery = "*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and EventID=19]]";

    private static readonly Dictionary<string, string> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["4d36e968-e325-11ce-bfc1-08002be10318"] = "graphics",
        ["4d36e96c-e325-11ce-bfc1-08002be10318"] = "audio",
        ["4d36e972-e325-11ce-bfc1-08002be10318"] = "network",
        ["4d36e97d-e325-11ce-bfc1-08002be10318"] = "system",
        ["4d36e97b-e325-11ce-bfc1-08002be10318"] = "storage controller",
        ["4d36e96a-e325-11ce-bfc1-08002be10318"] = "storage",
        ["36fc9e60-c465-11cf-8056-444553540000"] = "USB",
        ["745a17a0-74d3-11d0-b6fe-00a0c90f57da"] = "input",
        ["e0cbf06c-cd8b-4647-bb8a-263b43f0f974"] = "Bluetooth",
    };

    /// <summary>Driver installs and Windows updates between the two times, oldest first.</summary>
    public static List<SystemChange> Read(DateTime from, DateTime to)
    {
        var list = new List<SystemChange>();
        try { ReadDrivers(from, to, list); } catch (Exception ex) { Log.Error("changes", ex); }
        try { ReadUpdates(from, to, list); } catch (Exception ex) { Log.Error("changes", ex); }
        return Merge(list);
    }

    internal static List<SystemChange> Merge(List<SystemChange> list)
    {
        // A driver delivered by Windows Update shows up in both logs: keep the driver entry.
        var drivers = list.Where(c => c.Kind == ChangeKind.Driver).ToList();
        // NVIDIA's own entry carries no version to match by ("NVIDIA graphics driver installed"): by its name, then.
        bool Same(SystemChange update, SystemChange driver, string version) => driver.Title.Contains(version)
            || (driver.Title.StartsWith("NVIDIA graphics", StringComparison.Ordinal) && !Version().IsMatch(driver.Title)
                && update.Title.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                && update.Title.Contains("display", StringComparison.OrdinalIgnoreCase));
        list.RemoveAll(c => c.Kind == ChangeKind.WindowsUpdate && Version().Match(c.Title) is { Success: true } v &&
                            drivers.Any(d => Same(c, d, v.Value) && Math.Abs((d.Time - c.Time).TotalHours) < 3));
        return [.. list.OrderBy(c => c.Time)];
    }

    private static void ReadDrivers(DateTime from, DateTime to, List<SystemChange> list)
    {
        // Each driver package counts once per version: VPN and virtual adapters get "installed" again every time they're created.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Query(PnpLog, PnpQuery, from, to))
        {
            using (r)
            {
                if (Prop(r, 11) is not "True") continue;
                string provider = Prop(r, 5)?.Trim() ?? "";
                string version = Prop(r, 4) ?? "";
                string package = Prop(r, 14) ?? "";
                string inf = package.Split('.')[0];
                // Windows' own drivers change with Windows updates, which are listed separately.
                if (provider.Length == 0 || provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add($"{inf}|{version}")) continue;

                string title, subject = inf.ToLowerInvariant();
                string? now = version;
                if (provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && inf.StartsWith("nv", StringComparison.OrdinalIgnoreCase))
                {
                    // NVIDIA's installer logs its audio and helper parts, not the display driver itself: one entry per install.
                    if (list.Any(c => c.Kind == ChangeKind.Driver && c.Title.StartsWith("NVIDIA graphics") && Math.Abs((c.Time - r.TimeCreated!.Value).TotalHours) < 2)) continue;
                    title = "NVIDIA graphics driver installed";
                    (subject, now) = ("nvidia-graphics", null);
                }
                else if (inf.Equals("pawnio", StringComparison.OrdinalIgnoreCase))
                {
                    title = $"PawnIO sensor driver {version}";
                }
                else
                {
                    string kind = Classes.GetValueOrDefault(Prop(r, 2) ?? "", "device");
                    title = $"{Shorten(provider)} {kind} driver {version}";
                }
                list.Add(new SystemChange(r.TimeCreated ?? from, ChangeKind.Driver, title) { Subject = subject, Now = now });
            }
        }
    }

    private static void ReadUpdates(DateTime from, DateTime to, List<SystemChange> list)
    {
        foreach (var r in Query("System", UpdateQuery, from, to))
        {
            using (r)
            {
                string title = Prop(r, 0)?.Trim() ?? "";
                if (title.Length == 0 || IsRoutine(title)) continue;
                list.Add(new SystemChange(r.TimeCreated ?? from, ChangeKind.WindowsUpdate, Describe(title)) { Subject = title });
            }
        }
    }

    /// <summary>Defender definitions (several a day), Store apps and the malware scanner aren't worth listing.</summary>
    internal static bool IsRoutine(string title) =>
        title.Contains("Security Intelligence Update", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Malicious Software Removal Tool", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("antimalware platform", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Windows Security platform", StringComparison.OrdinalIgnoreCase) ||
        StoreApp().IsMatch(title);

    internal static string Describe(string title)
    {
        var kb = Kb().Match(title);
        // .NET first: its updates are called "Cumulative Update for .NET Framework…" and ".NET 8.0.20 Security Update…".
        if (title.Contains(".NET", StringComparison.OrdinalIgnoreCase))
            return kb.Success ? $".NET update {kb.Value}" : ".NET update";
        if (title.Contains("Cumulative Update", StringComparison.OrdinalIgnoreCase) || title.Contains("Security Update", StringComparison.OrdinalIgnoreCase))
            return kb.Success ? $"Windows update {kb.Value}" : "Windows update";
        // Drivers delivered by Windows Update: "NVIDIA - Display - 32.0.15.8097".
        // "LG Electronics Inc. Extension Driver Update (1.1.2026.6241)".
        var driver = DriverUpdate().Match(title);
        if (driver.Success) return $"{Shorten(driver.Groups[1].Value)} driver {driver.Groups[2].Value} (from Windows Update)";
        var parts = title.Split(" - ");
        if (parts.Length >= 3) return $"{Shorten(parts[0])} {parts[1].ToLowerInvariant()} driver {parts[^1]} (from Windows Update)";
        return title.Length > 70 ? title[..67] + "…" : title;
    }

    internal static string Shorten(string provider) => provider
        .Replace(" Corporation", "").Replace(" Corp.", "").Replace(", Inc.", "").Replace(" Inc.", "").Replace(" LLC", "")
        .Replace("Advanced Micro Devices", "AMD").Replace("Intel(R)", "Intel").Trim();

    private static IEnumerable<EventRecord> Query(string log, string xpath, DateTime from, DateTime to)
    {
        string time = $"TimeCreated[@SystemTime>='{from.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}' and @SystemTime<'{to.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']";
        var q = xpath.Replace("*[System[", $"*[System[{time} and (").Replace("]]", ")]]");
        EventLogReader reader;
        try { reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, q)); }
        catch (Exception ex)
        {
            Log.Error("changes", ex);
            yield break;
        }
        using (reader)
        {
            while (true)
            {
                EventRecord? record;
                try { record = reader.ReadEvent(); }
                catch { yield break; }
                if (record is null) yield break;
                yield return record;
            }
        }
    }

    private static string? Prop(EventRecord r, int index) =>
        index < r.Properties.Count ? r.Properties[index].Value?.ToString() : null;

    [GeneratedRegex(@"^9[A-Z0-9]{11}-")]
    private static partial Regex StoreApp();

    [GeneratedRegex(@"^(.+?)\s+\S*\s*Driver Update \(([\d.]+)\)")]
    private static partial Regex DriverUpdate();

    [GeneratedRegex(@"\d+(\.\d+){2,}")]
    private static partial Regex Version();

    [GeneratedRegex(@"KB\d{6,8}")]
    private static partial Regex Kb();
}
