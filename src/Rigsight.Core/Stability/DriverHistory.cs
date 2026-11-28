namespace Rigsight.Core.Stability;

/// <summary>The kind of device a driver is for, as the every-driver list groups them (in this order).</summary>
public enum DriverKind { Graphics, Sound, Network, Bluetooth, InputUsb, Storage, Chipset, Monitors, Other }

/// <summary>What a step in a driver's history was.</summary>
public enum DriverStepKind { Changed, FirstSeen, Removed }

/// <summary>A version a device had: from <see cref="Since"/> to <see cref="Until"/> (null: still on it).</summary>
/// <param name="Older">Lower than the version before it: the driver went back.</param>
public sealed record DriverVersion(string Version, string Shown, DateTime Since, DateTime? Until, DriverStart How, bool Older, DateTime? Date);

/// <summary>A change in a device's driver: a version after another, a device first seen, or one gone for 30 days.</summary>
/// <param name="FromRaw">The version before as Windows has it (for matching Windows' log); <paramref name="From"/> as shown.</param>
public sealed record DriverStep(DriverDevice Device, DateTime At, DriverStepKind Kind, string? From, string To, string? FromRaw, string ToRaw, bool Older);

/// <summary>
/// One device and every version of its driver on record, newest first. Followed by its hardware ID (see
/// <see cref="DriverRecord.KeyOf"/>), so a device unplugged and plugged back is the same one.
/// </summary>
public sealed class DriverDevice
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Maker { get; init; }
    public required string ClassGuid { get; init; }
    public DriverKind Kind => DriverHistory.KindOf(ClassGuid);
    /// <summary>A driver that comes with Windows: listed apart, at the end.</summary>
    public bool Inbox { get; init; }
    /// <summary>Newest first; never empty.</summary>
    public required IReadOnlyList<DriverVersion> Versions { get; init; }
    /// <summary>The last time a look saw it (or Windows' log configured it).</summary>
    public DateTime Seen { get; init; }
    /// <summary>Not seen for <see cref="DriverHistory.GoneAfter"/>: no longer on this PC.</summary>
    public bool Gone { get; init; }

    public DriverVersion Current => Versions[0];
    /// <summary>A later look found the device new (it wasn't there at the first look, and the log had nothing of it).</summary>
    public bool FirstSeen => Versions[^1].How == DriverStart.Found;
    /// <summary>The earliest time on record for it.</summary>
    public DateTime RecordStart => Versions[^1].Since;

    /// <summary>A graphics card's own driver (the inventory follows its version as the graphics driver, see <see cref="Inventory"/>).</summary>
    public bool IsGraphicsCard => Kind == DriverKind.Graphics && !Inbox && Key.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Every device's driver and each version it has had (1.22.0, "Drivers inside Timeline", approved mock-up v8): from
/// RigSight's daily look at every device's driver, and Windows' log of driver installs for the weeks before the first
/// look. The one source for the Timeline's driver lines, its This PC now line, the every-driver list and one driver's view.
/// </summary>
public sealed class DriverHistory
{
    /// <summary>A device not seen for this long is no longer on this PC.</summary>
    public static readonly TimeSpan GoneAfter = TimeSpan.FromDays(30);

    /// <summary>What counts as recent in the list's filter and This PC now.</summary>
    public const int RecentDays = 90;

    public IReadOnlyList<DriverDevice> Devices { get; }
    /// <summary>Every change, oldest first.</summary>
    public IReadOnlyList<DriverStep> Steps { get; }
    public DateTime Now { get; }

    private DriverHistory(List<DriverDevice> devices, List<DriverStep> steps, DateTime now) => (Devices, Steps, Now) = (devices, steps, now);

    public static DriverHistory Empty(DateTime now) => new([], [], now);

    public bool HasRecord => Devices.Count > 0;

