using System.Diagnostics;
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
    public static bool IsEnabled() => Schtasks("/Query", "/TN", RigsightPaths.AgentTaskName) == 0;

    /// <summary>True when the task starts this copy of the agent (not an older install elsewhere).</summary>
    public static bool PointsHere()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return true;
        Schtasks(out var xml, "/Query", "/TN", RigsightPaths.AgentTaskName, "/XML");
        return xml.Contains($"<Command>{SecurityElement.Escape(exe)}</Command>", StringComparison.OrdinalIgnoreCase);
    }

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

        var file = Path.Combine(Path.GetTempPath(), "Rigsight-agent-task.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return Schtasks("/Create", "/TN", RigsightPaths.AgentTaskName, "/XML", file, "/F") == 0;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    public static bool Disable() => Schtasks("/Delete", "/TN", RigsightPaths.AgentTaskName, "/F") == 0;

    private static int Schtasks(params string[] args) => Schtasks(out _, args);

    private static int Schtasks(out string output, params string[] args)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Error("startup", ex);
            return -1;
        }
    }
}
