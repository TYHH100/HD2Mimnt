using System.Buffers.Binary;
using System.IO.Compression;
using HD2Mimnt.ModelConversion.Source.Gma;
using HD2Mimnt.ModelConversion.Source.Vtf;

namespace HD2Mimnt.ModelConversion.Gmod.Materials;

/// <summary>把 VTF 的 mip0/frame0 解码为 RGBA8 PNG；不支持的布局保留原始 VTF 字节并给出说明。</summary>
public static class GmodTextureDecoder
{
    public static (byte[]? Png, string? Issue) Decode(VtfFile texture)
    {
        var (rgba, width, height, issue) = DecodePixels(texture);
        return rgba is null ? (null, issue) : (PngWriter.Write(rgba, width, height), null);
    }

    /// <summary>解出 mip0 的 RGBA8 像素；写 Stingray 贴图资源时直接从这里编码，避免 PNG 往返。</summary>
    public static (byte[]? Rgba, int Width, int Height, string? Issue) DecodePixels(VtfFile texture)
    {
        if (!texture.HasImageLayout)
            return (null, 0, 0, texture.Warnings.FirstOrDefault() ?? "VTF image layout is unavailable; original bytes are retained.");
        var image = texture.Images.FirstOrDefault(item => item.Mip == 0 && item.Frame == 0);
        if (image is null)
            return (null, 0, 0, "VTF mip0/frame0 is unavailable; original bytes are retained.");
        var data = texture.GetImageData(image).Span;
        byte[]? rgba;
        try
        {
            rgba = DecodeFormat(texture.Format, data, image.Width, image.Height);
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return (null, 0, 0, $"VTF format {texture.Format} could not be decoded: {error.Message}. Original bytes are retained.");
        }
        if (rgba is null)
            return (null, 0, 0, $"VTF format {texture.Format} has no verified decoder; original bytes are retained verbatim.");
        return (rgba, image.Width, image.Height, null);
    }

    private static byte[]? DecodeFormat(int format, ReadOnlySpan<byte> data, int width, int height)
    {
        var pixels = width * height;
        switch (format)
        {
            case 0: return Rgba8888(data, pixels, false, false);
            case 1: return Rgba8888(data, pixels, true, false);
            case 2: case 9: return Rgb888(data, pixels, false, format == 9);
            case 3: case 10: return Rgb888(data, pixels, true, format == 10);
            case 11: return Rgba8888(data, pixels, false, true);
            case 12: case 16: return Rgba8888(data, pixels, true, true);
            case 4: case 17: return Rgb565(data, pixels, format == 17);
            case 5: return Luminance(data, pixels, 1);
            case 6: return Luminance(data, pixels, 2);
            case 8: return Alpha(data, pixels);
            case 18: case 21: return Bgra5551(data, pixels, format == 21);
            case 19: return Bgra4444(data, pixels);
            case 22: return Uv88(data, pixels);
            case 23: return Uvwq8888(data, pixels);
            case 27: return R32F(data, pixels);
            case 13: case 20: return Dxt(data, width, height, 8, format == 20);
            case 14: return Dxt(data, width, height, 14, false);
            case 15: return Dxt(data, width, height, 15, false);
            default: return null;
        }
    }

