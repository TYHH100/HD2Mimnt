using System.Runtime.InteropServices;

namespace HD2Mimnt.Core.ModelPreview;

/// <summary>
/// Texture format classes the preview decoder understands. BCn classes go through the
/// BCn decoder; every other class is converted to BGRA here from the raw payload bytes.
/// </summary>
public enum PreviewTextureFormat
{
    Unknown = 0,
    Bc1,
    Bc2,
    Bc3,
    Bc4,
    Bc5,
    Bc7,

    /// <summary>Recognized HDR block format the BCn decoder cannot decode; preview degrades gracefully.</summary>
    Bc6h,

    Rgba8,
    Rgba8Snorm,
    Bgra8,
    Bgra8X,
    Rgba16,
    Rgba16Snorm,
    Rgba16Half,
    Rgba32Float,
    Rgb10A2,
    R11G11B10Float,
    Rgb9E5,
    R8,
    R8Snorm,
    R8G8,
    R8G8Snorm,
    R16,
    R16Snorm,
    R16Half,
    R16G16,
    R16G16Snorm,
    R16G16Half,
    R32Float,
    R32G32Float,
    A8,
    B5g6r5,
    B5g5r5a1,
    B4g4r4a4,
}

/// <summary>
/// Full DXGI format table for the previewer. Values match the canonical DXGI_FORMAT enum;
/// the mapping decides whether a texture decodes through the BCn decoder or the raw pixel
/// paths below, and how many bytes each 4×4 block (compressed) or pixel (raw) occupies so
/// mip-chain math stays format-agnostic.
/// </summary>
public static class DxgiTextureFormats
{
    /// <summary>Maps a DXGI format id to its preview decoder class. Unknown stays Unknown.</summary>
    public static PreviewTextureFormat FromDxgiFormat(int dxgiFormat) => dxgiFormat switch
    {
        71 or 72 => PreviewTextureFormat.Bc1,             // BC1_UNORM / _SRGB
        74 or 75 => PreviewTextureFormat.Bc2,             // BC2_UNORM / _SRGB
        77 or 78 => PreviewTextureFormat.Bc3,             // BC3_UNORM / _SRGB
        80 or 81 => PreviewTextureFormat.Bc4,             // BC4_UNORM / _SNORM
        83 or 84 => PreviewTextureFormat.Bc5,             // BC5_UNORM / _SNORM
        95 or 96 => PreviewTextureFormat.Bc6h,            // BC6H_UF16 / _SF16
        98 or 99 => PreviewTextureFormat.Bc7,             // BC7_UNORM / _SRGB
        28 or 29 or 30 => PreviewTextureFormat.Rgba8,     // R8G8B8A8 UNORM/_SRGB/UINT
        31 => PreviewTextureFormat.Rgba8Snorm,
        87 or 91 => PreviewTextureFormat.Bgra8,           // B8G8R8A8 UNORM/_SRGB
        88 or 93 => PreviewTextureFormat.Bgra8X,          // B8G8R8X8 UNORM/_SRGB
        10 => PreviewTextureFormat.Rgba16Half,            // R16G16B16A16_FLOAT
        11 or 12 => PreviewTextureFormat.Rgba16,          // R16G16B16A16 UNORM/UINT
        13 => PreviewTextureFormat.Rgba16Snorm,
        2 => PreviewTextureFormat.Rgba32Float,            // R32G32B32A32_FLOAT
        24 => PreviewTextureFormat.Rgb10A2,               // R10G10B10A2_UNORM
        26 => PreviewTextureFormat.R11G11B10Float,
        67 => PreviewTextureFormat.Rgb9E5,                // R9G9B9E5_SHAREDEXP
        61 => PreviewTextureFormat.R8,                    // R8_UNORM
        63 => PreviewTextureFormat.R8Snorm,
        49 => PreviewTextureFormat.R8G8,                  // R8G8_UNORM
        51 => PreviewTextureFormat.R8G8Snorm,
        56 => PreviewTextureFormat.R16,                   // R16_UNORM
        58 => PreviewTextureFormat.R16Snorm,
        54 => PreviewTextureFormat.R16Half,               // R16_FLOAT
        35 => PreviewTextureFormat.R16G16,                // R16G16_UNORM
        37 => PreviewTextureFormat.R16G16Snorm,
        34 => PreviewTextureFormat.R16G16Half,            // R16G16_FLOAT
        41 => PreviewTextureFormat.R32Float,              // R32_FLOAT
        16 => PreviewTextureFormat.R32G32Float,           // R32G32_FLOAT
        65 => PreviewTextureFormat.A8,                    // A8_UNORM
        85 => PreviewTextureFormat.B5g6r5,
        86 => PreviewTextureFormat.B5g5r5a1,
        115 => PreviewTextureFormat.B4g4r4a4,
        _ => PreviewTextureFormat.Unknown
    };

