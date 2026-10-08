using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Rigsight.Core.Stability;

/// <summary>
/// One thing the PC has or is set to: an installed app with its version, a program that starts with Windows, a drive,
/// the BIOS version, a Windows setting. <see cref="Key"/> stays the same from one reading to the next.
/// </summary>
public sealed record InventoryItem(string Kind, string Key, string Name, string Value);

/// <summary>
/// What's installed and how the PC is set up, read from the registry in a few milliseconds. Two readings set side by
/// side (<see cref="Diff"/>) say what changed in between, also for things Windows logs nowhere: an app removed, a
/// program added to startup, a setting switched.
/// </summary>
public static partial class Inventory
{
    public const string App = "app", Startup = "startup", Gpu = "gpu", GpuDriver = "gpudriver", Disk = "disk",
        Cpu = "cpu", Ram = "ram", Board = "board", Bios = "bios", Windows = "windows", Setting = "setting";

    public const string On = "on", Off = "off";

    /// <summary>The setting that isn't on or off: which power plan Windows is on, by name.</summary>
    public const string PowerPlan = "power-plan";

    /// <summary>How often the agent reads the inventory: a change found by it happened within this long before.</summary>
    public const int ScanMinutes = 10;

    /// <summary>Marks a change found in the inventory whose exact time is known after all (from Windows' logs).</summary>
    public const string ExactPrefix = "logged-";

    /// <summary>One of the inventory's kinds (the start of the subject of every change found by it).</summary>
    public static bool IsKind(string kind) => kind is App or Startup or Gpu or GpuDriver or Disk or Cpu or Ram or Board or Bios or Windows or Setting;

    /// <summary>Kinds whose items come and go (an app, a drive); the others are single facts that only change value.</summary>
    public static bool IsList(string kind) => kind is App or Startup or Gpu or Disk;

