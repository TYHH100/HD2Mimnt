using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Tests.ModelPreview;

public class ModelPreviewPureBlackTests
{
    private static byte[] Pixels(int pixelCount, byte r, byte g, byte b, Func<int, byte> alpha)
    {
        var bgra = new byte[pixelCount * 4];
        for (var i = 0; i < pixelCount; i++)
        {
            bgra[i * 4] = b;
            bgra[i * 4 + 1] = g;
            bgra[i * 4 + 2] = r;
            bgra[i * 4 + 3] = alpha(i);
        }
        return bgra;
    }

    [Theory]
    [InlineData(255)]
    [InlineData(254)]
    [InlineData(231)]
    public void IsOpaqueBgraPureBlack_BlackRgbWithQuantizedOpaqueAlpha_IsPlaceholder(byte alpha)
    {
        // 实测夏安原皮模组的 BC7 纯黑占位图：RGB 全 0、alpha 经端点量化落在 231~254。
        var pixels = Pixels(256, 0, 0, 0, _ => alpha);
        Assert.True(ModelPreviewMaterialVariantSelector.IsOpaqueBgraPureBlack(pixels));
    }

    [Theory]
    [InlineData(229)]
    [InlineData(128)]
    [InlineData(0)]
    public void IsOpaqueBgraPureBlack_TransparentOrMixedAlpha_IsNotPlaceholder(byte alpha)
    {
        var pixels = Pixels(256, 0, 0, 0, _ => alpha);
        Assert.False(ModelPreviewMaterialVariantSelector.IsOpaqueBgraPureBlack(pixels));
    }

    [Fact]
    public void IsOpaqueBgraPureBlack_NonBlackRgb_IsNotPlaceholder()
    {
        var pixels = Pixels(256, 0, 0, 1, _ => byte.MaxValue);
        Assert.False(ModelPreviewMaterialVariantSelector.IsOpaqueBgraPureBlack(pixels));
    }

    [Fact]
    public void IsOpaqueBgraPureBlack_SingleTransparentPixel_IsNotPlaceholder()
    {
        // 掺入一个透明像素（真实镂空/遮罩的典型形态）即不判定为纯黑占位。
        var pixels = Pixels(256, 0, 0, 0, i => i == 128 ? (byte)0 : byte.MaxValue);
        Assert.False(ModelPreviewMaterialVariantSelector.IsOpaqueBgraPureBlack(pixels));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void IsOpaqueBgraPureBlack_RejectsInvalidLengths(int extraBytes) =>
        Assert.False(ModelPreviewMaterialVariantSelector.IsOpaqueBgraPureBlack(new byte[8 + extraBytes]));
}
