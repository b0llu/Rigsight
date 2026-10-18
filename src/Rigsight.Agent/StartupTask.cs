using System.Security;
using System.Security.Principal;
using System.Text;
using Rigsight.Core;

namespace Rigsight.Agent;

/// <summary>
/// "Start with Windows" via a Task Scheduler logon task, which can start the agent with admin
/// rights and no UAC prompt (a Run-key entry can't).
/// </summary>
internal static class StartupTask
{
    public static bool IsEnabled() => AgentTask.Exists();

    /// <summary>True when the task starts this copy of the agent (not an older install elsewhere).</summary>
    public static bool PointsHere() => Environment.ProcessPath is not { } exe || AgentTask.PointsTo(exe);

    public static bool Enable()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;

        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Rigsight background agent: tracks temperatures and app usage, and shows the tray icon and widgets.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;

        // Next to the agent (Program Files), not in the temp folder: there any program could swap the file between
        // writing it and Windows reading it, and have its own command run as admin at every sign-in.
        var file = Path.Combine(AppContext.BaseDirectory, "Rigsight-agent-task.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            // This account's own task, never the old one for the whole PC: written over by a second admin, that one kept
            // the first admin's permissions, and the second, without admin rights, could then neither read nor start
            // what was now its own task (seen on a test machine: "Access is denied").
            if (AgentTask.Schtasks(out _, "/Create", "/TN", AgentTask.Mine, "/XML", file, "/F") != 0) return false;
            AgentTask.DropOldOne();
            return true;
        }
        catch (Exception ex)
        {
            // The program's folder not writable, say: no task, but the agent still starts.
            Rigsight.Core.Log.Write("startup", $"The start-with-Windows task couldn't be made: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    public static bool Disable() => AgentTask.Delete();
}
