using Rigsight.Services;

namespace Rigsight.Tests.App;

/// <summary>
/// A drive's scan is saved and read back after a restart (the person's call, 2026-10-24): the same folders, sizes, kinds,
/// flags and biggest files, and a damaged file is passed over, not a crash.
/// </summary>
[Collection("UI")]
public sealed class DriveScansTests
{
    [Theory]
    [InlineData(24, "at 2:35 PM")]
    [InlineData(23, "yesterday at 2:35 PM")]
    [InlineData(21, "on Wed 21 Oct at 2:35 PM")]
    public void An_older_scan_says_its_day_not_only_its_time(int day, string said) =>
        Assert.Equal(said, Rigsight.ViewModels.StorageViewModel.Ago(new DateTime(2026, 10, day, 14, 35, 0), new DateTime(2026, 10, 24, 18, 0, 0)));

    [Fact]
    public async Task A_saved_scan_reads_back_the_same_after_a_restart()
    {
        string old = DriveScans.Folder, folder = Path.Combine(Path.GetTempPath(), $"rigsight-scans-{Guid.NewGuid():N}");
        DriveScans.Folder = folder;
        try
        {
            var root = new FolderNode { Name = "Q:", Path = @"Q:\", Size = 1000, Files = 30 };
            var games = new FolderNode { Name = "Games", Path = @"Q:\Games", Size = 700, Files = 20, Parent = root, NeedsScan = true };
            games.Kinds[2] = 700;
            var loose = new FolderNode { Name = "Files in this folder", Path = @"Q:\", Size = 300, Files = 10, Parent = root, IsBucket = true, IsLooseFiles = true };
            root.Children.AddRange([games, loose]);
            root.Kinds[2] = 700;
            var at = new DateTime(2026, 10, 24, 15, 0, 0);
            await DriveScans.Keep(@"Q:\", new ScanResult
            {
                Root = root, FileCount = 30, Elapsed = TimeSpan.FromSeconds(4),
                LargestFiles = [new(@"Q:\Games\big.pak", 500) { LastWrite = at.AddDays(-400) }],
            }, at);

            DriveScans.Clear(); // as after a restart
            Assert.True(DriveScans.Has("Q"));
            var (back, when) = DriveScans.Get("Q")!.Value;
            Assert.Equal(at, when);
            Assert.Equal(30, back.FileCount);
            Assert.Equal(1000, back.Root.Size);
            var g = back.Root.Children[0];
            Assert.Equal(("Games", @"Q:\Games", 700L, 20L, true, 700L), (g.Name, g.Path, g.Size, g.Files, g.NeedsScan, g.Kinds[2]));
            Assert.Same(back.Root, g.Parent);
            Assert.True(back.Root.Children[1] is { IsBucket: true, IsLooseFiles: true, IsFile: false });
            Assert.Equal((@"Q:\Games\big.pak", 500L, at.AddDays(-400)), (back.LargestFiles[0].Path, back.LargestFiles[0].Size, back.LargestFiles[0].LastWrite));

            // A damaged file: passed over.
            File.WriteAllText(Path.Combine(folder, "R.json.gz"), "not a scan");
            DriveScans.Clear();
            Assert.Null(DriveScans.Get("R"));
            Assert.NotNull(DriveScans.Get("Q"));
        }
        finally
        {
            DriveScans.Folder = old;
            DriveScans.Clear();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
    // File names in the chat without sending them (2026-10-27): the AI reads "file Q1", the answer's "[file Q1]" shows the
    // real name from the scan on this PC, and a label with no such file stays plain words.
    [Fact]
    public async Task A_files_label_in_an_answer_shows_its_name_from_the_scan_here_and_never_goes_out()
    {
        string old = DriveScans.Folder, folder = Path.Combine(Path.GetTempPath(), $"rigsight-scans-{Guid.NewGuid():N}");
        DriveScans.Folder = folder;
        Directory.CreateDirectory(folder);
        string big = Path.Combine(folder, "docker_data.vhdx"), gone = Path.Combine(folder, "old capture.mp4");
        File.WriteAllText(big, "x");
        var oldFileOf = Rigsight.Controls.Md.FileOf;
        try
        {
            var root = new FolderNode { Name = "Q:", Path = @"Q:\", Size = 1000, Files = 2 };
            var result = new ScanResult { Root = root, FileCount = 2, Elapsed = TimeSpan.FromSeconds(1), LargestFiles = [new(big, 900), new(gone, 100)] };
            var at = new DateTime(2026, 10, 27, 13, 5, 0);
            await DriveScans.Keep(@"Q:\", result, at);
            string code = DriveScans.ScanCode(at);
            string sent = DriveScans.Describe("Q", result, at, at, []);
            Assert.Contains($"[file Q1 {code}]: 900 B, a .vhdx file", sent);
            Assert.DoesNotContain("docker_data", sent);
            Assert.DoesNotContain("old capture", sent);

            Rigsight.Controls.Md.FileOf = DriveScans.FileAt;
            string written = $"The biggest is [file Q1 {code}], then [file Q2 {code}], [file Q9 {code}] and [file Q1].";
            var named = Assert.Single(Rigsight.Controls.Md.Parse(written), p => p.File is not null);
            Assert.Equal("docker_data.vhdx", named.Text);
            Assert.Equal(("Q", 900L), (named.File!.Drive, named.File.Size));
            // A deleted file, a number past the list, and a label without its scan's code: plain words.
            Assert.Equal("The biggest is docker_data.vhdx, then a file no longer there, a file from an older scan and a file.", Rigsight.Controls.Md.Plain(written));

            // Rescanned since the answer: the old labels no longer name anything, rather than naming another file.
            await DriveScans.Keep(@"Q:\", result, at.AddMinutes(30));
            Assert.Equal("The biggest is a file from an older scan.", Rigsight.Controls.Md.Plain($"The biggest is [file Q1 {code}]."));

            // The answer keeps the label as the AI wrote it (what goes back to the AI in a follow-up); only drawing names it.
            var answer = Rigsight.ViewModels.AskViewModel.ToAnswer($"The biggest is [file Q1 {code}], Docker's disk.", []);
            Assert.Contains(answer.Parts, p => p is Rigsight.ViewModels.AskLine l && l.Text.Contains($"[file Q1 {code}]", StringComparison.Ordinal));
        }
        finally
        {
            Rigsight.Controls.Md.FileOf = oldFileOf;
            DriveScans.Folder = old;
            DriveScans.Clear();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
