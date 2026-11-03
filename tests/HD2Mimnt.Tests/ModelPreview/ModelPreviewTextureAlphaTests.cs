using HD2Mimnt.Core.ModelPreview;
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

    [Fact]
    public async Task PreviewTexture_DataTextureFlag_PreservesRoughnessAlpha()
    {
        // Nar 类数据贴图的 Alpha 是粗糙度：低粗糙度（这里整张 4/255）会被"假 alpha 回填
        // 不透明"抹成 1.0，预览因此永远算成完全粗糙。读取器标记后解码必须跳过该回填。
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"hd2mimnt-alpha-{Guid.NewGuid():N}"));
        try
        {
            var service = new PatchResourceInspectionService();
            var color = WriteRgba8Texture(root, alpha: 4);
            var data = WriteRgba8Texture(root, alpha: 4, textureId: 0x73, name: "data");
            data.AlphaIsMaterialParameter = true;

            var normalized = await service.PreviewTextureAsync(root, color);
            var raw = await service.PreviewTextureAsync(root, data);

            Assert.All(ReduceAlpha(normalized!.BgraPixels!), alpha => Assert.Equal(byte.MaxValue, alpha));
            Assert.All(ReduceAlpha(raw!.BgraPixels!), alpha => Assert.Equal(4, alpha));
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    /// <summary>写出最小可用的 patch + .gpu_resources：main 数据在 0xC0 处放 DDS 头，
    /// 像素负载走 gpu_resources，格式 RGBA8。</summary>
    private static TextureInspectionItem WriteRgba8Texture(DirectoryInfo root, byte alpha, ulong textureId = 0x72,
        string name = "color")
    {
        const int headerSize = 148;
        var main = new byte[0xC0 + headerSize];
        "DDS "u8.CopyTo(main.AsSpan(0xC0));
        BitConverter.GetBytes(32).CopyTo(main, 0xC0 + 12);   // height
        BitConverter.GetBytes(32).CopyTo(main, 0xC0 + 16);   // width
        BitConverter.GetBytes(1).CopyTo(main, 0xC0 + 28);    // mip count
        BitConverter.GetBytes(28).CopyTo(main, 0xC0 + 128);  // DXGI_FORMAT_R8G8B8A8_UNORM

        var payload = new byte[32 * 32 * 4];
        for (var i = 0; i < payload.Length / 4; i++)
        {
            payload[i * 4] = (byte)(i % 256);
            payload[i * 4 + 1] = (byte)(i * 7 % 256);
            payload[i * 4 + 2] = (byte)(i * 13 % 256);
            payload[i * 4 + 3] = alpha;
        }

        var patch = Path.Combine(root.FullName, $"probe-{name}.patch_0");
        File.WriteAllBytes(patch, main);
        File.WriteAllBytes(patch + ".gpu_resources", payload);
        return new TextureInspectionItem
        {
            PatchFile = Path.GetFileName(patch),
            PatchPath = Path.GetFileName(patch),
            PatchOrder = 0,
            TocEntryIndex = 0,
            TextureId = textureId,
            MainOffset = 0,
            MainSize = (uint)main.Length,
            GpuOffset = 0,
            GpuSize = (uint)payload.Length,
            StreamOffset = 0,
            StreamSize = 0,
            Width = 32,
            Height = 32,
            MipCount = 1,
            DxgiFormat = 28,
            PayloadKind = "DDS",
            PayloadSource = "gpu_resources"
        };
    }
}
