namespace HD2Mimnt.ModelConversion.Hd2;

/// <summary>
/// RGBA 通道重排：HD2 装甲材质把多个语义压进一张贴图（例如 <c>base_color_metal_map</c> 用
/// 颜色 RGB + A 通道的金属度），而 Source 的 VMT 用多张贴图分别表达。这里只做确定性的
/// 通道搬运，不猜测缺失通道的内容——缺失时用中性值填充。
/// </summary>
public static class Hd2TextureComposer
{
    /// <summary>BC 压缩要求宽高是 4 的倍数；不足时按边缘复制补齐（不改变 UV 覆盖范围最多 3 像素）。</summary>
    public const int BlockSize = 4;

    /// <summary>取单个通道作为灰度图（r=g=b=该通道，a=255）。</summary>
    public static Hd2TextureImage Gray(Hd2TextureImage source, int channel)
    {
        ArgumentNullException.ThrowIfNull(source);
        var index = Math.Clamp(channel, 0, 3);
        var pixels = new byte[source.Width * source.Height * 4];
        for (var i = 0; i < source.Width * source.Height; i++)
        {
            var value = source.Rgba[i * 4 + index];
            pixels[i * 4] = value;
            pixels[i * 4 + 1] = value;
            pixels[i * 4 + 2] = value;
            pixels[i * 4 + 3] = 255;
        }
        return new Hd2TextureImage(source.Width, source.Height, pixels);
    }

    /// <summary>保留 RGB，把 alpha 换成另一张图的指定通道（缺失时用 <paramref name="fallback"/>）。</summary>
    public static Hd2TextureImage WithAlpha(Hd2TextureImage color, Hd2TextureImage? alphaSource, int alphaChannel,
        byte fallback)
    {
        ArgumentNullException.ThrowIfNull(color);
        var index = Math.Clamp(alphaChannel, 0, 3);
        var pixels = (byte[])color.Rgba.Clone();
        var count = color.Width * color.Height;
        for (var i = 0; i < count; i++)
        {
            if (alphaSource is null || alphaSource.Width != color.Width || alphaSource.Height != color.Height)
            {
                pixels[i * 4 + 3] = fallback;
                continue;
            }
            pixels[i * 4 + 3] = alphaSource.Rgba[i * 4 + index];
        }
        return new Hd2TextureImage(color.Width, color.Height, pixels);
    }

    /// <summary>强制不透明：HD2 的不透明材质族不使用 alpha，保留源图 alpha 会在部分着色器下产生噪点。</summary>
    public static Hd2TextureImage Opaque(Hd2TextureImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var pixels = (byte[])source.Rgba.Clone();
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return new Hd2TextureImage(source.Width, source.Height, pixels);
    }

    /// <summary>把缺通道的兜底常量色按目标尺寸铺开（用 4×4 常量图直接贴到任意尺寸无法保持 mip 比例）。</summary>
    public static Hd2TextureImage Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        width = Math.Max(BlockSize, width);
        height = Math.Max(BlockSize, height);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = r; pixels[i * 4 + 1] = g; pixels[i * 4 + 2] = b; pixels[i * 4 + 3] = a;
        }
        return new Hd2TextureImage(width, height, pixels);
    }

    /// <summary>宽高向上补齐到 <see cref="BlockSize"/> 的倍数（边缘复制），BC 编码器才能全尺寸压缩。</summary>
    public static Hd2TextureImage PadToBlocks(Hd2TextureImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var width = (source.Width + BlockSize - 1) / BlockSize * BlockSize;
        var height = (source.Height + BlockSize - 1) / BlockSize * BlockSize;
        width = Math.Max(BlockSize, width);
        height = Math.Max(BlockSize, height);
        if (width == source.Width && height == source.Height) return source;

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(y, source.Height - 1);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(x, source.Width - 1);
                var from = (sourceY * source.Width + sourceX) * 4;
                var to = (y * width + x) * 4;
                pixels[to] = source.Rgba[from];
                pixels[to + 1] = source.Rgba[from + 1];
                pixels[to + 2] = source.Rgba[from + 2];
                pixels[to + 3] = source.Rgba[from + 3];
            }
        }
        return new Hd2TextureImage(width, height, pixels);
    }

    /// <summary>逐像素生成贴图；用于把不同来源的通道拼进同一张图（金属度/粗糙度/AO 组合）。</summary>
    public static Hd2TextureImage Map(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = sample(x, y);
                var offset = (y * width + x) * 4;
                pixels[offset] = r; pixels[offset + 1] = g; pixels[offset + 2] = b; pixels[offset + 3] = a;
            }
        }
        return new Hd2TextureImage(width, height, pixels);
    }

    /// <summary>取像素通道；尺寸不一致或缺失时返回兜底值（不放大采样，避免产生混乱的块状色差）。</summary>
    public static byte SampleChannel(Hd2TextureImage? image, int x, int y, int channel, byte fallback)
    {
        if (image is null || image.Width <= 0 || image.Height <= 0) return fallback;
        var index = Math.Clamp(channel, 0, 3);
        var sampleX = Math.Clamp(x, 0, image.Width - 1);
        var sampleY = Math.Clamp(y, 0, image.Height - 1);
        var offset = (sampleY * image.Width + sampleX) * 4 + index;
        return offset < image.Rgba.Length ? image.Rgba[offset] : fallback;
    }

    /// <summary>用遮罩图灰度替换 alpha（遮罩缺失时用常量），RGB 保持不变。</summary>
    public static Hd2TextureImage WithMaskedAlpha(Hd2TextureImage source, Hd2TextureImage? mask, int channel,
        byte fallback)
    {
        ArgumentNullException.ThrowIfNull(source);
        var pixels = (byte[])source.Rgba.Clone();
        var sameSize = mask is not null && mask.Width == source.Width && mask.Height == source.Height;
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var offset = (y * source.Width + x) * 4 + 3;
                pixels[offset] = sameSize ? SampleChannel(mask, x, y, channel, fallback) : fallback;
            }
        }
        return new Hd2TextureImage(source.Width, source.Height, pixels);
    }
}
