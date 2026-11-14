using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>
/// 角色库转换使用的哈希：Stingray 的资源名使用 MurmurHash64A，低 32 位参与场景节点匹配。
/// 参考实现为上游 <c>ias_binary.py</c> 的 <c>murmur64</c>/<c>murmur32</c>。
/// </summary>
public static class IasHash
{
    private const ulong M = 0xC6A4A7935BD1E995UL;

    public static ulong Murmur64(ReadOnlySpan<byte> data)
    {
        var h = unchecked((ulong)data.Length * M);
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            var k = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
            unchecked
            {
                k *= M;
                k ^= k >> 47;
                k *= M;
                h ^= k;
                h *= M;
            }
            offset += 8;
        }
        if (offset < data.Length)
        {
            ulong tail = 0;
            for (var i = 0; i < data.Length - offset; i++) tail |= (ulong)data[offset + i] << (8 * i);
            unchecked
            {
                h ^= tail;
                h *= M;
            }
        }
        unchecked
        {
            h ^= h >> 47;
            h *= M;
            h ^= h >> 47;
        }
        return h;
    }

    public static ulong Murmur64(string value) => Murmur64(System.Text.Encoding.UTF8.GetBytes(value));

    public static uint Murmur32(string value) => (uint)(Murmur64(value) >> 32);

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string value) => Sha256Hex(System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>分段哈希，避免为拼接 TOC/GPU/Stream 再分配一份同等大小的缓冲区。</summary>
    public static string Sha256Hex(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts) hash.AppendData(part);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static ulong Fnv1a64(string value)
    {
        var result = 14695981039346656037UL;
        foreach (var item in System.Text.Encoding.UTF8.GetBytes(value))
        {
            result ^= item;
            result *= 1099511628211UL;
        }
        return result;
    }
}
