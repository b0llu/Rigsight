namespace Rigsight.Core;

/// <summary>Well-known locations and names shared by the agent and the app.</summary>
public static class RigsightPaths
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rigsight");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string Database => Path.Combine(DataDir, "rigsight.db");
    public static string IconCache => Path.Combine(DataDir, "icons");
    public static string LogFile => Path.Combine(DataDir, "rigsight.log");

    /// <summary>Named pipe the agent serves live data on.</summary>
    public const string PipeName = "Rigsight.Agent.v1";

    /// <summary>Task Scheduler task that starts the agent at sign-in.</summary>
    public const string AgentTaskName = "Rigsight Agent";

    public const string AgentExe = "Rigsight.Agent.exe";
    public const string AppExe = "Rigsight.exe";

    /// <summary>Finds a sibling executable (the agent and app are deployed side by side).</summary>
    public static string Sibling(string exeName) => Path.Combine(AppContext.BaseDirectory, exeName);
}
