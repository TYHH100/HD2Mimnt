using HD2Mimnt.Core;

namespace HD2Mimnt.Tests;

public class RuntimeDataPathsTests
{
    [Fact]
    public void GetDefaultApplicationDataPath_UsesBuildConfigurationDirectory()
    {
#if DEBUG
        const string expectedDirectoryName = "DarktideMimnt.dev";
#else
        const string expectedDirectoryName = "DarktideMimnt";
#endif

        Assert.Equal(expectedDirectoryName, Path.GetFileName(RuntimeDataPaths.GetDefaultApplicationDataPath()));
    }
}
