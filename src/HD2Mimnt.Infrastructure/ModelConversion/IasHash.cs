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

    public static string ResourceFingerprint(IasResource resource)
    {
        Span<byte> lengths = stackalloc byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(lengths, (ulong)resource.Toc.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(lengths[8..], (ulong)resource.Gpu.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(lengths[16..], (ulong)resource.Stream.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(lengths);
        hash.AppendData(resource.Toc);
        Append(hash, resource.Gpu);
        Append(hash, resource.Stream);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>分段哈希，避免为拼接 TOC/GPU/Stream 再分配一份同等大小的缓冲区。</summary>
    public static string Sha256Hex(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts) hash.AppendData(part);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>TOC 与两段载荷一起取摘要；载荷可以是文件视图，按段读取不整块载入。</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> first, IasPayload second, IasPayload third)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(first);
        Append(hash, second);
        Append(hash, third);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, IasPayload payload)
    {
        if (payload.Length == 0) return;
        if (payload.InMemory)
        {
            hash.AppendData(payload.Span);
            return;
        }
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            for (long position = 0; position < payload.Length; position += buffer.Length)
            {
                var count = (int)Math.Min(buffer.Length, payload.Length - position);
                payload.CopyTo(position, buffer.AsSpan(0, count));
                hash.AppendData(buffer, 0, count);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
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
