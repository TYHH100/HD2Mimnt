using HD2Mimnt.Application.Search;

namespace HD2Mimnt.Tests.Services;

public class SearchMatcherTests
{
    private static readonly SearchMatchOptions Both = new(Fuzzy: true, Pinyin: true);
    private static readonly SearchMatchOptions None = new(Fuzzy: false, Pinyin: false);

    [Fact]
    public void Match_ContiguousSubstring_ReturnsContains()
    {
        Assert.Equal(SearchMatchKind.Contains, SearchMatcher.Match("Armor Mod Alpha", "mod", None));
        Assert.Equal(SearchMatchKind.Prefix, SearchMatcher.Match("Armor Mod Alpha", "arm", None));
        Assert.Equal(SearchMatchKind.Exact, SearchMatcher.Match("Armor Mod Alpha", "Armor Mod Alpha", None));
    }

    [Fact]
    public void Match_IgnoresSpacesAndPunctuation()
    {
        Assert.Equal(SearchMatchKind.Normalized, SearchMatcher.Match("Armor Mod Alpha", "armormod", None));
    }

    [Fact]
    public void Match_FuzzyDisabled_DoesNotMatchNonAdjacentCharacters()
    {
        Assert.Equal(SearchMatchKind.None, SearchMatcher.Match("Helldivers 2", "hdvrs", None));
    }

    [Fact]
    public void Match_FuzzyEnabled_MatchesNonAdjacentCharacters()
    {
        Assert.Equal(SearchMatchKind.Fuzzy, SearchMatcher.Match("Helldivers 2", "hdvrs", new(true, false)));
    }

    [Fact]
    public void Match_FuzzyDoesNotMatchTwoCharacterQueries()
    {
        Assert.Equal(SearchMatchKind.None, SearchMatcher.Match("aXbYc", "ab", new(true, false)));
    }

    [Fact]
    public void Match_PinyinDisabled_DoesNotMatchChineseByPinyin()
    {
        Assert.Equal(SearchMatchKind.None, SearchMatcher.Match("中文模组", "zhongwen", None));
        Assert.Equal(SearchMatchKind.None, SearchMatcher.Match("中文模组", "zw", None));
    }

    [Fact]
    public void Match_PinyinEnabled_MatchesFullPinyin()
    {
        Assert.Equal(SearchMatchKind.Pinyin, SearchMatcher.Match("中文模组", "zhongwen", new(false, true)));
        Assert.Equal(SearchMatchKind.Pinyin, SearchMatcher.Match("中文模组", "wenmo", new(false, true)));
    }

    [Fact]
    public void Match_PinyinEnabled_MatchesInitials()
    {
        Assert.Equal(SearchMatchKind.Pinyin, SearchMatcher.Match("中文模组", "zw", new(false, true)));
    }

    [Fact]
    public void Match_PinyinEnabled_MatchesPolyphonicReadings()
    {
        Assert.Equal(SearchMatchKind.Pinyin, SearchMatcher.Match("重装模组", "chongzhuang", new(false, true)));
        Assert.Equal(SearchMatchKind.Pinyin, SearchMatcher.Match("重装模组", "zhongzhuang", new(false, true)));
    }

    [Fact]
    public void Match_PinyinEnabled_NormalizesUmlautInput()
    {
        Assert.Equal(SearchMatchKind.Pinyin, SearchMatcher.Match("女猎人", "nv", new(false, true)));
        Assert.Equal(SearchMatchKind.Pinyin, SearchMatcher.Match("女猎人", "nu", new(false, true)));
    }

    [Fact]
    public void Match_PinyinEnabled_MatchesChineseWithLatinSuffix()
    {
        Assert.Equal(SearchMatchKind.Pinyin, SearchMatcher.Match("HD2 中文", "hd2zw", new(false, true)));
    }

    [Fact]
    public void MatchAny_UsesBestMatchingField()
    {
        Assert.Equal(SearchMatchKind.Exact,
            SearchMatcher.MatchAny("武器", Both, "护甲", "武器"));
        Assert.Equal(SearchMatchKind.None,
            SearchMatcher.MatchAny("不存在", Both, "护甲", "武器"));
    }
}
