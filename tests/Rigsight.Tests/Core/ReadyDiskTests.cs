using Rigsight.Core.Apps;

namespace Rigsight.Tests.Core;

/// <summary>
/// Programs on the network or on a drive that isn't there are never looked at: Windows can wait seconds on each, and
/// the agent looks at every program it knows when it starts. Same collection as the engine tests, which read real drives.
/// </summary>
[Collection("Disks")]
public sealed class ReadyDiskTests : IDisposable
{
    private readonly Func<string, bool> _was = ReadyDisk.ReadDrive;
    private readonly List<string> _asked = [];

    public ReadyDiskTests()
    {
        ReadyDisk.Forget();
        ReadyDisk.ReadDrive = root => { _asked.Add(root); return root.StartsWith("C", StringComparison.OrdinalIgnoreCase); };
    }

    public void Dispose()
    {
        ReadyDisk.ReadDrive = _was;
        ReadyDisk.Forget();
    }

    [Fact]
    public void Network_paths_are_never_looked_at()
    {
        Assert.False(ReadyDisk.Holds(@"\\nas\games\Golf\Golf.exe"));
        Assert.False(ReadyDisk.Holds(@"\\?\UNC\nas\games\Golf.exe"));
        Assert.Empty(_asked);
    }

    [Fact]
    public void A_drive_that_isnt_there_is_skipped_and_asked_once_a_minute()
    {
        Assert.True(ReadyDisk.Holds(@"C:\Games\Golf\Golf.exe"));
        Assert.False(ReadyDisk.Holds(@"E:\Games\Old\Old.exe"));
        Assert.False(ReadyDisk.Holds(@"E:\Games\Other\Other.exe"));
        Assert.Equal([@"C:\", @"E:\"], _asked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Golf.exe")]
    [InlineData(@"Games\Golf.exe")]
    public void A_path_that_isnt_a_full_one_isnt_looked_at(string? path) => Assert.False(ReadyDisk.Holds(path));

    [Fact]
    public void A_game_on_a_missing_drive_isnt_taken_for_one_without_an_engine_for_good()
    {
        // Not looked at while the drive is away, and not remembered, so it's found once the drive is back.
        var dir = Path.Combine(Path.GetTempPath(), "rigsight-tests", "ready-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "Golf.exe");
            File.WriteAllText(exe, "");
            File.WriteAllText(Path.Combine(dir, "UnityPlayer.dll"), "");
            ReadyDisk.ReadDrive = _ => false;
            Assert.Null(AppCatalog.EngineOf("Golf.exe", exe));
            ReadyDisk.Forget();
            ReadyDisk.ReadDrive = _ => true;
            Assert.Equal("Unity", AppCatalog.EngineOf("Golf.exe", exe));
        }
        finally { Directory.Delete(dir, true); }
    }
}
