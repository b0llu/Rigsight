using System.Runtime.CompilerServices;

namespace Rigsight.Tests.Support;

/// <summary>
/// Runs before any test touches Rigsight: every path Rigsight uses (settings, database, log, icons) points at a
/// fresh folder of this run, never at the real %LocalAppData%\Rigsight. That also makes this a test instance
/// (<see cref="Core.RigsightPaths.IsTestInstance"/>), with its own pipe and names.
/// </summary>
internal static class TestEnvironment
{
    public static string DataDir { get; private set; } = "";

    [ModuleInitializer]
    internal static void Init()
    {
        var runs = Path.Combine(Path.GetTempPath(), "rigsight-tests");
        DataDir = Path.Combine(runs, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
        Directory.CreateDirectory(DataDir);
        // Runs don't pile up: the last two are kept (screenshots, logs), older ones go.
        foreach (var old in Directory.GetDirectories(runs).Where(d => d != DataDir).OrderDescending().Skip(2))
        {
            try { Directory.Delete(old, recursive: true); }
            catch (IOException) { } // still in use by another test run
            catch (UnauthorizedAccessException) { }
        }
        // The tests' own databases (two years of history each, tens of MB) go when the run ends; the screenshots,
        // log and measures stay for a look.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var dir in Directory.GetDirectories(DataDir).Where(d => Path.GetFileName(d) != "screens"))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        };
        Environment.SetEnvironmentVariable("RIGSIGHT_DATA_DIR", DataDir);
    }

    /// <summary>A new empty folder inside this run's data folder.</summary>
    public static string NewFolder(string name)
    {
        var dir = Path.Combine(DataDir, $"{name}-{Guid.NewGuid():N}"[..Math.Min(name.Length + 9, 60)]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
