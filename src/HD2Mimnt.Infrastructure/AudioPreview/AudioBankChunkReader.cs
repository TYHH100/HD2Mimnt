using System.Buffers.Binary;

namespace HD2Mimnt.Infrastructure.AudioPreview;

/// <summary>
/// 音频巡检服务（模组 patch，经 FileStream）与原版基线读取（内存内）共用的 Wwise bank
/// chunk 解析。bank 是平铺的 tag+size chunk 序列；这里只关心 BKHD/DIDX/DATA —— DIDX 是
/// 12 字节媒体条目表（source id、相对 DATA 体起点偏移、大小）。
/// </summary>
internal static class AudioBankChunkReader
{
    internal const int BankDataTag = unchecked((int)0x41544144); // "DATA"
    private const int DidxTag = unchecked((int)0x58444944);     // "DIDX"

    internal const int MaxMediaEntries = 65536;

    internal readonly record struct DidxMedia(uint Id, uint Offset, uint Size);

    internal readonly record struct BankChunks(long DataOffset, uint DataSize, List<DidxMedia> Didx);

    /// <summary>从 patch 文件的 <paramref name="start"/> 起遍历 bank chunk；大 DATA/HIRC chunk
    /// 体只跳过不读取。</summary>
    public static BankChunks? ReadFromStream(FileStream patchFile, long start, uint length)
    {
        long? dataOffset = null;
        uint dataSize = 0;
        List<DidxMedia>? didx = null;
        var position = start;
        var end = start + length;

        Span<byte> chunkHeader = stackalloc byte[8];
        while (position + 8 <= end)
        {
            if (!TryReadAt(patchFile, position, chunkHeader))
                return null;
            var tag = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            var bodyOffset = position + 8;
            if (bodyOffset + size > end)
                return null;

            if (tag == BankDataTag)
            {
                dataOffset = bodyOffset;
                dataSize = size;
            }
            else if (tag == DidxTag && size >= 12 && size % 12 == 0)
            {
                didx = ParseDidx(patchFile, bodyOffset, size);
                if (didx is null)
                    return null;
            }

            position = bodyOffset + size;
        }

        return didx is null || dataOffset is null ? null : new BankChunks(dataOffset.Value, dataSize, didx);
    }

    /// <summary>从原版游戏包解出的 bank 字节的内存内变体。</summary>
    public static BankChunks? Parse(byte[] bankData)
    {
        long? dataOffset = null;
        uint dataSize = 0;
        List<DidxMedia>? didx = null;
        var position = 0;

        while (position + 8 <= bankData.Length)
        {
            var tag = BinaryPrimitives.ReadInt32LittleEndian(bankData.AsSpan(position));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bankData.AsSpan(position + 4));
            var bodyOffset = position + 8;
            if (bodyOffset + size > bankData.Length)
                return null;

            if (tag == BankDataTag)
            {
                dataOffset = bodyOffset;
                dataSize = size;
            }
            else if (tag == DidxTag && size >= 12 && size % 12 == 0)
            {
                var count = (int)(size / 12);
                if (count > MaxMediaEntries)
                    return null;
                didx = new List<DidxMedia>(count);
                for (var i = 0; i < count; i++)
                {
                    didx.Add(new DidxMedia(
                        BinaryPrimitives.ReadUInt32LittleEndian(bankData.AsSpan(bodyOffset + i * 12)),
                        BinaryPrimitives.ReadUInt32LittleEndian(bankData.AsSpan(bodyOffset + i * 12 + 4)),
                        BinaryPrimitives.ReadUInt32LittleEndian(bankData.AsSpan(bodyOffset + i * 12 + 8))));
                }
            }

            position = bodyOffset + (int)size;
        }

        return didx is null || dataOffset is null ? null : new BankChunks(dataOffset.Value, dataSize, didx);
    }

    private static List<DidxMedia>? ParseDidx(FileStream patchFile, long bodyOffset, uint size)
    {
        var count = (int)(size / 12);
        if (count > MaxMediaEntries)
            return null;
        var buffer = new byte[size];
        if (!TryReadAt(patchFile, bodyOffset, buffer))
            return null;
        var didx = new List<DidxMedia>(count);
        for (var i = 0; i < count; i++)
        {
            didx.Add(new DidxMedia(
                BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i * 12)),
                BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i * 12 + 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i * 12 + 8))));
        }
        return didx;
    }

    public static bool TryReadAt(FileStream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > stream.Length - buffer.Length)
            return false;
        stream.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count <= 0)
                return false;
            read += count;
        }
        return true;
    }
}