    /// <summary>
    /// Whose registry this is. The agent can run as the person at the PC or, on a PC where an administrator approved
    /// it, as that administrator: their apps and startup programs differ, which isn't a change on the PC.
    /// </summary>
    public static string Owner()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch { return Environment.UserName; }
    }

    /// <summary>Everything, by kind. A kind that couldn't be read is left out (so nothing is taken as removed).</summary>
    public static Dictionary<string, List<InventoryItem>> Read()
    {
        var all = new Dictionary<string, List<InventoryItem>>();
        void Add(string kind, Func<List<InventoryItem>> read, bool needed = false)
        {
            try
            {
                var items = read();
                if (items.Count > 0 || !needed) all[kind] = items;
            }
            catch (Exception ex) { Log.Error("inventory", ex); }
        }
        Add(App, ReadApps, needed: true);
        Add(Startup, ReadStartup);
        try
        {
            var (cards, drivers) = ReadGraphics();
            // None found: the driver is being installed right now (Windows' basic adapter stands in). Next time.
            if (cards.Count > 0) (all[Gpu], all[GpuDriver]) = (cards, drivers);
        }
        catch (Exception ex) { Log.Error("inventory", ex); }
        Add(Disk, ReadDisks, needed: true);
        Add(Cpu, () => Fact(Cpu, "Processor", Machine(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")));
        Add(Ram, () => Fact(Ram, "Memory", GetPhysicallyInstalledSystemMemory(out long kb) && kb > 0 ? $"{Math.Round(kb / 1048576.0):0} GB" : null));
        Add(Board, () => Fact(Board, "Motherboard", Machine(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct")));
        Add(Bios, () => Fact(Bios, "BIOS", Machine(@"HARDWARE\DESCRIPTION\System\BIOS", "BIOSVersion")));
        Add(Windows, ReadWindows);
        Add(Setting, ReadSettings);
        return all;
    }

    private static List<InventoryItem> Fact(string kind, string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [new(kind, kind, name, Spaces().Replace(value, " ").Trim())];

    // ── Apps ─────────────────────────────────────────────────────────────

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static List<InventoryItem> ReadApps() => AppItems(ListedApps().Select(a => (a.Name, a.Version)));

    /// <summary>The apps Windows lists as installed, as they're written there, with the folder each says it's in ("" for none).</summary>
    private static List<(string Name, string Version, string Folder)> ListedApps()
    {
        var found = new List<(string Name, string Version, string Folder)>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var apps = root.OpenSubKey(UninstallKey);
            if (apps is null) continue;
            foreach (string name in apps.GetSubKeyNames())
            {
                using var app = apps.OpenSubKey(name);
                if (app is null) continue;
                string display = app.GetValue("DisplayName") as string ?? "";
                // Parts of Windows and of other apps, and their patches, aren't apps of their own.
                if (display.Length == 0 || app.GetValue("SystemComponent") is 1 || app.GetValue("ParentKeyName") is string { Length: > 0 }) continue;
                found.Add((display, app.GetValue("DisplayVersion") as string ?? "", app.GetValue("InstallLocation") as string ?? ""));
            }
        }
        return found;
    }

    /// <summary>Where each app's files are, by the app's key, for the apps that say (a drive on its own is no answer).</summary>
    internal static Dictionary<string, List<string>> AppFolders(IEnumerable<(string Name, string Version, string Folder)> listed)
    {
        var folders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (display, version, raw) in listed)
        {
            string folder = raw.Trim().Trim('"').TrimEnd('\\', '/');
            string key = SplitName(display, version).Name.ToLowerInvariant();
            if (folder.Length < 4 || key.Length == 0) continue;
            if (!folders.TryGetValue(key, out var list)) folders[key] = list = [];
            list.Add(folder + '\\');
        }
        return folders;
    }

    /// <summary>
    /// An update that's only waiting isn't one yet. Apps that update themselves (Discord, Chrome) put the new version
    /// beside the running one and tell Windows at once, then go on running the old one until they're next started: a
    /// user's Discord was "updated to 1.0.9261" at 7:20 while it ran 1.0.9260, with its own "install update" button
    /// lit, until 8:05. While a program in the app's folder is still the version the app is leaving, the app stays in
    /// the reading as it was, so the update is found, and dated, by the first reading after the old one has gone.
    /// An app that isn't running, doesn't say where it is, or whose programs carry another number than the one
    /// Windows lists is taken as updated straight away, as before.
    /// </summary>
    /// <param name="running">The programs running now, by their full path; only asked for when an app's version changed.</param>
    public static void HoldRunningUpdates(Dictionary<string, List<InventoryItem>> now, IEnumerable<InventoryItem> before, Func<IEnumerable<string>> running)
    {
        if (!now.TryGetValue(App, out var apps)) return;
        HoldRunningUpdates(apps, before, () => AppFolders(ListedApps()),
            () => running().Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(path => FileVersions(path).Select(version => (path, version))));
    }

    internal static void HoldRunningUpdates(List<InventoryItem> apps, IEnumerable<InventoryItem> before, Func<Dictionary<string, List<string>>> folders,
        Func<IEnumerable<(string Path, string Version)>> running)
    {
        var was = before.Where(i => i.Kind == App).ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>>? where = null;
        List<(string Path, string Version)>? programs = null;
        for (int i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            if (!was.TryGetValue(app.Key, out var old) || string.Equals(old.Value, app.Value, StringComparison.OrdinalIgnoreCase)) continue;
            if (!(where ??= folders()).TryGetValue(app.Key, out var dirs)) continue;
            var leaving = old.Value.Split(", ").Except(app.Value.Split(", "), StringComparer.OrdinalIgnoreCase).ToList();
            programs ??= [.. running()];
            if (programs.Any(p => dirs.Any(d => p.Path.StartsWith(d, StringComparison.OrdinalIgnoreCase)) && leaving.Any(v => SameVersion(v, p.Version))))
                apps[i] = old;
        }
    }

    /// <summary>The version a program file carries, both ways it can be written there.</summary>
    private static IEnumerable<string> FileVersions(string path)
    {
        System.Diagnostics.FileVersionInfo info;
        try { info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path); }
        catch { yield break; } // gone meanwhile
        if (!string.IsNullOrWhiteSpace(info.ProductVersion)) yield return info.ProductVersion;
        if (!string.IsNullOrWhiteSpace(info.FileVersion) && info.FileVersion != info.ProductVersion) yield return info.FileVersion;
    }

    /// <summary>The same numbers, however they're padded or followed: "1.0.9260" is "1.0.9260.0" and "1.0.9260+a1b2c3".</summary>
    internal static bool SameVersion(string a, string b)
    {
        var (x, y) = (Numbers(a), Numbers(b));
        return x.Length > 0 && x.SequenceEqual(y);

        static string[] Numbers(string s)
        {
            s = s.Trim().TrimStart('v', 'V');
            int end = 0;
            while (end < s.Length && (char.IsAsciiDigit(s[end]) || s[end] == '.')) end++;
            var parts = s[..end].Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => p.TrimStart('0')).ToList();
            while (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
            return [.. parts];
        }
    }

    /// <summary>
    /// Apps by the name they keep across versions ("7-Zip 26.01 (x64)" is "7-Zip (x64)", version 26.01). Two versions
    /// side by side (Python 3.12 and 3.13) are one app with both versions.
    /// </summary>
    internal static List<InventoryItem> AppItems(IEnumerable<(string Name, string Version)> apps)
    {
        var byName = new Dictionary<string, (string Name, SortedSet<string> Versions)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (display, version) in apps)
        {
            var (name, found) = SplitName(display, version);
            // The graphics driver is followed as a driver (see ReadGraphics); this app is this page's own.
            if (name.Length == 0 || name.Contains("graphics driver", StringComparison.OrdinalIgnoreCase) || name.Equals("Rigsight", StringComparison.OrdinalIgnoreCase)) continue;
            if (!byName.TryGetValue(name, out var entry)) byName[name] = entry = (name, new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
            if (found.Length > 0) entry.Versions.Add(found);
        }
        return [.. byName.Values.Select(a => new InventoryItem(App, a.Name.ToLowerInvariant(), a.Name, string.Join(", ", a.Versions)))];
    }

    /// <summary>An app's name without its version, and the version (the one Windows lists, else the one in the name).</summary>
    internal static (string Name, string Version) SplitName(string display, string version)
    {
        string name = display;
        version = version.Trim();
        var inName = VersionInName().Match(name);
        if (version.Length == 0 && inName.Success) version = inName.Groups[1].Value;
        // With its "v" or "version" first; then the listed version where it's written some other way.
        name = VersionInName().Replace(name, " ");
        if (version.Length >= 3 && version.Contains('.')) name = name.Replace(version, " ", StringComparison.OrdinalIgnoreCase);
        name = Spaces().Replace(name, " ").Replace("- (", "(").Replace("( )", "").Replace("()", "").Trim();
        return (name.TrimEnd(' ', '-', '–', ',').TrimEnd(), version);
    }

    // ── Startup programs ─────────────────────────────────────────────────

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private static List<InventoryItem> ReadStartup()
    {
        var found = new List<(string Name, bool Enabled)>();
        foreach (var (hive, view, approved) in new[] { (RegistryHive.CurrentUser, RegistryView.Default, "Run"),
                     (RegistryHive.LocalMachine, RegistryView.Registry64, "Run"), (RegistryHive.LocalMachine, RegistryView.Registry32, "Run32") })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var run = root.OpenSubKey(RunKey);
            if (run is null) continue;
            // Task Manager's switch: a value whose first byte is odd turns the program off without removing it.
            using var root64 = RegistryKey.OpenBaseKey(hive, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : view);
            using var state = root64.OpenSubKey(ApprovedKey + approved);
            foreach (string name in run.GetValueNames())
                if (name.Length > 0) found.Add((name, Enabled(state, name)));
        }
        foreach (var (folder, hive) in new[] { (Environment.SpecialFolder.Startup, RegistryHive.CurrentUser), (Environment.SpecialFolder.CommonStartup, RegistryHive.LocalMachine) })
        {
            string path = Environment.GetFolderPath(folder);
            if (path.Length == 0 || !Directory.Exists(path)) continue;
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var state = root.OpenSubKey(ApprovedKey + "StartupFolder");
            foreach (string file in Directory.EnumerateFiles(path))
                if (!Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    found.Add((Path.GetFileNameWithoutExtension(file), Enabled(state, Path.GetFileName(file))));
        }
        return StartupItems(found);

        static bool Enabled(RegistryKey? state, string name) => state?.GetValue(name) is not byte[] { Length: > 0 } b || b[0] % 2 == 0;
    }

    /// <summary>Startup programs by name, without the version or the code some put in it (so an update isn't a new program).</summary>
    internal static List<InventoryItem> StartupItems(IEnumerable<(string Name, bool Enabled)> entries)
    {
        var byName = new Dictionary<string, (string Name, bool Enabled)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, enabled) in entries)
        {
            string name = Spaces().Replace(StartupNoise().Replace(raw, " "), " ").Trim(' ', '_', '-');
            if (name.Length == 0) name = raw.Trim();
            byName[name] = (name, enabled || (byName.TryGetValue(name, out var before) && before.Enabled));
        }
        return [.. byName.Values.Select(s => new InventoryItem(Startup, s.Name.ToLowerInvariant(), s.Name, s.Enabled ? On : Off))];
    }

    // ── Hardware ─────────────────────────────────────────────────────────

    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static (List<InventoryItem> Cards, List<InventoryItem> Drivers) ReadGraphics()
    {
        var found = new List<(string Device, string Name, string Provider, string Version, string? Radeon)>();
        using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
        foreach (string name in cls?.GetSubKeyNames() ?? [])
        {
            if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;
            using var card = cls!.OpenSubKey(name);
            if (card?.GetValue("DriverDesc") is not string desc || card.GetValue("MatchingDeviceId") is not string device) continue;
            found.Add((device, desc, card.GetValue("ProviderName") as string ?? "", card.GetValue("DriverVersion") as string ?? "",
                card.GetValue("RadeonSoftwareVersion") as string));
        }
        return GraphicsItems(found);
    }

    /// <summary>
    /// The real graphics cards (on the PCI bus, with their maker's driver: not virtual displays, not Windows' basic
    /// adapter standing in during an install) and each one's driver version as its maker writes it.
    /// </summary>
    internal static (List<InventoryItem> Cards, List<InventoryItem> Drivers) GraphicsItems(
        IEnumerable<(string Device, string Name, string Provider, string Version, string? Radeon)> found)
    {
        var cards = new List<InventoryItem>();
        var drivers = new List<InventoryItem>();
        foreach (var c in found)
        {
            if (!c.Device.StartsWith(@"pci\", StringComparison.OrdinalIgnoreCase) || c.Provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            string key = c.Device.ToLowerInvariant();
            for (int n = 2; cards.Any(x => x.Key == key); n++) key = $"{c.Device.ToLowerInvariant()}#{n}"; // two of the same card
            cards.Add(new(Gpu, key, c.Name.Trim(), ""));
            string maker = ChangeLogReader.Shorten(c.Provider).Trim();
            string version = DriverVersion(maker, c.Version, c.Radeon);
            if (version.Length > 0) drivers.Add(new(GpuDriver, key, maker.Length > 0 ? $"{maker} graphics driver" : "Graphics driver", version));
        }
        return (cards, drivers);
    }

    /// <summary>NVIDIA's 616.92 is the last five digits of Windows' 32.0.16.1692; AMD names its own; others as Windows has them.</summary>
    internal static string DriverVersion(string maker, string version, string? radeon)
    {
        if (!string.IsNullOrWhiteSpace(radeon)) return radeon.Trim();
        if (maker.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            string digits = new(version.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length >= 5) return $"{digits[^5..^2]}.{digits[^2..]}";
        }
        return version.Trim();
    }

    private static List<InventoryItem> ReadDisks()
    {
        var names = new List<string>();
        using var disks = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\disk\Enum");
        int count = disks?.GetValue("Count") is int n ? n : 0;
        for (int i = 0; i < count; i++)
        {
            if (disks!.GetValue(i.ToString()) is not string id || OnUsb(id)) continue;
            using var device = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{id}");
            names.Add($"{id}\n{device?.GetValue("FriendlyName") as string}");
        }
        return DiskItems(names.Select(x => (x.Split('\n')[0], x.Split('\n')[1])));
    }

    /// <summary>
    /// Whether a device is plugged in over USB, however it names itself: a modern USB SSD or enclosure shows up as a
    /// "SCSI" disk like an internal one (only older ones say USBSTOR), and every plug and unplug was put down as a drive
    /// added to or removed from the PC. Found by what it hangs off: a USB device somewhere above it.
    /// </summary>
    internal static bool OnUsb(string deviceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint node, deviceId, 0) != 0) return false;
            var id = new char[512];
            for (int up = 0; up < 8 && CM_Get_Parent(out node, node, 0) == 0; up++)
                if (CM_Get_Device_IDW(node, id, id.Length, 0) == 0 && new string(id).StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception ex) { Log.Error("inventory", ex); }
        return false;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint node, string deviceId, int flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Parent(out uint parent, uint node, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_IDW(uint node, [Out] char[] buffer, int length, int flags);

    /// <summary>
    /// The drives inside the PC, by name (a drive moved to another port is the same drive). USB drives and virtual
    /// disks (a mounted image, WSL) come and go all day and are left out.
    /// </summary>
    internal static List<InventoryItem> DiskItems(IEnumerable<(string Id, string Name)> found)
    {
        var items = new List<InventoryItem>();
        foreach (var (id, friendly) in found)
        {
            if (id.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase) || id.Contains("Ven_Msft", StringComparison.OrdinalIgnoreCase)
                || friendly.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            string name = Spaces().Replace(friendly, " ").Trim();
            if (name.Length == 0) continue;
            string key = name.ToLowerInvariant();
            for (int n = 2; items.Any(x => x.Key == key); n++) key = $"{name.ToLowerInvariant()}#{n}"; // two of the same drive
            items.Add(new(Disk, key, name, ""));
        }
        return items;
    }

    // ── Windows and its settings ─────────────────────────────────────────

    private static List<InventoryItem> ReadWindows()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        // Windows 11 still calls itself "Windows 10" there; the build number tells them apart.
        string name = int.TryParse(Machine(key, "CurrentBuild"), out int build) && build >= 22000 ? "Windows 11" : "Windows 10";
        return Machine(key, "DisplayVersion") is { Length: > 0 } version ? [new(Windows, Windows, name, version)] : [];
    }

    private static List<InventoryItem> ReadSettings()
    {
        var list = new List<InventoryItem>();
        void Add(string key, string name, bool? on)
        {
            if (on is bool value) list.Add(new(Setting, key, name, value ? On : Off));
        }
        Add("fast-startup", "Fast Startup", Number(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled") is int f ? f != 0 : null);
        Add("gpu-scheduling", "Hardware-accelerated GPU scheduling",
            Number(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") is int h ? h == 2 : null);
        // Both are on (Game Mode) or off (memory integrity) until Windows writes the value.
        Add("game-mode", "Game Mode", (Number(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") ?? 1) != 0);
        Add("memory-integrity", "Memory integrity",
            (Number(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") ?? 0) != 0);

        // The power plan in use: other programs switch it (a game booster, a maker's tool, a driver install), and nothing says so.
        const string plans = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
        if (Machine(plans, "ActivePowerScheme") is { Length: > 0 } active && PlanName(active, Machine($@"{plans}\{active}", "FriendlyName")) is { } plan)
            list.Add(new(Setting, PowerPlan, "Power plan", plan));
        return list;
    }

    /// <summary>
    /// A power plan's name from what the registry holds for it: Windows' own are a pointer into a file with the English
    /// name after it ("@%SystemRoot%\system32\powrprof.dll,-13,High performance"), a maker's or the user's own is just
    /// the name. Without one, the plans every PC has are known by their id.
    /// </summary>
    internal static string? PlanName(string id, string? friendly)
    {
        string name = friendly?.Trim() ?? "";
        if (name.StartsWith('@')) name = name.Split(',', 3) is { Length: 3 } parts ? parts[2].Trim() : "";
        if (name.EndsWith(" (recommended)", StringComparison.OrdinalIgnoreCase)) name = name[..^" (recommended)".Length];
        if (name.Length > 0) return name;
        return id.ToLowerInvariant() switch
        {
            "381b4222-f694-41f0-9685-ff5bb260df2e" => "Balanced",
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "High performance",
            "a1841308-3541-4fab-bc81-f71556f20b4a" => "Power saver",
            "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Ultimate Performance",
            _ => null,
        };
    }

    private static string? Machine(string key, string value)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue(value)?.ToString();
    }

    private static int? Number(RegistryKey hive, string key, string value)
    {
        using var k = hive.OpenSubKey(key);
        return k?.GetValue(value) as int?;
    }

    // ── What changed between two readings ────────────────────────────────

    /// <summary>
    /// The changes from <paramref name="before"/> to <paramref name="after"/> (both of one kind), dated <paramref name="at"/>.
    /// </summary>
    public static List<SystemChange> Diff(string kind, IEnumerable<InventoryItem> before, IEnumerable<InventoryItem> after, DateTime at)
    {
        var changes = new List<SystemChange>();
        var was = before.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        var now = after.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        void Add(ChangeKind k, InventoryItem item, string title, string? old = null, string? value = null) =>
            changes.Add(new SystemChange(at, k, title) { Subject = $"{kind}:{item.Key}", Was = Text(old), Now = Text(value) });

        foreach (var item in now.Values)
        {
            if (!was.TryGetValue(item.Key, out var old))
            {
                // A fact read for the first time (a newer version reads more) isn't a change.
                switch (kind)
                {
                    case App: Add(ChangeKind.AppInstalled, item, $"{item.Name} installed", value: item.Value); break;
                    case Startup when item.Value == On: Add(ChangeKind.Startup, item, $"{item.Name} now starts with Windows"); break;
                    case Gpu: Add(ChangeKind.Hardware, item, $"Graphics card added: {item.Name}"); break;
                    case Disk: Add(ChangeKind.Hardware, item, $"Drive added: {item.Name}"); break;
                }
                continue;
            }
            if (string.Equals(old.Value, item.Value, StringComparison.OrdinalIgnoreCase)) continue;
            switch (kind)
            {
                case App: Add(ChangeKind.AppUpdated, item, item.Value.Length > 0 ? $"{item.Name} updated to {item.Value}" : $"{item.Name} updated", old.Value, item.Value); break;
                case Startup: Add(ChangeKind.Startup, item, $"{item.Name} turned {item.Value} at startup"); break;
                case GpuDriver: Add(ChangeKind.Driver, item, $"{item.Name} {item.Value}", old.Value, item.Value); break;
                case Cpu: Add(ChangeKind.Hardware, item, $"Processor changed to {item.Value}", old.Value, item.Value); break;
                case Ram: Add(ChangeKind.Hardware, item, $"Memory changed to {item.Value}", old.Value, item.Value); break;
                case Board: Add(ChangeKind.Hardware, item, $"Motherboard changed to {item.Value}", old.Value, item.Value); break;
                case Bios: Add(ChangeKind.Firmware, item, $"BIOS updated to {item.Value}", old.Value, item.Value); break;
                case Windows: Add(ChangeKind.Windows, item, $"{item.Name} updated to {item.Value}", old.Value, item.Value); break;
                case Setting when item.Key == PowerPlan: Add(ChangeKind.Setting, item, $"{item.Name} changed to {item.Value}", old.Value, item.Value); break;
                case Setting: Add(ChangeKind.Setting, item, $"{item.Name} turned {item.Value}"); break;
            }
        }
        if (IsList(kind))
            foreach (var old in was.Values.Where(o => !now.ContainsKey(o.Key)))
            {
                switch (kind)
                {
                    case App: Add(ChangeKind.AppRemoved, old, $"{old.Name} removed", old.Value); break;
                    case Startup when old.Value == On: Add(ChangeKind.Startup, old, $"{old.Name} no longer starts with Windows"); break;
                    case Gpu: Add(ChangeKind.Hardware, old, $"Graphics card removed: {old.Name}"); break;
                    case Disk: Add(ChangeKind.Hardware, old, $"Drive removed: {old.Name}"); break;
                }
            }
        return changes;

        static string? Text(string? s) => string.IsNullOrEmpty(s) ? null : s;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long kilobytes);

    // "26.01", "v1.7.3", "version 6.7.3": a version written into a name.
    [GeneratedRegex(@"(?:\bversion\s+|\bv|(?<![\w.]))(\d+(?:\.\d+)+[a-z]?)(?![\w.])", RegexOptions.IgnoreCase)]
    private static partial Regex VersionInName();

    // "CometUpdaterTaskUser145.2.7632.4583", "GoogleChromeAutoLaunch_7E36FFB55DA820F16E11CF1EFF742541".
    [GeneratedRegex(@"\d+(\.\d+)+|_[0-9A-Fa-f]{16,}$")]
    private static partial Regex StartupNoise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
