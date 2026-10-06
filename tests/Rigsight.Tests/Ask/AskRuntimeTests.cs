using Rigsight.Services;
using Rigsight.Tests.Support;

namespace Rigsight.Tests.Ask;

/// <summary>The model is only loaded where the Microsoft runtime it needs is there: checked first, never found out by crashing.</summary>
public sealed class AskRuntimeTests
{
    [Fact]
    public void This_pc_has_what_the_model_needs() => Assert.True(AskEmbedder.RuntimeReady && AskEmbedder.Available);

    [Fact]
    public void A_pc_without_the_runtime_or_with_part_of_it_does_not_load_the_model()
    {
        string empty = TestEnvironment.NewFolder("no-vcrt");
        Assert.False(AskEmbedder.CheckRuntime(empty));

        // Three of the four files: still no.
        string system = Environment.SystemDirectory;
        foreach (string name in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
            File.Copy(Path.Combine(system, name), Path.Combine(empty, name));
        Assert.False(AskEmbedder.CheckRuntime(empty));
        File.Copy(Path.Combine(system, "msvcp140_1.dll"), Path.Combine(empty, "msvcp140_1.dll"));
        Assert.True(AskEmbedder.CheckRuntime(empty));

        // A file that isn't a program at all (no version to read) counts as too old.
        File.WriteAllText(Path.Combine(empty, "msvcp140.dll"), "not a dll");
        Assert.False(AskEmbedder.CheckRuntime(empty));
        Assert.False(AskEmbedder.CheckRuntime(Path.Combine(empty, "nowhere")));
    }
}
