using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Tests.ModelPreview;

public class DxgiTextureFormatsTests
{
    [Theory]
    [InlineData(71, PreviewTextureFormat.Bc1)]
    [InlineData(72, PreviewTextureFormat.Bc1)]
    [InlineData(74, PreviewTextureFormat.Bc2)]
    [InlineData(77, PreviewTextureFormat.Bc3)]
    [InlineData(80, PreviewTextureFormat.Bc4)]
    [InlineData(81, PreviewTextureFormat.Bc4)]
    [InlineData(83, PreviewTextureFormat.Bc5)]
    [InlineData(98, PreviewTextureFormat.Bc7)]
    [InlineData(99, PreviewTextureFormat.Bc7)]
    [InlineData(95, PreviewTextureFormat.Bc6h)]
    [InlineData(28, PreviewTextureFormat.Rgba8)]
    [InlineData(31, PreviewTextureFormat.Rgba8Snorm)]
    [InlineData(87, PreviewTextureFormat.Bgra8)]
    [InlineData(10, PreviewTextureFormat.Rgba16Half)]
    [InlineData(2, PreviewTextureFormat.Rgba32Float)]
    [InlineData(24, PreviewTextureFormat.Rgb10A2)]
    [InlineData(26, PreviewTextureFormat.R11G11B10Float)]
    [InlineData(67, PreviewTextureFormat.Rgb9E5)]
    [InlineData(61, PreviewTextureFormat.R8)]
    [InlineData(49, PreviewTextureFormat.R8G8)]
    [InlineData(56, PreviewTextureFormat.R16)]
    [InlineData(34, PreviewTextureFormat.R16G16Half)]
    [InlineData(65, PreviewTextureFormat.A8)]
    [InlineData(85, PreviewTextureFormat.B5g6r5)]
    [InlineData(115, PreviewTextureFormat.B4g4r4a4)]
    [InlineData(0, PreviewTextureFormat.Unknown)]
    [InlineData(23, PreviewTextureFormat.Unknown)]
    [InlineData(999, PreviewTextureFormat.Unknown)]
    public void FromDxgiFormat_MapsCanonicalValues(int dxgiFormat, PreviewTextureFormat expected) =>
        Assert.Equal(expected, DxgiTextureFormats.FromDxgiFormat(dxgiFormat));

    [Theory]
    [InlineData(PreviewTextureFormat.Bc1, 8)]
    [InlineData(PreviewTextureFormat.Bc4, 8)]
    [InlineData(PreviewTextureFormat.Bc7, 16)]
    [InlineData(PreviewTextureFormat.Bc5, 16)]
    [InlineData(PreviewTextureFormat.Rgba8, 4)]
    [InlineData(PreviewTextureFormat.R8, 1)]
    [InlineData(PreviewTextureFormat.R8G8, 2)]
    [InlineData(PreviewTextureFormat.Rgba16Half, 8)]
    [InlineData(PreviewTextureFormat.Rgba32Float, 16)]
    [InlineData(PreviewTextureFormat.Unknown, 0)]
    [InlineData(PreviewTextureFormat.Bc6h, 0)]
    public void GetBlockByteCount_CompressedIsPerBlockRawIsPerPixel(PreviewTextureFormat format, int expected) =>
        Assert.Equal(expected, DxgiTextureFormats.GetBlockByteCount(format));