    /// <param name="changes">
    /// The Timeline's changes: a graphics card's versions are the inventory's graphics driver lines (followed every minute
    /// since 0.15.0, to the minute; Windows' log often has no entry for them), the look's reading only adding its date.
    /// </param>
    public static DriverHistory Build(IEnumerable<DriverRow> rows, DateTime now, IEnumerable<SystemChange>? changes = null)
    {
        var devices = new List<DriverDevice>();
        var steps = new List<DriverStep>();
        var groups = rows.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderBy(r => r.Since).ToList()).ToList();
        var graphics = (changes ?? []).Where(c => c.IsGraphicsDriver && c.Now is { Length: > 0 }).OrderBy(c => c.Time).ToList();
        var cards = groups.Where(g => IsCard(g[^1])).ToList();
        foreach (var g in groups)
        {
            var list = cards.Contains(g) ? WithInventory(g, [.. graphics.Where(c => cards.Count == 1 || CardOf(c, g[^1].Key))]) : g;
            var newest = list[^1];
            var versions = new List<DriverVersion>();
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                versions.Add(new DriverVersion(r.Version, Shown(r), r.Since, i + 1 < list.Count ? list[i + 1].Since : null, r.How, i > 0 && Older(list[i - 1], r), r.Date));
            }
            versions.Reverse();
            var seen = g.Concat(list).Max(r => r.Seen);
            var device = new DriverDevice
            {
                Key = newest.Key, Name = newest.Name, Maker = newest.Maker, ClassGuid = newest.ClassGuid, Inbox = newest.Inbox,
                Versions = versions, Seen = seen, Gone = now - seen > GoneAfter,
            };
            devices.Add(device);
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (i > 0 && r.How != DriverStart.Unknown)
                {
                    var before = list[i - 1];
                    steps.Add(new DriverStep(device, r.Since, DriverStepKind.Changed, Shown(before), Shown(r), before.Version, r.Version, Older(before, r)));
                }
                else if (i == 0 && r.How == DriverStart.Found)
                    steps.Add(new DriverStep(device, r.Since, DriverStepKind.FirstSeen, null, Shown(r), null, r.Version, false));
            }
            if (device.Gone) steps.Add(new DriverStep(device, seen, DriverStepKind.Removed, null, device.Current.Shown, null, device.Current.Version, false));
        }
        devices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        steps.Sort((a, b) => a.At.CompareTo(b.At));
        return new DriverHistory(devices, steps, now);

        static string Shown(DriverRow r) => r.Shown.Length > 0 ? r.Shown : r.Version;
    }

    /// <summary>
    /// A version lower than the one before it, as its maker numbers them. Not when the device moved between Windows' own
    /// driver and a maker's (Windows' generic 10.0.26100.1, then the maker's 1.0.3.56670): those are two different drivers.
    /// </summary>
    internal static bool Older(DriverRow before, DriverRow after) =>
        before.Inbox == after.Inbox && Compare(after.Shown.Length > 0 ? after.Shown : after.Version, before.Shown.Length > 0 ? before.Shown : before.Version) < 0;

    private static bool IsCard(DriverRow r) => !r.Inbox && KindOf(r.ClassGuid) == DriverKind.Graphics && r.Key.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase);

    /// <summary>An inventory line of this card ("gpudriver:pci\ven_10de&amp;dev_2208", the device Windows matched its driver to).</summary>
    private static bool CardOf(SystemChange c, string key)
    {
        string subject = c.Subject.StartsWith(Inventory.ExactPrefix, StringComparison.Ordinal) ? c.Subject[Inventory.ExactPrefix.Length..] : c.Subject;
        if (!subject.StartsWith(Inventory.GpuDriver + ":", StringComparison.Ordinal)) return false;
        string device = subject[(Inventory.GpuDriver.Length + 1)..].Split('#')[0];
        return device.Length > 0 && key.StartsWith(device, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A graphics card's versions from the inventory's lines: the one before the first line (its start not known), each
    /// line's version from its time, and the look's own reading of the version now for its date (newer than the last line,
    /// it's a version of its own).
    /// </summary>
    private static List<DriverRow> WithInventory(List<DriverRow> rows, List<SystemChange> changes)
    {
        if (changes.Count == 0) return rows;
        var cur = rows[^1];
        var list = new List<DriverRow>();
        var first = changes[0];
        if (first.Was is { Length: > 0 } was)
            list.Add(cur with { Since = first.Time.AddSeconds(-1), Version = was, Shown = was, How = DriverStart.Unknown, Date = null, Seen = first.Time });
        foreach (var c in changes)
            if (list.Count == 0 || !list[^1].Shown.Equals(c.Now, StringComparison.OrdinalIgnoreCase))
                list.Add(cur with { Since = c.Time, Version = c.Now!, Shown = c.Now!, How = DriverStart.Logged, Date = null, Seen = c.Time });
        if (list[^1].Shown.Equals(cur.Shown, StringComparison.OrdinalIgnoreCase)) list[^1] = list[^1] with { Date = cur.Date, Seen = cur.Seen };
        else if (cur.Since > list[^1].Since) list.Add(cur);
        return list;
    }

    public DriverDevice? Find(string key) => Devices.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The devices with their maker's driver that are still here (This PC now's "Drivers · 30").</summary>
    public IEnumerable<DriverDevice> FromMakers => Devices.Where(d => !d.Inbox && !d.Gone);

    /// <summary>The drivers that come with Windows, still here.</summary>
    public IEnumerable<DriverDevice> FromWindows => Devices.Where(d => d.Inbox && !d.Gone);

    /// <summary>Had a step in the last <see cref="RecentDays"/> days (changed, first seen or gone).</summary>
    public bool ChangedRecently(DriverDevice d) => Steps.Any(s => s.Device == d && s.At >= Now.AddDays(-RecentDays));

    /// <summary>The graphics card's own driver device (the first present one).</summary>
    public DriverDevice? GraphicsCard => Devices.Where(d => d.IsGraphicsCard && !d.Gone).OrderByDescending(d => d.Seen).FirstOrDefault();

    /// <summary>
    /// The groups of the every-driver list, in their order: each kind of device with its maker's drivers, then the ones no
    /// longer on this PC. Windows' own drivers aren't in them (see <see cref="FromWindows"/>).
    /// </summary>
    public List<(DriverKind? Kind, List<DriverDevice> Devices)> Groups(Func<DriverDevice, bool>? keep = null)
    {
        var mine = Devices.Where(d => !d.Inbox && (keep?.Invoke(d) ?? true)).ToList();
        var groups = new List<(DriverKind? Kind, List<DriverDevice> Devices)>();
        foreach (var kind in Enum.GetValues<DriverKind>())
            if (mine.Where(d => !d.Gone && d.Kind == kind).ToList() is { Count: > 0 } of) groups.Add((kind, of));
        if (mine.Where(d => d.Gone).OrderByDescending(d => d.Seen).ToList() is { Count: > 0 } gone) groups.Add((null, gone));
        return groups;
    }

    // ── Kinds ──────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, DriverKind> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{4d36e968-e325-11ce-bfc1-08002be10318}"] = DriverKind.Graphics,  // Display
        ["{4d36e96c-e325-11ce-bfc1-08002be10318}"] = DriverKind.Sound,     // MEDIA
        ["{c166523c-fe0c-4a94-a586-f1a80cfbbf3e}"] = DriverKind.Sound,     // AudioEndpoint
        ["{5989fce8-9cd0-467d-8a6a-5419e31529d4}"] = DriverKind.Sound,     // AudioProcessingObject
        ["{4d36e972-e325-11ce-bfc1-08002be10318}"] = DriverKind.Network,   // Net
        ["{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}"] = DriverKind.Bluetooth,
        ["{36fc9e60-c465-11cf-8056-444553540000}"] = DriverKind.InputUsb,  // USB controllers and hubs
        ["{88bae032-5a81-49f0-bc3d-a4ff138216d6}"] = DriverKind.InputUsb,  // USBDevice
        ["{745a17a0-74d3-11d0-b6fe-00a0c90f57da}"] = DriverKind.InputUsb,  // HIDClass
        ["{4d36e96b-e325-11ce-bfc1-08002be10318}"] = DriverKind.InputUsb,  // Keyboard
        ["{4d36e96f-e325-11ce-bfc1-08002be10318}"] = DriverKind.InputUsb,  // Mouse
        ["{d61ca365-5af4-4486-998b-9db4734c6ca3}"] = DriverKind.InputUsb,  // XnaComposite (game controllers)
        ["{4d36e967-e325-11ce-bfc1-08002be10318}"] = DriverKind.Storage,   // DiskDrive
        ["{4d36e97b-e325-11ce-bfc1-08002be10318}"] = DriverKind.Storage,   // SCSIAdapter
        ["{4d36e96a-e325-11ce-bfc1-08002be10318}"] = DriverKind.Storage,   // hdc
        ["{4d36e965-e325-11ce-bfc1-08002be10318}"] = DriverKind.Storage,   // CDROM
        ["{71a27cdd-812a-11d0-bec7-08002be2092f}"] = DriverKind.Storage,   // Volume
        ["{4d36e97d-e325-11ce-bfc1-08002be10318}"] = DriverKind.Chipset,   // System
        ["{50127dc3-0f36-415e-a6cc-4cb3be910b65}"] = DriverKind.Chipset,   // Processor
        ["{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}"] = DriverKind.Chipset,   // Firmware
        ["{d94ee5d8-d189-4994-83d2-f68d7d41b0e6}"] = DriverKind.Chipset,   // SecurityDevices (TPM)
        ["{4d36e96e-e325-11ce-bfc1-08002be10318}"] = DriverKind.Monitors,  // Monitor
    };

    public static DriverKind KindOf(string classGuid)
    {
        string g = (classGuid ?? "").Trim();
        if (g.Length > 0 && g[0] != '{') g = "{" + g + "}";
        return Classes.GetValueOrDefault(g, DriverKind.Other);
    }

    // ── Versions ───────────────────────────────────────────────────────────

    /// <summary>Two versions part by part as numbers ("10.0.22027.2" is lower than "10.0.22028.0"); text where a part isn't one.</summary>
    public static int Compare(string a, string b)
    {
        var x = (a ?? "").Split('.');
        var y = (b ?? "").Split('.');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            string p = i < x.Length ? x[i] : "0", q = i < y.Length ? y[i] : "0";
            int c = long.TryParse(p, out long m) && long.TryParse(q, out long n) ? m.CompareTo(n) : string.Compare(p, q, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return 0;
    }

    // ── The Timeline's driver lines ────────────────────────────────────────

    /// <summary>How far from a line of Windows' log a step of the history may be and still be that line.</summary>
    private static readonly TimeSpan SameInstall = TimeSpan.FromHours(24);

    /// <summary>The subject of a driver line made from the history alone (never stored).</summary>
    public const string DevicePrefix = "device:";

    /// <summary>
    /// The Timeline's changes with the history's driver lines in them: each driver line already recorded (a graphics
    /// driver from the inventory, a driver install from Windows' log) gets its device and its from and to; each step of a
    /// maker's driver no recorded line stands for (a Microsoft driver from Windows Update, a device first seen or gone) is
    /// added as a line of its own. Windows' own drivers change with every Windows update and are left out; so is a
    /// graphics card's own step, which the inventory's line already is. Oldest first.
    /// </summary>
    public (List<SystemChange> Changes, Dictionary<SystemChange, DriverChangeLine> Lines) WithChanges(IReadOnlyList<SystemChange> changes)
    {
        var lines = new Dictionary<SystemChange, DriverChangeLine>(ReferenceEqualityComparer.Instance);
        var used = new HashSet<DriverStep>(ReferenceEqualityComparer.Instance);
        var card = GraphicsCard;
        foreach (var c in changes.Where(c => c.Kind == ChangeKind.Driver))
        {
            if (c.IsGraphicsDriver)
            {
                var step = Steps.Where(s => s.Device.IsGraphicsCard && !used.Contains(s) && s.Kind == DriverStepKind.Changed && c.Now is { } now
                                            && s.To.Equals(now, StringComparison.OrdinalIgnoreCase) && (s.At - c.Time).Duration() <= SameInstall)
                    .MinBy(s => (s.At - c.Time).Duration());
                if (step is not null) used.Add(step);
                var device = step?.Device ?? Devices.Where(d => d.IsGraphicsCard && c.Now is { } v && d.Versions.Any(x => x.Shown.Equals(v, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(d => d.Gone).FirstOrDefault() ?? card;
                string name = c.Now is { Length: > 0 } to && c.Title.EndsWith(" " + to, StringComparison.Ordinal) ? c.Title[..^(to.Length + 1)] : c.Shown;
                lines[c] = new DriverChangeLine(name, c.Was, c.Now ?? "", device?.Key, DriverStepKind.Changed, device?.Maker ?? name);
                continue;
            }
            if (c.Now is not { Length: > 0 } version) continue;
            var match = Steps.Where(s => !used.Contains(s) && !s.Device.Inbox && s.Kind != DriverStepKind.Removed && s.ToRaw.Equals(version, StringComparison.OrdinalIgnoreCase)
                                         && (s.At - c.Time).Duration() <= SameInstall)
                .MinBy(s => (s.At - c.Time).Duration());
            // A driver installed before the history starts, or on a device it doesn't follow: its own words, as before.
            if (match is null)
            {
                var first = Devices.Where(d => !d.Inbox && d.Versions[^1].Version.Equals(version, StringComparison.OrdinalIgnoreCase)
                                               && d.Versions[^1].How == DriverStart.Logged && (d.Versions[^1].Since - c.Time).Duration() <= SameInstall)
                    .MinBy(d => (d.Versions[^1].Since - c.Time).Duration());
                if (first is not null) lines[c] = new DriverChangeLine(first.Name, null, first.Versions[^1].Shown, first.Key, DriverStepKind.Changed, first.Maker);
                continue;
            }
            used.Add(match);
            lines[c] = DriverChangeLine.Of(match);
        }
        var all = new List<SystemChange>(changes);
        foreach (var s in Steps.Where(s => !used.Contains(s) && !s.Device.Inbox && !(s.Device.IsGraphicsCard && s.Kind == DriverStepKind.Changed)))
        {
            var line = DriverChangeLine.Of(s);
            var change = new SystemChange(s.At, ChangeKind.Driver, line.English) { Subject = DevicePrefix + s.Device.Key, Was = s.From, Now = s.To };
            lines[change] = line;
            all.Add(change);
        }
        return ([.. all.OrderBy(c => c.Time)], lines);
    }

    // ── Words ──────────────────────────────────────────────────────────────

    /// <summary>A date as the lists say it: "13 Nov", with the year when it isn't this one.</summary>
    public static string Day(DateTime d, DateTime now) => d.ToString(d.Year == now.Year ? "d MMM" : "d MMM yyyy");

    /// <summary>The name of a kind as the list's heading says it (in the language in use).</summary>
    public static string KindName(DriverKind? kind) => kind switch
    {
        DriverKind.Graphics => Lang.T("Graphics"),
        DriverKind.Sound => Lang.T("Sound"),
        DriverKind.Network => Lang.T("Network"),
        DriverKind.Bluetooth => Lang.T("Bluetooth"),
        DriverKind.InputUsb => Lang.T("Input and USB"),
        DriverKind.Storage => Lang.T("Storage"),
        DriverKind.Chipset => Lang.T("Chipset and board"),
        DriverKind.Monitors => Lang.T("Monitors"),
        DriverKind.Other => Lang.T("Other"),
        _ => Lang.T("No longer on this PC"),
    };

    /// <summary>The kind's icon (Segoe Fluent) and its palette colour, never green, amber or red (they say how a reading is doing).</summary>
    public static (string Glyph, string Brush) KindLook(DriverKind? kind) => kind switch
    {
        DriverKind.Graphics => ("", "GpuBrush"),
        DriverKind.Sound => ("", "PinkBrush"),
        DriverKind.Network => ("", "SkyBrush"),
        DriverKind.Bluetooth => ("", "CpuBrush"),
        DriverKind.InputUsb => ("", "PurpleBrush"),
        DriverKind.Storage => ("", "SkyBrush"),
        DriverKind.Chipset => ("", "CpuBrush"),
        DriverKind.Monitors => ("", "PurpleBrush"),
        DriverKind.Other => ("", "MutedBrush"),
        _ => ("", "FaintBrush"),
    };
}

/// <summary>
/// A driver line on the Timeline: the device (a graphics card's line keeps the inventory's "NVIDIA graphics driver"), the
/// version before and after, and the device it opens (null: none followed).
/// </summary>
public sealed record DriverChangeLine(string Name, string? From, string To, string? DeviceKey, DriverStepKind Kind, string Maker)
{
    public static DriverChangeLine Of(DriverStep s) => new(s.Device.Name, s.From, s.To, s.Device.Key, s.Kind, s.Device.Maker);

    /// <summary>The title without the versions, in the language in use ("WireGuard Tunnel removed").</summary>
    public string Title => Kind switch
    {
        DriverStepKind.Removed => Lang.T($"{Name} removed"),
        DriverStepKind.FirstSeen => Lang.T($"{Name} first seen"),
        _ => Name,
    };

    /// <summary>The version before, shown muted before the arrow (none for a device first seen or gone).</summary>
    public string? Was => Kind == DriverStepKind.Changed ? From : null;

    /// <summary>The version it went to, shown bold after the arrow (a device first seen: its version, small; gone: nothing).</summary>
    public string? Version => Kind == DriverStepKind.Removed ? null : To;

    /// <summary>The whole line as one text, in the language in use (for a month's folded list, the calendar and search).</summary>
    public string Text => Kind switch
    {
        DriverStepKind.Removed => Title,
        DriverStepKind.FirstSeen => $"{Title} {To}",
        _ => From is { Length: > 0 } from ? $"{Name} {from} → {To}" : To.Length > 0 ? $"{Name} {To}" : Name,
    };

    /// <summary>The line in English, for a line made from the history alone (searching finds it either way).</summary>
    public string English => Kind switch
    {
        DriverStepKind.Removed => $"{Name} removed",
        DriverStepKind.FirstSeen => $"{Name} first seen {To}",
        _ => From is { Length: > 0 } from ? $"{Name} {from} → {To}" : $"{Name} {To}",
    };
}

/// <summary>
/// The square that stands for a driver's maker: its brand colour with its letter (approved mock-up, round 2: "brand-colour
/// maker squares"), or a quiet grey square for a maker not on the list. Colours are the makers' own, like an app's icon,
/// so they stay whatever the theme or accent.
/// </summary>
public static class DriverMakers
{
    private static readonly (string Match, string Back, string Letter, bool DarkInk)[] Known =
    [
        ("NVIDIA", "#76B900", "N", false), ("Microsoft", "#0078D4", "M", false), ("Realtek", "#1E5AA8", "R", false),
        ("Intel", "#0071C5", "i", false), ("AMD", "#ED1C24", "A", false), ("Advanced Micro", "#ED1C24", "A", false),
        ("TP-Link", "#4ACBD6", "T", true), ("Logitech", "#00B8FC", "L", true), ("Xbox", "#107C10", "X", false),
        ("LG", "#A50034", "LG", false), ("Qualcomm", "#3253DC", "Q", false), ("Samsung", "#1428A0", "S", false),
        ("Razer", "#44D62C", "R", true), ("Broadcom", "#CC092F", "B", false), ("MediaTek", "#EC9430", "M", true),
        ("Elgato", "#1D1D1D", "E", false), ("Corsair", "#1D1D1D", "C", false), ("ASUS", "#00539B", "A", false),
    ];

    /// <summary>The square's background (a colour or a palette brush's name), its letter and the letter's colour.</summary>
    public static (string Back, string Letter, string Ink) Of(string maker)
    {
        string m = (maker ?? "").Trim();
        foreach (var k in Known)
            if (m.Equals(k.Match, StringComparison.OrdinalIgnoreCase) || m.StartsWith(k.Match + " ", StringComparison.OrdinalIgnoreCase) || m.StartsWith(k.Match + ",", StringComparison.OrdinalIgnoreCase))
                return (k.Back, k.Letter, k.DarkInk ? "#000000" : "#FFFFFF");
        char first = m.FirstOrDefault(char.IsLetterOrDigit);
        string letter = first == default ? "?" : char.ToUpperInvariant(first).ToString();
        return ("HoverBrush", letter, "TextBrush");
    }
}
