using System.Buffers.Binary;
using BCnEncoder.Encoder;
using BCnEncoder.Encoder.Options;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;

namespace HD2Mimnt.ModelConversion.Hd2;

/// <summary>未压缩的 mip0 像素（RGBA8，行优先）。</summary>
public sealed record Hd2TextureImage(int Width, int Height, byte[] Rgba);

/// <summary>写出的贴图资源：Toc 数据（280 字节头）与 GPU 像素载荷。</summary>
public sealed record Hd2TextureResult(byte[] Main, byte[] Gpu, int MipCount, string Format);

/// <summary>
/// 把 RGBA 像素编码成游戏原生 Stingray 贴图资源：主数据是 340 字节
/// （12 字节前缀 + 15×12 字节 mip 记录 + 148 字节带 DX10 扩展的 DDS 头，DDS 恰好落在 0xC0），
/// 像素载荷写入 <c>.gpu_resources</c>。
/// </summary>
public static class Hd2TextureWriter
{
    public const int MainHeaderSize = 340;
    public const int DdsHeaderSize = 148;
    public const int MipRecordStride = 12;

    /// <summary>HD2 的 BC1/BC3 对应的 DXGI 格式（读取器按这些值分发解码器）。</summary>
    public const uint Bc1Unorm = 71;
    public const uint Bc3Unorm = 77;

    public static Hd2TextureResult Build(Hd2TextureImage image, bool preserveAlpha,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0)
            throw new Hd2FormatException("A texture needs a positive size.");
        if (image.Rgba.Length < image.Width * image.Height * 4)
            throw new Hd2FormatException("Texture pixel data is shorter than width × height × 4.");

        var encoder = new BcEncoder(preserveAlpha ? CompressionFormat.Bc3 : CompressionFormat.Bc1)
        {
            OutputOptions =
            {
                GenerateMipMaps = true,
                Quality = CompressionQuality.Fast,
                FileFormat = OutputFileFormat.Dds,
                DdsPreferDxt10Header = true
            }
        };

        DdsFile dds;
        try
        {
            dds = encoder.EncodeToDds(image.Rgba, image.Width, image.Height, PixelFormat.Rgba32);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new Hd2FormatException($"Texture encoding failed: {error.Message}");
        }
        cancellationToken.ThrowIfCancellationRequested();

        using var buffer = new MemoryStream();
        dds.Write(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length <= DdsHeaderSize)
            throw new Hd2FormatException("Encoded DDS payload is empty.");
        if (bytes[0] != 'D' || bytes[1] != 'D' || bytes[2] != 'S' || bytes[3] != ' ')
            throw new Hd2FormatException("Encoded texture does not start with a DDS magic.");
        var mipCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28, 4));
        var dxgi = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(128, 4));
        var height = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        var width = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
        if (dxgi != Bc1Unorm && dxgi != Bc3Unorm)
            throw new Hd2FormatException($"Encoded texture uses DXGI format {dxgi}; only BC1/BC3 are verified.");
        if (width != image.Width || height != image.Height)
            throw new Hd2FormatException("Encoded texture size does not match the source pixels.");

        var main = new byte[MainHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(main, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(8), uint.MaxValue);
        bytes.AsSpan(0, DdsHeaderSize).CopyTo(main.AsSpan(MainHeaderSize - DdsHeaderSize));

        var gpu = bytes.AsSpan(DdsHeaderSize).ToArray();
        var issues = Hd2TextureInspector.Validate(main, gpu);
        if (issues.Count > 0)
            throw new Hd2FormatException("Generated texture failed self-validation: " + string.Join(" | ", issues));
        return new Hd2TextureResult(main, gpu, (int)mipCount, dxgi == Bc3Unorm ? "BC3" : "BC1");
    }

    /// <summary>常量色贴图，用作缺失通道的兜底（例如没有金属度/粗糙度贴图的材质）。</summary>
    public static Hd2TextureImage Solid(byte r, byte g, byte b, byte a = 255, int size = 4)
    {
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            pixels[i * 4] = r; pixels[i * 4 + 1] = g; pixels[i * 4 + 2] = b; pixels[i * 4 + 3] = a;
        }
        return new Hd2TextureImage(size, size, pixels);
    }

    /// <summary>平面法线贴图（128,128,255），缺失 <c>$bumpmap</c> 时使用。</summary>
    public static Hd2TextureImage FlatNormal() => Solid(128, 128, 255);
}

/// <summary>贴图结构的自检：DDS 头、尺寸与 mip 链字节数必须自洽。</summary>
public static class Hd2TextureInspector
{
    public static IReadOnlyList<string> Validate(ReadOnlySpan<byte> main, ReadOnlySpan<byte> gpu)
    {
        var issues = new List<string>();
        if (main.Length < Hd2TextureWriter.MainHeaderSize)
        {
            issues.Add("Texture header is shorter than 280 bytes.");
            return issues;
        }
        var dds = main[(Hd2TextureWriter.MainHeaderSize - Hd2TextureWriter.DdsHeaderSize)..];
        if (dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S' || dds[3] != ' ')
        {
            issues.Add("Texture header does not contain a DDS signature.");
            return issues;
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(dds[4..]) != 124) issues.Add("DDS header size field is not 124.");
        var height = BinaryPrimitives.ReadUInt32LittleEndian(dds[12..]);
        var width = BinaryPrimitives.ReadUInt32LittleEndian(dds[16..]);
        var mipCount = BinaryPrimitives.ReadUInt32LittleEndian(dds[28..]);
        var dxgi = BinaryPrimitives.ReadUInt32LittleEndian(dds[128..]);
        if (width == 0 || height == 0) issues.Add("Texture size is zero.");
        if (mipCount == 0 || mipCount > 15) issues.Add($"Texture mip count {mipCount} is invalid.");
        if (dxgi != Hd2TextureWriter.Bc1Unorm && dxgi != Hd2TextureWriter.Bc3Unorm)
            issues.Add($"Texture DXGI format {dxgi} is not supported by the preview decoder.");

        var blockBytes = dxgi == Hd2TextureWriter.Bc3Unorm ? 16 : 8;
        long expected = 0;
        var w = (int)width; var h = (int)height;
        for (var mip = 0; mip < mipCount; mip++)
        {
            expected += (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes;
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        if (expected != gpu.Length)
            issues.Add($"Texture payload is {gpu.Length} bytes but the mip chain needs {expected}.");
        return issues;
    }
}
