using HD2Mimnt.Infrastructure.ModelConversion;
using Xunit;

namespace HD2Mimnt.Tests.ModelConversion;

public class IasBinaryLayerTests
{
    [Theory]
    [InlineData("bones", 1792059921637536489UL, 417246465u)]
    [InlineData("render_config", 2848016463268491676UL, 663105506u)]
    [InlineData("lua", 11623383793923069922UL, 2706279930u)]
    [InlineData("StingrayEntityRoot", 5339060518727939630UL, 1243096897u)]
    [InlineData("head", 10112112548227665684UL, 2354409673u)]
    [InlineData("left_hand", 12403071216512415938UL, 2887815054u)]
    public void Murmur_MatchesUpstreamReference(string value, ulong expected64, uint expected32)
    {
        Assert.Equal(expected64, IasHash.Murmur64(value));
        Assert.Equal(expected32, IasHash.Murmur32(value));
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(100.0, "100.0")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e-4, "0.0001")]
    [InlineData(1e-5, "1e-05")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1.25, "1.25")]
    public void PythonFloat_MatchesPythonRepr(double value, string expected)
        => Assert.Equal(expected, PythonFloat.Repr(value));

    [Fact]
    public void LuaLiteral_MatchesUpstream()
    {
        var value = IasValue.Obj(
            ("a", IasValue.Of(1L)),
            ("b", IasValue.Of([IasValue.Of(1.0), IasValue.Of(2L), IasValue.Of("x")])),
            ("c", IasValue.Of(true)),
            ("d", IasValue.Nil.Instance));
        Assert.Equal("{[\"a\"]=1,[\"b\"]={1.0,2,\"x\"},[\"c\"]=true,[\"d\"]=nil}", IasLua.Literal(value));
    }

    [Fact]
    public void CanonicalJson_MatchesUpstream()
    {
        var value = IasValue.Obj(
            ("b", IasValue.Of(2L)),
            ("a", IasValue.Of("x")),
            ("c", IasValue.Of([IasValue.Of(1L), IasValue.Of(true)])));
        Assert.Equal("{\"a\":\"x\",\"b\":2,\"c\":[1,true]}", System.Text.Encoding.ASCII.GetString(IasLua.JsonBytes(value)));
    }

    [Fact]
    public void Json_EscapesNonAsciiLikePython()
    {
        Assert.Equal("\"\\u6a21\"", IasLua.QuoteJson("模", ensureAscii: true));
        Assert.Equal("\"模\"", IasLua.QuoteJson("模", ensureAscii: false));
    }

    [Fact]
    public void ArchiveWrite_MatchesUpstreamByteLayout()
    {
        var entries = new Dictionary<(ulong Type, ulong Id), IasResource>
        {
            [(1, 2)] = new([0x41, 0x42, 0x43, 0x44], [0x47, 0x47], []),
            [(3, 4)] = new([0x57, 0x58, 0x59, 0x5A], [], [0x53, 0x53])
        };
        var buffers = IasArchive.Write(entries);
        Assert.Equal(324, buffers.Toc.Length);
        Assert.Equal(64, buffers.Gpu.Length);
        Assert.Equal(2, buffers.Stream.Length);
        var tocHex = Convert.ToHexString(buffers.Toc).ToLowerInvariant();
        Assert.Equal(
            "0200000000000000010000000000000030010000000000000000000000000000000000000000000000000000000000000000000000000000040000000000000002000000100000004000000001000000",
            tocHex.Substring((72 + 64) * 2, 80 * 2));
        Assert.Equal(
            "0400000000000000030000000000000040010000000000000000000000000000000000000000000000000000000000000000000000000000040000000200000000000000100000004000000002000000",
            tocHex.Substring((72 + 64 + 80) * 2, 80 * 2));
        Assert.Equal(
            "110000f0020000000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000100000000000000010000000000000010000000400000000000000000000000030000000000000001000000000000001000000040000000020000000000000001000000000000003001000000000000000000000000000000000000000000000000000000000000000000000000000004000000000000000200000010000000400000000100000004000000000000000300000000000000400100000000000000000000000000000000000000000000000000000000000000000000000000000400000002000000000000001000000040000000020000000000000000000000414243440000000000000000000000005758595a",
            tocHex);
        Assert.Equal("4747" + new string('0', 124), Convert.ToHexString(buffers.Gpu).ToLowerInvariant());
        Assert.Equal("5353", Convert.ToHexString(buffers.Stream).ToLowerInvariant());

        var rows = IasArchive.ReadRows(buffers.Toc);
        Assert.Equal(new IasArchiveRow(2, 1, 304, 0, 0, 0, 0, 4, 0, 2, 16, 64, 1), rows[(1, 2)]);
        Assert.Equal(new IasArchiveRow(4, 3, 320, 0, 0, 0, 0, 4, 2, 0, 16, 64, 2), rows[(3, 4)]);
    }

    [Fact]
    public void ArchiveReadWrite_RoundTripsPayloads()
    {
        var entries = new Dictionary<(ulong Type, ulong Id), IasResource>
        {
            [(IasResourceTypes.Unit, 7)] = new([1, 2, 3], [9, 8, 7, 6], [], [1, 2, 16, 64]),
            [(IasResourceTypes.Lua, 9)] = new([4, 5, 6], [], [3, 2, 1], [0, 0, 16, 64])
        };
        var buffers = IasArchive.Write(entries);
        var parsed = IasArchive.ReadEntries(buffers.Toc, buffers.Gpu, buffers.Stream);
        Assert.Equal(entries[(IasResourceTypes.Unit, 7)].Toc, parsed[(IasResourceTypes.Unit, 7)].Toc);
        Assert.Equal(entries[(IasResourceTypes.Unit, 7)].Gpu, parsed[(IasResourceTypes.Unit, 7)].Gpu);
        Assert.Equal(entries[(IasResourceTypes.Lua, 9)].Toc, parsed[(IasResourceTypes.Lua, 9)].Toc);
        Assert.Equal(entries[(IasResourceTypes.Lua, 9)].Stream, parsed[(IasResourceTypes.Lua, 9)].Stream);
        Assert.Equal(new[] { 1, 2, 16, 64 }, parsed[(IasResourceTypes.Unit, 7)].Metadata);
    }

    [Fact]
    public void Affine_InverseComposesToIdentity()
    {
        var matrix = IasAffine.FromColumnMatrix([2, 0, 0, 0, 0, 3, 0, 0, 0, 0, 4, 0, 10, 20, 30, 1]);
        var product = IasAffine.Product(matrix, IasAffine.Inverse(matrix));
        var expected = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };
        for (var index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], product[index], 12);
    }

    [Fact]
    public void Pose_IdentityProducesUnitQuaternion()
    {
        var pose = IasAffine.Pose(IasAffine.FromColumnMatrix([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 1, 2, 3, 1]));
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 1.0 }, pose.Q);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, pose.P);
    }
}
