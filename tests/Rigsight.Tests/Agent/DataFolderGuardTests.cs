using System.Diagnostics;
using Rigsight.Agent;

namespace Rigsight.Tests.Agent;

/// <summary>
/// The agent with admin rights works in a folder the person owns: a link swapped in for one of its folders is removed (the
/// link only), and the folders are held so none can be swapped while it runs.
/// </summary>
[Collection("Disks")]
public sealed class DataFolderGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rigsight-guard-" + Guid.NewGuid().ToString("N")[..8]);

    public DataFolderGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        DataFolderGuard.Release();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Its_folders_are_made_and_held_so_they_cant_be_swapped()
    {
        string data = Path.Combine(_root, "Rigsight");
        var notes = DataFolderGuard.Secure(data);
        Assert.NotNull(notes);
        Assert.Empty(notes);
        Assert.All(DataFolderGuard.Folders, name => Assert.True(Directory.Exists(Path.Combine(data, name))));
        // Held: renamed away (to put a link in its place), it can't be.
        Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(data, "updates"), Path.Combine(data, "updates-away")));
        Assert.ThrowsAny<IOException>(() => Directory.Move(data, data + "-away"));
        // Files in them come and go as before.
        string file = Path.Combine(data, "updates", "now");
        File.WriteAllText(file, "");
        File.Delete(file);
    }

    [Fact]
    public void A_link_in_place_of_one_of_its_folders_is_removed_and_what_it_pointed_at_is_untouched()
    {
        string data = Path.Combine(_root, "Rigsight"), elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "mine");
        Junction(Path.Combine(data, "updates"), elsewhere);

        var notes = DataFolderGuard.Secure(data);
        Assert.NotNull(notes);
        Assert.Contains(notes, n => n.Contains("\"updates\"", StringComparison.Ordinal));
        Assert.False(new DirectoryInfo(Path.Combine(data, "updates")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(elsewhere, "keep.txt")));
    }

    [Theory]
    [InlineData(@"D:\Data\Rigsight", true)]
    [InlineData(@"\RPC Control", false)]
    [InlineData(@"\??\GLOBALROOT\Device", false)]
    [InlineData(null, false)]
    public void A_data_folder_moved_to_another_drive_is_fine_a_link_into_anything_else_isnt(string? target, bool fine) =>
        Assert.Equal(fine, DataFolderGuard.IsDrivePath(target));

    /// <summary>A junction (no admin rights needed), as an attacker running as the person could make.</summary>
    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
