using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace Rigsight.Core.Stability;

public enum ChangeKind { Driver, WindowsUpdate }

/// <summary>Something that changed on the PC (a driver or a Windows update), as a possible cause of later crashes.</summary>
public sealed record SystemChange(DateTime Time, ChangeKind Kind, string Title)
{
    public string Short => $"{Title} ({Time:d MMM})";
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

        // A driver delivered by Windows Update shows up in both logs: keep the driver entry.
        var drivers = list.Where(c => c.Kind == ChangeKind.Driver).ToList();
        list.RemoveAll(c => c.Kind == ChangeKind.WindowsUpdate && Version().Match(c.Title) is { Success: true } v &&
                            drivers.Any(d => d.Title.Contains(v.Value) && Math.Abs((d.Time - c.Time).TotalHours) < 3));
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

                string title;
                if (provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && inf.StartsWith("nv", StringComparison.OrdinalIgnoreCase))
                {
                    // NVIDIA's installer logs its audio and helper parts, not the display driver itself: one entry per install.
                    if (list.Any(c => c.Kind == ChangeKind.Driver && c.Title.StartsWith("NVIDIA graphics") && Math.Abs((c.Time - r.TimeCreated!.Value).TotalHours) < 2)) continue;
                    title = "NVIDIA graphics driver installed";
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
                list.Add(new SystemChange(r.TimeCreated ?? from, ChangeKind.Driver, title));
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
                list.Add(new SystemChange(r.TimeCreated ?? from, ChangeKind.WindowsUpdate, Describe(title)));
            }
        }
    }

    /// <summary>Defender definitions (several a day), Store apps and the malware scanner aren't worth listing.</summary>
    private static bool IsRoutine(string title) =>
        title.Contains("Security Intelligence Update", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Malicious Software Removal Tool", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("antimalware platform", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Windows Security platform", StringComparison.OrdinalIgnoreCase) ||
        StoreApp().IsMatch(title);

    private static string Describe(string title)
    {
        var kb = Kb().Match(title);
        if (title.Contains("Cumulative Update", StringComparison.OrdinalIgnoreCase) || title.Contains("Security Update", StringComparison.OrdinalIgnoreCase))
            return kb.Success ? $"Windows update {kb.Value}" : "Windows update";
        if (title.Contains(".NET", StringComparison.OrdinalIgnoreCase))
            return kb.Success ? $".NET update {kb.Value}" : ".NET update";
        // Drivers delivered by Windows Update: "NVIDIA - Display - 32.0.15.8097".
        // "LG Electronics Inc. Extension Driver Update (1.1.2026.6241)".
        var driver = DriverUpdate().Match(title);
        if (driver.Success) return $"{Shorten(driver.Groups[1].Value)} driver {driver.Groups[2].Value} (from Windows Update)";
        var parts = title.Split(" - ");
        if (parts.Length >= 3) return $"{Shorten(parts[0])} {parts[1].ToLowerInvariant()} driver {parts[^1]} (from Windows Update)";
        return title.Length > 70 ? title[..67] + "…" : title;
    }

    private static string Shorten(string provider) => provider
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
