using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Tests.Services;

public sealed class AppUpdateVersionTests
{
    [Theory]
    [InlineData("0.1.1-alpha", true, 0, 1, 1, "alpha")]
    [InlineData("V0.1.1-alpha", true, 0, 1, 1, "alpha")]
    [InlineData("v1.2.3", true, 1, 2, 3, "")]
    [InlineData("1.2", true, 1, 2, 0, "")]
    [InlineData("1.2.3+build.5", true, 1, 2, 3, "")]
    [InlineData("1.2.3-rc.1+meta", true, 1, 2, 3, "rc.1")]
    [InlineData("1.2.3.4", false, 0, 0, 0, "")]
    [InlineData("alpha", false, 0, 0, 0, "")]
    [InlineData("1.-1", false, 0, 0, 0, "")]
    [InlineData("", false, 0, 0, 0, "")]
    [InlineData(null, false, 0, 0, 0, "")]
    public void TryParse_CoversCommonReleaseForms(string? input, bool expected, int major, int minor, int patch, string prerelease)
    {
        var result = AppUpdateVersion.TryParse(input, out var parts);
        Assert.Equal(expected, result);
        if (expected)
        {
            Assert.Equal(major, parts.Major);
            Assert.Equal(minor, parts.Minor);
            Assert.Equal(patch, parts.Patch);
            Assert.Equal(prerelease, parts.Prerelease);
        }
    }

    [Theory]
    [InlineData("0.1.1-alpha", "0.1.1", true)] // 同号正式版高于预发布
    [InlineData("0.1.1", "0.1.1-alpha", false)]
    [InlineData("0.1.1-alpha", "0.1.1-beta", true)]
    [InlineData("0.1.1-rc.1", "0.1.1-rc.2", true)]
    [InlineData("0.1.1-rc", "0.1.1-rc.1", true)] // 少标识符低于多标识符
    [InlineData("0.1.1-alpha", "0.1.1-alpha.1", true)]
    [InlineData("0.1.1-alpha", "0.1.1-alpha", false)] // 完全相同不算更新
    [InlineData("0.1.1", "0.1.1", false)]
    [InlineData("0.1.1", "0.1.2-alpha", true)] // 更高位比较优先于预发布
    [InlineData("0.1.1", "0.0.9", false)]
    [InlineData("0.1.1", "1.0.0", true)]
    [InlineData("0.1.1-alpha", "V0.1.2-alpha", true)] // V 前缀与当前无前缀混用
    public void IsNewer_ComparesWithSemverPrereleaseRules(string current, string candidate, bool expected)
    {
        Assert.Equal(expected, AppUpdateVersion.IsNewer(current, candidate));
    }

    [Fact]
    public void IsNewer_UnparsableVersions_ReturnsFalse()
    {
        Assert.False(AppUpdateVersion.IsNewer("dev", "0.2.0"));
        Assert.False(AppUpdateVersion.IsNewer("0.1.1", "not-a-version"));
    }

    [Fact]
    public void Format_ProducesCanonicalForm()
    {
        AppUpdateVersion.TryParse("V0.1.11-alpha.2", out var parts);
        Assert.Equal("0.1.11-alpha.2", AppUpdateVersion.Format(parts));
        AppUpdateVersion.TryParse("1.2", out parts);
        Assert.Equal("1.2.0", AppUpdateVersion.Format(parts));
    }
}
