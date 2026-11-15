using Rigsight.Core;
using Rigsight.Core.Ai;

namespace Rigsight.Tests.Core;

/// <summary>Small files people would miss are swapped in whole, with the one before kept, so a cut-off save costs nothing.</summary>
public sealed class SafeFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rigsight-safefile-" + Guid.NewGuid().ToString("N")[..8]);

    public SafeFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_save_swaps_the_file_in_and_keeps_the_one_before()
    {
        string path = Path.Combine(_dir, "a.json");
        SafeFile.WriteText(path, "one");
        Assert.Equal("one", File.ReadAllText(path));
        Assert.False(File.Exists(SafeFile.BackupOf(path)));
        SafeFile.WriteText(path, "two");
        Assert.Equal(("two", "one"), (File.ReadAllText(path), File.ReadAllText(SafeFile.BackupOf(path))));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Without_a_backup_nothing_is_kept_beside_it()
    {
        string path = Path.Combine(_dir, "b.json");
        SafeFile.WriteText(path, "one", backup: false);
        SafeFile.WriteText(path, "two", backup: false);
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal(["b.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Conversations_left_empty_come_back_from_the_save_before()
    {
        string path = Path.Combine(_dir, "conversations.json");
        SavedConversation Talk(string id) => new(id, "Is my PC ok?", DateTime.Now, null, false, false, [new SavedMessage(SavedMessage.Question, "Is my PC ok?")]);
        AiConversations.Save([Talk("a1")], path);
        AiConversations.Save([Talk("a1"), Talk("a2")], path);
        File.WriteAllText(path, "");
        Assert.Equal("a1", Assert.Single(AiConversations.Load(path)).Id);
        Assert.True(File.Exists(path + ".bad"));
    }
}
