using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml;
using Microsoft.Win32;
using Rigsight.Core.Data;

namespace Rigsight.Core.Startup;

/// <summary>
/// Reads each start of the PC from Windows' own logs (1.22.0). Every part comes from logs anyone can read, so every start
/// is measured the same way, with or without admin rights:
/// <list type="bullet">
/// <item>System log: Kernel-Boot 27 (the kind of start: 0 a full start or restart, 1 Fast Startup, 2 waking from
/// hibernation), Kernel-Boot 30 (the firmware's boot metrics: StartOSImageStart is the time before Windows, the same figure
/// as FwPOSTTime) and Kernel-General 12 (StartTime, when Windows itself started).</item>
/// <item>Winlogon/Operational: 811 with notification 0 (the sign-in screen is ready), 1 (authentication started: the person
/// is signing in) and 2 (authentication stopped, Win32Status 0 when it succeeded). Windows starting runs from Kernel-General
/// 12's StartTime to that 811; the time at the sign-in screen (811 to 1) is left out and kept only for the log.</item>
/// <item>Shell-Core/Operational: 9705/9706 (Windows starting and finishing the list of a Run or RunOnce key), 9707/9708 and
/// 62408/62409 (each app that starts with Windows, started and finished, "Command" and "PID"), 62171 (a logon task
/// finished, "AllLogonTasks" among them). Getting ready after sign-in runs from Winlogon 2 to the RunOnce list Windows
/// finishes after the Run lists (the last step of launching the apps that start with Windows).</item>
/// <item>Diagnostics-Performance/Operational, readable only with admin rights, where a PC still writes it: event 100 (its own
/// figures, used only for a start the sign-in logs no longer reach) and 101 to 110 (what Windows named as slower than
/// its usual).</item>
/// <item>FwPOSTTime in the registry: the firmware's time of this start only, for when event 30 isn't there.</item>
/// </list>
/// Shell-Core's log holds about a week (1 MB) and Winlogon's about a month, so each start is copied into the history a few
/// minutes after it.
/// </summary>
public static partial class StartupLogReader
{
    private const string SystemLog = "System";
    private const string Diagnostics = "Microsoft-Windows-Diagnostics-Performance/Operational";
    internal const string Winlogon = "Microsoft-Windows-Winlogon/Operational";
    internal const string ShellCore = "Microsoft-Windows-Shell-Core/Operational";
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    /// <summary>The longest a part of a start can be and still be believed (anything longer is a bad reading).</summary>
    private const int MostBiosMs = 10 * 60_000, MostWindowsMs = 60 * 60_000;

    /// <summary>Records of one start in the System log are written within seconds of each other; Windows' start-up record's own start time within minutes.</summary>
    private const double SameWithinSec = 30, RecordWithinSec = 180;

    /// <summary>
    /// How long after signing in (or after the start, before anyone signs in) a start's parts may still be to come: the apps
    /// that start with Windows are launched a minute or two after sign-in, and Windows' own record (where a PC writes it) once
    /// the PC has settled.
    /// </summary>
    public static readonly TimeSpan RecordComesWithin = TimeSpan.FromMinutes(45);

    /// <summary>A start as read, before it's kept: what's null wasn't in this read.</summary>
    /// <param name="KindKnown">The kind is from the System log (a start made from Windows' start-up record alone is taken as a full start).</param>
    /// <param name="MainMs">Event 100's MainPathBootTime (to the desktop, the sign-in screen included).</param>
    /// <param name="LogonWaitMs">Event 100's UserLogonWaitDuration.</param>
    /// <param name="PostMs">Event 100's BootPostBootTime.</param>
    public sealed record FoundStart(DateTime Time, StartKind Kind, bool KindKnown, int? BiosMs, int? MainMs, int? LogonWaitMs, int? PostMs, bool? AfterUpdate, bool Read)
    {
        public IReadOnlyList<SlowItem> Slow { get; init; } = [];

