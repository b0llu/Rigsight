using System.Text.RegularExpressions;

namespace Rigsight.Core.Ai;

/// <summary>An app an answer may open: by its Start menu name when it's on this PC, else its maker's download page.</summary>
/// <param name="Id">The id the AI writes ("[do: app nvidia-app]"), the same in the relay's list (rigsight-relay src/apps.ts).</param>
/// <param name="StartName">Its name on the Start menu, matched whole (never "Uninstall …" or "Reset settings" beside it).</param>
/// <param name="Download">Its maker's own download page (https only), or null when there is none to offer.</param>
public sealed record KnownApp(string Id, string Name, Regex StartName, string? Download);

/// <summary>An app on the Start menu: its name and the id Windows opens it by (shell:AppsFolder\&lt;id&gt;).</summary>
public sealed record StartApp(string Name, string AppId);

/// <summary>
/// The apps an answer may open (the person's case, 2026-10-24: "check that in the NVIDIA App", with it installed and no
/// button). A fixed list, never whatever name the AI writes: the Start menu also holds "Uninstall CPU-Z" and Cheat
/// Engine's "Reset settings". An app is opened by the id Windows gives it on the Start menu, which works for installed
/// programs and Store apps alike, and nothing is installed or changed.
/// </summary>
public static class OpenableApps
{
    private static Regex Whole(string pattern) => new($"^(?:{pattern})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static readonly IReadOnlyList<KnownApp> All =
    [
        new("nvidia-app", "NVIDIA App", Whole("NVIDIA App"), "https://www.nvidia.com/en-us/software/nvidia-app/"),
        new("amd-software", "AMD Software", Whole(@"AMD Software(: Adrenalin Edition)?|AMD Radeon Software"), "https://www.amd.com/en/support/download/drivers.html"),
        new("intel-graphics", "Intel Graphics Software", Whole(@"Intel(®|\(R\))? Graphics Software|Intel(®|\(R\))? Arc(™)? Control|Intel(®|\(R\))? Graphics Command Center"), null),
        new("steam", "Steam", Whole("Steam"), "https://store.steampowered.com/about/"),
        new("epic", "Epic Games Launcher", Whole("Epic Games Launcher"), "https://store.epicgames.com/download"),
        new("battlenet", "Battle.net", Whole(@"Battle\.net"), "https://download.battle.net/"),
        new("ea-app", "EA app", Whole("EA|EA app"), "https://www.ea.com/ea-app"),
        new("ubisoft-connect", "Ubisoft Connect", Whole("Ubisoft Connect"), "https://ubisoftconnect.com/"),
        new("xbox", "Xbox app", Whole("Xbox"), null),
        new("msi-afterburner", "MSI Afterburner", Whole("MSI Afterburner"), "https://www.msi.com/Landing/afterburner/graphics-cards"),
        new("msi-center", "MSI Center", Whole("MSI Center"), null),
        new("armoury-crate", "Armoury Crate", Whole("Armoury Crate"), null),
        new("icue", "iCUE", Whole(@"(Corsair )?iCUE( \d+)?"), null),
        new("g-hub", "Logitech G HUB", Whole("Logitech G HUB"), "https://www.logitechg.com/innovation/g-hub.html"),
        new("razer-synapse", "Razer Synapse", Whole(@"Razer Synapse( \d)?"), null),
        new("samsung-magician", "Samsung Magician", Whole("Samsung Magician"), "https://semiconductor.samsung.com/consumer-storage/magician/"),
        new("hwinfo", "HWiNFO", Whole(@"HWiNFO(64|32)?"), null),
        new("cpu-z", "CPU-Z", Whole("CPU-Z"), null),
    ];

    public static KnownApp? Get(string id) => All.FirstOrDefault(a => a.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Any other PC tool an answer names ("[do: app MSI Center]"), found by its name on this PC's Start menu (the person's
    /// call, 2026-10-25: the list stays for its download links, any other tool is matched by name). Nothing is sent for
    /// it: the AI names the tool, the Start menu says whether it's here. Never an uninstaller, a reset, a set-up, a help
    /// or web page, a settings file, a Windows tool that could change the PC (a command line, the registry), or anything
    /// in the Windows folder; a name matching two entries opens neither.
    /// </summary>
    public static StartApp? MatchByName(string name, IEnumerable<StartApp> start)
    {
        string wanted = Normal(name);
        if (wanted.Length < 3) return null;
        var safe = start.Where(Allowed).ToList();
        var exact = safe.Where(s => Normal(s.Name) == wanted).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        // "MSI Center" for "MSI Center Pro": the name's words, whole, at its start; only when one entry has them.
        var starts = safe.Where(s => (Normal(s.Name) + " ").StartsWith(wanted + " ", StringComparison.Ordinal)).ToList();
        return starts.Count == 1 ? starts[0] : null;
    }

    /// <summary>
    /// The other PC tools on this PC (the person's call, 2026-10-25: "they should be dynamic"): Start menu entries from
    /// hardware and PC-tool makers that aren't on the list, so the AI knows they're here and can offer them by name. Only
    /// these leave the PC, never the rest of the Start menu, and the same safety rules as a name match apply.
    /// </summary>
    public static IReadOnlyList<StartApp> OtherTools(IEnumerable<StartApp> start)
    {
        var list = start.ToList();
        var known = Installed(list).Values.Select(v => v.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. list.Where(s => !known.Contains(s.AppId) && Allowed(s) && PcTool.IsMatch(s.Name))
            .GroupBy(s => Normal(s.Name)).Where(g => g.Count() == 1).Select(g => g.First()).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Take(25)];
    }

    /// <summary>The makers and tools a PC tool's name starts with or holds: graphics, boards, cases, peripherals, drives, monitoring.</summary>
    private static readonly Regex PcTool = new(@"\b(nvidia|geforce|amd|radeon|ryzen|intel|msi|asus|rog|armoury|gigabyte|aorus|asrock|corsair|icue|razer|logitech|steelseries|hyperx|nzxt|lian li|thermaltake|cooler master|be quiet|noctua|samsung magician|crucial|western digital|wd dashboard|seagate|kingston|hwinfo|hwmonitor|cpuid|cpu-z|gpu-z|afterburner|rivatuner|occt|fan control|signalrgb|openrgb|elgato|wallpaper engine|displayfusion|dell|alienware|lenovo vantage|legion|omen|predator|nitrosense|acer)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Normal(string s) => Regex.Replace(s.Trim().ToLowerInvariant().Replace("®", "").Replace("™", ""), @"\s+", " ");

    private static readonly Regex NotATool = new(@"\b(uninstall|uninstaller|remove|reset|repair|setup|set up|install(er)?|update(r)?|readme|read me|help|manual|documentation|license|licence|website|support|release notes|changelog|config|settings file|safe mode|recovery|debug)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NotThisFile = new(@"\.(ini|txt|url|html?|pdf|chm|log|bat|cmd|ps1|reg|vbs|msi)$|^https?:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> WindowsTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Command Prompt", "Windows PowerShell", "PowerShell", "Windows Terminal", "Terminal", "Registry Editor", "Run", "Control Panel",
        "Computer Management", "Disk Management", "Local Security Policy", "Group Policy Editor", "Services", "System Configuration", "Task Scheduler",
    };

    private static bool Allowed(StartApp s)
    {
        if (NotATool.IsMatch(s.Name) || NotThisFile.IsMatch(s.AppId) || WindowsTools.Contains(s.Name.Trim())) return false;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // A path into the Windows folder ({1AC14E77-…} is System32's known-folder id in an app id).
        return !(s.AppId.StartsWith(windows, StringComparison.OrdinalIgnoreCase) || s.AppId.StartsWith("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", StringComparison.OrdinalIgnoreCase)
            || s.AppId.StartsWith("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Which known apps are on this PC, each with the Start menu entry that opens it.</summary>
    public static IReadOnlyDictionary<string, StartApp> Installed(IEnumerable<StartApp> start)
    {
        var found = new Dictionary<string, StartApp>(StringComparer.OrdinalIgnoreCase);
        var list = start.ToList();
        foreach (var app in All)
            if (list.FirstOrDefault(s => app.StartName.IsMatch(s.Name.Trim())) is { } entry) found[app.Id] = entry;
        return found;
    }
}

/// <summary>
/// The Start menu's apps, as Windows lists them in its Apps folder (what Get-StartApps reads): read on a thread of its own
/// (the Shell wants one), kept a few minutes, and an empty list when Windows won't say.
/// </summary>
public static class StartApps
{
    private static readonly object Gate = new();
    private static (DateTime At, IReadOnlyList<StartApp> Apps)? _last;
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);

    /// <summary>Where the list comes from (the tests give their own).</summary>
    public static Func<IReadOnlyList<StartApp>> Source { get; set; } = ReadAppsFolder;

    public static IReadOnlyList<StartApp> List()
    {
        lock (Gate) if (_last is { } last && DateTime.Now - last.At < KeepFor) return last.Apps;
        var apps = Source();
        lock (Gate) _last = (DateTime.Now, apps);
        return apps;
    }

    /// <summary>Forgets the kept list (the tests, and after the person installs something).</summary>
    public static void Forget() { lock (Gate) _last = null; }

    private static IReadOnlyList<StartApp> ReadAppsFolder()
    {
        var apps = new List<StartApp>();
        var thread = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type is null) return;
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                if (folder is null) return;
                foreach (dynamic item in folder.Items())
                {
                    string? name = item.Name, path = item.Path;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path)) apps.Add(new StartApp(name!, path!));
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
            {
                Log.Write("ask", $"The Start menu's apps couldn't be read: {ex.Message}");
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(5))) { Log.Write("ask", "The Start menu's apps took over 5 s to read; none listed this time"); return []; }
        return apps;
    }
}
