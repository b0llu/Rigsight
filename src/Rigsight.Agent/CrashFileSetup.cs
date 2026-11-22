using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Rigsight.Core;
using Rigsight.Core.Stability;

namespace Rigsight.Agent;

/// <summary>
/// Asks Windows to save a crash file when RigSight's agent or app crashes (see <see cref="CrashFiles"/>). The installer
/// sets the same up; the agent checks it at each start with admin rights, so a PC that updated from an older version
/// (or where something removed it) has it too. Only what differs is written. Without admin rights it is only read.
/// </summary>
internal static class CrashFileSetup
{
    public static void Ensure(bool isAdmin)
    {
        try
        {
            if (isAdmin) MakeFolder();
            var changed = new List<string>();
            var missing = new List<string>();
            foreach (var program in CrashFiles.Programs)
            {
                if (IsSet(program)) continue;
                if (!isAdmin) { missing.Add(program); continue; }
                using var key = Registry.LocalMachine.CreateSubKey(CrashFiles.KeyFor(program), writable: true);
                key.SetValue("DumpFolder", CrashFiles.Folder, RegistryValueKind.ExpandString);
                key.SetValue("DumpType", CrashFiles.DumpType, RegistryValueKind.DWord);
                key.SetValue("CustomDumpFlags", CrashFiles.CustomDumpFlags, RegistryValueKind.DWord);
                key.SetValue("DumpCount", CrashFiles.DumpCount, RegistryValueKind.DWord);
                changed.Add(program);
            }
            int saved = CrashFiles.All().Count;
            Log.Write("crash files", changed.Count > 0
                ? $"Asked Windows to save a crash file of {string.Join(" and ", changed)} in {CrashFiles.Folder} (it wasn't set up); {saved} there now"
                : missing.Count > 0
                    ? $"Windows isn't set to save a crash file of {string.Join(" and ", missing)}, and without admin rights it can't be set here; {saved} in {CrashFiles.Folder}"
                    : $"Windows saves a crash file of RigSight's programs in {CrashFiles.Folder}; {saved} there now");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Write("crash files", $"Couldn't set up crash files: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Set up as RigSight sets it: the same folder, kind of dump and count.</summary>
    private static bool IsSet(string program)
    {
        using var key = Registry.LocalMachine.OpenSubKey(CrashFiles.KeyFor(program));
        return key is not null
            && string.Equals(key.GetValue("DumpFolder", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, CrashFiles.Folder, StringComparison.OrdinalIgnoreCase)
            && key.GetValue("DumpType") is int type && type == CrashFiles.DumpType
            && key.GetValue("CustomDumpFlags") is int flags && flags == CrashFiles.CustomDumpFlags
            && key.GetValue("DumpCount") is int count && count == CrashFiles.DumpCount;
    }

    /// <summary>
    /// The folder, writable by everyone signed in: Windows saves a crash of the app (which runs with the person's own
    /// rights) as that person, so a folder only admins could write to would stay empty for the app.
    /// </summary>
    private static void MakeFolder()
    {
        var dir = Directory.CreateDirectory(CrashFiles.Folder);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = dir.GetAccessControl();
        var rule = new FileSystemAccessRule(users, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow);
        bool has = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(r => r.IdentityReference == users && r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
        if (has) return;
        security.AddAccessRule(rule);
        dir.SetAccessControl(security);
    }
}