        /// <summary>Windows starting, from the sign-in logs: Kernel-General 12's StartTime to the sign-in screen being ready.</summary>
        public int? WindowsMs { get; init; }
        /// <summary>The time at the sign-in screen (left out of every part; for the log).</summary>
        public int? SignInWaitMs { get; init; }
        /// <summary>Getting ready after sign-in, from the sign-in logs: signing in to the apps that start with Windows launched.</summary>
        public int? ReadyMs { get; init; }
        /// <summary>The apps that start with Windows were all launched (the start can't get more from Shell-Core).</summary>
        public bool AppsDone { get; init; }
        /// <summary>When the person signed in (Winlogon 2).</summary>
        public DateTime? SignedIn { get; init; }
        /// <summary>Each app launched as it started.</summary>
        public IReadOnlyList<AppLaunch> Apps { get; init; } = [];
    }

    /// <summary>Windows' start-up record of one start (event 100).</summary>
    /// <param name="Start">BootStartTime, local.</param>
    /// <param name="Logged">When the record was written.</param>
    internal sealed record BootRecord(DateTime Start, DateTime Logged, int MainMs, int? LogonWaitMs, int PostMs, bool? AfterUpdate);

    /// <summary>A Winlogon entry: 811 for notification 0 (the sign-in screen is ready), 1 (authentication started), 2 (stopped; Ok when it succeeded).</summary>
    /// <param name="Pid">Winlogon's own process (one per session): a sign-in is the same process as its sign-in screen.</param>
    internal sealed record LogonEvent(DateTime At, int Id, int Pid, bool Ok = true);

    /// <summary>
    /// A Shell-Core entry about the apps that start with Windows: 9705/9706 a Run or RunOnce key's list started or finished
    /// (<paramref name="Key"/>), 9707/9708 and 62408/62409 one app started or finished (<paramref name="Command"/>), 62170/62171
    /// a logon task (<paramref name="Task"/>).
    /// </summary>
    /// <param name="Pid">The process that wrote it (Explorer for the apps).</param>
    /// <param name="Sid">The account it was written for.</param>
    internal sealed record ShellEvent(DateTime At, int Id, int Pid, string? Sid, string? Key = null, string? Command = null, string? Task = null);

    /// <summary>The sign-in logs, for joining: their entries and the oldest entry each still holds.</summary>
    internal sealed record SignInLogs(List<LogonEvent> Logons, List<ShellEvent> Shell, DateTime? WinlogonOldest, DateTime? ShellOldest);

    /// <summary>One start's parts from the sign-in logs.</summary>
    internal sealed record SignInParts(int? WindowsMs, int? WaitMs, int? ReadyMs, bool AppsDone, DateTime? SignedIn, List<AppLaunch> Apps);

    /// <summary>What one read found, for the log.</summary>
    public sealed class Found
    {
        public List<FoundStart> Starts { get; } = [];
        public int Boots, Firmware, Generals, Records, Slow, Screens, SignIns, Launches;
        /// <summary>Windows' start-up record could be read (admin rights).</summary>
        public bool DiagReadable;
        /// <summary>Why it couldn't (Windows' own words), where it said.</summary>
        public string? DiagWhy;
        /// <summary>The oldest entry in Windows' start-up record: starts before it can't be found there any more.</summary>
        public DateTime? DiagOldest;
        /// <summary>The oldest entry Winlogon's and Shell-Core's logs still hold, and Windows' words where it wouldn't give them.</summary>
        public DateTime? WinlogonOldest, ShellOldest;
        public string? WinlogonWhy, ShellWhy;
        /// <summary>This start's firmware time came from FwPOSTTime (no event 30 for it).</summary>
        public bool UsedFwPost;
        /// <summary>This start (since Windows last started), when the apps that start with Windows aren't all launched yet.</summary>
        public DateTime? ThisStartWaiting;
        /// <summary>This start, as read.</summary>
        public FoundStart? ThisStart;
    }

    /// <summary>Windows starting from event 100: its time to the desktop less the time spent at the sign-in screen (kept apart, both as Windows gave them).</summary>
    public static int? Windows(int? mainMs, int? logonWaitMs) =>
        mainMs is not int main ? null : logonWaitMs is int wait && wait > 0 && wait < main ? main - wait : main;

    // ── Reading ──

