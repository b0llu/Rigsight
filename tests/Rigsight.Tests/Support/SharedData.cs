using Rigsight.Core;
using Rigsight.Core.Settings;

namespace Rigsight.Tests.Support;

/// <summary>
/// The run's default data folder (<see cref="RigsightPaths.Database"/>, <see cref="RigsightPaths.SettingsFile"/>), seeded
/// once with <see cref="SeedProfile.Small"/>. Code that reads the default paths (the app's view models, ReportService)
/// uses it; such tests belong to the "UI" collection, which runs one test at a time, and must not change the data.
/// Tests that write history use a database of their own (<c>RigsightDb.OpenWriter(path)</c>).
/// </summary>
public static class SharedData
{
    private static readonly Lock Gate = new();
    private static bool _seeded;

    public const string DashboardId = "testdash01";

    public static void EnsureSeeded()
    {
        lock (Gate)
        {
            if (_seeded) return;
            SeedData.Generate(RigsightPaths.Database, SeedProfile.Small);
            _seeded = true;
        }
    }

    /// <summary>Resets settings.json to the quiet test settings (plus the everything-dashboard).</summary>
    public static RigsightSettings ResetSettings()
    {
        var settings = SeedData.QuietSettings();
        settings.CustomPages.Add(AppUi.AppHost.Dashboard());
        SettingsStore.Save(settings);
        return settings;
    }
}
