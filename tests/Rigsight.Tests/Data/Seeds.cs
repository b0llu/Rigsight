using Rigsight.Core.Data;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Data;

/// <summary>
/// Generated histories shared by the read-only tests of this run, each made once in a folder of its own. Tests only
/// open them with <see cref="RigsightDb.OpenReader(string)"/>, so they can run side by side.
/// </summary>
public static class Seeds
{
    private static readonly Lazy<(string Path, DateTime Now)> SmallSeed = new(() => Generate(SeedProfile.Small));
    private static readonly Lazy<(string Path, DateTime Now)> TypicalSeed = new(() => Generate(SeedProfile.Typical));

    public static string Small => SmallSeed.Value.Path;
    public static string Typical => TypicalSeed.Value.Path;

    /// <summary>The "now" the seed was generated for (today's history ends there).</summary>
    public static DateTime SmallNow => SmallSeed.Value.Now;
    public static DateTime TypicalNow => TypicalSeed.Value.Now;

    public static string PathOf(string profile) => profile == "small" ? Small : Typical;

    public static RigsightDb Open(string profile) => RigsightDb.OpenReader(PathOf(profile))!;

    private static (string, DateTime) Generate(SeedProfile profile)
    {
        var path = System.IO.Path.Combine(TestEnvironment.NewFolder("seed-" + profile.Name), "rigsight.db");
        var now = DateTime.Now;
        SeedData.Generate(path, profile, now);
        return (path, now);
    }
}