    /// <summary>
    /// Every start in Windows' logs since <paramref name="since"/> (the System log and the sign-in logs; Windows' start-up
    /// record is read whole, it's small), joined into one entry each.
    /// </summary>
    public static Found Read(DateTime since, DateTime now)
    {
        var found = new Found();
        var boots = new List<(DateTime At, StartKind Kind)>();
        var firmware = new List<(DateTime At, int Ms)>();
        var generals = new List<DateTime>();
        foreach (var e in Query(SystemLog, "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and (EventID=27 or EventID=30)]]", since, null))
        {
            var fields = Fields(e.Xml);
            if (e.Id == 27 && Kind(fields) is { } kind) boots.Add((e.At, kind));
            else if (e.Id == 30 && Firmware(fields) is int ms) firmware.Add((e.At, ms));
        }
        foreach (var e in Query(SystemLog, "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12]]", since, null))
            generals.Add(StartTime(Fields(e.Xml)) ?? e.At);
        (found.Boots, found.Firmware, found.Generals) = (boots.Count, firmware.Count, generals.Count);

        // The sign-in logs, from a little before the first start read (a start's sign-in comes after it).
        var signInSince = since > DateTime.MinValue.AddDays(1) ? since : (DateTime?)null;
        var logons = new List<LogonEvent>();
        foreach (var e in Query(Winlogon, "*[System[(EventID=811 or EventID=1 or EventID=2)]]", signInSince, w => found.WinlogonWhy = w))
            if (Logon(e.Id, Fields(e.Xml), e.At, e.Pid) is { } logon) logons.Add(logon);
        var shell = new List<ShellEvent>();
        foreach (var e in Query(ShellCore, "*[System[(EventID=9705 or EventID=9706 or EventID=9707 or EventID=9708 or EventID=62408 or EventID=62409 or EventID=62170 or EventID=62171)]]",
                     signInSince, w => found.ShellWhy = w))
            if (Shell(e.Id, Fields(e.Xml), e.At, e.Pid, e.Sid) is { } s) shell.Add(s);
        found.WinlogonOldest = Oldest(Winlogon);
        found.ShellOldest = Oldest(ShellCore);
        found.Screens = logons.Count(l => l.Id == 811);
        found.SignIns = logons.Count(l => l.Id == 2 && l.Ok);
        var logs = new SignInLogs(logons, shell, found.WinlogonOldest, found.ShellOldest);

        var records = new List<BootRecord>();
        var slow = new List<(DateTime Logged, SlowItem Item)>();
        string? why = null;
        foreach (var e in Query(Diagnostics, "*[System[(EventID>=100 and EventID<=110)]]", null, w => why = w))
        {
            found.DiagOldest = found.DiagOldest is { } o && o < e.At ? o : e.At;
            var fields = Fields(e.Xml);
            if (e.Id == 100) { if (Boot(fields, e.At) is { } b) records.Add(b); }
            else if (Slow(e.Id, fields) is { } item) slow.Add((e.At, item));
        }
        // Without admin rights Windows either refuses or answers with nothing at all: an empty log is read as not readable.
        found.DiagReadable = why is null && found.DiagOldest is not null;
        found.DiagWhy = why ?? (found.DiagOldest is null ? "no entries came back (it needs admin rights, or Windows keeps none)" : null);
        (found.Records, found.Slow) = (records.Count, slow.Count);

        // This start's firmware time from the registry, for when event 30 isn't there for it.
        var thisStart = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        int? fwPost = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PowerKey);
            if (key?.GetValue("FwPOSTTime") is int v) fwPost = v;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }

        found.Starts.AddRange(Join(boots, firmware, generals, records, slow, found.DiagReadable, found.DiagOldest, now, thisStart, fwPost, out bool usedFw, logs));
        found.UsedFwPost = usedFw;
        found.Launches = found.Starts.Sum(s => s.Apps.Count);
        var current = found.Starts.Where(s => Math.Abs((s.Time - thisStart).TotalMinutes) < 5).MaxBy(s => s.Time);
        found.ThisStart = current;
        if (current is not null && Waiting(current, now)) found.ThisStartWaiting = current.Time;
        return found;
    }

    /// <summary>A start whose apps that start with Windows aren't all launched yet, and still can be (to look again soon).</summary>
    internal static bool Waiting(FoundStart s, DateTime now) => !s.AppsDone && now - (s.SignedIn ?? s.Time) < RecordComesWithin;

    /// <summary>
    /// The records of each start joined into one entry: each Kernel-Boot 27 (or a Kernel-General 12 without one) is a start,
    /// with the firmware time written within half a minute of it, its parts from the sign-in logs (up to the next start), and
    /// Windows' start-up record whose BootStartTime is nearest to it, within three minutes (nearest pairs first: two starts
    /// can be a minute apart). A record with no start in the System log any more is a start of its own (a full start).
    /// </summary>
    internal static List<FoundStart> Join(List<(DateTime At, StartKind Kind)> boots, List<(DateTime At, int Ms)> firmware, List<DateTime> generals,
        List<BootRecord> records, List<(DateTime Logged, SlowItem Item)> slow, bool diagReadable, DateTime? diagOldest, DateTime now,
        DateTime thisStart, int? fwPost, out bool usedFwPost, SignInLogs? logs = null)
    {
        usedFwPost = false;
        var starts = boots.Select(b => (At: Second(b.At), b.Kind, Known: true)).ToList();
        foreach (var g in generals)
            if (!starts.Any(s => Math.Abs((s.At - g).TotalSeconds) < SameWithinSec)) starts.Add((Second(g), StartKind.Full, false));
        starts.Sort((a, b) => a.At.CompareTo(b.At));
        // Each record to the start nearest it, the nearest pairs first.
        var recordOf = new BootRecord?[starts.Count];
        var used = new HashSet<BootRecord>(ReferenceEqualityComparer.Instance);
        foreach (var (record, i, _) in records.SelectMany(r => starts.Select((s, i) => (Record: r, Index: i, Gap: Math.Abs((r.Start - s.At).TotalSeconds))))
                     .Where(p => p.Gap < RecordWithinSec).OrderBy(p => p.Gap))
        {
            if (recordOf[i] is not null || used.Contains(record)) continue;
            recordOf[i] = record;
            used.Add(record);
        }
        var list = new List<FoundStart>();
        for (int i = 0; i < starts.Count; i++)
        {
            var (at, kind, known) = starts[i];
            int? bios = firmware.Where(f => Math.Abs((f.At - at).TotalSeconds) < SameWithinSec).OrderBy(f => Math.Abs((f.At - at).Ticks)).Select(f => (int?)f.Ms).FirstOrDefault();
            if (bios is null && fwPost is int fw && Math.Abs((at - thisStart).TotalMinutes) < 5 && Plausible(fw, MostBiosMs)
                && !starts.Any(s => s.At > at && s.At <= thisStart.AddMinutes(5)))
            {
                bios = fw;
                usedFwPost = true;
            }
            // Windows' own start: Kernel-General 12's StartTime; a start without one (Fast Startup, waking from hibernation) from its Kernel-Boot 27.
            var os = generals.Where(g => Math.Abs((g - at).TotalSeconds) < SameWithinSec).OrderBy(g => Math.Abs((g - at).Ticks)).Select(g => (DateTime?)g).FirstOrDefault() ?? at;
            var until = i + 1 < starts.Count ? starts[i + 1].At : DateTime.MaxValue;
            var parts = logs is null ? null : Parts(os, until, logs, now);
            list.Add(Make(at, kind, known, bios, recordOf[i], parts));
        }
        foreach (var record in records.Where(r => !used.Contains(r))) list.Add(Make(Second(record.Start), StartKind.Full, false, null, record, null));
        return [.. list.OrderBy(s => s.Time)];

        FoundStart Make(DateTime at, StartKind kind, bool known, int? bios, BootRecord? record, SignInParts? parts)
        {
            // Looked for and not there: Windows didn't record it, once its record can't still be to come and the log reaches back to it.
            bool read = record is not null || (diagReadable && diagOldest is { } oldest && oldest <= at && now - at >= RecordComesWithin);
            // The sign-in logs reached back to it, and it can't get more from them.
            if (parts is not null && logs?.WinlogonOldest is { } wOld && wOld <= at && (parts.AppsDone || now - (parts.SignedIn ?? at) >= RecordComesWithin)) read = true;
            // What Windows named as slower is written with its start's record: each to the record written nearest it.
            var items = record is null ? [] : slow.Where(s => ReferenceEquals(NearestRecord(s.Logged), record)).Select(s => s.Item)
                .OrderByDescending(s => s.SlowerMs).ToList();
            return new FoundStart(at, kind, known, bios, record?.MainMs, record?.LogonWaitMs, record?.PostMs, record?.AfterUpdate, read)
            {
                Slow = items,
                WindowsMs = parts?.WindowsMs, SignInWaitMs = parts?.WaitMs, ReadyMs = parts?.ReadyMs, AppsDone = parts?.AppsDone ?? false,
                SignedIn = parts?.SignedIn, Apps = parts?.Apps ?? [],
            };
        }

        BootRecord? NearestRecord(DateTime logged) => records.Where(r => logged >= r.Logged.AddMinutes(-2) && logged <= r.Logged.AddMinutes(10))
            .OrderBy(r => Math.Abs((r.Logged - logged).Ticks)).FirstOrDefault();

        static DateTime Second(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, t.Kind);
    }

    /// <summary>
    /// One start's parts from the sign-in logs, between Windows' own start <paramref name="os"/> and the next start:
    /// <list type="bullet">
    /// <item>Windows starting: <paramref name="os"/> to the first Winlogon 811 with notification 0 (the sign-in screen is ready).</item>
    /// <item>At the sign-in screen (left out): that 811 to the first Winlogon 1 after it, from the same Winlogon.</item>
    /// <item>Getting ready after sign-in: the first successful Winlogon 2 after it to the moment the apps that start with
    /// Windows were launched: Explorer's RunOnce list finished after its Run lists (9706). Where no such list comes (an odd
    /// PC), once the start can't get more: the last list or app Windows finished, or else its logon tasks ("AllLogonTasks")
    /// finished.</item>
    /// <item>Each app: its 9707 (62408 for an app from the Store) to the 9708 (62409) of the same command, up to then.</item>
    /// </list>
    /// A log that no longer reaches back to the start gives nothing for it.
    /// </summary>
    internal static SignInParts Parts(DateTime os, DateTime until, SignInLogs logs, DateTime now)
    {
        var none = new SignInParts(null, null, null, false, null, []);
        if (logs.WinlogonOldest is not { } wOld || wOld > os) return none;
        var screen = logs.Logons.FirstOrDefault(e => e.Id == 811 && e.At >= os && e.At < until && (e.At - os).TotalMilliseconds <= MostWindowsMs);
        if (screen is null) return none;
        int? windows = Ms(screen.At - os) is var w && Plausible(w, MostWindowsMs) ? w : null;
        var started = logs.Logons.FirstOrDefault(e => e.Id == 1 && e.At >= screen.At && e.At < until && Same(e, screen));
        var signedIn = started is null ? null : logs.Logons.FirstOrDefault(e => e.Id == 2 && e.Ok && e.At >= started.At && e.At < until && Same(e, screen));
        int? wait = started is null ? null : Ms(started.At - screen.At);
        if (signedIn is null || logs.ShellOldest is not { } sOld || sOld > signedIn.At)
            return none with { WindowsMs = windows, WaitMs = wait, SignedIn = signedIn?.At };

        var end = until < signedIn.At + RecordComesWithin ? until : signedIn.At + RecordComesWithin;
        var window = logs.Shell.Where(e => e.At >= signedIn.At && e.At < end).ToList();
        // The account that signed in (another account signing in soon after keeps its own).
        string? sid = window.FirstOrDefault(e => e.Id == 62170 && e.Sid is not null)?.Sid ?? window.FirstOrDefault(e => e.Sid is not null)?.Sid;
        if (sid is not null) window = [.. window.Where(e => e.Sid is null || e.Sid == sid)];

        DateTime? done = null;
        var ranRun = new HashSet<int>();
        foreach (var e in window.Where(e => e.Id == 9706 && e.Key is not null))
        {
            if (IsRunOnce(e.Key!)) { if (ranRun.Contains(e.Pid)) { done = e.At; break; } }
            else if (IsRun(e.Key!)) ranRun.Add(e.Pid);
        }
        if (done is null && now >= end)
        {
            var last = window.Where(e => e.Id is 9706 or 9708 or 62409).Select(e => (DateTime?)e.At).Max();
            var tasks = window.FirstOrDefault(e => e.Id == 62171 && "AllLogonTasks".Equals(e.Task, StringComparison.OrdinalIgnoreCase))?.At;
            done = last is { } l && (tasks is not { } t || l > t) ? l : tasks;
        }

        var open = new Dictionary<(int, string), DateTime>();
        var apps = new List<AppLaunch>();
        foreach (var e in window)
        {
            if (done is { } d && e.At > d) break;
            if (e.Command is not { Length: > 0 } cmd) continue;
            if (e.Id is 9707 or 62408) open[(e.Pid, cmd)] = e.At;
            else if ((e.Id is 9708 or 62409) && open.Remove((e.Pid, cmd), out var at))
                apps.Add(new AppLaunch(StartupApps.DefaultName(cmd), StartupApps.ExeOfLogged(cmd), Ms(at - signedIn.At), Ms(e.At - at)) { Command = cmd });
        }
        int? ready = done is { } dn && Ms(dn - signedIn.At) is var r && r >= 0 && r <= MostWindowsMs ? r : null;
        return new SignInParts(windows, wait, ready, ready is not null, signedIn.At, apps);

        static bool Same(LogonEvent a, LogonEvent b) => a.Pid == 0 || b.Pid == 0 || a.Pid == b.Pid;
    }

    private static bool IsRunOnce(string key) => key.EndsWith(@"\RunOnce", StringComparison.OrdinalIgnoreCase);
    private static bool IsRun(string key) => key.EndsWith(@"\Run", StringComparison.OrdinalIgnoreCase);

    private static int Ms(TimeSpan t) => (int)Math.Round(Math.Clamp(t.TotalMilliseconds, int.MinValue, int.MaxValue));

    // ── One entry's fields ──

    /// <summary>An entry's named values (EventData's Data elements), from its XML.</summary>
    internal static Dictionary<string, string> Fields(string xml)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(xml)) return fields;
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            foreach (XmlNode node in doc.GetElementsByTagName("Data"))
                if (node.Attributes?["Name"]?.Value is { Length: > 0 } name) fields[name] = node.InnerText.Trim();
        }
        catch (XmlException) { }
        return fields;
    }

    /// <summary>Kernel-Boot 27's BootType: 0 a full start, 1 Fast Startup, 2 waking from hibernation; null for anything else.</summary>
    internal static StartKind? Kind(Dictionary<string, string> f) =>
        Int(f, "BootType") switch { 0 => StartKind.Full, 1 => StartKind.Fast, 2 => StartKind.Hibernate, _ => null };

    /// <summary>Kernel-Boot 30's StartOSImageStart: the firmware's time before Windows (ms); 0 or absurd is no reading.</summary>
    internal static int? Firmware(Dictionary<string, string> f) => Int(f, "StartOSImageStart") is int ms && Plausible(ms, MostBiosMs) ? ms : null;

    /// <summary>Kernel-General 12's StartTime, local.</summary>
    internal static DateTime? StartTime(Dictionary<string, string> f) => Time(f, "StartTime");

    /// <summary>A Winlogon entry: 811 only for notification 0 (the sign-in screen is ready), 1, and 2 (Win32Status 0 when it succeeded).</summary>
    internal static LogonEvent? Logon(int id, Dictionary<string, string> f, DateTime at, int pid) => id switch
    {
        811 => Int(f, "Event") == 0 ? new LogonEvent(at, 811, pid) : null,
        1 => new LogonEvent(at, 1, pid),
        2 => new LogonEvent(at, 2, pid, Int(f, "Win32Status") is null or 0),
        _ => null,
    };

    /// <summary>A Shell-Core entry about the apps that start with Windows, or null for any other.</summary>
    internal static ShellEvent? Shell(int id, Dictionary<string, string> f, DateTime at, int pid, string? sid) => id switch
    {
        9705 or 9706 => Text(f, "KeyName") is { } key ? new ShellEvent(at, id, pid, sid, Key: key) : null,
        9707 or 9708 or 62408 or 62409 => Text(f, "Command") is { } cmd ? new ShellEvent(at, id, pid, sid, Command: cmd) : null,
        62170 or 62171 => new ShellEvent(at, id, pid, sid, Task: Text(f, "TaskName")),
        _ => null,
    };

    /// <summary>Event 100: the start's own record, or null when its figures can't be believed.</summary>
    internal static BootRecord? Boot(Dictionary<string, string> f, DateTime logged)
    {
        if (Time(f, "BootStartTime") is not { } start) return null;
        if (Int(f, "MainPathBootTime") is not int main || !Plausible(main, MostWindowsMs)) return null;
        if (Int(f, "BootPostBootTime") is not int post || post < 0 || post > MostWindowsMs) return null;
        int? wait = Int(f, "UserLogonWaitDuration") is int w && w >= 0 && w < main ? w : null;
        bool? update = f.TryGetValue("BootIsRebootAfterInstall", out var u) ? u.Equals("true", StringComparison.OrdinalIgnoreCase) || u == "1" : null;
        return new BootRecord(start, logged, main, wait, post, update);
    }

    /// <summary>Events 101 to 110: what Windows named as slower than its usual, with its own figures.</summary>
    internal static SlowItem? Slow(int id, Dictionary<string, string> f)
    {
        if (id is < 101 or > 110) return null;
        int slower = Int(f, "DegradationTime") ?? 0;
        if (slower <= 0) return null;
        int total = Int(f, "TotalTime") ?? slower;
        string? friendly = Text(f, "FriendlyName"), name = Text(f, "Name");
        string? file = id is 101 or 102 or 103 ? name : null;
        string shown = friendly ?? (name is null ? "" : id == 101 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
        // A part of Windows' own start has no name of its own: it's named by the event.
        if (id is 104 or 105 or 106 or 107 or 108 or 110) shown = "";
        return new SlowItem(id, shown, file, total, slower);
    }

    private static string? Text(Dictionary<string, string> f, string key) => f.TryGetValue(key, out var v) && v.Trim().Length > 0 ? v.Trim() : null;

    private static int? Int(Dictionary<string, string> f, string key) =>
        f.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;

    /// <summary>A time in an entry's data, in UTC: Windows writes up to nine places after the second, .NET reads seven.</summary>
    private static DateTime? Time(Dictionary<string, string> f, string key) =>
        f.TryGetValue(key, out var v) && DateTime.TryParse(TooPrecise().Replace(v, "$1"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) && t.Year > 2000
            ? (t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t).ToLocalTime() : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"(\.\d{7})\d+")]
    private static partial System.Text.RegularExpressions.Regex TooPrecise();

    private static bool Plausible(int ms, int most) => ms > 0 && ms <= most;

    private sealed record Entry(int Id, DateTime At, string Xml, int Pid, string? Sid);

    /// <summary>A log's entries, oldest first; <paramref name="refused"/> hears Windows' words when it won't give the log at all.</summary>
    private static IEnumerable<Entry> Query(string log, string xpath, DateTime? since, Action<string>? refused)
    {
        string query = since is { } s && s > DateTime.MinValue.AddDays(1)
            ? xpath.Replace("*[System[", $"*[System[TimeCreated[@SystemTime>='{s.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}'] and ")
            : xpath;
        var list = new List<Entry>();
        EventLogReader reader;
        try
        {
            reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query));
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            refused?.Invoke(ex.Message);
            if (refused is null) Log.Write("startup", $"Couldn't read the {log} log: {ex.Message}");
            return list;
        }
        using (reader)
        {
            while (true)
            {
                EventRecord? record;
                try { record = reader.ReadEvent(); }
                catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
                {
                    if (list.Count == 0) refused?.Invoke(ex.Message);
                    Log.Write("startup", $"Stopped reading the {log} log after {list.Count} entries: {ex.Message}");
                    break;
                }
                if (record is null) break;
                using (record)
                {
                    try { list.Add(new Entry(record.Id, record.TimeCreated ?? DateTime.Now, record.ToXml(), record.ProcessId ?? 0, record.UserId?.Value)); }
                    catch (EventLogException) { }
                }
            }
        }
        return list;
    }

    /// <summary>When a log's oldest entry was written (how far back it still reaches), or null when it can't be read or is empty.</summary>
    private static DateTime? Oldest(string log)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName));
            using var first = reader.ReadEvent();
            return first?.TimeCreated;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ── Reading and keeping ──

    /// <summary>
    /// Reads Windows' records of every start since a week before the newest kept (all they hold the first time, and the first
    /// time the sign-in logs are read) and keeps them, saying in the log what was expected and what was found. Returns this
    /// start's time while the apps that start with Windows aren't all launched yet (to look again soon).
    /// </summary>
    public static DateTime? Record(RigsightDb db, bool admin, DateTime now)
    {
        var latest = db.LatestStart();
        // Shell-Core keeps about a week, so a week back fills in any start a read caught half-way.
        bool whole = latest is null || !db.StartupSignInRead();
        var since = whole ? DateTime.MinValue : latest!.Value.AddDays(-7);
        var found = Read(since, now);
        // Cleared with Clear all history: what Windows still holds from before isn't read back in.
        if (db.StartsClearedAt() is { } cleared) found.Starts.RemoveAll(s => s.Time < cleared);

        // Each app by the name the Start-up page gives the apps that start with Windows (else the app's own name, else its file).
        var run = StartupApps.Now();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in db.LoadApps())
            if (app.Exe.Length > 0 && app.Name.Length > 0) names.TryAdd(app.Exe, app.Name);
        for (int i = 0; i < found.Starts.Count; i++)
            if (found.Starts[i].Apps.Count > 0)
                found.Starts[i] = found.Starts[i] with { Apps = [.. found.Starts[i].Apps.Select(a => a with { Name = StartupApps.LaunchName(a.Command ?? a.Exe, run, names) })] };

        var (added, filled) = db.SaveStarts(found.Starts);
        if (whole) db.MarkStartupSignInRead();

        string kinds = string.Join(", ", found.Starts.GroupBy(s => s.Kind).Select(g => $"{g.Count()} {g.Key}"));
        string diag = found.DiagReadable
            ? $"Windows' start-up record (Diagnostics-Performance, a bonus) has {found.Records} starts and {found.Slow} things named as slower, from {found.DiagOldest:d MMM HH:mm}"
            : admin ? $"Windows' start-up record (Diagnostics-Performance, a bonus) couldn't be read though RigSight has admin rights: {found.DiagWhy}"
            : $"Windows' start-up record (Diagnostics-Performance, a bonus) isn't read without admin rights ({found.DiagWhy})";
        string winlogon = found.WinlogonWhy is { } ww ? $"Winlogon couldn't be read ({ww})" : $"Winlogon holds entries from {Day(found.WinlogonOldest)} ({found.Screens} sign-in screens, {found.SignIns} sign-ins read)";
        string shellCore = found.ShellWhy is { } sw ? $"Shell-Core couldn't be read ({sw})" : $"Shell-Core from {Day(found.ShellOldest)} ({found.Launches} app launches read)";
        int all3 = found.Starts.Count(s => s.WindowsMs is not null && s.ReadyMs is not null);
        int noScreen = found.Starts.Count(s => s.WindowsMs is null && s.Kind != StartKind.Hibernate);
        int noReady = found.Starts.Count(s => s.WindowsMs is not null && s.ReadyMs is null);
        Log.Write("startup", $"Read {(whole ? "every start Windows still holds" : $"the starts since {since:d MMM HH:mm}")}: {found.Starts.Count} starts ({(kinds.Length == 0 ? "none" : kinds)}) "
            + $"from {found.Boots} Kernel-Boot 27 and {found.Generals} Kernel-General 12 entries, {found.Firmware} firmware times (Kernel-Boot 30){(found.UsedFwPost ? ", this start's from FwPOSTTime" : "")}. "
            + $"{winlogon}; {shellCore}. {all3} starts with Windows starting and getting ready after sign-in, {noScreen} full or Fast Startup starts without a sign-in screen in Winlogon "
            + $"(or older than it reaches), {noReady} without the apps that start with Windows in Shell-Core. {diag}. Kept {added} new, {filled} filled in.");
        if (found.ThisStart is { } s)
        {
            var longest = s.Apps.MaxBy(a => a.Ms);
            string parts = $"before Windows {Sec(s.BiosMs)}, Windows starting {Sec(s.WindowsMs)}, at the sign-in screen {Sec(s.SignInWaitMs)} (left out), "
                + $"getting ready after sign-in {Sec(s.ReadyMs)}, {s.Apps.Count} apps launched{(longest is null ? "" : $" (longest {longest.Exe} {Sec(longest.Ms)})")}";
            Log.Write("startup", found.ThisStartWaiting is not null
                ? $"This start ({s.Time:HH:mm:ss}) so far: {parts}. The apps that start with Windows aren't all launched yet{(s.SignedIn is null ? " (nobody has signed in yet)" : "")}: looking again in a few minutes"
                : $"This start ({s.Time:HH:mm:ss}): {parts}.");
        }
        else Log.Write("startup", $"This start ({now - TimeSpan.FromMilliseconds(Environment.TickCount64):d MMM HH:mm:ss}) isn't among the starts read (no Kernel-Boot 27 or Kernel-General 12 near it)");
        return found.ThisStartWaiting;

        static string Day(DateTime? t) => t is { } d ? d.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) : "nothing (empty)";
        static string Sec(int? ms) => ms is int m ? (m / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s" : "none";
    }
}
