using System.Diagnostics;
using Microsoft.Win32;
using Rigsight.Agent.Native;
using Rigsight.Core;

namespace Rigsight.Agent.Widgets;

internal enum RtssInstallResult
{
    /// <summary>It was there already (nothing was run).</summary>
    AlreadyInstalled,
    Installed,
    /// <summary>The installer finished (or couldn't start) without RivaTuner being there.</summary>
    Failed,
    /// <summary>The installer stopped making progress and was ended.</summary>
    Stuck,
    /// <summary>No winget on this PC (older Windows 10 without App Installer).</summary>
    NoWinget,
}

/// <summary>
/// Finding RivaTuner Statistics Server, however it was installed (on its own, with MSI Afterburner, into another
/// folder), and installing it with winget when it isn't there, for setup and for the Overlay page alike.
/// </summary>
internal static class RtssSetup
{
    /// <summary>Where the pieces of the lookup come from (the tests give their own).</summary>
    internal sealed record Sources(
        Func<RegistryView, string, string, string?> Registry,
        Func<IEnumerable<(string? Name, string? Location, string? Uninstaller, string? Icon)>> Installed,
        Func<IEnumerable<string?>> RunningPaths,
        Func<bool> Running,
        Func<string, bool> FileExists,
        Func<Environment.SpecialFolder, string> Folder);

    internal static readonly Sources Windows = new(ReadRegistry, InstalledPrograms, RunningRtssPaths,
        () => { var p = Process.GetProcessesByName("RTSS"); foreach (var x in p) x.Dispose(); return p.Length > 0; },
        File.Exists, Environment.GetFolderPath);

    /// <summary>RTSS.exe, or null when RivaTuner isn't installed.</summary>
    public static string? Find() => Find(Windows);

    internal static string? Find(Sources s)
    {
        string? Exe(string? dir) => string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir.Trim().Trim('"'), "RTSS.exe") is var e && s.FileExists(e) ? e : null;
        string? ExeOf(string? file) =>
            string.IsNullOrWhiteSpace(file) ? null : Exe(Path.GetDirectoryName(file.Trim().Split(',')[0].Trim('"')));

        // Its own registry entry: the folder, or (newer versions) the program itself.
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            if (Exe(s.Registry(view, @"SOFTWARE\Unwinder\RTSS", "InstallDir")) is { } a) return a;
            if (s.Registry(view, @"SOFTWARE\Unwinder\RTSS", "InstallPath") is { } path && s.FileExists(path.Trim('"'))) return path.Trim('"');
        }
        // The installed-programs list (Settings → Apps), which every installer fills in.
        foreach (var (name, location, uninstaller, icon) in s.Installed())
        {
            if (name?.Contains("RivaTuner Statistics Server", StringComparison.OrdinalIgnoreCase) != true) continue;
            if ((Exe(location) ?? ExeOf(uninstaller) ?? ExeOf(icon)) is { } b) return b;
        }
        // Running right now (MSI Afterburner starts it with Windows).
        foreach (var running in s.RunningPaths())
            if (running is not null && s.FileExists(running)) return running;
        // The usual folders.
        foreach (var pf in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            if (Exe(Path.Combine(s.Folder(pf), "RivaTuner Statistics Server")) is { } c) return c;
        return null;
    }

    /// <summary>Whether RivaTuner is there, even if its program can't be located (it's running).</summary>
    public static bool IsInstalled() => Find() is not null || Windows.Running();

    /// <summary>
    /// Installs RivaTuner with winget unless it's already there. Waits as long as the install makes progress (a slow PC
    /// or download is fine); a question from RivaTuner's installer is brought to the front; an install that does nothing
    /// for minutes is ended. <paramref name="state"/> gets "working", "asking" or "idle".
    /// </summary>
    public static RtssInstallResult Install(Action<string>? state = null, InstallWatch? watch = null, CancellationToken ct = default)
    {
        if (IsInstalled())
        {
            Log.Write("install", "RivaTuner is already installed");
            return RtssInstallResult.AlreadyInstalled;
        }
        var winget = FindWinget();
        if (winget is null)
        {
            Log.Write("install", "winget isn't available: RivaTuner can't be installed automatically");
            return RtssInstallResult.NoWinget;
        }

        watch ??= new InstallWatch { StateChanged = state };
        var start = new ProcessStartInfo(winget,
            ["install", "--id", "Guru3D.RTSS", "-e", "--silent", "--accept-package-agreements", "--accept-source-agreements"])
        // Its input is closed: a question winget itself asks gets no answer rather than waiting unseen.
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        var result = watch.Run(start, ct);
        Log.Write("install", $"winget: {result.Outcome}, exit code {result.ExitCode?.ToString("X") ?? "-"}, after {result.Elapsed.TotalSeconds:0} s");

        if (IsInstalled()) return RtssInstallResult.Installed;
        return result.Outcome is WatchOutcome.Stuck or WatchOutcome.TooLong ? RtssInstallResult.Stuck : RtssInstallResult.Failed;
    }

    /// <summary>winget.exe (an App Installer alias in the user's WindowsApps folder), or null.</summary>
    private static string? FindWinget()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var exe = Path.Combine(dir.Trim(), "winget.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    private static string? ReadRegistry(RegistryView view, string key, string value)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = root.OpenSubKey(key);
            return k?.GetValue(value) as string;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<(string?, string?, string?, string?)> InstalledPrograms()
    {
        var list = new List<(string?, string?, string?, string?)>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var app = uninstall.OpenSubKey(name);
                    if (app is null) continue;
                    list.Add((app.GetValue("DisplayName") as string, app.GetValue("InstallLocation") as string,
                        app.GetValue("UninstallString") as string, app.GetValue("DisplayIcon") as string));
                }
            }
            catch
            {
                // Unreadable: the other places still count.
            }
        }
        return list;
    }

    private static IEnumerable<string?> RunningRtssPaths()
    {
        var paths = new List<string?>();
        foreach (var p in Process.GetProcessesByName("RTSS"))
        {
            paths.Add(Win32.ProcessPath(p.Id));
            p.Dispose();
        }
        return paths;
    }
}
