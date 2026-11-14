using HD2Mimnt.Infrastructure.ModelConversion;
using Xunit;

namespace HD2Mimnt.Tests.ModelConversion;

public class IasPreviewModuleTests
{
    [Theory]
    [InlineData("mods/hd2qm/ias_abc/v1/catalog", "mods/hd2qm/ias_abc/v1/preview")]
    [InlineData("mods/hd2qm/ias_abc/other", "mods/hd2qm/ias_abc/preview")]
    [InlineData("catalog", "preview")]
    public void PreviewModule_IsSiblingOfCatalog(string catalog, string expected)
        => Assert.Equal(expected, IasModelPackager.PreviewModule(catalog));
}