    public static bool IsBlockCompressed(PreviewTextureFormat format) =>
        format is >= PreviewTextureFormat.Bc1 and <= PreviewTextureFormat.Bc7;

    /// <summary>Bytes per 4×4 block for compressed formats, bytes per pixel otherwise; 0 means undecodable.</summary>
    public static int GetBlockByteCount(PreviewTextureFormat format) => format switch
    {
        PreviewTextureFormat.Bc1 or PreviewTextureFormat.Bc4 => 8,
        PreviewTextureFormat.Bc2 or PreviewTextureFormat.Bc3 or PreviewTextureFormat.Bc5 or PreviewTextureFormat.Bc7 => 16,
        PreviewTextureFormat.Rgba8 or PreviewTextureFormat.Rgba8Snorm or PreviewTextureFormat.Bgra8
            or PreviewTextureFormat.Bgra8X or PreviewTextureFormat.Rgb10A2 or PreviewTextureFormat.R11G11B10Float
            or PreviewTextureFormat.Rgb9E5 or PreviewTextureFormat.R32Float => 4,
        PreviewTextureFormat.Rgba16 or PreviewTextureFormat.Rgba16Snorm or PreviewTextureFormat.Rgba16Half
            or PreviewTextureFormat.R32G32Float => 8,
        PreviewTextureFormat.Rgba32Float => 16,
        PreviewTextureFormat.R8 or PreviewTextureFormat.R8Snorm or PreviewTextureFormat.A8 => 1,
        PreviewTextureFormat.R8G8 or PreviewTextureFormat.R8G8Snorm or PreviewTextureFormat.R16
            or PreviewTextureFormat.R16Snorm or PreviewTextureFormat.R16Half or PreviewTextureFormat.B5g6r5
            or PreviewTextureFormat.B5g5r5a1 or PreviewTextureFormat.B4g4r4a4 => 2,
        PreviewTextureFormat.R16G16 or PreviewTextureFormat.R16G16Snorm or PreviewTextureFormat.R16G16Half => 4,
        _ => 0
    };

    /// <summary>
    /// Converts a raw (uncompressed) payload to 32-bit BGRA. Channel counts below four are
    /// shown faithfully (R as gray, RG without blue) exactly like the existing BC5 path;
    /// SNORM channels are re-centered to unorm so signed data stays visually centered.
    /// </summary>
    public static bool TryDecodeRawToBgra(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        PreviewTextureFormat format,
        Span<byte> destination)
    {
        if (width <= 0 || height <= 0 || IsBlockCompressed(format))
            return false;
        var pixelCount = width * height;
        var bytesPerPixel = GetBlockByteCount(format);
        if (bytesPerPixel <= 0 ||
            source.Length < (long)pixelCount * bytesPerPixel ||
            destination.Length < (long)pixelCount * 4)
            return false;

        switch (format)
        {
            case PreviewTextureFormat.Rgba8:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    destination[o] = source[o + 2];
                    destination[o + 1] = source[o + 1];
                    destination[o + 2] = source[o];
                    destination[o + 3] = source[o + 3];
                }
                return true;

            case PreviewTextureFormat.Rgba8Snorm:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    destination[o] = (byte)(source[o + 2] ^ 0x80);
                    destination[o + 1] = (byte)(source[o + 1] ^ 0x80);
                    destination[o + 2] = (byte)(source[o] ^ 0x80);
                    destination[o + 3] = (byte)(source[o + 3] ^ 0x80);
                }
                return true;

