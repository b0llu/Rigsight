using Rigsight.Core;
using Rigsight.Tests.Core;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// Settings › Language's list: each language with its download size until it's downloaded. One test at a time with
/// <see cref="LanguageStoreTests"/>: both put packs in this run's languages folder and take them out again.
/// </summary>
[Collection("LanguageStore")]
public sealed class LanguagePickerTests : IDisposable
{
    public LanguagePickerTests() => LanguageStore.Remove("es");

    public void Dispose() => LanguageStore.Remove("es");

    private static void Download()
    {
        Directory.CreateDirectory(LanguageStore.Folder);
        File.Copy(Path.Combine(Repo.Root, "languages", "es.json"), LanguageStore.PackPath("es"), overwrite: true);
    }

    private static string NoteOf(IReadOnlyList<LanguageOption> options, string code) => options.Single(o => o.Code == code).Note;

    [Fact]
    public void A_language_shows_its_size_until_it_is_downloaded()
    {
        var settings = Kit.OfflineSettings();
        using var _ = Lang.UseHere(null);
        Assert.Equal("", NoteOf(new LanguageViewModel(settings).Options, LanguagePack.English));
        Assert.EndsWith(" KB", NoteOf(new LanguageViewModel(settings).Options, "es"));
        Download();
        Assert.Equal("", NoteOf(new LanguageViewModel(settings).Options, "es"));
    }

    /// <summary>
    /// The language in use was read as RigSight opened, so the list doesn't read its pack again. It did on every first
    /// visit of Settings in Spanish: the whole pack read and checked on the window's thread, 11 MB of short-lived
    /// memory each time (2026-11-11).
    /// </summary>
    [Fact]
    public void The_list_does_not_read_the_language_in_use_again()
    {
        Download();
        var spanish = LanguageStore.Find("es")!;
        var settings = Kit.OfflineSettings();
        using var _ = Lang.UseHere(spanish);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var options = new LanguageViewModel(settings).Options;
        double kb = (GC.GetAllocatedBytesForCurrentThread() - before) / 1024.0;

        Assert.True(options.Single(o => o.Code == "es").IsCurrent);
        Assert.Equal("", NoteOf(options, "es"));
        Assert.True(kb < 256, $"Building the language list took {kb:0} KB: the pack in use was read again");
    }
}
