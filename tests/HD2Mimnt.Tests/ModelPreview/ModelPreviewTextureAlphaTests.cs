using HD2Mimnt.Infrastructure.ModelPreview;

namespace HD2Mimnt.Tests.ModelPreview;

public class ModelPreviewTextureAlphaTests
{
    private static byte[] Bgra(int pixelCount, Func<int, byte> alpha)
    {
        var bgra = new byte[pixelCount * 4];
        for (var i = 0; i < pixelCount; i++)
        {
            bgra[i * 4] = (byte)(i % 256);
            bgra[i * 4 + 1] = (byte)(i * 7 % 256);
            bgra[i * 4 + 2] = (byte)(i * 13 % 256);
            bgra[i * 4 + 3] = alpha(i);
        }
        return bgra;
    }

    private static byte[] ReduceAlpha(byte[] bgra)
    {
        var alpha = new byte[bgra.Length / 4];
        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = bgra[i * 4 + 3];
        return alpha;
    }

    [Fact]
    public void Normalize_PureRgbBc7ExportWithZeroAlpha_BecomesOpaque()
    {
        // VRChat 转 HD2 模组的"本体"贴图实测：94.5% 像素 alpha=0，其余是压缩噪声。
        // 游戏材质不采样 alpha 所以正常；预览若当真，两趟 alpha 剔除后网格只剩碎片。
        var bgra = Bgra(1024, i => i < 968 ? (byte)0 : (byte)8);
        PatchResourceInspectionService.NormalizeDecodedAlpha(bgra);
        Assert.All(ReduceAlpha(bgra), alpha => Assert.Equal(byte.MaxValue, alpha));
    }

    [Fact]
    public void Normalize_OpaquePixelsBelowFraction_BecomesOpaque()
    {
        // 不透明像素不足 1%（这里 20/4096 ≈ 0.5%）即视为 alpha 平面无效 → 回填。
        var bgra = Bgra(4096, i => i < 20 ? (byte)255 : (byte)4);
        PatchResourceInspectionService.NormalizeDecodedAlpha(bgra);
        Assert.All(ReduceAlpha(bgra), alpha => Assert.Equal(byte.MaxValue, alpha));
    }

    [Fact]
    public void Normalize_OpaquePixelsAtFraction_IsPreserved()
    {
        // 恰好达到 1% 门槛即认为 alpha 有效，保留原样（避免误伤小面积镂空）。
        var bgra = Bgra(1024, i => i < 10 ? (byte)255 : (byte)0);
        var expected = ReduceAlpha(bgra);
        PatchResourceInspectionService.NormalizeDecodedAlpha(bgra);
        Assert.Equal(expected, ReduceAlpha(bgra));
    }

    [Fact]
    public void Normalize_CutoutTextureWithRealOpaqueRegion_IsPreserved()
    {
        // 镂空材质（实测蕾丝贴图 22% 不透明像素）：alpha 有意义，原样保留。
        var bgra = Bgra(4096, i => i < 900 ? (byte)255 : (byte)0);
        var expected = ReduceAlpha(bgra);
        PatchResourceInspectionService.NormalizeDecodedAlpha(bgra);
        Assert.Equal(expected, ReduceAlpha(bgra));
    }

    [Fact]
    public void Normalize_UniformTranslucentAlpha_IsPreserved()
    {
        // 均匀半透明（玻璃类）：全部像素 alpha≥128，不算无效，保留。
        var bgra = Bgra(1024, _ => (byte)128);
        PatchResourceInspectionService.NormalizeDecodedAlpha(bgra);
        Assert.All(ReduceAlpha(bgra), alpha => Assert.Equal((byte)128, alpha));
    }
}
