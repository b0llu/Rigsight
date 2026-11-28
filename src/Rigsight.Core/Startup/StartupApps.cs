using System.Text.RegularExpressions;
using Microsoft.Win32;
using Rigsight.Core.Stability;

namespace Rigsight.Core.Startup;

/// <summary>A program that starts with Windows: its name as the Timeline says it, whether it's on, and its file (for its icon).</summary>
public sealed record StartupApp(string Name, bool On, string? Path)
{
    /// <summary>The command it's started with (a Run value's text), to know it again in Windows' log of each start.</summary>
    public string? Command { get; init; }
}

/// <summary>
/// The programs that start with Windows, as the Timeline already follows them (the Run keys and the startup folders, Task
/// Manager's switch included), with the file each one runs, for its icon on the Start-up page. RigSight switches nothing.
/// </summary>
public static partial class StartupApps
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>Every program that starts with Windows now (on and off), by name.</summary>
    public static List<StartupApp> Now()
    {
        var found = new List<(string Raw, bool On, string? Path, string? Command)>();
        try
        {
            foreach (var (hive, view, approved) in new[] { (RegistryHive.CurrentUser, RegistryView.Default, "Run"),
                         (RegistryHive.LocalMachine, RegistryView.Registry64, "Run"), (RegistryHive.LocalMachine, RegistryView.Registry32, "Run32") })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var run = root.OpenSubKey(RunKey);
                if (run is null) continue;
                using var root64 = RegistryKey.OpenBaseKey(hive, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : view);
                using var state = root64.OpenSubKey(ApprovedKey + approved);
                foreach (string name in run.GetValueNames())
                    if (name.Length > 0 && run.GetValue(name) as string is var command) found.Add((name, Enabled(state, name), ExeOf(command), command));
            }
            foreach (var (folder, hive) in new[] { (Environment.SpecialFolder.Startup, RegistryHive.CurrentUser), (Environment.SpecialFolder.CommonStartup, RegistryHive.LocalMachine) })
            {
                string path = Environment.GetFolderPath(folder);
                if (path.Length == 0 || !Directory.Exists(path)) continue;
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var state = root.OpenSubKey(ApprovedKey + "StartupFolder");
                foreach (string file in Directory.EnumerateFiles(path))
                    if (!System.IO.Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                        found.Add((System.IO.Path.GetFileNameWithoutExtension(file), Enabled(state, System.IO.Path.GetFileName(file)), file, null));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Write("startup", $"Couldn't read every program that starts with Windows: {ex.Message}");
        }
        // Named as the Timeline names them (Inventory's own rule), one each.
        var byName = new Dictionary<string, StartupApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, on, path, command) in found)
        {
            string name = Inventory.StartupItems([(raw, on)])[0].Name;
            byName[name] = byName.TryGetValue(name, out var was)
                ? was with { On = was.On || on, Path = was.Path ?? path, Command = was.Command ?? command }
                : new StartupApp(name, on, path) { Command = command };
        }
        return [.. byName.Values.OrderByDescending(a => a.On).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];

        static bool Enabled(RegistryKey? state, string name) => state?.GetValue(name) is not byte[] { Length: > 0 } b || b[0] % 2 == 0;
    }

    /// <summary>The program a Run value starts: its quoted or first path, with any environment variables filled in.</summary>
    internal static string? ExeOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith('"')) return c.IndexOf('"', 1) is int end and > 1 ? c[1..end] : null;
        var m = ExePath().Match(c);
        return m.Success ? m.Value.Trim() : c.Split(' ')[0];
    }

    [GeneratedRegex(@"^.+?\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex ExePath();

    // ── Each app as Windows launched it ──

    /// <summary>
    /// The program of a command as Windows' log of a start writes it: the file and what follows it, without its folder
    /// (steam.exe" -silent, Update.exe" --processStart Discord.exe). An app started through an updater
    /// (--processStart) is that app's file.
    /// </summary>
    public static string ExeOfLogged(string command)
    {
        var target = ProcessStart().Match(command);
        if (target.Success) return target.Groups[1].Value;
        string c = command.Trim().TrimStart('"');
        int end = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        string exe = end > 0 ? c[..(end + 4)] : c.Split(' ', '"')[0];
        int slash = exe.LastIndexOf('\\');
        return slash >= 0 ? exe[(slash + 1)..] : exe;
    }

    /// <summary>An app's name from its file alone ("steam.exe" is "steam"), for when nothing better is known.</summary>
    public static string DefaultName(string command)
    {
        string exe = ExeOfLogged(command);
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && exe.Length > 4 ? exe[..^4] : exe;
    }

    /// <summary>
    /// The name of an app Windows launched as it started: the program that starts with Windows whose command it is (its
    /// name as the Timeline and this page give it), else the app's own name RigSight knows by its file, else its file.
    /// </summary>
    /// <param name="command">The command as Windows logged it.</param>
    /// <param name="names">App names by file ("steam.exe" to "Steam"), from the apps RigSight has seen.</param>
    public static string LaunchName(string command, IReadOnlyList<StartupApp> run, IReadOnlyDictionary<string, string> names)
    {
        string logged = Plain(command);
        if (logged.Length > 0)
            foreach (var app in run)
                if (app.Command is { Length: > 0 } c && Plain(Environment.ExpandEnvironmentVariables(c)) is var full && (full == logged || full.EndsWith("\\" + logged, StringComparison.Ordinal)))
                    return app.Name;
        string exe = ExeOfLogged(command);
        return names.TryGetValue(exe, out var name) && name.Length > 0 ? name : DefaultName(command);

        static string Plain(string s) => Space().Replace(s.Replace("\"", ""), " ").Trim().ToLowerInvariant();
    }

    [GeneratedRegex(@"--processStart\s+""?([^""\s]+\.exe)", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessStart();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    // ── As it was ──

    private const string Added = " now starts with Windows", Removed = " no longer starts with Windows", TurnedOff = " turned off at startup", TurnedOn = " turned on at startup";

    /// <summary>
    /// The list as it was at <paramref name="at"/>: today's, with each startup change since undone, newest first (a program
    /// that began starting with Windows since then wasn't there; one that stopped was). Changes come from the Timeline.
    /// </summary>
    public static List<StartupApp> At(IReadOnlyList<StartupApp> now, IEnumerable<SystemChange> since, DateTime at)
    {
        var list = now.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var c in since.Where(c => c.Kind == ChangeKind.Startup && c.Time >= at).OrderByDescending(c => c.Time))
        {
            if (c.Title.EndsWith(Added, StringComparison.Ordinal)) list.Remove(c.Title[..^Added.Length]);
            else if (c.Title.EndsWith(Removed, StringComparison.Ordinal) && c.Title[..^Removed.Length] is var name) list[name] = list.GetValueOrDefault(name) ?? new StartupApp(name, true, null);
            else if (c.Title.EndsWith(TurnedOff, StringComparison.Ordinal) && list.TryGetValue(c.Title[..^TurnedOff.Length], out var off)) list[off.Name] = off with { On = true };
            else if (c.Title.EndsWith(TurnedOn, StringComparison.Ordinal) && list.TryGetValue(c.Title[..^TurnedOn.Length], out var on)) list[on.Name] = on with { On = false };
        }
        return [.. list.Values.OrderByDescending(a => a.On).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The name of the program a startup change is about (its title without what happened), or null.</summary>
    public static string? NameOf(SystemChange c)
    {
        if (c.Kind != ChangeKind.Startup) return null;
        foreach (var ends in new[] { Added, Removed, TurnedOff, TurnedOn })
            if (c.Title.EndsWith(ends, StringComparison.Ordinal)) return c.Title[..^ends.Length];
        return null;
    }
}
