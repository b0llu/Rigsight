using Microsoft.Win32;

namespace Rigsight.Core;

/// <summary>
/// What the admin who installed chose for the whole PC, kept where every account can read it and only an admin can
/// change it (HKLM): whether Rigsight starts for everyone who signs in, and whether the PC keeps it up to date for
/// them. Neither exists unless an admin asked for it in setup or in Settings, so an update never turns one on by itself.
/// </summary>
public static class MachineSetup
{
    private const string Key = @"SOFTWARE\Rigsight";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Rigsight";
    private const string EveryoneName = "Everyone", KeepUpdatedName = "KeepUpdated";

    /// <summary>What the Run entry starts the agent with: it then decides, for the person signing in, how to run.</summary>
    public const string SignInArgument = "--signin";

    /// <summary>
    /// The same start for someone already signed in, after an update closed their agent: as at sign-in, except that
    /// their own "Start with Windows" isn't asked (it was running, so it runs again).
    /// </summary>
    public const string ResumeArgument = "--resume";

    /// <summary>Rigsight starts for each person who signs in on this PC.</summary>
    public static bool Everyone => Read(EveryoneName);

    /// <summary>The PC keeps Rigsight up to date by itself, for accounts that can't install an update.</summary>
    public static bool KeepUpdated => Read(KeepUpdatedName);

    private static bool Read(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Key);
            return key?.GetValue(name) is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is under Program Files, where only an admin can change a file. What is set up
    /// for the whole PC runs the agent for every account, and as the system: installed anywhere else (a folder picked in
    /// setup that ordinary accounts can write to), any of them could put their own program in its place.
    /// </summary>
    public static bool OnlyAdminsCanChange(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        foreach (var special in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string root = Environment.GetFolderPath(special);
            if (root.Length > 0 && full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Admin only. Starts <paramref name="agentExe"/> at every sign-in on this PC, or stops doing so.</summary>
    public static void SetEveryone(bool on, string agentExe)
    {
        Mark(EveryoneName, on);
        using var run = Registry.LocalMachine.CreateSubKey(RunKey);
        if (on) run.SetValue(RunName, $"\"{agentExe}\" {SignInArgument}");
        else run.DeleteValue(RunName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Whether this person's own account starts the agent as they sign in: what "Start with Windows" is on a standard
    /// account, on a PC where no admin set Rigsight up for everyone. Their own Run entry, which needs no admin rights
    /// (the start-up task does, and its file is written where only an admin can write).
    /// </summary>
    public static bool StartsForMe
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                return run?.GetValue(RunName) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Adds or removes this person's own start at sign-in. False when it couldn't be changed.</summary>
    public static bool SetStartsForMe(bool on, string agentExe)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) run.SetValue(RunName, $"\"{agentExe}\" {SignInArgument}");
            else run.DeleteValue(RunName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("setup", ex);
            return false;
        }
    }

    /// <summary>Admin only. Notes the choice; the task that does the updating is the agent's to add or remove.</summary>
    public static void SetKeepUpdated(bool on) => Mark(KeepUpdatedName, on);

    private static void Mark(string name, bool on)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Key);
        key.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>Admin only, on uninstall: nothing of the above is left behind.</summary>
    public static void Clear()
    {
        try
        {
            using (var run = Registry.LocalMachine.OpenSubKey(RunKey, writable: true)) run?.DeleteValue(RunName, throwOnMissingValue: false);
            Registry.LocalMachine.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log.Error("setup", ex);
        }
    }
}
