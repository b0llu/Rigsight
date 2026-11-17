using Rigsight.Core.Ai;
using Rigsight.Core.Settings;
using Rigsight.Services;
using Rigsight.ViewModels;

namespace Rigsight.Tests.App;

/// <summary>
/// get_drive_scan says the Storage page's "Space you could free" cards with their sizes, from the page's own calculation
/// (audit 2026-11-17): "there's no 'unchanged for a year' group in it" of a card the person saw (Q072), and "I can't
/// confirm the 4.2 GB figure" of Old downloads on a drive not scanned (Q076).
/// </summary>
[Collection("UI")]
public sealed class AiStorageCardsTests
{
    private const long Gb = 1L << 30;
    private static readonly DateTime Now = DateTime.Now;

    /// <summary>Eight 10 GB files changed this month, and two of 3.3 GB not changed since March 2024: below the 8 biggest.</summary>
    private static ScanResult Scan() => new()
    {
        Root = new FolderNode { Name = "Q:", Path = @"Q:\", Size = 100 * Gb, Files = 10 },
        FileCount = 10, Elapsed = TimeSpan.FromSeconds(3),
        LargestFiles =
        [
            .. Enumerable.Range(1, 8).Select(i => new LargeFile($@"Q:\Games\pack{i}.pak", 10 * Gb) { LastWrite = Now.AddDays(-3) }),
            new LargeFile(@"Q:\Old\backup1.bak", 33 * Gb / 10) { LastWrite = new DateTime(2024, 3, 2) },
            new LargeFile(@"Q:\Old\backup2.bak", 33 * Gb / 10) { LastWrite = new DateTime(2024, 3, 9) },
        ],
    };

    [Fact]
    public void The_page_and_the_AI_have_the_same_unchanged_for_a_year_card()
    {
        string old = DriveScans.Folder, folder = Path.Combine(Path.GetTempPath(), $"rigsight-cards-{Guid.NewGuid():N}");
        DriveScans.Folder = folder;
        try
        {
            var scan = Scan();
            var card = Assert.Single(StorageViewModel.ItemsOn(@"Q:\", false, [], null, scan, Now));
            Assert.Equal(("Big files unchanged for a year", "2 files · from the scan", 66 * Gb / 10), (card.Title, card.Description, card.Size));

            DriveScans.Keep(@"Q:\", scan, Now).Wait();
            string said = DriveScans.Text("Q")!;
            string code = DriveScans.ScanCode(Now);

            Assert.Contains($"  Big files unchanged for a year: {Rigsight.Core.Units.Bytes(card.Size)}. 2 files among the scan's biggest files, not changed in over a year", said);
            Assert.Contains("take a card's group and size as true", said);
            // Both files the card counts have a label, though neither is among the 8 biggest.
            Assert.Contains($"[file Q9 {code}]: 3.3 GB, a .bak file in Q:\\Old, not changed since Mar 2024", said);
            Assert.Contains($"[file Q10 {code}]: 3.3 GB, a .bak file in Q:\\Old, not changed since Mar 2024", said);
            Assert.Equal("backup1.bak", DriveScans.FileAt("Q", 9, code)?.Name);
        }
        finally
        {
            DriveScans.Folder = old;
            DriveScans.Clear();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_drive_not_scanned_still_says_the_cards_the_page_shows_for_it()
    {
        var before = DriveScans.CleanupOn;
        DriveScans.CleanupOn = letter => letter == "C" ? [("Old downloads", "Files in Downloads you haven't touched in 90+ days.", 42 * Gb / 10)] : [];
        try
        {
            Assert.Null(DriveScans.CleanupText("D"));
            var tools = new AiTools(new RigsightSettings()) { DriveScan = _ => null, DriveCleanup = DriveScans.CleanupText };

            string said = tools.Run("get_drive_scan", """{"drive":"C"}""");

            Assert.StartsWith("C: hasn't been scanned", said);
            Assert.Contains("Space that could be freed on C: (as RigSight's Storage page lists it, without a scan; all 4.2 GB)", said);
            Assert.Contains("  Old downloads: 4.2 GB. Files in Downloads you haven't touched in 90+ days.", said);
            Assert.Contains("[do: scan C]", said);
        }
        finally { DriveScans.CleanupOn = before; }
    }
}
