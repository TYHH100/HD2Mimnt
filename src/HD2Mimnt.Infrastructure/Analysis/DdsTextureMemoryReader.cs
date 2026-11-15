using System.Buffers.Binary;
using System.Numerics;
using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Infrastructure.Analysis;

public readonly record struct DdsTextureMemoryInfo(long Bytes, int HeaderBytes);

/// <summary>只读取 Stingray 纹理的 DDS 头，计算紧凑 mip 载荷，不解码或读取纹理像素。</summary>
public static class DdsTextureMemoryReader
{
    public const int TextureHeaderOffset = 0xC0;

    internal static DdsTextureMemoryInfo? Read(FileStream stream, ulong mainOffset, uint mainSize)
    {
        if (mainSize < TextureHeaderOffset + 128) return null;
        Span<byte> header = stackalloc byte[148];
        var length = (int)Math.Min(148u, mainSize - TextureHeaderOffset);
        stream.Position = checked((long)mainOffset + TextureHeaderOffset);
        stream.ReadExactly(header[..length]);
        return TryCalculate(header[..length], out var result) ? result : null;
    }

    // DDS/DX10 layout and BC block sizes:
    // https://learn.microsoft.com/windows/win32/direct3ddds/dds-header
    // https://learn.microsoft.com/windows/win32/direct3d11/texture-block-compression-in-direct3d-11
    public static bool TryCalculate(ReadOnlySpan<byte> header, out DdsTextureMemoryInfo result)
    {
        result = default;
        if (header.Length < 128 || !header[..4].SequenceEqual("DDS "u8)
            || Read(header, 4) != 124 || Read(header, 76) != 32) return false;
        var width = Read(header, 16);
        var height = Read(header, 12);
        var depth = 1u;
        var layers = 1L;
        var mipCount = Math.Max(1u, Read(header, 28));
        var fourCc = Read(header, 84);
        var headerBytes = 128;
        var blockBytes = 0;
        var pixelBytes = 0;
        if (fourCc == 0x30315844) // DX10
        {
            if (header.Length < 148) return false;
            headerBytes = 148;
            var format = Read(header, 128);
            var dimension = Read(header, 132);
            layers = Read(header, 140);
            if (layers == 0 || dimension is < 2 or > 4) return false;
            if (dimension == 2 && height != 1) return false;
            if ((Read(header, 136) & 4) != 0)
            {
                if (dimension != 3) return false;
                layers *= 6;
            }
            if (dimension == 4)
            {
                if (layers != 1) return false;
                depth = Read(header, 24);
            }
            blockBytes = format switch
            {
                >= 70 and <= 72 or >= 79 and <= 81 => 8,
                >= 73 and <= 78 or >= 82 and <= 84 or >= 94 and <= 99 => 16,
                _ => 0
            };
            if (blockBytes == 0)
                pixelBytes = DxgiTextureFormats.GetBlockByteCount(DxgiTextureFormats.FromDxgiFormat((int)format));
        }
        else
        {
            blockBytes = fourCc switch
            {
                0x31545844 or 0x31495441 or 0x55344342 or 0x53344342 => 8, // DXT1/ATI1/BC4
                0x32545844 or 0x33545844 or 0x34545844 or 0x35545844
                    or 0x32495441 or 0x55354342 or 0x53354342 => 16,
                _ => 0
            };
            if (blockBytes == 0)
            {
                var bits = Read(header, 88);
                if (fourCc != 0 || (Read(header, 80) & 0x20042) == 0 || bits is not (8 or 16 or 24 or 32)) return false;
                pixelBytes = (int)bits / 8;
            }
            var caps2 = Read(header, 112);
            if ((caps2 & 0x200) != 0) layers = BitOperations.PopCount(caps2 & 0xFC00);
            if ((caps2 & 0x200000) != 0) depth = Read(header, 24);
        }
        if (width == 0 || height == 0 || depth == 0 || layers == 0 || blockBytes + pixelBytes == 0
            || mipCount > 1 + BitOperations.Log2(Math.Max(width, Math.Max(height, depth)))) return false;
        try
        {
            long bytes = 0;
            for (var mip = 0; mip < mipCount; mip++)
            {
                var level = blockBytes > 0
                    ? checked(((width + 3L) / 4) * ((height + 3L) / 4) * blockBytes * depth)
                    : checked((long)width * height * pixelBytes * depth);
                bytes = checked(bytes + checked(level * layers));
                width = Math.Max(1, width >> 1);
                height = Math.Max(1, height >> 1);
                depth = Math.Max(1, depth >> 1);
            }
            result = new(bytes, headerBytes);
            return true;
        }
        catch (OverflowException) { return false; }
    }

    private static uint Read(ReadOnlySpan<byte> bytes, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
}
