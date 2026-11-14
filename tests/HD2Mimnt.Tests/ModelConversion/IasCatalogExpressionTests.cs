using System.Text.RegularExpressions;
using HD2Mimnt.Infrastructure.ModelConversion;
using Xunit;

namespace HD2Mimnt.Tests.ModelConversion;

public class IasCatalogExpressionTests
{
    [Fact]
    public void SmallCatalog_StaysInline()
    {
        var catalog = IasValue.Obj(("version", IasValue.Of(2L)));
        Assert.Equal("{[\"version\"]=2}", IasModelPackager.CatalogExpression(catalog));
    }

    [Fact]
    public void LargeCatalog_SharesRepeatedProfiles()
    {
        var profile = IasValue.Obj(("nodes", IasValue.Of(new List<IasValue>
        {
            IasValue.Obj(("name", IasValue.Of("root"))),
            IasValue.Obj(("name", IasValue.Of("spine")))
        })));
        var catalog = IasValue.Obj(
            ("blob", IasValue.Of(new string('x', 4_300_000))),
            ("a", profile), ("b", profile), ("c", profile));
        var expression = IasModelPackager.CatalogExpression(catalog);
        Assert.StartsWith("(function() local _profiles={", expression);
        Assert.Equal(3, Regex.Matches(expression, "_profiles\\[1\\]").Count);
        Assert.Single(Regex.Matches(expression, "\\[\"name\"\\]=\"root\""));
    }
}
