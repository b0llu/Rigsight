using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// Storage's "Space you could free" sizes, kept between starts for an hour so a first visit doesn't add up %TEMP%
/// again (2.2 s of a core on a 22,000-folder %TEMP%, 2026-11-05).
/// </summary>
[Collection("UI")]
public sealed class CleanupSizesTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 14, 0, 0);

    private static Dictionary<string, long> Sizes() => new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\Users\me\AppData\Local\Temp"] = 1_400L << 20,
        [@"C:\Users\me\Downloads"] = 80L << 20,
    };

    [Fact]
    public void Sizes_kept_within_the_hour_come_back_and_older_ones_dont()
    {
        CleanupSizes.Save(Sizes(), Now);
        var back = CleanupSizes.Load(Now.AddMinutes(59));
        Assert.NotNull(back);
        Assert.Equal(1_400L << 20, back![@"c:\users\me\appdata\local\temp"]); // any case, as Windows paths are
        Assert.Equal(2, back.Count);

        Assert.Null(CleanupSizes.Load(Now.AddMinutes(61)));
        // A clock set back: counted "in the future", so counted again.
        Assert.Null(CleanupSizes.Load(Now.AddMinutes(-5)));
    }

    [Fact]
    public void A_folder_opened_to_be_cleaned_is_counted_again_and_the_rest_stay()
    {
        CleanupSizes.Save(Sizes(), Now);
        CleanupSizes.Forget(@"C:\Users\me\Downloads");
        var back = CleanupSizes.Load(Now.AddMinutes(10));
        Assert.NotNull(back);
        Assert.False(back!.ContainsKey(@"C:\Users\me\Downloads"));
        Assert.True(back.ContainsKey(@"C:\Users\me\AppData\Local\Temp"));
        // Forgetting keeps when they were counted: still gone after the hour from the first count.
        Assert.Null(CleanupSizes.Load(Now.AddMinutes(61)));
    }

    [Fact]
    public void A_broken_or_missing_file_means_counting_again()
    {
        File.WriteAllText(CleanupSizes.File, "{ not json");
        Assert.Null(CleanupSizes.Load(Now));
        CleanupSizes.Forget(@"C:\Users\me\Downloads"); // no throw

        File.Delete(CleanupSizes.File);
        Assert.Null(CleanupSizes.Load(Now));
        CleanupSizes.Forget(@"C:\Users\me\Downloads");
        Assert.False(File.Exists(CleanupSizes.File));
    }
}
