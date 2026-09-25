using System.Globalization;

namespace Rigsight.Tests.Core;

/// <summary>Tests that change process-wide statics (Units.Fahrenheit, environment variables) run one at a time.</summary>
[CollectionDefinition("Core statics", DisableParallelization = true)]
public sealed class CoreStaticsCollection;

/// <summary>Runs a block under another culture (formatting follows the current culture), then puts the old one back.</summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _ui = CultureInfo.CurrentUICulture;

    public CultureScope(string name) =>
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _ui;
    }
}

internal static class Repo
{
    /// <summary>The copy's root folder (the one with Rigsight.slnx), found from the test's output folder.</summary>
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Rigsight.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Rigsight.slnx not found above " + AppContext.BaseDirectory);
    }
}
