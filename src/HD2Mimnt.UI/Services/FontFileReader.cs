using System;
using System.IO;
using System.Text;

namespace HD2Mimnt.UI.Services;

/// <summary>从 TrueType/OpenType 字体文件读取字体族名；用于把用户选择的字体文件挂到 fonts: 集合。</summary>
public static class FontFileReader
{
    private const uint TtcTag = 0x74746366;       // 'ttcf'
    private const uint NameTableTag = 0x6E616D65; // 'name'
    private const ushort TypographicFamilyNameId = 16;
    private const ushort FamilyNameId = 1;

    /// <summary>返回字体文件中的字体族名；无法解析时返回 null（调用方回退默认字体）。</summary>
    public static string? TryReadFamilyName(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 12) return null;

            var fontOffset = ReadU32(reader) == TtcTag ? ReadFirstTtcFontOffset(reader) : 0;
            if (fontOffset < 0 || fontOffset + 12 > stream.Length) return null;

            stream.Position = fontOffset;
            ReadU32(reader);                      // sfntVersion
            var tableCount = ReadU16(reader);
            stream.Position += 6;                 // searchRange / entrySelector / rangeShift

            long nameOffset = -1;
            for (var i = 0; i < tableCount; i++)
            {
                if (stream.Position + 16 > stream.Length) return null;
                var tag = ReadU32(reader);
                ReadU32(reader);                  // checksum
                var offset = ReadU32(reader);
                ReadU32(reader);                  // length
                if (tag == NameTableTag) nameOffset = offset;
            }
            if (nameOffset <= 0 || nameOffset + 6 > stream.Length) return null;

            return ReadNameTable(stream, reader, nameOffset);
        }
        catch
        {
            return null;
        }
    }

    private static long ReadFirstTtcFontOffset(BinaryReader reader)
    {
        ReadU32(reader);                          // version
        var count = ReadU32(reader);
        return count == 0 ? -1 : ReadU32(reader);
    }

    private static string? ReadNameTable(Stream stream, BinaryReader reader, long nameOffset)
    {
        stream.Position = nameOffset;
        ReadU16(reader);                          // format
        var recordCount = ReadU16(reader);
        var stringOffset = ReadU16(reader);

        string? best = null;
        var bestRank = int.MaxValue;
        for (var i = 0; i < recordCount; i++)
        {
            if (stream.Position + 12 > stream.Length) break;
            var platformId = ReadU16(reader);
            var encodingId = ReadU16(reader);
            var languageId = ReadU16(reader);
            var nameId = ReadU16(reader);
            var length = ReadU16(reader);
            var offset = ReadU16(reader);

            if (nameId != FamilyNameId && nameId != TypographicFamilyNameId) continue;
            var rank = Rank(platformId, encodingId, languageId, nameId);
            if (rank >= bestRank) continue;

            var valueOffset = nameOffset + stringOffset + offset;
            if (valueOffset + length > stream.Length) continue;
            stream.Position = valueOffset;
            var bytes = reader.ReadBytes(length);
            var value = (platformId is 0 or 3 ? Encoding.BigEndianUnicode : Encoding.Latin1).GetString(bytes).Trim();
            if (value.Length == 0) continue;

            best = value;
            bestRank = rank;
        }
        return best;
    }

    /// <summary>优先排版族名（16）而非兼容族名（1），Windows 英文名优先于其他平台。</summary>
    private static int Rank(ushort platformId, ushort encodingId, ushort languageId, ushort nameId)
    {
        var rank = nameId == TypographicFamilyNameId ? 0 : 100;
        if (platformId == 3 && encodingId is 1 or 10) return rank + (languageId == 0x0409 ? 0 : 1);
        return platformId switch
        {
            0 => rank + 2,
            1 => rank + 4,
            _ => int.MaxValue
        };
    }

    // 字体格式是大端，BinaryReader 固定小端，必须逐字节拼装。
    private static ushort ReadU16(BinaryReader reader)
        => (ushort)((reader.ReadByte() << 8) | reader.ReadByte());

    private static uint ReadU32(BinaryReader reader)
        => ((uint)reader.ReadByte() << 24) | ((uint)reader.ReadByte() << 16)
           | ((uint)reader.ReadByte() << 8) | reader.ReadByte();
}
