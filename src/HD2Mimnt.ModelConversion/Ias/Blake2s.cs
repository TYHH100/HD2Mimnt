using System.Buffers.Binary;
using System.Numerics;

namespace HD2Mimnt.ModelConversion.Ias;

/// <summary>
/// BLAKE2s（RFC 7693）单次摘要。
///
/// HD2 的独立骨架运行时用 BLAKE2s 派生稳定身份：
/// 隔离清单 ID 是 <c>try_&quot;owned_&quot; + blake2s(project\0archive, 12)</c>，
/// 差分清单 ID 是 <c>blake2s(payload, 16, person=&quot;HD2DfV4&quot;)</c> 的前 16 位。
/// 只有逐位复刻这两处，产物才能与官方 SDK 导出的配套包对得上号，
/// 因此这里带一组固定向量（含官方样本的真实输出）把它们钉死。
/// </summary>
internal static class Blake2s
{
    private static readonly uint[] Iv =
    [
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
        0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19
    ];

    /// <summary>消息调度表：10 轮 × 16 个消息下标。</summary>
    private static readonly byte[] Sigma =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
        11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
        7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
        9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
        2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
        12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
        13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
        6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
        10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0
    ];

    /// <summary>无密钥摘要；<paramref name="person"/> 最多 8 字节，超出部分被忽略（与参考实现一致）。</summary>
    public static byte[] Hash(ReadOnlySpan<byte> data, int digestSize, ReadOnlySpan<byte> person = default)
    {
        if (digestSize is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(digestSize));

        Span<uint> state = stackalloc uint[8];
        Iv.CopyTo(state);
        // 参数块：digest_length | key_length << 8 | fanout=1 << 16 | depth=1 << 24
        state[0] ^= 0x01010000u ^ (uint)digestSize;
        if (!person.IsEmpty)
        {
            Span<byte> tail = stackalloc byte[8];
            tail.Clear();
            person[..Math.Min(8, person.Length)].CopyTo(tail);
            state[6] ^= BinaryPrimitives.ReadUInt32LittleEndian(tail);
            state[7] ^= BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
        }

        Span<byte> block = stackalloc byte[64];
        ulong counter = 0;
        var offset = 0;
        while (data.Length - offset > 64)
        {
            counter += 64;
            Compress(state, data.Slice(offset, 64), counter, false);
            offset += 64;
        }

        // 最后一块（可能不足 64 字节）补零，并置最后一块标志。
        var remaining = data.Length - offset;
        block.Clear();
        data[offset..].CopyTo(block);
        counter += (ulong)remaining;
        Compress(state, block, counter, true);

        var digest = new byte[32];
        for (var index = 0; index < 8; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(index * 4), state[index]);
        return digest[..digestSize];
    }

    private static void Compress(Span<uint> state, ReadOnlySpan<byte> block, ulong counter, bool last)
    {
        Span<uint> v = stackalloc uint[16];
        Span<uint> m = stackalloc uint[16];
        for (var index = 0; index < 16; index++)
            m[index] = BinaryPrimitives.ReadUInt32LittleEndian(block[(index * 4)..]);
        state.CopyTo(v);
        Iv.CopyTo(v[8..]);
        v[12] ^= (uint)counter;
        v[13] ^= (uint)(counter >> 32);
        if (last) v[14] = ~v[14];

        for (var round = 0; round < 10; round++)
        {
            var s = Sigma.AsSpan(round * 16, 16);
            Mix(v, m, 0, 4, 8, 12, s[0], s[1]);
            Mix(v, m, 1, 5, 9, 13, s[2], s[3]);
            Mix(v, m, 2, 6, 10, 14, s[4], s[5]);
            Mix(v, m, 3, 7, 11, 15, s[6], s[7]);
            Mix(v, m, 0, 5, 10, 15, s[8], s[9]);
            Mix(v, m, 1, 6, 11, 12, s[10], s[11]);
            Mix(v, m, 2, 7, 8, 13, s[12], s[13]);
            Mix(v, m, 3, 4, 9, 14, s[14], s[15]);
        }

        for (var index = 0; index < 8; index++)
            state[index] ^= v[index] ^ v[index + 8];
    }

    private static void Mix(Span<uint> v, ReadOnlySpan<uint> m, int a, int b, int c, int d, int x, int y)
    {
        v[a] = v[a] + v[b] + m[x];
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] += v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 12);
        v[a] = v[a] + v[b] + m[y];
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 8);
        v[c] += v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 7);
    }
}