            case PreviewTextureFormat.Bgra8:
                source[..(pixelCount * 4)].CopyTo(destination);
                return true;

            case PreviewTextureFormat.Bgra8X:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    destination[o] = source[o];
                    destination[o + 1] = source[o + 1];
                    destination[o + 2] = source[o + 2];
                    destination[o + 3] = byte.MaxValue;
                }
                return true;

            case PreviewTextureFormat.Rgba16:
                DecodeRgba16(source, pixelCount, destination, snorm: false);
                return true;

            case PreviewTextureFormat.Rgba16Snorm:
                DecodeRgba16(source, pixelCount, destination, snorm: true);
                return true;

            case PreviewTextureFormat.Rgba16Half:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    var s = i * 8;
                    // 源为 RGBA 序（R 在前），输出 BGRA。
                    destination[o] = HalfToByte(source, s + 4);
                    destination[o + 1] = HalfToByte(source, s + 2);
                    destination[o + 2] = HalfToByte(source, s);
                    destination[o + 3] = HalfToByte(source, s + 6);
                }
                return true;

            case PreviewTextureFormat.Rgba32Float:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    var s = i * 16;
                    destination[o] = FloatToByte(MemoryMarshal.Read<float>(source[(s + 8)..]));
                    destination[o + 1] = FloatToByte(MemoryMarshal.Read<float>(source[(s + 4)..]));
                    destination[o + 2] = FloatToByte(MemoryMarshal.Read<float>(source[s..]));
                    destination[o + 3] = FloatToByte(MemoryMarshal.Read<float>(source[(s + 12)..]));
                }
                return true;

            case PreviewTextureFormat.Rgb10A2:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    var v = MemoryMarshal.Read<uint>(source[(i * 4)..]);
                    // D3D 位域：R 低 10 位、G 次之、B 再次、A 最高 2 位；输出为 BGRA。
                    destination[o] = Scale10To8((v >> 20) & 0x3FF);
                    destination[o + 1] = Scale10To8((v >> 10) & 0x3FF);
                    destination[o + 2] = Scale10To8(v & 0x3FF);
                    destination[o + 3] = (byte)(((v >> 30) & 0x3) * 85);
                }
                return true;

            case PreviewTextureFormat.R11G11B10Float:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    var v = MemoryMarshal.Read<uint>(source[(i * 4)..]);
                    // 位域：R 高 11 位、G 中 11 位、B 低 10 位；输出为 BGRA。
                    destination[o] = FloatToByte(DecodeFloat10(v & 0x3FF));
                    destination[o + 1] = FloatToByte(DecodeFloat11((v >> 10) & 0x7FF));
                    destination[o + 2] = FloatToByte(DecodeFloat11((v >> 21) & 0x7FF));
                    destination[o + 3] = byte.MaxValue;
                }
                return true;

            case PreviewTextureFormat.Rgb9E5:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    var v = MemoryMarshal.Read<uint>(source[(i * 4)..]);
                    // D3D 位域：E 最高 5 位，R 中间 9 位、G 次之、B 低 9 位；输出为 BGRA。
                    var scale = MathF.Pow(2, ((int)(v >> 27) & 0x1F) - 24);
                    destination[o] = FloatToByte((v & 0x1FF) * scale);
                    destination[o + 1] = FloatToByte((v >> 9 & 0x1FF) * scale);
                    destination[o + 2] = FloatToByte((v >> 18 & 0x1FF) * scale);
                    destination[o + 3] = byte.MaxValue;
                }
                return true;

            case PreviewTextureFormat.R8:
                FillGray(source, pixelCount, destination, snorm: false);
                return true;

            case PreviewTextureFormat.R8Snorm:
                FillGray(source, pixelCount, destination, snorm: true);
                return true;

            case PreviewTextureFormat.R8G8:
                FillTwoChannel8(source, pixelCount, destination, snorm: false);
                return true;

            case PreviewTextureFormat.R8G8Snorm:
                FillTwoChannel8(source, pixelCount, destination, snorm: true);
                return true;

            case PreviewTextureFormat.R16:
                FillGray16(source, pixelCount, destination, snorm: false);
                return true;

            case PreviewTextureFormat.R16Snorm:
                FillGray16(source, pixelCount, destination, snorm: true);
                return true;

            case PreviewTextureFormat.R16Half:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    var gray = HalfToByte(source, i * 2);
                    destination[o] = gray;
                    destination[o + 1] = gray;
                    destination[o + 2] = gray;
                    destination[o + 3] = byte.MaxValue;
                }
                return true;

            case PreviewTextureFormat.R16G16:
                FillTwoChannel16(source, pixelCount, destination, snorm: false);
                return true;

            case PreviewTextureFormat.R16G16Snorm:
                FillTwoChannel16(source, pixelCount, destination, snorm: true);
                return true;

            case PreviewTextureFormat.R16G16Half:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    var s = i * 4;
                    destination[o] = 0;
                    destination[o + 1] = HalfToByte(source, s + 2);
                    destination[o + 2] = HalfToByte(source, s);
                    destination[o + 3] = byte.MaxValue;
                }
                return true;

            case PreviewTextureFormat.R32Float:
                FillGrayFloat(source, pixelCount, destination);
                return true;

            case PreviewTextureFormat.R32G32Float:
                FillTwoChannelFloat(source, pixelCount, destination);
                return true;

            case PreviewTextureFormat.A8:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    destination[o] = 0;
                    destination[o + 1] = 0;
                    destination[o + 2] = 0;
                    destination[o + 3] = source[i];
                }
                return true;

            case PreviewTextureFormat.B5g6r5:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    var v = MemoryMarshal.Read<ushort>(source[(i * 2)..]);
                    destination[o] = Scale5To8(v & 0x1F);
                    destination[o + 1] = Scale6To8((v >> 5) & 0x3F);
                    destination[o + 2] = Scale5To8((v >> 11) & 0x1F);
                    destination[o + 3] = byte.MaxValue;
                }
                return true;

            case PreviewTextureFormat.B5g5r5a1:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    var v = MemoryMarshal.Read<ushort>(source[(i * 2)..]);
                    destination[o] = Scale5To8(v & 0x1F);
                    destination[o + 1] = Scale5To8((v >> 5) & 0x1F);
                    destination[o + 2] = Scale5To8((v >> 10) & 0x1F);
                    destination[o + 3] = (byte)(((v >> 15) & 1) * 255);
                }
                return true;

            case PreviewTextureFormat.B4g4r4a4:
                for (var i = 0; i < pixelCount; i++)
                {
                    var o = i * 4;
                    var v = MemoryMarshal.Read<ushort>(source[(i * 2)..]);
                    destination[o] = (byte)((v & 0xF) * 17);
                    destination[o + 1] = (byte)((v >> 4 & 0xF) * 17);
                    destination[o + 2] = (byte)((v >> 8 & 0xF) * 17);
                    destination[o + 3] = (byte)((v >> 12 & 0xF) * 17);
                }
                return true;

            default:
                return false;
        }
    }

    private static void DecodeRgba16(ReadOnlySpan<byte> source, int pixelCount, Span<byte> destination, bool snorm)
    {
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            var s = i * 8;
            destination[o] = Channel16(source, s + 4, snorm);
            destination[o + 1] = Channel16(source, s + 2, snorm);
            destination[o + 2] = Channel16(source, s, snorm);
            destination[o + 3] = Channel16(source, s + 6, snorm);
        }
    }

    private static byte Channel16(ReadOnlySpan<byte> source, int offset, bool snorm)
    {
        var v = MemoryMarshal.Read<ushort>(source[offset..]);
        if (snorm)
            return (byte)((v ^ 0x8000) >> 8);
        return (byte)(v >> 8);
    }

    private static void FillGray(ReadOnlySpan<byte> source, int pixelCount, Span<byte> destination, bool snorm)
    {
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            var gray = snorm ? (byte)(source[i] ^ 0x80) : source[i];
            destination[o] = gray;
            destination[o + 1] = gray;
            destination[o + 2] = gray;
            destination[o + 3] = byte.MaxValue;
        }
    }

    private static void FillTwoChannel8(ReadOnlySpan<byte> source, int pixelCount, Span<byte> destination, bool snorm)
    {
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            var s = i * 2;
            destination[o] = 0;
            destination[o + 1] = snorm ? (byte)(source[s + 1] ^ 0x80) : source[s + 1];
            destination[o + 2] = snorm ? (byte)(source[s] ^ 0x80) : source[s];
            destination[o + 3] = byte.MaxValue;
        }
    }

    private static void FillGray16(ReadOnlySpan<byte> source, int pixelCount, Span<byte> destination, bool snorm)
    {
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            var gray = Channel16(source, i * 2, snorm);
            destination[o] = gray;
            destination[o + 1] = gray;
            destination[o + 2] = gray;
            destination[o + 3] = byte.MaxValue;
        }
    }

    private static void FillTwoChannel16(ReadOnlySpan<byte> source, int pixelCount, Span<byte> destination, bool snorm)
    {
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            var s = i * 4;
            destination[o] = 0;
            destination[o + 1] = Channel16(source, s + 2, snorm);
            destination[o + 2] = Channel16(source, s, snorm);
            destination[o + 3] = byte.MaxValue;
        }
    }

    private static void FillGrayFloat(ReadOnlySpan<byte> source, int pixelCount, Span<byte> destination)
    {
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            var gray = FloatToByte(MemoryMarshal.Read<float>(source[(i * 4)..]));
            destination[o] = gray;
            destination[o + 1] = gray;
            destination[o + 2] = gray;
            destination[o + 3] = byte.MaxValue;
        }
    }

    private static void FillTwoChannelFloat(ReadOnlySpan<byte> source, int pixelCount, Span<byte> destination)
    {
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            var s = i * 8;
            destination[o] = 0;
            destination[o + 1] = FloatToByte(MemoryMarshal.Read<float>(source[(s + 4)..]));
            destination[o + 2] = FloatToByte(MemoryMarshal.Read<float>(source[s..]));
            destination[o + 3] = byte.MaxValue;
        }
    }

    private static byte HalfToByte(ReadOnlySpan<byte> source, int offset) =>
        FloatToByte((float)BitConverter.UInt16BitsToHalf(MemoryMarshal.Read<ushort>(source[offset..])));

    private static byte FloatToByte(float value)
    {
        if (!float.IsFinite(value))
            return 0;
        return (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);
    }

    private static byte Scale10To8(uint value) => (byte)((value * 255 + 511) / 1023);

    private static byte Scale5To8(int value) => (byte)((value * 255 + 15) / 31);

    private static byte Scale6To8(int value) => (byte)((value * 255 + 31) / 63);

    /// <summary>R11G11B10 style float: 5-bit exponent, 6-bit mantissa, no sign bit.</summary>
    private static float DecodeFloat11(uint bits)
    {
        var exponent = (int)((bits >> 6) & 0x1F);
        var mantissa = bits & 0x3F;
        if (exponent == 0)
            return mantissa / 64f * MathF.Pow(2, -14);
        if (exponent == 31)
            return float.PositiveInfinity;
        return (mantissa / 64f + 1f) * MathF.Pow(2, exponent - 15);
    }

    /// <summary>R11G11B10 style float with a 5-bit mantissa (the blue channel).</summary>
    private static float DecodeFloat10(uint bits)
    {
        var exponent = (int)((bits >> 5) & 0x1F);
        var mantissa = bits & 0x1F;
        if (exponent == 0)
            return mantissa / 32f * MathF.Pow(2, -14);
        if (exponent == 31)
            return float.PositiveInfinity;
        return (mantissa / 32f + 1f) * MathF.Pow(2, exponent - 15);
    }
}
