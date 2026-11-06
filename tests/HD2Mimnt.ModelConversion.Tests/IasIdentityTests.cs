using System.Text;
using HD2Mimnt.ModelConversion.Ias;

namespace HD2Mimnt.ModelConversion.Tests;

/// <summary>
/// IAS 运行时身份：BLAKE2s 摘要与三类 ID 的派生。
/// 这些 ID 决定游戏能否把 <c>*.rigbin</c> 认成某个 Archive 的骨架，
/// 也决定 <c>*.isolation.ini</c> 的文件名能否对上它自己的 <c>isolation_id</c>；
/// 一旦算法漂移，产物看起来「文件都在」却在游戏里完全不生效，因此逐位钉死。
/// </summary>
public sealed class IasIdentityTests
{
    [Theory]
    [InlineData("", 32, "69217a3079908094e11121d042354a7c1f55b6482ca1a51e1b250dfd1ed0eef9")]
    [InlineData("abc", 32, "508c5e8c327c14e2e1a72ba34eeb452f37458b209ed63a294d999b4c86675982")]
    [InlineData("abc", 12, "0e8ac6520eb9581512e372a3")]
    public void Blake2s_MatchesRfc7693Vectors(string input, int digits, string expected)
    {
        Assert.Equal(expected, Convert.ToHexStringLower(Blake2s.Hash(Encoding.UTF8.GetBytes(input), digits)));
    }

    [Fact]
    public void Blake2s_HandlesMultiBlockAndPersonalisation()
    {
        // 128 字节跨两个消息块，最后一块正好满块，不能多压一个空块。
        var data = Encoding.UTF8.GetBytes(new string('a', 128));
        Assert.Equal("3ac477e27353f9019b81694afe60c8049403784f91a58288428ea318bfa82809",
            Convert.ToHexStringLower(Blake2s.Hash(data, 32)));
        // 个性化参数进参数块，摘要必须随之改变。
        Assert.Equal("16ea0a7794e6a305fe7c0ba30e549af8a120248bc1dcad44fb234ed01dcc8fc3",
            Convert.ToHexStringLower(Blake2s.Hash(data, 32, "HD2DfV4"u8)));
    }

    [Fact]
    public void Owned_ReproducesTheShippedRuntimePackIdentities()
    {
        // 取自真实社区配套包 milfy 泳装比基尼 sr-24 的 isolation_id / 文件名。
        const string project = "180965182dfa4459ab4284dfd088329a";
        Assert.Equal("owned_9d2637de639edfa9c55b7045", IasIdentity.Owned(project, 0x082F22454170EFD6));
        Assert.Equal("owned_bacb89bcce5826735c11b595", IasIdentity.Owned(project, 0xB68F33F2CDD058DA));
    }

    [Fact]
    public void Project_IsStableHex32AndAcceptsAnExplicitIdentity()
    {
        var first = IasIdentity.Project("milfy 泳装比基尼");
        Assert.Equal(first, IasIdentity.Project("milfy 泳装比基尼"));
        Assert.NotEqual(first, IasIdentity.Project("milfy 泳装比基尼 2"));
        Assert.Equal(32, first.Length);
        Assert.True(IasIdentity.IsProjectId(first));
        // 已经是 32 位十六进制的项目身份原样保留，便于复刻作者自带的 ID。
        const string explicitId = "180965182dfa4459ab4284dfd088329a";
        Assert.Equal(explicitId, IasIdentity.Project(explicitId.ToUpperInvariant()));
        Assert.False(IasIdentity.IsProjectId("sample"));
        Assert.False(IasIdentity.IsProjectId(explicitId.ToUpperInvariant()));
    }

    [Fact]
    public void Difference_IsHd2FreeLabelOfSixteenHex()
    {
        var id = IasIdentity.Difference(IasIdentity.Project("sample"), 0x08241BA732C97A9D);
        Assert.StartsWith("hd2free_", id);
        Assert.Equal(8 + 16, id.Length);
        Assert.True(id[8..].All(Uri.IsHexDigit));
        Assert.NotEqual(id, IasIdentity.Difference(IasIdentity.Project("sample"), 0x08241BA732C97A9E));
    }
}
