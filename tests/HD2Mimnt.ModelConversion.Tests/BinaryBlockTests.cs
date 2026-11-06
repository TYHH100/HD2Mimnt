using System.Text;
using HD2Mimnt.ModelConversion.Source;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class BinaryBlockTests
{
    [Theory]
    [InlineData(-1L, 0L)]
    [InlineData(0L, -1L)]
    [InlineData(9L, 0L)]
    [InlineData(8L, 1L)]
    [InlineData(1L, long.MaxValue)]
    [InlineData(long.MaxValue, 1L)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void Check_InvalidOrOverflowingRange_ThrowsSourceFormatException(long offset, long size)
    {
        var block = new BinaryBlock(new byte[8], "fixture");
        Assert.Throws<SourceFormatException>(() => block.Check(offset, size, "member"));
    }

    [Theory]
    [InlineData(0L, 8L)]
    [InlineData(8L, 0L)]
    [InlineData(0L, 0L)]
    [InlineData(7L, 1L)]
    public void Check_RangeInsideOrEmptyAtEnd_IsAccepted(long offset, long size)
    {
        var block = new BinaryBlock(new byte[8], "fixture");
        block.Check(offset, size, "member");
    }

    [Fact]
    public void ScalarAndSliceReads_InvalidRange_ThrowSourceFormatException()
    {
        var block = new BinaryBlock(new byte[8], "fixture");
        Assert.Throws<SourceFormatException>(() => block.Slice(int.MaxValue, int.MaxValue, "member").ToArray());
        Assert.Throws<SourceFormatException>(() => block.U8(8));
        Assert.Throws<SourceFormatException>(() => block.I32(6));
        Assert.Throws<SourceFormatException>(() => block.Floats(0, int.MaxValue, "floats"));
        Assert.Throws<SourceFormatException>(() => block.Floats(0, -1, "floats"));
    }

    [Fact]
    public void CString_TerminatedUtf8AndEmptyString_ReadWithinLimit()
    {
        var block = new BinaryBlock(Encoding.UTF8.GetBytes("\0材质\0extra"), "fixture");
        Assert.Equal(string.Empty, block.CString(0, 1));
        Assert.Equal("材质", block.CString(1, 7));
    }

    [Fact]
    public void CString_UnterminatedAtFileEndOrLimit_IsRejected()
    {
        var block = new BinaryBlock("name\0"u8.ToArray(), "fixture");
        Assert.Equal("name", block.CString(0, 5));
        Assert.Throws<SourceFormatException>(() => block.CString(0, 4));
        Assert.Throws<SourceFormatException>(() => new BinaryBlock("name"u8.ToArray(), "fixture").CString(0));
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(4, 4)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    public void CString_InvalidOffsetOrLimit_IsRejected(int offset, int limit)
    {
        var block = new BinaryBlock(new byte[4], "fixture");
        Assert.Throws<SourceFormatException>(() => block.CString(offset, limit));
    }

    [Fact]
    public void FixedString_FullInlineField_DoesNotRequireTerminator()
    {
        var name = new string('a', 64);
        var block = new BinaryBlock(Encoding.UTF8.GetBytes(name + "suffix"), "fixture");
        Assert.Equal(name, block.FixedString(0, 64));
        Assert.Throws<SourceFormatException>(() => block.CString(0, 64));
    }

    [Fact]
    public void FixedString_TerminatorAndBounds_RespectEntireField()
    {
        var block = new BinaryBlock("name\0xyz"u8.ToArray(), "fixture");
        Assert.Equal("name", block.FixedString(0, 8));
        Assert.Equal(string.Empty, block.FixedString(8, 0));
        Assert.Throws<SourceFormatException>(() => block.FixedString(0, 9));
        Assert.Throws<SourceFormatException>(() => block.FixedString(0, -1));
        Assert.Throws<SourceFormatException>(() => block.FixedString(int.MaxValue, 2));
    }

    [Theory]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(int.MinValue, int.MinValue + 4)]
    [InlineData(1, -2)]
    [InlineData(1, 7)]
    public void RelativeCString_OutsideOrWrappingPointer_IsRejected(int structOffset, int relativeOffset)
    {
        var block = new BinaryBlock("abc\0ok\0\0"u8.ToArray(), "fixture");
        Assert.Throws<SourceFormatException>(() => block.RelativeCString(structOffset, relativeOffset, "fallback"));
    }

    [Fact]
    public void RelativeCString_ValidPointerAndFallback_ArePreserved()
    {
        var block = new BinaryBlock("\0name\0"u8.ToArray(), "fixture");
        Assert.Equal("name", block.RelativeCString(0, 1, "fallback"));
        Assert.Equal("name", block.RelativeCString(3, -2, "fallback"));
        Assert.Equal("fallback", block.RelativeCString(0, 0, "fallback"));
        Assert.Equal("fallback", block.RelativeCString(1, 4, "fallback"));
    }

    [Fact]
    public void RelativeCString_UnterminatedTarget_IsRejected()
    {
        var block = new BinaryBlock("\0name"u8.ToArray(), "fixture");
        Assert.Throws<SourceFormatException>(() => block.RelativeCString(0, 1, "fallback"));
    }
}