    private static byte[] Rgba8888(ReadOnlySpan<byte> data, int pixels, bool swapped, bool ignoreAlpha)
    {
        Require(data.Length >= pixels * 4);
        var result = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            var r = data[i * 4];
            var g = data[i * 4 + 1];
            var b = data[i * 4 + 2];
            var a = data[i * 4 + 3];
            result[i * 4] = swapped ? b : r;
            result[i * 4 + 1] = g;
            result[i * 4 + 2] = swapped ? r : b;
            result[i * 4 + 3] = ignoreAlpha ? (byte)255 : a;
        }
        return result;
    }

    private static byte[] Rgb888(ReadOnlySpan<byte> data, int pixels, bool swapped, bool blueScreen)
    {
        Require(data.Length >= pixels * 3);
        var result = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            var r = data[i * 3];
            var g = data[i * 3 + 1];
            var b = data[i * 3 + 2];
            result[i * 4] = swapped ? b : r;
            result[i * 4 + 1] = g;
            result[i * 4 + 2] = swapped ? r : b;
            result[i * 4 + 3] = blueScreen && r == 0 && g == 0 && b == 255 ? (byte)0 : (byte)255;
        }
        return result;
    }

    private static byte[] Rgb565(ReadOnlySpan<byte> data, int pixels, bool swapped)
    {
        Require(data.Length >= pixels * 2);
        var result = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2, 2));
            var r5 = swapped ? value & 0x1F : (value >> 11) & 0x1F;
            var g6 = (value >> 5) & 0x3F;
            var b5 = swapped ? (value >> 11) & 0x1F : value & 0x1F;
            result[i * 4] = (byte)(r5 * 255 / 31);
            result[i * 4 + 1] = (byte)(g6 * 255 / 63);
            result[i * 4 + 2] = (byte)(b5 * 255 / 31);
            result[i * 4 + 3] = 255;
        }
        return result;
    }

    private static byte[] Luminance(ReadOnlySpan<byte> data, int pixels, int stride)
    {
        Require(data.Length >= pixels * stride);
        var result = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            result[i * 4] = result[i * 4 + 1] = result[i * 4 + 2] = data[i * stride];
            result[i * 4 + 3] = stride == 2 ? data[i * stride + 1] : (byte)255;
        }
        return result;
    }

    private static byte[] Alpha(ReadOnlySpan<byte> data, int pixels)
    {
        Require(data.Length >= pixels);
        var result = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            result[i * 4] = result[i * 4 + 1] = result[i * 4 + 2] = 255;
            result[i * 4 + 3] = data[i];
        }
        return result;
    }

    private static byte[] Bgra5551(ReadOnlySpan<byte> data, int pixels, bool hasAlpha)
    {
        Require(data.Length >= pixels * 2);
        var result = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2, 2));
            var b = (value >> 11) & 0x1F;
            var g = (value >> 6) & 0x1F;
            var r = (value >> 1) & 0x1F;
            result[i * 4] = (byte)(r * 255 / 31);
            result[i * 4 + 1] = (byte)(g * 255 / 31);
            result[i * 4 + 2] = (byte)(b * 255 / 31);
            result[i * 4 + 3] = hasAlpha && (value & 1) == 0 ? (byte)0 : (byte)255;
        }
        return result;
    }

    private static byte[] Bgra4444(ReadOnlySpan<byte> data, int pixels)
    {
        Require(data.Length >= pixels * 2);
        var result = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2, 2));
            result[i * 4] = (byte)(((value >> 8) & 0xF) * 17);
            result[i * 4 + 1] = (byte)(((value >> 4) & 0xF) * 17);
            result[i * 4 + 2] = (byte)((value & 0xF) * 17);
            result[i * 4 + 3] = (byte)(((value >> 12) & 0xF) * 17);
        }
        return result;
    }

    private static byte[] Uv88(ReadOnlySpan<byte> data, int pixels)
    {
        Require(data.Length >= pixels * 2);
        var result = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            result[i * 4] = result[i * 4 + 1] = result[i * 4 + 2] = data[i * 2];
            result[i * 4 + 3] = data[i * 2 + 1];
        }
        return result;
    }

    private static byte[] Uvwq8888(ReadOnlySpan<byte> data, int pixels) => Rgba8888(data, pixels, false, false);

    private static byte[] R32F(ReadOnlySpan<byte> data, int pixels)
    {
        Require(data.Length >= pixels * 4);
        var result = new byte[pixels * 4];
        for (var i = 0; i < pixels; i++)
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i * 4, 4));
            var scaled = (byte)(float.IsFinite(value) ? Math.Clamp(value, 0, 1) * 255 : 0);
            result[i * 4] = result[i * 4 + 1] = result[i * 4 + 2] = scaled;
            result[i * 4 + 3] = 255;
        }
        return result;
    }

    private static byte[] Dxt(ReadOnlySpan<byte> data, int width, int height, int variant, bool oneBitAlpha)
    {
        var blocksX = (width + 3) / 4;
        var blocksY = (height + 3) / 4;
        var blockSize = variant is 14 or 15 ? 16 : 8;
        Require(data.Length >= blocksX * blocksY * blockSize);
        var result = new byte[width * height * 4];
        var alpha = new byte[16];
        for (var by = 0; by < blocksY; by++)
        for (var bx = 0; bx < blocksX; bx++)
        {
            var block = data.Slice((by * blocksX + bx) * blockSize, blockSize);
            if (variant is 14 or 15)
            {
                DecodeAlpha(block, alpha, variant == 15);
            }
            else
            {
                Array.Fill(alpha, (byte)255);
            }
            var colorOffset = variant is 14 or 15 ? 8 : 0;
            var c0 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(colorOffset, 2));
            var c1 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(colorOffset + 2, 2));
            var codes = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(colorOffset + 4, 4));
            var colors = DecodeColorBlock(c0, c1, oneBitAlpha || variant == 13);
            for (var pixel = 0; pixel < 16; pixel++)
            {
                var x = bx * 4 + pixel % 4;
                var y = by * 4 + pixel / 4;
                if (x >= width || y >= height) continue;
                var color = colors[(codes >> (pixel * 2)) & 3];
                var offset = (y * width + x) * 4;
                result[offset] = color.R;
                result[offset + 1] = color.G;
                result[offset + 2] = color.B;
                result[offset + 3] = oneBitAlpha && ((codes >> (pixel * 2)) & 3) == 3 && c0 <= c1 ? (byte)0 : alpha[pixel];
            }
        }
        return result;
    }

    private static void DecodeAlpha(ReadOnlySpan<byte> block, Span<byte> alpha, bool interpolated)
    {
        if (!interpolated)
        {
            for (var i = 0; i < 4; i++)
            {
                var row = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(i * 2, 2));
                for (var j = 0; j < 4; j++) alpha[i * 4 + j] = (byte)(((row >> (j * 4)) & 0xF) * 17);
            }
            return;
        }
        var a0 = block[0];
        var a1 = block[1];
        Span<byte> table = stackalloc byte[8];
        table[0] = a0;
        table[1] = a1;
        if (a0 > a1)
            for (var i = 1; i <= 6; i++) table[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        else
        {
            for (var i = 1; i <= 4; i++) table[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            table[6] = 0;
            table[7] = 255;
        }
        ulong bits = 0;
        for (var i = 0; i < 6; i++) bits |= (ulong)block[2 + i] << (8 * i);
        for (var pixel = 0; pixel < 16; pixel++) alpha[pixel] = table[(int)((bits >> (pixel * 3)) & 7)];
    }

    private static (byte R, byte G, byte B)[] DecodeColorBlock(ushort c0, ushort c1, bool oneBitAlpha)
    {
        static (byte R, byte G, byte B) Rgb(ushort value) => (
            (byte)(((value >> 11) & 0x1F) * 255 / 31),
            (byte)(((value >> 5) & 0x3F) * 255 / 63),
            (byte)((value & 0x1F) * 255 / 31));
        var a = Rgb(c0);
        var b = Rgb(c1);
        var result = new (byte R, byte G, byte B)[4];
        result[0] = a;
        result[1] = b;
        if (c0 > c1 || !oneBitAlpha)
        {
            result[2] = ((byte)((2 * a.R + b.R) / 3), (byte)((2 * a.G + b.G) / 3), (byte)((2 * a.B + b.B) / 3));
            result[3] = ((byte)((a.R + 2 * b.R) / 3), (byte)((a.G + 2 * b.G) / 3), (byte)((a.B + 2 * b.B) / 3));
        }
        else
        {
            result[2] = ((byte)((a.R + b.R) / 2), (byte)((a.G + b.G) / 2), (byte)((a.B + b.B) / 2));
            result[3] = (0, 0, 0);
        }
        return result;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new ArgumentException("VTF image payload is truncated for its declared dimensions.");
    }
}

/// <summary>零依赖的最小 PNG 编码器（RGBA8，非隔行，zlib 压缩）。</summary>
public static class PngWriter
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Write(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length < checked(width * height * 4))
            throw new ArgumentException("RGBA buffer does not match the requested dimensions.");
        using var stream = new MemoryStream();
        stream.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering
        ihdr[12] = 0; // no interlace
        Chunk(stream, "IHDR"u8, ihdr);

        var rowBytes = width * 4;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            // 逐行写入，避免为整张图（可能几十 MB）额外分配一份扫描线缓冲。
            var row = new byte[rowBytes + 1];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0; // filter type None
                rgba.AsSpan(y * rowBytes, rowBytes).CopyTo(row.AsSpan(1));
                zlib.Write(row);
            }
        }
        Chunk(stream, "IDAT"u8, compressed.ToArray());
        Chunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return stream.ToArray();
    }

    private static void Chunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        // IDAT 可能有好几 MB，CRC 覆盖「类型 + 数据」时必须流式计算，不能 stackalloc 拼成一整块。
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(type, data));
        stream.Write(type);
        stream.Write(data);
        stream.Write(crc);
    }
}
