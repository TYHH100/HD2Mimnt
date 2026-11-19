using HD2Mimnt.Application.Search;

namespace HD2Mimnt.Tests.Services;

public class TagSuggestionTests
{
    [Theory]
    [InlineData("weapon @lu", true, "lu", 7)]
    [InlineData("@", true, "", 0)]
    [InlineData("  @hd2", true, "hd2", 2)]
    [InlineData("weapon lu", false, "", 0)]
    [InlineData("", false, "", 0)]
    public void TryGetTrailingToken_ShouldFindOnlyAtWord(string text, bool expected, string token, int start)
    {
        Assert.Equal(expected, TagSuggestion.TryGetTrailingToken(text, out var actualToken, out var actualStart));
        Assert.Equal(token, actualToken);
        Assert.Equal(start, actualStart);
    }

    [Fact]
    public void Rank_ShouldMatchPrefixAndPinyin()
    {
        var tags = new[] { "Lua Mods", "护甲", "HD2IAS", "HD2CL" };

        Assert.Contains("Lua Mods", TagSuggestion.Rank(tags, "lu"));
        Assert.Contains("护甲", TagSuggestion.Rank(tags, "hujia"));
        Assert.Contains("HD2IAS", TagSuggestion.Rank(tags, "hd2"));
        Assert.Equal(2, TagSuggestion.Rank(tags, "hd2").Count);
    }

    [Fact]
    public void Rank_EmptyToken_ShouldReturnSortedTags()
    {
        var tags = new[] { "b", "a", "c" };

        Assert.Equal(new[] { "a", "b", "c" }, TagSuggestion.Rank(tags, ""));
        Assert.Equal(new[] { "a", "b" }, TagSuggestion.Rank(tags, "", limit: 2));
    }

    [Fact]
    public void Apply_ShouldReplaceTrailingToken()
    {
        Assert.Equal("weapon @Lua Mods ", TagSuggestion.Apply("weapon @lu", 7, "Lua Mods"));
        Assert.Equal("@tag ", TagSuggestion.Apply("@x", 0, "tag"));
    }
}
