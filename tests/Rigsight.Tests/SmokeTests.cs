using Rigsight.Core;
using Rigsight.Tests.Support;

namespace Rigsight.Tests;

public class SmokeTests
{
    [Fact]
    public void Data_goes_to_the_test_folder_never_the_real_one()
    {
        Assert.Equal(TestEnvironment.DataDir, RigsightPaths.DataDir);
        Assert.True(RigsightPaths.IsTestInstance);
        Assert.NotEqual("Rigsight.Agent.v1", RigsightPaths.PipeName);
        Assert.StartsWith("Rigsight.Agent.v1.", RigsightPaths.PipeName);
    }
}
