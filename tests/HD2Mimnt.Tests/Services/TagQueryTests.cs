using HD2Mimnt.Application.Search;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Tests.Services;

public class TagQueryTests
{
    [Fact]
    public void Parse_ShouldExtractTagsAnywhereAndKeepKeyword()
    {
        var (keyword, tags) = TagQuery.Parse("weapon @DPS优化 @meta");

        Assert.Equal("weapon", keyword);
        Assert.Equal(new[] { "DPS优化", "meta" }, tags);
    }

    [Fact]
    public void Parse_ShouldIgnoreBareAtSign()
    {
        var (keyword, tags) = TagQuery.Parse("@123");

        Assert.Equal(string.Empty, keyword);
        Assert.Equal(new[] { "123" }, tags);
    }

    [Theory]
    [InlineData(new[] { "1", "666" }, new[] { "1" }, true)]
    [InlineData(new[] { "1", "666" }, new[] { "1", "666" }, true)]
    [InlineData(new[] { "1" }, new[] { "1", "666" }, false)]
    [InlineData(new[] { "DPS" }, new[] { "dps" }, true)]
    [InlineData(new[] { "Lua Mods" }, new[] { "lua" }, true)]
    [InlineData(new[] { "视频 BIK2" }, new[] { "视频" }, true)]
    [InlineData(new string[0], new string[0], true)]
    public void MatchesAllTags_ShouldRequireEveryFilter(string[] tags, string[] filters, bool expected)
        => Assert.Equal(expected, TagQuery.MatchesAllTags(tags, filters));

    [Fact]
    public void EnumerateModTags_ShouldIncludeAllSystemCategories()
    {
        var mod = new Mod
        {
            Tags = new List<string> { "HD2CL" },
            UserTags = new List<string> { "mine" },
            WorkflowType = "HD2IAS",
            ResourceTypeIds = new List<ulong> { 0x5ee65304478f8db5UL },
            PlatformLink = new ModPlatformLink("nexusmods", "helldivers2", "1", "https://example.com/",
                Name: "Example"),
            BslRole = BslModRole.Loader
        };

        var tags = TagQuery.EnumerateModTags(mod).ToArray();

        Assert.Contains("HD2CL", tags);
        Assert.Contains("mine", tags);
        Assert.Contains("HD2IAS", tags);
        Assert.Contains("Lua Mods", tags);
        Assert.Contains("视频 BIK2", tags);
        Assert.Contains("Nexus Mods", tags);
        Assert.False(TagQuery.EnumerateModTags(new Mod { WorkflowType = "Legacy" }).Contains("HD2IAS"));
    }
}
