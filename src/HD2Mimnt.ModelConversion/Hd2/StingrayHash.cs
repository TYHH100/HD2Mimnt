using System.Text;

namespace HD2Mimnt.ModelConversion.Hd2;

/// <summary>
/// AQSDK 使用的 Stingray 名称哈希：<c>murmur64_hash</c> 与取高 32 位的 <c>murmur32_hash</c>
/// （见 HD2SDK-AQ-Edition <c>stingray/hash.py</c>）。Unit 的 TransformInfo 名称哈希与
/// CompositeUnit 的 MeshID 都依赖它，必须逐位一致。
/// </summary>
public static class StingrayHash
{
    private const ulong M = 0xC6A4A7935BD1E995UL;
    private const int R = 47;

    public static ulong Murmur64(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        var h = seed ^ unchecked(M * (ulong)data.Length);
        var blockEnd = data.Length & ~7;
        for (var offset = 0; offset < blockEnd; offset += 8)
        {
            var k = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
            k = unchecked(k * M);
            k ^= k >> R;
            k = unchecked(k * M);
            h ^= k;
            h = unchecked(h * M);
        }

        // Tail bytes are folded in from the highest index down, matching the reference if-chain.
        var tailLength = data.Length & 7;
        if (tailLength >= 7) h ^= (ulong)data[blockEnd + 6] << 48;
        if (tailLength >= 6) h ^= (ulong)data[blockEnd + 5] << 40;
        if (tailLength >= 5) h ^= (ulong)data[blockEnd + 4] << 32;
        if (tailLength >= 4) h ^= (ulong)data[blockEnd + 3] << 24;
        if (tailLength >= 3) h ^= (ulong)data[blockEnd + 2] << 16;
        if (tailLength >= 2) h ^= (ulong)data[blockEnd + 1] << 8;
        if (tailLength >= 1)
        {
            h ^= data[blockEnd];
            h = unchecked(h * M);
        }

        h ^= h >> R;
        h = unchecked(h * M);
        h ^= h >> R;
        return h;
    }

    public static uint Murmur32(ReadOnlySpan<byte> data, ulong seed = 0) => (uint)(Murmur64(data, seed) >> 32);

    public static uint Murmur32(string text) => Murmur32(Encoding.UTF8.GetBytes(text));

    private const ulong FnvOffset = 0xCBF29CE484222325UL;
    private const ulong FnvPrime = 0x100000001B3UL;

    /// <summary>FNV-1a 64：用于生成稳定的资源 ID（名称哈希与资源身份是两套用途，不能混用）。</summary>
    public static ulong Fnv1A64(ReadOnlySpan<byte> data, ulong seed = FnvOffset)
    {
        var hash = seed;
        foreach (var value in data)
        {
            hash ^= value;
            hash = unchecked(hash * FnvPrime);
        }
        return hash;
    }

    public static ulong Fnv1A64(string text) => Fnv1A64(Encoding.UTF8.GetBytes(text));
}