    [Fact]
    public void TryDecodeRawToBgra_Rgba8_SwapsToBgra()
    {
        byte[] source = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        var bgra = new byte[8];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 2, 1, PreviewTextureFormat.Rgba8, bgra));
        Assert.Equal(new byte[] { 0x03, 0x02, 0x01, 0x04, 0x07, 0x06, 0x05, 0x08 }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_Rgba8Snorm_RecentersSignedRange()
    {
        // -128 → 0，0 → 128，127 → 255：(v+128) 即 (v ^ 0x80)。
        byte[] source = [0x80, 0x00, 0x7F, 0x80];
        var bgra = new byte[4];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 1, 1, PreviewTextureFormat.Rgba8Snorm, bgra));
        Assert.Equal(new byte[] { 0xFF, 0x80, 0x00, 0x00 }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_Bgra8X_ForcesOpaqueAlpha()
    {
        byte[] source = [10, 20, 30, 0];
        var bgra = new byte[4];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 1, 1, PreviewTextureFormat.Bgra8X, bgra));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_R8_ShowsGray()
    {
        byte[] source = [0x40, 0x80];
        var bgra = new byte[8];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 2, 1, PreviewTextureFormat.R8, bgra));
        Assert.Equal(new byte[] { 0x40, 0x40, 0x40, 255, 0x80, 0x80, 0x80, 255 }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_R8G8_KeepsChannelsAndDropsBlue()
    {
        byte[] source = [0x40, 0x80];
        var bgra = new byte[4];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 1, 1, PreviewTextureFormat.R8G8, bgra));
        Assert.Equal(new byte[] { 0x00, 0x80, 0x40, 255 }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_Rgba16_TruncatesToHighByte()
    {
        var source = new byte[16];
        // R=0x1234 G=0x5678 B=0x9ABC A=0xFFFF
        BitConverter.GetBytes((ushort)0x1234).CopyTo(source, 0);
        BitConverter.GetBytes((ushort)0x5678).CopyTo(source, 2);
        BitConverter.GetBytes((ushort)0x9ABC).CopyTo(source, 4);
        BitConverter.GetBytes(ushort.MaxValue).CopyTo(source, 6);
        var bgra = new byte[4];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 1, 1, PreviewTextureFormat.Rgba16, bgra));
        Assert.Equal(new byte[] { 0x9A, 0x56, 0x12, 0xFF }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_Rgba16Half_ConvertsKnownHalves()
    {
        var source = new byte[8];
        // R=1.0 G=0.5 B=0.0 A=1.0（half 位型：0x3C00=1.0, 0x3800=0.5）
        BitConverter.GetBytes((ushort)0x3C00).CopyTo(source, 0);
        BitConverter.GetBytes((ushort)0x3800).CopyTo(source, 2);
        BitConverter.GetBytes((ushort)0x0000).CopyTo(source, 4);
        BitConverter.GetBytes((ushort)0x3C00).CopyTo(source, 6);
        var bgra = new byte[4];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 1, 1, PreviewTextureFormat.Rgba16Half, bgra));
        Assert.Equal(new byte[] { 0, 128, 255, 255 }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_Rgba32Float_ClampsAndScales()
    {
        var source = new byte[16];
        BitConverter.GetBytes(1.0f).CopyTo(source, 0);
        BitConverter.GetBytes(0.25f).CopyTo(source, 4);
        BitConverter.GetBytes(2.0f).CopyTo(source, 8);
        BitConverter.GetBytes(-1.0f).CopyTo(source, 12);
        var bgra = new byte[4];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 1, 1, PreviewTextureFormat.Rgba32Float, bgra));
        Assert.Equal(new byte[] { 255, 64, 255, 0 }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_Rgb10A2_UnpacksPackedFields()
    {
        // R=1023 G=512 B=1 A=3
        var v = 1023u | 512u << 10 | 1u << 20 | 3u << 30;
        var source = BitConverter.GetBytes(v);
        var bgra = new byte[4];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 1, 1, PreviewTextureFormat.Rgb10A2, bgra));
        Assert.Equal(new byte[] { 0, 128, 255, 255 }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_R11G11B10Float_DecodesStandardBitLayout()
    {
        // 0.5 在 5 位指数浮点里是 exp=14：R11/G11 位型 = 14<<6，B10 位型 = 14<<5。
        const uint r11 = 14u << 6;
        const uint g11 = 15u << 6; // 1.0
        const uint b10 = 14u << 5; // 0.5
        var source = BitConverter.GetBytes(r11 << 21 | g11 << 10 | b10);
        var bgra = new byte[4];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 1, 1, PreviewTextureFormat.R11G11B10Float, bgra));
        Assert.Equal(new byte[] { 128, 255, 128, 255 }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_Rgb9E5_AppliesSharedExponent()
    {
        // mantissa=256、exponent=15 → 值 = 256 * 2^(15-24) = 0.5 → 128。
        var source = BitConverter.GetBytes(256u << 18 | 15u << 27);
        var bgra = new byte[4];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 1, 1, PreviewTextureFormat.Rgb9E5, bgra));
        Assert.Equal(new byte[] { 0, 0, 128, 255 }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_A8_KeepsOnlyAlpha()
    {
        byte[] source = [0x7F];
        var bgra = new byte[4];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 1, 1, PreviewTextureFormat.A8, bgra));
        Assert.Equal(new byte[] { 0, 0, 0, 0x7F }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_B5g6r5_ExpandsChannelScales()
    {
        var source = BitConverter.GetBytes((ushort)(0x1F << 11 | 0x3F << 5 | 0x1F));
        var bgra = new byte[4];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 1, 1, PreviewTextureFormat.B5g6r5, bgra));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, bgra);
    }

    [Fact]
    public void TryDecodeRawToBgra_B4g4r4a4_ExpandsNibbles()
    {
        var source = BitConverter.GetBytes((ushort)(0xA << 12 | 0x5 << 8 | 0x3 << 4 | 0x1));
        var bgra = new byte[4];
        Assert.True(DxgiTextureFormats.TryDecodeRawToBgra(source, 1, 1, PreviewTextureFormat.B4g4r4a4, bgra));
        Assert.Equal(new byte[] { 17, 51, 85, 170 }, bgra);
    }

    [Theory]
    [InlineData(PreviewTextureFormat.Bc7)]
    [InlineData(PreviewTextureFormat.Bc6h)]
    [InlineData(PreviewTextureFormat.Unknown)]
    public void TryDecodeRawToBgra_RejectsCompressedAndUnknownFormats(PreviewTextureFormat format)
    {
        var bgra = new byte[4];
        Assert.False(DxgiTextureFormats.TryDecodeRawToBgra([1, 2, 3, 4], 1, 1, format, bgra));
    }

    [Fact]
    public void TryDecodeRawToBgra_RejectsTruncatedSource()
    {
        var bgra = new byte[4];
        Assert.False(DxgiTextureFormats.TryDecodeRawToBgra([1, 2, 3], 1, 1, PreviewTextureFormat.Rgba8, bgra));
    }
}
