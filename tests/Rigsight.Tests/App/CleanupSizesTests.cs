using Rigsight.Core.Data;
using Rigsight.Services;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// Storage's "Space you could free" sizes, kept between starts so a first visit doesn't add up %TEMP% again (2.2 s of
/// a core on a 22,000-folder %TEMP%, 2026-11-05): a place counted whole (Downloads, through its filter) for an hour,
/// each folder inside the others for as long as it holds (see StorageScanTests for when that is).
/// </summary>
[Collection("UI")]
public sealed class CleanupSizesTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 14, 0, 0);
    private const string Temp = @"C:\Users\me\AppData\Local\Temp", Downloads = @"C:\Users\me\Downloads";

    private static Dictionary<string, long> Whole() => new(StringComparer.OrdinalIgnoreCase) { [Downloads] = 80L << 20 };

    private static Dictionary<string, Dictionary<string, KeptFolder>> Folders() => new(StringComparer.OrdinalIgnoreCase)
    {
        [Temp] = new() { ["setup-123"] = new KeptFolder(638_000_000_000_000_000, TimeUtil.ToUnix(Now), 1_400L << 20) },
    };

    [Fact]
    public void Whole_places_come_back_within_the_hour_and_folders_stay_with_their_own_times()
    {
        CleanupSizes.Save(Whole(), Now, Folders());
        var back = CleanupSizes.Load(Now.AddMinutes(59));
        Assert.NotNull(back);
        Assert.Equal(80L << 20, back!.Whole![@"c:\users\me\downloads"]); // any case, as Windows paths are
        var folder = back.Folders[@"c:\users\me\appdata\local\temp"]["SETUP-123"];
        Assert.Equal(new KeptFolder(638_000_000_000_000_000, TimeUtil.ToUnix(Now), 1_400L << 20), folder);

        // After the hour the whole places are counted again; the folders are still there, each with when it was counted.
        var later = CleanupSizes.Load(Now.AddMinutes(61));
        Assert.Null(later!.Whole);
        Assert.Single(later.Folders[Temp]);
        // A clock set back: counted "in the future", so counted again.
        Assert.Null(CleanupSizes.Load(Now.AddMinutes(-5))!.Whole);
    }

    [Fact]
    public void A_place_opened_to_be_cleaned_is_counted_again_and_the_rest_stay()
    {
        CleanupSizes.Save(Whole(), Now, Folders());
        CleanupSizes.Forget(Temp);
        var back = CleanupSizes.Load(Now.AddMinutes(10))!;
        Assert.False(back.Folders.ContainsKey(Temp));
        Assert.True(back.Whole!.ContainsKey(Downloads));

        CleanupSizes.Forget(Downloads);
        back = CleanupSizes.Load(Now.AddMinutes(10))!;
        Assert.False(back.Whole!.ContainsKey(Downloads));
        // Forgetting keeps when the rest were counted: the hour still runs from the first count.
        Assert.Null(CleanupSizes.Load(Now.AddMinutes(61))!.Whole);
    }

    [Fact]
    public void A_file_an_older_version_wrote_gives_its_sizes_and_no_folders()
    {
        // Before folders were kept one by one, every place was a whole size.
        File.WriteAllText(CleanupSizes.File, "{\"At\":" + TimeUtil.ToUnix(Now) + ",\"Sizes\":{\"" + Downloads.Replace(@"\", @"\\") + "\":83886080,\"" + Temp.Replace(@"\", @"\\") + "\":1468006400}}");
        var back = CleanupSizes.Load(Now.AddMinutes(5))!;
        Assert.Equal(80L << 20, back.Whole![Downloads]);
        Assert.Empty(back.Folders);
    }

    [Fact]
    public void A_broken_or_missing_file_means_counting_again()
    {
        File.WriteAllText(CleanupSizes.File, "{ not json");
        Assert.Null(CleanupSizes.Load(Now));
        CleanupSizes.Forget(Downloads); // no throw

        File.Delete(CleanupSizes.File);
        Assert.Null(CleanupSizes.Load(Now));
        CleanupSizes.Forget(Downloads);
        Assert.False(File.Exists(CleanupSizes.File));
    }
}
