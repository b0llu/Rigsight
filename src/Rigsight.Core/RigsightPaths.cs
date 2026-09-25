namespace Rigsight.Core;

/// <summary>Well-known locations and names shared by the agent and the app.</summary>
public static class RigsightPaths
{
    private static readonly string? TestDataDir =
        Environment.GetEnvironmentVariable("RIGSIGHT_DATA_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary>
    /// %LocalAppData%\Rigsight. For testing, the RIGSIGHT_DATA_DIR environment variable points it elsewhere
    /// (e.g. at a folder of generated history) without touching the real data.
    /// </summary>
    public static string DataDir { get; } =
        TestDataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rigsight");

    /// <summary>
    /// A test copy (RIGSIGHT_DATA_DIR set): its agent and app pair up only with each other, through their own pipe and
    /// names, so they run alongside the real ones without touching them. A test agent never registers itself to start
    /// with Windows, updates, asks for admin rights or starts RivaTuner.
    /// </summary>
    public static bool IsTestInstance => TestDataDir is not null;

    /// <summary>Added to every shared name by a test copy: a hash of its data folder ("" normally).</summary>
    public static string InstanceSuffix { get; } = SuffixFor(TestDataDir);

    internal static string SuffixFor(string? testDataDir) => testDataDir is null ? ""
        : "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(testDataDir.ToLowerInvariant())))[..12];

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string Database => Path.Combine(DataDir, "rigsight.db");
    public static string IconCache => Path.Combine(DataDir, "icons");
    public static string LogFile => Path.Combine(DataDir, "rigsight.log");

    /// <summary>Named pipe the agent serves live data on.</summary>
    public static string PipeName { get; } = "Rigsight.Agent.v1" + InstanceSuffix;

    /// <summary>Held by the running agent (one per user).</summary>
    public static string AgentMutex { get; } = @"Local\Rigsight.Agent" + InstanceSuffix;

    /// <summary>Signalled (by "Rigsight.Agent.exe --quit", e.g. from the installer) to ask the agent to save and exit.</summary>
    public static string AgentQuitEvent { get; } = @"Local\Rigsight.Agent.Quit" + InstanceSuffix;

    /// <summary>Task Scheduler task that starts the agent at sign-in.</summary>
    public const string AgentTaskName = "Rigsight Agent";

    public const string AgentExe = "Rigsight.Agent.exe";
    public const string AppExe = "Rigsight.exe";

    /// <summary>Finds a sibling executable (the agent and app are deployed side by side).</summary>
    public static string Sibling(string exeName) => Path.Combine(AppContext.BaseDirectory, exeName);
}
